using System.Diagnostics;
using System.Text;
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
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    /// <summary>Resolves the CLI path from the setting, PATH, or the default install folders. Null when not found.</summary>
    public string? Locate()
    {
        var configured = options.CurrentValue.CliPath;
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return File.Exists(configured) ? configured : null;
        }

        var candidates = new List<string>();
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
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

        foreach (var candidate in candidates)
        {
            try
            {
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException)
            {
                // Malformed PATH entry.
            }
        }
        return null;
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
    public Process? StartLogin(string cliPath)
    {
        var info = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("cmd.exe", "/d /s /c \"" + QuoteArgument(cliPath) + " auth login & echo. & pause\"")
            : new ProcessStartInfo(cliPath, "auth login");
        info.UseShellExecute = true;
        info.WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Process.Start(info);
    }

    public async Task LogoutAsync(CancellationToken cancellationToken)
    {
        var cliPath = Locate();
        if (cliPath is not null)
        {
            await RunAsync(cliPath, ["auth", "logout"], cancellationToken);
        }
    }

    /// <summary>
    /// Builds a hidden, redirected process for the CLI. <c>.cmd</c> shims (npm installs) go through cmd.exe,
    /// so arguments must not contain cmd metacharacters such as &amp; | &lt; &gt; ^.
    /// </summary>
    public ProcessStartInfo CreateStartInfo(string cliPath, IEnumerable<string> arguments, string workingDirectory)
    {
        var args = string.Join(" ", arguments.Select(QuoteArgument));
        var isShim = cliPath.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
                  || cliPath.EndsWith(".bat", StringComparison.OrdinalIgnoreCase);

        var info = isShim
            ? new ProcessStartInfo("cmd.exe", "/d /s /c \"" + QuoteArgument(cliPath) + " " + args + "\"")
            : new ProcessStartInfo(cliPath, args);

        info.UseShellExecute = false;
        info.CreateNoWindow = true;
        info.RedirectStandardInput = true;
        info.RedirectStandardOutput = true;
        info.RedirectStandardError = true;
        info.StandardOutputEncoding = Utf8NoBom;
        info.StandardErrorEncoding = Utf8NoBom;
        info.WorkingDirectory = workingDirectory;
        return info;
    }

    /// <summary>Kills the process and its children (the CLI may spawn node / shell processes).</summary>
    public static void KillTree(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Already gone.
        }
    }

    /// <summary>Quotes one argument following the CommandLineToArgvW rules.</summary>
    public static string QuoteArgument(string arg)
    {
        if (string.IsNullOrEmpty(arg)) return "\"\"";
        if (arg.IndexOfAny([' ', '\t', '\n', '\v', '"']) < 0) return arg;

        var sb = new StringBuilder("\"");
        for (var i = 0; i < arg.Length; i++)
        {
            var backslashes = 0;
            while (i < arg.Length && arg[i] == '\\') { backslashes++; i++; }

            if (i == arg.Length)
            {
                sb.Append('\\', backslashes * 2);
                break;
            }
            if (arg[i] == '"')
            {
                sb.Append('\\', backslashes * 2 + 1).Append('"');
            }
            else
            {
                sb.Append('\\', backslashes).Append(arg[i]);
            }
        }
        return sb.Append('"').ToString();
    }

    private async Task<(int ExitCode, string Output)> RunAsync(string cliPath, string[] arguments, CancellationToken cancellationToken)
    {
        var info = CreateStartInfo(cliPath, arguments, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        using var process = new Process { StartInfo = info };
        process.Start();
        process.StandardInput.Close();

        var stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);

        await using (cancellationToken.Register(() => KillTree(process)))
        {
            await Task.WhenAll(stdout, stderr);
            await process.WaitForExitAsync(CancellationToken.None);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return (process.ExitCode, await stdout + await stderr);
    }
}
