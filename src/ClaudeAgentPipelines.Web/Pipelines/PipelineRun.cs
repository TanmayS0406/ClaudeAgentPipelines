using System.Text;
using System.Text.Json.Serialization;

namespace ClaudeAgentPipelines.Pipelines;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RunStatus { Running, Completed, Failed, Cancelled }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum StepStatus { Pending, Running, Completed, Failed, Cancelled }

/// <summary>One agent's part of a run.</summary>
public sealed class StepState
{
    private readonly StringBuilder _output = new();

    public string AgentId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public StepStatus Status { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public long? InputTokens { get; set; }
    public long? OutputTokens { get; set; }
    public string? Error { get; set; }
    public List<string> Activity { get; set; } = [];

    public string Output
    {
        get => _output.ToString();
        set => _output.Clear().Append(value);
    }

    internal void AppendOutput(string text) => _output.Append(text);
}

/// <summary>Something that happened during a run, streamed to the browser in order.</summary>
public sealed record RunEvent(long Seq, string Type, string? AgentId = null, string? Text = null, object? Data = null);

/// <summary>
/// A pipeline run. Thread-safe: the runner changes it from a background task while requests read snapshots and
/// stream its events. Events are only kept in memory; finished runs are saved without them.
/// </summary>
public sealed class PipelineRun
{
    private readonly object _lock = new();
    private readonly List<RunEvent> _events = [];
    private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public string Id { get; set; } = Ids.New();
    public string PipelineId { get; set; } = string.Empty;
    public string PipelineName { get; set; } = string.Empty;

    /// <summary>Set when the run builds a board feature instead of running a pipeline.</summary>
    public string? ProjectId { get; set; }
    public string? FeatureId { get; set; }

    /// <summary>What the run is doing right now, for example "Testing (round 2)".</summary>
    public string? Stage { get; set; }

    public string Input { get; set; } = string.Empty;
    public string StartedBy { get; set; } = string.Empty;
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }
    public RunStatus Status { get; set; }
    public string? Error { get; set; }
    public List<StepState> Steps { get; set; } = [];

    /// <summary>The last step's output once the run has completed.</summary>
    public string? FinalOutput => Status == RunStatus.Completed ? Steps.LastOrDefault()?.Output : null;

    [JsonIgnore]
    public bool IsFinished => Status != RunStatus.Running;

    [JsonIgnore]
    internal CancellationTokenSource? Cancellation { get; set; }

    /// <summary>Sequence number of the last event, so a page rendered from a snapshot can stream what follows.</summary>
    [JsonIgnore]
    public long LastSeq { get; private set; }

    /// <summary>A consistent copy for rendering or saving.</summary>
    public PipelineRun Snapshot()
    {
        lock (_lock)
        {
            var copy = new PipelineRun
            {
                Id = Id,
                PipelineId = PipelineId,
                PipelineName = PipelineName,
                ProjectId = ProjectId,
                FeatureId = FeatureId,
                Stage = Stage,
                Input = Input,
                StartedBy = StartedBy,
                StartedAt = StartedAt,
                CompletedAt = CompletedAt,
                Status = Status,
                Error = Error,
                Steps = Steps.Select(s => new StepState
                {
                    AgentId = s.AgentId,
                    Name = s.Name,
                    Model = s.Model,
                    Status = s.Status,
                    StartedAt = s.StartedAt,
                    CompletedAt = s.CompletedAt,
                    InputTokens = s.InputTokens,
                    OutputTokens = s.OutputTokens,
                    Error = s.Error,
                    Activity = [.. s.Activity],
                    Output = s.Output
                }).ToList()
            };
            copy.LastSeq = LastSeq;
            return copy;
        }
    }

    public (IReadOnlyList<RunEvent> Events, bool Finished) GetEventsAfter(long seq)
    {
        lock (_lock)
        {
            return (_events.Where(e => e.Seq > seq).ToList(), IsFinished);
        }
    }

    /// <summary>Completes when there are events after <paramref name="seq"/> or the run has finished.</summary>
    public Task WaitForChangeAsync(long seq, CancellationToken cancellationToken)
    {
        Task changed;
        lock (_lock)
        {
            if (LastSeq > seq || IsFinished) return Task.CompletedTask;
            changed = _changed.Task;
        }
        return changed.WaitAsync(cancellationToken);
    }

