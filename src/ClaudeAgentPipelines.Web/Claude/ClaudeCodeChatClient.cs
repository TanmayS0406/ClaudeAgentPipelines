using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;

namespace ClaudeAgentPipelines.Claude;

public sealed class ClaudeCodeChatClientOptions
{
    /// <summary>Folder the CLI runs in.</summary>
    public required string WorkingDirectory { get; init; }

    /// <summary>Extra folders the Read / Glob / Grep tools may open (passed with --add-dir).</summary>
    public IReadOnlyList<string> AdditionalDirectories { get; init; } = [];

    /// <summary>Built-in Claude Code tools the agent may use. Read-only by default.</summary>
    public IReadOnlyList<string> Tools { get; init; } = ["Read", "Glob", "Grep"];

    /// <summary>
    /// Permission rules for those tools, such as <c>Edit(./backend/**)</c> or <c>Bash(dotnet build:*)</c>.
    /// Anything they don't allow is refused. Null = allow <see cref="Tools"/> without restrictions.
    /// </summary>
    public IReadOnlyList<string>? AllowedTools { get; init; }

    /// <summary>Model alias or id. Null = CLI default. <see cref="ChatOptions.ModelId"/> wins when set.</summary>
    public string? Model { get; init; }

    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(15);
}

/// <summary>
/// <see cref="IChatClient"/> backed by the Claude Code CLI (<c>claude -p</c>), so Microsoft Agent Framework agents run on
/// the Claude account signed in on this computer. Each call is a fresh, stateless CLI session: the instructions become
/// the system prompt and the conversation so far is sent as the prompt.
/// </summary>
public sealed class ClaudeCodeChatClient(ClaudeCli cli, ClaudeCodeChatClientOptions options) : IChatClient
{
    /// <summary>
    /// Key in <see cref="ChatResponseUpdate.AdditionalProperties"/> of updates that carry no text but describe what
    /// Claude is doing, such as "Read: C:\files\spec.md".
    /// </summary>
    public const string ActivityKey = "claude.activity";

