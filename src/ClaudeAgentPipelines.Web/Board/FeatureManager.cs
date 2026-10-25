using System.Collections.Concurrent;
using ClaudeAgentPipelines.Azure;
using ClaudeAgentPipelines.Pipelines;
using Microsoft.Extensions.Options;

namespace ClaudeAgentPipelines.Board;

/// <summary>
/// Starts features on the agent team and keeps their board state up to date. Features of one project run one at a
/// time because they share the workspace; the others wait in the queue.
/// </summary>
public sealed class FeatureManager(
    BoardStore store,
    RunManager runs,
    FeatureOrchestrator orchestrator,
    BoardNotifier notifier,
    AzureAccountService azure,
    IOptionsMonitor<ClaudeOptions> claudeOptions,
    ILogger<FeatureManager> logger)
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _workspaceLocks = new();

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
        });

        runs.Start(run, (r, token) => ExecuteAsync(project.Id, feature.Id, r, token));
        return null;
    }

    public async Task<bool> CancelAsync(string projectId, string featureId, CancellationToken cancellationToken)
    {
        var feature = await store.GetFeatureAsync(projectId, featureId, cancellationToken);
        return feature is { IsActive: true, CurrentRunId: { } runId } && runs.Cancel(runId);
    }

    public Task<Feature?> UpdateAsync(string projectId, string featureId, Action<Feature> change) =>
        UpdateAsync(projectId, featureId, change, CancellationToken.None);

    public async Task<Feature?> UpdateAsync(string projectId, string featureId, Action<Feature> change, CancellationToken cancellationToken)
    {
        var feature = await store.UpdateFeatureAsync(projectId, featureId, change, cancellationToken);
        notifier.Notify(projectId);
        return feature;
    }

    /// <summary>Refreshes open board pages after a feature was added or removed.</summary>
    public void BoardChanged(string projectId) => notifier.Notify(projectId);

    /// <summary>Features that were in progress when the app stopped can't continue: their agents are gone.</summary>
    public async Task RecoverAsync()
    {
        foreach (var project in await store.ListProjectsAsync())
        {
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

    private async Task ExecuteAsync(string projectId, string featureId, PipelineRun run, CancellationToken cancellationToken)
    {
        var workspaceLock = _workspaceLocks.GetOrAdd(projectId, _ => new SemaphoreSlim(1, 1));
        try
        {
            if (workspaceLock.CurrentCount == 0) run.SetStage("Queued: waiting for the feature in progress to finish");
            await workspaceLock.WaitAsync(cancellationToken);
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

            await orchestrator.RunAsync(project, feature, run, change => UpdateAsync(projectId, featureId, change), cancellationToken);

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
            workspaceLock.Release();
        }
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
}
