namespace ObservabilityMcp;

public sealed class ObservabilityOptions
{
    public const string SectionName = "Observability";

    public string PrometheusBaseUrl { get; set; } = "http://localhost:9090";
    public string TempoBaseUrl { get; set; } = "http://localhost:3200";
    public string LokiBaseUrl { get; set; } = "http://localhost:3100";
}
