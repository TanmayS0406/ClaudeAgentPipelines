using System.Text.Json;
using ClaudeAgentPipelines.Board;
using ClaudeAgentPipelines.Pipelines;
using Microsoft.EntityFrameworkCore;

namespace ClaudeAgentPipelines.Data;

/// <summary>Brings the database up to date on start: migrations first, then the one-time import of old JSON files.</summary>
public static class DatabaseInitializer
{
    private const string ImportedKey = "legacy-json-imported";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Applies every pending migration (creating the database first when it does not exist), then imports the JSON files
    /// older versions of the app kept. Runs on every start; with nothing pending it does nothing.
    /// </summary>
    public static async Task InitializeAsync(IDbContextFactory<AppDbContext> databases, StorageOptions storage, string contentRoot, ILogger logger)
    {
        await using var db = await databases.CreateDbContextAsync();
        var pending = (await db.Database.GetPendingMigrationsAsync()).ToList();
        if (pending.Count > 0) logger.LogInformation("Applying {Count} database migration(s): {Migrations}", pending.Count, string.Join(", ", pending));
        await db.Database.MigrateAsync();

        if (await db.Meta.AnyAsync(m => m.Key == ImportedKey)) return;
        await ImportLegacyJsonAsync(databases, storage.ResolveDataFolder(contentRoot), logger);
        db.Meta.Add(new AppMeta { Key = ImportedKey, Value = DateTimeOffset.UtcNow.ToString("O") });
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Copies pipelines ({data}/pipelines/*/pipeline.json), boards ({data}/projects/*) and runs ({data}/runs/*.json) into
    /// the database, once. The old files are left where they are.
    /// </summary>
    private static async Task ImportLegacyJsonAsync(IDbContextFactory<AppDbContext> databases, string dataFolder, ILogger logger)
    {
        foreach (var file in Files(Path.Combine(dataFolder, "pipelines"), "pipeline.json", subfolders: true))
        {
            await ImportAsync<Pipeline>(databases, file, logger, (db, pipeline) => db.Stage(pipeline));
        }

        foreach (var file in Files(Path.Combine(dataFolder, "projects"), "project.json", subfolders: true))
        {
            await ImportAsync<Project>(databases, file, logger, (db, project) => db.Stage(project));
            var features = Path.Combine(Path.GetDirectoryName(file)!, "features");
            foreach (var featureFile in Files(features, "*.json", subfolders: false))
            {
                await ImportAsync<Feature>(databases, featureFile, logger, (db, feature) => db.Stage(feature));
            }
        }

        foreach (var file in Files(Path.Combine(dataFolder, "runs"), "*.json", subfolders: false))
        {
            await ImportAsync<PipelineRun>(databases, file, logger, (db, run) => db.Stage(run));
        }
    }

    private static IEnumerable<string> Files(string folder, string pattern, bool subfolders) =>
        !Directory.Exists(folder)
            ? []
            : subfolders
                ? Directory.EnumerateDirectories(folder).Select(d => Path.Combine(d, pattern)).Where(File.Exists)
                : Directory.EnumerateFiles(folder, pattern);

    private static async Task ImportAsync<T>(IDbContextFactory<AppDbContext> databases, string file, ILogger logger, Action<AppDbContext, T> stage)
        where T : class
    {
        try
        {
            await using var stream = File.OpenRead(file);
            var item = await JsonSerializer.DeserializeAsync<T>(stream, Json);
            if (item is null) return;

            await using var db = await databases.CreateDbContextAsync();
            stage(db, item);
            await db.SaveChangesAsync();
        }
        catch (Exception ex) when (ex is JsonException or DbUpdateException or IOException)
        {
            logger.LogWarning(ex, "Could not import {File} into the database; skipped.", file);
        }
    }
}
