using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Iteration.Orchestrator.Application.AI;
using Microsoft.Extensions.Options;

namespace Iteration.Orchestrator.Infrastructure.AI;

public sealed class OpenAITextGenerationService : ITextGenerationService
{
    private readonly HttpClient _http;
    private readonly OpenAIOptions _options;

    public OpenAITextGenerationService(HttpClient http, IOptions<OpenAIOptions> options)
    {
        _http = http;
        _options = options.Value;
        _http.Timeout = TimeSpan.FromSeconds(Math.Max(1, _options.TimeoutSeconds));
    }

    public async Task<string> GenerateAsync(string prompt, string? model = null, CancellationToken ct = default)
    {
        if (!_options.IsComplete())
        {
            throw new InvalidOperationException("OpenAI configuration is incomplete.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, BuildResponsesUrl(_options.BaseUrl));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
        request.Content = JsonContent.Create(new
        {
            model = string.IsNullOrWhiteSpace(model) ? _options.Model : model,
            input = prompt
        });

        using var response = await _http.SendAsync(request, ct);
        var responseContent = await response.Content.ReadAsStringAsync(ct);
        response.EnsureSuccessStatusCode();

        using var document = JsonDocument.Parse(responseContent);
        return ExtractOutputText(document.RootElement);
    }

    private static string BuildResponsesUrl(string baseUrl)
        => $"{baseUrl.TrimEnd('/')}/responses";

    private static string ExtractOutputText(JsonElement root)
    {
        if (root.TryGetProperty("output_text", out var outputTextElement)
            && outputTextElement.ValueKind == JsonValueKind.String)
        {
            return outputTextElement.GetString() ?? string.Empty;
        }

        if (!root.TryGetProperty("output", out var outputElement) || outputElement.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        foreach (var item in outputElement.EnumerateArray())
        {
            if (!item.TryGetProperty("content", out var contentElement) || contentElement.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var content in contentElement.EnumerateArray())
            {
                if (!content.TryGetProperty("text", out var textElement) || textElement.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var text = textElement.GetString();
                if (string.IsNullOrWhiteSpace(text))
                {
                    continue;
                }

                if (sb.Length > 0)
                {
                    sb.AppendLine();
                }

                sb.Append(text);
            }
        }

        return sb.ToString();
    }
}
