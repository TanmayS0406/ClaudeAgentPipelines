using ClaudeAgentPipelines.Models;
using ClaudeAgentPipelines.Pipelines;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace ClaudeAgentPipelines.Controllers;

/// <summary>Create pipelines and edit their agents: name, prompt, model and attached files.</summary>
public sealed class PipelinesController(
    PipelineStore store,
    RunManager runs,
    IOptionsMonitor<StorageOptions> storageOptions,
    IOptionsMonitor<ClaudeOptions> claudeOptions) : Controller
{
    private static readonly string[] ModelOptions = ["opus", "sonnet", "haiku", "fable"];

    public async Task<IActionResult> Index(CancellationToken cancellationToken) =>
        View(await store.ListAsync(cancellationToken));

    [HttpGet]
    public IActionResult Create() => View(new PipelineForm());

    [HttpPost]
    public async Task<IActionResult> Create(PipelineForm form, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid) return View(form);

        var pipeline = new Pipeline { Name = form.Name.Trim(), Description = form.Description?.Trim() ?? string.Empty };
        await store.CreateAsync(pipeline, cancellationToken);
        return RedirectToAction(nameof(Edit), new { id = pipeline.Id });
    }

    [HttpGet]
    public async Task<IActionResult> Edit(string id, CancellationToken cancellationToken)
    {
        var pipeline = await store.GetAsync(id, cancellationToken);
        if (pipeline is null) return NotFound();

        return View(new PipelineEditViewModel(pipeline, ModelOptions, claudeOptions.CurrentValue.DefaultModel, storageOptions.CurrentValue));
    }

    [HttpPost]
    public async Task<IActionResult> Update(string id, PipelineForm form, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid) return Fail(id);

        var found = await store.UpdateAsync(id, p =>
        {
            p.Name = form.Name.Trim();
            p.Description = form.Description?.Trim() ?? string.Empty;
        }, cancellationToken);
        return found ? Saved(id, "Pipeline saved.") : NotFound();
    }

    [HttpPost]
    public async Task<IActionResult> Delete(string id, CancellationToken cancellationToken)
    {
        await store.DeleteAsync(id, cancellationToken);
        TempData["Message"] = "Pipeline deleted.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    public async Task<IActionResult> AddAgent(string id, AgentForm form, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid) return Fail(id);

        var agent = new AgentDefinition
        {
            Name = form.Name.Trim(),
            Instructions = form.Instructions.Trim(),
            Model = form.Model?.Trim() ?? string.Empty
        };
        var found = await store.UpdateAsync(id, p => p.Agents.Add(agent), cancellationToken);
        return found ? Saved(id, $"Agent \"{agent.Name}\" added.", agent.Id) : NotFound();
    }

    [HttpPost]
    public async Task<IActionResult> UpdateAgent(string id, string agentId, AgentForm form, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid) return Fail(id, agentId);

        var found = await store.UpdateAsync(id, p =>
        {
            var agent = p.Agents.FirstOrDefault(a => a.Id == agentId);
            if (agent is null) return;
            agent.Name = form.Name.Trim();
            agent.Instructions = form.Instructions.Trim();
            agent.Model = form.Model?.Trim() ?? string.Empty;
        }, cancellationToken);
        return found ? Saved(id, "Agent saved.", agentId) : NotFound();
    }

    [HttpPost]
    public async Task<IActionResult> DeleteAgent(string id, string agentId, CancellationToken cancellationToken)
    {
        var found = await store.DeleteAgentAsync(id, agentId, cancellationToken);
        return found ? Saved(id, "Agent removed.") : NotFound();
    }

    /// <summary>Moves an agent one step earlier (<paramref name="offset"/> = -1) or later (+1) in the pipeline.</summary>
    [HttpPost]
    public async Task<IActionResult> MoveAgent(string id, string agentId, int offset, CancellationToken cancellationToken)
    {
        var found = await store.UpdateAsync(id, p =>
        {
            var index = p.Agents.FindIndex(a => a.Id == agentId);
            var target = index + Math.Sign(offset);
            if (index < 0 || target < 0 || target >= p.Agents.Count) return;
            (p.Agents[index], p.Agents[target]) = (p.Agents[target], p.Agents[index]);
        }, cancellationToken);
        return found ? Saved(id, null, agentId) : NotFound();
    }

    [HttpPost]
    public async Task<IActionResult> UploadFiles(string id, string agentId, List<IFormFile> files, CancellationToken cancellationToken)
    {
        var storage = storageOptions.CurrentValue;
        var errors = new List<string>();
        var added = 0;

        foreach (var file in files.Where(f => f.Length > 0))
        {
            var extension = Path.GetExtension(file.FileName);
            if (!storage.EffectiveExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
            {
                errors.Add($"{file.FileName}: {(extension.Length == 0 ? "files without an extension" : extension + " files")} are not allowed.");
                continue;
            }
            if (file.Length > storage.MaxFileBytes)
            {
                errors.Add($"{file.FileName}: larger than {storage.MaxFileBytes / (1024 * 1024)} MB.");
                continue;
            }

            try
            {
                await using var content = file.OpenReadStream();
                await store.AddFileAsync(id, agentId, file.FileName, content, storage.MaxFilesPerAgent, cancellationToken);
                added++;
            }
            catch (InvalidOperationException ex)
            {
                errors.Add($"{file.FileName}: {ex.Message}");
            }
        }

        if (errors.Count > 0) TempData["Error"] = string.Join(" ", errors);
        if (added == 0 && errors.Count == 0) TempData["Error"] = "Choose one or more files to attach.";
        return Saved(id, added > 0 ? $"{added} file(s) attached." : null, agentId);
    }

    [HttpPost]
    public async Task<IActionResult> DeleteFile(string id, string agentId, string fileId, CancellationToken cancellationToken)
    {
        await store.DeleteFileAsync(id, agentId, fileId, cancellationToken);
        return Saved(id, "File removed.", agentId);
    }

    [HttpGet]
    public async Task<IActionResult> DownloadFile(string id, string agentId, string fileId, CancellationToken cancellationToken)
    {
        var found = await store.GetFileAsync(id, agentId, fileId, cancellationToken);
        if (found is null) return NotFound();

        // Always a download, never rendered inline (an uploaded .html file must not run in the app's origin).
        return PhysicalFile(found.Value.Path, "application/octet-stream", found.Value.File.FileName);
    }

    [HttpGet]
    public async Task<IActionResult> Run(string id, CancellationToken cancellationToken)
    {
        var pipeline = await store.GetAsync(id, cancellationToken);
        if (pipeline is null) return NotFound();
        return View(await RunViewModel(pipeline, new RunForm(), cancellationToken));
    }

    [HttpPost, ActionName("Run")]
    public async Task<IActionResult> StartRun(string id, RunForm form, CancellationToken cancellationToken)
    {
        var pipeline = await store.GetAsync(id, cancellationToken);
        if (pipeline is null) return NotFound();
        if (pipeline.Agents.Count == 0)
        {
            ModelState.AddModelError(string.Empty, "Add at least one agent before running the pipeline.");
        }
        if (!ModelState.IsValid) return View(await RunViewModel(pipeline, form, cancellationToken));

        var run = runs.Start(pipeline, form.Input.Trim(), User.Identity?.Name ?? "unknown");
        return RedirectToAction("Details", "Runs", new { id = run.Id });
    }

    private async Task<RunPipelineViewModel> RunViewModel(Pipeline pipeline, RunForm form, CancellationToken cancellationToken)
    {
        var recent = (await runs.ListAsync(50, cancellationToken)).Where(r => r.PipelineId == pipeline.Id).Take(5).ToList();
        return new RunPipelineViewModel(pipeline, form, recent);
    }

    private IActionResult Saved(string id, string? message, string? agentId = null)
    {
        if (message is not null) TempData["Message"] = message;
        var url = Url.Action(nameof(Edit), new { id });
        return Redirect(agentId is null ? url! : url + "#agent-" + agentId);
    }

    private IActionResult Fail(string id, string? agentId = null)
    {
        TempData["Error"] = string.Join(" ", ModelState.Values.SelectMany(v => v.Errors).Select(e => e.ErrorMessage));
        return Saved(id, null, agentId);
    }
}
