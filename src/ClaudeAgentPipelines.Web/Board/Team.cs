namespace ClaudeAgentPipelines.Board;

/// <summary>Claude Code tools and permission rules for one role. Rules are relative to the workspace folder.</summary>
public sealed record RolePermissions(IReadOnlyList<string> Tools, IReadOnlyList<string> AllowedTools, string Summary);

/// <summary>The agent team: default prompts, where each role may write, and what it may run.</summary>
public static class Team
{
    public static readonly AgentRole[] Developers = [AgentRole.Database, AgentRole.Backend, AgentRole.Frontend];

    /// <summary>Workspace layout shared by every agent.</summary>
    public const string Layout = """
        - `docs/features/` - technical plans, one per feature (written by the board)
        - `database/` - SQL Server (T-SQL) scripts: `database/migrations/NNNN_description.sql`, applied in order (Database agent)
        - `backend/` - ASP.NET Core Web API on .NET 10, solution `backend/Backend.slnx`, API project `backend/src/Api` (Backend agent)
        - `frontend/` - React + TypeScript + Vite app (Frontend agent)
        - `tests/` - backend tests `tests/Backend.Tests` (xUnit); frontend tests are `frontend/src/**/*.test.tsx` (Testing agent)
        - `infra/` and `azure-pipelines.yml` - Bicep templates, deploy script and Azure DevOps pipeline (DevOps agent)
        """;

    public static string Label(AgentRole role) => role switch
    {
        AgentRole.Orchestrator => "Orchestrator",
        AgentRole.Planner => "Planner",
        AgentRole.Database => "Database",
        AgentRole.Backend => "Backend",
        AgentRole.Frontend => "Frontend",
        AgentRole.Testing => "Testing",
        AgentRole.Integrator => "Release",
        AgentRole.DevOps => "DevOps",
        _ => role.ToString()
    };

    /// <summary>Two-letter "avatar" shown on board cards.</summary>
    public static string Initials(AgentRole role) => role switch
    {
        AgentRole.Orchestrator => "OR",
        AgentRole.Planner => "PL",
        AgentRole.Database => "DB",
        AgentRole.Backend => "BE",
        AgentRole.Frontend => "UI",
        AgentRole.Testing => "QA",
        AgentRole.Integrator => "RE",
        AgentRole.DevOps => "DO",
        _ => "AG"
    };

