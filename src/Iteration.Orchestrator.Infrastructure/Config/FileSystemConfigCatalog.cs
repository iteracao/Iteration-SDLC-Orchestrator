using Iteration.Orchestrator.Application.Abstractions;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Iteration.Orchestrator.Infrastructure.Config;

public sealed class FileSystemConfigCatalog : IConfigCatalog
{
    private readonly string _root;
    private readonly IDeserializer _yaml;

    public FileSystemConfigCatalog(string root)
    {
        _root = root;
        _yaml = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();
    }

    public async Task<WorkflowDefinition> GetWorkflowAsync(string workflowCode, CancellationToken ct)
    {
        var path = Path.Combine(_root, "workflows", workflowCode, "workflow.yaml");
        var yaml = await File.ReadAllTextAsync(path, ct);
        var dto = _yaml.Deserialize<WorkflowYaml>(yaml);

        return new WorkflowDefinition(
            dto.Code,
            dto.Name,
            dto.Phase,
            dto.Purpose,
            dto.PrimaryAgent,
            (dto.RequiredInputs ?? [])
                .Select(x => new WorkflowInputDefinition(x.Name, x.Type, x.Required))
                .ToList(),
            dto.KnowledgeReads ?? [],
            (dto.ProducedArtifacts ?? [])
                .Select(x => new WorkflowArtifactDefinition(x.Type, x.Name))
                .ToList(),
            dto.KnowledgeUpdates ?? [],
            dto.ExecutionRules ?? [],
            dto.NextWorkflows ?? []);
    }

    public async Task<AgentDefinition> GetAgentAsync(string agentCode, CancellationToken ct)
    {
        var folder = Path.Combine(_root, "agents", agentCode);
        var yaml = await File.ReadAllTextAsync(Path.Combine(folder, "agent.yaml"), ct);
        var dto = _yaml.Deserialize<AgentYaml>(yaml);
        var prompt = await File.ReadAllTextAsync(Path.Combine(folder, dto.PromptFile), ct);
        var schema = string.IsNullOrWhiteSpace(dto.OutputSchema)
            ? string.Empty
            : await File.ReadAllTextAsync(Path.Combine(folder, dto.OutputSchema), ct);
        return new AgentDefinition(dto.Code, dto.Name, dto.Description, dto.AllowedTools ?? [], prompt, schema);
    }

    public async Task<ProfileDefinition> GetProfileAsync(string profileCode, CancellationToken ct)
    {
        var folder = Path.Combine(_root, "profiles", profileCode);
        var yaml = await File.ReadAllTextAsync(Path.Combine(folder, "profile.yaml"), ct);
        var dto = _yaml.Deserialize<ProfileYaml>(yaml);

        var rules = new List<TextDocumentInput>();
        foreach (var relativePath in dto.Rules ?? [])
        {
            var path = Path.Combine(folder, relativePath.Replace('/', Path.DirectorySeparatorChar));
            var content = await File.ReadAllTextAsync(path, ct);
            rules.Add(new TextDocumentInput(relativePath, content));
        }

        return new ProfileDefinition(dto.Code, dto.Name, dto.Description, rules);
    }

    public async Task<SolutionOverlayDefinition> GetSolutionOverlayAsync(string solutionCode, CancellationToken ct)
    {
        var folder = Path.Combine(_root, "solutions", solutionCode);
        var yaml = await File.ReadAllTextAsync(Path.Combine(folder, "solution.yaml"), ct);
        var dto = _yaml.Deserialize<SolutionYaml>(yaml);

        var knowledgeDocuments = new List<TextDocumentInput>();
        foreach (var relativePath in dto.KnowledgeFiles ?? [])
        {
            var path = Path.Combine(folder, relativePath.Replace('/', Path.DirectorySeparatorChar));
            var content = await File.ReadAllTextAsync(path, ct);
            knowledgeDocuments.Add(new TextDocumentInput(relativePath, content));
        }

        return new SolutionOverlayDefinition(
            dto.Code,
            dto.Name,
            dto.Profile,
            dto.EntryPoints?.SolutionFile,
            knowledgeDocuments);
    }

    public Task<string> ReadFrameworkTextAsync(string relativePath, CancellationToken ct)
    {
        var normalizedPath = relativePath.Replace('/', Path.DirectorySeparatorChar);
        var path = Path.Combine(_root, normalizedPath);
        return File.ReadAllTextAsync(path, ct);
    }

    private sealed class WorkflowYaml
    {
        public string Code { get; set; } = "";
        public string Name { get; set; } = "";
        public string Phase { get; set; } = "";
        public string Purpose { get; set; } = "";
        public string PrimaryAgent { get; set; } = "";
        public List<WorkflowInputYaml>? RequiredInputs { get; set; }
        public List<string>? KnowledgeReads { get; set; }
        public List<WorkflowArtifactYaml>? ProducedArtifacts { get; set; }
        public List<string>? KnowledgeUpdates { get; set; }
        public List<string>? ExecutionRules { get; set; }
        public List<string>? NextWorkflows { get; set; }
    }

    private sealed class WorkflowInputYaml
    {
        public string Name { get; set; } = "";
        public string Type { get; set; } = "string";
        public bool Required { get; set; }
    }

    private sealed class WorkflowArtifactYaml
    {
        public string Type { get; set; } = "";
        public string Name { get; set; } = "";
    }

    private sealed class AgentYaml
    {
        public string Code { get; set; } = "";
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        public List<string>? AllowedTools { get; set; }
        public string OutputSchema { get; set; } = "";
        public string PromptFile { get; set; } = "prompt.md";
    }

    private sealed class ProfileYaml
    {
        public string Code { get; set; } = "";
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        public List<string>? Rules { get; set; }
    }

    private sealed class SolutionYaml
    {
        public string Code { get; set; } = "";
        public string Name { get; set; } = "";
        public string Profile { get; set; } = "";
        public SolutionEntryPointsYaml? EntryPoints { get; set; }
        public List<string>? KnowledgeFiles { get; set; }
    }

    private sealed class SolutionEntryPointsYaml
    {
        public string? SolutionFile { get; set; }
    }
}
