using WorkflowCore.Interface;
using WorkflowCore.Models;

namespace OrderTools;

public sealed class OrderData
{
    public string OrderId { get; set; } = "";
    public int Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal Total { get; set; }
    public string Status { get; set; } = "new";
    public string Confirmation { get; set; } = "";
    public List<string> Trail { get; set; } = [];
}

public sealed class CancelData
{
    public string OrderId { get; set; } = "";
    public string Status { get; set; } = "new";
    public List<string> Trail { get; set; } = [];
}

// Four steps, one of them a Workflow Core Delay, so the dispatcher has to wait
// for a step the engine schedules later rather than seeing a synchronous finish.
public sealed class PlaceOrderWorkflow : IWorkflow<OrderData>
{
    public string Id => "PlaceOrder";
    public int Version => 1;

    public void Build(IWorkflowBuilder<OrderData> builder) => builder
        .StartWith(ctx => Step(ctx, d => d.Status = "validated", "validate"))
        .Then(ctx => Step(ctx, d => d.Total = d.Quantity * d.UnitPrice, "price"))
        .Delay(d => TimeSpan.FromMilliseconds(300))
        .Then(ctx => Step(ctx, d => d.Trail.Add("waited"), null))
        .Then(ctx => Step(ctx, d => { d.Confirmation = $"CONF-{d.OrderId}"; d.Status = "placed"; }, "confirm"));

    static ExecutionResult Step(IStepExecutionContext ctx, Action<OrderData> body, string? trail)
    {
        var data = (OrderData)ctx.Workflow.Data;
        body(data);
        if (trail is not null) data.Trail.Add(trail);
        return ExecutionResult.Next();
    }
}

// A second registered workflow. Break mode points the place_order tool here.
public sealed class CancelOrderWorkflow : IWorkflow<CancelData>
{
    public string Id => "CancelOrder";
    public int Version => 1;

    public void Build(IWorkflowBuilder<CancelData> builder) => builder
        .StartWith(ctx =>
        {
            var data = (CancelData)ctx.Workflow.Data;
            data.Status = "cancelled";
            data.Trail.Add("cancel");
            return ExecutionResult.Next();
        });
}
