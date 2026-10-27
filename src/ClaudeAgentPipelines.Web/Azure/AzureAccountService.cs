using System.Diagnostics;

namespace ClaudeAgentPipelines.Azure;

/// <summary>
/// The Azure account signed in on this computer, as reported by the Azure CLI. The status is cached because
/// <c>az</c> takes a few seconds to start.
/// </summary>
public sealed class AzureAccountService(AzureCli cli, ILogger<AzureAccountService> logger)
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan StatusTimeout = TimeSpan.FromSeconds(45);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private AzureAuthStatus? _cached;
    private DateTime _cachedAtUtc;
    private Process? _loginConsole;

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

    public async Task<AzureAuthStatus> GetStatusAsync(bool refresh = false, CancellationToken cancellationToken = default)
    {
        var cached = _cached;
        if (!refresh && cached is not null && DateTime.UtcNow - _cachedAtUtc < CacheFor) return cached;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!refresh && _cached is not null && DateTime.UtcNow - _cachedAtUtc < CacheFor) return _cached;

            AzureAuthStatus status;
            try
            {
                using var timeout = new CancellationTokenSource(StatusTimeout);
                status = await cli.GetAuthStatusAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                status = new AzureAuthStatus(cli.Locate() is not null, false, Detail: "Timed out checking the Azure sign-in.");
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

    public async Task<IReadOnlyList<AzureSubscription>> ListSubscriptionsAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(StatusTimeout);
        try
        {
            return await cli.ListSubscriptionsAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return [];
        }
    }

    /// <summary>Makes a subscription the Azure CLI's default. Affects every tool that uses the CLI on this computer.</summary>
    public async Task<(bool Ok, string? Error)> SetDefaultSubscriptionAsync(string subscriptionId, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(StatusTimeout);
        var result = await cli.SetDefaultSubscriptionAsync(subscriptionId, timeout.Token);
        _cached = null;
        return result;
    }

    /// <summary>
    /// Opens the Azure sign-in console on this computer, optionally for one tenant (directory). Returns false when the
    /// CLI is not installed.
    /// </summary>
    public bool StartLogin(string? tenant = null)
    {
        if (IsLoginInProgress) return true;

        var cliPath = cli.Locate();
        if (cliPath is null) return false;

        logger.LogInformation("Starting 'az login' with {CliPath}", cliPath);
        _loginConsole?.Dispose();
        _loginConsole = cli.StartLogin(cliPath, tenant);
        _cached = null;
        return true;
    }

    /// <summary>Signs the Azure CLI on this computer out (affects every tool that uses it).</summary>
    public async Task LogoutAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(StatusTimeout);
        await cli.LogoutAsync(timeout.Token);
        _cached = null;
    }
}
