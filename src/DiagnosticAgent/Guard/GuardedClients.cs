using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace DiagnosticAgent.Guard;

public sealed class GuardedChatClient(IChatClient innerClient, AgentExecutionGuard guard)
    : DelegatingChatClient(innerClient)
{
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        guard.EnsureCanCallModel();
        var response = await base.GetResponseAsync(messages, options, cancellationToken);
        guard.RecordUsage(response.Usage);
        return response;
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        guard.EnsureCanCallModel();
        await foreach (var update in base.GetStreamingResponseAsync(messages, options, cancellationToken))
        {
            foreach (var usage in update.Contents.OfType<UsageContent>())
            {
                guard.RecordUsage(usage.Details);
            }

            yield return update;
        }
    }
}

public sealed class GuardedAIFunction(AIFunction innerFunction, AgentExecutionGuard guard)
    : DelegatingAIFunction(innerFunction)
{
    protected override async ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments,
        CancellationToken cancellationToken)
    {
        if (!guard.TryBeginToolCall(out var refused))
        {
            guard.CompleteToolCall(Name, TimeSpan.Zero, "refusé par le guard");
            Activity.Current?.AddEvent(new ActivityEvent(
                "guard.triggered",
                tags: new ActivityTagsCollection
                {
                    ["demo.agent.guard.reason"] = refused.Reason,
                    ["demo.agent.guard.consumed"] = refused.Consumed,
                    ["demo.agent.guard.limit"] = refused.Limit
                }));

            // Stop the function-invocation loop: no further LLM call after the refusal.
            if (FunctionInvokingChatClient.CurrentContext is { } context)
            {
                context.Terminate = true;
            }

            return $"Appel refusé par le guard de l'agent ({refused.Reason}). Analyse interrompue.";
        }

        var started = Stopwatch.GetTimestamp();
        try
        {
            var result = await base.InvokeCoreAsync(arguments, cancellationToken);
            guard.CompleteToolCall(Name, Stopwatch.GetElapsedTime(started), "succès");
            return result;
        }
        catch (Exception)
        {
            guard.CompleteToolCall(Name, Stopwatch.GetElapsedTime(started), "échec");
            throw;
        }
    }
}
