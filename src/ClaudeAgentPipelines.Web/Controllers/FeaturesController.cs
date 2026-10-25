using ClaudeAgentPipelines.Board;
using ClaudeAgentPipelines.Models;
using ClaudeAgentPipelines.Pipelines;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeAgentPipelines.Controllers;

/// <summary>Feature work items: create from a prompt, start the agent team, follow it live, cancel.</summary>
public sealed class FeaturesController(BoardStore store, FeatureManager features, RunManager runs) : Controller
{
    [HttpPost]
    public async Task<IActionResult> Create(string projectId, FeatureForm form, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            TempData["Error"] = string.Join(" ", ModelState.Values.SelectMany(v => v.Errors).Select(e => e.ErrorMessage));
            return RedirectToAction("Board", "Boards", new { id = projectId });
        }

        var feature = await store.CreateFeatureAsync(projectId, new Feature
        {
            Title = form.Title.Trim(),
            Prompt = form.Prompt.Trim(),
            CreatedBy = User.Identity?.Name ?? "unknown"
        }, cancellationToken);
        if (feature is null) return NotFound();

        features.BoardChanged(projectId);

        if (form.StartNow)
        {
            var error = await features.StartAsync(projectId, feature.Id, User.Identity?.Name ?? "unknown", cancellationToken);
            if (error is not null) TempData["Error"] = error;
        }
        return RedirectToAction("Board", "Boards", new { id = projectId });
    }

    public async Task<IActionResult> Details(string projectId, string id, CancellationToken cancellationToken)
    {
        var project = await store.GetProjectAsync(projectId, cancellationToken);
        var feature = await store.GetFeatureAsync(projectId, id, cancellationToken);
        if (project is null || feature is null) return NotFound();

        var run = feature.CurrentRunId is { } runId ? (await runs.GetAsync(runId, cancellationToken))?.Snapshot() : null;
        var history = new List<PipelineRun>();
        foreach (var previous in feature.RunIds.Where(r => r != feature.CurrentRunId).Take(10))
        {
            if (await runs.GetAsync(previous, cancellationToken) is { } past) history.Add(past);
        }
        return View(new FeatureDetailsViewModel(project, feature, run, history));
    }

    [HttpPost]
    public async Task<IActionResult> Update(string projectId, string id, FeatureForm form, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            TempData["Error"] = string.Join(" ", ModelState.Values.SelectMany(v => v.Errors).Select(e => e.ErrorMessage));
            return RedirectToAction(nameof(Details), new { projectId, id });
        }

        var current = await store.GetFeatureAsync(projectId, id, cancellationToken);
        if (current is null) return NotFound();
        if (current.IsActive)
        {
            TempData["Error"] = "The feature can't be edited while the agents work on it.";
            return RedirectToAction(nameof(Details), new { projectId, id });
        }

        await features.UpdateAsync(projectId, id, f =>
        {
            f.Title = form.Title.Trim();
            f.Prompt = form.Prompt.Trim();
        }, cancellationToken);
        TempData["Message"] = "Feature saved.";
        return RedirectToAction(nameof(Details), new { projectId, id });
    }

    /// <summary>Starts the agent team. <paramref name="returnTo"/> = "board" goes back to the board (drag and drop).</summary>
    [HttpPost]
    public async Task<IActionResult> Start(string projectId, string id, string? returnTo, CancellationToken cancellationToken)
    {
        var error = await features.StartAsync(projectId, id, User.Identity?.Name ?? "unknown", cancellationToken);
        if (Request.Headers.Accept.Any(a => a?.Contains("application/json") == true))
        {
            return error is null ? Json(new { started = true }) : BadRequest(new { error });
        }

        if (error is not null) TempData["Error"] = error;
        return returnTo == "board"
            ? RedirectToAction("Board", "Boards", new { id = projectId })
            : RedirectToAction(nameof(Details), new { projectId, id });
    }

    [HttpPost]
    public async Task<IActionResult> Cancel(string projectId, string id, CancellationToken cancellationToken)
    {
        if (!await features.CancelAsync(projectId, id, cancellationToken)) TempData["Error"] = "The feature is not in progress.";
        return RedirectToAction(nameof(Details), new { projectId, id });
    }

    [HttpPost]
    public async Task<IActionResult> Delete(string projectId, string id, CancellationToken cancellationToken)
    {
        var feature = await store.GetFeatureAsync(projectId, id, cancellationToken);
        if (feature is null) return NotFound();
        if (feature.IsActive)
        {
            TempData["Error"] = "Cancel the feature before deleting it.";
            return RedirectToAction(nameof(Details), new { projectId, id });
        }

        await store.DeleteFeatureAsync(projectId, id, cancellationToken);
        features.BoardChanged(projectId);
        TempData["Message"] = $"Feature #{feature.Number} deleted. The code the agents wrote stays in the workspace.";
        return RedirectToAction("Board", "Boards", new { id = projectId });
    }
}
