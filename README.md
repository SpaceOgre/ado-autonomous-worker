# Ado.AutonomousWorker

A `.NET 10` console application for automating repetitive Azure DevOps work around PRIIS backlog items.

## Features

- Query eligible `AI-Work` and `AI-Rework` user stories.
- Update work item state, tags, iteration path, comments, and child tasks.
- Orchestrate separate `Plan`, `Code`, and `Review` AI roles with configurable `Codex CLI` and `Claude Code CLI` providers.
- Create isolated git worktrees per repo task.
- Persist execution state in SQLite and keep append-only runtime logs/artifacts.
- Provide a terminal UI with commands for queue inspection, execution, resume, bootstrap, and health checks.

## Commands

```powershell
ado-worker bootstrap
ado-worker doctor
ado-worker queue
ado-worker executions
ado-worker executions --active
ado-worker execution show --id <execution-id>
ado-worker execution show --last
ado-worker run
ado-worker task run --id 12345
ado-worker task resume --execution-id <execution-id>
```

## Configuration

Base configuration lives in:

- `src/Ado.AutonomousWorker.Cli/appsettings.json`

Local machine overrides should go in:

- `src/Ado.AutonomousWorker.Cli/appsettings.local.json`

Set Azure DevOps and provider credentials outside source control.

## Development

```powershell
dotnet build src/Ado.AutonomousWorker.Cli/Ado.AutonomousWorker.Cli.csproj
dotnet test tests/Ado.AutonomousWorker.Tests/Ado.AutonomousWorker.Tests.csproj --no-build
```
