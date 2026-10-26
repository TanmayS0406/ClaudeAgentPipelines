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

/// <summary>"Billing" section: the pay-as-you-go price and whether the app needs a subscription.</summary>
public sealed class BillingOptions
{
    public const string Section = "Billing";

    /// <summary>Charged every day for every user on a subscription, in rupees.</summary>
    public decimal DailyRatePerUser { get; set; } = 200;

    /// <summary>
    /// On: users without an active subscription can only reach the subscription, billing and account pages.
    /// Turn it off to use the app without paying (for example while Cashfree is not set up yet).
    /// </summary>
    public bool RequireSubscription { get; set; } = true;

    /// <summary>How often payments waiting for Cashfree are checked and the daily charges are applied.</summary>
    public int WorkerIntervalSeconds { get; set; } = 60;
}

/// <summary>"Cashfree" section: the payment gateway account. Keep the secret in user secrets or an environment variable.</summary>
public sealed class CashfreeOptions
{
    public const string Section = "Cashfree";

    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;

    /// <summary>"Sandbox" (test payments) or "Production".</summary>
    public string Environment { get; set; } = "Sandbox";

    public string ApiVersion { get; set; } = "2023-08-01";

    /// <summary>
    /// Where Cashfree sends the customer back to after paying. Empty = the address the app is opened on.
    /// Production needs an https address.
    /// </summary>
    public string ReturnBaseUrl { get; set; } = string.Empty;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret);

    public bool IsProduction => string.Equals(Environment, "Production", StringComparison.OrdinalIgnoreCase);

    public string ApiBase => IsProduction ? "https://api.cashfree.com/pg" : "https://sandbox.cashfree.com/pg";

    /// <summary>The "mode" the Cashfree JS checkout is opened with.</summary>
    public string CheckoutMode => IsProduction ? "production" : "sandbox";
}
