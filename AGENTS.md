# AGENTS.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What This Project Does

`Ado.AutonomousWorker` is a .NET 10 console application (CLI tool) that automates end-to-end AI-assisted development for Azure DevOps work items tagged `AI-Work` or `AI-Rework` in the PRIIS backlog. It orchestrates three AI roles — **Plan**, **Code**, and **Review** — against isolated git worktrees, then publishes draft PRs linked to the parent work item. It also monitors existing AI-generated PRs for reviewer feedback (`AI-Rework` PR label) and automatically addresses review comments via a lightweight `AiPrFix` queue kind.

## Maintenance Rules

**When you add, rename, or remove a CLI command**, you must update all five of these in the same change:

1. **`CliCommandCatalog.cs`** (`src/Ado.AutonomousWorker.Cli/Completion/CliCommandCatalog.cs`) — add the command to `TopLevelCommands` and/or `BranchCommands`, and its options to `CommandOptions`. This drives tab-completion.
2. **`SlashCommandRegistry.cs`** (`src/Ado.AutonomousWorker.Cli/Interactive/SlashCommandRegistry.cs`) — add the command to the interactive-mode slash command catalog with description and category.
3. **`README.md`** — add the command to the `## Commands` section with example invocations and a description.
4. **`Program.cs`** — register the command class as a singleton and register the command in the CLI startup path: usually in `CliAppConfiguration.cs`, or directly in `Program.cs` when the command is intentionally wired there (for example, `interactive`).
5. **`AGENTS.md`** — add the command to the Commands section below.

**When you add, rename, or remove a flag on an existing command**, you must update all four of these in the same change:

1. **`CliCommandCatalog.cs`** — add/update the flag in `CommandOptions` under the relevant command key.
2. **`SlashCommandRegistry.cs`** — add/update the flag in `CommandFlagMap` under the matching slash-command key. Use `TakesValue: true` for flags that consume a next token (e.g. `--iteration <VALUE>`); omit it for boolean switches.
3. **`README.md`** — add the flag to the command's example invocations in `## Commands` and update its description in the Command Details table.
4. **`AGENTS.md`** — document the flag in the Commands section below.

**When you add, rename, or remove a subcommand** (e.g. `task resume`, `ui splash`), apply both the command rules above *and* the flag rules for any options it takes. Additionally update `BranchCommands` in `CliCommandCatalog.cs` so the subcommand appears in tab-completion after its parent.

Forgetting any of these will leave tab-completion, interactive mode, the README, or agent guidance out of sync.

## Commands

```bash
# Build
dotnet build src/Ado.AutonomousWorker.Cli/Ado.AutonomousWorker.Cli.csproj

# Build the dashboard
dotnet build src/Ado.AutonomousWorker.Dashboard/Ado.AutonomousWorker.Dashboard.csproj

# Run all tests
dotnet run --project tests/Ado.AutonomousWorker.Tests/Ado.AutonomousWorker.Tests.csproj

# Run a single test class
dotnet run --project tests/Ado.AutonomousWorker.Tests/Ado.AutonomousWorker.Tests.csproj -- --filter "FullyQualifiedName~RulesEngineTests"

# Install as local dotnet tool (adds `ado-worker` to the shell)
.\scripts\install-local-tool.ps1

# Run the tool after local install
dotnet tool run ado-worker                                                   # launches interactive mode
dotnet tool run ado-worker -- interactive                                    # launches interactive mode explicitly
dotnet tool run ado-worker -- queue
dotnet tool run ado-worker -- run
dotnet tool run ado-worker -- run --pr-fix-only
dotnet tool run ado-worker -- deploy
dotnet tool run ado-worker -- deploy --dry-run
dotnet tool run ado-worker -- dashboard
dotnet tool run ado-worker -- dashboard --port 8080
dotnet tool run ado-worker -- task run --id <workItemId>
dotnet tool run ado-worker -- task run --pr <pullRequestId>
dotnet tool run ado-worker -- task resume --execution-id <executionId>
dotnet tool run ado-worker -- task resume --last
dotnet tool run ado-worker -- task set-phase --last --phase Reviewing
dotnet tool run ado-worker -- task set-phase --execution-id <executionId> --phase Validating
dotnet tool run ado-worker -- agent test
dotnet tool run ado-worker -- agent test --role review
dotnet tool run ado-worker -- agent test --role pr
dotnet tool run ado-worker -- doctor

# Run the dashboard standalone (development)
dotnet run --project src/Ado.AutonomousWorker.Dashboard

# Format all C# code (run after coding is done)
dotnet csharpier format .
```

Local configuration goes in `src/Ado.AutonomousWorker.Cli/appsettings.local.json` (git-ignored). The PAT and local repo paths live there.

## Architecture

### Project Layout

| Project                               | Role                                                                                   |
| ------------------------------------- | -------------------------------------------------------------------------------------- |
| `Ado.AutonomousWorker.Cli`            | CLI entry point, commands, DI wiring, Spectre.Console UI                               |
| `Ado.AutonomousWorker.Core`           | Domain abstractions, models, enums, `RulesEngine`                                      |
| `Ado.AutonomousWorker.Dashboard`      | Blazor Server web dashboard (MudBlazor) for viewing executions, logs, and agent output |
| `Ado.AutonomousWorker.Infrastructure` | All real implementations (agents, state, ADO client, workspaces)                       |
| `Ado.AutonomousWorker.Tests`          | TUnit tests for pure/unit-testable components                                          |

### Queue Kinds

