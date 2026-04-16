using System.Text.Json;
using Iteration.Orchestrator.Application.Abstractions;
using Iteration.Orchestrator.Domain.Solutions;
using Iteration.Orchestrator.Domain.Workflows;
using Microsoft.EntityFrameworkCore;

namespace Iteration.Orchestrator.Application.Solutions;

public sealed record SetupSolutionCommand(
    Guid? SolutionId,
    string Code,
    string Name,
    string Description,
    string RepositoryPath,
    string MainSolutionFile,
    string ProfileCode,
    string SolutionOverlayCode,
    string? RemoteRepositoryUrl,
    string RequestedBy);

public sealed record SetupSolutionExecutionResult(
    Guid WorkflowRunId,
    Guid SolutionId,
    string KnowledgeRoot,
    IReadOnlyList<string> CreatedDocuments,
    IReadOnlyList<string> ExistingDocuments,
    bool RepositoryCreated,
    bool GitInitialized,
    bool RemoteConfigured);

public sealed class SetupSolutionHandler
{
    private readonly IAppDbContext _db;
    private readonly IConfigCatalog _config;
    private readonly ISolutionSetupService _solutionSetupService;
    private readonly IArtifactStore _artifacts;

    public SetupSolutionHandler(
        IAppDbContext db,
        IConfigCatalog config,
        ISolutionSetupService solutionSetupService,
        IArtifactStore artifacts)
    {
        _db = db;
        _config = config;
        _solutionSetupService = solutionSetupService;
        _artifacts = artifacts;
    }

    public async Task<SetupSolutionExecutionResult> HandleAsync(SetupSolutionCommand command, CancellationToken ct)
    {
        var workflow = await _config.GetWorkflowAsync("setup-solution", ct);
        var agent = await _config.GetAgentAsync(workflow.PrimaryAgent, ct);
        _ = await _config.GetProfileAsync(command.ProfileCode, ct);
        var solutionCode = BuildSolutionCode(command.Code, command.Name);

        var existingByCode = await _db.SolutionTargets
            .FirstOrDefaultAsync(x => x.Code == solutionCode, ct);

        var solutionRecord = command.SolutionId.HasValue
            ? await _db.Solutions.FirstOrDefaultAsync(x => x.Id == command.SolutionId.Value, ct)
            : null;

        if (command.SolutionId.HasValue && solutionRecord is null)
        {
            throw new InvalidOperationException("Solution not found.");
        }

        if (existingByCode is not null &&
            (!command.SolutionId.HasValue || existingByCode.SolutionId != command.SolutionId.Value))
        {
            throw new InvalidOperationException($"Solution code '{solutionCode}' is already in use.");
        }

        if (solutionRecord is null)
        {
            solutionRecord = new Solution(
                command.Name,
                command.Description,
                command.ProfileCode);

            _db.Solutions.Add(solutionRecord);
            await _db.SaveChangesAsync(ct);
        }

        var existing = await _db.SolutionTargets
            .FirstOrDefaultAsync(x => x.SolutionId == solutionRecord.Id, ct);

        var solution = existing ?? new SolutionTarget(
            solutionRecord.Id,
            solutionCode,
            command.Name,
            command.RepositoryPath,
            command.MainSolutionFile,
            command.ProfileCode,
            command.SolutionOverlayCode);

        if (existing is null)
        {
            _db.SolutionTargets.Add(solution);
            await _db.SaveChangesAsync(ct);
        }

        var run = new WorkflowRun(Guid.Empty, solutionRecord.Id, workflow.Code, command.RequestedBy);
        run.Start("solution-setup");
        _db.WorkflowRuns.Add(run);
        await _db.SaveChangesAsync(ct);

        var inputPayload = JsonSerializer.Serialize(new
        {
            workflow = workflow.Code,
            workflowName = workflow.Name,
            workflowPurpose = workflow.Purpose,
            requestedBy = command.RequestedBy,
            solution = new
            {
                Code = solutionCode,
                command.Name,
                command.Description,
                RepositoryRoot = command.RepositoryPath,
                command.MainSolutionFile,
                command.ProfileCode,
                command.SolutionOverlayCode,
                command.RemoteRepositoryUrl
            }
        });

        var taskRun = new AgentTaskRun(run.Id, agent.Code, inputPayload);
        taskRun.Start();
        _db.AgentTaskRuns.Add(taskRun);
        await _db.SaveChangesAsync(ct);

        try
        {
            var setupResult = await _solutionSetupService.SetupAsync(
                new SolutionSetupRequest(
                    solutionCode,
                    command.Name,
                    command.RepositoryPath,
                    command.MainSolutionFile,
                    command.ProfileCode,
                    command.SolutionOverlayCode,
                    command.RemoteRepositoryUrl),
                ct);

            var outputPayload = JsonSerializer.Serialize(new
            {
                workflow = workflow.Code,
                workflowName = workflow.Name,
                agent = agent.Code,
                solutionId = solutionRecord.Id,
                setupResult.KnowledgeRoot,
                setupResult.RepositoryCreated,
                setupResult.GitInitialized,
                setupResult.RemoteConfigured,
                setupResult.CreatedDocuments,
                setupResult.ExistingDocuments
            });

            taskRun.Succeed(outputPayload);
            run.Succeed("setup-completed");
            await _db.SaveChangesAsync(ct);

            await _artifacts.SaveTextAsync(run.Id, "setup-solution.input.json", inputPayload, ct);
            await _artifacts.SaveTextAsync(run.Id, "solution-setup-result.json", outputPayload, ct);

            return new SetupSolutionExecutionResult(
                run.Id,
                solutionRecord.Id,
                setupResult.KnowledgeRoot,
                setupResult.CreatedDocuments,
                setupResult.ExistingDocuments,
                setupResult.RepositoryCreated,
                setupResult.GitInitialized,
                setupResult.RemoteConfigured);
        }
        catch (Exception ex)
        {
            taskRun.Fail(ex.Message);
            run.Fail("solution-setup", ex.Message);
            await _db.SaveChangesAsync(ct);
            throw;
        }
    }

    private static string BuildSolutionCode(string code, string name)
    {
        var source = string.IsNullOrWhiteSpace(code) ? name : code;
        var chars = source
            .Trim()
            .ToLowerInvariant()
            .Select(ch => char.IsLetterOrDigit(ch) ? ch : '-')
            .ToArray();

        var normalized = new string(chars);
        while (normalized.Contains("--", StringComparison.Ordinal))
        {
            normalized = normalized.Replace("--", "-", StringComparison.Ordinal);
        }

        return normalized.Trim('-');
    }
}
