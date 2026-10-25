using System.Reflection;
using ClaudeAgentPipelines.Claude;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;

namespace ClaudeAgentPipelines.Pipelines;

/// <summary>
/// Runs a Microsoft Agent Framework workflow of <see cref="ClaudeCodeChatClient"/> agents and reports what happens to
/// the matching steps of a <see cref="PipelineRun"/>. Each agent's ID must be the AgentId of its step.
/// </summary>
public static class AgentWorkflowExecution
{
    public static async Task RunAsync(Workflow workflow, string input, PipelineRun run, IReadOnlyCollection<string> agentIds,
                                      CancellationToken cancellationToken)
    {
        await using var execution = await InProcessExecution.RunStreamingAsync(
            workflow, new List<ChatMessage> { new(ChatRole.User, input) }, cancellationToken: cancellationToken);
        await execution.TrySendMessageAsync(new TurnToken(emitEvents: true));

        Exception? failure = null;
        await foreach (var evt in execution.WatchStreamAsync(cancellationToken))
        {
            switch (evt)
            {
                // Each agent executor is invoked with the accumulated messages and then with the turn token that starts it.
                case ExecutorInvokedEvent { Data: TurnToken } invoked when FindAgentId(agentIds, invoked.ExecutorId) is { } agentId:
                    run.StepStarted(agentId);
                    break;

                case AgentResponseUpdateEvent { Update: var update }:
                    HandleUpdate(agentIds, run, update);
                    break;

                case ExecutorCompletedEvent completed when FindAgentId(agentIds, completed.ExecutorId) is { } agentId:
                    run.StepCompleted(agentId);
                    break;

                case ExecutorFailedEvent failed:
                    failure = Unwrap(failed.Data as Exception) ?? new InvalidOperationException("An agent failed.");
                    if (FindAgentId(agentIds, failed.ExecutorId) is { } failedId) run.StepFailed(failedId, failure.Message);
                    break;

                case WorkflowErrorEvent error:
                    failure ??= Unwrap(error.Exception) ?? new InvalidOperationException("The workflow failed.");
                    break;
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (failure is not null) throw new PipelineFailedException(failure.Message, failure);

        // Concurrent workflows don't always report each agent's completion; the workflow ending without errors does.
        foreach (var agentId in agentIds) run.StepCompleted(agentId);
    }

    private static void HandleUpdate(IReadOnlyCollection<string> agentIds, PipelineRun run, AgentResponseUpdate update)
    {
        var agentId = update.AgentId;
        if (agentId is null || !agentIds.Contains(agentId)) return;

        // An agent can stream before ExecutorInvokedEvent is seen; starting twice is a no-op.
        run.StepStarted(agentId);

        if (update.RawRepresentation is ChatResponseUpdate { AdditionalProperties: { } properties }
            && properties.TryGetValue(ClaudeCodeChatClient.ActivityKey, out var activity) && activity is string text)
        {
            run.StepActivity(agentId, text);
        }

        foreach (var usage in update.Contents.OfType<UsageContent>())
        {
            run.StepUsage(agentId, usage.Details.InputTokenCount, usage.Details.OutputTokenCount);
        }

        var delta = update.Text;
        if (!string.IsNullOrEmpty(delta)) run.StepText(agentId, delta);
    }

    /// <summary>Agent executors are named "{agent name}_{agent id}".</summary>
    private static string? FindAgentId(IReadOnlyCollection<string> agentIds, string? executorId) =>
        executorId is null
            ? null
            : agentIds.FirstOrDefault(id => executorId == id || executorId.EndsWith("_" + id, StringComparison.Ordinal));

    private static Exception? Unwrap(Exception? exception)
    {
        while (exception is TargetInvocationException or AggregateException && exception.InnerException is not null)
        {
            exception = exception.InnerException;
        }
        return exception;
    }
}

public sealed class PipelineFailedException(string message, Exception inner) : Exception(message, inner);
