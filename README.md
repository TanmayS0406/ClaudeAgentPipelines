# Claude Agent Pipelines

An ASP.NET Core MVC app where you **sign in with your Claude account** and run **multi-agent pipelines** built on
[Microsoft Agent Framework](https://github.com/microsoft/agent-framework). Each agent has its own prompt, model and
attached reference files.

It also has **feature boards**: an Azure Boards style Kanban board where you add a feature as a prompt, and a team of
Claude agents plans it, writes the SQL scripts, the .NET API and the React UI in parallel, tests it, and prepares or
runs the Azure deployment.

## Feature boards

| Column | Agent | What it does | May write | May run |
|---|---|---|---|---|
| New | | Feature prompts waiting. Drag a card to Planning (or click Start) to start it. | | |
| Planning | Planner (opus) | Technical plan: schema, API contract, UI, test plan. Saved to `docs/features/`. | nothing | nothing |
| Development | Database, Backend, Frontend (sonnet) | Run **concurrently**: T-SQL migrations, ASP.NET Core Web API (.NET 10), React + TypeScript + Vite. | `database/`, `backend/`, `frontend/` | `dotnet`; `npm`, `npx`, `node` |
| Testing | Tester (sonnet) | xUnit + WebApplicationFactory and Vitest tests, runs them, reviews the SQL. Ends with `VERDICT: PASS/FAIL` and `FIX: ...`; on FAIL the named developers fix and it retests (up to `Board:MaxTestRounds`). | `tests/`, `frontend/**/*.test.ts(x)` | `dotnet`, `npm`, `npx` |
| Deploying | DevOps (sonnet) | `infra/main.bicep`, `infra/deploy.ps1`, `azure-pipelines.yml`, validated with `az bicep build`. Deploys only when turned on. | `infra/`, `azure-pipelines.yml` | `az bicep`; with deployment on, also `infra/deploy.ps1` and read-only `az` commands |
| Done | | Built, tested and committed to the workspace's git repository. | | |

- **Orchestration.** Each stage is a Microsoft Agent Framework workflow (`BuildSequential` for one agent,
  `BuildConcurrent` for the three developers) of `ChatClientAgent`s on `ClaudeCodeChatClient`. `FeatureOrchestrator`
  chains the stages and passes each agent the feature prompt, the plan, the developers' reports and the test report.
- **Workspace.** Each board has a folder (default `%USERPROFILE%\ClaudeAgentBoards\<board>`) with the layout above.
  Every agent can read all of it but write only its own part (Claude Code permission rules such as `Edit(./backend/**)`);
  anything else is refused, since nobody can approve a prompt. Features of one board run one at a time (the others are
  queued) because they share the folder. The workspace is a git repository and each finished feature is committed.
- **Live.** The board updates over server-sent events; the feature page (an Azure-style work item form) streams every
  agent's output, tool use and token usage, and lists the child tasks, including fix and retest rounds.
- **Team.** Each board's Settings page has the six agents' names, models and prompts, which you can change per board.
- **Sign in to Azure.** Like Claude: **Azure > Sign in to Azure** opens `az login` (the official Azure CLI) on this
  computer and the app never sees credentials. Choose the subscription, resource group and location in the board
  settings. Deployment is **off** by default; when you turn it on the DevOps agent runs `infra/deploy.ps1`, which
  creates resources that cost money in that subscription. Note that `dotnet`, `npm` and the deploy script can run any
  code, so the folder rules guard against mistakes, not against a hostile agent: use boards on code you trust.

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
- For feature boards: Git for Windows (Claude Code's Bash tool), Node.js 20+, and the
  [Azure CLI](https://aka.ms/installazurecliwindows) for the Azure sign-in and Bicep validation

## Run it

```powershell
cd src/ClaudeAgentPipelines.Web
dotnet run
```

Open http://localhost:5180 and click **Sign in with Claude**, then **New board** to start a feature board. The first start also creates a sample **Release notes**
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
| `Azure:CliPath` | *(search PATH)* | Full path to `az.cmd` |
| `Board:WorkspacesFolder` | `%USERPROFILE%\ClaudeAgentBoards` | Where new boards' workspaces are created |
| `Board:AgentTimeoutMinutes` | `40` | One board agent step is stopped after this long |
| `Board:MaxTestRounds` | `2` | Test rounds before a feature fails (developers fix between rounds) |
| `Board:CommitToGit` | `true` | Commit each finished feature in the workspace |

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
  Azure/        AzureCli (az account show/list, az login/logout), AzureAccountService
  Board/        models, BoardStore (JSON files), Team (roles, prompts, permissions), FeatureOrchestrator (stages as
                Agent Framework workflows), FeatureManager (start, queue, cancel), BoardNotifier (live board)
  Pipelines/    models, PipelineStore (JSON files), PipelineRunner (Agent Framework workflow), RunManager, PipelineRun
  Controllers/  Account (sign in with Claude), Pipelines (agents, prompts, files), Runs (live view, SSE, cancel),
                Boards (board, SSE, settings, team), Features (work items), Azure (sign in to Azure)
  Views/, wwwroot/
```
