using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace ClaudeAgentPipelines.Azure;

/// <summary>Result of <c>az account show</c>: the signed-in Azure user and their default subscription.</summary>
public sealed record AzureAuthStatus(
    bool CliFound,
    bool LoggedIn,
    string? User = null,
    string? SubscriptionId = null,
    string? SubscriptionName = null,
    string? TenantId = null,
    string? Detail = null);

/// <summary>A subscription the signed-in account can see. Only <c>Enabled</c> ones can be deployed to.</summary>
public sealed record AzureSubscription(string Id, string Name, string TenantId, bool IsDefault, string State)
{
    public bool IsEnabled => State == "Enabled";
}

/// <summary>
/// Thin wrapper around the Azure CLI. Like Claude, the app never handles Azure credentials: <c>az login</c> signs in
/// on this computer and the DevOps agent's <c>az</c> commands use that sign-in.
/// </summary>
public sealed class AzureCli(IOptionsMonitor<AzureOptions> options)
{
    public string? Locate()
    {
        var configured = options.CurrentValue.CliPath;
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return File.Exists(configured) ? configured : null;
        }

        var candidates = new List<string>();
        foreach (var dir in CliProcess.PathFolders())
        {
            candidates.Add(Path.Combine(dir, OperatingSystem.IsWindows() ? "az.cmd" : "az"));
        }
        if (OperatingSystem.IsWindows())
        {
            foreach (var programFiles in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
            {
                candidates.Add(Path.Combine(Environment.GetFolderPath(programFiles), "Microsoft SDKs", "Azure", "CLI2", "wbin", "az.cmd"));
            }
        }
        return CliProcess.FindFirst(candidates);
    }

    public async Task<AzureAuthStatus> GetAuthStatusAsync(CancellationToken cancellationToken)
    {
        var cliPath = Locate();
        if (cliPath is null) return new AzureAuthStatus(false, false, Detail: "Azure CLI not found.");

        try
        {
            var result = await RunAsync(cliPath, ["account", "show", "--output", "json", "--only-show-errors"], cancellationToken);
            if (result.ExitCode != 0 || JsonNode.Parse(result.Output) is not JsonObject account)
            {
                return new AzureAuthStatus(true, false, Detail: "Not signed in to Azure.");
            }
            return new AzureAuthStatus(
                true,
                true,
                (string?)account["user"]?["name"],
                (string?)account["id"],
                (string?)account["name"],
                (string?)account["tenantId"]);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return new AzureAuthStatus(true, false, Detail: ex.Message);
        }
    }

    /// <summary>
    /// Every subscription the Azure CLI knows for the signed-in account, in any state (<c>az account list --all</c>, the
    /// CLI's own list, which is quick). Subscriptions in a directory (tenant) the CLI has no sign-in for are not in it:
    /// sign in to that tenant first. Empty when not signed in.
    /// </summary>
    public async Task<IReadOnlyList<AzureSubscription>> ListSubscriptionsAsync(CancellationToken cancellationToken)
    {
        var cliPath = Locate();
        if (cliPath is null) return [];

        var result = await RunAsync(cliPath, ["account", "list", "--all", "--output", "json", "--only-show-errors"], cancellationToken);
        if (result.ExitCode != 0) return [];

        try
        {
            return (JsonNode.Parse(result.Output) as JsonArray ?? [])
                .OfType<JsonObject>()
                .Select(s => new AzureSubscription(
                    (string?)s["id"] ?? string.Empty,
                    (string?)s["name"] ?? string.Empty,
                    (string?)s["tenantId"] ?? string.Empty,
                    (bool?)s["isDefault"] == true,
                    (string?)s["state"] ?? "Enabled"))
                .Where(s => s.Id.Length > 0)
                .OrderByDescending(s => s.IsEnabled)
                .ThenBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// Makes a subscription the CLI's default (<c>az account set</c>). The id must be a GUID, which keeps it safe to pass on.
    /// </summary>
    public async Task<(bool Ok, string? Error)> SetDefaultSubscriptionAsync(string subscriptionId, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(subscriptionId, out var id)) return (false, "That is not a subscription ID.");
        var cliPath = Locate();
        if (cliPath is null) return (false, "Azure CLI not found.");

        var result = await RunAsync(cliPath, ["account", "set", "--subscription", id.ToString(), "--only-show-errors"], cancellationToken);
        if (result.ExitCode == 0) return (true, null);

        var message = result.Error.ReplaceLineEndings(" ").Trim();
        if (message.Length > 300) message = message[..300] + "...";
        return (false, message.Length > 0 ? message : "The Azure CLI could not switch to that subscription.");
    }

    /// <summary>True for a tenant ID (GUID) or a domain name like contoso.onmicrosoft.com: nothing a command line could misread.</summary>
    public static bool IsValidTenant(string? tenant) =>
        tenant is { Length: >= 3 and <= 100 } && char.IsAsciiLetterOrDigit(tenant[0]) && tenant.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-');

    /// <summary>
    /// Opens a console window running <c>az login</c> (browser or Windows account sign-in). With a tenant it signs in to
    /// that directory, which is how its subscriptions show up.
    /// </summary>
    public Process? StartLogin(string cliPath, string? tenant = null) =>
        CliProcess.StartConsole(cliPath, string.IsNullOrWhiteSpace(tenant) ? "login" : "login --tenant " + tenant.Trim());

    public async Task LogoutAsync(CancellationToken cancellationToken)
    {
        var cliPath = Locate();
        if (cliPath is not null) await RunAsync(cliPath, ["logout", "--only-show-errors"], cancellationToken);
    }

    private static Task<(int ExitCode, string Output, string Error)> RunAsync(string cliPath, string[] arguments,
                                                                               CancellationToken cancellationToken) =>
        CliProcess.RunAsync(cliPath, arguments, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), cancellationToken);
}
