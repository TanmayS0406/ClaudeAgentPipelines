using System.ComponentModel;

namespace ClaudeAgentPipelines.Board;

public sealed record GitResult(int ExitCode, string Output, string Error)
{
    public bool Ok => ExitCode == 0;
    public string Message => (Error.Trim().Length > 0 ? Error : Output).Trim();
}

/// <summary>
/// The git side of a board: the main workspace is a repository, and each feature is built on its own branch in its
/// own worktree (a second checkout of the same repository), so features can be built in parallel and merged like a
/// real team's pull requests.
/// </summary>
public static class GitWorkspace
{
    private const string BotName = "Claude Agent Board";
    private const string BotEmail = "agents@claude-agent-board.local";

    public static string BranchName(Feature feature) => $"feature/{feature.Number:0000}-{Slug(feature.Title)}";

    /// <summary>Worktrees live next to the main workspace, not inside it, so builds and agents in main don't see them.</summary>
    public static string WorktreePath(Project project, Feature feature) =>
        Path.Combine(Path.TrimEndingDirectorySeparator(project.WorkspacePath) + ".worktrees", $"feature-{feature.Number:0000}");

    /// <summary>Makes the main workspace a repository with at least one commit (worktrees need one to branch from).</summary>
    public static async Task EnsureRepositoryAsync(Project project, CancellationToken cancellationToken)
    {
        var main = project.WorkspacePath;
        Directory.CreateDirectory(Path.Combine(main, "docs", "features"));
        var created = new List<string>();

        var readme = Path.Combine(main, "README.md");
        if (!File.Exists(readme))
        {
            created.Add("README.md");
            await File.WriteAllTextAsync(readme,
                $"# {project.Name}\n\n{project.Description}\n\nBuilt feature by feature by the Claude agent team.\n\n## Layout\n\n{Team.Layout}",
                cancellationToken);
        }

        var gitignore = Path.Combine(main, ".gitignore");
        if (!File.Exists(gitignore))
        {
            created.Add(".gitignore");
            await File.WriteAllTextAsync(gitignore,
                "bin/\nobj/\nnode_modules/\ndist/\ncoverage/\n*.user\n.vs/\n.env\n.env.*\n*.zip\npublish/\nTestResults/\n",
                cancellationToken);
        }

        if (!Directory.Exists(Path.Combine(main, ".git")) && !File.Exists(Path.Combine(main, ".git")))
        {
            await RequireAsync(main, ["init", "--quiet"], cancellationToken);
        }
        if (!(await RunAsync(main, ["rev-parse", "--verify", "--quiet", "HEAD"], cancellationToken)).Ok)
        {
            await CommitAsync(main, "Initialize workspace", cancellationToken, allowEmpty: true);
        }
        else if (created.Count > 0)
        {
            // Only the files made here: the user's own uncommitted work in main is not swept into a commit.
            await RequireAsync(main, ["add", "--", .. created], cancellationToken);
            await RequireAsync(main, [.. await IdentityAsync(main, cancellationToken), "commit", "--quiet", "--no-verify",
                                      "-m", "Add workspace files", "--", .. created], cancellationToken);
        }
    }

    public static async Task<string> CurrentBranchAsync(string repository, CancellationToken cancellationToken) =>
        (await RequireAsync(repository, ["rev-parse", "--abbrev-ref", "HEAD"], cancellationToken)).Output.Trim();

    /// <summary>
    /// Creates (or reuses) the feature's worktree. A fresh start branches from main as it is now; a retry continues on the
    /// existing branch and first takes in what was merged to main since.
    /// </summary>
    public static async Task<(string Path, string Branch)> PrepareWorktreeAsync(Project project, Feature feature, bool fresh,
                                                                                Action<string> log, CancellationToken cancellationToken)
    {
        var main = project.WorkspacePath;
        var path = feature.WorktreePath ?? WorktreePath(project, feature);
        var branch = feature.Branch ?? BranchName(feature);
        var mainBranch = await CurrentBranchAsync(main, cancellationToken);

        await RunAsync(main, ["worktree", "prune"], cancellationToken);
        var exists = Directory.Exists(path) && File.Exists(Path.Combine(path, ".git"));

        if (fresh && exists)
        {
            await RunAsync(main, ["worktree", "remove", "--force", path], cancellationToken);
            exists = false;
        }
        if (fresh || !exists)
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            // -B: a feature that is run again starts its branch over from main.
            var branchExists = (await RunAsync(main, ["rev-parse", "--verify", "--quiet", "refs/heads/" + branch], cancellationToken)).Ok;
            string[] args = fresh || !branchExists
                ? ["worktree", "add", "-B", branch, path, mainBranch]
                : ["worktree", "add", path, branch];
            await RequireAsync(main, args, cancellationToken);
            log($"git worktree {path} on branch {branch}");
            return (path, branch);
        }

