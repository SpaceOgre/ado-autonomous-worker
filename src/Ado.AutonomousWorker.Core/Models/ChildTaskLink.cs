namespace Ado.AutonomousWorker.Core.Models;

public sealed record ChildTaskLink(int WorkItemId, string RepoKind, string Title);
