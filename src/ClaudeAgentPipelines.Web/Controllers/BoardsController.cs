using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using ClaudeAgentPipelines.Board;
using ClaudeAgentPipelines.Models;
using ClaudeAgentPipelines.Pipelines;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace ClaudeAgentPipelines.Controllers;

/// <summary>Feature boards: the Kanban board, its settings and its agent team.</summary>
public sealed partial class BoardsController(
    BoardStore store,
    BoardNotifier notifier,
    RunManager runs,
    FeatureManager features,
    BoardOrchestrator orchestrator,
    PipelineStore pipelines,
    IWebHostEnvironment environment,
    IOptionsMonitor<BoardOptions> boardOptions,
    IOptionsMonitor<ClaudeOptions> claudeOptions) : Controller
{
    private static readonly string[] ModelOptions = ["opus", "sonnet", "haiku", "fable"];

    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        var summaries = new List<BoardSummary>();
        foreach (var project in await store.ListProjectsAsync(cancellationToken))
        {
            var features = await store.ListFeaturesAsync(project.Id, cancellationToken);
            summaries.Add(new BoardSummary(
                project,
                features.GroupBy(f => f.Stage).ToDictionary(g => g.Key, g => g.Count()),
                features.Count(f => f.IsActive)));
        }
        return View(summaries);
    }

    [HttpGet]
    public IActionResult Create() => View(new BoardForm());

    [HttpPost]
    public async Task<IActionResult> Create(BoardForm form, CancellationToken cancellationToken)
    {
        var workspace = string.IsNullOrWhiteSpace(form.WorkspacePath)
            ? NewWorkspaceFolder(form.Name)
            : form.WorkspacePath.Trim();
        if (ValidateWorkspace(workspace) is { } error) ModelState.AddModelError(nameof(form.WorkspacePath), error);
        if (!ModelState.IsValid) return View(form);

        var project = new Project
        {
            Name = form.Name.Trim(),
            Description = form.Description?.Trim() ?? string.Empty,
            WorkspacePath = Path.GetFullPath(workspace),
            Team = Team.CreateDefaultTeam()
        };
        await store.CreateProjectAsync(project, cancellationToken);
        return RedirectToAction(nameof(Board), new { id = project.Id });
    }

    public async Task<IActionResult> Board(string id, CancellationToken cancellationToken)
    {
        var project = await store.GetProjectAsync(id, cancellationToken);
        if (project is null) return NotFound();

        var version = notifier.Version(project.Id);
        return View(new BoardViewModel(project, await CardsAsync(project.Id, cancellationToken), version,
            boardOptions.CurrentValue.MaxParallelFeaturesLimit));
    }

    /// <summary>Server-sent events: the board's cards, each time a feature on it changes.</summary>
    [HttpGet]
    public async Task<IResult> Events(string id, long version, CancellationToken cancellationToken)
    {
        var project = await store.GetProjectAsync(id, cancellationToken);
        if (project is null) return Results.NotFound();
        return TypedResults.ServerSentEvents(StreamBoard(project.Id, version, cancellationToken));
    }

    /// <summary>Turns autopilot on or off and sets how many features are built in parallel.</summary>
    [HttpPost]
    public async Task<IActionResult> Autopilot(string id, bool enabled, int maxParallel, CancellationToken cancellationToken)
    {
        var limit = Math.Max(1, boardOptions.CurrentValue.MaxParallelFeaturesLimit);
        var project = await store.UpdateProjectAsync(id, p =>
        {
            p.Autopilot = enabled;
            if (maxParallel > 0) p.MaxParallelFeatures = Math.Clamp(maxParallel, 1, limit);
        }, cancellationToken);
        if (project is null) return NotFound();
        features.BoardChanged(id);

        if (enabled)
        {
            // Plan what is not planned yet; when everything is, just start what is ready.
            var backlog = await store.ListFeaturesAsync(id, cancellationToken);
            if (backlog.Any(f => f.Status == FeatureStatus.NotStarted && f.PlannedAt is null))
            {
                if (await orchestrator.PlanAsync(id, User.Identity?.Name ?? "unknown", cancellationToken) is { } error) TempData["Error"] = error;
            }
            else
            {
                await features.ScheduleAsync(id, cancellationToken);
            }
        }
        TempData["Message"] = enabled
            ? $"Autopilot on: the orchestrator starts ready features, up to {project.MaxParallelFeatures} in parallel."
            : "Autopilot off: features in progress continue, but no new ones start by themselves.";
        return RedirectToAction(nameof(Board), new { id });
    }

    /// <summary>Asks the orchestrator to plan the backlog now.</summary>
    [HttpPost]
    public async Task<IActionResult> Plan(string id, CancellationToken cancellationToken)
    {
        var error = await orchestrator.PlanAsync(id, User.Identity?.Name ?? "unknown", cancellationToken);
        if (error is not null) TempData["Error"] = error;
        return RedirectToAction(nameof(Board), new { id });
    }

    [HttpGet]
    public async Task<IActionResult> Settings(string id, CancellationToken cancellationToken)
    {
        var project = await store.GetProjectAsync(id, cancellationToken);
        if (project is null) return NotFound();
        return View(new BoardSettingsViewModel(project, ModelOptions, claudeOptions.CurrentValue.DefaultModel));
    }

    [HttpPost]
    public async Task<IActionResult> Settings(string id, BoardSettingsForm form, CancellationToken cancellationToken)
    {
        var project = await store.GetProjectAsync(id, cancellationToken);
        if (project is null) return NotFound();

        var workspace = form.WorkspacePath.Trim();
        var workspaceChanged = !string.Equals(SafeFullPath(workspace), project.WorkspacePath, StringComparison.OrdinalIgnoreCase);
        if (workspaceChanged && ValidateWorkspace(workspace) is { } error) ModelState.AddModelError(nameof(form.WorkspacePath), error);
        if (workspaceChanged && (await store.ListFeaturesAsync(id, cancellationToken)).Any(f => f.IsActive))
        {
            ModelState.AddModelError(nameof(form.WorkspacePath), "The workspace can't change while a feature is in progress.");
        }
        if (form.DeployEnabled && (string.IsNullOrWhiteSpace(form.SubscriptionId) || string.IsNullOrWhiteSpace(form.ResourceGroup)))
        {
            ModelState.AddModelError(nameof(form.DeployEnabled), "Choose a subscription and a resource group to deploy to Azure.");
        }
        if (!ModelState.IsValid)
        {
            TempData["Error"] = string.Join(" ", ModelState.Values.SelectMany(v => v.Errors).Select(e => e.ErrorMessage));
            return RedirectToAction(nameof(Settings), new { id });
        }

        await store.UpdateProjectAsync(id, p =>
        {
            p.Name = form.Name.Trim();
            p.Description = form.Description?.Trim() ?? string.Empty;
            p.WorkspacePath = Path.GetFullPath(workspace);
            p.Azure.SubscriptionId = form.SubscriptionId?.Trim() ?? string.Empty;
            p.Azure.SubscriptionName = string.IsNullOrWhiteSpace(form.SubscriptionId) ? string.Empty : form.SubscriptionName?.Trim() ?? string.Empty;
            p.Azure.ResourceGroup = form.ResourceGroup?.Trim() ?? string.Empty;
            p.Azure.Location = string.IsNullOrWhiteSpace(form.Location) ? "eastus" : form.Location.Trim();
            p.Azure.DeployEnabled = form.DeployEnabled;
        }, cancellationToken);
        TempData["Message"] = "Board settings saved.";
        return RedirectToAction(nameof(Settings), new { id });
    }

    [HttpPost]
    public async Task<IActionResult> UpdateAgent(string id, AgentRole role, TeamAgentForm form, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            TempData["Error"] = string.Join(" ", ModelState.Values.SelectMany(v => v.Errors).Select(e => e.ErrorMessage));
            return Redirect(Url.Action(nameof(Settings), new { id }) + "#agent-" + role);
        }

        var project = await store.UpdateProjectAsync(id, p =>
        {
            var agent = p.Team.FirstOrDefault(a => a.Role == role);
            if (agent is null) return;
            agent.Name = form.Name.Trim();
            agent.Instructions = form.Instructions.Trim();
            agent.Model = form.Model?.Trim() ?? string.Empty;
        }, cancellationToken);
        if (project is null) return NotFound();

        TempData["Message"] = $"{Team.Label(role)} agent saved. Features started from now on use it.";
        return Redirect(Url.Action(nameof(Settings), new { id }) + "#agent-" + role);
    }

    [HttpPost]
    public async Task<IActionResult> ResetAgent(string id, AgentRole role, CancellationToken cancellationToken)
    {
        var defaults = Team.CreateDefaultTeam().First(a => a.Role == role);
        var project = await store.UpdateProjectAsync(id, p =>
        {
            var index = p.Team.FindIndex(a => a.Role == role);
            if (index >= 0) p.Team[index] = defaults;
            else p.Team.Add(defaults);
        }, cancellationToken);
        if (project is null) return NotFound();

        TempData["Message"] = $"{Team.Label(role)} agent reset to the default prompt.";
        return Redirect(Url.Action(nameof(Settings), new { id }) + "#agent-" + role);
    }

    [HttpPost]
    public async Task<IActionResult> Delete(string id, CancellationToken cancellationToken)
    {
        if ((await store.ListFeaturesAsync(id, cancellationToken)).Any(f => f.IsActive))
        {
            TempData["Error"] = "Cancel the features in progress before deleting the board.";
            return RedirectToAction(nameof(Settings), new { id });
        }
        await store.DeleteProjectAsync(id, cancellationToken);
        TempData["Message"] = "Board deleted. Its workspace folder (the generated code) was kept.";
        return RedirectToAction(nameof(Index));
    }

    private async IAsyncEnumerable<SseItem<object>> StreamBoard(string projectId, long version,
                                                                [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // A version from before the app restarted: send the current board straight away.
        if (version > notifier.Version(projectId)) version = -1;

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await notifier.WaitForChangeAsync(projectId, version, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                yield break;
            }

            version = notifier.Version(projectId);
            var cards = await CardsAsync(projectId, cancellationToken);
            var project = await store.GetProjectAsync(projectId, cancellationToken);
            if (project is null) yield break;
            yield return new SseItem<object>(new { version, cards, orchestration = OrchestrationCard.From(project) }, "board");
        }
    }

    private async Task<IReadOnlyList<FeatureCard>> CardsAsync(string projectId, CancellationToken cancellationToken)
    {
        var cards = new List<FeatureCard>();
        var all = await store.ListFeaturesAsync(projectId, cancellationToken);
        foreach (var feature in all)
        {
            var run = feature is { IsActive: true, CurrentRunId: { } runId } ? await runs.GetAsync(runId, cancellationToken) : null;
            cards.Add(FeatureCard.From(feature, run?.Stage, FeatureManager.BlockedBy(feature, all)));
        }
        return cards;
    }

    private string NewWorkspaceFolder(string name)
    {
        var slug = NonSlugRegex().Replace((name ?? string.Empty).Trim().ToLowerInvariant(), "-").Trim('-');
        if (slug.Length == 0) slug = "board";
        if (slug.Length > 40) slug = slug[..40].TrimEnd('-');

        var parent = boardOptions.CurrentValue.EffectiveWorkspacesFolder;
        var candidate = Path.Combine(parent, slug);
        for (var i = 2; Directory.Exists(candidate) || System.IO.File.Exists(candidate); i++) candidate = Path.Combine(parent, $"{slug}-{i}");
        return candidate;
    }

    /// <summary>
    /// The workspace must be a full path to a folder (new or existing) that isn't a drive root or inside this app,
    /// because agents write code and run builds in it.
    /// </summary>
    private string? ValidateWorkspace(string path)
    {
        if (!Path.IsPathFullyQualified(path)) return "Enter a full folder path, such as C:\\Projects\\my-app.";
        var full = SafeFullPath(path);
        if (full is null) return "The workspace folder path is not valid.";
        if (string.Equals(Path.GetPathRoot(full), full, StringComparison.OrdinalIgnoreCase)) return "Choose a folder, not a drive root.";
        if (System.IO.File.Exists(full)) return "That path is a file. Choose a folder.";

        foreach (var reserved in new[] { environment.ContentRootPath, pipelines.DataFolder })
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(reserved));
            if (full.Equals(root, StringComparison.OrdinalIgnoreCase)
                || full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || root.StartsWith(full + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                return "Choose a folder outside this app's folder.";
            }
        }
        return null;
    }

    private static string? SafeFullPath(string path)
    {
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NonSlugRegex();
}
