using ClaudeAgentPipelines.Data;
using ClaudeAgentPipelines.Pipelines;
using Microsoft.EntityFrameworkCore;

namespace ClaudeAgentPipelines.Board;

/// <summary>Stores boards (projects), their agent teams, features and tasks in the database.</summary>
public sealed class BoardStore(IDbContextFactory<AppDbContext> databases)
{
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public async Task<IReadOnlyList<Project>> ListProjectsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await databases.CreateDbContextAsync(cancellationToken);
        var projects = await db.ProjectsWithTeam().ToListAsync(cancellationToken);
        foreach (var project in projects) WithDefaultAgents(project);
        return projects.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public async Task<Project?> GetProjectAsync(string? id, CancellationToken cancellationToken = default)
    {
        if (!Ids.IsValid(id)) return null;
        await using var db = await databases.CreateDbContextAsync(cancellationToken);
        var project = await db.ProjectsWithTeam().FirstOrDefaultAsync(p => p.Id == id, cancellationToken);
        return project is null ? null : WithDefaultAgents(project);
    }

    public async Task CreateProjectAsync(Project project, CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            project.UpdatedAt = DateTimeOffset.UtcNow;
            await using var db = await databases.CreateDbContextAsync(cancellationToken);
            db.Stage(project);
            await db.SaveChangesAsync(cancellationToken);
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
            await using var db = await databases.CreateDbContextAsync(cancellationToken);
            await db.UpdateAsync(project, cancellationToken);
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
            await using var db = await databases.CreateDbContextAsync(cancellationToken);
            await db.Projects.Where(p => p.Id == id).ExecuteDeleteAsync(cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<IReadOnlyList<Feature>> ListFeaturesAsync(string projectId, CancellationToken cancellationToken = default)
    {
        if (!Ids.IsValid(projectId)) return [];
        await using var db = await databases.CreateDbContextAsync(cancellationToken);
        return await db.FeaturesWithTasks()
            .Where(f => f.ProjectId == projectId)
            .OrderBy(f => f.Number)
            .ToListAsync(cancellationToken);
    }

    public async Task<Feature?> GetFeatureAsync(string? projectId, string? featureId, CancellationToken cancellationToken = default)
    {
        if (!Ids.IsValid(projectId) || !Ids.IsValid(featureId)) return null;
        await using var db = await databases.CreateDbContextAsync(cancellationToken);
        return await db.FeaturesWithTasks().FirstOrDefaultAsync(f => f.ProjectId == projectId && f.Id == featureId, cancellationToken);
    }

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
            feature.UpdatedAt = DateTimeOffset.UtcNow;

            await using var db = await databases.CreateDbContextAsync(cancellationToken);
            await db.UpdateAsync(project, cancellationToken);
            db.Stage(feature);
            await db.SaveChangesAsync(cancellationToken);
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

            // Saved as a whole: the feature's rows (its tasks go with them) are deleted and inserted again.
            await using var db = await databases.CreateDbContextAsync(cancellationToken);
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            await db.Features.Where(f => f.Id == featureId).ExecuteDeleteAsync(cancellationToken);
            db.Stage(feature);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
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
            await using var db = await databases.CreateDbContextAsync(cancellationToken);
            await db.Features.Where(f => f.ProjectId == projectId && f.Id == featureId).ExecuteDeleteAsync(cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>Boards made before a role existed get its default agent, in team order.</summary>
    private static Project WithDefaultAgents(Project project)
    {
        var defaults = Team.CreateDefaultTeam();
        if (defaults.Any(d => project.Team.All(a => a.Role != d.Role)))
        {
            project.Team = defaults.Select(d => project.Team.FirstOrDefault(a => a.Role == d.Role) ?? d).ToList();
        }
        return project;
    }
}
