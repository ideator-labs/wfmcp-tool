# AGENTS.md

Handoff for a coding agent with no prior context on this repo.

## What this proves

One claim: a self-hosted MCP tool catalog can advertise a registered Workflow Core workflow on `tools/list` and start that real workflow on `tools/call`, returning the completed workflow data as the tool result. It runs in one .NET process with the in-memory Workflow Core store. No Azure Functions, no Durable Task Scheduler.

The comparison point is Microsoft's pattern of backing long-running MCP tools with Azure Functions and Durable Functions (see Sources). This repo shows the same shape works on a plain Workflow Core host.

## Status

| Piece | Real or stubbed |
|---|---|
| Workflow Core host, registry, step execution, `Delay` step, in-memory persistence | Real. NuGet `WorkflowCore` 3.21.0. |
| Instance id, workflow id, version, status, output in the tool result | Real. Read back from the Workflow Core persistence store after the run. |
| Instance count used by the control check | Real. `IPersistenceProvider.GetWorkflowInstances` on the same store. |
| MCP protocol layer | Hand-rolled subset. Newline-delimited JSON-RPC 2.0 over stdio. Handles `initialize`, `ping`, `tools/list`, `tools/call`. Ignores notifications. Not the official `ModelContextProtocol` SDK. Not tested against a real MCP client. |
| Sample workflows `PlaceOrder` and `CancelOrder` | Real Workflow Core workflows with toy bodies. No external IO. |

`bash demo.sh` passes 7 of 7 checks on a fresh clone. `bash demo.sh --break` fails checks 2 and 3 and exits 1.

## Data shape

```
WorkflowToolSpec          one MCP tool -> one Workflow Core definition
  Name                    MCP tool name, e.g. "place_order"
  Description             shown on tools/list
  WorkflowId              Workflow Core definition id, e.g. "PlaceOrder"
  Version                 int, or null for latest registered
  InputSchema             JSON Schema, passed through to tools/list as-is

IWorkflowToolCatalog
  Register(spec)          throws if WorkflowId/Version is not registered on the host, or Name is taken
  ListTools()             specs in registration order
  Resolve(name)           spec or null

WorkflowToolDispatcher.CallAsync(name, argumentsJson) -> ToolCallResult
  ToolNotFound(name)                                      Resolve returned null. Nothing is started.
  InvalidArguments(name, reason)                          arguments did not deserialize into the workflow data type
  Completed(instanceId, workflowId, version, output)      instance reached WorkflowStatus.Complete
  NotCompleted(instanceId, workflowId, version, status)   Terminated, or still running at the timeout
```

Wire mapping in `McpStdioServer`:

- `ToolNotFound` -> JSON-RPC error `-32602`, `data.error = "tool_not_found"`.
- `InvalidArguments` -> JSON-RPC error `-32602`, `data.error = "invalid_arguments"`.
- `Completed` -> `result.structuredContent = { ok: true, instanceId, workflowId, version, status: "Complete", output }`, `isError: false`, plus the same JSON as a text content block.
- `NotCompleted` -> same envelope with `ok: false`, the real status, and `isError: true`.

Catalog file (`catalog.json`):

```json
{ "tools": [ { "name": "place_order", "description": "...", "workflowId": "PlaceOrder", "version": 1, "inputSchema": { ... } } ] }
```

## Repo map

```
demo.sh                                  the proof. Builds, runs two servers, prints CHECK lines
catalog.json                             maps place_order -> PlaceOrder v1
src/WfMcpTool/
  WorkflowToolSpec.cs                    spec record and catalog file loader
  WorkflowToolCatalog.cs                 IWorkflowToolCatalog and the in-memory implementation
  WorkflowToolDispatcher.cs              ToolCallResult and CallAsync (start, poll, return)
  McpStdioServer.cs                      JSON-RPC over stdio, maps ToolCallResult to MCP responses
samples/OrderTools/
  Workflows.cs                           PlaceOrder (validate, price, 300 ms Delay, confirm) and CancelOrder
  Program.cs                             `serve [--catalog path] [--timeout-ms n]`, wires the host, prints instances_total to stderr on exit
```

## Setup, run, verify (fresh clone)

Prereqs: .NET 8 SDK (`dotnet --list-sdks` shows 8.x) and `jq`. If the SDK lives in `~/.dotnet`, `demo.sh` puts it on PATH. The first build needs NuGet access.

```bash
git clone https://github.com/ideator-labs/wfmcp-tool.git
cd wfmcp-tool
bash demo.sh
```

Expected: seven `CHECK PASS` lines, then `RESULT: PASS`, exit 0.

```
CHECK PASS  1 registered: tools/list advertises place_order
CHECK PASS  2 registered: tools/call started a real instance (non-empty instanceId, ran PlaceOrder v1)
CHECK PASS  3 registered: instance reached Complete with expected output
CHECK PASS  4 registered: Workflow Core store holds exactly 1 instance
CHECK PASS  5 empty catalog: tools/list is empty
CHECK PASS  6 empty catalog: tools/call returns tool_not_found
CHECK PASS  7 empty catalog: Workflow Core store holds 0 instances
```

