# AGENTS.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What This Project Does

`Ado.AutonomousWorker` is a .NET 10 console application (CLI tool) that automates end-to-end AI-assisted development for Azure DevOps work items tagged `AI-Work` or `AI-Rework` in the PRIIS backlog. It orchestrates three AI roles — **Plan**, **Code**, and **Review** — against isolated git worktrees, then publishes draft PRs linked to the parent work item.

## Maintenance Rules

**When you add, rename, or remove a CLI command**, you must update all three of these in the same change:

1. **`CliCommandCatalog.cs`** (`src/Ado.AutonomousWorker.Cli/Completion/CliCommandCatalog.cs`) — add the command to `TopLevelCommands` and its options to `CommandOptions`. This drives tab-completion.
2. **`README.md`** — add the command to the `## Commands` section.
3. **`Program.cs`** — register the command class as a singleton and add it to the CLI config.

Forgetting any of these will leave tab-completion or the README out of sync.

## Commands

```bash
# Build
dotnet build src/Ado.AutonomousWorker.Cli/Ado.AutonomousWorker.Cli.csproj

# Run all tests
dotnet test tests/Ado.AutonomousWorker.Tests/Ado.AutonomousWorker.Tests.csproj

# Run a single test class
dotnet test tests/Ado.AutonomousWorker.Tests/Ado.AutonomousWorker.Tests.csproj --filter "FullyQualifiedName~RulesEngineTests"

# Install as local dotnet tool (adds `ado-worker` to the shell)
.\scripts\install-local-tool.ps1

# Run the tool after local install
dotnet tool run ado-worker -- queue
dotnet tool run ado-worker -- run
dotnet tool run ado-worker -- task run --id <workItemId>
dotnet tool run ado-worker -- task resume --execution-id <executionId>
dotnet tool run ado-worker -- doctor
```

Local configuration goes in `src/Ado.AutonomousWorker.Cli/appsettings.local.json` (git-ignored). The PAT and local repo paths live there.

## Architecture

### Project Layout

| Project | Role |
|---|---|
| `Ado.AutonomousWorker.Cli` | CLI entry point, commands, DI wiring, Spectre.Console UI |
| `Ado.AutonomousWorker.Core` | Domain abstractions, models, enums, `RulesEngine` |
| `Ado.AutonomousWorker.Infrastructure` | All real implementations (agents, state, ADO client, workspaces) |
| `Ado.AutonomousWorker.Tests` | xUnit tests for pure/unit-testable components |

### Task Lifecycle — `TaskPhase` enum

```
Queued → Planning → Coding → Reviewing → ApplyingReview → Validating → Publishing → Completed
                                                                                   → Failed
                                                                                   → Skipped
```

The orchestrator (`TaskOrchestrator`) is phase-aware and can **resume** from the `Planning` or `Validating` checkpoints without re-running earlier phases. Phase state is persisted in SQLite.

### Three-Role Agent Model

Each role is independently configured in `appsettings.json` under `Agents.Plan`, `Agents.Code`, and `Agents.Review`. Roles map to `AgentRole` enum; providers are `Claude` (invokes `claude` CLI) or `Codex` (invokes `codex` CLI).

- **Plan**: Decomposes the work item into a `PlanResult` (scope, target repos, validation steps, risks). Output is JSON extracted by `AgentOutputNormalizer`.
- **Code**: Implements the feature in an isolated git worktree. Loops until review approves or `ReviewCycleLimit` is hit.
- **Review**: Critiques the code, outputs structured `ReviewResult` JSON. If findings exist, Code re-runs to address them.

### Repository Isolation

Each work item execution creates a **git worktree** under the configured `WorktreeRoot` (one per repo). Branch names are derived from the work item title (slugified). On successful completion the worktree is optionally cleaned up (`CleanupWorktreesAfterSuccess`).

### Eligibility & Queue Ordering (`RulesEngine`)

- Items must have `AI-Work` or `AI-Rework` tag, state `New`, and be unassigned or assigned to `DefaultAssignee`.
- Items exceeding `MaxPromptComments` × `MaxPromptCommentCharacters` acceptance criteria are tagged `AI-TooLarge` and skipped.
- Queue is ordered by stack rank then priority. `AI-Rework` items are de-duplicated against already-running executions.

### State Persistence

`SqliteStateStore` stores execution records, phase events, plan results, review results, and agent output chunks in `.data/worker.db`. The schema is created on first run.

### ADO Tag Lifecycle

| Tag | Meaning |
|---|---|
| `AI-Work` | Item is eligible for first-time automation |
| `AI-Rework` | Item was previously attempted; needs a retry pass |
| `AI-Done` | Successfully completed; PR published |
| `AI-Failed` | Terminal failure; requires human intervention |
| `AI-TooLarge` | Acceptance criteria exceeded size limit |

After a successful run, `AI-Work`/`AI-Rework` is removed and `AI-Done` is added. A success comment with PR links is posted to the work item.

### Prompt Templates

Agent prompts are loaded by `PromptTemplateLoader` from `.txt` files in the Infrastructure project. Variable substitution uses `{{VariableName}}` syntax. Prompts are the primary mechanism for shaping agent behavior — changes here affect all runs.

### Configuration Override

Environment variables with prefix `AdoWorker_` override any `appsettings.json` value (double underscore `__` for nesting, e.g. `AdoWorker_AzureDevOps__PersonalAccessToken`).
