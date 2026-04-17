using System.Reflection;
using Ado.AutonomousWorker.Core.Enums;
using Ado.AutonomousWorker.Infrastructure.Orchestration;

namespace Ado.AutonomousWorker.Tests;

public sealed class WorkItemCommentFormatterTests
{
    [Test]
    public async Task BuildFailureComment_ForValidationFailure_IncludesCommandAndLogPath()
    {
        var formatterType = typeof(PromptTemplateLoader).Assembly.GetType(
            "Ado.AutonomousWorker.Infrastructure.Orchestration.WorkItemCommentFormatter",
            throwOnError: true
        );
        var validationFailureType = typeof(PromptTemplateLoader).Assembly.GetType(
            "Ado.AutonomousWorker.Infrastructure.Orchestration.ValidationFailureException",
            throwOnError: true
        );

        await Assert.That(formatterType).IsNotNull();
        await Assert.That(validationFailureType).IsNotNull();

        var exception = (Exception?)
            Activator.CreateInstance(
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
                culture: null
            );

        await Assert.That(exception).IsNotNull();

        var method = formatterType!.GetMethod("BuildFailureComment", BindingFlags.Public | BindingFlags.Static);
        await Assert.That(method).IsNotNull();

        var comment = (string?)method!.Invoke(null, ["exec-123", exception!]);

        await Assert.That(comment).IsNotNull();
        await Assert.That(comment).Contains("Execution exec-123 failed.");
        await Assert.That(comment).Contains("Validation failed for Backend.");
        await Assert.That(comment).Contains("dotnet test tests/Backend.Tests.csproj --no-build");
        await Assert.That(comment).Contains(@"C:\logs\backend-validation.log");
        await Assert.That(comment).Contains("Expected 200 OK but got 500.");
    }
}
