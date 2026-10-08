namespace DiagnosticAgent.Guard;

public sealed class AgentGuardOptions
{
    public const string SectionName = "AgentGuard";

    public double MaxDurationSeconds { get; set; } = 90;
    public long MaxTotalTokens { get; set; } = 60_000;
    public double MaxCostUsd { get; set; } = 0.05;
    public int MaxToolCalls { get; set; } = 8;
    public Dictionary<string, AgentGuardProfileOptions> Profiles { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
    public GlobalBudgetOptions GlobalBudget { get; set; } = new();

    public AgentGuardLimits Resolve(string? profileName)
    {
        var defaults = new AgentGuardLimits(
            "Default",
            TimeSpan.FromSeconds(MaxDurationSeconds),
            MaxTotalTokens,
            MaxCostUsd,
            MaxToolCalls);

        if (string.IsNullOrWhiteSpace(profileName)
            || profileName.Equals("Default", StringComparison.OrdinalIgnoreCase))
        {
            return defaults;
        }

        var key = Profiles.Keys.FirstOrDefault(
            name => name.Equals(profileName, StringComparison.OrdinalIgnoreCase));
        if (key is null)
        {
            throw new UnknownGuardProfileException(profileName, ["Default", .. Profiles.Keys]);
        }

        var profile = Profiles[key];

        // A profile may only tighten the defaults, never loosen them.
        return new AgentGuardLimits(
            key,
            profile.MaxDurationSeconds is { } duration
                ? TimeSpan.FromSeconds(Math.Min(duration, MaxDurationSeconds))
                : defaults.MaxDuration,
            Math.Min(profile.MaxTotalTokens ?? MaxTotalTokens, MaxTotalTokens),
            Math.Min(profile.MaxCostUsd ?? MaxCostUsd, MaxCostUsd),
            Math.Min(profile.MaxToolCalls ?? MaxToolCalls, MaxToolCalls));
    }
}

public sealed class AgentGuardProfileOptions
{
    public double? MaxDurationSeconds { get; set; }
    public long? MaxTotalTokens { get; set; }
    public double? MaxCostUsd { get; set; }
    public int? MaxToolCalls { get; set; }
}

public sealed class GlobalBudgetOptions
{
    public bool Enabled { get; set; } = true;
    public double MaxCostUsdPerHour { get; set; } = 0.50;
    public string PrometheusBaseUrl { get; set; } = "http://localhost:9090";
    public string Query { get; set; } =
        "sum(increase({__name__=~\"demo_gen_ai_estimated_cost.*_total\"}[1h]))";
    public bool FailOpen { get; set; } = true;
    public double TimeoutSeconds { get; set; } = 3;
}

public sealed record AgentGuardLimits(
    string Profile,
    TimeSpan MaxDuration,
    long MaxTotalTokens,
    double MaxCostUsd,
    int MaxToolCalls);

public sealed class UnknownGuardProfileException(string profile, IEnumerable<string> known)
    : Exception($"Profil de guard inconnu « {profile} ». Profils disponibles : {string.Join(", ", known)}.");
