namespace DiagnosticAgent;

public sealed record DiagnosticRequest(string? Question);

public sealed record GuardReport(
    string Profile,
    bool Triggered,
    string? Reason,
    double? Consumed,
    double? Limit,
    string? Unit,
    double DurationSeconds,
    double MaxDurationSeconds,
    long TotalTokens,
    long MaxTotalTokens,
    double EstimatedCostUsd,
    double MaxCostUsd,
    int ToolCalls,
    int MaxToolCalls,
    double? GlobalCostLastHourUsd,
    double GlobalBudgetUsdPerHour,
    bool GlobalBudgetChecked);

public sealed record DiagnosticResult(
    string Mode,
    string Question,
    string Diagnosis,
    string? TraceId,
    int LlmAttempts,
    int ToolLimit,
    bool SensitiveContentCaptured,
    bool GuardTriggered = false,
    GuardReport? Guard = null);

public sealed class AgentConfigurationException(string message) : Exception(message);
