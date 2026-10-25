using System.Collections.Concurrent;
using Microsoft.Extensions.Options;

namespace ClaudeAgentPipelines.Pipelines;

/// <summary>Starts runs (pipelines and board features) in the background and keeps the active ones in memory.</summary>
public sealed class RunManager(
    PipelineRunner runner,
    RunRepository repository,
    IOptionsMonitor<ClaudeOptions> claudeOptions,
    IHostApplicationLifetime lifetime,
    ILogger<RunManager> logger)
{
    private readonly ConcurrentDictionary<string, PipelineRun> _active = new();

    public PipelineRun Start(Pipeline pipeline, string input, string startedBy)
    {
        if (pipeline.Agents.Count == 0) throw new InvalidOperationException("Add at least one agent before running the pipeline.");

        var run = new PipelineRun
        {
            PipelineId = pipeline.Id,
            PipelineName = pipeline.Name,
            Input = input,
            StartedBy = startedBy,
            Steps = pipeline.Agents.Select(a => new StepState
            {
                AgentId = a.Id,
                Name = a.Name,
                Model = PipelineRunner.EffectiveModel(a, claudeOptions.CurrentValue)
            }).ToList()
        };
        return Start(run, (r, token) => runner.RunAsync(pipeline, r, token));
    }

    /// <summary>
    /// Runs <paramref name="execute"/> in the background. The run completes when it returns, is cancelled when it throws
    /// <see cref="OperationCanceledException"/> after a cancel, and fails when it throws anything else.
    /// </summary>
    public PipelineRun Start(PipelineRun run, Func<PipelineRun, CancellationToken, Task> execute)
    {
        run.Status = RunStatus.Running;
        run.Cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.ApplicationStopping);
        _active[run.Id] = run;

        _ = Task.Run(() => ExecuteAsync(run, execute));
        return run;
    }

    public async Task<PipelineRun?> GetAsync(string id, CancellationToken cancellationToken = default) =>
        _active.TryGetValue(id, out var run) ? run : await repository.GetAsync(id, cancellationToken);

    /// <summary>Active runs first, then recently finished ones; newest first.</summary>
    public async Task<IReadOnlyList<PipelineRun>> ListAsync(int max, CancellationToken cancellationToken = default)
    {
        var saved = await repository.ListAsync(max, cancellationToken);
        return _active.Values.Select(r => r.Snapshot())
            .Concat(saved.Where(s => !_active.ContainsKey(s.Id)))
            .OrderByDescending(r => r.StartedAt)
            .Take(max)
            .ToList();
    }

    public bool Cancel(string id)
    {
        if (!_active.TryGetValue(id, out var run) || run.IsFinished) return false;
        run.Cancellation?.Cancel();
        return true;
    }

    public bool Delete(string id)
    {
        if (_active.ContainsKey(id)) return false;
        repository.Delete(id);
        return true;
    }

    private async Task ExecuteAsync(PipelineRun run, Func<PipelineRun, CancellationToken, Task> execute)
    {
        var token = run.Cancellation!.Token;
        try
        {
            await execute(run, token);
            run.Finish(RunStatus.Completed);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            run.Finish(RunStatus.Cancelled, "The run was cancelled.");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Run {RunId} of {Pipeline} failed", run.Id, run.PipelineName);
            run.Finish(RunStatus.Failed, ex.Message);
        }

        try
        {
            await repository.SaveAsync(run, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not save pipeline run {RunId}", run.Id);
        }
        finally
        {
            run.Cancellation.Dispose();
            run.Cancellation = null;
            _active.TryRemove(run.Id, out _);
        }
    }
}
