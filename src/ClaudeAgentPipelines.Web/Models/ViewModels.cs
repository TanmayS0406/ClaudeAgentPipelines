using System.ComponentModel.DataAnnotations;
using ClaudeAgentPipelines.Board;
using ClaudeAgentPipelines.Claude;
using ClaudeAgentPipelines.Pipelines;

namespace ClaudeAgentPipelines.Models;

public sealed record ClaudeViewModel(ClaudeAuthStatus Status, bool LoginInProgress);

public sealed class PipelineForm
{
    [Required, StringLength(100)]
    public string Name { get; set; } = string.Empty;

    [StringLength(1000)]
    public string? Description { get; set; }
}

public sealed class AgentForm
{
    [Required, StringLength(80)]
    public string Name { get; set; } = string.Empty;

    [Required, StringLength(20000)]
    public string Instructions { get; set; } = string.Empty;

    [StringLength(100), RegularExpression(@"^[A-Za-z0-9.\-\[\]_]*$", ErrorMessage = "Use a model alias such as sonnet, or a model id.")]
    public string? Model { get; set; }
}

public sealed record PipelineEditViewModel(
    Pipeline Pipeline,
    IReadOnlyList<string> ModelOptions,
    string DefaultModel,
    StorageOptions Storage);

public sealed class RunForm
{
    [Required(ErrorMessage = "Enter the pipeline input."), StringLength(50000)]
    public string Input { get; set; } = string.Empty;
}

public sealed record RunPipelineViewModel(Pipeline Pipeline, RunForm Form, IReadOnlyList<PipelineRun> RecentRuns);

public sealed class BoardForm
{
    [Required, StringLength(100)]
    public string Name { get; set; } = string.Empty;

    [StringLength(2000)]
    public string? Description { get; set; }

    /// <summary>Folder the agents write the code in. Empty = a new folder under Board:WorkspacesFolder.</summary>
    [StringLength(260)]
    public string? WorkspacePath { get; set; }
}

public sealed class BoardSettingsForm
{
    [Required, StringLength(100)]
    public string Name { get; set; } = string.Empty;

    [StringLength(2000)]
    public string? Description { get; set; }

    [Required(ErrorMessage = "Enter the workspace folder."), StringLength(260)]
    public string WorkspacePath { get; set; } = string.Empty;

    [StringLength(100), RegularExpression(@"^[0-9a-fA-F-]*$", ErrorMessage = "Choose a subscription from the list.")]
    public string? SubscriptionId { get; set; }

    [StringLength(200)]
    public string? SubscriptionName { get; set; }

    [StringLength(90), RegularExpression(@"^[-\w._()]*$", ErrorMessage = "Resource group names use letters, digits, - _ . ( ).")]
    public string? ResourceGroup { get; set; }

    [StringLength(40), RegularExpression(@"^[a-z0-9]*$", ErrorMessage = "Use an Azure region name such as eastus or westeurope.")]
    public string? Location { get; set; }

    public bool DeployEnabled { get; set; }
}

public sealed class TeamAgentForm
{
    [Required, StringLength(80)]
    public string Name { get; set; } = string.Empty;

    [Required, StringLength(20000)]
    public string Instructions { get; set; } = string.Empty;

    [StringLength(100), RegularExpression(@"^[A-Za-z0-9.\-\[\]_]*$", ErrorMessage = "Use a model alias such as sonnet, or a model id.")]
    public string? Model { get; set; }
}

public sealed class FeatureForm
{
    [Required(ErrorMessage = "Enter a title."), StringLength(200)]
    public string Title { get; set; } = string.Empty;

    [Required(ErrorMessage = "Describe the feature for the agents."), StringLength(20000)]
    public string Prompt { get; set; } = string.Empty;

    public bool StartNow { get; set; }
}

/// <summary>A board card, as JSON for the board page.</summary>
public sealed record FeatureCard(
    string Id,
    int Number,
    string Title,
    string Prompt,
    string Stage,
    string Status,
    string? StageLabel,
    string? Error,
    int TasksDone,
    int TasksTotal,
    IReadOnlyList<CardAssignee> Assigned,
    string? DeploymentUrl,
    IReadOnlyList<int> DependsOn,
    IReadOnlyList<int> BlockedBy,
    bool Planned,
    string? OrchestratorNote,
    string? Branch,
    DateTimeOffset UpdatedAt)
{
    public static FeatureCard From(Feature feature, string? stageLabel, IReadOnlyList<int> blockedBy)
    {
        var assigned = feature.Tasks.Where(t => t.State == TaskState.Doing)
            .Select(t => new CardAssignee(Team.Initials(t.Role), t.Title, t.Role.ToString().ToLowerInvariant()))
            .ToList();
        return new FeatureCard(
            feature.Id,
            feature.Number,
            feature.Title,
            feature.Prompt.Length > 160 ? feature.Prompt[..160] + "..." : feature.Prompt,
            feature.Stage.ToString(),
            feature.Status.ToString(),
            feature.IsActive ? stageLabel : null,
            feature.Error,
            feature.Tasks.Count(t => t.State == TaskState.Done),
            feature.Tasks.Count,
            assigned,
            feature.DeploymentUrl,
            feature.DependsOn,
            blockedBy,
            feature.PlannedAt is not null,
            feature.OrchestratorNote,
            feature.Branch,
            feature.UpdatedAt);
    }
}

public sealed record CardAssignee(string Initials, string Name, string Role);

public sealed record BoardViewModel(Project Project, IReadOnlyList<FeatureCard> Cards, long Version, int MaxParallelLimit);

/// <summary>The board's orchestrator state, as JSON for the board page.</summary>
public sealed record OrchestrationCard(bool Autopilot, int MaxParallel, bool Planning, string? RunId, string? Summary, string? Error,
                                       DateTimeOffset? PlannedAt)
{
    public static OrchestrationCard From(Project project) => new(
        project.Autopilot, project.MaxParallelFeatures, project.Orchestration.Planning, project.Orchestration.RunId,
        project.Orchestration.Summary, project.Orchestration.Error, project.Orchestration.PlannedAt);
}

public sealed record BoardSettingsViewModel(Project Project, IReadOnlyList<string> ModelOptions, string DefaultModel, string? Error = null);

public sealed record FeatureDetailsViewModel(Project Project, Feature Feature, PipelineRun? Run, IReadOnlyList<PipelineRun> History,
                                             IReadOnlyList<Feature> Dependencies, IReadOnlyList<int> BlockedBy);

public sealed record BoardSummary(Project Project, IReadOnlyDictionary<FeatureStage, int> Counts, int Active);