        // Retry: bring the branch up to date with main. A conflict here is left for the developers: abort and go on.
        var update = await RunAsync(path, ["-c", "user.name=" + BotName, "-c", "user.email=" + BotEmail,
                                           "merge", "--no-edit", "--quiet", mainBranch], cancellationToken);
        if (!update.Ok)
        {
            await RunAsync(path, ["merge", "--abort"], cancellationToken);
            log($"git: could not merge {mainBranch} into {branch} ({update.Message}); continuing without it");
        }
        log($"git worktree {path} on branch {branch} (continued)");
        return (path, branch);
    }

    /// <summary>Commits everything in the folder. Returns false when there was nothing to commit.</summary>
    public static async Task<bool> CommitAsync(string folder, string message, CancellationToken cancellationToken, bool allowEmpty = false)
    {
        await RequireAsync(folder, ["add", "--all"], cancellationToken);
        if (!allowEmpty && (await RunAsync(folder, ["diff", "--cached", "--quiet"], cancellationToken)).Ok) return false;

        string[] args = ["commit", "--quiet", "--no-verify", "-m", message, .. allowEmpty ? new[] { "--allow-empty" } : []];
        await RequireAsync(folder, [.. await IdentityAsync(folder, cancellationToken), .. args], cancellationToken);
        return true;
    }

    /// <summary>Merges the branch into the main workspace's branch. Returns the conflicted files (empty when it merged).</summary>
    public static async Task<IReadOnlyList<string>> MergeAsync(string main, string branch, string message, CancellationToken cancellationToken)
    {
        var status = await RequireAsync(main, ["status", "--porcelain"], cancellationToken);
        if (status.Output.Trim().Length > 0)
        {
            throw new FeatureFailedException(
                "The main workspace has uncommitted changes, so the feature can't be merged. Commit or discard them, then retry.");
        }

        var merge = await RunAsync(main, [.. await IdentityAsync(main, cancellationToken), "merge", "--no-ff", "-m", message, branch],
                                   cancellationToken);
        if (merge.Ok) return [];

        var conflicts = await ConflictedFilesAsync(main, cancellationToken);
        if (conflicts.Count == 0)
        {
            await RunAsync(main, ["merge", "--abort"], cancellationToken);
            throw new FeatureFailedException($"git merge failed: {merge.Message}");
        }
        return conflicts;
    }

    public static async Task<IReadOnlyList<string>> ConflictedFilesAsync(string main, CancellationToken cancellationToken) =>
        (await RunAsync(main, ["diff", "--name-only", "--diff-filter=U"], cancellationToken)).Output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>Files that still contain conflict markers.</summary>
    public static async Task<IReadOnlyList<string>> FilesWithMarkersAsync(string main, CancellationToken cancellationToken) =>
        (await RunAsync(main, ["grep", "-l", "-E", "^(<<<<<<< |>>>>>>> )", "--", "."], cancellationToken)).Output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>Concludes a merge whose conflicts were resolved and staged.</summary>
    public static async Task CompleteMergeAsync(string main, CancellationToken cancellationToken)
    {
        await RequireAsync(main, ["add", "--all"], cancellationToken);
        await RequireAsync(main, [.. await IdentityAsync(main, cancellationToken), "commit", "--quiet", "--no-verify", "--no-edit"],
                           cancellationToken);
    }

    public static Task AbortMergeAsync(string main, CancellationToken cancellationToken) =>
        RunAsync(main, ["merge", "--abort"], cancellationToken);

    /// <summary>Removes a merged feature's worktree and branch.</summary>
    public static async Task RemoveWorktreeAsync(string main, string? path, string? branch, CancellationToken cancellationToken)
    {
        if (path is not null)
        {
            await RunAsync(main, ["worktree", "remove", "--force", path], cancellationToken);
            try
            {
                if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            }
            catch (IOException)
            {
                // A build server may still hold a file; git worktree prune cleans up later.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
        if (branch is not null) await RunAsync(main, ["branch", "-d", branch], cancellationToken);
    }

    public static async Task<GitResult> RunAsync(string folder, string[] args, CancellationToken cancellationToken)
    {
        try
        {
            var (exitCode, output, error) = await CliProcess.RunAsync("git", args, folder, cancellationToken);
            return new GitResult(exitCode, output, error);
        }
        catch (Win32Exception)
        {
            throw new FeatureFailedException("git was not found. Install Git for Windows (Claude Code needs it too).");
        }
    }

    private static async Task<GitResult> RequireAsync(string folder, string[] args, CancellationToken cancellationToken)
    {
        var result = await RunAsync(folder, args, cancellationToken);
        if (!result.Ok) throw new FeatureFailedException($"git {args.FirstOrDefault(a => !a.StartsWith('-') && !a.Contains('='))} failed: {result.Message}");
        return result;
    }

    /// <summary>The user's git identity when there is one, otherwise the board's.</summary>
    private static async Task<string[]> IdentityAsync(string folder, CancellationToken cancellationToken) =>
        (await RunAsync(folder, ["config", "user.email"], cancellationToken)).Ok
            ? []
            : ["-c", "user.name=" + BotName, "-c", "user.email=" + BotEmail];

    public static string Slug(string title)
    {
        var chars = title.ToLowerInvariant().Select(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' ? c : '-').ToArray();
        var slug = string.Join('-', new string(chars).Split('-', StringSplitOptions.RemoveEmptyEntries));
        if (slug.Length > 40) slug = slug[..40].TrimEnd('-');
        return slug.Length > 0 ? slug : "feature";
    }
}
