using System.Text;
using System.Text.RegularExpressions;
using ClaudeAgentPipelines.Claude;
using ClaudeAgentPipelines.Pipelines;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace ClaudeAgentPipelines.Board;

/// <summary>
/// Builds one feature with the agent team, like a developer team works on a feature branch, as Microsoft Agent
/// Framework workflows on the signed-in Claude account:
/// <list type="number">
/// <item>Planner writes the technical plan and API contract.</item>
/// <item>Database, Backend and Frontend develop concurrently; the frontend against the contract, with mocks.</item>
/// <item>Integration: Backend and Frontend talk in a group chat until the UI runs on the real API.</item>
/// <item>Tester tests; developers fix what fails and the tester retests (up to Board:MaxTestRounds).</item>
/// <item>The branch is merged into main (one feature at a time); a release engineer resolves conflicts.</item>
/// <item>DevOps updates the infrastructure (and deploys) from main.</item>
/// </list>
/// Steps 1-4 happen in the feature's own git worktree, so several features can be built at the same time.
/// </summary>
public sealed partial class FeatureOrchestrator(
    ClaudeCli cli,
    IOptionsMonitor<BoardOptions> boardOptions,
    IOptionsMonitor<ClaudeOptions> claudeOptions,
    ILoggerFactory loggerFactory)
{
    private const string IntegrationDone = "INTEGRATION: DONE";

    /// <summary>Changes the feature and saves it (and refreshes the board).</summary>
    public delegate Task FeatureUpdater(Action<Feature> change);

    /// <summary>Waits for exclusive use of the main workspace (merging, deploying); dispose to release it.</summary>
    public delegate Task<IDisposable> MainWorkspaceLock(CancellationToken cancellationToken);

    /// <summary>The child tasks a feature starts with, in the order they run.</summary>
    public static List<FeatureTask> InitialTasks(Project project) =>
    [
        NewTask(project, AgentRole.Planner, TaskKind.Plan, 1),
        NewTask(project, AgentRole.Database, TaskKind.Develop, 1),
        NewTask(project, AgentRole.Backend, TaskKind.Develop, 1),
        NewTask(project, AgentRole.Frontend, TaskKind.Develop, 1),
        NewTask(project, AgentRole.Backend, TaskKind.Integrate, 1),
        NewTask(project, AgentRole.Frontend, TaskKind.Integrate, 1),
        NewTask(project, AgentRole.Testing, TaskKind.Test, 1),
        NewTask(project, AgentRole.DevOps, TaskKind.Deploy, 1)
    ];

    public static StepState ToStep(FeatureTask task, Project project, ClaudeOptions options) => new()
    {
        AgentId = task.StepId,
        Name = task.Title,
        Model = EffectiveModel(project.Agent(task.Role), options)
    };

    /// <param name="fresh">Start the feature branch over from main (first run, or running a finished feature again).</param>
    public async Task RunAsync(Project project, Feature feature, PipelineRun run, FeatureUpdater update, MainWorkspaceLock mainLock,
                               bool fresh, CancellationToken cancellationToken)
    {
        var options = boardOptions.CurrentValue;
        var main = project.WorkspacePath;
        var tasks = feature.Tasks;
        FeatureTask First(TaskKind kind, AgentRole role) => tasks.First(t => t.Kind == kind && t.Role == role);
        void Log(string text) => run.StepActivity(First(TaskKind.Plan, AgentRole.Planner).StepId, text);

        // 0. A branch and worktree of its own. Git changes to the repository itself are made one feature at a time.
        run.SetStage("Creating the feature branch");
        string workspace, branch;
        using (await mainLock(cancellationToken))
        {
            await GitWorkspace.EnsureRepositoryAsync(project, cancellationToken);
            (workspace, branch) = await GitWorkspace.PrepareWorktreeAsync(project, feature, fresh, Log, cancellationToken);
        }
        await update(f =>
        {
            f.Branch = branch;
            f.WorktreePath = workspace;
        });

        var brief = Brief(feature);

        // 1. Plan.
        var planner = First(TaskKind.Plan, AgentRole.Planner);
        await RunStageAsync(project, workspace, run, update, FeatureStage.Planning, "Planning", [planner], brief +
            Section("Your task", "Write the technical plan for this feature."), cancellationToken);
        var plan = run.StepOutput(planner.StepId).Trim();

        var planPath = $"docs/features/{feature.Number:0000}-{GitWorkspace.Slug(feature.Title)}.md";
        Directory.CreateDirectory(Path.Combine(workspace, "docs", "features"));
        await File.WriteAllTextAsync(Path.Combine(workspace, planPath),
            $"# Feature #{feature.Number}: {feature.Title}\n\n## Request\n\n{feature.Prompt.Trim()}\n\n## Technical plan\n\n{plan}\n",
            cancellationToken);
        await update(f => f.PlanPath = planPath);

        var context = brief + Section("Technical plan", plan);

        // 2. Develop: database, backend and frontend at the same time, against the plan's contract.
        var developers = Team.Developers.Select(role => First(TaskKind.Develop, role)).ToList();
        await RunStageAsync(project, workspace, run, update, FeatureStage.Development, "Development", developers, context +
            Section("Your task",
                "Implement your part of the technical plan in your folder of the workspace. The other developers work at the " +
                "same time, so stick to the API contract; Backend and Frontend integrate in the next stage."),
            cancellationToken);
        var reports = developers.ToDictionary(t => t.Role, t => run.StepOutput(t.StepId).Trim());

        // 3. Integrate: backend and frontend talk until the UI runs on the real API.
        var integration = new[] { First(TaskKind.Integrate, AgentRole.Backend), First(TaskKind.Integrate, AgentRole.Frontend) };
        await RunConversationAsync(project, workspace, run, update, FeatureStage.Integration, "Integration", integration,
            context + Section("Developer reports", DeveloperReports(reports)) + Section("Your task", $"""
                Integration. Backend and Frontend were built at the same time against the API contract; now connect them.
                You are talking with each other: the Backend developer speaks first, then you take turns.
                - Backend developer: describe the API exactly as implemented (routes, request and response JSON, status
                  codes, validation, CORS, port) and every difference from the contract. Fix what the Frontend developer
                  reports on your side.
                - Frontend developer: switch the app from the mocks onto the real API (keep the mocks only for tests and
                  VITE_USE_MOCKS), align the types and calls with what the backend really does, check the dev proxy, and
                  build. Report anything the backend must change.
                Each of you only changes your own folder. When your side is integrated and builds, end your message with
                the line `{IntegrationDone}`; otherwise end with what you need from the other developer.
                """), cancellationToken);
        foreach (var task in integration)
        {
            reports[task.Role] = reports.GetValueOrDefault(task.Role) + "\n\nIntegration:\n" + run.StepOutput(task.StepId).Trim();
        }

        // 4. Test, and let the developers fix what fails.
        var maxRounds = Math.Max(1, options.MaxTestRounds);
        var testReport = string.Empty;
        for (var round = 1; ; round++)
        {
            var tester = round == 1
                ? First(TaskKind.Test, AgentRole.Testing)
                : await AddTaskAsync(project, run, update, AgentRole.Testing, TaskKind.Retest, round);
            var testInput = context + Section("Developer reports", DeveloperReports(reports)) + Section("Your task", round == 1
                ? "Test the feature."
                : $"This is test round {round}: the developers fixed the problems in your previous report (below). " +
                  "Run all tests again, and update the tests if the plan says they were wrong.\n\n" + testReport);
            await RunStageAsync(project, workspace, run, update, FeatureStage.Testing,
                round == 1 ? "Testing" : $"Testing (round {round})", [tester], testInput, cancellationToken);

            testReport = run.StepOutput(tester.StepId).Trim();
            var verdict = ParseVerdict(testReport);
            if (verdict.Passed) break;

            if (round >= maxRounds)
            {
                throw new FeatureFailedException(
                    $"Tests still fail after {round} round{(round == 1 ? "" : "s")}. See the tester's report for the details.");
            }

            var fixers = new List<FeatureTask>();
            foreach (var role in verdict.Fix) fixers.Add(await AddTaskAsync(project, run, update, role, TaskKind.Fix, round + 1));
            await RunStageAsync(project, workspace, run, update, FeatureStage.Development, $"Fixing (round {round + 1})", fixers,
                context + Section("Test report", testReport) +
                Section("Your task", "The tester found the problems above. Fix the ones in your area, then build again. " +
                                     "If a test is wrong rather than your code, explain why in your reply."),
                cancellationToken);
            foreach (var fixer in fixers)
            {
                reports[fixer.Role] = reports.GetValueOrDefault(fixer.Role) + $"\n\nFixes in round {round + 1}:\n" +
                                      run.StepOutput(fixer.StepId).Trim();
            }
        }

        await GitWorkspace.CommitAsync(workspace, $"Feature #{feature.Number}: {feature.Title}", cancellationToken);

        // 5. Merge into main, one feature at a time, like a pull request.
        run.SetStage("Waiting to merge into main");
        await update(f => f.Stage = FeatureStage.Merging);
        using (await mainLock(cancellationToken))
        {
            run.SetStage("Merging into main");
            var conflicts = await GitWorkspace.MergeAsync(main, branch, $"Merge feature #{feature.Number}: {feature.Title}", cancellationToken);
            if (conflicts.Count > 0) await ResolveConflictsAsync(project, run, update, context, branch, conflicts, cancellationToken);
            Log($"git: merged {branch} into main");

            // 6. Infrastructure, pipeline and (when turned on) deployment, from main.
            var devops = First(TaskKind.Deploy, AgentRole.DevOps);
            await RunStageAsync(project, main, run, update, FeatureStage.Deploying,
                project.Azure.DeployEnabled ? "Deploying to Azure" : "Infrastructure and pipeline", [devops],
                context + Section("Developer reports", DeveloperReports(reports)) + Section("Test report", testReport) +
                Section("Your task", project.Azure.DeployEnabled
                    ? "The feature is merged into main. Update the infrastructure and pipeline for it, then deploy it to Azure."
                    : "The feature is merged into main. Update the infrastructure and pipeline for it. Deployment is turned off: do not deploy."),
                cancellationToken);

            if (project.Azure.DeployEnabled && DeployedUrl(run.StepOutput(devops.StepId)) is { } url)
            {
                await update(f => f.DeploymentUrl = url);
            }
            if (await GitWorkspace.CommitAsync(main, $"Feature #{feature.Number}: infrastructure", cancellationToken))
            {
                run.StepActivity(devops.StepId, "git commit: infrastructure");
            }

            await GitWorkspace.RemoveWorktreeAsync(main, workspace, branch, CancellationToken.None);
        }
        await update(f =>
        {
            f.Branch = null;
            f.WorktreePath = null;
        });
    }

    /// <summary>The release engineer resolves the conflicts of a merge in progress; the merge is concluded or aborted.</summary>
    private async Task ResolveConflictsAsync(Project project, PipelineRun run, FeatureUpdater update, string context, string branch,
                                             IReadOnlyList<string> conflicts, CancellationToken cancellationToken)
    {
        var main = project.WorkspacePath;
        var integrator = await AddTaskAsync(project, run, update, AgentRole.Integrator, TaskKind.Merge, 1);
        try
        {
            await RunStageAsync(project, main, run, update, FeatureStage.Merging, "Resolving merge conflicts", [integrator],
                context + Section("Your task",
                    $"Merging `{branch}` into main stopped with conflicts in these files:\n" +
                    string.Join("\n", conflicts.Select(c => "- " + c)) +
                    "\n\nResolve them so that main's features and this feature both work, build, run the tests, and stage the files."),
                cancellationToken);

            var unresolved = (await GitWorkspace.ConflictedFilesAsync(main, cancellationToken))
                .Concat(await GitWorkspace.FilesWithMarkersAsync(main, cancellationToken))
                .Distinct().ToList();
            if (unresolved.Count > 0)
            {
                throw new FeatureFailedException("Merge conflicts are left in: " + string.Join(", ", unresolved) +
                                                 ". The merge was undone; the feature's branch is kept for a retry.");
            }
            await GitWorkspace.CompleteMergeAsync(main, cancellationToken);
        }
        catch
        {
            await GitWorkspace.AbortMergeAsync(main, CancellationToken.None);
            throw;
        }
    }

    /// <summary>Runs one or more agents as a workflow (concurrent when there are several) and keeps the tasks in step.</summary>
    private Task RunStageAsync(Project project, string workspace, PipelineRun run, FeatureUpdater update, FeatureStage stage,
                               string label, IReadOnlyList<FeatureTask> tasks, string input, CancellationToken cancellationToken)
    {
        var agents = tasks.Select(task => CreateAgent(project, task, workspace)).ToList();
        var workflow = agents.Count == 1
            ? AgentWorkflowBuilder.BuildSequential(label, agents)
            : AgentWorkflowBuilder.BuildConcurrent(label, agents);
        return ExecuteAsync(run, update, stage, label, tasks, workflow, input, conversation: false, cancellationToken);
    }

    /// <summary>
    /// Runs agents as a round-robin group chat: they take turns, each seeing the conversation so far, until all of them
    /// end a message with <see cref="IntegrationDone"/> or the turns run out.
    /// </summary>
    private Task RunConversationAsync(Project project, string workspace, PipelineRun run, FeatureUpdater update, FeatureStage stage,
                                      string label, IReadOnlyList<FeatureTask> tasks, string input, CancellationToken cancellationToken)
    {
        var agents = tasks.Select(task => CreateAgent(project, task, workspace)).ToList();
        var names = agents.Select(a => a.Name).ToHashSet();
        var maxTurns = Math.Max(2, boardOptions.CurrentValue.MaxIntegrationTurns);

        var workflow = AgentWorkflowBuilder
            .CreateGroupChatBuilderWith(participants => new RoundRobinGroupChatManager(participants, (_, history, _) =>
                ValueTask.FromResult(AllDone(history, names)))
            {
                MaximumIterationCount = maxTurns
            })
            .AddParticipants(agents)
            .Build();
        return ExecuteAsync(run, update, stage, label, tasks, workflow, input, conversation: true, cancellationToken);
    }

    /// <summary>True when every participant's latest message ends the conversation.</summary>
    private static bool AllDone(IEnumerable<ChatMessage> history, HashSet<string?> names)
    {
        var latest = new Dictionary<string, string>();
        foreach (var message in history.Where(m => m.Role == ChatRole.Assistant && m.AuthorName is not null && names.Contains(m.AuthorName)))
        {
            latest[message.AuthorName!] = message.Text;
        }
        return latest.Count == names.Count && latest.Values.All(text => text.Contains(IntegrationDone, StringComparison.OrdinalIgnoreCase));
    }

    private static async Task ExecuteAsync(PipelineRun run, FeatureUpdater update, FeatureStage stage, string label,
                                           IReadOnlyList<FeatureTask> tasks, Workflow workflow, string input, bool conversation,
                                           CancellationToken cancellationToken)
    {
        var ids = tasks.Select(t => t.StepId).ToHashSet();
        run.SetStage(label);
        await update(f =>
        {
            f.Stage = stage;
            foreach (var task in f.Tasks.Where(t => ids.Contains(t.StepId))) task.State = TaskState.Doing;
        });

        try
        {
            await AgentWorkflowExecution.RunAsync(workflow, input, run, ids, cancellationToken, conversation);
        }
        finally
        {
            var states = tasks.ToDictionary(t => t.StepId, t => run.StepStatusOf(t.StepId) switch
            {
                StepStatus.Completed => TaskState.Done,
                StepStatus.Failed => TaskState.Failed,
                _ when cancellationToken.IsCancellationRequested => TaskState.Cancelled,
                StepStatus.Running => TaskState.Failed,
                _ => TaskState.ToDo
            });
            await update(f =>
            {
                foreach (var task in f.Tasks.Where(t => states.ContainsKey(t.StepId))) task.State = states[task.StepId];
            });
        }
    }

    private async Task<FeatureTask> AddTaskAsync(Project project, PipelineRun run, FeatureUpdater update, AgentRole role,
                                                 TaskKind kind, int round)
    {
        var task = NewTask(project, role, kind, round);
        run.AddStep(ToStep(task, project, claudeOptions.CurrentValue));
        await update(f => f.Tasks.Add(task));
        return task;
    }

    private static FeatureTask NewTask(Project project, AgentRole role, TaskKind kind, int round)
    {
        var name = project.Agent(role).Name;
        var title = kind switch
        {
            TaskKind.Integrate => $"{name}: integration",
            TaskKind.Fix => $"{name}: fix (round {round})",
            TaskKind.Retest => $"{name}: retest (round {round})",
            TaskKind.Merge => $"{name}: resolve merge conflicts",
            _ => name
        };
        return new FeatureTask { Role = role, Kind = kind, Round = round, Title = title };
    }

    private AIAgent CreateAgent(Project project, FeatureTask task, string workspace)
    {
        var agent = project.Agent(task.Role);
        var permissions = Team.Permissions(task.Role, project.Azure.DeployEnabled);
        var chatClient = new ClaudeCodeChatClient(cli, new ClaudeCodeChatClientOptions
        {
            WorkingDirectory = workspace,
            Tools = permissions.Tools,
            AllowedTools = permissions.AllowedTools,
            Model = EffectiveModel(agent, claudeOptions.CurrentValue),
            Timeout = TimeSpan.FromMinutes(Math.Max(1, boardOptions.CurrentValue.AgentTimeoutMinutes))
        });

        return new ChatClientAgent(chatClient, new ChatClientAgentOptions
        {
            Id = task.StepId,
            Name = agent.Name,
            ChatOptions = new ChatOptions { Instructions = BuildInstructions(project, agent, permissions) }
        }, loggerFactory);
    }

    public static string EffectiveModel(TeamAgent agent, ClaudeOptions options) =>
        !string.IsNullOrWhiteSpace(agent.Model) ? agent.Model.Trim() : options.DefaultModel.Trim();

    public static string BuildInstructions(Project project, TeamAgent agent, RolePermissions permissions)
    {
        var sb = new StringBuilder()
            .Append($"You are \"{agent.Name}\", the {Team.Label(agent.Role)} agent on an AI development team that builds features ")
            .Append($"for the project \"{project.Name}\". ")
            .Append("Each feature is built on its own git branch: Planner, then Database, Backend and Frontend in parallel, then ")
            .Append("Backend and Frontend integrate, then Testing; then the branch is merged into main and DevOps updates the infrastructure. ")
            .Append("Several features may be in progress at the same time on other branches.\n\n");
        if (!string.IsNullOrWhiteSpace(project.Description))
        {
            sb.Append("# The project\n\n").Append(project.Description.Trim()).Append("\n\n");
        }
        sb.Append("# The workspace\n\n")
          .Append("Your current folder is the workspace:\n")
          .Append(Team.Layout).Append('\n')
          .Append("Read the existing code before you change anything, and follow its conventions. ")
          .Append("Other agents may be writing their own folders at the same time as you.\n\n")
          .Append("# What you may do\n\n")
          .Append(permissions.Summary)
          .Append(" Anything else is refused automatically, so don't try it. Use relative paths from the workspace ")
          .Append("(to delete a file in your folder: `rm <folder>/<path>`). ")
          .Append("This computer runs Windows; the Bash tool is Git Bash. ")
          .Append("Nobody can answer questions while you work: decide sensibly, keep going until your part is done, ")
          .Append("and mention your assumptions in your reply.\n\n");

        if (agent.Role == AgentRole.DevOps) sb.Append(AzureSection(project.Azure));

        sb.Append("# Your instructions\n\n").Append(agent.Instructions.Trim());
        return sb.ToString();
    }

    private static string AzureSection(ProjectAzureSettings azure)
    {
        var sb = new StringBuilder("# Azure\n\n");
        sb.Append($"- Subscription: {(azure.SubscriptionId.Length > 0 ? $"{azure.SubscriptionName} ({azure.SubscriptionId})" : "not chosen")}\n")
          .Append($"- Resource group: {(azure.ResourceGroup.Length > 0 ? azure.ResourceGroup : "not chosen")}\n")
          .Append($"- Location: {azure.Location}\n");
        if (azure.DeployEnabled)
        {
            sb.Append("\nDeployment is turned on. The Azure CLI on this computer is signed in. After writing and validating the files, ")
              .Append("deploy by running exactly:\n")
              .Append($"`powershell -NoProfile -ExecutionPolicy Bypass -File infra/deploy.ps1 -SubscriptionId {azure.SubscriptionId} ")
              .Append($"-ResourceGroup {azure.ResourceGroup} -Location {azure.Location}`\n")
              .Append("Only create or change resources in that resource group. Never delete resource groups.\n\n");
        }
        else
        {
            sb.Append("\nDeployment is turned off for this project: write and validate the files, but do not deploy.\n\n");
        }
        return sb.ToString();
    }

    private static string Brief(Feature feature) =>
        $"# Feature #{feature.Number}: {feature.Title}\n\n{feature.Prompt.Trim()}\n";

    private static string Section(string title, string body) => $"\n# {title}\n\n{body.Trim()}\n";

    private static string DeveloperReports(Dictionary<AgentRole, string> reports) =>
        string.Join("\n\n", reports.Select(r => $"## {Team.Label(r.Key)}\n\n{r.Value}"));

    /// <summary>
    /// Reads the "VERDICT:" and "FIX:" lines the tester ends with. No verdict counts as a failure, and a failure that
    /// names no area sends every developer back.
    /// </summary>
    public static (bool Passed, IReadOnlyList<AgentRole> Fix) ParseVerdict(string report)
    {
        var verdict = VerdictRegex().Matches(report).LastOrDefault();
        if (verdict is not null && verdict.Groups[1].Value.Equals("PASS", StringComparison.OrdinalIgnoreCase)) return (true, []);

        var fix = new List<AgentRole>();
        if (FixRegex().Matches(report).LastOrDefault() is { } line)
        {
            foreach (var role in Team.Developers)
            {
                if (line.Groups[1].Value.Contains(role.ToString(), StringComparison.OrdinalIgnoreCase)) fix.Add(role);
            }
        }
        return (false, fix.Count > 0 ? fix : Team.Developers);
    }

    /// <summary>The URL on the DevOps agent's last "DEPLOYED_URL:" line, if it is a real web address.</summary>
    private static string? DeployedUrl(string reply)
    {
        var match = DeployedUrlRegex().Matches(reply).LastOrDefault();
        var text = match?.Groups[1].Value.TrimEnd('.', ',', ')', '`', '*');
        return Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http" && uri.Host.Contains('.')
            ? uri.ToString()
            : null;
    }

    [GeneratedRegex(@"^\W*VERDICT\W*:?\W*(PASS|FAIL)", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex VerdictRegex();

    [GeneratedRegex(@"^\W*FIX\W*:\s*(.*)$", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex FixRegex();

    [GeneratedRegex(@"^\W*DEPLOYED_URL\W*:\s*(https?://[^\s`*]+)", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex DeployedUrlRegex();
}

public sealed class FeatureFailedException(string message) : Exception(message);
