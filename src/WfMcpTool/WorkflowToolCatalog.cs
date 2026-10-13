using WorkflowCore.Interface;

namespace WfMcpTool;

public interface IWorkflowToolCatalog
{
    void Register(WorkflowToolSpec spec);
    IReadOnlyList<WorkflowToolSpec> ListTools();
    WorkflowToolSpec? Resolve(string toolName);
}

public sealed class WorkflowToolCatalog(IWorkflowRegistry registry) : IWorkflowToolCatalog
{
    readonly Dictionary<string, WorkflowToolSpec> _tools = new(StringComparer.Ordinal);

    // Fails fast if the spec points at a workflow the host never registered,
    // so a stale mapping breaks at startup instead of on the first call.
    public void Register(WorkflowToolSpec spec)
    {
        if (registry.GetDefinition(spec.WorkflowId, spec.Version) is null)
            throw new InvalidOperationException(
                $"tool '{spec.Name}' maps to workflow '{spec.WorkflowId}' v{spec.Version?.ToString() ?? "latest"}, which is not registered with the Workflow Core host");
        if (!_tools.TryAdd(spec.Name, spec))
            throw new InvalidOperationException($"tool '{spec.Name}' is already registered");
    }

    public IReadOnlyList<WorkflowToolSpec> ListTools() => _tools.Values.ToList();

    public WorkflowToolSpec? Resolve(string toolName) => _tools.GetValueOrDefault(toolName);
}
