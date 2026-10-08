using System.Diagnostics;
using DiagnosticAgent;
using DiagnosticAgent.Guard;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Demo.UnitTests;

public sealed class AgentTelemetryTests
{
    [Fact]
    public async Task Replay_emits_auditable_agent_and_tool_spans()
    {
        var stoppedActivities = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == AgentTelemetry.SourceName,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = stoppedActivities.Add
        };
        ActivitySource.AddActivityListener(listener);

        using var telemetry = new AgentTelemetry();
        var options = Options.Create(new AgentGuardOptions());
        var runner = new DiagnosticAgentRunner(
            new ConfigurationManager(),
            telemetry,
            options,
            new GlobalBudgetChecker(
                new StubHttpClientFactory(_ => throw new InvalidOperationException("not used")),
                options,
                NullLogger<GlobalBudgetChecker>.Instance),
            NullLoggerFactory.Instance,
            NullLogger<DiagnosticAgentRunner>.Instance);

        var result = await runner.RunReplayAsync(
            "Why are orders failing?",
            null,
            CancellationToken.None);

        Assert.Equal("replay", result.Mode);
        Assert.False(result.GuardTriggered);
        Assert.Equal("Default", result.Guard?.Profile);
        Assert.Contains(stoppedActivities, activity => activity.DisplayName == "invoke_agent");
        Assert.Equal(
            5,
            stoppedActivities.Count(activity =>
                activity.DisplayName.StartsWith("execute_tool ", StringComparison.Ordinal)));
        var chat = Assert.Single(
            stoppedActivities,
            activity => activity.DisplayName == "chat conference-replay");
        Assert.Equal(684L, Convert.ToInt64(chat.GetTagItem("gen_ai.usage.input_tokens")));
        Assert.Equal(173L, Convert.ToInt64(chat.GetTagItem("gen_ai.usage.output_tokens")));
    }
}
