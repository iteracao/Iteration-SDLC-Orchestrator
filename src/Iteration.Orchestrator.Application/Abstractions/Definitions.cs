namespace Iteration.Orchestrator.Application.Abstractions;

public sealed record TextDocumentInput(
    string Path,
    string Content);

public sealed record WorkflowFileReference(
    string Path,
    string Kind,
    string Purpose,
    string Source);

public sealed record WorkflowInputDefinition(
    string Name,
    string Type,
    bool Required);

public sealed record WorkflowArtifactDefinition(
    string Type,
    string Name);

public sealed record ProfileDefinition(
    string Code,
    string Name,
    string Description,
    IReadOnlyList<TextDocumentInput> Rules);

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
    string Phase,
    string Purpose,
    string PrimaryAgent,
    IReadOnlyList<WorkflowInputDefinition> RequiredInputs,
    IReadOnlyList<string> KnowledgeReads,
    IReadOnlyList<WorkflowArtifactDefinition> ProducedArtifacts,
    IReadOnlyList<string> KnowledgeUpdates,
    IReadOnlyList<string> ExecutionRules,
    IReadOnlyList<string> NextWorkflows);

public sealed record SolutionOverlayDefinition(
    string Code,
    string Name,
    string ProfileCode,
    string? EntryPointSolutionFile,
    IReadOnlyList<TextDocumentInput> KnowledgeDocuments);
