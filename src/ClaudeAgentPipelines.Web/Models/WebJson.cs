using System.Text.Json;

namespace ClaudeAgentPipelines.Models;

/// <summary>JSON settings for data sent to the browser (not for storage).</summary>
public static class WebJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
}
