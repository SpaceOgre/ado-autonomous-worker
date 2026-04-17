using System.Reflection;
using Ado.AutonomousWorker.Core.Configuration;
using Ado.AutonomousWorker.Core.Enums;
using Ado.AutonomousWorker.Core.Models;
using Ado.AutonomousWorker.Infrastructure.Orchestration;

namespace Ado.AutonomousWorker.Tests;

public sealed class PromptTemplateLoaderTests : IDisposable
{
    private readonly string _rootPath = Path.Combine(
        Path.GetTempPath(),
        "ado-worker-tests",
        Guid.NewGuid().ToString("N")
    );

    public PromptTemplateLoaderTests()
    {
        Directory.CreateDirectory(_rootPath);
    }

    [Test]
    public async Task ApplyAsync_PrependsTemplateWhenFileExists()
    {
        var templatePath = Path.Combine(_rootPath, "agents.md");
        await File.WriteAllTextAsync(templatePath, "## Repository conventions\nAlways write tests.");

        var result = await PromptTemplateLoader.ApplyAsync(
            new AgentRoleConfig { PromptTemplatePath = templatePath },
            "Implement feature X.",
            CancellationToken.None
        );

        await Assert.That(result).StartsWith("## Repository conventions");
        await Assert.That(result).Contains("Implement feature X.");
    }

    [Test]
    public async Task ApplyAsync_ReturnsOriginalPromptWhenPathIsEmpty()
    {
        var result = await PromptTemplateLoader.ApplyAsync(
            new AgentRoleConfig { PromptTemplatePath = string.Empty },
            "Implement feature X.",
            CancellationToken.None
        );

        await Assert.That(result).IsEqualTo("Implement feature X.");
    }

    [Test]
    public async Task ApplyAsync_TrimsTrailingWhitespaceBeforeJoining()
    {
        var templatePath = Path.Combine(_rootPath, "agents-trailing.md");
        await File.WriteAllTextAsync(templatePath, "Template content.   \n\n   ");

        var result = await PromptTemplateLoader.ApplyAsync(
            new AgentRoleConfig { PromptTemplatePath = templatePath },
            "Main prompt.",
            CancellationToken.None
        );

        await Assert.That(result).Contains($"Template content.{Environment.NewLine}{Environment.NewLine}Main prompt.");
    }

    [Test]
    public async Task BuildCodePrompt_IncludesDescriptionAndAcceptanceCriteria()
    {
        var repositoryWorkflowServiceType = typeof(PromptTemplateLoader).Assembly.GetType(
            "Ado.AutonomousWorker.Infrastructure.Orchestration.Handlers.WorkItemCodingHandler",
            throwOnError: true
        );
        await Assert.That(repositoryWorkflowServiceType).IsNotNull();

        var method = repositoryWorkflowServiceType!.GetMethod(
            "BuildCodePrompt",
            BindingFlags.NonPublic | BindingFlags.Static
        );
        await Assert.That(method).IsNotNull();

        var repositoryExecutionType = typeof(PromptTemplateLoader).Assembly.GetType(
            "Ado.AutonomousWorker.Infrastructure.Orchestration.RepositoryExecution",
            throwOnError: true
        );
        await Assert.That(repositoryExecutionType).IsNotNull();

        var repositoryExecution = Activator.CreateInstance(
            repositoryExecutionType!,
            new ChildTaskRecord(28883, RepoKind.Backend, "Backend - Story", "Active"),
            new RepositoryProfile { Kind = RepoKind.Backend, ValidationCommands = ["dotnet build --no-restore"] },
            new RepoExecutionContext(28639, 28883, RepoKind.Backend, "feature/28883_story", @"D:\worktrees\backend"),
            @"D:\artifacts"
        );

        await Assert.That(repositoryExecution).IsNotNull();

        var repositoryExecutions = Array.CreateInstance(repositoryExecutionType!, 1);
        repositoryExecutions.SetValue(repositoryExecution, 0);

        var prompt = (string?)
            method!.Invoke(
                null,
                [
                    new WorkItemDetails
                    {
                        WorkItemId = 28639,
                        WorkItemType = WorkItemType.UserStory,
                        Title = "Story",
                        Description = "Detailed business description.",
                        AcceptanceCriteria = "1. Save data\n2. Show result",
                    },
                    repositoryExecutions,
                    new PlanResult(
                        WorkItemScope.Backend,
                        ["Backend"],
                        ["Backend validation"],
                        ["Run backend tests"],
                        [],
                        "Implement backend flow."
                    ),
                    @"D:\worktrees",
                    "- Latest comment context.",
                ]
            );

        await Assert.That(prompt).IsNotNull();
        await Assert.That(prompt).Contains("Detailed business description.");
        await Assert.That(prompt).Contains("1. Save data");
        await Assert.That(prompt).Contains("Acceptance Criteria:");
    }

    [Test]
    public async Task BuildCodePrompt_IncludesParentFeatureAndComments()
    {
        var repositoryWorkflowServiceType = typeof(PromptTemplateLoader).Assembly.GetType(
            "Ado.AutonomousWorker.Infrastructure.Orchestration.Handlers.WorkItemCodingHandler",
            throwOnError: true
        );
        await Assert.That(repositoryWorkflowServiceType).IsNotNull();

        var method = repositoryWorkflowServiceType!.GetMethod(
            "BuildCodePrompt",
            BindingFlags.NonPublic | BindingFlags.Static
        );
        await Assert.That(method).IsNotNull();

        var repositoryExecutionType = typeof(PromptTemplateLoader).Assembly.GetType(
            "Ado.AutonomousWorker.Infrastructure.Orchestration.RepositoryExecution",
            throwOnError: true
        );
        await Assert.That(repositoryExecutionType).IsNotNull();

        var repositoryExecution = Activator.CreateInstance(
            repositoryExecutionType!,
            new ChildTaskRecord(28883, RepoKind.Backend, "Backend - Story", "Active"),
            new RepositoryProfile { Kind = RepoKind.Backend, ValidationCommands = ["dotnet build --no-restore"] },
            new RepoExecutionContext(28639, 28883, RepoKind.Backend, "feature/28883_story", @"D:\worktrees\backend"),
            @"D:\artifacts"
        );

        await Assert.That(repositoryExecution).IsNotNull();

        var repositoryExecutions = Array.CreateInstance(repositoryExecutionType!, 1);
        repositoryExecutions.SetValue(repositoryExecution, 0);

        var prompt = (string?)
            method!.Invoke(
                null,
                [
                    new WorkItemDetails
                    {
                        WorkItemId = 28639,
                        WorkItemType = WorkItemType.UserStory,
                        Title = "Story",
                        ParentFeatureTitle = "Feature - Accounts",
                        Comments = ["Please fix the failing save flow.", "Update the validation message."],
                    },
                    repositoryExecutions,
                    new PlanResult(
                        WorkItemScope.Backend,
                        ["Backend"],
                        ["Backend validation"],
                        ["Run backend tests"],
                        [],
                        "Implement backend flow."
                    ),
                    @"D:\worktrees",
                    "- Please fix the failing save flow.\n\n- Update the validation message.",
                ]
            );

        await Assert.That(prompt).IsNotNull();
        await Assert.That(prompt).Contains("Parent feature: Feature - Accounts");
        await Assert.That(prompt).Contains("Recent comments:");
        await Assert.That(prompt).Contains("Please fix the failing save flow.");
        await Assert.That(prompt).Contains("Update the validation message.");
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_rootPath))
            {
                Directory.Delete(_rootPath, recursive: true);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
