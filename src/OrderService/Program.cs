using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Json;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();

builder.Services.AddHttpClient<InventoryClient>((services, client) =>
{
    var configuration = services.GetRequiredService<IConfiguration>();
    client.BaseAddress = new Uri(configuration["Inventory:BaseUrl"] ?? "http://localhost:5102");
});
builder.Services.AddSingleton<OrderTelemetry>();

var app = builder.Build();
app.MapDefaultEndpoints();

app.MapGet("/", () => Results.Ok(new
{
    service = "order-service",
    healthySku = "DEMO-FAST",
    slowSku = "VOLCAMP-63"
}));

app.MapPost("/orders", async (
    OrderRequest request,
    InventoryClient inventory,
    OrderTelemetry telemetry,
    IConfiguration configuration,
    ILogger<Program> logger,
    HttpContext httpContext) =>
{
    if (string.IsNullOrWhiteSpace(request.Sku) || request.Quantity <= 0)
    {
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["order"] = ["A non-empty SKU and a positive quantity are required."]
        });
    }

    using var activity = telemetry.ActivitySource.StartActivity("order.process", ActivityKind.Internal);
    activity?.SetTag("demo.order.sku", request.Sku);
    activity?.SetTag("demo.order.quantity", request.Quantity);

    var startedAt = Stopwatch.GetTimestamp();

    try
    {
        var inventoryResult = await inventory.CheckAsync(
            request.Sku,
            request.Quantity,
            configuration,
            telemetry,
            logger,
            httpContext.RequestAborted);

        var orderId = $"ORD-{Guid.NewGuid():N}"[..12].ToUpperInvariant();
        var elapsedMs = Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;
        telemetry.Orders.Add(
            1,
            new KeyValuePair<string, object?>("demo.order.outcome", "accepted"));
        telemetry.Duration.Record(
            elapsedMs,
            new KeyValuePair<string, object?>("demo.order.outcome", "accepted"));
        logger.LogInformation("Order {OrderId} accepted for SKU {Sku}", orderId, request.Sku);

        return Results.Accepted($"/orders/{orderId}", new OrderResponse(
            orderId,
            "accepted",
            inventoryResult.Available,
            Activity.Current?.TraceId.ToString()));
    }
    catch (InventoryTimeoutException exception)
    {
        var elapsedMs = Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;
        telemetry.Orders.Add(
            1,
            new KeyValuePair<string, object?>("demo.order.outcome", "timeout"));
        telemetry.Duration.Record(
            elapsedMs,
            new KeyValuePair<string, object?>("demo.order.outcome", "timeout"));
        activity?.SetStatus(ActivityStatusCode.Error, exception.Message);
        logger.LogError(
            exception,
            "Order failed after {Attempts} inventory attempts for SKU {Sku}",
            exception.Attempts,
            request.Sku);

        return Results.Problem(
            title: "Inventory dependency timed out",
            detail: exception.Message,
            statusCode: StatusCodes.Status504GatewayTimeout,
            extensions: new Dictionary<string, object?>
            {
                ["traceId"] = Activity.Current?.TraceId.ToString(),
                ["attempts"] = exception.Attempts
            });
    }
});

app.Run();

public sealed record OrderRequest(string Sku, int Quantity);

public sealed record OrderResponse(
    string OrderId,
    string Status,
    bool InventoryAvailable,
    string? TraceId);

public sealed record InventoryResponse(
    string Sku,
    int RequestedQuantity,
    bool Available,
    int ProcessingDelayMs,
    bool FaultInjected);

public sealed class InventoryClient(HttpClient httpClient)
{
    public async Task<InventoryResponse> CheckAsync(
        string sku,
        int quantity,
        IConfiguration configuration,
        OrderTelemetry telemetry,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var timeoutMs = configuration.GetValue("Inventory:TimeoutMs", 700);
        var retryDelays = configuration
            .GetSection("Inventory:RetryDelaysMs")
            .Get<int[]>() ?? [150, 300];
        var attempts = retryDelays.Length + 1;

        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromMilliseconds(timeoutMs));
                using var response = await httpClient.GetAsync(
                    $"/inventory/{Uri.EscapeDataString(sku)}?quantity={quantity}",
                    timeout.Token);
                response.EnsureSuccessStatusCode();

                return await response.Content.ReadFromJsonAsync<InventoryResponse>(
                    cancellationToken: cancellationToken)
                    ?? throw new InvalidOperationException("Inventory returned an empty response.");
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                Activity.Current?.AddEvent(new ActivityEvent(
                    "inventory.timeout",
                    tags: new ActivityTagsCollection
                    {
                        ["demo.retry.attempt"] = attempt,
                        ["demo.timeout_ms"] = timeoutMs
                    }));

                if (attempt == attempts)
                {
                    throw new InventoryTimeoutException(sku, attempts, timeoutMs);
                }

                var delay = retryDelays[attempt - 1];
                telemetry.Retries.Add(
                    1,
                    new KeyValuePair<string, object?>("server.address", "inventory-service"));
                logger.LogWarning(
                    "Inventory attempt {Attempt}/{Attempts} timed out after {TimeoutMs} ms; retrying in {DelayMs} ms",
                    attempt,
                    attempts,
                    timeoutMs,
                    delay);
                await Task.Delay(delay, cancellationToken);
            }
            catch (HttpRequestException exception) when (
                exception.StatusCode is null or HttpStatusCode.ServiceUnavailable)
            {
                throw new InvalidOperationException("Inventory dependency is unavailable.", exception);
            }
        }

        throw new UnreachableException();
    }
}

public sealed class InventoryTimeoutException(string sku, int attempts, int timeoutMs)
    : TimeoutException($"Inventory lookup for {sku} exceeded {timeoutMs} ms on all {attempts} attempts.")
{
    public int Attempts { get; } = attempts;
}

public sealed class OrderTelemetry : IDisposable
{
    public OrderTelemetry(IHostEnvironment environment) : this(environment.ApplicationName)
    {
    }

    public OrderTelemetry(string serviceName)
    {
        ActivitySource = new ActivitySource(serviceName);
        Meter = new Meter(serviceName);
        Orders = Meter.CreateCounter<long>("demo.orders", description: "Orders by outcome");
        Duration = Meter.CreateHistogram<double>(
            "demo.order.processing.duration",
            unit: "ms",
            description: "Order processing duration");
        Retries = Meter.CreateCounter<long>("demo.http.retries", description: "Dependency retries");
    }

    public ActivitySource ActivitySource { get; }
    public Meter Meter { get; }
    public Counter<long> Orders { get; }
    public Histogram<double> Duration { get; }
    public Counter<long> Retries { get; }

    public void Dispose()
    {
        ActivitySource.Dispose();
        Meter.Dispose();
    }
}

public partial class Program;
