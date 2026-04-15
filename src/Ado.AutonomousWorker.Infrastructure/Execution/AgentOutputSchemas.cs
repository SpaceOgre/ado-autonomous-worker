namespace Ado.AutonomousWorker.Infrastructure.Execution;

public static class AgentOutputSchemas
{
    public const string Plan = """
    {
      "type": "object",
      "additionalProperties": false,
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

    public const string PullRequestDescription = """
    {
      "type": "object",
      "additionalProperties": false,
      "properties": {
        "title": { "type": "string" },
        "description": { "type": "string" }
      },
      "required": ["title", "description"]
    }
    """;

    public const string PrFixCommit = """
    {
      "type": "object",
      "additionalProperties": false,
      "properties": {
        "commitMessage": { "type": "string" }
      },
      "required": ["commitMessage"]
    }
    """;

    public const string Review = """
    {
      "type": "object",
      "additionalProperties": false,
      "properties": {
        "summary": { "type": "string" },
        "findings": {
          "type": "array",
          "items": {
            "type": "object",
            "additionalProperties": false,
            "properties": {
              "id": { "type": "string" },
              "severity": { "type": "string" },
              "title": { "type": "string" },
              "rationale": { "type": "string" },
              "filePath": { "anyOf": [{ "type": "string" }, { "type": "null" }] }
            },
            "required": ["id", "severity", "title", "rationale", "filePath"]
          }
        }
      },
      "required": ["summary", "findings"]
    }
    """;
}
