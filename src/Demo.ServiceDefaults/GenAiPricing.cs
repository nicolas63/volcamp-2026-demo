using Microsoft.Extensions.Configuration;

namespace Demo.ServiceDefaults;

public sealed record GenAiCostBreakdown(double InputUsd, double CachedInputUsd, double OutputUsd)
{
    public double TotalUsd => InputUsd + CachedInputUsd + OutputUsd;
}

public sealed record GenAiPricing(
    double InputUsdPerMillion,
    double CachedInputUsdPerMillion,
    double OutputUsdPerMillion)
{
    public static GenAiPricing FromConfiguration(IConfiguration configuration) => new(
        configuration.GetValue("AzureOpenAI:Pricing:InputUsdPerMillion", 0.40),
        configuration.GetValue("AzureOpenAI:Pricing:CachedInputUsdPerMillion", 0.10),
        configuration.GetValue("AzureOpenAI:Pricing:OutputUsdPerMillion", 1.60));

    // Reasoning tokens are already included in output tokens and must not be billed twice.
    public GenAiCostBreakdown Estimate(double inputTokens, double cachedInputTokens, double outputTokens)
    {
        inputTokens = Math.Max(0, inputTokens);
        outputTokens = Math.Max(0, outputTokens);
        cachedInputTokens = Math.Clamp(cachedInputTokens, 0, inputTokens);

        return new GenAiCostBreakdown(
            (inputTokens - cachedInputTokens) * InputUsdPerMillion / 1_000_000,
            cachedInputTokens * CachedInputUsdPerMillion / 1_000_000,
            outputTokens * OutputUsdPerMillion / 1_000_000);
    }
}
