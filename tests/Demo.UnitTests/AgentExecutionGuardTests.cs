using System.Net;
using System.Text;
using Demo.ServiceDefaults;
using DiagnosticAgent.Guard;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Demo.UnitTests;

public sealed class AgentExecutionGuardTests
{
    private static readonly GenAiPricing Pricing = new(0.40, 0.10, 1.60);

    private static AgentGuardLimits Limits(
        long tokens = 60_000,
        double cost = 0.05,
        int tools = 8,
        double seconds = 90) =>
        new("Test", TimeSpan.FromSeconds(seconds), tokens, cost, tools);

    [Fact]
    public void Profiles_can_only_tighten_default_limits()
    {
        var options = new AgentGuardOptions
        {
            Profiles =
            {
                ["Strict"] = new AgentGuardProfileOptions { MaxTotalTokens = 8_000, MaxToolCalls = 2 },
                ["Loose"] = new AgentGuardProfileOptions { MaxTotalTokens = 1_000_000, MaxCostUsd = 10 }
            }
        };

        var strict = options.Resolve("strict");
        Assert.Equal("Strict", strict.Profile);
        Assert.Equal(8_000, strict.MaxTotalTokens);
        Assert.Equal(2, strict.MaxToolCalls);
        Assert.Equal(0.05, strict.MaxCostUsd);

        var loose = options.Resolve("Loose");
        Assert.Equal(60_000, loose.MaxTotalTokens);
        Assert.Equal(0.05, loose.MaxCostUsd);

        Assert.Equal("Default", options.Resolve(null).Profile);
        Assert.Throws<UnknownGuardProfileException>(() => options.Resolve("Unknown"));
    }

    [Fact]
    public async Task Chat_client_refuses_model_call_once_token_budget_is_reached()
    {
        using var guard = new AgentExecutionGuard(Limits(tokens: 8_000), Pricing, CancellationToken.None);
        var fake = new FakeChatClient(_ => new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok"))
        {
            Usage = new UsageDetails { InputTokenCount = 4_000, OutputTokenCount = 500 }
        });
        using var client = new GuardedChatClient(fake, guard);

        await client.GetResponseAsync("1");
        await client.GetResponseAsync("2");
        var exception = await Assert.ThrowsAsync<AgentGuardTriggeredException>(
            () => client.GetResponseAsync("3"));

        Assert.Equal(GuardReasons.Tokens, exception.Trigger.Reason);
        Assert.Equal(9_000, exception.Trigger.Consumed);
        Assert.Equal(2, fake.Calls);
        Assert.Equal(GuardReasons.Tokens, guard.Trigger?.Reason);
    }

    [Fact]
    public async Task Chat_client_refuses_model_call_once_cost_budget_is_reached()
    {
        using var guard = new AgentExecutionGuard(Limits(cost: 0.001), Pricing, CancellationToken.None);
        var fake = new FakeChatClient(_ => new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok"))
        {
            Usage = new UsageDetails
            {
                InputTokenCount = 2_000,
                CachedInputTokenCount = 1_000,
                OutputTokenCount = 500
            }
        });
        using var client = new GuardedChatClient(fake, guard);

        await client.GetResponseAsync("1");
        var exception = await Assert.ThrowsAsync<AgentGuardTriggeredException>(
            () => client.GetResponseAsync("2"));

        Assert.Equal(GuardReasons.Cost, exception.Trigger.Reason);
        Assert.Equal(0.0013, guard.CostUsd, precision: 7);
    }

    [Fact]
    public async Task Tool_limit_refuses_call_and_stops_function_invocation_loop()
    {
        using var guard = new AgentExecutionGuard(Limits(tools: 2), Pricing, CancellationToken.None);
        var toolExecutions = 0;
        var tool = new GuardedAIFunction(
            AIFunctionFactory.Create(() => ++toolExecutions, "get_service_health"),
            guard);
        var fake = new FakeChatClient(call => new ChatResponse(new ChatMessage(
            ChatRole.Assistant,
            [new FunctionCallContent($"call-{call}", "get_service_health")]))
        {
            Usage = new UsageDetails { InputTokenCount = 100, OutputTokenCount = 10 }
        });
        using var client = new FunctionInvokingChatClient(new GuardedChatClient(fake, guard));

        await client.GetResponseAsync("diagnose", new ChatOptions { Tools = [tool] });

        Assert.Equal(2, toolExecutions);
        Assert.Equal(3, fake.Calls);
        Assert.Equal(GuardReasons.ToolCalls, guard.Trigger?.Reason);
        Assert.Equal(3, guard.Trigger?.Consumed);
        Assert.Collection(
            guard.ToolCalls,
            call => Assert.Equal("succès", call.Status),
            call => Assert.Equal("succès", call.Status),
            call => Assert.Equal("refusé par le guard", call.Status));
    }

    [Fact]
    public async Task Duration_limit_cancels_the_guard_token()
    {
        using var guard = new AgentExecutionGuard(Limits(seconds: 0.05), Pricing, CancellationToken.None);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Task.Delay(TimeSpan.FromSeconds(5), guard.Token));

        Assert.True(guard.TimedOut);
        Assert.Equal(GuardReasons.Duration, guard.TriggerDuration().Reason);
    }

    [Fact]
    public async Task Global_budget_is_exceeded_when_prometheus_cost_reaches_limit()
    {
        var checker = CreateChecker(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"status":"success","data":{"resultType":"vector","result":[{"metric":{},"value":[1,"0.72"]}]}}""",
                Encoding.UTF8,
                "application/json")
        });

        var status = await checker.CheckAsync(CancellationToken.None);

        Assert.True(status.Checked);
        Assert.True(status.Exceeded);
        Assert.Equal(0.72, status.ObservedCostUsd);
    }

    [Fact]
    public async Task Global_budget_fails_open_when_prometheus_is_unavailable()
    {
        var checker = CreateChecker(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        var status = await checker.CheckAsync(CancellationToken.None);

        Assert.False(status.Checked);
        Assert.False(status.Exceeded);
        Assert.NotNull(status.Error);
    }

    private static GlobalBudgetChecker CreateChecker(Func<HttpRequestMessage, HttpResponseMessage> handler) =>
        new(
            new StubHttpClientFactory(handler),
            Options.Create(new AgentGuardOptions()),
            NullLogger<GlobalBudgetChecker>.Instance);

    private sealed class FakeChatClient(Func<int, ChatResponse> respond) : IChatClient
    {
        public int Calls { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(respond(++Calls));

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}

internal sealed class StubHttpClientFactory(Func<HttpRequestMessage, HttpResponseMessage> handler)
    : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(new StubHandler(handler));

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(handler(request));
    }
}