Break it on purpose:

```bash
bash demo.sh --break
```

This writes `.demo/catalog.broken.json` with `place_order` pointed at `CancelOrder`. The tool still lists. The call still completes. But the result reports `workflowId: "CancelOrder"` and the wrong output, so checks 2 and 3 fail and the script exits 1. `catalog.json` is never touched, so running `bash demo.sh` again passes.

Raw server output lands in `.demo/` (`registered.jsonl`, `empty.jsonl`, and their `.err` files).

To talk to the server by hand:

```bash
dotnet build samples/OrderTools -c Release
echo '{"jsonrpc":"2.0","id":1,"method":"tools/list"}' | dotnet samples/OrderTools/bin/Release/net8.0/OrderTools.dll serve --catalog catalog.json
```

## Design decisions

- The catalog is a lookup table, not a framework. One spec maps one tool name to one definition id and version. Everything else comes from the Workflow Core registry.
- `Register` checks the registry. A spec that names an unregistered workflow fails at startup, not on the first agent call. A spec that names the wrong but registered workflow still passes. Break mode shows exactly that gap.
- The tool result reports `workflowId` and `version` read from the persisted instance, not copied from the spec. That is what lets a test catch a bad mapping.
- The data type for argument deserialization comes from `WorkflowDefinition.DataType`, so the catalog file needs no .NET type names.
- `tools/call` blocks until a terminal status or the timeout (default 10 s). It polls the persistence store every 50 ms. Simple, and it works with any Workflow Core persistence provider.
- `PlaceOrder` includes a 300 ms Workflow Core `Delay`. The `waited` entry in the trail can only appear after the engine resumed the instance, so the dispatcher really waited for the engine.
- stdout carries only JSON-RPC. All diagnostics go to stderr, as the MCP stdio transport requires.
- Two demo servers instead of one with a mutable catalog. The control run is a separate process with nothing registered, so "no instance created" is a count on a clean store.

## Limits

- In-memory persistence only. Instances die with the process. Durability across restarts is not shown here (the earlier `WorkflowCoreMcp` POC covers that with SQLite).
- Long workflows block the call until the timeout. On timeout the client gets `ok: false` and the instance id, but there is no tool to poll it later and no MCP Tasks support.
- Arguments are not validated against `inputSchema`. Any JSON that deserializes into the data type is accepted. Unknown fields are ignored.
- Requests are handled one at a time. No concurrency, cancellation, or progress notifications.
- `GetWorkflowInstances` is marked obsolete in Workflow Core 3.21. It is used only for the demo instance count.
- The protocol subset has not been run against Claude Desktop, VS Code, or the MCP Inspector.

## Next steps (in priority order)

1. **Real MCP client.** Run the server under the MCP Inspector or the official C# SDK client. Done when a scripted client run lists `place_order` and gets `structuredContent.ok == true` without any change to `McpStdioServer` behavior, or the diff needed is recorded.
2. **Start-and-poll for long workflows.** Add a `get_workflow_result` tool that takes `instanceId`. Return `{ ok: false, status: "Runnable", instanceId }` when the wait budget runs out. Done when a demo check calls a workflow with a 3 s Delay using a 500 ms budget, gets the instance id, then polls it to `Complete`.
3. **Durable store.** Swap in `WorkflowCore.Persistence.Sqlite`. Done when a demo check starts a call, kills the server mid-Delay, restarts it, and `get_workflow_result` returns `Complete` for the same instance id.
4. **Schema validation.** Validate arguments against `inputSchema` before starting. Done when a call with a missing required field returns `invalid_arguments` and the store count stays unchanged.
5. **Package it.** Ship `src/WfMcpTool` as a NuGet with an `IServiceCollection` extension such as `AddWorkflowMcpTools(catalogPath)`. Done when a new console app with only that call plus its own workflow passes the same 7 checks.

## Working rules

- `bash demo.sh` is the source of truth. Keep it passing and keep `--break` failing.
- Every new claim gets a CHECK line that reads real server output, and a way to make it fail.
- Keep stdout clean. Anything that is not JSON-RPC goes to stderr.
- Keep the `WorkflowCore` package at 3.21.x unless a step needs a newer API.
- Commit as `danielgerlag <daniel@gerlag.ca>`.
- Plain prose. No em-dashes.

## Sources

- Workflow Core: https://github.com/danielgerlag/workflow-core
- WorkflowCore 3.21.0 on NuGet: https://www.nuget.org/packages/WorkflowCore/3.21.0
- MCP spec, tools: https://modelcontextprotocol.io/specification/2025-06-18/server/tools
- MCP spec, transports (stdio): https://modelcontextprotocol.io/specification/2025-06-18/basic/transports
- Azure sample, long-running MCP tools on Azure Functions with Durable Functions: https://github.com/Azure-Samples/mcp-functions-long-running-tools
- Azure SDK blog, how to build long-running MCP tools on Azure Functions: https://devblogs.microsoft.com/azure-sdk/long-running-mcp-tools-azure-functions/
