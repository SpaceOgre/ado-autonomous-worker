namespace Ado.AutonomousWorker.Infrastructure.Orchestration;

internal static class WorkItemPromptContextFormatter
{
    public static string FormatComments(IReadOnlyList<string> comments, int maxComments, int maxCharacters)
    {
        if (comments.Count == 0 || maxComments <= 0 || maxCharacters <= 0)
        {
            return "-";
        }

        var selectedComments = comments
            .Where(comment => !string.IsNullOrWhiteSpace(comment))
            .TakeLast(maxComments)
            .Select(comment => comment.Trim())
            .ToArray();

        if (selectedComments.Length == 0)
        {
            return "-";
        }

        var lines = new List<string>(selectedComments.Length);
        var usedCharacters = 0;

        foreach (var comment in selectedComments)
        {
            var remaining = maxCharacters - usedCharacters;
            if (remaining <= 0)
            {
                break;
            }

            var trimmedComment =
                comment.Length <= remaining ? comment : $"{comment[..Math.Max(0, remaining - 3)].TrimEnd()}...";

            lines.Add($"- {trimmedComment}");
            usedCharacters += trimmedComment.Length;
        }

        return lines.Count == 0 ? "-" : string.Join(Environment.NewLine + Environment.NewLine, lines);
    }
}
