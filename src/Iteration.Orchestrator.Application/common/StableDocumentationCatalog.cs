namespace Iteration.Orchestrator.Application.Common;

public static class StableDocumentationCatalog
{
    private static readonly string[] CanonicalRelativePaths =
    [
        "context/overview.md",
        "business/business-rules.md",
        "business/workflows.md",
        "architecture/architecture-overview.md",
        "architecture/module-map.md"
    ];

    public static IReadOnlyList<string> GetCanonicalRelativePaths()
        => CanonicalRelativePaths;

    public static string BuildKnowledgeRoot(string repositoryPath, string solutionCode)
        => Path.Combine(repositoryPath, "AI", "solutions", solutionCode.Replace('/', Path.DirectorySeparatorChar));

    public static IReadOnlyList<string> GetRepositoryRelativePaths(string solutionCode)
        => CanonicalRelativePaths
            .Select(path => $"AI/solutions/{solutionCode}/{path}")
            .ToArray();

    public static IReadOnlyList<string> GetExistingRepositoryRelativePaths(string repositoryPath, string solutionCode)
    {
        var knowledgeRoot = BuildKnowledgeRoot(repositoryPath, solutionCode);

        return CanonicalRelativePaths
            .Where(path => File.Exists(Path.Combine(knowledgeRoot, path.Replace('/', Path.DirectorySeparatorChar))))
            .Select(path => $"AI/solutions/{solutionCode}/{path}")
            .ToArray();
    }
}
