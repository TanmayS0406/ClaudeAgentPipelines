using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeAgentPipelines.Claude;
using ClaudeAgentPipelines.Pipelines;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace ClaudeAgentPipelines.Board;

/// <summary>
/// The orchestrator for a whole board: an agent reads the backlog and the code, decides which features depend on which
/// (and which can be built in parallel), and the board starts every feature whose dependencies are done. Each plan is
/// a run, so its reasoning can be read like any agent's.
/// </summary>
public sealed class BoardOrchestrator(
    BoardStore store,
    RunManager runs,
    FeatureManager features,
    ClaudeCli cli,
    IOptionsMonitor<ClaudeOptions> claudeOptions,
    IOptionsMonitor<BoardOptions> boardOptions,
    ILoggerFactory loggerFactory,
    ILogger<BoardOrchestrator> logger)
{
    /// <summary>Boards that asked for another plan while one was being made.</summary>
    private readonly ConcurrentDictionary<string, bool> _replan = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _startLocks = new();

    /// <summary>
    /// Plans the backlog (in the background). When a plan is being made already, another one follows it, so features
    /// added meanwhile are planned too. Returns an error message when it can't plan.
    /// </summary>
    public async Task<string?> PlanAsync(string projectId, string startedBy, CancellationToken cancellationToken = default)
    {
        var gate = _startLocks.GetOrAdd(projectId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            var project = await store.GetProjectAsync(projectId, cancellationToken);
            if (project is null) return "The board was not found.";
            if (project.Orchestration.Planning)
            {
                _replan[projectId] = true;
                return null;
            }

            var all = await store.ListFeaturesAsync(projectId, cancellationToken);
            var backlog = all.Where(f => f.Status != FeatureStatus.Completed && !f.IsActive).ToList();
            if (backlog.Count == 0) return "There is nothing in the backlog to plan.";

            var agent = project.Agent(AgentRole.Orchestrator);
            var step = new StepState
            {
                AgentId = Ids.New(),
                Name = agent.Name + ": backlog plan",
                Model = FeatureOrchestrator.EffectiveModel(agent, claudeOptions.CurrentValue)
            };
            var input = BuildInput(all, backlog);
            var run = new PipelineRun
            {
                PipelineName = $"{project.Name}: backlog plan",
                ProjectId = project.Id,
                Input = input,
                StartedBy = startedBy,
                Steps = [step]
            };

            await store.UpdateProjectAsync(projectId, p =>
            {
                p.Orchestration.Planning = true;
                p.Orchestration.RunId = run.Id;
                p.Orchestration.Error = null;
            }, cancellationToken);
            features.BoardChanged(projectId);

            runs.Start(run, (r, token) => ExecuteAsync(project, agent, step.AgentId, backlog.Select(f => f.Id).ToHashSet(), r, token));
            return null;
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task ExecuteAsync(Project project, TeamAgent agent, string stepId, HashSet<string> planned, PipelineRun run,
                                    CancellationToken cancellationToken)
    {
        string? error = null;
        try
        {
            run.SetStage("Planning the backlog");
            await GitWorkspace.EnsureRepositoryAsync(project, cancellationToken);

            var permissions = Team.Permissions(AgentRole.Orchestrator, project.Azure.DeployEnabled);
            var chatClient = new ClaudeCodeChatClient(cli, new ClaudeCodeChatClientOptions
            {
                WorkingDirectory = project.WorkspacePath,
                Tools = permissions.Tools,
                AllowedTools = permissions.AllowedTools,
                Model = FeatureOrchestrator.EffectiveModel(agent, claudeOptions.CurrentValue),
                Timeout = TimeSpan.FromMinutes(Math.Max(1, boardOptions.CurrentValue.AgentTimeoutMinutes))
            });
            var orchestratorAgent = new ChatClientAgent(chatClient, new ChatClientAgentOptions
            {
                Id = stepId,
                Name = agent.Name,
                ChatOptions = new ChatOptions { Instructions = FeatureOrchestrator.BuildInstructions(project, agent, permissions) }
            }, loggerFactory);

            var workflow = AgentWorkflowBuilder.BuildSequential("Backlog plan", [orchestratorAgent]);
            await AgentWorkflowExecution.RunAsync(workflow, run.Input, run, [stepId], cancellationToken);

            var plan = ParsePlan(run.StepOutput(stepId));
            await ApplyAsync(project.Id, planned, plan, cancellationToken);
            await store.UpdateProjectAsync(project.Id, p =>
            {
                p.Orchestration.Summary = plan.Summary;
                p.Orchestration.PlannedAt = DateTimeOffset.UtcNow;
            }, CancellationToken.None);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            error = "The backlog plan was cancelled.";
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Backlog plan of board {ProjectId} failed", project.Id);
            error = ex.Message;
            throw;
        }
        finally
        {
            await store.UpdateProjectAsync(project.Id, p =>
            {
                p.Orchestration.Planning = false;
                p.Orchestration.Error = error;
            }, CancellationToken.None);
            features.BoardChanged(project.Id);

            _ = Task.Run(async () =>
            {
                try
                {
                    if (_replan.TryRemove(project.Id, out _))
                    {
                        await PlanAsync(project.Id, "Orchestrator");
                    }
                    else
                    {
                        await features.ScheduleAsync(project.Id);
                    }
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Scheduling board {ProjectId} failed", project.Id);
                }
            });
        }
    }

    private static string BuildInput(IReadOnlyList<Feature> all, IReadOnlyList<Feature> backlog)
    {
        var sb = new StringBuilder("# Backlog to plan\n");
        foreach (var feature in backlog)
        {
            sb.Append($"\n## #{feature.Number}: {feature.Title}\n");
            if (feature.Status is FeatureStatus.Failed or FeatureStatus.Cancelled)
            {
                sb.Append($"(Started before and {feature.Status.ToString().ToLowerInvariant()}; it will be retried.)\n");
            }
            sb.Append('\n').Append(feature.Prompt.Trim()).Append('\n');
        }

        var inProgress = all.Where(f => f.IsActive).ToList();
        if (inProgress.Count > 0)
        {
            sb.Append("\n# In progress now (on their own branches; not merged yet)\n\n");
            foreach (var feature in inProgress) sb.Append($"- #{feature.Number}: {feature.Title}\n");
        }

        var done = all.Where(f => f.Status == FeatureStatus.Completed).ToList();
        if (done.Count > 0)
        {
            sb.Append("\n# Done (merged into main)\n\n");
            foreach (var feature in done) sb.Append($"- #{feature.Number}: {feature.Title}\n");
        }

        sb.Append("\n# Your task\n\nDecide the dependencies of every feature in the backlog, and reply with the explanation and the JSON block.\n");
        return sb.ToString();
    }

    public sealed record BacklogPlan(string? Summary, IReadOnlyDictionary<int, (IReadOnlyList<int> DependsOn, string? Reason)> Features);

    /// <summary>Reads the JSON block of the orchestrator's reply (the last ```json fence, or the last {...} object).</summary>
    public static BacklogPlan ParsePlan(string reply)
    {
        var json = ExtractJson(reply) ?? throw new FeatureFailedException("The orchestrator's reply has no JSON plan.");
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new FeatureFailedException("The orchestrator's JSON plan is not valid: " + ex.Message);
        }

        var result = new Dictionary<int, (IReadOnlyList<int>, string?)>();
        foreach (var item in (root?["features"] as JsonArray ?? []).OfType<JsonObject>())
        {
            if (!TryInt(item["number"], out var number)) continue;
            var dependsOn = (item["dependsOn"] as JsonArray ?? [])
                .Select(n => TryInt(n, out var d) ? d : (int?)null)
                .OfType<int>().Where(d => d != number).Distinct().ToList();
            result[number] = (dependsOn, (string?)item["reason"]);
        }
        return new BacklogPlan((string?)root?["summary"], result);
    }

    /// <summary>Saves the dependencies, leaving out unknown features and any edge that would make a cycle.</summary>
    private async Task ApplyAsync(string projectId, HashSet<string> planned, BacklogPlan plan, CancellationToken cancellationToken)
    {
        var all = await store.ListFeaturesAsync(projectId, cancellationToken);
        var numbers = all.Select(f => f.Number).ToHashSet();
        var graph = all.ToDictionary(f => f.Number, f => planned.Contains(f.Id) ? new List<int>() : f.DependsOn.ToList());

        foreach (var feature in all.Where(f => planned.Contains(f.Id)).OrderBy(f => f.Number))
        {
            if (!plan.Features.TryGetValue(feature.Number, out var entry)) continue;
            foreach (var dependency in entry.DependsOn.Where(numbers.Contains))
            {
                if (!Reaches(graph, dependency, feature.Number)) graph[feature.Number].Add(dependency);
            }
        }

        var now = DateTimeOffset.UtcNow;
        foreach (var feature in all.Where(f => planned.Contains(f.Id)))
        {
            var reason = plan.Features.TryGetValue(feature.Number, out var entry) ? entry.Reason : "Not mentioned in the plan; no dependencies.";
            await store.UpdateFeatureAsync(projectId, feature.Id, f =>
            {
                f.DependsOn = graph[feature.Number];
                f.OrchestratorNote = reason;
                f.PlannedAt = now;
                if (f.Status == FeatureStatus.NotStarted) f.Error = null;
            }, CancellationToken.None);
        }
    }

    /// <summary>True when <paramref name="to"/> can be reached from <paramref name="from"/> following dependencies.</summary>
    private static bool Reaches(Dictionary<int, List<int>> graph, int from, int to)
    {
        var seen = new HashSet<int>();
        var stack = new Stack<int>([from]);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (current == to) return true;
            if (!seen.Add(current) || !graph.TryGetValue(current, out var next)) continue;
            foreach (var n in next) stack.Push(n);
        }
        return false;
    }

    private static string? ExtractJson(string reply)
    {
        var fence = reply.LastIndexOf("```json", StringComparison.OrdinalIgnoreCase);
        if (fence >= 0)
        {
            var start = reply.IndexOf('\n', fence);
            var end = start < 0 ? -1 : reply.IndexOf("```", start, StringComparison.Ordinal);
            if (start >= 0 && end > start) return reply[start..end].Trim();
        }
        var open = reply.IndexOf('{');
        var close = reply.LastIndexOf('}');
        return open >= 0 && close > open ? reply[open..(close + 1)] : null;
    }

    private static bool TryInt(JsonNode? node, out int value)
    {
        value = 0;
        if (node is not JsonValue v) return false;
        if (v.TryGetValue(out int i)) { value = i; return true; }
        if (v.TryGetValue(out string? s)) return int.TryParse(s.TrimStart('#'), out value);
        return false;
    }
}
