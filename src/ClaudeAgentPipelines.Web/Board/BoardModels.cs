using System.Text.Json.Serialization;
using ClaudeAgentPipelines.Pipelines;

namespace ClaudeAgentPipelines.Board;

/// <summary>The jobs on the agent team. Each role writes only to its own part of the workspace.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AgentRole { Planner, Database, Backend, Frontend, Testing, DevOps }

/// <summary>Board columns, left to right.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FeatureStage { New, Planning, Development, Testing, Deploying, Done }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FeatureStatus { NotStarted, Queued, Running, Failed, Cancelled, Completed }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TaskState { ToDo, Doing, Done, Failed, Cancelled }

/// <summary>A board: one workspace (code folder) that the agent team builds features in.</summary>
public sealed class Project
{
    public string Id { get; set; } = Ids.New();
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;

    /// <summary>Absolute path of the folder the agents write code in.</summary>
    public string WorkspacePath { get; set; } = string.Empty;

    /// <summary>Number given to the next feature, like work item IDs in Azure Boards.</summary>
    public int NextNumber { get; set; } = 1;

    public ProjectAzureSettings Azure { get; set; } = new();

    /// <summary>One agent per role; prompts and models can be changed per project.</summary>
    public List<TeamAgent> Team { get; set; } = [];

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public TeamAgent Agent(AgentRole role) => Team.First(a => a.Role == role);
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

/// <summary>A work item on the board: a feature the agent team plans, builds, tests and deploys.</summary>
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

    public List<FeatureTask> Tasks { get; set; } = [];

    public string? CurrentRunId { get; set; }
    public List<string> RunIds { get; set; } = [];

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
    public string Title { get; set; } = string.Empty;
    public int Round { get; set; } = 1;
    public TaskState State { get; set; } = TaskState.ToDo;
}
