using System.Diagnostics;
using System.Text.Json;
using WorkflowCore.Interface;
using WorkflowCore.Models;

namespace WfMcpTool;

public abstract record ToolCallResult
{
    public sealed record Completed(string InstanceId, string WorkflowId, int Version, JsonElement Output) : ToolCallResult;
    public sealed record NotCompleted(string InstanceId, string WorkflowId, int Version, WorkflowStatus Status) : ToolCallResult;
    public sealed record ToolNotFound(string ToolName) : ToolCallResult;
    public sealed record InvalidArguments(string ToolName, string Reason) : ToolCallResult;
}

// tools/call -> start a Workflow Core instance -> wait for a terminal status -> return its data.
public sealed class WorkflowToolDispatcher(
    IWorkflowToolCatalog catalog,
    IWorkflowRegistry registry,
    IWorkflowHost host,
    IPersistenceProvider persistence,
    TimeSpan timeout)
{
    static readonly JsonSerializerOptions DataOptions = new(JsonSerializerDefaults.Web);
    static readonly TimeSpan PollEvery = TimeSpan.FromMilliseconds(50);

    public async Task<ToolCallResult> CallAsync(string toolName, JsonElement? arguments, CancellationToken ct = default)
    {
        var spec = catalog.Resolve(toolName);
        if (spec is null)
            return new ToolCallResult.ToolNotFound(toolName);

        var definition = registry.GetDefinition(spec.WorkflowId, spec.Version)!;
        object? data;
        try
        {
            var json = arguments?.GetRawText() ?? "{}";
            data = JsonSerializer.Deserialize(json, definition.DataType, DataOptions);
        }
        catch (JsonException e)
        {
            return new ToolCallResult.InvalidArguments(toolName, e.Message);
        }

        var instanceId = await host.StartWorkflow(definition.Id, definition.Version, data);

        var clock = Stopwatch.StartNew();
        while (true)
        {
            var instance = await persistence.GetWorkflowInstance(instanceId, ct);
            if (instance.Status == WorkflowStatus.Complete)
                return new ToolCallResult.Completed(
                    instanceId,
                    instance.WorkflowDefinitionId,
                    instance.Version,
                    JsonSerializer.SerializeToElement(instance.Data, definition.DataType, DataOptions));
            if (instance.Status == WorkflowStatus.Terminated || clock.Elapsed > timeout)
                return new ToolCallResult.NotCompleted(instanceId, instance.WorkflowDefinitionId, instance.Version, instance.Status);
            await Task.Delay(PollEvery, ct);
        }
    }
}
