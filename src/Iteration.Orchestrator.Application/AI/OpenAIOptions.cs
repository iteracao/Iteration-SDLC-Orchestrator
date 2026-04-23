namespace Iteration.Orchestrator.Application.AI;

public sealed class OpenAIOptions
{
    public string ApiKey { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = "https://api.openai.com/v1";
    public string Model { get; set; } = "gpt-5.4";
    public int TimeoutSeconds { get; set; } = 180;

    public bool IsComplete()
        => !string.IsNullOrWhiteSpace(ApiKey)
           && !string.IsNullOrWhiteSpace(BaseUrl)
           && !string.IsNullOrWhiteSpace(Model);
}
