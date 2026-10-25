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

public sealed record AzureSubscription(string Id, string Name, string TenantId, bool IsDefault);

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

    /// <summary>Enabled subscriptions of the signed-in account. Empty when not signed in.</summary>
    public async Task<IReadOnlyList<AzureSubscription>> ListSubscriptionsAsync(CancellationToken cancellationToken)
    {
        var cliPath = Locate();
        if (cliPath is null) return [];

        var result = await RunAsync(cliPath, ["account", "list", "--output", "json", "--only-show-errors"], cancellationToken);
        if (result.ExitCode != 0) return [];

        try
        {
            return (JsonNode.Parse(result.Output) as JsonArray ?? [])
                .OfType<JsonObject>()
                .Where(s => (string?)s["state"] is null or "Enabled")
                .Select(s => new AzureSubscription(
                    (string?)s["id"] ?? string.Empty,
                    (string?)s["name"] ?? string.Empty,
                    (string?)s["tenantId"] ?? string.Empty,
                    (bool?)s["isDefault"] == true))
                .Where(s => s.Id.Length > 0)
                .OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>Opens a console window running <c>az login</c> (browser or Windows account sign-in).</summary>
    public Process? StartLogin(string cliPath) => CliProcess.StartConsole(cliPath, "login");

    public async Task LogoutAsync(CancellationToken cancellationToken)
    {
        var cliPath = Locate();
        if (cliPath is not null) await RunAsync(cliPath, ["logout", "--only-show-errors"], cancellationToken);
    }

    private static Task<(int ExitCode, string Output, string Error)> RunAsync(string cliPath, string[] arguments,
                                                                               CancellationToken cancellationToken) =>
        CliProcess.RunAsync(cliPath, arguments, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), cancellationToken);
}
