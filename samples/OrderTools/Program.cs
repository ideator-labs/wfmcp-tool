using Microsoft.Extensions.DependencyInjection;
using OrderTools;
using WfMcpTool;
using WorkflowCore.Interface;

// Usage: OrderTools serve [--catalog <path>] [--timeout-ms <n>]
// stdout carries MCP JSON-RPC only. Diagnostics go to stderr.
if (args.Length == 0 || args[0] != "serve")
{
    Console.Error.WriteLine("usage: OrderTools serve [--catalog <path>] [--timeout-ms <n>]");
    return 2;
}

string? catalogPath = null;
var timeout = TimeSpan.FromSeconds(10);
for (var i = 1; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--catalog": catalogPath = args[++i]; break;
        case "--timeout-ms": timeout = TimeSpan.FromMilliseconds(int.Parse(args[++i])); break;
        default: Console.Error.WriteLine($"unknown arg {args[i]}"); return 2;
    }
}

var services = new ServiceCollection()
    .AddLogging()
    .AddWorkflow()
    .BuildServiceProvider();

var host = services.GetRequiredService<IWorkflowHost>();
host.RegisterWorkflow<PlaceOrderWorkflow, OrderData>();
host.RegisterWorkflow<CancelOrderWorkflow, CancelData>();
await host.StartAsync(CancellationToken.None);

var registry = services.GetRequiredService<IWorkflowRegistry>();
var persistence = services.GetRequiredService<IPersistenceProvider>();

var catalog = new WorkflowToolCatalog(registry);
foreach (var spec in catalogPath is null ? [] : WorkflowToolSpec.LoadFile(catalogPath))
    catalog.Register(spec);
Console.Error.WriteLine($"wfmcp: catalog={catalogPath ?? "(none)"} tools={catalog.ListTools().Count}");

var dispatcher = new WorkflowToolDispatcher(catalog, registry, host, persistence, timeout);
await new McpStdioServer(catalog, dispatcher).RunAsync(Console.In, Console.Out);

// Ask the Workflow Core persistence store how many instances exist, so the demo
// can prove the control path started nothing.
#pragma warning disable CS0612 // obsolete in 3.21 but still the store-level query we want
var instances = await persistence.GetWorkflowInstances(null, null, null, null, 0, 1000);
#pragma warning restore CS0612
Console.Error.WriteLine($"wfmcp: instances_total={instances.Count()}");

await host.StopAsync(CancellationToken.None);
return 0;
