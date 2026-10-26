using System.Net;

namespace ClaudeAgentPipelines.Billing;

/// <summary>Works out where Cashfree should send the customer after paying.</summary>
public static class ReturnAddress
{
    /// <summary><c>{order_id}</c> is filled in by Cashfree.</summary>
    public const string Path = "/Subscription/Return?order_id={order_id}";

    /// <summary>
    /// <paramref name="configuredBase"/> (Cashfree:ReturnBaseUrl) when set, else the address the page is opened on.
    /// Null when that address is this computer (localhost, 127.0.0.1, ::1): a phone that paid would be sent to its own
    /// "localhost" and show a broken page, so no address is sent and Cashfree shows its own payment-status page.
    /// </summary>
    public static string? For(string? configuredBase, string scheme, HostString host)
    {
        if (!string.IsNullOrWhiteSpace(configuredBase)) return configuredBase.TrimEnd('/') + Path;
        return IsThisComputer(host.Host) ? null : $"{scheme}://{host}{Path}";
    }

    public static bool IsThisComputer(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
        || (IPAddress.TryParse(host.Trim('[', ']'), out var address) && IPAddress.IsLoopback(address));
}
