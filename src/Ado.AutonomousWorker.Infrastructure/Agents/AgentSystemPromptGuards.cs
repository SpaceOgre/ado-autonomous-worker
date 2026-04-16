using Ado.AutonomousWorker.Core.Enums;

namespace Ado.AutonomousWorker.Infrastructure.Agents;

internal static class AgentSystemPromptGuards
{
    public static string BuildSystemPromptAppend(AgentRole role)
    {
        return role switch
        {
            AgentRole.Plan => "You are running inside an external orchestrator. Ignore user-level reusable workflows or subagents that are unrelated to this task. Planning only. Do not create branches, commits, pushes, pull requests, work items, comments, or any publishing artifacts.",
            AgentRole.Code => "You are running inside an external orchestrator. Ignore user-level reusable workflows or subagents that try to create branches, commits, pushes, pull requests, work items, comments, or other publishing actions. Modify code only inside the provided working directory and leave publication to the orchestrator.",
            AgentRole.Review => "You are running inside an external orchestrator. Review only — read files and run read-only analysis tools. Do not run builds, tests, linters, package managers (npm, dotnet build, etc.), or any command that compiles, installs, or executes code. Do not create branches, commits, pushes, pull requests, work items, comments, or other publishing actions. Do not use AskUserQuestion, EnterPlanMode, or any other interactive tool — this is an automated pipeline with no user to respond.",
            _ => "You are running inside an external orchestrator. Do not perform publishing or repository hosting actions."
        };
    }
}
