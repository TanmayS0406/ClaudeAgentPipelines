namespace ClaudeAgentPipelines.Board;

/// <summary>Tells open board pages that a feature on their board changed, so they can refresh their cards.</summary>
public sealed class BoardNotifier
{
    private readonly object _lock = new();
    private readonly Dictionary<string, (long Version, TaskCompletionSource Changed)> _boards = [];

    public long Version(string projectId)
    {
        lock (_lock)
        {
            return Get(projectId).Version;
        }
    }

    public void Notify(string projectId)
    {
        TaskCompletionSource changed;
        lock (_lock)
        {
            var board = Get(projectId);
            changed = board.Changed;
            _boards[projectId] = (board.Version + 1, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        }
        changed.TrySetResult();
    }

    /// <summary>Completes once the board's version is past <paramref name="version"/>.</summary>
    public Task WaitForChangeAsync(string projectId, long version, CancellationToken cancellationToken)
    {
        Task changed;
        lock (_lock)
        {
            var board = Get(projectId);
            if (board.Version > version) return Task.CompletedTask;
            changed = board.Changed.Task;
        }
        return changed.WaitAsync(cancellationToken);
    }

    // Called under _lock.
    private (long Version, TaskCompletionSource Changed) Get(string projectId)
    {
        if (!_boards.TryGetValue(projectId, out var board))
        {
            board = (0, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
            _boards[projectId] = board;
        }
        return board;
    }
}
