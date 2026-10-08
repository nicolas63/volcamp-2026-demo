using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using OpenTelemetry;

namespace Demo.ServiceDefaults;

public sealed class GenAiCostProcessor : BaseProcessor<Activity>
{
    private readonly GenAiPricing pricing;
    private readonly Meter meter;
    private readonly Counter<double> estimatedCost;

    public GenAiCostProcessor(
        string meterName,
        double inputUsdPerMillion,
        double cachedInputUsdPerMillion,
        double outputUsdPerMillion)
        : this(meterName, new GenAiPricing(inputUsdPerMillion, cachedInputUsdPerMillion, outputUsdPerMillion))
    {
    }

    public GenAiCostProcessor(string meterName, GenAiPricing pricing)
    {
        this.pricing = pricing;
        meter = new Meter(meterName);
        estimatedCost = meter.CreateCounter<double>(
            "demo.gen_ai.estimated_cost",
            unit: "USD",
            description: "Estimated Azure OpenAI token cost");
    }

    public override void OnEnd(Activity activity)
    {
        if (!string.Equals(
                ReadString(activity, "gen_ai.operation.name"),
                "invoke_agent",
                StringComparison.Ordinal)
            || !TryReadDouble(activity, "gen_ai.usage.input_tokens", out var inputTokens)
            || !TryReadDouble(activity, "gen_ai.usage.output_tokens", out var outputTokens))
        {
            return;
        }

        TryReadDouble(activity, "gen_ai.usage.cache_read.input_tokens", out var cachedInputTokens);
        var cost = pricing.Estimate(inputTokens, cachedInputTokens, outputTokens);
        var totalCost = cost.TotalUsd;
        var model = ReadString(activity, "gen_ai.response.model")
            ?? ReadString(activity, "gen_ai.request.model")
            ?? "unknown";

        activity.SetTag("demo.gen_ai.estimated_cost.usd", totalCost);
        activity.SetTag("demo.gen_ai.estimated_input_cost.usd", cost.InputUsd + cost.CachedInputUsd);
        activity.SetTag("demo.gen_ai.estimated_output_cost.usd", cost.OutputUsd);
        activity.SetTag("demo.gen_ai.pricing.input_usd_per_million", pricing.InputUsdPerMillion);
        activity.SetTag(
            "demo.gen_ai.pricing.cached_input_usd_per_million",
            pricing.CachedInputUsdPerMillion);
        activity.SetTag("demo.gen_ai.pricing.output_usd_per_million", pricing.OutputUsdPerMillion);
        activity.SetTag("demo.gen_ai.pricing.currency", "USD");
        activity.SetTag("demo.gen_ai.pricing.type", "estimated");

        estimatedCost.Add(
            totalCost,
            new KeyValuePair<string, object?>("gen_ai.request.model", model),
            new KeyValuePair<string, object?>("demo.pricing.currency", "USD"),
            new KeyValuePair<string, object?>("demo.pricing.type", "estimated"));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            meter.Dispose();
        }

        base.Dispose(disposing);
    }

    private static string? ReadString(Activity activity, string name) =>
        activity.GetTagItem(name)?.ToString();

    private static bool TryReadDouble(Activity activity, string name, out double value)
    {
        var raw = activity.GetTagItem(name);
        return raw switch
        {
            null => ReturnFalse(out value),
            byte number => Return(number, out value),
            short number => Return(number, out value),
            int number => Return(number, out value),
            long number => Return(number, out value),
            float number => Return(number, out value),
            double number => Return(number, out value),
            decimal number => Return((double)number, out value),
            _ => double.TryParse(
                raw.ToString(),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out value)
        };
    }

    private static bool Return(double number, out double value)
    {
        value = number;
        return true;
    }

    private static bool ReturnFalse(out double value)
    {
        value = 0;
        return false;
    }
}
