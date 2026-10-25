using System.ComponentModel;
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
/// Builds one feature with the agent team, as Microsoft Agent Framework workflows on the signed-in Claude account:
/// Planner, then Database + Backend + Frontend concurrently, then Testing (developers fix what fails, up to
/// Board:MaxTestRounds), then DevOps. All agents work in the project's workspace folder, each writing only its own part.
/// </summary>
public sealed partial class FeatureOrchestrator(
    ClaudeCli cli,
    IOptionsMonitor<BoardOptions> boardOptions,
    IOptionsMonitor<ClaudeOptions> claudeOptions,
    ILoggerFactory loggerFactory,
    ILogger<FeatureOrchestrator> logger)
{
    /// <summary>Changes the feature and saves it (and refreshes the board).</summary>
    public delegate Task FeatureUpdater(Action<Feature> change);

    /// <summary>The child tasks a feature starts with: one per role, in the order they run.</summary>
    public static List<FeatureTask> InitialTasks(Project project) =>
    [
        .. new[] { AgentRole.Planner, AgentRole.Database, AgentRole.Backend, AgentRole.Frontend, AgentRole.Testing, AgentRole.DevOps }
            .Select(role => NewTask(project, role, 1))
    ];

    public static StepState ToStep(FeatureTask task, Project project, ClaudeOptions options) => new()
    {
        AgentId = task.StepId,
        Name = task.Title,
        Model = EffectiveModel(project.Agent(task.Role), options)
    };

    public async Task RunAsync(Project project, Feature feature, PipelineRun run, FeatureUpdater update, CancellationToken cancellationToken)
    {
        var options = boardOptions.CurrentValue;
        var workspace = project.WorkspacePath;
        await PrepareWorkspaceAsync(project, options, run, cancellationToken);

        var tasks = feature.Tasks;
        FeatureTask First(AgentRole role) => tasks.First(t => t.Role == role && t.Round == 1);
        var brief = Brief(feature);

        // 1. Plan.
        var planner = First(AgentRole.Planner);
        await RunStageAsync(project, run, update, FeatureStage.Planning, "Planning", [planner], brief +
            Section("Your task", "Write the technical plan for this feature."), cancellationToken);
        var plan = run.StepOutput(planner.StepId).Trim();

        var planPath = $"docs/features/{feature.Number:0000}-{Slug(feature.Title)}.md";
        await File.WriteAllTextAsync(Path.Combine(workspace, planPath),
            $"# Feature #{feature.Number}: {feature.Title}\n\n## Request\n\n{feature.Prompt.Trim()}\n\n## Technical plan\n\n{plan}\n",
            cancellationToken);
        await update(f => f.PlanPath = planPath);

        var context = brief + Section("Technical plan", plan);

        // 2. Develop: database, backend and frontend at the same time.
        var developers = Team.Developers.Select(First).ToList();
        await RunStageAsync(project, run, update, FeatureStage.Development, "Development", developers, context +
            Section("Your task", "Implement your part of the technical plan in your folder of the workspace."), cancellationToken);
        var reports = developers.ToDictionary(t => t.Role, t => run.StepOutput(t.StepId).Trim());

        // 3. Test, and let the developers fix what fails.
        var maxRounds = Math.Max(1, options.MaxTestRounds);
        var testReport = string.Empty;
        for (var round = 1; ; round++)
        {
            var tester = round == 1 ? First(AgentRole.Testing) : await AddTaskAsync(project, run, update, AgentRole.Testing, round);
            var testInput = context + Section("Developer reports", DeveloperReports(reports)) + Section("Your task", round == 1
                ? "Test the feature."
                : $"This is test round {round}: the developers fixed the problems in your previous report (below). " +
                  "Run all tests again, and update the tests if the plan says they were wrong.\n\n" + testReport);
            await RunStageAsync(project, run, update, FeatureStage.Testing, round == 1 ? "Testing" : $"Testing (round {round})",
                [tester], testInput, cancellationToken);

            testReport = run.StepOutput(tester.StepId).Trim();
            var verdict = ParseVerdict(testReport);
            if (verdict.Passed) break;

            if (round >= maxRounds)
            {
                throw new FeatureFailedException(
                    $"Tests still fail after {round} round{(round == 1 ? "" : "s")}. See the tester's report for the details.");
            }

            var fixers = new List<FeatureTask>();
            foreach (var role in verdict.Fix) fixers.Add(await AddTaskAsync(project, run, update, role, round + 1));
            await RunStageAsync(project, run, update, FeatureStage.Development, $"Fixing (round {round + 1})", fixers, context +
                Section("Test report", testReport) +
                Section("Your task", "The tester found the problems above. Fix the ones in your area, then build again. " +
                                     "If a test is wrong rather than your code, explain why in your reply."),
                cancellationToken);
            foreach (var fixer in fixers)
            {
                reports[fixer.Role] = reports.GetValueOrDefault(fixer.Role) + $"\n\nFixes in round {round + 1}:\n" +
                                      run.StepOutput(fixer.StepId).Trim();
            }
        }

        // 4. Infrastructure, pipeline and (when turned on) deployment.
        var devops = First(AgentRole.DevOps);
        await RunStageAsync(project, run, update, FeatureStage.Deploying,
            project.Azure.DeployEnabled ? "Deploying to Azure" : "Infrastructure and pipeline", [devops],
            context + Section("Developer reports", DeveloperReports(reports)) + Section("Test report", testReport) +
            Section("Your task", project.Azure.DeployEnabled
                ? "Update the infrastructure and pipeline for this feature, then deploy it to Azure."
                : "Update the infrastructure and pipeline for this feature. Deployment is turned off: do not deploy."),
            cancellationToken);

        if (project.Azure.DeployEnabled && DeployedUrl(run.StepOutput(devops.StepId)) is { } url)
        {
            await update(f => f.DeploymentUrl = url);
        }

        if (options.CommitToGit)
        {
            run.SetStage("Committing to git");
            await CommitAsync(workspace, $"Feature #{feature.Number}: {feature.Title}", run, devops.StepId, cancellationToken);
        }
    }

    /// <summary>Runs one or more agents as a workflow (concurrent when there are several) and keeps the tasks in step.</summary>
    private async Task RunStageAsync(Project project, PipelineRun run, FeatureUpdater update, FeatureStage stage, string label,
                                     IReadOnlyList<FeatureTask> tasks, string input, CancellationToken cancellationToken)
    {
        var ids = tasks.Select(t => t.StepId).ToHashSet();
        run.SetStage(label);
        await update(f =>
        {
            f.Stage = stage;
            foreach (var task in f.Tasks.Where(t => ids.Contains(t.StepId))) task.State = TaskState.Doing;
        });

        var agents = tasks.Select(task => CreateAgent(project, task)).ToList();
        var workflow = agents.Count == 1
            ? AgentWorkflowBuilder.BuildSequential(label, agents)
            : AgentWorkflowBuilder.BuildConcurrent(label, agents);

        try
        {
            await AgentWorkflowExecution.RunAsync(workflow, input, run, ids, cancellationToken);
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

    private async Task<FeatureTask> AddTaskAsync(Project project, PipelineRun run, FeatureUpdater update, AgentRole role, int round)
    {
        var task = NewTask(project, role, round);
        run.AddStep(ToStep(task, project, claudeOptions.CurrentValue));
        await update(f => f.Tasks.Add(task));
        return task;
    }

    private static FeatureTask NewTask(Project project, AgentRole role, int round)
    {
        var name = project.Agent(role).Name;
        var title = round == 1
            ? name
            : role == AgentRole.Testing ? $"{name}: retest (round {round})" : $"{name}: fix (round {round})";
        return new FeatureTask { Role = role, Round = round, Title = title };
    }

    private AIAgent CreateAgent(Project project, FeatureTask task)
    {
        var agent = project.Agent(task.Role);
        var permissions = Team.Permissions(task.Role, project.Azure.DeployEnabled);
        var chatClient = new ClaudeCodeChatClient(cli, new ClaudeCodeChatClientOptions
        {
            WorkingDirectory = project.WorkspacePath,
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

    private static string EffectiveModel(TeamAgent agent, ClaudeOptions options) =>
        !string.IsNullOrWhiteSpace(agent.Model) ? agent.Model.Trim() : options.DefaultModel.Trim();

    private static string BuildInstructions(Project project, TeamAgent agent, RolePermissions permissions)
    {
        var sb = new StringBuilder()
            .Append($"You are \"{agent.Name}\", the {Team.Label(agent.Role)} agent on an AI development team that builds features ")
            .Append($"for the project \"{project.Name}\". ")
            .Append("The team works in this order: Planner, then Database, Backend and Frontend in parallel, then Testing, then DevOps.\n\n");
        if (!string.IsNullOrWhiteSpace(project.Description))
        {
            sb.Append("# The project\n\n").Append(project.Description.Trim()).Append("\n\n");
        }
        sb.Append("# The workspace\n\n")
          .Append("Your current folder is the project workspace, shared by the whole team:\n")
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

    private async Task PrepareWorkspaceAsync(Project project, BoardOptions options, PipelineRun run, CancellationToken cancellationToken)
    {
        var workspace = project.WorkspacePath;
        Directory.CreateDirectory(Path.Combine(workspace, "docs", "features"));

        var readme = Path.Combine(workspace, "README.md");
        if (!File.Exists(readme))
        {
            await File.WriteAllTextAsync(readme,
                $"# {project.Name}\n\n{project.Description}\n\nBuilt feature by feature by the Claude agent team.\n\n## Layout\n\n{Team.Layout}",
                cancellationToken);
        }

        var gitignore = Path.Combine(workspace, ".gitignore");
        if (!File.Exists(gitignore))
        {
            await File.WriteAllTextAsync(gitignore,
                "bin/\nobj/\nnode_modules/\ndist/\ncoverage/\n*.user\n.vs/\n.env\n.env.*\n*.zip\npublish/\nTestResults/\n",
                cancellationToken);
        }

        if (options.CommitToGit && !Directory.Exists(Path.Combine(workspace, ".git")))
        {
            await GitAsync(workspace, ["init", "--quiet"], run, null, cancellationToken);
        }
    }

    private async Task CommitAsync(string workspace, string message, PipelineRun run, string stepId, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(Path.Combine(workspace, ".git"))) return;
        if (await GitAsync(workspace, ["add", "--all"], run, stepId, cancellationToken) is not { ExitCode: 0 }) return;

        // Use the user's git identity when there is one.
        var identity = await GitAsync(workspace, ["config", "user.email"], run, null, cancellationToken);
        string[] author = identity is { ExitCode: 0 } ? [] : ["-c", "user.name=Claude Agent Board", "-c", "user.email=agents@claude-agent-board.local"];

        var commit = await GitAsync(workspace, [.. author, "commit", "--quiet", "-m", message], run, stepId, cancellationToken);
        if (commit is { ExitCode: 0 }) run.StepActivity(stepId, "git commit: " + message);
    }

    private async Task<(int ExitCode, string Output, string Error)?> GitAsync(string workspace, string[] args, PipelineRun run,
                                                                              string? stepId, CancellationToken cancellationToken)
    {
        try
        {
            var result = await CliProcess.RunAsync("git", args, workspace, cancellationToken);
            if (result.ExitCode != 0 && stepId is not null && result.Error.Trim().Length > 0)
            {
                run.StepActivity(stepId, $"git {args[0]}: {result.Error.Trim()}");
            }
            return result;
        }
        catch (Win32Exception ex)
        {
            logger.LogWarning(ex, "git is not available; the feature is not committed");
            return null;
        }
    }

    private static string Slug(string title)
    {
        var slug = NonSlugRegex().Replace(title.ToLowerInvariant(), "-").Trim('-');
        if (slug.Length > 50) slug = slug[..50].TrimEnd('-');
        return slug.Length > 0 ? slug : "feature";
    }

    [GeneratedRegex(@"^\W*VERDICT\W*:?\W*(PASS|FAIL)", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex VerdictRegex();

    [GeneratedRegex(@"^\W*FIX\W*:\s*(.*)$", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex FixRegex();

    [GeneratedRegex(@"^\W*DEPLOYED_URL\W*:\s*(https?://[^\s`*]+)", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex DeployedUrlRegex();

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NonSlugRegex();
}

public sealed class FeatureFailedException(string message) : Exception(message);
