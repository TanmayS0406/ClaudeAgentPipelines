using ClaudeAgentPipelines.Azure;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeAgentPipelines.Controllers;

/// <summary>
/// "Sign in to Azure": like Claude, the Azure CLI on this computer does the sign-in (<c>az login</c>), and the DevOps
/// agent's <c>az</c> commands use it. The app never sees Azure credentials.
/// </summary>
public sealed class AzureController(AzureAccountService azure) : Controller
{
    public IActionResult Index() => View();

    [HttpGet]
    public async Task<IActionResult> Status(bool refresh, CancellationToken cancellationToken)
    {
        var status = await azure.GetStatusAsync(refresh, cancellationToken);
        return Json(new
        {
            cliFound = status.CliFound,
            loggedIn = status.LoggedIn,
            user = status.User,
            subscriptionId = status.SubscriptionId,
            subscriptionName = status.SubscriptionName,
            tenantId = status.TenantId,
            detail = status.Detail,
            loginInProgress = azure.IsLoginInProgress
        });
    }

    /// <summary>All subscriptions the signed-in account can see, each with its state (only Enabled ones can be used).</summary>
    [HttpGet]
    public async Task<IActionResult> Subscriptions(CancellationToken cancellationToken) =>
        Json((await azure.ListSubscriptionsAsync(cancellationToken))
            .Select(s => new { s.Id, s.Name, s.TenantId, s.IsDefault, s.State, isEnabled = s.IsEnabled }));

    /// <summary>Makes a subscription the Azure CLI's default.</summary>
    [HttpPost]
    public async Task<IActionResult> SetSubscription(string id, CancellationToken cancellationToken)
    {
        var (ok, error) = await azure.SetDefaultSubscriptionAsync(id, cancellationToken);
        return ok ? Json(new { ok = true }) : BadRequest(new { error });
    }

    /// <summary>Opens <c>az login</c>; with a tenant, for that directory (to see the subscriptions in it).</summary>
    [HttpPost]
    public IActionResult StartLogin(string? tenant)
    {
        if (!string.IsNullOrWhiteSpace(tenant) && !AzureCli.IsValidTenant(tenant.Trim()))
        {
            return BadRequest(new { error = "Enter a tenant ID (a GUID) or a domain name such as contoso.onmicrosoft.com." });
        }
        if (!azure.StartLogin(string.IsNullOrWhiteSpace(tenant) ? null : tenant.Trim()))
        {
            return BadRequest(new { error = "Azure CLI not found. Install it (https://aka.ms/installazurecliwindows), or set Azure:CliPath in appsettings.json." });
        }
        return Json(new { started = true });
    }

    [HttpPost]
    public async Task<IActionResult> Logout(CancellationToken cancellationToken)
    {
        await azure.LogoutAsync(cancellationToken);
        TempData["Message"] = "The Azure CLI on this computer is signed out.";
        return RedirectToAction(nameof(Index));
    }
}
