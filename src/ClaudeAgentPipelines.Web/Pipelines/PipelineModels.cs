namespace ClaudeAgentPipelines.Pipelines;

/// <summary>A sequence of agents. Each agent receives the pipeline input plus every earlier agent's reply.</summary>
public sealed class Pipeline
{
    public string Id { get; set; } = Ids.New();
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public List<AgentDefinition> Agents { get; set; } = [];
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class AgentDefinition
{
    public string Id { get; set; } = Ids.New();
    public string Name { get; set; } = string.Empty;

    /// <summary>The agent's prompt: what it does with its input. Becomes its system prompt.</summary>
    public string Instructions { get; set; } = string.Empty;

    /// <summary>Model alias or id (for example "sonnet"). Empty = Claude:DefaultModel.</summary>
    public string Model { get; set; } = string.Empty;

    /// <summary>Reference files only this agent can read.</summary>
    public List<AgentFile> Files { get; set; } = [];
}

public sealed class AgentFile
{
    public string Id { get; set; } = Ids.New();

    /// <summary>Name shown to the user and used on disk (made unique within the agent).</summary>
    public string FileName { get; set; } = string.Empty;

    public long Length { get; set; }
    public DateTimeOffset UploadedAt { get; set; } = DateTimeOffset.UtcNow;
}

public static class Ids
{
    public static string New() => Guid.NewGuid().ToString("N");

    /// <summary>IDs end up in file paths, so only accept the format <see cref="New"/> produces.</summary>
    public static bool IsValid(string? id) =>
        id is { Length: 32 } && id.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}
