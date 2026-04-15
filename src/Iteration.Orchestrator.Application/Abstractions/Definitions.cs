namespace Iteration.Orchestrator.Application.Abstractions;

public sealed record ProfileDefinition(
    string Code,
    string Name,
    string Description,
    IReadOnlyList<string> RuleFiles);

public sealed record AgentDefinition(
    string Code,
    string Name,
    string Description,
    IReadOnlyList<string> AllowedTools,
    string PromptText,
    string OutputSchemaJson);

public sealed record WorkflowDefinition(
    string Code,
    string Name,
    string Description,
    IReadOnlyList<string> ApplicableProfiles,
    IReadOnlyList<string> AgentCodes);

public sealed record SolutionOverlayDefinition(
    string Code,
    string Name,
    string ProfileCode,
    IReadOnlyList<string> KnowledgeFiles);
