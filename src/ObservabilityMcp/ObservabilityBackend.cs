using System.Globalization;
using System.Text.Json;

namespace ObservabilityMcp;

public sealed class ObservabilityBackend(
    IHttpClientFactory httpClientFactory,
    ILogger<ObservabilityBackend> logger)
{
    public Task<JsonElement> QueryPrometheusAsync(string expression, CancellationToken cancellationToken)
    {
        var query = $"/api/v1/query?query={Uri.EscapeDataString(expression)}";
        return GetJsonAsync("prometheus", query, cancellationToken);
    }

    public async Task<JsonElement> FindSlowTracesAsync(
        string serviceName,
        int windowMinutes,
        int minimumDurationMs,
        int limit,
        CancellationToken cancellationToken)
    {
        var traceQl = $"{{ resource.service.name = \"{serviceName}\" && duration >= {minimumDurationMs}ms }}";

        async Task<JsonElement> SearchAsync()
        {
            var end = DateTimeOffset.UtcNow;
            var start = end.AddMinutes(-windowMinutes);
            var query = string.Create(
                CultureInfo.InvariantCulture,
                $"/api/search?q={Uri.EscapeDataString(traceQl)}&start={start.ToUnixTimeSeconds()}&end={end.ToUnixTimeSeconds()}&limit={limit}");
            return await GetJsonAsync("tempo", query, cancellationToken);
        }

        var result = await SearchAsync();
        if (HasTraces(result))
        {
            return result;
        }

        logger.LogInformation("Tempo returned no traces; retrying once after index propagation delay");
        await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
        return await SearchAsync();
    }

    public async Task<object> GetTraceSummaryAsync(string traceId, CancellationToken cancellationToken)
    {
        var trace = await GetJsonAsync("tempo", $"/api/traces/{traceId}", cancellationToken);
        var spans = new List<object>();

        if (trace.TryGetProperty("batches", out var batches))
        {
            foreach (var batch in batches.EnumerateArray())
            {
                var serviceName = ReadServiceName(batch);
                if (!batch.TryGetProperty("scopeSpans", out var scopeSpans))
                {
                    continue;
                }

                foreach (var scopeSpan in scopeSpans.EnumerateArray())
                {
                    if (!scopeSpan.TryGetProperty("spans", out var batchSpans))
                    {
                        continue;
                    }

                    foreach (var span in batchSpans.EnumerateArray())
                    {
                        spans.Add(new
                        {
                            service = serviceName,
                            name = ReadString(span, "name"),
                            spanId = ReadString(span, "spanId"),
                            parentSpanId = ReadString(span, "parentSpanId"),
                            startUnixNano = ReadString(span, "startTimeUnixNano"),
                            endUnixNano = ReadString(span, "endTimeUnixNano"),
                            status = span.TryGetProperty("status", out var status)
                                ? status.Clone()
                                : default(JsonElement?)
                        });
                    }
                }
            }
        }

        return new
        {
            traceId,
            spanCount = spans.Count,
            spans = spans.Take(50)
        };
    }

    public Task<JsonElement> SearchLogsAsync(
        string traceId,
        int limit,
        CancellationToken cancellationToken)
    {
        var end = DateTimeOffset.UtcNow;
        var start = end.AddMinutes(-15);
        var logQl = $"{{service_name=~\".+\"}} | trace_id=\"{traceId}\"";
        var query = string.Create(
            CultureInfo.InvariantCulture,
            $"/loki/api/v1/query_range?query={Uri.EscapeDataString(logQl)}&start={start.ToUnixTimeMilliseconds() * 1_000_000}&end={end.ToUnixTimeMilliseconds() * 1_000_000}&limit={limit}&direction=backward");
        return GetJsonAsync("loki", query, cancellationToken);
    }

    private async Task<JsonElement> GetJsonAsync(
        string clientName,
        string relativeUri,
        CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient(clientName);
        using var response = await client.GetAsync(relativeUri, cancellationToken);
        var content = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            logger.LogError(
                "{Backend} query failed with status {StatusCode}",
                clientName,
                (int)response.StatusCode);
            throw new HttpRequestException(
                $"{clientName} query failed with HTTP {(int)response.StatusCode}.",
                inner: null,
                response.StatusCode);
        }

        using var document = JsonDocument.Parse(content);
        return document.RootElement.Clone();
    }

    private static string? ReadServiceName(JsonElement batch)
    {
        if (!batch.TryGetProperty("resource", out var resource)
            || !resource.TryGetProperty("attributes", out var attributes))
        {
            return null;
        }

        foreach (var attribute in attributes.EnumerateArray())
        {
            if (ReadString(attribute, "key") != "service.name"
                || !attribute.TryGetProperty("value", out var value))
            {
                continue;
            }

            return ReadString(value, "stringValue");
        }

        return null;
    }

    private static bool HasTraces(JsonElement result) =>
        result.TryGetProperty("traces", out var traces)
        && traces.ValueKind == JsonValueKind.Array
        && traces.GetArrayLength() > 0;

    private static string? ReadString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property)
            ? property.GetString()
            : null;
}
