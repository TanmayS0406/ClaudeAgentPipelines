using System.Diagnostics;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace ClaudeAgentPipelines.Claude;

/// <summary>Result of <c>claude auth status</c>.</summary>
public sealed record ClaudeAuthStatus(
    bool CliFound,
    bool LoggedIn,
    string? Email = null,
    string? AuthMethod = null,
    string? SubscriptionType = null,
    string? Detail = null)
{
    /// <summary>Stable name of the signed-in account: the e-mail, or the auth method when there is none (API key).</summary>
    public string? Account => LoggedIn ? Email ?? AuthMethod ?? "claude" : null;
}

/// <summary>
/// Thin wrapper around the Claude Code CLI. The app never handles Claude credentials itself:
/// sign-in, token storage and refresh all happen inside the official CLI.
/// </summary>
public sealed class ClaudeCli(IOptionsMonitor<ClaudeOptions> options)
{
    /// <summary>Resolves the CLI path from the setting, PATH, or the default install folders. Null when not found.</summary>
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
            if (OperatingSystem.IsWindows())
            {
                candidates.Add(Path.Combine(dir, "claude.exe"));
                candidates.Add(Path.Combine(dir, "claude.cmd"));
            }
            else
            {
                candidates.Add(Path.Combine(dir, "claude"));
            }
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        candidates.Add(Path.Combine(home, ".local", "bin", OperatingSystem.IsWindows() ? "claude.exe" : "claude")); // native installer
        candidates.Add(Path.Combine(appData, "npm", "claude.cmd"));                                                  // npm -g install

        return CliProcess.FindFirst(candidates);
    }

    /// <summary>Runs <c>claude auth status</c>.</summary>
    public async Task<ClaudeAuthStatus> GetAuthStatusAsync(CancellationToken cancellationToken)
    {
        var cliPath = Locate();
        if (cliPath is null)
        {
            return new ClaudeAuthStatus(false, false, Detail: "Claude Code CLI not found.");
        }

        (int ExitCode, string Output) result;
        try
        {
            result = await RunAsync(cliPath, ["auth", "status"], cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new ClaudeAuthStatus(true, false, Detail: ex.Message);
        }

        try
        {
            var json = JsonNode.Parse(result.Output.Trim())!;
            var loggedIn = (bool?)json["loggedIn"] ?? result.ExitCode == 0;
            return new ClaudeAuthStatus(
                true,
                loggedIn,
                (string?)json["email"],
                (string?)json["authMethod"],
                (string?)json["subscriptionType"],
                loggedIn ? null : "Not signed in.");
        }
        catch
        {
            // Plain text output (older CLI): rely on the exit code.
            return new ClaudeAuthStatus(true, result.ExitCode == 0, Detail: result.ExitCode == 0 ? null : "Not signed in.");
        }
    }

    /// <summary>
    /// Opens a console window on this computer running <c>claude auth login</c>, which takes the user through the
    /// browser sign-in. Returns the console process so the caller can tell when it closes.
    /// </summary>
    public Process? StartLogin(string cliPath) => CliProcess.StartConsole(cliPath, "auth login");

    public async Task LogoutAsync(CancellationToken cancellationToken)
    {
        var cliPath = Locate();
        if (cliPath is not null)
        {
            await RunAsync(cliPath, ["auth", "logout"], cancellationToken);
        }
    }

    private static async Task<(int ExitCode, string Output)> RunAsync(string cliPath, string[] arguments, CancellationToken cancellationToken)
    {
        var result = await CliProcess.RunAsync(cliPath, arguments, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), cancellationToken);
        return (result.ExitCode, result.Output + result.Error);
    }
}