    /// <summary>Adds a step while the run is going (for example a fix round after failed tests).</summary>
    internal void AddStep(StepState step)
    {
        lock (_lock)
        {
            if (IsFinished || Steps.Any(s => s.AgentId == step.AgentId)) return;
            Steps.Add(step);
            Publish(new RunEvent(0, "stepAdded", step.AgentId, step.Name, new { model = step.Model }));
        }
    }

    internal void SetStage(string stage)
    {
        lock (_lock)
        {
            if (IsFinished) return;
            Stage = stage;
            Publish(new RunEvent(0, "stage", Text: stage));
        }
    }

    /// <summary>Output of a step so far (empty for unknown steps).</summary>
    public string StepOutput(string agentId)
    {
        lock (_lock)
        {
            return Steps.FirstOrDefault(s => s.AgentId == agentId)?.Output ?? string.Empty;
        }
    }

    public StepStatus? StepStatusOf(string agentId)
    {
        lock (_lock)
        {
            return Steps.FirstOrDefault(s => s.AgentId == agentId)?.Status;
        }
    }

    internal void StepStarted(string agentId) => UpdateStep(agentId, step =>
    {
        if (step.Status != StepStatus.Pending) return null;
        step.Status = StepStatus.Running;
        step.StartedAt = DateTimeOffset.UtcNow;
        return new RunEvent(0, "stepStarted", agentId);
    });

    internal void StepText(string agentId, string text) => UpdateStep(agentId, step =>
    {
        step.AppendOutput(text);
        return new RunEvent(0, "text", agentId, text);
    });

    internal void StepActivity(string agentId, string text) => UpdateStep(agentId, step =>
    {
        step.Activity.Add(text);
        return new RunEvent(0, "activity", agentId, text);
    });

    internal void StepUsage(string agentId, long? inputTokens, long? outputTokens) => UpdateStep(agentId, step =>
    {
        step.InputTokens = (step.InputTokens ?? 0) + (inputTokens ?? 0);
        step.OutputTokens = (step.OutputTokens ?? 0) + (outputTokens ?? 0);
        return new RunEvent(0, "usage", agentId, Data: new { inputTokens = step.InputTokens, outputTokens = step.OutputTokens });
    });

    internal void StepCompleted(string agentId) => UpdateStep(agentId, step =>
    {
        if (step.Status != StepStatus.Running) return null;
        step.Status = StepStatus.Completed;
        step.CompletedAt = DateTimeOffset.UtcNow;
        return new RunEvent(0, "stepCompleted", agentId);
    });

    internal void StepFailed(string agentId, string error) => UpdateStep(agentId, step =>
    {
        step.Status = StepStatus.Failed;
        step.CompletedAt = DateTimeOffset.UtcNow;
        step.Error = error;
        return new RunEvent(0, "stepFailed", agentId, error);
    });

    /// <summary>Marks the run finished; steps still pending or running end with the same status (or Cancelled).</summary>
    internal void Finish(RunStatus status, string? error = null)
    {
        lock (_lock)
        {
            if (IsFinished) return;

            foreach (var step in Steps.Where(s => s.Status is StepStatus.Pending or StepStatus.Running))
            {
                var wasRunning = step.Status == StepStatus.Running;
                step.Status = status == RunStatus.Completed ? StepStatus.Completed
                            : wasRunning && status == RunStatus.Failed ? StepStatus.Failed
                            : StepStatus.Cancelled;
                if (wasRunning) step.CompletedAt = DateTimeOffset.UtcNow;
            }

            Status = status;
            Error = error;
            CompletedAt = DateTimeOffset.UtcNow;
            Publish(new RunEvent(0, "runFinished", Text: error, Data: new { status = status.ToString() }));
        }
    }

    private void UpdateStep(string agentId, Func<StepState, RunEvent?> change)
    {
        lock (_lock)
        {
            if (IsFinished) return;
            var step = Steps.FirstOrDefault(s => s.AgentId == agentId);
            if (step is null) return;

            var evt = change(step);
            if (evt is not null) Publish(evt);
        }
    }

    // Called under _lock.
    private void Publish(RunEvent evt)
    {
        LastSeq++;
        _events.Add(evt with { Seq = LastSeq });
        var changed = _changed;
        _changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        changed.TrySetResult();
    }
}
