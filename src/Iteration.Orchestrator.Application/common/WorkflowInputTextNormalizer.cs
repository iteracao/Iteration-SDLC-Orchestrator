using System.Text.RegularExpressions;

namespace Iteration.Orchestrator.Application.Common;

internal static partial class WorkflowInputTextNormalizer
{
    public static string NormalizeMultiline(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = value.Replace("\r\n", "\n").Replace("\r", "\n");
        var lines = normalized
            .Split('\n')
            .Select(line => line.TrimEnd())
            .ToArray();

        normalized = string.Join("\n", lines).Trim();
        normalized = RepeatedBlankLinesRegex().Replace(normalized, "\n\n");
        return normalized;
    }

    public static string NormalizeSingleLine(string? value)
        => NormalizeMultiline(value).Replace("\n", " ").Trim();

    [GeneratedRegex(@"\n{3,}", RegexOptions.Compiled)]
    private static partial Regex RepeatedBlankLinesRegex();
}