    private static readonly UTF8Encoding Utf8NoBom = new(false);

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? chatOptions = null, CancellationToken cancellationToken = default) =>
        GetStreamingResponseAsync(messages, chatOptions, cancellationToken).ToChatResponseAsync(cancellationToken);

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? chatOptions = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var cliPath = cli.Locate() ?? throw new InvalidOperationException(
            "Claude Code CLI not found. Install it (https://claude.com/claude-code) or set Claude:CliPath in appsettings.json.");

        var history = messages.ToList();
        var model = chatOptions?.ModelId ?? options.Model;

        // The system prompt goes through a file and the prompt through stdin: no quoting or length limits.
        var systemPromptFile = Path.Combine(Path.GetTempPath(), "claude-agent-" + Guid.NewGuid().ToString("N") + ".txt");
        await File.WriteAllTextAsync(systemPromptFile, BuildSystemPrompt(chatOptions, history), Utf8NoBom, cancellationToken);

        try
        {
            for (var attempt = 1; ; attempt++)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(options.Timeout);

                Directory.CreateDirectory(options.WorkingDirectory);
                using var process = new Process
                {
                    StartInfo = CliProcess.CreateStartInfo(cliPath, BuildArguments(systemPromptFile, model), options.WorkingDirectory)
                };
                process.Start();

                var stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);
                await using (var stdin = new StreamWriter(process.StandardInput.BaseStream, Utf8NoBom))
                {
                    await stdin.WriteAsync(BuildPrompt(history));
                }

                var reader = new StreamJsonReader();
                var yielded = false;
                await using (timeout.Token.Register(() => CliProcess.KillTree(process)))
                {
                    // Killing the process closes stdout, which ends this loop.
                    string? line;
                    while ((line = await process.StandardOutput.ReadLineAsync(CancellationToken.None)) is not null)
                    {
                        foreach (var update in reader.Read(line))
                        {
                            yielded = true;
                            yield return update;
                        }
                    }
                    await process.WaitForExitAsync(CancellationToken.None);
                }

                cancellationToken.ThrowIfCancellationRequested();
                if (timeout.IsCancellationRequested)
                {
                    throw new TimeoutException($"Claude did not finish within {options.Timeout.TotalMinutes:0} minutes.");
                }

                var stderr = (await stderrTask).Trim();
                var error = reader.Error
                            ?? (!reader.GotResult && process.ExitCode != 0
                                ? stderr.Length > 0 ? stderr : $"Claude Code exited with code {process.ExitCode}."
                                : null);
                if (error is not null)
                {
                    // Agents running in parallel race to refresh the shared OAuth token; the losers fail before doing
                    // anything. Nothing has been streamed yet, so it is safe to wait for the refresh and run again.
                    if (!yielded && IsTokenRefreshConflict(error) && attempt <= RefreshRetryDelays.Length)
                    {
                        await Task.Delay(RefreshRetryDelays[attempt - 1] + TimeSpan.FromMilliseconds(Random.Shared.Next(1000)), cancellationToken);
                        continue;
                    }
                    throw new InvalidOperationException(DescribeError(error));
                }
                if (reader.Usage is not null)
                {
                    yield return new ChatResponseUpdate(ChatRole.Assistant, [new UsageContent(reader.Usage)]) { ModelId = reader.Model };
                }
                yield break;
            }
        }
        finally
        {
            TryDelete(systemPromptFile);
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose()
    {
    }

    private IEnumerable<string> BuildArguments(string systemPromptFile, string? model)
    {
        var args = new List<string>
        {
            "-p",
            "--output-format", "stream-json",
            "--verbose",
            "--include-partial-messages",
            "--system-prompt-file", systemPromptFile,
            // Each call is self-contained: the agent framework holds the conversation.
            "--no-session-persistence",
            // No MCP servers (including claude.ai connectors from the user's account), and only the listed tools.
            "--strict-mcp-config",
            "--tools", options.Tools.Count == 0 ? "" : string.Join(",", options.Tools),
            // Nobody can answer a permission prompt here: anything not allowed below is refused.
            "--permission-mode", "default"
        };

        var allowed = options.AllowedTools ?? options.Tools;
        if (allowed.Count > 0)
        {
            args.Add("--allowedTools");
            args.AddRange(allowed);
        }
        if (options.AdditionalDirectories.Count > 0)
        {
            args.Add("--add-dir");
            args.AddRange(options.AdditionalDirectories);
        }
        if (!string.IsNullOrWhiteSpace(model))
        {
            args.Add("--model");
            args.Add(model.Trim());
        }
        return args;
    }

    private static string BuildSystemPrompt(ChatOptions? chatOptions, List<ChatMessage> history)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(chatOptions?.Instructions)) parts.Add(chatOptions.Instructions.Trim());
        parts.AddRange(history.Where(m => m.Role == ChatRole.System && !string.IsNullOrWhiteSpace(m.Text)).Select(m => m.Text.Trim()));
        return parts.Count > 0 ? string.Join("\n\n", parts) : "You are a helpful assistant.";
    }

    /// <summary>A single message is sent as is; a longer conversation is sent as a labelled transcript.</summary>
    private static string BuildPrompt(List<ChatMessage> history)
    {
        var turns = history.Where(m => m.Role != ChatRole.System && !string.IsNullOrWhiteSpace(m.Text)).ToList();
        if (turns.Count == 0) return "(no input)";
        if (turns.Count == 1) return turns[0].Text;

        var prompt = new StringBuilder("The conversation so far, oldest first:\n");
        foreach (var turn in turns)
        {
            var label = turn.AuthorName ?? (turn.Role == ChatRole.Assistant ? "Assistant" : "User");
            prompt.Append("\n===== ").Append(label).Append(" =====\n").Append(turn.Text.Trim()).Append('\n');
        }
        prompt.Append("\n===== End of conversation =====\n");
        return prompt.ToString();
    }

    /// <summary>Waits before each retry after a token refresh conflict (about a minute in total).</summary>
    private static readonly TimeSpan[] RefreshRetryDelays =
        [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(16), TimeSpan.FromSeconds(30)];

    /// <summary>True when the CLI failed only because another Claude Code process was refreshing the OAuth token.</summary>
    private static bool IsTokenRefreshConflict(string error) =>
        error.Contains("another Claude Code process", StringComparison.OrdinalIgnoreCase);

    private static string DescribeError(string error)
    {
        var lower = error.ToLowerInvariant();
        if (IsTokenRefreshConflict(error))
        {
            return error; // transient: signing in again does not help
        }
        if (lower.Contains("login") || lower.Contains("api key") || lower.Contains("authenticat") || lower.Contains("oauth"))
        {
            return error + " Sign in with Claude again.";
        }
        return error;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Turns stream-json lines into chat updates. Text arrives twice (as deltas and in the complete assistant
    /// message); the complete copy is only used when no deltas were seen for it.
    /// </summary>
    private sealed class StreamJsonReader
    {
        private bool _streamedThisMessage;
        private bool _needsBreak;
        private bool _hasText;

        public string? Model { get; private set; }
        public bool GotResult { get; private set; }
        public string? Error { get; private set; }
        public UsageDetails? Usage { get; private set; }

        public IEnumerable<ChatResponseUpdate> Read(string line)
        {
            if (Parse(line) is not JsonObject message) yield break;

            switch ((string?)message["type"])
            {
                case "system" when (string?)message["subtype"] == "init":
                    Model = (string?)message["model"];
                    break;

                case "stream_event":
                    var streamEvent = message["event"];
                    if ((string?)streamEvent?["type"] == "content_block_delta"
                        && (string?)streamEvent["delta"]?["type"] == "text_delta")
                    {
                        var text = (string?)streamEvent["delta"]?["text"];
                        if (!string.IsNullOrEmpty(text))
                        {
                            _streamedThisMessage = true;
                            yield return Text(text);
                        }
                    }
                    break;

                case "assistant":
                    if (message["message"]?["content"] is JsonArray content)
                    {
                        foreach (var block in content.OfType<JsonObject>())
                        {
                            switch ((string?)block["type"])
                            {
                                case "text" when !_streamedThisMessage:
                                    var text = (string?)block["text"];
                                    if (!string.IsNullOrEmpty(text)) yield return Text(text);
                                    break;
                                case "tool_use":
                                    yield return Activity(DescribeToolUse((string?)block["name"], block["input"] as JsonObject));
                                    break;
                            }
                        }
                    }
                    if (_streamedThisMessage) _needsBreak = true;
                    _streamedThisMessage = false;
                    break;

                case "result":
                    GotResult = true;
                    if (message["permission_denials"] is JsonArray denials && denials.Count > 0)
                    {
                        var refused = denials.OfType<JsonObject>()
                            .Select(d => DescribeToolUse((string?)d["tool_name"], d["tool_input"] as JsonObject))
                            .Distinct();
                        yield return Activity("Not permitted: " + string.Join("; ", refused));
                    }
                    if ((bool?)message["is_error"] == true)
                    {
                        Error = (string?)message["result"]
                                ?? string.Join("; ", (message["errors"] as JsonArray)?.Select(e => e?.ToString()) ?? [(string?)message["subtype"]]);
                    }
                    if (message["usage"] is JsonObject usage)
                    {
                        var input = (long?)usage["input_tokens"] ?? 0;
                        var cached = ((long?)usage["cache_read_input_tokens"] ?? 0) + ((long?)usage["cache_creation_input_tokens"] ?? 0);
                        var output = (long?)usage["output_tokens"] ?? 0;
                        Usage = new UsageDetails
                        {
                            InputTokenCount = input + cached,
                            CachedInputTokenCount = (long?)usage["cache_read_input_tokens"],
                            OutputTokenCount = output,
                            TotalTokenCount = input + cached + output
                        };
                    }
                    break;
            }
        }

        private static JsonNode? Parse(string line)
        {
            try
            {
                return JsonNode.Parse(line);
            }
            catch (JsonException)
            {
                return null; // not a stream-json line
            }
        }

        private ChatResponseUpdate Text(string text)
        {
            // Separate the text of consecutive assistant messages (for example before and after a tool call).
            if (_needsBreak && _hasText) text = "\n\n" + text;
            _needsBreak = false;
            _hasText = true;
            return new ChatResponseUpdate(ChatRole.Assistant, text) { ModelId = Model };
        }

        private ChatResponseUpdate Activity(string text) => new()
        {
            Role = ChatRole.Assistant,
            ModelId = Model,
            AdditionalProperties = new AdditionalPropertiesDictionary { [ActivityKey] = text }
        };

        private static string DescribeToolUse(string? name, JsonObject? input)
        {
            var label = name ?? "tool";
            var filePath = (string?)input?["file_path"];
            var detail = !string.IsNullOrWhiteSpace(filePath)
                ? Path.GetFileName(filePath)
                : (string?)input?["command"] ?? (string?)input?["pattern"] ?? (string?)input?["path"];
            if (string.IsNullOrWhiteSpace(detail)) return label;

            detail = detail.Trim().ReplaceLineEndings(" ");
            return label + ": " + (detail.Length > 120 ? detail[..120] + "..." : detail);
        }
    }
}
