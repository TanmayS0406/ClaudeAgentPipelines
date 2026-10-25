using System.Reflection;
using System.Text;
using ClaudeAgentPipelines.Claude;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace ClaudeAgentPipelines.Pipelines;

/// <summary>
/// Runs a pipeline as a Microsoft Agent Framework sequential workflow. Every agent is a <see cref="ChatClientAgent"/>
/// on a <see cref="ClaudeCodeChatClient"/>, so it runs on the signed-in Claude account and can read only its own files.
/// </summary>
public sealed class PipelineRunner(
    ClaudeCli cli,
    PipelineStore store,
    IOptionsMonitor<ClaudeOptions> claudeOptions,
    ILoggerFactory loggerFactory)
{
    public async Task RunAsync(Pipeline pipeline, PipelineRun run, CancellationToken cancellationToken)
    {
        // An empty working folder outside the data folder, so agents cannot browse other agents' files from it.
        var workspace = Path.Combine(Path.GetTempPath(), "ClaudeAgentPipelines", run.Id);
        Directory.CreateDirectory(workspace);
        try
        {
            await RunWorkflowAsync(pipeline, run, workspace, cancellationToken);
        }
        finally
        {
            try
            {
                Directory.Delete(workspace, recursive: true);
            }
            catch (IOException)
            {
                // Still in use by a CLI process that is shutting down.
            }
        }
    }

    private async Task RunWorkflowAsync(Pipeline pipeline, PipelineRun run, string workspace, CancellationToken cancellationToken)
    {
        var agents = pipeline.Agents.Select((agent, index) => CreateAgent(pipeline, index, agent, workspace)).ToList();
        var workflow = AgentWorkflowBuilder.BuildSequential(pipeline.Name, agents);

        await using var execution = await InProcessExecution.RunStreamingAsync(
            workflow, new List<ChatMessage> { new(ChatRole.User, run.Input) }, cancellationToken: cancellationToken);
        await execution.TrySendMessageAsync(new TurnToken(emitEvents: true));

        Exception? failure = null;
        await foreach (var evt in execution.WatchStreamAsync(cancellationToken))
        {
            switch (evt)
            {
                // Each agent executor is invoked with the accumulated messages and then with the turn token that starts it.
                case ExecutorInvokedEvent { Data: TurnToken } invoked when FindAgentId(pipeline, invoked.ExecutorId) is { } agentId:
                    run.StepStarted(agentId);
                    break;

                case AgentResponseUpdateEvent { Update: var update }:
                    HandleUpdate(pipeline, run, update);
                    break;

                case ExecutorCompletedEvent completed when FindAgentId(pipeline, completed.ExecutorId) is { } agentId:
                    run.StepCompleted(agentId);
                    break;

                case ExecutorFailedEvent failed:
                    failure = Unwrap(failed.Data as Exception) ?? new InvalidOperationException("An agent failed.");
                    if (FindAgentId(pipeline, failed.ExecutorId) is { } failedId) run.StepFailed(failedId, failure.Message);
                    break;

                case WorkflowErrorEvent error:
                    failure ??= Unwrap(error.Exception) ?? new InvalidOperationException("The pipeline failed.");
                    break;
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (failure is not null) throw new PipelineFailedException(failure.Message, failure);
    }

    private AIAgent CreateAgent(Pipeline pipeline, int index, AgentDefinition agent, string workspace)
    {
        var options = claudeOptions.CurrentValue;
        var filesFolder = store.AgentFolder(pipeline.Id, agent.Id)!;
        var files = agent.Files
            .Select(f => Path.Combine(filesFolder, f.FileName))
            .Where(File.Exists)
            .ToList();

        var chatClient = new ClaudeCodeChatClient(cli, new ClaudeCodeChatClientOptions
        {
            WorkingDirectory = workspace,
            AdditionalDirectories = files.Count > 0 ? [filesFolder] : [],
            Model = EffectiveModel(agent, options),
            Timeout = TimeSpan.FromMinutes(Math.Max(1, options.AgentTimeoutMinutes))
        });

        return new ChatClientAgent(chatClient, new ChatClientAgentOptions
        {
            Id = agent.Id,
            Name = agent.Name,
            ChatOptions = new ChatOptions { Instructions = BuildInstructions(pipeline, index, agent, files) }
        }, loggerFactory);
    }

    public static string EffectiveModel(AgentDefinition agent, ClaudeOptions options) =>
        !string.IsNullOrWhiteSpace(agent.Model) ? agent.Model.Trim() : options.DefaultModel.Trim();

    private static string BuildInstructions(Pipeline pipeline, int index, AgentDefinition agent, List<string> files)
    {
        var isLast = index == pipeline.Agents.Count - 1;
        var sb = new StringBuilder()
            .Append($"You are \"{agent.Name}\", step {index + 1} of {pipeline.Agents.Count} in the multi-agent pipeline \"{pipeline.Name}\". ")
            .Append("Your input is the pipeline input followed by the replies of the earlier steps, each labelled with its author. ")
            .Append(isLast
                ? "Your reply is the final result of the pipeline."
                : "Your reply is passed on to the next step, so reply with your result only - no questions, because nobody can answer them.")
            .Append("\n\n# Your instructions\n\n")
            .Append(agent.Instructions.Trim());

        if (files.Count > 0)
        {
            sb.Append("\n\n# Attached files\n\n")
              .Append("These reference files are attached to you. Read the ones relevant to your task with the Read tool before you answer:\n");
            foreach (var file in files) sb.Append("- ").Append(file).Append('\n');
        }
        return sb.ToString();
    }

    private static void HandleUpdate(Pipeline pipeline, PipelineRun run, AgentResponseUpdate update)
    {
        var agentId = update.AgentId;
        if (agentId is null || pipeline.Agents.All(a => a.Id != agentId)) return;

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
    private static string? FindAgentId(Pipeline pipeline, string? executorId) =>
        executorId is null
            ? null
            : pipeline.Agents.FirstOrDefault(a => executorId == a.Id || executorId.EndsWith("_" + a.Id, StringComparison.Ordinal))?.Id;

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
