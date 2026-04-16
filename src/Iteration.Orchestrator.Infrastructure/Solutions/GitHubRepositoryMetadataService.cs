using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Iteration.Orchestrator.Application.Abstractions;

namespace Iteration.Orchestrator.Infrastructure.Solutions;

public sealed class GitHubRepositoryMetadataService : IGitHubRepositoryMetadataService
{
    private const string MissingPrivateTokenMessage = "Repository may be private. Configure GITHUB_TOKEN in environment variables.";
    private readonly HttpClient _httpClient;

    public GitHubRepositoryMetadataService(HttpClient httpClient)
    {
        _httpClient = httpClient;
        if (_httpClient.BaseAddress is null)
        {
            _httpClient.BaseAddress = new Uri("https://api.github.com/");
        }

        if (!_httpClient.DefaultRequestHeaders.UserAgent.Any())
        {
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Iteration-SDLC-Orchestrator");
        }

        if (!_httpClient.DefaultRequestHeaders.Accept.Any())
        {
            _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        }
    }

    public async Task<GitHubRepositoryMetadata> GetMetadataAsync(string remoteRepositoryUrl, CancellationToken ct)
    {
        var repository = ParseRepository(remoteRepositoryUrl);

        Console.WriteLine($"Owner = {repository.Owner}");
        Console.WriteLine($"Name = {repository.Name}");

        using var publicRequest = CreateRequest(repository.Owner, repository.Name, token: null);
        using var publicResponse = await _httpClient.SendAsync(publicRequest, ct);

        if (publicResponse.IsSuccessStatusCode)
        {
            return await ReadMetadataAsync(publicResponse, repository.Owner, repository.Name, ct);
        }

        if (publicResponse.StatusCode != HttpStatusCode.NotFound)
        {
            var publicError = await ReadErrorAsync(publicResponse, ct);
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(publicError)
                ? "GitHub repository could not be validated."
                : publicError);
        }

        var token = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new InvalidOperationException(MissingPrivateTokenMessage);
        }

        using var privateRequest = CreateRequest(repository.Owner, repository.Name, token);
        using var privateResponse = await _httpClient.SendAsync(privateRequest, ct);

        if (privateResponse.IsSuccessStatusCode)
        {
            return await ReadMetadataAsync(privateResponse, repository.Owner, repository.Name, ct);
        }

        if (privateResponse.StatusCode == HttpStatusCode.Unauthorized || privateResponse.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new InvalidOperationException("GitHub repository is not accessible with the configured GITHUB_TOKEN.");
        }

        if (privateResponse.StatusCode == HttpStatusCode.NotFound)
        {
            throw new InvalidOperationException("GitHub repository was not found.");
        }

        var privateError = await ReadErrorAsync(privateResponse, ct);
        throw new InvalidOperationException(string.IsNullOrWhiteSpace(privateError)
            ? "GitHub repository could not be validated."
            : privateError);
    }

    private static HttpRequestMessage CreateRequest(string owner, string name, string? token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"repos/{owner}/{name}");
        if (!string.IsNullOrWhiteSpace(token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Trim());
        }

        return request;
    }

    private static async Task<GitHubRepositoryMetadata> ReadMetadataAsync(HttpResponseMessage response, string owner, string fallbackName, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var payload = await JsonSerializer.DeserializeAsync<GitHubRepositoryResponse>(stream, cancellationToken: ct)
            ?? throw new InvalidOperationException("GitHub repository metadata response was empty.");

        return new GitHubRepositoryMetadata(
            owner,
            string.IsNullOrWhiteSpace(payload.Name) ? fallbackName : payload.Name,
            string.IsNullOrWhiteSpace(payload.DefaultBranch) ? "main" : payload.DefaultBranch,
            payload.Private);
    }

    private static async Task<string> ReadErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        if (stream == Stream.Null)
        {
            return string.Empty;
        }

        try
        {
            var payload = await JsonSerializer.DeserializeAsync<GitHubErrorResponse>(stream, cancellationToken: ct);
            return payload?.Message ?? string.Empty;
        }
        catch (JsonException)
        {
            return string.Empty;
        }
    }

    private static (string Owner, string Name) ParseRepository(string remoteRepositoryUrl)
    {
        var candidate = remoteRepositoryUrl.Trim();
        if (string.IsNullOrWhiteSpace(candidate))
        {
            throw new InvalidOperationException("GitHub repository URL is required.");
        }

        if (Uri.TryCreate(candidate, UriKind.Absolute, out var uri))
        {
            if (!string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("GitHub repository URL must point to github.com.");
            }

            var segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (segments.Length < 2)
            {
                throw new InvalidOperationException("GitHub repository URL is invalid.");
            }

            return (segments[0], NormalizeRepositoryName(segments[1]));
        }

        if (candidate.StartsWith("git@github.com:", StringComparison.OrdinalIgnoreCase))
        {
            var path = candidate[15..].Trim();
            var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (segments.Length < 2)
            {
                throw new InvalidOperationException("GitHub repository URL is invalid.");
            }

            return (segments[0], NormalizeRepositoryName(segments[1]));
        }

        throw new InvalidOperationException("GitHub repository URL is invalid.");
    }

    private static string NormalizeRepositoryName(string value)
        => value.EndsWith(".git", StringComparison.OrdinalIgnoreCase)
            ? value[..^4]
            : value;

    private sealed class GitHubRepositoryResponse
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("default_branch")]
        public string DefaultBranch { get; set; } = string.Empty;

        [JsonPropertyName("private")]
        public bool Private { get; set; }
    }

    private sealed class GitHubErrorResponse
    {
        [JsonPropertyName("message")]
        public string Message { get; set; } = string.Empty;
    }
}