| Kind      | `QueueItemKind` | Trigger                      | Phase flow                                                                        |
| --------- | --------------- | ---------------------------- | --------------------------------------------------------------------------------- |
| AI Work   | `0`             | Work item tagged `AI-Work`   | Queued → Planning → Coding → Reviewing → ApplyingReview → Validating → Publishing |
| AI Rework | `1`             | Work item tagged `AI-Rework` | Same as AI Work                                                                   |
| PR Fix    | `2`             | PR has the `AI-Rework` label | Queued → Coding → Validating → Publishing                                         |

**PR Fix** (`AiPrFix`) skips Planning and Reviewing because:

- The existing PR branch already defines the scope.
- The reviewer's comments are the quality signal — running a separate AI review would be redundant.
- On success, the `AI-Rework` PR label is removed and `AI-Done` is added to the work item.
- On failure, the `AI-Rework` PR label is intentionally left intact (so the developer sees it still needs attention), and no `AI-Failed` tag is added to the work item (which may already be `AI-Done` from a prior run).
- Resume (`task resume`) reconstructs the `PrFixContext` (repo name, branch) via `GetPullRequestLocationAsync` using the stored `PullRequestId`.

### Task Lifecycle — `TaskPhase` enum

```
Queued → Planning → Coding → Reviewing → ApplyingReview → Validating → Publishing → Completed
                                                                                   → Failed
                                                                                   → Skipped
```

For `AiPrFix`, Planning and Reviewing are bypassed via dedicated handler classes:

- `PrFixEligibilityHandler` transitions directly to `Coding` (no eligibility or acceptance-criteria checks).
- `PrFixCodingHandler` transitions directly to `Validating`, so `ReviewingHandler` and `PlanningHandler` are never reached.

The handler dispatch uses `IPhaseHandler.Accepts(PhaseContext)` resolved via `ILookup<TaskPhase, IPhaseHandler>` in `TaskStateMachine`. Abstract bases `WorkItemHandler` and `PrFixHandler` perform the typed cast once; concrete handlers extend the right base and receive a typed context parameter.

The orchestrator (`TaskOrchestrator`) is phase-aware and can **resume** from any post-Coding phase without re-running earlier phases. On resume it rebuilds `RepositoryExecutions` from the persisted plan and existing worktrees (`preserveExistingWorkspace: true`), so Reviewing, Validating, and Publishing all have the correct repo contexts. Phase state is persisted in SQLite.

Use `task set-phase` to manually rewind a Completed or Failed execution to `Reviewing`, `ApplyingReview`, `Validating`, or `Publishing`, then `task resume` to continue from there.

### Four-Role Agent Model

Each role is independently configured in `appsettings.json` under `Agents.Plan`, `Agents.Code`, `Agents.Review`, and `Agents.Pr`. Roles map to `AgentRole` enum; providers are `Claude` (invokes `claude` CLI) or `Codex` (invokes `codex` CLI).

- **Plan**: Decomposes the work item into a `PlanResult` (scope, target repos, validation steps, risks). Output is JSON extracted by `AgentOutputNormalizer`.
- **Code**: Implements the feature in an isolated git worktree. Loops until review approves or `ReviewCycleLimit` is hit.
- **Review**: Critiques the code, outputs structured `ReviewResult` JSON. If findings exist, Code re-runs to address them.
- **Pr**: Generates a structured PR title and description (Classification, Summary, Key impacts, Bug description for bugfix branches) after the commit is made in Publishing. Uses `git log` and `git diff --stat` as input. Defaults to a cheap/fast model (Haiku); configure in `Agents.Pr`. Falls back to a bare-minimum format on failure.

### Repository Isolation

Each work item execution creates a **git worktree** under the configured `WorktreeRoot` (one per repo). Branch names are derived from the work item title (slugified). On successful completion the worktree is optionally cleaned up (`CleanupWorktreesAfterSuccess`).

### Eligibility & Queue Ordering (`RulesEngine`)

- Items must have `AI-Work` or `AI-Rework` tag, state `New`, and be unassigned or assigned to `DefaultAssignee`.
- Items exceeding `MaxPromptComments` × `MaxPromptCommentCharacters` acceptance criteria are tagged `AI-TooLarge` and skipped.
- Queue is ordered by stack rank then priority. `AI-Rework` items are de-duplicated against already-running executions.

### State Persistence

`SqliteStateStore` stores execution records, phase events, plan results, review results, and agent output chunks in `.data/worker.db`. The schema is created on first run.

### ADO Tag / PR Label Lifecycle

| Tag           | Meaning                                           |
| ------------- | ------------------------------------------------- |
| `AI-Work`     | Item is eligible for first-time automation        |
| `AI-Rework`   | Item was previously attempted; needs a retry pass |
| `AI-Done`     | Successfully completed; PR published              |
| `AI-Failed`   | Terminal failure; requires human intervention     |
| `AI-TooLarge` | Acceptance criteria exceeded size limit           |

| PR Label       | Set when                                 | Cleared when                      |
| -------------- | ---------------------------------------- | --------------------------------- |
| `AI-Generated` | PR is created by the worker              | —                                 |
| `AI-Rework`    | Manually by reviewer to request AI fixes | PR Fix run completes successfully |

After a successful AI Work / AI Rework run, `AI-Work`/`AI-Rework` is removed and `AI-Done` is added. A success comment with PR links is posted to the work item.

After a successful PR Fix run, `AI-Done` is added to the work item and the `AI-Rework` PR label is removed. A brief success comment (commit SHA, branch, PR number) is posted to the work item.

### Prompt Templates

Agent prompts are loaded by `PromptTemplateLoader` from `.txt` files in the Infrastructure project. Variable substitution uses `{{VariableName}}` syntax. Prompts are the primary mechanism for shaping agent behavior — changes here affect all runs.

### Configuration Override

Environment variables with prefix `AdoWorker_` override any `appsettings.json` value (double underscore `__` for nesting, e.g. `AdoWorker_AzureDevOps__PersonalAccessToken`).
