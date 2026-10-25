using System.Text.Json;

namespace ClaudeAgentPipelines.Pipelines;

/// <summary>Finished runs, saved as {DataFolder}/runs/{runId}.json.</summary>
public sealed class RunRepository
{
    private readonly string _root;

    public RunRepository(PipelineStore store)
    {
        _root = Path.Combine(store.DataFolder, "runs");
        Directory.CreateDirectory(_root);
    }

    public async Task SaveAsync(PipelineRun run, CancellationToken cancellationToken = default)
    {
        var file = Path.Combine(_root, run.Id + ".json");
        var temp = file + ".tmp";
        await using (var stream = File.Create(temp))
        {
            await JsonSerializer.SerializeAsync(stream, run.Snapshot(), PipelineStore.Json, cancellationToken);
        }
        File.Move(temp, file, overwrite: true);
    }

    public async Task<PipelineRun?> GetAsync(string id, CancellationToken cancellationToken = default)
    {
        if (!Ids.IsValid(id)) return null;
        var file = Path.Combine(_root, id + ".json");
        if (!File.Exists(file)) return null;

        await using var stream = File.OpenRead(file);
        return await JsonSerializer.DeserializeAsync<PipelineRun>(stream, PipelineStore.Json, cancellationToken);
    }

    /// <summary>The most recently finished runs, newest first.</summary>
    public async Task<IReadOnlyList<PipelineRun>> ListAsync(int max, CancellationToken cancellationToken = default)
    {
        var runs = new List<PipelineRun>();
        var files = new DirectoryInfo(_root).EnumerateFiles("*.json")
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .Take(max);
        foreach (var file in files)
        {
            try
            {
                await using var stream = file.OpenRead();
                var run = await JsonSerializer.DeserializeAsync<PipelineRun>(stream, PipelineStore.Json, cancellationToken);
                if (run is not null) runs.Add(run);
            }
            catch (JsonException)
            {
                // Skip a damaged file.
            }
        }
        return runs;
    }

    public void Delete(string id)
    {
        if (!Ids.IsValid(id)) return;
        var file = Path.Combine(_root, id + ".json");
        if (File.Exists(file)) File.Delete(file);
    }
}
