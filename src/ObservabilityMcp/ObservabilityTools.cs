using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace ObservabilityMcp;

[McpServerToolType]
public sealed class ObservabilityTools(
    ObservabilityBackend backend,
    ILogger<ObservabilityTools> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    [McpServerTool(Name = "get_service_health")]
    [Description("Returns request count, error ratio and p95 duration for one allowlisted service.")]
    public async Task<string> GetServiceHealthAsync(
        [Description("Allowlisted service name.")] string serviceName,
        [Description("Lookback window in minutes, from 1 through 15.")] int windowMinutes = 5,
        CancellationToken cancellationToken = default)
    {
        serviceName = ToolInputPolicy.Service(serviceName);
        windowMinutes = ToolInputPolicy.Window(windowMinutes);
        var window = $"{windowMinutes}m";

        var requestCount =
            $"round(sum(increase(http_server_request_duration_seconds_count{{service_name=\"{serviceName}\"}}[{window}])))";
        var errorRatio =
            $"sum(increase(http_server_request_duration_seconds_count{{service_name=\"{serviceName}\",http_response_status_code=~\"5..\"}}[{window}]))"
            + " / "
            + $"clamp_min(sum(increase(http_server_request_duration_seconds_count{{service_name=\"{serviceName}\"}}[{window}])), 1)";
        var p95 =
            $"histogram_quantile(0.95, sum by(le) (increase(http_server_request_duration_seconds_bucket{{service_name=\"{serviceName}\"}}[{window}])))";

        var results = await Task.WhenAll(
            backend.QueryPrometheusAsync(requestCount, cancellationToken),
            backend.QueryPrometheusAsync(errorRatio, cancellationToken),
            backend.QueryPrometheusAsync(p95, cancellationToken));

        return AuditAndSerialize(nameof(GetServiceHealthAsync), new
        {
            serviceName,
            windowMinutes,
            requestCount = results[0],
            errorRatio = results[1],
            p95DurationSeconds = results[2]
        });
    }

    [McpServerTool(Name = "find_slow_traces")]
    [Description("Finds slow traces for one allowlisted service in a bounded recent window.")]
    public async Task<string> FindSlowTracesAsync(
        [Description("Allowlisted service name.")] string serviceName,
        [Description("Lookback window in minutes, from 1 through 15.")] int windowMinutes = 5,
        [Description("Minimum trace duration in milliseconds.")] int minDurationMs = 500,
        [Description("Maximum traces to return, from 1 through 10.")] int limit = 5,
        CancellationToken cancellationToken = default)
    {
        serviceName = ToolInputPolicy.Service(serviceName);
        windowMinutes = ToolInputPolicy.Window(windowMinutes);
        limit = ToolInputPolicy.Limit(limit, 10);
        if (minDurationMs is < 1 or > 60_000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(minDurationMs),
                "Minimum duration must be between 1 and 60000 ms.");
        }

        var result = await backend.FindSlowTracesAsync(
            serviceName,
            windowMinutes,
            minDurationMs,
            limit,
            cancellationToken);
        return AuditAndSerialize(nameof(FindSlowTracesAsync), result);
    }

    [McpServerTool(Name = "get_trace_summary")]
    [Description("Returns a compact span tree for one validated trace ID.")]
    public async Task<string> GetTraceSummaryAsync(
        [Description("A 32-character lowercase hexadecimal trace ID.")] string traceId,
        CancellationToken cancellationToken = default)
    {
        traceId = ToolInputPolicy.TraceId(traceId);
        var result = await backend.GetTraceSummaryAsync(traceId, cancellationToken);
        return AuditAndSerialize(nameof(GetTraceSummaryAsync), result);
    }

    [McpServerTool(Name = "search_correlated_logs")]
    [Description("Returns recent logs correlated with one validated trace ID.")]
    public async Task<string> SearchCorrelatedLogsAsync(
        [Description("A 32-character lowercase hexadecimal trace ID.")] string traceId,
        [Description("Maximum log lines to return, from 1 through 50.")] int limit = 20,
        CancellationToken cancellationToken = default)
    {
        traceId = ToolInputPolicy.TraceId(traceId);
        limit = ToolInputPolicy.Limit(limit, 50);
        var result = await backend.SearchLogsAsync(traceId, limit, cancellationToken);
        return AuditAndSerialize(nameof(SearchCorrelatedLogsAsync), result);
    }

    [McpServerTool(Name = "get_dependency_metrics")]
    [Description("Returns request count, p95 duration and retry count for one allowlisted dependency edge.")]
    public async Task<string> GetDependencyMetricsAsync(
        [Description("Allowlisted caller service name.")] string callerService,
        [Description("Allowlisted dependency service name.")] string dependencyService,
        [Description("Lookback window in minutes, from 1 through 15.")] int windowMinutes = 5,
        CancellationToken cancellationToken = default)
    {
        callerService = ToolInputPolicy.Service(callerService);
        dependencyService = ToolInputPolicy.Service(dependencyService);
        var dependencyPort = ToolInputPolicy.DependencyServerPort(callerService, dependencyService);
        windowMinutes = ToolInputPolicy.Window(windowMinutes);
        var window = $"{windowMinutes}m";

        var requestCount =
            $"round(sum(increase(http_client_request_duration_seconds_count{{service_name=\"{callerService}\",server_port=\"{dependencyPort}\"}}[{window}])))";
        var p95 =
            $"histogram_quantile(0.95, sum by(le) (increase(http_client_request_duration_seconds_bucket{{service_name=\"{callerService}\",server_port=\"{dependencyPort}\"}}[{window}])))";
        var retries =
            $"round(sum(increase(demo_http_retries_total{{service_name=\"{callerService}\",server_address=\"{dependencyService}\"}}[{window}])))";

        var results = await Task.WhenAll(
            backend.QueryPrometheusAsync(requestCount, cancellationToken),
            backend.QueryPrometheusAsync(p95, cancellationToken),
            backend.QueryPrometheusAsync(retries, cancellationToken));

        return AuditAndSerialize(nameof(GetDependencyMetricsAsync), new
        {
            callerService,
            dependencyService,
            windowMinutes,
            requestCount = results[0],
            p95DurationSeconds = results[1],
            retryCount = results[2]
        });
    }

    private string AuditAndSerialize(string toolName, object result)
    {
        logger.LogInformation("Read-only observability tool {ToolName} completed", toolName);
        return JsonSerializer.Serialize(result, JsonOptions);
    }
}
