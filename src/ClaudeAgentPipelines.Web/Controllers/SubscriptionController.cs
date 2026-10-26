using System.Security.Claims;
using ClaudeAgentPipelines.Billing;
using ClaudeAgentPipelines.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace ClaudeAgentPipelines.Controllers;

/// <summary>The subscription: its users, its balance, adding money through Cashfree and adding users.</summary>
public sealed class SubscriptionController(BillingService billing, UserService users, IOptionsMonitor<CashfreeOptions> cashfree) : Controller
{
    private string UserId => User.FindFirstValue(ClaimTypes.NameIdentifier)!;

    [HttpGet]
    public async Task<IActionResult> Index(CancellationToken cancellationToken) =>
        View(new SubscriptionViewModel(await billing.GetSummaryAsync(UserId, cancellationToken), new AddUserForm(), cashfree.CurrentValue.IsConfigured, billing.DailyRate));

    [HttpGet]
    public async Task<IActionResult> Purchase(CancellationToken cancellationToken)
    {
        var user = await users.GetAsync(UserId, cancellationToken);
        var summary = await billing.GetSummaryAsync(UserId, cancellationToken);
        if (summary is { IsOwner: false })
        {
            TempData["Error"] = "Only the owner of the subscription can add money to it.";
            return RedirectToAction(nameof(Index));
        }
        return View(PurchaseModel(new PurchaseForm { Phone = user?.Phone ?? string.Empty, Email = user?.Email }, summary));
    }

    [HttpPost]
    public async Task<IActionResult> Purchase(PurchaseForm form, CancellationToken cancellationToken)
    {
        var summary = await billing.GetSummaryAsync(UserId, cancellationToken);
        if (!ModelState.IsValid) return View(PurchaseModel(form, summary));

        try
        {
            var user = (await users.GetAsync(UserId, cancellationToken))!;
            var baseUrl = string.IsNullOrWhiteSpace(cashfree.CurrentValue.ReturnBaseUrl)
                ? $"{Request.Scheme}://{Request.Host}"
                : cashfree.CurrentValue.ReturnBaseUrl.TrimEnd('/');
            var payment = await billing.StartPurchaseAsync(user, form.Days, form.Phone, form.Email,
                baseUrl + "/Subscription/Return?order_id={order_id}", cancellationToken);
            return RedirectToAction(nameof(Checkout), new { id = payment.Id });
        }
        catch (Exception ex) when (ex is BillingException or CashfreeException)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
            return View(PurchaseModel(form, summary));
        }
    }

    /// <summary>Opens the Cashfree checkout for a payment that is still waiting.</summary>
    [HttpGet]
    public async Task<IActionResult> Checkout(string id, CancellationToken cancellationToken)
    {
        var payment = await billing.GetPaymentAsync(UserId, id, cancellationToken);
        if (payment is null) return NotFound();
        if (payment.Status != PaymentStatus.Pending) return RedirectToAction(nameof(Return), new { order_id = payment.CashfreeOrderId });

        return View(new CheckoutViewModel(payment, cashfree.CurrentValue.CheckoutMode));
    }

    /// <summary>Cashfree sends the customer back here. The payment is confirmed with Cashfree, never trusted from the address.</summary>
    [HttpGet]
    public async Task<IActionResult> Return([FromQuery(Name = "order_id")] string? orderId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(orderId)) return RedirectToAction("Index", "Billing");

        try
        {
            var payment = await billing.SettleAsync(orderId, cancellationToken);
            switch (payment?.Status)
            {
                case PaymentStatus.Paid:
                    TempData["Message"] = $"Payment of {Money.Format(payment.Amount)} received. Your subscription is active.";
                    break;
                case PaymentStatus.Pending:
                    TempData["Error"] = "The payment has not been confirmed yet. If you paid, it is added as soon as Cashfree confirms it.";
                    break;
                case PaymentStatus.Failed:
                    TempData["Error"] = payment.Detail ?? "The payment did not go through.";
                    break;
            }
        }
        catch (Exception ex) when (ex is BillingException or CashfreeException)
        {
            TempData["Error"] = ex.Message;
        }
        return RedirectToAction("Index", "Billing");
    }

    [HttpPost]
    public async Task<IActionResult> AddUser(AddUserForm form, CancellationToken cancellationToken)
    {
        var owner = (await users.GetAsync(UserId, cancellationToken))!;
        if (ModelState.IsValid)
        {
            try
            {
                var added = await billing.AddUserAsync(owner, form.UserName, form.DisplayName, form.Password, form.Email, cancellationToken);
                TempData["Message"] = $"{added.DisplayName} can now sign in as \"{added.UserName}\". They are charged {Money.Format(billing.DailyRate)} a day.";
                return RedirectToAction(nameof(Index));
            }
            catch (BillingException ex)
            {
                ModelState.AddModelError(string.Empty, ex.Message);
            }
        }

        form.Password = string.Empty;
        return View(nameof(Index), new SubscriptionViewModel(await billing.GetSummaryAsync(UserId, cancellationToken), form, cashfree.CurrentValue.IsConfigured, billing.DailyRate));
    }

    [HttpPost]
    public async Task<IActionResult> RemoveUser(string id, CancellationToken cancellationToken)
    {
        try
        {
            await billing.RemoveUserAsync((await users.GetAsync(UserId, cancellationToken))!, id, cancellationToken);
            TempData["Message"] = "The user was removed from the subscription. They are not charged from tomorrow.";
        }
        catch (BillingException ex)
        {
            TempData["Error"] = ex.Message;
        }
        return RedirectToAction(nameof(Index));
    }

    private PurchaseViewModel PurchaseModel(PurchaseForm form, SubscriptionSummary? summary) =>
        new(form, summary?.Members.Count ?? 1, billing.DailyRate, cashfree.CurrentValue.IsConfigured, summary is null);
}
