# wfmcp-tool

Expose a registered [Workflow Core](https://github.com/danielgerlag/workflow-core) workflow as an MCP tool, self-hosted.

`tools/list` advertises each workflow you put in the catalog. `tools/call` starts a real Workflow Core instance, waits for it to finish, and returns the workflow data as the tool result. No Azure Functions, no Durable Task Scheduler.

This is a proof of concept. The MCP layer is a small hand-rolled JSON-RPC handler over stdio, not the official SDK.

```bash
bash demo.sh          # 7 checks, exit 0
bash demo.sh --break  # sabotaged catalog, exit 1
```

Needs the .NET 8 SDK and `jq`.

Read [AGENTS.md](AGENTS.md) for what is real, what is stubbed, the data shape, limits and next steps.

MIT licensed.
