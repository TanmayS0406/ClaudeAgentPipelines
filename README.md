# Claude Agent Pipelines

An ASP.NET Core MVC app where you **sign in with your Claude account** and run **multi-agent pipelines** built on
[Microsoft Agent Framework](https://github.com/microsoft/agent-framework). Each agent has its own prompt, model and
attached reference files.

## How it works

- **Sign in with Claude.** It works the same way as the SSMS Claude Chat extension: the app never sees your Claude
  password or tokens. Clicking **Sign in with Claude** opens `claude auth login` (the official Claude Code CLI) on this
  computer, and you finish signing in in your browser. Once the CLI reports a signed-in account, the app gives you a
  cookie session for that account. The session ends as soon as Claude Code is signed out or switches account.
- **Pipelines** are a chain of agents. They run as an Agent Framework sequential workflow
  (`AgentWorkflowBuilder.BuildSequential`). Every agent receives the pipeline input plus the replies of all earlier
  agents, and the last agent's reply is the result.
- **Agents** are `ChatClientAgent`s on `ClaudeCodeChatClient`, an `IChatClient` that runs `claude -p` on the
  signed-in account. The agent's prompt becomes the system prompt.
- **Agent files.** Files you attach to an agent are passed to the CLI with `--add-dir`, so only that agent can read them.
  Agents only get the read-only `Read`, `Glob` and `Grep` tools. They have no shell, no file edits and no MCP servers.
- **Runs** stream live to the browser over server-sent events: text as it is written, files the agent reads,
  token usage, and step status. You can cancel a run, and finished runs are kept under `App_Data/runs`.

## Requirements

- .NET 10 SDK
- [Claude Code](https://claude.com/claude-code) installed on the same computer (`claude` on PATH, or set `Claude:CliPath`)
- A Claude subscription (Pro / Max / Team / Enterprise) or Anthropic Console account to sign in with

## Run it

```powershell
cd src/ClaudeAgentPipelines.Web
dotnet run
```

Open http://localhost:5180 and click **Sign in with Claude**. The first start creates a sample **Release notes**
pipeline (analyst → writer → editor) with attached files, so you can run it right away.

## Configuration (`appsettings.json`)

| Setting | Default | |
|---|---|---|
| `Claude:CliPath` | *(search PATH)* | Full path to `claude.exe` / `claude.cmd` |
| `Claude:DefaultModel` | *(CLI default)* | Model for agents that don't set one: `opus`, `sonnet`, `haiku`, `fable` or a model id |
| `Claude:AgentTimeoutMinutes` | `15` | One agent step is stopped after this long |
| `Storage:DataFolder` | `App_Data` | Pipelines, agent files and run history |
| `Storage:MaxFileBytes` / `MaxFilesPerAgent` | 10 MB / 20 | Upload limits |
| `Storage:AllowedFileExtensions` | text, code, PDF, images | Files the agent's Read tool can use |
| `Security:AllowRemoteClients` | `false` | See below |

## Security model

Every pipeline runs on **the Claude account signed in to Claude Code on the computer running the app**, and sign-in
opens a console window on that computer. So this is a single-user app for your own machine:

- Kestrel listens on `localhost` only, and any request that doesn't come from loopback is rejected with 403
  unless `Security:AllowRemoteClients` is set. Don't host it for other people: they would all use your Claude account.
- All forms use antiforgery tokens. Uploaded files are always downloaded as attachments, never rendered inline.
  Pipeline, agent and file IDs are validated before they are used in paths.

## Project layout

```
src/ClaudeAgentPipelines.Web/
  Claude/       ClaudeCli (locate, auth status/login/logout), ClaudeAccountService, ClaudeCodeChatClient (IChatClient)
  Pipelines/    models, PipelineStore (JSON files), PipelineRunner (Agent Framework workflow), RunManager, PipelineRun
  Controllers/  Account (sign in with Claude), Pipelines (agents, prompts, files), Runs (live view, SSE, cancel)
  Views/, wwwroot/
```
