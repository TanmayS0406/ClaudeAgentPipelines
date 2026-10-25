using System.Collections.Concurrent;
using ClaudeAgentPipelines.Azure;
using ClaudeAgentPipelines.Pipelines;
using Microsoft.Extensions.Options;

namespace ClaudeAgentPipelines.Board;

/// <summary>
/// Starts features on the agent team and keeps their board state up to date. Each feature is built on its own branch,
/// so up to the board's "features in parallel" run at the same time (the others wait in the queue); merging into main
/// and deploying happen one feature at a time. With autopilot on, <see cref="ScheduleAsync"/> starts every feature whose
/// dependencies are done.
/// </summary>
public sealed class FeatureManager(
    BoardStore store,
    RunManager runs,
    FeatureOrchestrator orchestrator,
    BoardNotifier notifier,
    AzureAccountService azure,
    IOptionsMonitor<ClaudeOptions> claudeOptions,
    IOptionsMonitor<BoardOptions> boardOptions,
    ILogger<FeatureManager> logger)
{
    private readonly ConcurrentDictionary<string, ParallelSlots> _slots = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _mainLocks = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _scheduleLocks = new();

    /// <summary>Starts (or restarts) a feature. Returns an error message when it can't start.</summary>
    public async Task<string?> StartAsync(string projectId, string featureId, string startedBy, CancellationToken cancellationToken)
    {
        var project = await store.GetProjectAsync(projectId, cancellationToken);
        var feature = await store.GetFeatureAsync(projectId, featureId, cancellationToken);
        if (project is null || feature is null) return "The feature was not found.";
        if (feature.IsActive) return $"Feature #{feature.Number} is already in progress.";

        if (project.Azure.DeployEnabled)
        {
            if (project.Azure.SubscriptionId.Length == 0 || project.Azure.ResourceGroup.Length == 0)
            {
                return "Deployment to Azure is turned on, but no subscription or resource group is chosen. Open the board settings.";
            }
            var status = await azure.GetStatusAsync(cancellationToken: cancellationToken);
            if (!status.LoggedIn)
            {
                return "Deployment to Azure is turned on, but the Azure CLI is not signed in. Sign in to Azure first.";
            }
        }

        // A failed or cancelled feature continues on its branch; a new or finished one starts a branch from main.
        var fresh = feature.Status is FeatureStatus.NotStarted or FeatureStatus.Completed || feature.Branch is null;
        var tasks = FeatureOrchestrator.InitialTasks(project);
        var run = new PipelineRun
        {
            PipelineName = $"#{feature.Number} {feature.Title}",
            ProjectId = project.Id,
            FeatureId = feature.Id,
            Input = feature.Prompt,
            StartedBy = startedBy,
            Steps = tasks.Select(t => FeatureOrchestrator.ToStep(t, project, claudeOptions.CurrentValue)).ToList()
        };

        await UpdateAsync(project.Id, feature.Id, f =>
        {
            f.Stage = FeatureStage.New;
            f.Status = FeatureStatus.Queued;
            f.Error = null;
            f.DeploymentUrl = null;
            f.Tasks = tasks;
            f.CurrentRunId = run.Id;
            f.RunIds.Insert(0, run.Id);
            if (fresh)
            {
                f.Branch = null;
                f.WorktreePath = null;
            }
        });

        runs.Start(run, (r, token) => ExecuteAsync(project.Id, feature.Id, fresh, r, token));
        return null;
    }

    /// <summary>
    /// Autopilot: starts the planned features whose dependencies are done, oldest first, until the board's parallel
    /// limit is reached. Does nothing when autopilot is off.
    /// </summary>
    public async Task ScheduleAsync(string projectId, CancellationToken cancellationToken = default)
    {
        var gate = _scheduleLocks.GetOrAdd(projectId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            var project = await store.GetProjectAsync(projectId, cancellationToken);
            if (project is not { Autopilot: true } || project.Orchestration.Planning) return;

            var features = await store.ListFeaturesAsync(projectId, cancellationToken);
            var active = features.Count(f => f.IsActive);
            foreach (var feature in ReadyToStart(features))
            {
                if (active >= Math.Max(1, project.MaxParallelFeatures)) break;
                var error = await StartAsync(projectId, feature.Id, "Orchestrator", cancellationToken);
                if (error is null)
                {
                    active++;
                }
                else
                {
                    // For example Azure is not signed in: report it on the card instead of trying again and again.
                    await UpdateAsync(projectId, feature.Id, f => f.Error = error, cancellationToken);
                    break;
                }
            }
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Features autopilot may start now: planned, never started, and every dependency done.</summary>
    public static IEnumerable<Feature> ReadyToStart(IReadOnlyList<Feature> features) =>
        features
            .Where(f => f.Status == FeatureStatus.NotStarted && f.PlannedAt is not null)
            .Where(f => BlockedBy(f, features).Count == 0)
            .OrderBy(f => f.Number);

    /// <summary>Dependencies of a feature that are not done yet (deleted features don't block).</summary>
    public static IReadOnlyList<int> BlockedBy(Feature feature, IReadOnlyList<Feature> features) =>
        feature.DependsOn
            .Where(number => features.Any(f => f.Number == number && !(f.Stage == FeatureStage.Done && f.Status == FeatureStatus.Completed)))
            .ToList();

    public async Task<bool> CancelAsync(string projectId, string featureId, CancellationToken cancellationToken)
    {
        var feature = await store.GetFeatureAsync(projectId, featureId, cancellationToken);
        return feature is { IsActive: true, CurrentRunId: { } runId } && runs.Cancel(runId);
    }

    /// <summary>Deletes a feature and its worktree and branch. The code merged into main stays.</summary>
    public async Task DeleteAsync(Project project, Feature feature, CancellationToken cancellationToken)
    {
        if (feature.WorktreePath is not null || feature.Branch is not null)
        {
            using (await LockMainAsync(project.Id, cancellationToken))
            {
                try
                {
                    await GitWorkspace.RemoveWorktreeAsync(project.WorkspacePath, feature.WorktreePath, null, cancellationToken);
                    if (feature.Branch is not null)
                    {
                        await GitWorkspace.RunAsync(project.WorkspacePath, ["branch", "-D", feature.Branch], cancellationToken);
                    }
                }
                catch (FeatureFailedException ex)
                {
                    logger.LogWarning(ex, "Could not remove the worktree of feature #{Number}", feature.Number);
                }
            }
        }
        await store.DeleteFeatureAsync(project.Id, feature.Id, cancellationToken);
        notifier.Notify(project.Id);
    }

    public Task<Feature?> UpdateAsync(string projectId, string featureId, Action<Feature> change) =>
        UpdateAsync(projectId, featureId, change, CancellationToken.None);

    public async Task<Feature?> UpdateAsync(string projectId, string featureId, Action<Feature> change, CancellationToken cancellationToken)
    {
        var feature = await store.UpdateFeatureAsync(projectId, featureId, change, cancellationToken);
        notifier.Notify(projectId);
        return feature;
    }

    /// <summary>Refreshes open board pages after something on the board changed.</summary>
    public void BoardChanged(string projectId) => notifier.Notify(projectId);

    /// <summary>
    /// Features that were in progress when the app stopped can't continue: their agents are gone. Their branches are
    /// kept, so a retry continues where they were.
    /// </summary>
    public async Task RecoverAsync()
    {
        foreach (var project in await store.ListProjectsAsync())
        {
            if (project.Orchestration.Planning)
            {
                await store.UpdateProjectAsync(project.Id, p =>
                {
                    p.Orchestration.Planning = false;
                    p.Orchestration.Error = "The app stopped while the orchestrator was planning.";
                });
            }
            foreach (var feature in (await store.ListFeaturesAsync(project.Id)).Where(f => f.IsActive))
            {
                await store.UpdateFeatureAsync(project.Id, feature.Id, f =>
                {
                    f.Status = FeatureStatus.Failed;
                    f.Error = "The app stopped while this feature was in progress. Start it again to continue.";
                    foreach (var task in f.Tasks.Where(t => t.State == TaskState.Doing)) task.State = TaskState.Cancelled;
                });
            }
        }
    }

    private async Task ExecuteAsync(string projectId, string featureId, bool fresh, PipelineRun run, CancellationToken cancellationToken)
    {
        var slots = _slots.GetOrAdd(projectId, _ => new ParallelSlots());
        try
        {
            if (!slots.TryEnter(await MaxParallelAsync(projectId)))
            {
                run.SetStage("Queued: waiting for a free slot (features in parallel)");
                await slots.EnterAsync(() => MaxParallelAsync(projectId), cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            await FinishAsync(projectId, featureId, FeatureStatus.Cancelled, null);
            throw;
        }

        try
        {
            var project = await store.GetProjectAsync(projectId, cancellationToken)
                          ?? throw new FeatureFailedException("The board was deleted.");
            var feature = await UpdateAsync(projectId, featureId, f => f.Status = FeatureStatus.Running, cancellationToken)
                          ?? throw new FeatureFailedException("The feature was deleted.");

            await orchestrator.RunAsync(project, feature, run, change => UpdateAsync(projectId, featureId, change),
                token => LockMainAsync(projectId, token), fresh, cancellationToken);

            run.SetStage("Done");
            await FinishAsync(projectId, featureId, FeatureStatus.Completed, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await FinishAsync(projectId, featureId, FeatureStatus.Cancelled, null);
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Feature {FeatureId} of board {ProjectId} failed", featureId, projectId);
            await FinishAsync(projectId, featureId, FeatureStatus.Failed, ex.Message);
            throw;
        }
        finally
        {
            slots.Exit();
            // A finished feature may unblock others, and its slot is free: let autopilot start what is ready.
            _ = Task.Run(async () =>
            {
                try
                {
                    await ScheduleAsync(projectId);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Scheduling board {ProjectId} failed", projectId);
                }
            });
        }
    }

    private async Task<int> MaxParallelAsync(string projectId)
    {
        var project = await store.GetProjectAsync(projectId);
        return Math.Clamp(project?.MaxParallelFeatures ?? 1, 1, Math.Max(1, boardOptions.CurrentValue.MaxParallelFeaturesLimit));
    }

    private async Task<IDisposable> LockMainAsync(string projectId, CancellationToken cancellationToken)
    {
        var gate = _mainLocks.GetOrAdd(projectId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        return new Releaser(gate);
    }

    private Task FinishAsync(string projectId, string featureId, FeatureStatus status, string? error) =>
        UpdateAsync(projectId, featureId, f =>
        {
            f.Status = status;
            f.Error = error;
            if (status == FeatureStatus.Completed) f.Stage = FeatureStage.Done;
            foreach (var task in f.Tasks.Where(t => t.State is TaskState.Doing or TaskState.ToDo))
            {
                task.State = status == FeatureStatus.Completed ? TaskState.Done : TaskState.Cancelled;
            }
        });

    private sealed class Releaser(SemaphoreSlim gate) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) gate.Release();
        }
    }

    /// <summary>A counting gate whose limit is read each time (the board setting can change while features wait).</summary>
    private sealed class ParallelSlots
    {
        private readonly object _lock = new();
        private int _running;
        private TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool TryEnter(int max)
        {
            lock (_lock)
            {
                if (_running >= max) return false;
                _running++;
                return true;
            }
        }

        public async Task EnterAsync(Func<Task<int>> max, CancellationToken cancellationToken)
        {
            while (true)
            {
                Task released;
                lock (_lock)
                {
                    released = _released.Task;
                }
                if (TryEnter(await max())) return;
                // Re-check now and then too, in case the limit was raised.
                await Task.WhenAny(released, Task.Delay(TimeSpan.FromSeconds(30), cancellationToken)).WaitAsync(cancellationToken);
            }
        }

        public void Exit()
        {
            TaskCompletionSource released;
            lock (_lock)
            {
                _running--;
                released = _released;
                _released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            released.TrySetResult();
        }
    }
}
