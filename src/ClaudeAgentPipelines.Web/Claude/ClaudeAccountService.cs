using System.Diagnostics;

namespace ClaudeAgentPipelines.Claude;

/// <summary>
/// The Claude account signed in on this computer, as reported by the Claude Code CLI.
/// The status is cached briefly because every authenticated request checks it.
/// </summary>
public sealed class ClaudeAccountService(ClaudeCli cli, ILogger<ClaudeAccountService> logger)
{
    /// <summary>Claim holding <see cref="ClaudeAuthStatus.Account"/> in the app's sign-in cookie.</summary>
    public const string AccountClaim = "claude:account";

    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan StatusTimeout = TimeSpan.FromSeconds(30);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private ClaudeAuthStatus? _cached;
    private DateTime _cachedAtUtc;
    private Process? _loginConsole;

    /// <summary>True while the <c>claude auth login</c> console window started by <see cref="StartLogin"/> is open.</summary>
    public bool IsLoginInProgress
    {
        get
        {
            try
            {
                return _loginConsole is { HasExited: false };
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }
    }

    public async Task<ClaudeAuthStatus> GetStatusAsync(bool refresh = false, CancellationToken cancellationToken = default)
    {
        var cached = _cached;
        if (!refresh && cached is not null && DateTime.UtcNow - _cachedAtUtc < CacheFor) return cached;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            // Another request may have refreshed it while this one waited.
            if (!refresh && _cached is not null && DateTime.UtcNow - _cachedAtUtc < CacheFor) return _cached;

            ClaudeAuthStatus status;
            try
            {
                using var timeout = new CancellationTokenSource(StatusTimeout);
                status = await cli.GetAuthStatusAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                status = new ClaudeAuthStatus(cli.Locate() is not null, false, Detail: "Timed out checking Claude sign-in.");
            }

            _cached = status;
            _cachedAtUtc = DateTime.UtcNow;
            return status;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Opens the Claude sign-in console on this computer. Returns false when the CLI is not installed.</summary>
    public bool StartLogin()
    {
        if (IsLoginInProgress) return true;

        var cliPath = cli.Locate();
        if (cliPath is null) return false;

        logger.LogInformation("Starting 'claude auth login' with {CliPath}", cliPath);
        _loginConsole?.Dispose();
        _loginConsole = cli.StartLogin(cliPath);
        _cached = null;
        return true;
    }

    /// <summary>Signs Claude Code on this computer out (affects every app that uses the CLI).</summary>
    public async Task LogoutAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(StatusTimeout);
        await cli.LogoutAsync(timeout.Token);
        _cached = null;
    }
}
