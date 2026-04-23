namespace Iteration.Orchestrator.Application.AI;

public sealed record LlmProviderSelection(
    string ProviderName,
    string BaseUrl,
    string Model,
    int TimeoutSeconds,
    bool IsOpenAiConfigurationComplete,
    string? ApiKey = null);
