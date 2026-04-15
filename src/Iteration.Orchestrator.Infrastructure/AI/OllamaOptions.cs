namespace Iteration.Orchestrator.Infrastructure.AI;

public sealed class OllamaOptions
{
    public string BaseUrl { get; set; } = "http://127.0.0.1:11434";
    public string ExecutablePath { get; set; } = "ollama";
    public string DefaultModel { get; set; } = "qwen2.5-coder:7b";
    public bool AutoStart { get; set; } = true;
    public int StartupTimeoutSeconds { get; set; } = 20;
    public int RequestTimeoutSeconds { get; set; } = 300;
}