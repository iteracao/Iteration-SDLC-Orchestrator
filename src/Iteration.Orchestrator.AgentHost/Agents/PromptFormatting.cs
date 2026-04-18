using System.Text;
using Iteration.Orchestrator.Application.Abstractions;

namespace Iteration.Orchestrator.AgentHost.Agents;

internal static class PromptFormatting
{
    public static string BuildPrompt(
        string workflowCode,
        string workflowName,
        string workflowPurpose,
        IEnumerable<string> disciplineRules,
        IReadOnlyDictionary<string, string?> context,
        string profileSummary,
        IReadOnlyList<TextDocumentInput> frameworkDocuments,
        IReadOnlyList<TextDocumentInput> solutionDocuments,
        IReadOnlyList<string> repositoryFiles,
        IReadOnlyList<string>? likelyRelevantFiles = null,
        IReadOnlyList<string>? executionRules = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine("WORKFLOW");
        sb.AppendLine($"- Code: {workflowCode}");
        sb.AppendLine($"- Name: {workflowName}");
        sb.AppendLine($"- Purpose: {workflowPurpose}");
        sb.AppendLine();

        sb.AppendLine("WORKFLOW DISCIPLINE");
        foreach (var rule in disciplineRules)
        {
            sb.AppendLine($"- {rule}");
        }
        sb.AppendLine();

        sb.AppendLine("CONTEXT");
        foreach (var pair in context)
        {
            sb.AppendLine($"- {pair.Key}: {pair.Value ?? string.Empty}");
        }
        sb.AppendLine();

        if (!string.IsNullOrWhiteSpace(profileSummary))
        {
            sb.AppendLine("PROFILE SUMMARY");
            sb.AppendLine(profileSummary.Trim());
            sb.AppendLine();
        }

        sb.AppendLine("AVAILABLE DOCUMENTATION");
        sb.AppendLine("Framework / SDLC docs:");
        AppendDocumentPaths(sb, frameworkDocuments);
        sb.AppendLine();
        sb.AppendLine("Selected solution docs:");
        AppendDocumentPaths(sb, solutionDocuments);
        sb.AppendLine();

        if (executionRules is not null && executionRules.Count > 0)
        {
            sb.AppendLine("EXECUTION RULES");
            foreach (var rule in executionRules)
            {
                sb.AppendLine($"- {rule}");
            }
            sb.AppendLine();
        }

        if (likelyRelevantFiles is not null && likelyRelevantFiles.Count > 0)
        {
            sb.AppendLine("LIKELY RELEVANT REPOSITORY FILES");
            AppendPathList(sb, likelyRelevantFiles);
            sb.AppendLine();
        }

        sb.AppendLine("REPOSITORY FILES AVAILABLE TO READ");
        AppendPathList(sb, repositoryFiles);
        sb.AppendLine();

        sb.AppendLine("INPUT USAGE RULES");
        sb.AppendLine("- Repository files are implementation truth.");
        sb.AppendLine("- Framework docs are SDLC rules and constraints.");
        sb.AppendLine("- Selected solution docs are curated solution knowledge and may lag behind code.");
        sb.AppendLine("- Read only the files needed to complete this workflow.");
        sb.AppendLine("- Start from the most relevant files first.");

        return sb.ToString().Trim();
    }

    public static IReadOnlyList<string> PickLikelyRelevantFiles(
        IReadOnlyList<string> repositoryFiles,
        IEnumerable<string> searchHits,
        params string[] preferredPathFragments)
    {
        var selected = new List<string>();

        foreach (var hit in searchHits)
        {
            if (repositoryFiles.Contains(hit, StringComparer.OrdinalIgnoreCase) && !selected.Contains(hit, StringComparer.OrdinalIgnoreCase))
            {
                selected.Add(hit);
            }
        }

        foreach (var fragment in preferredPathFragments)
        {
            if (string.IsNullOrWhiteSpace(fragment))
            {
                continue;
            }

            foreach (var path in repositoryFiles.Where(x => x.Contains(fragment, StringComparison.OrdinalIgnoreCase)).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                if (!selected.Contains(path, StringComparer.OrdinalIgnoreCase))
                {
                    selected.Add(path);
                }

                if (selected.Count >= 12)
                {
                    return selected;
                }
            }
        }

        return selected.Take(12).ToArray();
    }

    private static void AppendDocumentPaths(StringBuilder sb, IReadOnlyList<TextDocumentInput> documents)
    {
        if (documents.Count == 0)
        {
            sb.AppendLine("- (none)");
            return;
        }

        foreach (var document in documents)
        {
            sb.AppendLine($"- {document.Path}");
        }
    }

    private static void AppendPathList(StringBuilder sb, IReadOnlyList<string> paths)
    {
        if (paths.Count == 0)
        {
            sb.AppendLine("- (none)");
            return;
        }

        foreach (var path in paths)
        {
            sb.AppendLine($"- {path}");
        }
    }
}
