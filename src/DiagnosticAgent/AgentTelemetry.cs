using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace DiagnosticAgent;

public sealed class AgentTelemetry : IDisposable
{
    public const string SourceName = "DiagnosticAgent";

    public AgentTelemetry()
    {
        ActivitySource = new ActivitySource(SourceName);
        Meter = new Meter(SourceName);
        Invocations = Meter.CreateCounter<long>(
            "demo.agent.invocations",
            description: "Agent invocations by mode and outcome");
        LlmRetries = Meter.CreateCounter<long>(
            "demo.agent.llm.retries",
            description: "LLM retries");
        GuardTriggers = Meter.CreateCounter<long>(
            "demo.agent.guard.triggers",
            description: "Agent runs interrupted or refused by the guard, by reason");
        GuardUsageRatio = Meter.CreateHistogram<double>(
            "demo.agent.guard.usage_ratio",
            description: "Consumed / limit ratio per guard dimension at the end of a run");
    }

    public ActivitySource ActivitySource { get; }
    public Meter Meter { get; }
    public Counter<long> Invocations { get; }
    public Counter<long> LlmRetries { get; }
    public Counter<long> GuardTriggers { get; }
    public Histogram<double> GuardUsageRatio { get; }

    public void Dispose()
    {
        ActivitySource.Dispose();
        Meter.Dispose();
    }
}
