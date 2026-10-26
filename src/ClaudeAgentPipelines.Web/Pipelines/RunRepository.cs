using ClaudeAgentPipelines.Data;
using Microsoft.EntityFrameworkCore;

namespace ClaudeAgentPipelines.Pipelines;

/// <summary>Finished runs, saved in the database.</summary>
public sealed class RunRepository(IDbContextFactory<AppDbContext> databases)
{
    public async Task SaveAsync(PipelineRun run, CancellationToken cancellationToken = default)
    {
        var snapshot = run.Snapshot();
        await using var db = await databases.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.Runs.Where(r => r.Id == snapshot.Id).ExecuteDeleteAsync(cancellationToken);
        db.Stage(snapshot);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<PipelineRun?> GetAsync(string id, CancellationToken cancellationToken = default)
    {
        if (!Ids.IsValid(id)) return null;
        await using var db = await databases.CreateDbContextAsync(cancellationToken);
        return await db.RunsWithSteps().FirstOrDefaultAsync(r => r.Id == id, cancellationToken);
    }

    /// <summary>The most recently finished runs, newest first.</summary>
    public async Task<IReadOnlyList<PipelineRun>> ListAsync(int max, CancellationToken cancellationToken = default)
    {
        await using var db = await databases.CreateDbContextAsync(cancellationToken);
        return await db.RunsWithSteps()
            .OrderByDescending(r => r.CompletedAt)
            .ThenByDescending(r => r.StartedAt)
            .Take(max)
            .ToListAsync(cancellationToken);
    }

    public void Delete(string id)
    {
        if (!Ids.IsValid(id)) return;
        using var db = databases.CreateDbContext();
        db.Runs.Where(r => r.Id == id).ExecuteDelete();
    }
}
