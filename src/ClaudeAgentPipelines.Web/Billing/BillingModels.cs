using ClaudeAgentPipelines.Pipelines;

namespace ClaudeAgentPipelines.Billing;

/// <summary>A person who can sign in to the app.</summary>
public sealed class AppUser
{
    public string Id { get; set; } = Ids.New();
    public string UserName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public string? Email { get; set; }

    /// <summary>10-digit mobile number; Cashfree requires one on every order.</summary>
    public string? Phone { get; set; }

    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Inactive: nothing paid yet. Active: every member is charged for today. Suspended: the balance ran out.</summary>
public enum SubscriptionStatus { Inactive, Active, Suspended }

/// <summary>
/// A pay-as-you-go subscription: a prepaid balance (money added through Cashfree) from which each member user is
/// charged a fixed amount per day.
/// </summary>
public sealed class Subscription
{
    public string Id { get; set; } = Ids.New();
    public string Name { get; set; } = string.Empty;

    /// <summary>The user who manages the subscription: pays, adds and removes users.</summary>
    public string OwnerId { get; set; } = string.Empty;

    public SubscriptionStatus Status { get; set; } = SubscriptionStatus.Inactive;

    /// <summary>The last day (India time) for which every member has been charged.</summary>
    public DateOnly? BilledDate { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<SubscriptionMember> Members { get; set; } = [];
}

/// <summary>A user on a subscription. A user belongs to at most one subscription, so the user is the key.</summary>
public sealed class SubscriptionMember
{
    public string UserId { get; set; } = string.Empty;
    public string SubscriptionId { get; set; } = string.Empty;
    public DateTimeOffset AddedAt { get; set; } = DateTimeOffset.UtcNow;
    public AppUser? User { get; set; }
}

public enum PaymentStatus { Pending, Paid, Failed }

/// <summary>One Cashfree order that adds money to a subscription's balance.</summary>
public sealed class Payment
{
    public string Id { get; set; } = Ids.New();
    public string SubscriptionId { get; set; } = string.Empty;

    /// <summary>The order ID sent to Cashfree.</summary>
    public string CashfreeOrderId { get; set; } = string.Empty;

    /// <summary>Lets the checkout be opened again while the order is pending.</summary>
    public string PaymentSessionId { get; set; } = string.Empty;

    public decimal Amount { get; set; }

    /// <summary>The days of use this payment was sized for (days x users x daily rate).</summary>
    public int Days { get; set; }

    public int Users { get; set; }
    public PaymentStatus Status { get; set; } = PaymentStatus.Pending;
    public string? Detail { get; set; }
    public string CreatedByUserId { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? PaidAt { get; set; }
}

/// <summary>The charge for one user for one day. The unique (subscription, user, day) key makes charging safe to repeat.</summary>
public sealed class UsageCharge
{
    public string Id { get; set; } = Ids.New();
    public string SubscriptionId { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;

    /// <summary>The user's name when charged, so the bill still reads right after a user is removed.</summary>
    public string UserName { get; set; } = string.Empty;

    public DateOnly Date { get; set; }
    public decimal Amount { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
