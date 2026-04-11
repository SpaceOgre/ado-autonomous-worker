namespace Ado.AutonomousWorker.Infrastructure.Execution;

internal static class AgentOutputSchemas
{
    public const string Plan = """
    {
      "type": "object",
      "properties": {
        "scope": { "type": "string", "enum": ["Backend", "Frontend", "Fullstack"] },
        "repositories": { "type": "array", "items": { "type": "string" } },
        "validationTargets": { "type": "array", "items": { "type": "string" } },
        "testIntent": { "type": "array", "items": { "type": "string" } },
        "risks": { "type": "array", "items": { "type": "string" } },
        "summary": { "type": "string" }
      },
      "required": ["scope", "repositories", "validationTargets", "testIntent", "risks", "summary"]
    }
    """;

    public const string Review = """
    {
      "type": "object",
      "properties": {
        "summary": { "type": "string" },
        "findings": {
          "type": "array",
          "items": {
            "type": "object",
            "properties": {
              "id": { "type": "string" },
              "severity": { "type": "string" },
              "title": { "type": "string" },
              "rationale": { "type": "string" },
              "filePath": { "type": ["string", "null"] }
            },
            "required": ["id", "severity", "title", "rationale"]
          }
        }
      },
      "required": ["summary", "findings"]
    }
    """;
}
