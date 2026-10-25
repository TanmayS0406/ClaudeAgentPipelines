using System.ComponentModel.DataAnnotations;
using ClaudeAgentPipelines.Claude;
using ClaudeAgentPipelines.Pipelines;

namespace ClaudeAgentPipelines.Models;

public sealed record LoginViewModel(ClaudeAuthStatus Status, bool LoginInProgress, string ReturnUrl);

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
