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

    [Fact]
    public async Task ApplyAsync_PrependsTemplateWhenFileExists()
    {
        var templatePath = Path.Combine(_rootPath, "agents.md");
        await File.WriteAllTextAsync(templatePath, "## Repository conventions\nAlways write tests.");

        var result = await PromptTemplateLoader.ApplyAsync(
            new AgentRoleConfig { PromptTemplatePath = templatePath },
            "Implement feature X.",
            CancellationToken.None
        );

        Assert.StartsWith("## Repository conventions", result);
        Assert.Contains("Implement feature X.", result);
    }

    [Fact]
    public async Task ApplyAsync_ReturnsOriginalPromptWhenPathIsEmpty()
    {
        var result = await PromptTemplateLoader.ApplyAsync(
            new AgentRoleConfig { PromptTemplatePath = string.Empty },
            "Implement feature X.",
            CancellationToken.None
        );

        Assert.Equal("Implement feature X.", result);
    }

    [Fact]
    public async Task ApplyAsync_TrimsTrailingWhitespaceBeforeJoining()
    {
        var templatePath = Path.Combine(_rootPath, "agents-trailing.md");
        await File.WriteAllTextAsync(templatePath, "Template content.   \n\n   ");

        var result = await PromptTemplateLoader.ApplyAsync(
            new AgentRoleConfig { PromptTemplatePath = templatePath },
            "Main prompt.",
            CancellationToken.None
        );

        Assert.Contains($"Template content.{Environment.NewLine}{Environment.NewLine}Main prompt.", result);
    }

    [Fact]
    public void BuildCodePrompt_IncludesDescriptionAndAcceptanceCriteria()
    {
        var repositoryWorkflowServiceType = typeof(PromptTemplateLoader).Assembly.GetType(
            "Ado.AutonomousWorker.Infrastructure.Orchestration.Handlers.WorkItemCodingHandler",
            throwOnError: true
        );
        Assert.NotNull(repositoryWorkflowServiceType);

        var method = repositoryWorkflowServiceType!.GetMethod(
            "BuildCodePrompt",
            BindingFlags.NonPublic | BindingFlags.Static
        );
        Assert.NotNull(method);

        var repositoryExecutionType = typeof(PromptTemplateLoader).Assembly.GetType(
            "Ado.AutonomousWorker.Infrastructure.Orchestration.RepositoryExecution",
            throwOnError: true
        );
        Assert.NotNull(repositoryExecutionType);

        var repositoryExecution = Activator.CreateInstance(
            repositoryExecutionType!,
            new ChildTaskRecord(28883, RepoKind.Backend, "Backend - Story", "Active"),
            new RepositoryProfile { Kind = RepoKind.Backend, ValidationCommands = ["dotnet build --no-restore"] },
            new RepoExecutionContext(28639, 28883, RepoKind.Backend, "feature/28883_story", @"D:\worktrees\backend"),
            @"D:\artifacts"
        );

        Assert.NotNull(repositoryExecution);

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

        Assert.NotNull(prompt);
        Assert.Contains("Detailed business description.", prompt);
        Assert.Contains("1. Save data", prompt);
        Assert.Contains("Acceptance Criteria:", prompt);
    }

    [Fact]
    public void BuildCodePrompt_IncludesParentFeatureAndComments()
    {
        var repositoryWorkflowServiceType = typeof(PromptTemplateLoader).Assembly.GetType(
            "Ado.AutonomousWorker.Infrastructure.Orchestration.Handlers.WorkItemCodingHandler",
            throwOnError: true
        );
        Assert.NotNull(repositoryWorkflowServiceType);

        var method = repositoryWorkflowServiceType!.GetMethod(
            "BuildCodePrompt",
            BindingFlags.NonPublic | BindingFlags.Static
        );
        Assert.NotNull(method);

        var repositoryExecutionType = typeof(PromptTemplateLoader).Assembly.GetType(
            "Ado.AutonomousWorker.Infrastructure.Orchestration.RepositoryExecution",
            throwOnError: true
        );
        Assert.NotNull(repositoryExecutionType);

        var repositoryExecution = Activator.CreateInstance(
            repositoryExecutionType!,
            new ChildTaskRecord(28883, RepoKind.Backend, "Backend - Story", "Active"),
            new RepositoryProfile { Kind = RepoKind.Backend, ValidationCommands = ["dotnet build --no-restore"] },
            new RepoExecutionContext(28639, 28883, RepoKind.Backend, "feature/28883_story", @"D:\worktrees\backend"),
            @"D:\artifacts"
        );

        Assert.NotNull(repositoryExecution);

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

        Assert.NotNull(prompt);
        Assert.Contains("Parent feature: Feature - Accounts", prompt);
        Assert.Contains("Recent comments:", prompt);
        Assert.Contains("Please fix the failing save flow.", prompt);
        Assert.Contains("Update the validation message.", prompt);
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