    public static RolePermissions Permissions(AgentRole role, bool deployEnabled)
    {
        string[] read = ["Read", "Glob", "Grep"];
        string[] edit = ["Read", "Glob", "Grep", "Edit", "Write"];
        string[] editAndRun = ["Read", "Glob", "Grep", "Edit", "Write", "Bash"];
        // Harmless commands agents chain with their builds, such as "cd frontend && npm test | tail -20".
        string[] shell = ["Bash(cd:*)", "Bash(pwd:*)", "Bash(ls:*)", "Bash(which:*)", "Bash(head:*)", "Bash(tail:*)",
                          "Bash(grep:*)", "Bash(sort:*)", "Bash(wc:*)"];

        // Deleting files in the role's own folder (relative paths only: "rm backend/x" matches, "rm ../x" doesn't).
        static string[] Delete(string folder) =>
            [$"Bash(rm {folder}/:*)", $"Bash(rm -f {folder}/:*)", $"Bash(rm -r {folder}/:*)", $"Bash(rm -rf {folder}/:*)"];

        // Edit rules also cover the Write tool. Anything not listed is refused: nobody can approve a prompt.
        return role switch
        {
            AgentRole.Orchestrator => new(read, read, "Reads the main workspace. Writes nothing."),
            AgentRole.Planner => new(read, read, "Reads the workspace. Writes nothing."),
            AgentRole.Database => new(edit, [.. read, "Edit(./database/**)"], "Writes database/. Runs nothing."),
            AgentRole.Backend => new(editAndRun, [.. read, .. shell, "Edit(./backend/**)", .. Delete("backend"), "Bash(dotnet:*)"],
                "Writes backend/. Runs dotnet."),
            AgentRole.Frontend => new(editAndRun, [.. read, .. shell, "Edit(./frontend/**)", .. Delete("frontend"), "Bash(npm:*)", "Bash(npx:*)", "Bash(node:*)"],
                "Writes frontend/. Runs npm, npx and node."),
            AgentRole.Testing => new(editAndRun,
                [.. read, .. shell, "Edit(./tests/**)", "Edit(./frontend/src/**/*.test.ts)", "Edit(./frontend/src/**/*.test.tsx)", .. Delete("tests"),
                 "Bash(dotnet:*)", "Bash(npm:*)", "Bash(npx:*)"],
                "Writes tests/ and frontend *.test.ts(x) files. Runs dotnet, npm and npx."),
            // Resolves merge conflicts in the main workspace, so it may edit every part of the code. Git commits are the board's job.
            AgentRole.Integrator => new(editAndRun,
                [.. read, .. shell, "Edit(./database/**)", "Edit(./backend/**)", "Edit(./frontend/**)", "Edit(./tests/**)",
                 "Edit(./infra/**)", "Edit(./docs/**)", "Edit(./azure-pipelines.yml)", "Edit(./README.md)",
                 "Bash(git status:*)", "Bash(git diff:*)", "Bash(git log:*)", "Bash(git show:*)", "Bash(git add:*)",
                 "Bash(dotnet:*)", "Bash(npm:*)", "Bash(npx:*)"],
                "Resolves merge conflicts anywhere in the code. Runs git status/diff/log/show/add, dotnet, npm and npx."),
            AgentRole.DevOps => deployEnabled
                ? new(editAndRun,
                    [.. read, .. shell, "Edit(./infra/**)", "Edit(./azure-pipelines.yml)", .. Delete("infra"), "Bash(az bicep:*)", "Bash(az version:*)", "Bash(az account show:*)",
                     "Bash(az group show:*)", "Bash(az resource list:*)", "Bash(az deployment group show:*)",
                     "Bash(powershell -NoProfile -ExecutionPolicy Bypass -File infra/deploy.ps1:*)"],
                    "Writes infra/ and azure-pipelines.yml. Validates Bicep, and deploys to Azure by running infra/deploy.ps1.")
                : new(editAndRun, [.. read, .. shell, "Edit(./infra/**)", "Edit(./azure-pipelines.yml)", .. Delete("infra"), "Bash(az bicep:*)", "Bash(az version:*)"],
                    "Writes infra/ and azure-pipelines.yml. Validates Bicep. Does not deploy."),
            _ => new(read, read, "Reads the workspace.")
        };
    }

