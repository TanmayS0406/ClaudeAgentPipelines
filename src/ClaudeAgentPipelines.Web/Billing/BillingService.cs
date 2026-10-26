using System.Globalization;
using System.Text.RegularExpressions;
using ClaudeAgentPipelines.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ClaudeAgentPipelines.Billing;

/// <summary>A problem the user can fix, such as "add money first". The message is shown as is.</summary>
public sealed class BillingException(string message) : Exception(message);

public static class Money
{
    private static readonly CultureInfo India = CultureInfo.GetCultureInfo("en-IN");

    public static string Format(decimal amount) => "₹" + amount.ToString("N2", India);
}

/// <summary>Times as shown to users: India time, which is also how billing days are cut.</summary>
public static class IndiaTime
{
    private static readonly TimeSpan Offset = TimeSpan.FromHours(5.5);

    public static string Format(DateTimeOffset time) => time.ToOffset(Offset).ToString("dd MMM yyyy, HH:mm", CultureInfo.InvariantCulture);

    public static string Format(DateOnly date) => date.ToString("ddd, dd MMM yyyy", CultureInfo.InvariantCulture);
}

public sealed record SubscriptionSummary(
    Subscription Subscription,
    IReadOnlyList<SubscriptionMember> Members,
    decimal Balance,
    decimal DailyRate,
    decimal DailyCost,
    int DaysLeft,
    bool IsOwner);

public sealed record DayCharge(DateOnly Date, IReadOnlyList<string> Users, decimal Amount);

public sealed record BillingOverview(
    SubscriptionSummary Summary,
    decimal TotalPaid,
    decimal TotalSpent,
    IReadOnlyList<Payment> Payments,
    IReadOnlyList<DayCharge> Days);

