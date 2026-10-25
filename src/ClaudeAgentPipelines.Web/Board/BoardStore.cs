using System.Text.Json;
using ClaudeAgentPipelines.Pipelines;

namespace ClaudeAgentPipelines.Board;

/// <summary>
/// Stores boards as JSON files: {DataFolder}/projects/{projectId}/project.json, with each feature in
/// {DataFolder}/projects/{projectId}/features/{featureId}.json.
/// </summary>
public sealed class BoardStore
{
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly string _root;

    public BoardStore(PipelineStore pipelines)
    {
        _root = Path.Combine(pipelines.DataFolder, "projects");
        Directory.CreateDirectory(_root);
    }

    public async Task<IReadOnlyList<Project>> ListProjectsAsync(CancellationToken cancellationToken = default)
    {
        var projects = new List<Project>();
        foreach (var dir in Directory.EnumerateDirectories(_root))
        {
            var project = await GetProjectAsync(Path.GetFileName(dir), cancellationToken);
            if (project is not null) projects.Add(project);
        }
        return projects.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public Task<Project?> GetProjectAsync(string? id, CancellationToken cancellationToken = default) =>
        Ids.IsValid(id) ? ReadAsync<Project>(ProjectFile(id!), cancellationToken) : Task.FromResult<Project?>(null);

    public async Task CreateProjectAsync(Project project, CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(FeaturesFolder(project.Id));
            project.UpdatedAt = DateTimeOffset.UtcNow;
            await WriteAsync(ProjectFile(project.Id), project, cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>Loads, changes and saves a project under the write lock. Null when it does not exist.</summary>
    public async Task<Project?> UpdateProjectAsync(string id, Action<Project> change, CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            var project = await GetProjectAsync(id, cancellationToken);
            if (project is null) return null;

            change(project);
            project.UpdatedAt = DateTimeOffset.UtcNow;
            await WriteAsync(ProjectFile(id), project, cancellationToken);
            return project;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>Deletes the board and its features. The workspace (generated code) is left alone.</summary>
    public async Task DeleteProjectAsync(string id, CancellationToken cancellationToken = default)
    {
        if (!Ids.IsValid(id)) return;
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            var folder = Path.Combine(_root, id);
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<IReadOnlyList<Feature>> ListFeaturesAsync(string projectId, CancellationToken cancellationToken = default)
    {
        if (!Ids.IsValid(projectId) || !Directory.Exists(FeaturesFolder(projectId))) return [];

        var features = new List<Feature>();
        foreach (var file in Directory.EnumerateFiles(FeaturesFolder(projectId), "*.json"))
        {
            try
            {
                if (await ReadAsync<Feature>(file, cancellationToken) is { } feature) features.Add(feature);
            }
            catch (JsonException)
            {
                // Skip a damaged file.
            }
        }
        return features.OrderBy(f => f.Number).ToList();
    }

    public Task<Feature?> GetFeatureAsync(string? projectId, string? featureId, CancellationToken cancellationToken = default) =>
        Ids.IsValid(projectId) && Ids.IsValid(featureId)
            ? ReadAsync<Feature>(FeatureFile(projectId!, featureId!), cancellationToken)
            : Task.FromResult<Feature?>(null);

    /// <summary>Gives the feature the project's next number and saves it.</summary>
    public async Task<Feature?> CreateFeatureAsync(string projectId, Feature feature, CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            var project = await GetProjectAsync(projectId, cancellationToken);
            if (project is null) return null;

            feature.ProjectId = project.Id;
            feature.Number = project.NextNumber++;
            project.UpdatedAt = DateTimeOffset.UtcNow;
            await WriteAsync(ProjectFile(project.Id), project, cancellationToken);

            Directory.CreateDirectory(FeaturesFolder(project.Id));
            feature.UpdatedAt = DateTimeOffset.UtcNow;
            await WriteAsync(FeatureFile(project.Id, feature.Id), feature, cancellationToken);
            return feature;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>Loads, changes and saves a feature under the write lock. Null when it does not exist.</summary>
    public async Task<Feature?> UpdateFeatureAsync(string projectId, string featureId, Action<Feature> change,
                                                   CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            var feature = await GetFeatureAsync(projectId, featureId, cancellationToken);
            if (feature is null) return null;

            change(feature);
            feature.UpdatedAt = DateTimeOffset.UtcNow;
            await WriteAsync(FeatureFile(projectId, featureId), feature, cancellationToken);
            return feature;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task DeleteFeatureAsync(string projectId, string featureId, CancellationToken cancellationToken = default)
    {
        if (!Ids.IsValid(projectId) || !Ids.IsValid(featureId)) return;
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            var file = FeatureFile(projectId, featureId);
            if (File.Exists(file)) File.Delete(file);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private string ProjectFile(string id) => Path.Combine(_root, id, "project.json");

    private string FeaturesFolder(string projectId) => Path.Combine(_root, projectId, "features");

    private string FeatureFile(string projectId, string featureId) => Path.Combine(FeaturesFolder(projectId), featureId + ".json");

    private static async Task<T?> ReadAsync<T>(string file, CancellationToken cancellationToken) where T : class
    {
        if (!File.Exists(file)) return null;
        await using var stream = File.OpenRead(file);
        return await JsonSerializer.DeserializeAsync<T>(stream, PipelineStore.Json, cancellationToken);
    }

    private static async Task WriteAsync<T>(string file, T value, CancellationToken cancellationToken)
    {
        var temp = file + ".tmp";
        await using (var stream = File.Create(temp))
        {
            await JsonSerializer.SerializeAsync(stream, value, PipelineStore.Json, cancellationToken);
        }
        File.Move(temp, file, overwrite: true);
    }
}
