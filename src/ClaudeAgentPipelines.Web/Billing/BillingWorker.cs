using Microsoft.Extensions.Options;

namespace ClaudeAgentPipelines.Billing;

/// <summary>
/// Runs in the background: settles payments Cashfree has not reported yet (the customer may close the browser before
/// coming back) and charges every subscription when a new day starts.
/// </summary>
public sealed class BillingWorker(BillingService billing, IOptionsMonitor<BillingOptions> options, ILogger<BillingWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await billing.RunMaintenanceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Billing maintenance failed; will try again.");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(10, options.CurrentValue.WorkerIntervalSeconds)), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