/// <summary>
/// Pay-as-you-go billing. A subscription holds a prepaid balance: money added through Cashfree (payments) minus the
/// daily charges, one per user per day at <see cref="BillingOptions.DailyRatePerUser"/>. Days are India time.
/// Everything that changes money or members runs one at a time (<c>_gate</c>); the unique (subscription, user, day)
/// index on charges guards against a double charge even so.
/// </summary>
public sealed partial class BillingService(
    IDbContextFactory<AppDbContext> databases,
    UserService users,
    CashfreeClient cashfree,
    IOptionsMonitor<BillingOptions> options,
    ILogger<BillingService> logger)
{
    private static readonly TimeSpan India = TimeSpan.FromHours(5.5);
    private const int MaxDays = 365;

    private readonly SemaphoreSlim _gate = new(1, 1);

    [GeneratedRegex(@"^\d{10}$")]
    private static partial Regex PhonePattern();

    public decimal DailyRate => options.CurrentValue.DailyRatePerUser;

    /// <summary>Today's date in India. Charges are per this day.</summary>
    public static DateOnly Today => DateOnly.FromDateTime(DateTimeOffset.UtcNow.ToOffset(India).DateTime);

    // ---- reading

    /// <summary>The subscription the user belongs to, brought up to date for today. Null when there is none.</summary>
    public async Task<SubscriptionSummary?> GetSummaryAsync(string userId, CancellationToken cancellationToken = default)
    {
        var subscription = await FindAsync(userId, cancellationToken);
        if (subscription is null) return null;

        if (subscription.Status != SubscriptionStatus.Inactive && subscription.BilledDate != Today)
        {
            await EnsureChargedAsync(subscription.Id, cancellationToken);
            subscription = await FindAsync(userId, cancellationToken) ?? subscription;
        }

        await using var db = await databases.CreateDbContextAsync(cancellationToken);
        return Summarize(subscription, await BalanceAsync(db, subscription.Id, cancellationToken), userId);
    }

    /// <summary>True when the user's subscription is active, that is every member is paid up for today.</summary>
    public async Task<bool> HasActiveSubscriptionAsync(string userId, CancellationToken cancellationToken = default) =>
        (await GetSummaryAsync(userId, cancellationToken))?.Subscription.Status == SubscriptionStatus.Active;

    public async Task<BillingOverview?> GetOverviewAsync(string userId, CancellationToken cancellationToken = default)
    {
        var summary = await GetSummaryAsync(userId, cancellationToken);
        if (summary is null) return null;

        await using var db = await databases.CreateDbContextAsync(cancellationToken);
        var id = summary.Subscription.Id;
        var paid = await db.Payments.Where(p => p.SubscriptionId == id && p.Status == PaymentStatus.Paid)
            .SumAsync(p => (decimal?)p.Amount, cancellationToken) ?? 0;
        var spent = await db.UsageCharges.Where(c => c.SubscriptionId == id).SumAsync(c => (decimal?)c.Amount, cancellationToken) ?? 0;
        var payments = await db.Payments.AsNoTracking().Where(p => p.SubscriptionId == id)
            .OrderByDescending(p => p.CreatedAt).Take(100).ToListAsync(cancellationToken);

        var since = Today.AddDays(-60);
        var charges = await db.UsageCharges.AsNoTracking().Where(c => c.SubscriptionId == id && c.Date >= since)
            .ToListAsync(cancellationToken);
        var days = charges.GroupBy(c => c.Date)
            .OrderByDescending(g => g.Key)
            .Select(g => new DayCharge(g.Key, g.Select(c => c.UserName).Order().ToList(), g.Sum(c => c.Amount)))
            .ToList();

        return new BillingOverview(summary, paid, spent, payments, days);
    }

    public async Task<Payment?> GetPaymentAsync(string userId, string paymentId, CancellationToken cancellationToken = default)
    {
        if (!Pipelines.Ids.IsValid(paymentId)) return null;
        await using var db = await databases.CreateDbContextAsync(cancellationToken);
        return await db.Payments.AsNoTracking().FirstOrDefaultAsync(p => p.Id == paymentId
            && db.SubscriptionMembers.Any(m => m.UserId == userId && m.SubscriptionId == p.SubscriptionId), cancellationToken);
    }

    // ---- paying

    /// <summary>
    /// Starts a payment that adds <paramref name="days"/> of use for the current number of users. Creates the
    /// subscription (owned by <paramref name="user"/>) on the first purchase. Returns the pending payment to open in the checkout.
    /// </summary>
    public async Task<Payment> StartPurchaseAsync(AppUser user, int days, string? phone, string? email, string? returnUrl,
                                                  CancellationToken cancellationToken = default)
    {
        if (days is < 1 or > MaxDays) throw new BillingException($"Choose between 1 and {MaxDays} days.");
        phone = phone?.Trim() ?? user.Phone;
        if (phone is null || !PhonePattern().IsMatch(phone)) throw new BillingException("Enter a 10-digit mobile number: Cashfree needs it to take the payment.");
        email = string.IsNullOrWhiteSpace(email) ? user.Email : email.Trim();

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var db = await databases.CreateDbContextAsync(cancellationToken);
            var subscription = await LoadAsync(db, user.Id, cancellationToken);
            if (subscription is not null && subscription.OwnerId != user.Id)
            {
                throw new BillingException("Only the owner of the subscription can add money to it.");
            }

            var isNew = subscription is null;
            subscription ??= new Subscription { Name = user.DisplayName + "'s subscription", OwnerId = user.Id };
            var members = isNew ? 1 : subscription.Members.Count;
            var amount = days * members * DailyRate;

            var orderId = "cap_" + Pipelines.Ids.New();
            var order = await cashfree.CreateOrderAsync(new CashfreeOrderRequest(
                orderId, amount, user.Id, user.DisplayName, phone, email, returnUrl,
                $"{days} day(s) for {members} user(s)", DateTimeOffset.UtcNow.AddHours(1)), cancellationToken);
            if (string.IsNullOrEmpty(order.PaymentSessionId)) throw new CashfreeException("Cashfree did not return a payment session.");

            if (isNew)
            {
                subscription.Members.Add(new SubscriptionMember { UserId = user.Id, SubscriptionId = subscription.Id });
                db.Subscriptions.Add(subscription);
            }
            var payment = new Payment
            {
                SubscriptionId = subscription.Id,
                CashfreeOrderId = orderId,
                PaymentSessionId = order.PaymentSessionId,
                Amount = amount,
                Days = days,
                Users = members,
                CreatedByUserId = user.Id
            };
            db.Payments.Add(payment);
            await db.SaveChangesAsync(cancellationToken);

            await users.SaveContactAsync(user.Id, email, phone, cancellationToken);
            return payment;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Asks Cashfree what became of the order. A paid order adds its amount to the balance (once) and the subscription
    /// is charged for today. Safe to call again and again.
    /// </summary>
    public async Task<Payment?> SettleAsync(string orderId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var db = await databases.CreateDbContextAsync(cancellationToken);
            var payment = await db.Payments.FirstOrDefaultAsync(p => p.CashfreeOrderId == orderId, cancellationToken);
            if (payment is null || payment.Status != PaymentStatus.Pending) return payment;

            var order = await cashfree.GetOrderAsync(orderId, cancellationToken);
            switch (order.Status.ToUpperInvariant())
            {
                case "PAID" when order.Amount != payment.Amount:
                    payment.Status = PaymentStatus.Failed;
                    payment.Detail = $"Cashfree reports {order.Amount} for an order of {payment.Amount}; not credited.";
                    logger.LogError("Order {OrderId}: amount mismatch ({Reported} vs {Expected})", orderId, order.Amount, payment.Amount);
                    break;

                case "PAID":
                    payment.Status = PaymentStatus.Paid;
                    payment.PaidAt = DateTimeOffset.UtcNow;
                    var subscription = await db.Subscriptions.FirstAsync(s => s.Id == payment.SubscriptionId, cancellationToken);
                    if (subscription.Status == SubscriptionStatus.Inactive) subscription.Status = SubscriptionStatus.Suspended;
                    await db.SaveChangesAsync(cancellationToken);
                    await ChargeAsync(db, payment.SubscriptionId, cancellationToken);
                    break;

                case "EXPIRED" or "TERMINATED" or "TERMINATION_REQUESTED":
                    payment.Status = PaymentStatus.Failed;
                    payment.Detail = "The payment was not completed (" + order.Status.ToLowerInvariant() + ").";
                    break;
            }
            await db.SaveChangesAsync(cancellationToken);
            return payment;
        }
        finally
        {
            _gate.Release();
        }
    }

    // ---- charging

    /// <summary>Charges every member for today if that has not happened yet; suspends the subscription when the balance is short.</summary>
    public async Task EnsureChargedAsync(string subscriptionId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var db = await databases.CreateDbContextAsync(cancellationToken);
            await ChargeAsync(db, subscriptionId, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>One pass of the background worker: settle payments that are waiting, then charge for the new day.</summary>
    public async Task RunMaintenanceAsync(CancellationToken cancellationToken)
    {
        await using var db = await databases.CreateDbContextAsync(cancellationToken);

        if (cashfree.IsConfigured)
        {
            var cutoff = DateTimeOffset.UtcNow.AddSeconds(-20);
            var pending = await db.Payments.Where(p => p.Status == PaymentStatus.Pending && p.CreatedAt < cutoff)
                .Select(p => p.CashfreeOrderId).ToListAsync(cancellationToken);
            foreach (var orderId in pending)
            {
                try
                {
                    await SettleAsync(orderId, cancellationToken);
                }
                catch (CashfreeException ex)
                {
                    logger.LogWarning("Could not check order {OrderId}: {Message}", orderId, ex.Message);
                }
            }
        }

        var today = Today;
        var due = await db.Subscriptions.Where(s => s.Status != SubscriptionStatus.Inactive && s.BilledDate != today)
            .Select(s => s.Id).ToListAsync(cancellationToken);
        foreach (var id in due) await EnsureChargedAsync(id, cancellationToken);
    }

    // ---- members

    /// <summary>Creates a new user and puts them on the owner's subscription. They are charged for today straight away.</summary>
    public async Task<AppUser> AddUserAsync(AppUser owner, string? userName, string? displayName, string? password, string? email,
                                            CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var db = await databases.CreateDbContextAsync(cancellationToken);
            var subscription = await LoadAsync(db, owner.Id, cancellationToken)
                               ?? throw new BillingException("Buy a subscription first.");
            if (subscription.OwnerId != owner.Id) throw new BillingException("Only the owner of the subscription can add users.");

            await ChargeAsync(db, subscription.Id, cancellationToken);
            if (subscription.Status != SubscriptionStatus.Active)
            {
                throw new BillingException("The subscription is not active. Add money first, then add users.");
            }
            var balance = await BalanceAsync(db, subscription.Id, cancellationToken);
            if (balance < DailyRate)
            {
                throw new BillingException(
                    $"The balance ({Money.Format(balance)}) does not cover another user for today ({Money.Format(DailyRate)}). Add money first.");
            }

            var (user, error) = users.Build(userName, displayName, password, email);
            if (user is null) throw new BillingException(error!);
            if (await db.Users.AnyAsync(u => u.UserName == user.UserName, cancellationToken))
            {
                throw new BillingException($"The user name \"{user.UserName}\" is already taken.");
            }

            db.Users.Add(user);
            subscription.Members.Add(new SubscriptionMember { UserId = user.Id, SubscriptionId = subscription.Id });
            db.UsageCharges.Add(NewCharge(subscription.Id, user, Today));
            await db.SaveChangesAsync(cancellationToken);
            return user;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Takes a user off the subscription. They are not charged from tomorrow; today's charge stays.</summary>
    public async Task RemoveUserAsync(AppUser owner, string userId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var db = await databases.CreateDbContextAsync(cancellationToken);
            var subscription = await LoadAsync(db, owner.Id, cancellationToken)
                               ?? throw new BillingException("You do not have a subscription.");
            if (subscription.OwnerId != owner.Id) throw new BillingException("Only the owner of the subscription can remove users.");
            if (userId == subscription.OwnerId) throw new BillingException("The owner cannot be removed from their own subscription.");

            var member = subscription.Members.FirstOrDefault(m => m.UserId == userId)
                         ?? throw new BillingException("That user is not on the subscription.");
            db.SubscriptionMembers.Remove(member);
            await db.SaveChangesAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    // ---- internals (call under _gate)

    private async Task ChargeAsync(AppDbContext db, string subscriptionId, CancellationToken cancellationToken)
    {
        var subscription = await LoadByIdAsync(db, subscriptionId, cancellationToken);
        if (subscription is null || subscription.Status == SubscriptionStatus.Inactive) return; // nothing is charged before the first payment

        var today = Today;
        var charged = await db.UsageCharges.Where(c => c.SubscriptionId == subscriptionId && c.Date == today)
            .Select(c => c.UserId).ToListAsync(cancellationToken);
        var missing = subscription.Members.Where(m => !charged.Contains(m.UserId)).ToList();

        if (missing.Count > 0)
        {
            var balance = await BalanceAsync(db, subscriptionId, cancellationToken);
            if (balance < missing.Count * DailyRate)
            {
                // All or nothing: a day is never charged for only some of the users.
                subscription.Status = SubscriptionStatus.Suspended;
                await db.SaveChangesAsync(cancellationToken);
                return;
            }
            foreach (var member in missing) db.UsageCharges.Add(NewCharge(subscriptionId, member.User, today, member.UserId));
        }

        subscription.Status = SubscriptionStatus.Active;
        subscription.BilledDate = today;
        await db.SaveChangesAsync(cancellationToken);
    }

    private UsageCharge NewCharge(string subscriptionId, AppUser? user, DateOnly date, string? userId = null) => new()
    {
        SubscriptionId = subscriptionId,
        UserId = user?.Id ?? userId ?? string.Empty,
        UserName = user?.UserName ?? string.Empty,
        Date = date,
        Amount = DailyRate
    };

    private static async Task<decimal> BalanceAsync(AppDbContext db, string subscriptionId, CancellationToken cancellationToken)
    {
        var paid = await db.Payments.Where(p => p.SubscriptionId == subscriptionId && p.Status == PaymentStatus.Paid)
            .SumAsync(p => (decimal?)p.Amount, cancellationToken) ?? 0;
        var spent = await db.UsageCharges.Where(c => c.SubscriptionId == subscriptionId)
            .SumAsync(c => (decimal?)c.Amount, cancellationToken) ?? 0;
        return paid - spent;
    }

    private SubscriptionSummary Summarize(Subscription subscription, decimal balance, string userId)
    {
        var members = subscription.Members.OrderBy(m => m.AddedAt).ToList();
        var cost = members.Count * DailyRate;
        return new SubscriptionSummary(subscription, members, balance, DailyRate, cost,
            cost > 0 && balance > 0 ? (int)(balance / cost) : 0, subscription.OwnerId == userId);
    }

    private static Task<Subscription?> LoadAsync(AppDbContext db, string userId, CancellationToken cancellationToken) =>
        db.Subscriptions.Include(s => s.Members).ThenInclude(m => m.User)
            .FirstOrDefaultAsync(s => s.Members.Any(m => m.UserId == userId), cancellationToken);

    private static Task<Subscription?> LoadByIdAsync(AppDbContext db, string subscriptionId, CancellationToken cancellationToken) =>
        db.Subscriptions.Include(s => s.Members).ThenInclude(m => m.User)
            .FirstOrDefaultAsync(s => s.Id == subscriptionId, cancellationToken);

    private async Task<Subscription?> FindAsync(string userId, CancellationToken cancellationToken)
    {
        await using var db = await databases.CreateDbContextAsync(cancellationToken);
        return await db.Subscriptions.AsNoTracking().Include(s => s.Members).ThenInclude(m => m.User)
            .FirstOrDefaultAsync(s => s.Members.Any(m => m.UserId == userId), cancellationToken);
    }
}
