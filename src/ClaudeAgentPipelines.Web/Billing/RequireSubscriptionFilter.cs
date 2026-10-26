using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Options;

namespace ClaudeAgentPipelines.Billing;

/// <summary>
/// Sends signed-in users without an active subscription to the subscription page. The account, subscription and
/// billing pages stay open so they can buy one.
/// </summary>
public sealed class RequireSubscriptionFilter(BillingService billing, IOptionsMonitor<BillingOptions> options) : IAsyncActionFilter
{
    private static readonly string[] OpenControllers = ["Account", "Subscription", "Billing", "Home"];

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var userId = context.HttpContext.User.FindFirstValue(ClaimTypes.NameIdentifier);
        var controller = context.RouteData.Values["controller"] as string;

        if (!options.CurrentValue.RequireSubscription
            || userId is null
            || OpenControllers.Contains(controller, StringComparer.OrdinalIgnoreCase)
            || await billing.HasActiveSubscriptionAsync(userId, context.HttpContext.RequestAborted))
        {
            await next();
            return;
        }

        if (context.Controller is Controller { TempData: { } tempData })
        {
            tempData["Error"] = "You need an active subscription to use the app. Add money below to start.";
        }
        context.Result = new RedirectToActionResult("Index", "Subscription", null);
    }
}
