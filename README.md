# Claude Agent Pipelines

An ASP.NET Core MVC app where you **sign in with your Claude account** and run **multi-agent pipelines** built on
[Microsoft Agent Framework](https://github.com/microsoft/agent-framework). Each agent has its own prompt, model and
attached reference files.

It also has **feature boards**: an Azure Boards style Kanban board where you add features as prompts. An orchestrator
agent decides which features depend on which and starts the independent ones **in parallel**, each on its own git
branch, the way a real team works: a planner writes the API contract, the database, backend (.NET) and frontend
(React) developers build at the same time, backend and frontend then integrate, a tester tests, the branch is merged
into main, and DevOps prepares or runs the Azure deployment.

## Feature boards

### The orchestrator (whole board)

Add features to **New**, then **Start autopilot** (or **Plan backlog** to only plan). The **Orchestrator** agent (opus)
reads the backlog and the code and decides each feature's dependencies: a feature waits for the features whose tables,
endpoints or pages it needs, and for features that would rewrite the same screens (to avoid painful merges); the rest
run in parallel. Its reasoning is a run you can read, and every card shows "after #1" / "Waiting for #1".

With autopilot on, the board starts every planned feature whose dependencies are merged, up to **Features in parallel**
(per board, default 3). When a feature finishes, the next ready ones start; new or edited features are planned again.

### One feature (its own branch)

| Column | Agents | What happens | May write | May run |
|---|---|---|---|---|
| Planning | Planner | Technical plan and binding API contract, saved to `docs/features/`. | nothing | nothing |
| Development | Database, Backend, Frontend | **Concurrently**: T-SQL migrations, ASP.NET Core Web API (.NET 10), React + TypeScript + Vite. The UI is built against the contract with mocks. | `database/`, `backend/`, `frontend/` | `dotnet`; `npm`, `npx`, `node` |
| Integration | Backend + Frontend | A **group chat**: the backend developer describes the real API, the frontend developer switches the UI onto it, and they fix mismatches until both say `INTEGRATION: DONE` (up to `Board:MaxIntegrationTurns` messages). | own folders | as above |
| Testing | Tester | xUnit + WebApplicationFactory and Vitest tests, contract check, SQL review. Ends with `VERDICT` and `FIX`; on FAIL the named developers fix and it retests (up to `Board:MaxTestRounds`). | `tests/`, `frontend/**/*.test.ts(x)` | `dotnet`, `npm`, `npx` |
| Merging | Release engineer | The branch is merged into main, **one feature at a time**. On conflicts the release engineer resolves them so both sides work, builds and tests; unresolved conflicts undo the merge and keep the branch for a retry. | all code folders (in main) | `git status/diff/log/show/add`, `dotnet`, `npm`, `npx` |
| Deploying | DevOps | On main: `infra/main.bicep`, `infra/deploy.ps1`, `azure-pipelines.yml`, validated with `az bicep build`. Deploys only when turned on. | `infra/`, `azure-pipelines.yml` | `az bicep`; with deployment on, `infra/deploy.ps1` and read-only `az` commands |
| Done | | Merged into main (and committed); the branch and worktree are removed. | | |

- **Agent Framework.** Every stage is a Microsoft Agent Framework workflow of `ChatClientAgent`s on
  `ClaudeCodeChatClient`: `BuildSequential` for one agent, `BuildConcurrent` for the three developers, and a group chat
  with `RoundRobinGroupChatManager` for integration. `FeatureOrchestrator` chains the stages for one feature;
  `BoardOrchestrator` plans the backlog and `FeatureManager` schedules features.
- **Workspace and branches.** Each board has a main workspace (default `%USERPROFILE%\ClaudeAgentBoards\<board>`), a git
  repository. Each feature gets a branch `feature/NNNN-title` in its own worktree next to it
  (`<board>.worktrees\feature-NNNN`), so features in parallel never write the same files. A retried feature continues on
  its branch. Every agent can read the whole workspace but write only its own part (Claude Code permission rules such as
  `Edit(./backend/**)`); anything else is refused, since nobody can approve a prompt.
- **Live.** The board, the orchestrator bar and the cards update over server-sent events; the feature page (an
  Azure-style work item form) streams every agent's output, tool use and token usage, and lists the child tasks,
  dependencies, branch, and fix, retest and merge steps.
- **Team.** Each board's Settings page has the eight agents' names, models and prompts, which you can change per board.
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
| `Board:MaxIntegrationTurns` | `6` | Messages Backend and Frontend may exchange while integrating |
| `Board:MaxParallelFeaturesLimit` | `6` | Highest "features in parallel" a board can choose |

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
  Board/        models, BoardStore (JSON files), Team (roles, prompts, permissions), BoardOrchestrator (backlog plan),
                FeatureOrchestrator (one feature's stages as Agent Framework workflows), FeatureManager (parallel
                slots, scheduling, merge lock), GitWorkspace (branches, worktrees, merges), BoardNotifier (live board)
  Pipelines/    models, PipelineStore (JSON files), PipelineRunner (Agent Framework workflow), RunManager, PipelineRun
  Controllers/  Account (sign in with Claude), Pipelines (agents, prompts, files), Runs (live view, SSE, cancel),
                Boards (board, SSE, settings, team), Features (work items), Azure (sign in to Azure)
  Views/, wwwroot/
```
