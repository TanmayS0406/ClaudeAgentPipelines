using System.Text.Json;
using Microsoft.Extensions.Options;

namespace ClaudeAgentPipelines.Pipelines;

/// <summary>
/// Stores pipelines as JSON files: {DataFolder}/pipelines/{pipelineId}/pipeline.json, with each agent's files in
/// {DataFolder}/pipelines/{pipelineId}/agents/{agentId}/.
/// </summary>
public sealed class PipelineStore
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly string _root;

    public PipelineStore(IOptions<StorageOptions> options, IWebHostEnvironment environment)
    {
        DataFolder = Path.GetFullPath(Path.Combine(environment.ContentRootPath, options.Value.DataFolder));
        _root = Path.Combine(DataFolder, "pipelines");
        Directory.CreateDirectory(_root);
    }

    public string DataFolder { get; }

    public async Task<IReadOnlyList<Pipeline>> ListAsync(CancellationToken cancellationToken = default)
    {
        var pipelines = new List<Pipeline>();
        foreach (var dir in Directory.EnumerateDirectories(_root))
        {
            var pipeline = await GetAsync(Path.GetFileName(dir), cancellationToken);
            if (pipeline is not null) pipelines.Add(pipeline);
        }
        return pipelines.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public async Task<Pipeline?> GetAsync(string? id, CancellationToken cancellationToken = default)
    {
        if (!Ids.IsValid(id)) return null;
        var file = PipelineFile(id!);
        if (!File.Exists(file)) return null;

        await using var stream = File.OpenRead(file);
        return await JsonSerializer.DeserializeAsync<Pipeline>(stream, Json, cancellationToken);
    }

    public async Task CreateAsync(Pipeline pipeline, CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(PipelineFolder(pipeline.Id));
            await WriteAsync(pipeline, cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>Loads, changes and saves a pipeline under the write lock. Returns false when it does not exist.</summary>
    public async Task<bool> UpdateAsync(string id, Action<Pipeline> change, CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            var pipeline = await GetAsync(id, cancellationToken);
            if (pipeline is null) return false;

            change(pipeline);
            await WriteAsync(pipeline, cancellationToken);
            return true;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        if (!Ids.IsValid(id)) return;
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            var folder = PipelineFolder(id);
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>Removes an agent and its files.</summary>
    public async Task<bool> DeleteAgentAsync(string id, string agentId, CancellationToken cancellationToken = default)
    {
        var removed = await UpdateAsync(id, p => p.Agents.RemoveAll(a => a.Id == agentId), cancellationToken);
        var folder = AgentFolder(id, agentId);
        if (removed && folder is not null && Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        return removed;
    }

    /// <summary>Folder holding an agent's files, or null for invalid IDs.</summary>
    public string? AgentFolder(string pipelineId, string agentId) =>
        Ids.IsValid(pipelineId) && Ids.IsValid(agentId) ? Path.Combine(PipelineFolder(pipelineId), "agents", agentId) : null;

    /// <summary>Full path of an agent file, or null when the pipeline, agent or file does not exist.</summary>
    public async Task<(AgentFile File, string Path)?> GetFileAsync(string pipelineId, string agentId, string fileId,
                                                                   CancellationToken cancellationToken = default)
    {
        var pipeline = await GetAsync(pipelineId, cancellationToken);
        var file = pipeline?.Agents.FirstOrDefault(a => a.Id == agentId)?.Files.FirstOrDefault(f => f.Id == fileId);
        if (file is null) return null;

        var path = Path.Combine(AgentFolder(pipelineId, agentId)!, file.FileName);
        return File.Exists(path) ? (file, path) : null;
    }

    /// <summary>Saves an uploaded file for an agent. Throws <see cref="InvalidOperationException"/> when it is not allowed.</summary>
    public async Task<AgentFile> AddFileAsync(string pipelineId, string agentId, string fileName, Stream content, int maxFiles,
                                              CancellationToken cancellationToken = default)
    {
        var folder = AgentFolder(pipelineId, agentId) ?? throw new InvalidOperationException("Agent not found.");

        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            var pipeline = await GetAsync(pipelineId, cancellationToken) ?? throw new InvalidOperationException("Pipeline not found.");
            var agent = pipeline.Agents.FirstOrDefault(a => a.Id == agentId) ?? throw new InvalidOperationException("Agent not found.");
            if (agent.Files.Count >= maxFiles)
            {
                throw new InvalidOperationException($"An agent can have at most {maxFiles} files.");
            }

            Directory.CreateDirectory(folder);
            var safeName = UniqueFileName(folder, SafeFileName(fileName));
            var path = Path.Combine(folder, safeName);
            await using (var target = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
            {
                await content.CopyToAsync(target, cancellationToken);
            }

            var file = new AgentFile { FileName = safeName, Length = new FileInfo(path).Length };
            agent.Files.Add(file);
            await WriteAsync(pipeline, cancellationToken);
            return file;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<bool> DeleteFileAsync(string pipelineId, string agentId, string fileId, CancellationToken cancellationToken = default)
    {
        string? fileName = null;
        var updated = await UpdateAsync(pipelineId, p =>
        {
            var agent = p.Agents.FirstOrDefault(a => a.Id == agentId);
            var file = agent?.Files.FirstOrDefault(f => f.Id == fileId);
            if (file is null) return;
            agent!.Files.Remove(file);
            fileName = file.FileName;
        }, cancellationToken);

        if (!updated || fileName is null) return false;
        var path = Path.Combine(AgentFolder(pipelineId, agentId)!, fileName);
        if (File.Exists(path)) File.Delete(path);
        return true;
    }

    /// <summary>Creates an example pipeline with agent files the first time the app runs.</summary>
    public async Task EnsureSamplePipelineAsync()
    {
        if (Directory.EnumerateDirectories(_root).Any()) return;

        var analyst = new AgentDefinition
        {
            Name = "Change analyst",
            Model = "sonnet",
            Instructions =
                "Read the attached changelog. List the changes that matter to customers, grouped as New, Improved and Fixed. " +
                "Leave out internal refactoring, build and test changes. For each item give one plain sentence and the ticket number."
        };
        var writer = new AgentDefinition
        {
            Name = "Release notes writer",
            Model = "sonnet",
            Instructions =
                "Write release notes from the change analyst's list, for the version and audience given in the pipeline input. " +
                "Follow the attached style guide exactly."
        };
        var editor = new AgentDefinition
        {
            Name = "Editor",
            Model = "haiku",
            Instructions =
                "Check the release notes against the style guide and the analyst's list: fix tone, remove anything not in the list, " +
                "and tighten wording. Reply with the final release notes in Markdown only - no commentary."
        };

        var pipeline = new Pipeline
        {
            Name = "Release notes",
            Description = "Sample: turns a raw changelog into customer-facing release notes in three steps.",
            Agents = [analyst, writer, editor]
        };
        await CreateAsync(pipeline);

        await AddSampleFileAsync(pipeline.Id, analyst.Id, "changelog.md", """
            # Changelog 2.4.0

            - PRJ-412 Add export of invoices to CSV from the invoice list
            - PRJ-418 Refactor InvoiceRepository to use async queries
            - PRJ-420 Fix: totals were rounded twice when a discount and tax applied together
            - PRJ-421 Bump test framework to 3.2
            - PRJ-425 Dashboard loads about 40% faster for accounts with more than 10,000 invoices
            - PRJ-430 Fix: password reset e-mail link expired after 5 minutes instead of 24 hours
            - PRJ-431 CI: cache NuGet packages
            - PRJ-433 New dark theme (Settings > Appearance)
            """);
        const string styleGuide = """
            # Release notes style guide

            - Start with a one-sentence summary of the release.
            - Sections, in this order: "What's new", "Improvements", "Fixes". Omit empty sections.
            - Write to the customer as "you". Friendly, plain English, no jargon, no internal names.
            - One bullet per change, at most two short sentences. Bold the feature name in "What's new".
            - Put the ticket number at the end of each bullet in parentheses.
            """;
        await AddSampleFileAsync(pipeline.Id, writer.Id, "style-guide.md", styleGuide);
        await AddSampleFileAsync(pipeline.Id, editor.Id, "style-guide.md", styleGuide);
    }

    private async Task AddSampleFileAsync(string pipelineId, string agentId, string name, string text)
    {
        using var content = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(text));
        await AddFileAsync(pipelineId, agentId, name, content, int.MaxValue);
    }

    private string PipelineFolder(string id) => Path.Combine(_root, id);

    private string PipelineFile(string id) => Path.Combine(PipelineFolder(id), "pipeline.json");

    private async Task WriteAsync(Pipeline pipeline, CancellationToken cancellationToken)
    {
        pipeline.UpdatedAt = DateTimeOffset.UtcNow;
        var file = PipelineFile(pipeline.Id);
        var temp = file + ".tmp";
        await using (var stream = File.Create(temp))
        {
            await JsonSerializer.SerializeAsync(stream, pipeline, Json, cancellationToken);
        }
        File.Move(temp, file, overwrite: true);
    }

    private static string SafeFileName(string fileName)
    {
        var name = Path.GetFileName(fileName ?? string.Empty).Trim();
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        name = name.Trim('.', ' ');
        if (name.Length == 0) name = "file";

        var extension = Path.GetExtension(name);
        var stem = Path.GetFileNameWithoutExtension(name);
        if (stem.Length > 80) stem = stem[..80];
        return stem + extension;
    }

    private static string UniqueFileName(string folder, string name)
    {
        var stem = Path.GetFileNameWithoutExtension(name);
        var extension = Path.GetExtension(name);
        var candidate = name;
        for (var i = 2; File.Exists(Path.Combine(folder, candidate)); i++)
        {
            candidate = $"{stem}-{i}{extension}";
        }
        return candidate;
    }
}
