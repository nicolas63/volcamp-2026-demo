using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace DiagnosticAgent.Guard;

public sealed record GlobalBudgetStatus(
    bool Checked,
    double? ObservedCostUsd,
    double LimitUsd,
    bool Exceeded,
    string? Error);

public sealed class GlobalBudgetExceededException(GlobalBudgetStatus status)
    : Exception(string.Create(
        CultureInfo.InvariantCulture,
        $"Budget global dépassé : {status.ObservedCostUsd:0.######} USD consommés sur la dernière heure pour une limite de {status.LimitUsd:0.##} USD."))
{
    public GlobalBudgetStatus Status { get; } = status;
}

public sealed class GlobalBudgetChecker(
    IHttpClientFactory httpClientFactory,
    IOptions<AgentGuardOptions> options,
    ILogger<GlobalBudgetChecker> logger)
{
    public const string HttpClientName = "agent-guard-prometheus";

    public async Task<GlobalBudgetStatus> CheckAsync(CancellationToken cancellationToken)
    {
        var budget = options.Value.GlobalBudget;
        if (!budget.Enabled)
        {
            return new GlobalBudgetStatus(false, null, budget.MaxCostUsdPerHour, false, "disabled");
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(budget.TimeoutSeconds));

            var client = httpClientFactory.CreateClient(HttpClientName);
            var uri = new Uri(
                new Uri(budget.PrometheusBaseUrl.TrimEnd('/') + "/"),
                "api/v1/query?query=" + Uri.EscapeDataString(budget.Query));
            using var response = await client.GetAsync(uri, timeout.Token);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            var cost = ParseScalar(await JsonDocument.ParseAsync(stream, cancellationToken: timeout.Token));
            return new GlobalBudgetStatus(
                true,
                cost,
                budget.MaxCostUsdPerHour,
                cost >= budget.MaxCostUsdPerHour,
                null);
        }
        catch (Exception exception) when (exception is HttpRequestException
            or JsonException
            or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(exception, "Global GenAI budget check against Prometheus failed");
            return new GlobalBudgetStatus(
                false,
                null,
                budget.MaxCostUsdPerHour,
                !budget.FailOpen,
                exception.GetType().Name);
        }
    }

    public static double ParseScalar(JsonDocument document)
    {
        using (document)
        {
            var result = document.RootElement.GetProperty("data").GetProperty("result");
            if (result.ValueKind != JsonValueKind.Array || result.GetArrayLength() == 0)
            {
                return 0;
            }

            var raw = result[0].GetProperty("value")[1].GetString();
            return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                && double.IsFinite(value)
                ? value
                : 0;
        }
    }
}