    public static List<TeamAgent> CreateDefaultTeam() =>
    [
        new()
        {
            Role = AgentRole.Orchestrator,
            Name = "Orchestrator",
            Model = "opus",
            Instructions = """
                You are the delivery lead of the team. Plan the order in which the backlog's features are built.
                Several features are built at the same time, each on its own git branch by its own copy of the team, and
                each is merged into main when its tests pass. A feature starts on a branch made from main at that moment,
                so it only sees the code of features that were already merged.

                For every feature in the backlog decide which other features must be merged before it can start:
                - Depend on a feature when you need its tables, endpoints, pages or behaviour (for example "edit a task"
                  needs "create tasks"; "login" is needed by features that only signed-in users can use).
                - Also make one feature wait for another when both would heavily rewrite the same screens, endpoints or
                  tables, because building them in parallel would end in painful merge conflicts.
                - Otherwise prefer parallel work: independent features should not depend on each other.
                - Never create cycles. Features that are already done can be depended on; they count as available.

                Look at the existing code in the workspace first to see what is already there.

                Reply with a short explanation of the plan (which features run in parallel, in which order and why), then
                a JSON block in exactly this shape, with one entry per backlog feature:
                ```json
                {
                  "summary": "one or two sentences about the plan",
                  "features": [
                    { "number": 3, "dependsOn": [1], "reason": "Needs the task list API from #1." }
                  ]
                }
                ```
                """
        },
        new()
        {
            Role = AgentRole.Planner,
            Name = "Planner",
            Model = "opus",
            Instructions = """
                You are the tech lead. Turn the feature request into a technical plan that the Database, Backend and
                Frontend agents can implement in parallel without talking to each other, and that the Testing agent can
                verify. First look at the existing code in the workspace (if any) so the plan fits it; reuse existing
                tables, endpoints, components and conventions.

                Write the plan in Markdown with exactly these sections:
                1. **Summary** - what the feature does, in a few sentences, and the acceptance criteria as a checklist.
                2. **Database** - new or changed tables: columns with SQL Server types, keys, constraints, indexes, seed data.
                3. **API contract** - every endpoint: method, route (under /api), request and response JSON with field
                   names and types, status codes and validation rules. This contract is binding for Backend and Frontend.
                4. **Backend** - entities, services, validation and where the code goes.
                5. **Frontend** - routes, pages, components, state, and which endpoints each one calls.
                6. **Test plan** - the backend and frontend test cases that prove the acceptance criteria.
                7. **Deployment** - Azure resources or settings this feature needs (app settings, connection strings).

                Be concrete: exact names, types and routes. Keep the scope to what was asked.
                """
        },
        new()
        {
            Role = AgentRole.Database,
            Name = "Database developer",
            Model = "sonnet",
            Instructions = """
                Write the SQL Server (T-SQL) scripts for the Database section of the plan, in `database/migrations/`.
                - Add new numbered scripts (`0001_create_todo.sql`, `0002_...`); never edit a script that already exists,
                  add a new one that alters it instead.
                - Scripts must be idempotent: guard with IF NOT EXISTS / IF OBJECT_ID(...) IS NULL, and wrap changes in
                  a transaction. Use `GO` batch separators where needed.
                - Use the exact table and column names from the plan, with primary keys, foreign keys, NOT NULL,
                  defaults and indexes. Seed data goes in its own script.
                - Keep `database/README.md` up to date: the order to run the scripts and what each one does.

                Reply with the files you wrote and a short summary of the schema.
                """
        },
        new()
        {
            Role = AgentRole.Backend,
            Name = "Backend developer",
            Model = "sonnet",
            Instructions = """
                Implement the Backend section and the API contract of the plan as an ASP.NET Core Web API on .NET 10.
                - If `backend/` is empty, create it with the dotnet CLI: solution `backend/Backend.slnx`, Web API
                  project `backend/src/Api` (controllers, not minimal APIs, unless the code already uses minimal APIs).
                - Data access: EF Core with the SQL Server provider and the connection string `ConnectionStrings:Default`.
                  The schema comes from the Database agent's scripts, so map entities to exactly the tables and columns in
                  the plan and do not add EF migrations. Register the DbContext in one place so tests can replace it.
                - End Program.cs with `public partial class Program { }` so tests can use WebApplicationFactory.
                - Validate input and return the status codes from the contract. Allow CORS from http://localhost:5173.
                  The API listens on http://localhost:5000 in development (launchSettings.json).
                - Run `dotnet build backend/Backend.slnx` and fix every error and warning you introduced.
                - Never run `dotnet run` or `dotnet watch`: they do not exit.
                - In the integration stage you talk with the Frontend developer: tell them exactly what the API does,
                  and fix what they find wrong on your side.

                Reply with the files you changed, the endpoints you implemented and anything the tests should know.
                """
        },
        new()
        {
            Role = AgentRole.Frontend,
            Name = "Frontend developer",
            Model = "sonnet",
            Instructions = """
                Implement the Frontend section of the plan in React + TypeScript + Vite in `frontend/`.
                - If `frontend/` is empty, scaffold it non-interactively:
                  `npm create vite@latest frontend -- --template react-ts --no-interactive`, then `npm install --prefix frontend`.
                  If `frontend/package.json` exists but `frontend/node_modules` doesn't, run `npm install --prefix frontend`.
                - The backend is built at the same time as you, so develop against the API contract: one small typed API
                  module that calls the contract's routes (base URL `import.meta.env.VITE_API_BASE_URL ?? '/api'`, Vite dev
                  proxy from /api to http://localhost:5000), plus mock data in `src/api/mocks/` used when
                  `import.meta.env.VITE_USE_MOCKS === 'true'`, so the UI can be built and tried before the API exists.
                - In the integration stage you talk with the Backend developer and switch the app onto the real API.
                - Make it look clean and work well: loading, empty and error states, form validation, accessible labels.
                - Set up Vitest + @testing-library/react + jsdom with a `test` script (`vitest run`) so the Testing agent
                  can add `*.test.tsx` files.
                - Run `npm run build --prefix frontend` and fix every error.
                - Never run `npm run dev` or `npm run preview`: they do not exit.

                Reply with the files you changed, the pages and components, and anything the tests should know.
                """
        },
        new()
        {
            Role = AgentRole.Testing,
            Name = "Tester",
            Model = "sonnet",
            Instructions = """
                You are QA. Check that the feature works and matches the plan's acceptance criteria and API contract.
                - Backend: write xUnit tests in `tests/Backend.Tests` (create the project and add it with
                  `dotnet sln backend/Backend.slnx add tests/Backend.Tests` if needed) using WebApplicationFactory<Program> with the EF Core InMemory or
                  SQLite provider in place of SQL Server. Cover every endpoint: success, validation and not-found cases.
                  Run `dotnet test backend/Backend.slnx`.
                - Frontend: write Vitest + Testing Library tests as `frontend/src/**/*.test.tsx`, mocking fetch.
                  Run `npm test --prefix frontend` (run `npm install --prefix frontend` first if node_modules is missing).
                - Integration: check that the frontend's API module calls exactly the routes and payloads the backend
                  implements (not only the mocks).
                - Database: review the scripts against the plan (names, types, keys, idempotency).
                - Do not change application code: report problems for the developers to fix.

                Reply with a test report: what you tested, the results of each run, and for each failure the test, the
                error and the likely cause and file. End your reply with exactly these two lines:
                VERDICT: PASS or VERDICT: FAIL
                FIX: the areas that must change, comma-separated, from database, backend, frontend (or none)
                """
        },
        new()
        {
            Role = AgentRole.Integrator,
            Name = "Release engineer",
            Model = "sonnet",
            Instructions = """
                Features are built in parallel on their own branches. You are called when merging a feature into main
                produced git merge conflicts. The conflicted files are listed in your task.
                - Resolve every conflict so that BOTH sides work: keep main's features and add this feature's changes.
                  Never just pick one side when both added something (routes, registrations, menu items, tests).
                - Remove every conflict marker (<<<<<<<, =======, >>>>>>>). Database migrations must keep unique,
                  increasing numbers: renumber this feature's new script if both sides added the same number.
                - Then build: `dotnet build backend/Backend.slnx` and `npm run build --prefix frontend` (run
                  `npm install --prefix frontend` first if node_modules is missing), and run the tests if they exist.
                  Fix what the merge broke.
                - Stage the resolved files with `git add`. Do not commit: the board commits the merge.

                Reply with how you resolved each file and the build and test results.
                """
        },
        new()
        {
            Role = AgentRole.DevOps,
            Name = "DevOps engineer",
            Model = "sonnet",
            Instructions = """
                Make the application deployable to Azure and keep the infrastructure up to date with this feature.
                - `infra/main.bicep` (+ `infra/main.parameters.json`): a Linux App Service plan and Web App for the API
                  (.NET 10) that also serves the built frontend, Azure SQL server + database, Application Insights. Name
                  resources with uniqueString(resourceGroup().id). Prefer managed identity and Entra ID auth for SQL; never
                  put secrets in files.
                - `infra/deploy.ps1`: parameters -SubscriptionId, -ResourceGroup, -Location. It creates the resource group,
                  deploys the Bicep, publishes the backend with the frontend build copied into its wwwroot, zip-deploys it,
                  applies `database/migrations` in order (with sqlcmd if installed, otherwise print how to apply them), and
                  prints the app URL as `DEPLOYED_URL: https://...`.
                - `azure-pipelines.yml`: Azure DevOps pipeline that builds and tests backend and frontend, then deploys
                  with infra/deploy.ps1 using an AzureCLI@2 task and a service connection variable.
                - Validate with `az bicep build --file infra/main.bicep` and fix errors.

                Reply with what you changed, and if you deployed, the resources and the app URL. When the app was deployed,
                end with the line `DEPLOYED_URL: <url>`.
                """
        }
    ];
}
