using System.Diagnostics.CodeAnalysis;
using Demo.ServiceDefaults;
using Microsoft.Extensions.AI;

namespace DiagnosticAgent.Guard;

public static class GuardReasons
{
    public const string Duration = "duration";
    public const string Tokens = "tokens";
    public const string Cost = "cost";
    public const string ToolCalls = "tool_calls";
    public const string GlobalBudget = "global_budget";
}

public sealed record AgentGuardTrigger(string Reason, double Consumed, double Limit, string Unit);

public sealed record GuardedToolCall(string Name, long DurationMs, string Status);

public sealed class AgentGuardTriggeredException(AgentGuardTrigger trigger)
    : Exception($"Guard déclenché ({trigger.Reason}) : {trigger.Consumed} / {trigger.Limit} {trigger.Unit}.")
{
    public AgentGuardTrigger Trigger { get; } = trigger;
}

public sealed class AgentExecutionGuard : IDisposable
{
    private readonly Lock gate = new();
    private readonly GenAiPricing pricing;
    private readonly TimeProvider timeProvider;
    private readonly long startedAt;
    private readonly CancellationTokenSource timeout;
    private readonly CancellationTokenSource linked;
    private readonly List<GuardedToolCall> toolCalls = [];
    private long inputTokens;
    private long cachedInputTokens;
    private long outputTokens;
    private int toolCallCount;
    private int modelCallCount;
    private AgentGuardTrigger? trigger;

    public AgentExecutionGuard(
        AgentGuardLimits limits,
        GenAiPricing pricing,
        CancellationToken requestCancellation,
        TimeProvider? timeProvider = null)
    {
        Limits = limits;
        this.pricing = pricing;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        startedAt = this.timeProvider.GetTimestamp();
        timeout = new CancellationTokenSource(limits.MaxDuration, this.timeProvider);
        linked = CancellationTokenSource.CreateLinkedTokenSource(requestCancellation, timeout.Token);
    }

    public AgentGuardLimits Limits { get; }
    public CancellationToken Token => linked.Token;
    public bool TimedOut => timeout.IsCancellationRequested;
    public TimeSpan Elapsed => timeProvider.GetElapsedTime(startedAt);

    public long InputTokens { get { lock (gate) { return inputTokens; } } }
    public long CachedInputTokens { get { lock (gate) { return cachedInputTokens; } } }
    public long OutputTokens { get { lock (gate) { return outputTokens; } } }
    public long TotalTokens { get { lock (gate) { return inputTokens + outputTokens; } } }
    public int ToolCallCount { get { lock (gate) { return toolCallCount; } } }
    public int ModelCallCount { get { lock (gate) { return modelCallCount; } } }
    public double CostUsd
    {
        get
        {
            lock (gate)
            {
                return pricing.Estimate(inputTokens, cachedInputTokens, outputTokens).TotalUsd;
            }
        }
    }

    public AgentGuardTrigger? Trigger { get { lock (gate) { return trigger; } } }

    public IReadOnlyList<GuardedToolCall> ToolCalls
    {
        get { lock (gate) { return [.. toolCalls]; } }
    }

    public void RecordUsage(UsageDetails? usage)
    {
        if (usage is null)
        {
            return;
        }

        lock (gate)
        {
            inputTokens += usage.InputTokenCount ?? 0;
            cachedInputTokens += usage.CachedInputTokenCount ?? 0;
            outputTokens += usage.OutputTokenCount ?? 0;
        }
    }

    public void EnsureCanCallModel()
    {
        lock (gate)
        {
            var reached = trigger ?? CheckBudgetsLocked();
            if (reached is not null)
            {
                trigger ??= reached;
                throw new AgentGuardTriggeredException(reached);
            }

            modelCallCount++;
        }
    }

    public bool TryBeginToolCall([NotNullWhen(false)] out AgentGuardTrigger? refused)
    {
        lock (gate)
        {
            refused = trigger ?? CheckBudgetsLocked();
            if (refused is null && toolCallCount >= Limits.MaxToolCalls)
            {
                refused = new AgentGuardTrigger(
                    GuardReasons.ToolCalls,
                    toolCallCount + 1,
                    Limits.MaxToolCalls,
                    "appels");
            }

            if (refused is not null)
            {
                trigger ??= refused;
                return false;
            }

            toolCallCount++;
            return true;
        }
    }

    public void CompleteToolCall(string name, TimeSpan duration, string status)
    {
        lock (gate)
        {
            toolCalls.Add(new GuardedToolCall(name, (long)duration.TotalMilliseconds, status));
        }
    }

    public AgentGuardTrigger TriggerDuration()
    {
        lock (gate)
        {
            trigger ??= new AgentGuardTrigger(
                GuardReasons.Duration,
                Math.Round(Elapsed.TotalSeconds, 1),
                Limits.MaxDuration.TotalSeconds,
                "s");
            return trigger;
        }
    }

    public void Dispose()
    {
        linked.Dispose();
        timeout.Dispose();
    }

    private AgentGuardTrigger? CheckBudgetsLocked()
    {
        var tokens = inputTokens + outputTokens;
        if (tokens >= Limits.MaxTotalTokens)
        {
            return new AgentGuardTrigger(GuardReasons.Tokens, tokens, Limits.MaxTotalTokens, "tokens");
        }

        var cost = pricing.Estimate(inputTokens, cachedInputTokens, outputTokens).TotalUsd;
        if (cost >= Limits.MaxCostUsd)
        {
            return new AgentGuardTrigger(GuardReasons.Cost, Math.Round(cost, 6), Limits.MaxCostUsd, "USD");
        }

        return null;
    }
}
