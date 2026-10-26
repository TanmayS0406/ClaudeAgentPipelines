using System.ComponentModel.DataAnnotations;
using ClaudeAgentPipelines.Billing;

namespace ClaudeAgentPipelines.Models;

public sealed class LoginForm
{
    [Required(ErrorMessage = "Enter your user name.")]
    public string UserName { get; set; } = string.Empty;

    [Required(ErrorMessage = "Enter your password.")]
    public string Password { get; set; } = string.Empty;

    public string? ReturnUrl { get; set; }
}

public sealed class PurchaseForm
{
    [Range(1, 365, ErrorMessage = "Choose between 1 and 365 days.")]
    public int Days { get; set; } = 30;

    [Required(ErrorMessage = "Cashfree needs a mobile number."), RegularExpression(@"^\d{10}$", ErrorMessage = "Enter a 10-digit mobile number.")]
    public string Phone { get; set; } = string.Empty;

    [EmailAddress, StringLength(256)]
    public string? Email { get; set; }
}

public sealed class AddUserForm
{
    [Required(ErrorMessage = "Enter a user name."), StringLength(32)]
    public string UserName { get; set; } = string.Empty;

    [StringLength(128)]
    public string? DisplayName { get; set; }

    [Required(ErrorMessage = "Enter a password."), DataType(DataType.Password), StringLength(100)]
    public string Password { get; set; } = string.Empty;

    [EmailAddress, StringLength(256)]
    public string? Email { get; set; }
}

public sealed record SubscriptionViewModel(SubscriptionSummary? Summary, AddUserForm AddUser, bool CashfreeConfigured, decimal DailyRate);

public sealed record PurchaseViewModel(PurchaseForm Form, int Users, decimal DailyRate, bool CashfreeConfigured, bool IsNew);

public sealed record CheckoutViewModel(Payment Payment, string Mode);
