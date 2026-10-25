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

    [HttpGet]
    public async Task<IActionResult> Subscriptions(CancellationToken cancellationToken) =>
        Json(await azure.ListSubscriptionsAsync(cancellationToken));

    [HttpPost]
    public IActionResult StartLogin()
    {
        if (!azure.StartLogin())
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
