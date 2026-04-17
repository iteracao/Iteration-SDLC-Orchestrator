using System.Text.Json;
using System.Text.RegularExpressions;
using Iteration.Orchestrator.Application.Abstractions;
using Iteration.Orchestrator.Domain.Solutions;
using Iteration.Orchestrator.Domain.Workflows;
using Microsoft.EntityFrameworkCore;

namespace Iteration.Orchestrator.Application.Solutions;

public sealed record SetupSolutionCommand(
    Guid? SolutionId,
    string Name,
    string Description,
    string RepositoryPath,
    string MainSolutionFile,
    string ProfileCode,
    string TargetCode,
    Guid? OverlayTargetId,
    string? RemoteRepositoryUrl,
    string RequestedBy);

public sealed record SetupSolutionExecutionResult(
    Guid WorkflowRunId,
    Guid SolutionId,
    string KnowledgeRoot,
    string TargetStorageCode,
    string TargetCode,
    string ProfileCode,
    string OverlaySolutionName,
    string OverlayTargetCode,
    bool RepositoryCreated,
    bool GitInitialized,
    bool RemoteConfigured,
    bool SolutionFileCreated,
    IReadOnlyList<string> CreatedDocuments,
    IReadOnlyList<string> ExistingDocuments,
    IReadOnlyList<string> CopiedEntries);

public sealed class SetupSolutionHandler
{
    private static readonly Regex MainSolutionFileRegex = new(@"^[A-Za-z0-9.]+\.sln$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private readonly IAppDbContext _db;
    private readonly IConfigCatalog _config;
    private readonly ISolutionSetupService _solutionSetupService;
    private readonly IGitHubRepositoryMetadataService _gitHubRepositoryMetadataService;
    private readonly IArtifactStore _artifacts;

    public SetupSolutionHandler(
        IAppDbContext db,
        IConfigCatalog config,
        ISolutionSetupService solutionSetupService,
        IGitHubRepositoryMetadataService gitHubRepositoryMetadataService,
        IArtifactStore artifacts)
    {
        _db = db;
        _config = config;
        _solutionSetupService = solutionSetupService;
        _gitHubRepositoryMetadataService = gitHubRepositoryMetadataService;
        _artifacts = artifacts;
    }

    public async Task<SetupSolutionExecutionResult> HandleAsync(SetupSolutionCommand command, CancellationToken ct)
    {
        var workflow = await _config.GetWorkflowAsync("setup-solution", ct);
        var agent = await _config.GetAgentAsync(workflow.PrimaryAgent, ct);

        var profileCode = string.IsNullOrWhiteSpace(command.ProfileCode)
            ? "dotnet-web-enterprise"
            : command.ProfileCode.Trim();

        _ = await _config.GetProfileAsync(profileCode, ct);

        var repositoryPath = ValidateRepositoryPath(command.RepositoryPath);
        var mainSolutionFile = ValidateMainSolutionFile(command.MainSolutionFile);
        var targetCode = NormalizeTargetCode(command.TargetCode);
        var remoteRepositoryUrl = ValidateRemoteRepositoryUrl(command.RemoteRepositoryUrl);
        var repositoryMetadata = await _gitHubRepositoryMetadataService.GetMetadataAsync(remoteRepositoryUrl, ct);

        var solutionRecord = command.SolutionId.HasValue
            ? await _db.Solutions.FirstOrDefaultAsync(x => x.Id == command.SolutionId.Value, ct)
            : null;

        if (command.SolutionId.HasValue && solutionRecord is null)
        {
            throw new InvalidOperationException("Solution not found.");
        }

        var nameInUse = await _db.Solutions.AnyAsync(
        x => x.Name == command.Name
             && (!command.SolutionId.HasValue || x.Id != command.SolutionId.Value),
        ct);

        if (nameInUse)
        {
            throw new InvalidOperationException("Solution name must be unique. Choose a different name.");
        }

        if (solutionRecord is null)
        {
            solutionRecord = new Solution(command.Name, command.Description, profileCode);
            _db.Solutions.Add(solutionRecord);
            await _db.SaveChangesAsync(ct);
        }
        else
        {
            solutionRecord.Update(command.Name, command.Description);
        }

        var targetStorageCode = BuildTargetStorageCode(command.Name, mainSolutionFile, targetCode);

        var existingByCode = await _db.SolutionTargets
            .FirstOrDefaultAsync(x => x.Code == targetStorageCode, ct);

        if (existingByCode is not null && existingByCode.SolutionId != solutionRecord.Id)
        {
            throw new InvalidOperationException($"Target code '{targetCode}' is already in use for another solution workspace.");
        }

        var overlayTarget = command.OverlayTargetId.HasValue
            ? await _db.SolutionTargets
                .Join(
                    _db.Solutions,
                    target => target.SolutionId,
                    solution => solution.Id,
                    (target, solution) => new OverlayTargetLookup(
                        target.Id,
                        target.Code,
                        target.RepositoryPath,
                        solution.Name))
                .FirstOrDefaultAsync(x => x.Id == command.OverlayTargetId.Value, ct)
            : null;

        if (command.OverlayTargetId.HasValue && overlayTarget is null)
        {
            throw new InvalidOperationException("Overlay source target not found.");
        }

        var overlaySolutionName = overlayTarget?.SolutionName ?? string.Empty;
        var overlayTargetCode = overlayTarget is null ? string.Empty : ExtractDisplayTargetCode(overlayTarget.StorageCode);

        var existing = await _db.SolutionTargets
            .FirstOrDefaultAsync(x => x.SolutionId == solutionRecord.Id, ct);

        var solutionTarget = existing ?? new SolutionTarget(
            solutionRecord.Id,
            targetStorageCode,
            overlaySolutionName,
            repositoryPath,
            mainSolutionFile,
            profileCode,
            overlayTargetCode);

        solutionTarget.Update(
            targetStorageCode,
            overlaySolutionName,
            repositoryPath,
            mainSolutionFile,
            profileCode,
            overlayTargetCode);

        if (existing is null)
        {
            _db.SolutionTargets.Add(solutionTarget);
        }

        await _db.SaveChangesAsync(ct);

        var run = new WorkflowRun(null, null, solutionRecord.Id, workflow.Code, command.RequestedBy);
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
                solutionRecord.Id,
                command.Name,
                command.Description,
                ProfileCode = profileCode,
                TargetCode = targetCode,
                TargetStorageCode = targetStorageCode,
                RepositoryRoot = repositoryPath,
                MainSolutionFile = mainSolutionFile,
                OverlayTargetId = overlayTarget?.Id,
                OverlaySolutionName = overlaySolutionName,
                OverlayTargetCode = overlayTargetCode,
                OverlaySourceRepositoryRoot = overlayTarget?.RepositoryPath,
                RemoteRepositoryUrl = remoteRepositoryUrl,
                GitHubRepositoryName = repositoryMetadata.Name,
                DefaultBranch = repositoryMetadata.DefaultBranch
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
                    targetStorageCode,
                    command.Name,
                    repositoryPath,
                    mainSolutionFile,
                    profileCode,
                    overlaySolutionName,
                    overlayTargetCode,
                    overlayTarget?.RepositoryPath,
                    remoteRepositoryUrl),
                ct);

