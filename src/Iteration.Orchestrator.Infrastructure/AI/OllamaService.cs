using Microsoft.Extensions.Options;
using System.Diagnostics;
using System.Net.Http.Json;
using Iteration.Orchestrator.Application.AI;

namespace Iteration.Orchestrator.Infrastructure.AI;

public sealed class OllamaService : ITextGenerationService
{
    private readonly HttpClient _http;
    private readonly OllamaOptions _options;

    public OllamaService(HttpClient http, IOptions<OllamaOptions> options)
    {
        _http = http;
        _options = options.Value;

        _http.Timeout = TimeSpan.FromSeconds(_options.RequestTimeoutSeconds);
    }

    public async Task<string> GenerateAsync(
        string prompt,
        string? model = null,
        CancellationToken ct = default)
    {
        var selectedModel = model ?? _options.DefaultModel;

        await EnsureRunningAsync(ct);

        var payload = new
        {
            model = selectedModel,
            prompt,
            stream = false
        };

        using var response = await _http.PostAsJsonAsync(
            $"{_options.BaseUrl}/api/generate",
            payload,
            ct);

        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<ResponseDto>(cancellationToken: ct);

        return result?.Response ?? string.Empty;
    }

    private async Task EnsureRunningAsync(CancellationToken ct)
    {
        if (await IsHealthyAsync(ct))
            return;

        if (!_options.AutoStart)
            throw new InvalidOperationException("Ollama is not running.");

        StartOllama();

        var timeout = TimeSpan.FromSeconds(_options.StartupTimeoutSeconds);
        var start = DateTime.UtcNow;

        while (DateTime.UtcNow - start < timeout)
        {
            if (await IsHealthyAsync(ct))
                return;

            await Task.Delay(500, ct);
        }

        throw new InvalidOperationException("Ollama did not start in time.");
    }

    private async Task<bool> IsHealthyAsync(CancellationToken ct)
    {
        try
        {
            var res = await _http.GetAsync($"{_options.BaseUrl}/api/tags", ct);
            return res.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private void StartOllama()
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = _options.ExecutablePath,
            Arguments = "serve",
            UseShellExecute = false,
            CreateNoWindow = true
        });
    }

    private sealed class ResponseDto
    {
        public string? Response { get; set; }
    }
}
