using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace WfMcpTool;

// Hand-rolled subset of MCP over stdio: newline-delimited JSON-RPC 2.0.
// Handles initialize, ping, tools/list, tools/call. Notifications are ignored.
// Not the official ModelContextProtocol SDK and not conformance tested.
public sealed class McpStdioServer(IWorkflowToolCatalog catalog, WorkflowToolDispatcher dispatcher)
{
    const string DefaultProtocolVersion = "2025-06-18";
    static readonly JsonSerializerOptions Compact = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public async Task RunAsync(TextReader input, TextWriter output, CancellationToken ct = default)
    {
        while (await input.ReadLineAsync(ct) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var response = await HandleAsync(line, ct);
            if (response is null) continue;
            await output.WriteLineAsync(response.ToJsonString(Compact));
            await output.FlushAsync(ct);
        }
    }

    public async Task<JsonObject?> HandleAsync(string line, CancellationToken ct)
    {
        JsonObject request;
        try
        {
            request = JsonNode.Parse(line)?.AsObject() ?? throw new JsonException("not an object");
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException)
        {
            return Error(null, -32700, "Parse error", null);
        }

        var id = request["id"]?.DeepClone();
        if (id is null) return null;

        var method = request["method"]?.GetValue<string>();
        var @params = request["params"] as JsonObject;

        return method switch
        {
            "initialize" => Result(id, Initialize(@params)),
            "ping" => Result(id, new JsonObject()),
            "tools/list" => Result(id, ListTools()),
            "tools/call" => await CallTool(id, @params, ct),
            _ => Error(id, -32601, $"Method not found: {method}", null),
        };
    }

    static JsonObject Initialize(JsonObject? @params) => new()
    {
        ["protocolVersion"] = @params?["protocolVersion"]?.GetValue<string>() ?? DefaultProtocolVersion,
        ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
        ["serverInfo"] = new JsonObject { ["name"] = "wfmcp-tool", ["version"] = "0.1.0" },
    };

    JsonObject ListTools() => new()
    {
        ["tools"] = new JsonArray(catalog.ListTools().Select(t => (JsonNode)new JsonObject
        {
            ["name"] = t.Name,
            ["description"] = t.Description,
            ["inputSchema"] = JsonNode.Parse(t.InputSchema.GetRawText()),
        }).ToArray()),
    };

    async Task<JsonObject> CallTool(JsonNode id, JsonObject? @params, CancellationToken ct)
    {
        var name = @params?["name"]?.GetValue<string>();
        if (string.IsNullOrEmpty(name))
            return Error(id, -32602, "Missing tool name", new JsonObject { ["error"] = "invalid_params" });

        JsonElement? arguments = @params!["arguments"] is { } a ? JsonSerializer.SerializeToElement(a) : null;
        var result = await dispatcher.CallAsync(name, arguments, ct);

        return result switch
        {
            ToolCallResult.ToolNotFound r => Error(id, -32602, $"Unknown tool: {r.ToolName}",
                new JsonObject { ["error"] = "tool_not_found", ["tool"] = r.ToolName }),
            ToolCallResult.InvalidArguments r => Error(id, -32602, $"Invalid arguments for {r.ToolName}: {r.Reason}",
                new JsonObject { ["error"] = "invalid_arguments", ["tool"] = r.ToolName }),
            ToolCallResult.Completed r => ToolResult(id, isError: false, new JsonObject
            {
                ["ok"] = true,
                ["instanceId"] = r.InstanceId,
                ["workflowId"] = r.WorkflowId,
                ["version"] = r.Version,
                ["status"] = "Complete",
                ["output"] = JsonNode.Parse(r.Output.GetRawText()),
            }),
            ToolCallResult.NotCompleted r => ToolResult(id, isError: true, new JsonObject
            {
                ["ok"] = false,
                ["instanceId"] = r.InstanceId,
                ["workflowId"] = r.WorkflowId,
                ["version"] = r.Version,
                ["status"] = r.Status.ToString(),
            }),
            _ => throw new InvalidOperationException($"unhandled result {result}"),
        };
    }

    static JsonObject ToolResult(JsonNode id, bool isError, JsonObject structured) => Result(id, new JsonObject
    {
        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = structured.ToJsonString(Compact) }),
        ["structuredContent"] = structured,
        ["isError"] = isError,
    });

    static JsonObject Result(JsonNode id, JsonObject result) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id,
        ["result"] = result,
    };

    static JsonObject Error(JsonNode? id, int code, string message, JsonObject? data)
    {
        var error = new JsonObject { ["code"] = code, ["message"] = message };
        if (data is not null) error["data"] = data;
        return new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = error };
    }
}
