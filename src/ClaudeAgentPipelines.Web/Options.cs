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

/// <summary>"Storage" section: where the files uploaded for agents are kept (the data itself is in SQL Server, see ConnectionStrings:Default).</summary>
public sealed class StorageOptions
{
    public const string Section = "Storage";

    private static readonly string[] DefaultExtensions =
        [".txt", ".md", ".csv", ".json", ".xml", ".yaml", ".yml", ".html", ".pdf", ".png", ".jpg", ".jpeg", ".gif", ".webp"];

    /// <summary>Relative paths are resolved against the application content root.</summary>
    public string DataFolder { get; set; } = "App_Data";

    public string ResolveDataFolder(string contentRoot) => Path.GetFullPath(Path.Combine(contentRoot, DataFolder));

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

/// <summary>"Azure" section: how the Azure CLI is found.</summary>
public sealed class AzureOptions
{
    public const string Section = "Azure";

    /// <summary>Full path to az.cmd / az. Empty = search PATH and the default install folder.</summary>
    public string CliPath { get; set; } = string.Empty;
}

/// <summary>"Board" section: feature boards and the agent team that builds features.</summary>
public sealed class BoardOptions
{
    public const string Section = "Board";

    /// <summary>
    /// Parent folder of project workspaces (the code the agents write). Empty = "ClaudeAgentBoards" in the user profile.
    /// Kept outside the app folder so generated projects are not built or watched as part of this app.
    /// </summary>
    public string WorkspacesFolder { get; set; } = string.Empty;

    /// <summary>One agent step is stopped after this long. Coding agents need longer than pipeline agents.</summary>
    public int AgentTimeoutMinutes { get; set; } = 40;

    /// <summary>How many times the testing agent runs; after a failed round the developers fix what it reported.</summary>
    public int MaxTestRounds { get; set; } = 2;

    /// <summary>Most messages Backend and Frontend exchange while integrating a feature.</summary>
    public int MaxIntegrationTurns { get; set; } = 6;

    /// <summary>Upper limit for a board's "features in parallel" setting.</summary>
    public int MaxParallelFeaturesLimit { get; set; } = 6;

    public string EffectiveWorkspacesFolder => !string.IsNullOrWhiteSpace(WorkspacesFolder)
        ? Path.GetFullPath(WorkspacesFolder)
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "ClaudeAgentBoards");
}