            var outputPayload = JsonSerializer.Serialize(new
            {
                workflow = workflow.Code,
                workflowName = workflow.Name,
                agent = agent.Code,
                solutionId = solutionRecord.Id,
                targetStorageCode,
                targetCode,
                profileCode,
                overlaySolutionName,
                overlayTargetCode,
                setupResult.KnowledgeRoot,
                setupResult.RepositoryCreated,
                setupResult.GitInitialized,
                setupResult.RemoteConfigured,
                setupResult.SolutionFileCreated,
                setupResult.CopiedEntries,
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
                targetStorageCode,
                targetCode,
                profileCode,
                overlaySolutionName,
                overlayTargetCode,
                setupResult.RepositoryCreated,
                setupResult.GitInitialized,
                setupResult.RemoteConfigured,
                setupResult.SolutionFileCreated,
                setupResult.CreatedDocuments,
                setupResult.ExistingDocuments,
                setupResult.CopiedEntries);
        }
        catch (Exception ex)
        {
            taskRun.Fail(ex.Message);
            run.Fail("solution-setup", ex.Message);
            await _db.SaveChangesAsync(ct);
            throw;
        }
    }


    private static string ValidateRepositoryPath(string value)
    {
        var candidate = value.Trim();
        if (string.IsNullOrWhiteSpace(candidate))
        {
            throw new InvalidOperationException("Repository folder is required.");
        }

        if (!Directory.Exists(candidate))
        {
            throw new InvalidOperationException("Repository folder was not found on the machine running the solution.");
        }

        try
        {
            _ = Directory.GetFiles(candidate);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Repository folder is not accessible: {ex.Message}");
        }

        return candidate;
    }

    private static string ValidateRemoteRepositoryUrl(string? value)
    {
        var candidate = value?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(candidate))
        {
            throw new InvalidOperationException("GitHub repository URL is required.");
        }

        return candidate;
    }

    private static string ValidateMainSolutionFile(string value)
    {
        var candidate = value.Trim();
        if (!MainSolutionFileRegex.IsMatch(candidate))
        {
            throw new InvalidOperationException("Main solution file must use only A-Z, a-z, 0-9 and '.' and end with .sln.");
        }

        return candidate;
    }

    private static string NormalizeTargetCode(string value)
    {
        var candidate = string.IsNullOrWhiteSpace(value) ? "dev" : value.Trim().ToLowerInvariant();
        return candidate;
    }

    private static string BuildTargetStorageCode(string solutionName, string mainSolutionFile, string targetCode)
    {
        var technicalName = Path.GetFileNameWithoutExtension(mainSolutionFile);
        var source = string.IsNullOrWhiteSpace(technicalName) ? solutionName : technicalName;
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

        normalized = normalized.Trim('-');
        return $"{normalized}/{targetCode}";
    }

    private static string ExtractDisplayTargetCode(string storageCode)
    {
        if (string.IsNullOrWhiteSpace(storageCode))
        {
            return string.Empty;
        }

        var slashIndex = storageCode.LastIndexOf('/');
        return slashIndex >= 0 ? storageCode[(slashIndex + 1)..] : storageCode;
    }

    private sealed record OverlayTargetLookup(Guid Id, string StorageCode, string RepositoryPath, string SolutionName);
}
