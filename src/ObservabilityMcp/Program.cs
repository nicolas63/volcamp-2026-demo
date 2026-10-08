using ModelContextProtocol.Server;
using ObservabilityMcp;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();

builder.Services.Configure<ObservabilityOptions>(
    builder.Configuration.GetSection(ObservabilityOptions.SectionName));
builder.Services.AddHttpClient("prometheus", (services, client) =>
{
    var options = services.GetRequiredService<IConfiguration>()
        .GetSection(ObservabilityOptions.SectionName)
        .Get<ObservabilityOptions>() ?? new ObservabilityOptions();
    client.BaseAddress = new Uri(options.PrometheusBaseUrl);
    client.Timeout = TimeSpan.FromSeconds(5);
});
builder.Services.AddHttpClient("tempo", (services, client) =>
{
    var options = services.GetRequiredService<IConfiguration>()
        .GetSection(ObservabilityOptions.SectionName)
        .Get<ObservabilityOptions>() ?? new ObservabilityOptions();
    client.BaseAddress = new Uri(options.TempoBaseUrl);
    client.Timeout = TimeSpan.FromSeconds(5);
});
builder.Services.AddHttpClient("loki", (services, client) =>
{
    var options = services.GetRequiredService<IConfiguration>()
        .GetSection(ObservabilityOptions.SectionName)
        .Get<ObservabilityOptions>() ?? new ObservabilityOptions();
    client.BaseAddress = new Uri(options.LokiBaseUrl);
    client.Timeout = TimeSpan.FromSeconds(5);
});

builder.Services.AddSingleton<ObservabilityBackend>();
builder.Services.AddMcpServer()
    .WithHttpTransport()
    .WithTools<ObservabilityTools>();

var app = builder.Build();
app.MapDefaultEndpoints();
app.MapGet("/", () => Results.Ok(new
{
    service = "observability-mcp",
    transport = "streamable-http",
    mutationTools = 0,
    tools = new[]
    {
        "get_service_health",
        "find_slow_traces",
        "get_trace_summary",
        "search_correlated_logs",
        "get_dependency_metrics"
    }
}));
app.MapMcp("/mcp");

app.Run();

public partial class Program;
