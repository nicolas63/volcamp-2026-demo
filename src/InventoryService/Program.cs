using System.Diagnostics;
using System.Diagnostics.Metrics;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();

var app = builder.Build();
app.MapDefaultEndpoints();

var activitySource = new ActivitySource(builder.Environment.ApplicationName);
var meter = new Meter(builder.Environment.ApplicationName);
var lookupDuration = meter.CreateHistogram<double>(
    "demo.inventory.lookup.duration",
    unit: "ms",
    description: "Inventory lookup duration");
var injectedFaults = meter.CreateCounter<long>(
    "demo.inventory.faults",
    description: "Number of injected inventory faults");

app.MapGet("/", () => Results.Ok(new
{
    service = "inventory-service",
    scenario = "VOLCAMP-63 waits 2500 ms"
}));

app.MapGet("/inventory/{sku}", async (
    string sku,
    int? quantity,
    IConfiguration configuration,
    ILogger<Program> logger) =>
{
    var requestedQuantity = quantity.GetValueOrDefault(1);
    var faultEnabled = configuration.GetValue("DemoFaults:SlowInventoryEnabled", true);
    var isSlow = faultEnabled && string.Equals(sku, "VOLCAMP-63", StringComparison.OrdinalIgnoreCase);
    var delay = isSlow
        ? configuration.GetValue("DemoFaults:SlowInventoryDelayMs", 2500)
        : configuration.GetValue("DemoFaults:NormalInventoryDelayMs", 25);

    using var activity = activitySource.StartActivity("inventory.check", ActivityKind.Internal);
    activity?.SetTag("demo.inventory.sku", sku);
    activity?.SetTag("demo.inventory.fault.injected", isSlow);
    activity?.SetTag("demo.inventory.delay_ms", delay);

    if (isSlow)
    {
        injectedFaults.Add(1, new KeyValuePair<string, object?>("demo.fault.type", "latency"));
        logger.LogWarning(
            "Injecting deterministic inventory latency of {DelayMs} ms for demo SKU {Sku}",
            delay,
            sku);
    }
    else
    {
        logger.LogInformation("Checking inventory for SKU {Sku}", sku);
    }

    var startedAt = Stopwatch.GetTimestamp();
    await Task.Delay(delay);
    lookupDuration.Record(
        Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds,
        new KeyValuePair<string, object?>("demo.inventory.outcome", "available"));

    return Results.Ok(new InventoryResponse(sku, requestedQuantity, true, delay, isSlow));
});

app.Run();

public sealed record InventoryResponse(
    string Sku,
    int RequestedQuantity,
    bool Available,
    int ProcessingDelayMs,
    bool FaultInjected);

public partial class Program;
