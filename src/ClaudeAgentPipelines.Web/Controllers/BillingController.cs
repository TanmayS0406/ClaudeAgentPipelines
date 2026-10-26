using System.Security.Claims;
using ClaudeAgentPipelines.Billing;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeAgentPipelines.Controllers;

/// <summary>What the subscription has been paid and charged.</summary>
public sealed class BillingController(BillingService billing) : Controller
{
    [HttpGet]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        var overview = await billing.GetOverviewAsync(User.FindFirstValue(ClaimTypes.NameIdentifier)!, cancellationToken);
        return overview is null ? RedirectToAction("Index", "Subscription") : View(overview);
    }
}
