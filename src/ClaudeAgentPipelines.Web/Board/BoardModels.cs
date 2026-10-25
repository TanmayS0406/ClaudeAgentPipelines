using System.Text.Json.Serialization;
using ClaudeAgentPipelines.Pipelines;

namespace ClaudeAgentPipelines.Board;

/// <summary>The jobs on the agent team. Each role writes only to its own part of the workspace.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AgentRole { Orchestrator, Planner, Database, Backend, Frontend, Testing, Integrator, DevOps }

/// <summary>Board columns, left to right.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FeatureStage { New, Planning, Development, Integration, Testing, Merging, Deploying, Done }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FeatureStatus { NotStarted, Queued, Running, Failed, Cancelled, Completed }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TaskState { ToDo, Doing, Done, Failed, Cancelled }

/// <summary>What a child task is for: the same agent does different jobs in different stages.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TaskKind { Plan, Develop, Integrate, Test, Fix, Retest, Merge, Deploy }

/// <summary>A board: one workspace (git repository) that the agent team builds features in.</summary>
public sealed class Project
{
    public string Id { get; set; } = Ids.New();
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;

    /// <summary>Absolute path of the main workspace: the git repository features are merged into.</summary>
    public string WorkspacePath { get; set; } = string.Empty;

    /// <summary>Number given to the next feature, like work item IDs in Azure Boards.</summary>
    public int NextNumber { get; set; } = 1;

    public ProjectAzureSettings Azure { get; set; } = new();

    /// <summary>
    /// Autopilot: the orchestrator plans the backlog whenever it changes and starts every feature whose dependencies
    /// are done, up to <see cref="MaxParallelFeatures"/> at a time.
    /// </summary>
    public bool Autopilot { get; set; }

    /// <summary>Features built at the same time, each on its own branch and worktree.</summary>
    public int MaxParallelFeatures { get; set; } = 3;

    public OrchestrationState Orchestration { get; set; } = new();

    /// <summary>One agent per role; prompts and models can be changed per project.</summary>
    public List<TeamAgent> Team { get; set; } = [];

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public TeamAgent Agent(AgentRole role) =>
        Team.FirstOrDefault(a => a.Role == role) ?? Board.Team.CreateDefaultTeam().First(a => a.Role == role);
}

/// <summary>The orchestrator's last backlog plan.</summary>
public sealed class OrchestrationState
{
    public bool Planning { get; set; }
    public string? RunId { get; set; }
    public DateTimeOffset? PlannedAt { get; set; }

    /// <summary>The orchestrator's summary of the plan: what runs in parallel and why.</summary>
    public string? Summary { get; set; }

    public string? Error { get; set; }
}

public sealed class ProjectAzureSettings
{
    public string SubscriptionId { get; set; } = string.Empty;
    public string SubscriptionName { get; set; } = string.Empty;
    public string ResourceGroup { get; set; } = string.Empty;
    public string Location { get; set; } = "eastus";

    /// <summary>
    /// Off: the DevOps agent only writes infrastructure-as-code and the pipeline, and validates them.
    /// On: it also creates the resources and deploys (this costs money on the subscription).
    /// </summary>
    public bool DeployEnabled { get; set; }
}

public sealed class TeamAgent
{
    public AgentRole Role { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public string Instructions { get; set; } = string.Empty;
}

/// <summary>A work item on the board: a feature the agent team plans, builds, integrates, tests, merges and deploys.</summary>
public sealed class Feature
{
    public string Id { get; set; } = Ids.New();
    public string ProjectId { get; set; } = string.Empty;
    public int Number { get; set; }
    public string Title { get; set; } = string.Empty;

    /// <summary>What the user asked for. Every agent gets it.</summary>
    public string Prompt { get; set; } = string.Empty;

    public FeatureStage Stage { get; set; } = FeatureStage.New;
    public FeatureStatus Status { get; set; } = FeatureStatus.NotStarted;
    public string? Error { get; set; }

    /// <summary>Numbers of the features that must be done (merged) before this one starts, decided by the orchestrator.</summary>
    public List<int> DependsOn { get; set; } = [];

    /// <summary>The orchestrator's reason for the dependencies (or for none).</summary>
    public string? OrchestratorNote { get; set; }

    /// <summary>When the orchestrator last planned this feature. Autopilot only starts planned features.</summary>
    public DateTimeOffset? PlannedAt { get; set; }

    public List<FeatureTask> Tasks { get; set; } = [];

    public string? CurrentRunId { get; set; }
    public List<string> RunIds { get; set; } = [];

    /// <summary>Git branch and worktree the feature is built in, until it is merged.</summary>
    public string? Branch { get; set; }
    public string? WorktreePath { get; set; }

    /// <summary>Workspace-relative path of the planner's technical plan.</summary>
    public string? PlanPath { get; set; }

    /// <summary>URL the DevOps agent reported after deploying.</summary>
    public string? DeploymentUrl { get; set; }

    public string CreatedBy { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    [JsonIgnore]
    public bool IsActive => Status is FeatureStatus.Queued or FeatureStatus.Running;
}

/// <summary>A child task of a feature: one agent's step, matching a step of the feature's run.</summary>
public sealed class FeatureTask
{
    public string StepId { get; set; } = Ids.New();
    public AgentRole Role { get; set; }
    public TaskKind Kind { get; set; }
    public string Title { get; set; } = string.Empty;
    public int Round { get; set; } = 1;
    public TaskState State { get; set; } = TaskState.ToDo;
}
