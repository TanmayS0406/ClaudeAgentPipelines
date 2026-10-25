using System.Diagnostics;
using System.Text;

namespace ClaudeAgentPipelines;

/// <summary>Starts command-line tools (claude, az, git) as hidden processes with redirected output.</summary>
public static class CliProcess
{
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    /// <summary>
    /// Builds a hidden, redirected process for a tool. <c>.cmd</c> shims (npm installs, az) go through cmd.exe,
    /// so arguments must not contain cmd metacharacters such as &amp; | &lt; &gt; ^.
    /// </summary>
    public static ProcessStartInfo CreateStartInfo(string path, IEnumerable<string> arguments, string workingDirectory)
    {
        var args = string.Join(" ", arguments.Select(QuoteArgument));
        var isShim = path.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
                  || path.EndsWith(".bat", StringComparison.OrdinalIgnoreCase);

        var info = isShim
            ? new ProcessStartInfo("cmd.exe", "/d /s /c \"" + QuoteArgument(path) + " " + args + "\"")
            : new ProcessStartInfo(path, args);

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

    /// <summary>Runs a tool to completion. Output is stdout, and stderr separately.</summary>
    public static async Task<(int ExitCode, string Output, string Error)> RunAsync(
        string path, IEnumerable<string> arguments, string workingDirectory, CancellationToken cancellationToken)
    {
        using var process = new Process { StartInfo = CreateStartInfo(path, arguments, workingDirectory) };
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
        return (process.ExitCode, await stdout, await stderr);
    }

    /// <summary>Opens a visible console window running the command, for interactive sign-ins.</summary>
    public static Process? StartConsole(string path, string arguments)
    {
        var info = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("cmd.exe", "/d /s /c \"" + QuoteArgument(path) + " " + arguments + " & echo. & pause\"")
            : new ProcessStartInfo(path, arguments);
        info.UseShellExecute = true;
        info.WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Process.Start(info);
    }

    /// <summary>First existing file among the candidates, or null.</summary>
    public static string? FindFirst(IEnumerable<string> candidates)
    {
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

    /// <summary>The folders on PATH.</summary>
    public static IEnumerable<string> PathFolders() =>
        (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

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
}
