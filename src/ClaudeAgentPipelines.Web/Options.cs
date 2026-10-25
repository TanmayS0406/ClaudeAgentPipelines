namespace ClaudeAgentPipelines;

/// <summary>"Claude" section: how the Claude Code CLI is found and run.</summary>
public sealed class ClaudeOptions
{
    public const string Section = "Claude";

    /// <summary>Full path to claude.exe / claude.cmd. Empty = search PATH and the usual install folders.</summary>
    public string CliPath { get; set; } = string.Empty;

    /// <summary>Model alias or id used for agents that do not set one (for example "sonnet"). Empty = CLI default.</summary>
    public string DefaultModel { get; set; } = string.Empty;

    /// <summary>An agent step is stopped when it runs longer than this.</summary>
    public int AgentTimeoutMinutes { get; set; } = 15;
}

/// <summary>"Storage" section: where pipelines, agent files and runs are kept.</summary>
public sealed class StorageOptions
{
    public const string Section = "Storage";

    private static readonly string[] DefaultExtensions =
        [".txt", ".md", ".csv", ".json", ".xml", ".yaml", ".yml", ".html", ".pdf", ".png", ".jpg", ".jpeg", ".gif", ".webp"];

    /// <summary>Relative paths are resolved against the application content root.</summary>
    public string DataFolder { get; set; } = "App_Data";

    public long MaxFileBytes { get; set; } = 10 * 1024 * 1024;

    public int MaxFilesPerAgent { get; set; } = 20;

    public string[] AllowedFileExtensions { get; set; } = [];

    public IReadOnlyCollection<string> EffectiveExtensions =>
        AllowedFileExtensions.Length > 0 ? AllowedFileExtensions : DefaultExtensions;
}

/// <summary>"Security" section.</summary>
public sealed class SecurityOptions
{
    public const string Section = "Security";

    /// <summary>
    /// Off by default: every pipeline runs on the Claude account signed in on this computer,
    /// so letting other machines in would share that account with them.
    /// </summary>
    public bool AllowRemoteClients { get; set; }
}
