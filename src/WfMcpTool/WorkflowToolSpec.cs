using System.Text.Json;

namespace WfMcpTool;

// One MCP tool backed by one registered Workflow Core definition.
// Version null means "latest registered version".
public sealed record WorkflowToolSpec(
    string Name,
    string Description,
    string WorkflowId,
    int? Version,
    JsonElement InputSchema)
{
    static readonly JsonSerializerOptions FileOptions = new(JsonSerializerDefaults.Web);

    // Catalog file shape: { "tools": [ { name, description, workflowId, version, inputSchema } ] }
    public static IReadOnlyList<WorkflowToolSpec> LoadFile(string path)
    {
        var file = JsonSerializer.Deserialize<CatalogFile>(File.ReadAllText(path), FileOptions)
            ?? throw new InvalidDataException($"{path}: empty catalog file");
        return file.Tools;
    }

    sealed record CatalogFile(List<WorkflowToolSpec> Tools);
}
