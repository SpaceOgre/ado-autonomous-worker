using System.Reflection;
using Ado.AutonomousWorker.Core.Enums;
using Ado.AutonomousWorker.Infrastructure.Orchestration;

namespace Ado.AutonomousWorker.Tests;

public sealed class WorkItemCommentFormatterTests
{
    [Fact]
    public void BuildFailureComment_ForValidationFailure_IncludesCommandAndLogPath()
    {
        var formatterType = typeof(PromptTemplateLoader).Assembly.GetType(
            "Ado.AutonomousWorker.Infrastructure.Orchestration.WorkItemCommentFormatter",
            throwOnError: true);
        var validationFailureType = typeof(PromptTemplateLoader).Assembly.GetType(
            "Ado.AutonomousWorker.Infrastructure.Orchestration.ValidationFailureException",
            throwOnError: true);

        Assert.NotNull(formatterType);
        Assert.NotNull(validationFailureType);

        var exception = (Exception?)Activator.CreateInstance(
            validationFailureType!,
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
            binder: null,
            args:
            [
                RepoKind.Backend,
                "dotnet test tests/Backend.Tests.csproj --no-build",
                @"C:\logs\backend-validation.log",
                string.Empty,
                "Expected 200 OK but got 500.",
            ],
            culture: null);

        Assert.NotNull(exception);

        var method = formatterType!.GetMethod("BuildFailureComment", BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(method);

        var comment = (string?)method!.Invoke(null, ["exec-123", exception!]);

        Assert.NotNull(comment);
        Assert.Contains("Execution exec-123 failed.", comment);
        Assert.Contains("Validation failed for Backend.", comment);
        Assert.Contains("dotnet test tests/Backend.Tests.csproj --no-build", comment);
        Assert.Contains(@"C:\logs\backend-validation.log", comment);
        Assert.Contains("Expected 200 OK but got 500.", comment);
    }
}
