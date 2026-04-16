using System.Text.Json;
using Iteration.Orchestrator.Application.Abstractions;
using Iteration.Orchestrator.Domain.Requirements;
using Microsoft.EntityFrameworkCore;

namespace Iteration.Orchestrator.Application.Requirements;

public sealed record CreateRequirementCommand(
    string Title,
    string Description,
    Guid TargetSolutionId,
    string Priority,
    string RequirementType,
    string Source,
    string Status,
    IReadOnlyList<string> AcceptanceCriteria,
    IReadOnlyList<string> Constraints);

public sealed class CreateRequirementHandler
{
    private readonly IAppDbContext _db;

    public CreateRequirementHandler(IAppDbContext db)
    {
        _db = db;
    }

    public async Task<Guid> HandleAsync(CreateRequirementCommand command, CancellationToken ct)
    {
        var solutionExists = await _db.Solutions.AnyAsync(x => x.Id == command.TargetSolutionId, ct);
        if (!solutionExists)
        {
            throw new InvalidOperationException("Target solution not found.");
        }

        var entity = new Requirement(
            command.TargetSolutionId,
            null,
            null,
            null,
            command.Title,
            command.Description,
            string.IsNullOrWhiteSpace(command.RequirementType) ? "functional" : command.RequirementType.Trim(),
            string.IsNullOrWhiteSpace(command.Source) ? "user" : command.Source.Trim(),
            string.IsNullOrWhiteSpace(command.Status) ? "submitted" : command.Status.Trim(),
            NormalizePriority(command.Priority),
            SerializeList(command.AcceptanceCriteria),
            SerializeList(command.Constraints),
            DateTime.UtcNow,
            DateTime.UtcNow);

        _db.Requirements.Add(entity);
        await _db.SaveChangesAsync(ct);
        return entity.Id;
    }

    private static string NormalizePriority(string? priority)
    {
        if (string.IsNullOrWhiteSpace(priority))
        {
            return "medium";
        }

        return priority.Trim().ToLowerInvariant() switch
        {
            "low" => "low",
            "high" => "high",
            "critical" => "critical",
            _ => "medium"
        };
    }

    private static string SerializeList(IReadOnlyList<string>? values)
    {
        var normalized = values?
            .Select(x => x?.Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Cast<string>()
            .ToList() ?? [];

        return JsonSerializer.Serialize(normalized);
    }
}
