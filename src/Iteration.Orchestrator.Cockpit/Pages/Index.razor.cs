using System.Net.Http.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using MudBlazor;
using Iteration.Orchestrator.Cockpit.Models;
using Iteration.Orchestrator.Cockpit.Services;

namespace Iteration.Orchestrator.Cockpit.Pages;

public partial class Index : ComponentBase, IDisposable
{
    [Inject] private IHttpClientFactory HttpClientFactory { get; set; } = default!;
    [Inject] private IJSRuntime JS { get; set; } = default!;
    [Inject] private SelectedSolutionState SelectedSolutionState { get; set; } = default!;


    private static readonly IReadOnlyList<PipelineDefinition> _pipelineDefinitions =
    [
        new(LaneKeys.Requirement, "Requirement", Icons.Material.Filled.Flag),
        new(LaneKeys.Analysis, "Analysis", Icons.Material.Filled.Search),
        new(LaneKeys.Design, "Design", Icons.Material.Filled.Draw),
        new(LaneKeys.Planning, "Planning", Icons.Material.Filled.AccountTree),
        new(LaneKeys.Implementation, "Implementation", Icons.Material.Filled.Code),
        new(LaneKeys.Test, "Test", Icons.Material.Filled.FactCheck),
        new(LaneKeys.Review, "Review", Icons.Material.Filled.RateReview),
        new(LaneKeys.Deliver, "Deliver", Icons.Material.Filled.LocalShipping)
    ];

    private const string DefaultProfileCode = "dotnet-web-enterprise";
    private const string DefaultTargetCode = "dev";

    private readonly List<SolutionSummary> _solutions = [];
    private readonly List<RequirementRow> _requirements = [];
    private readonly List<BacklogRow> _backlog = [];
    private readonly List<WorkflowRunRow> _runs = [];
    private readonly List<SolutionDocumentSummary> _documents = [];

    private Guid? _selectedTargetId;
    private List<SolutionTargetOption> _solutionTargetOptions => _solutions
        .SelectMany(solution => solution.Targets.Select(target => new SolutionTargetOption
        {
            TargetId = target.Id,
            Label = $"{solution.Name} / {target.DisplayName}"
        }))
        .OrderBy(x => x.Label, StringComparer.OrdinalIgnoreCase)
        .ToList();
    private bool _loading;
    private bool _drawerOpen;
    private bool _viewerOpen;
    private bool _documentsLoading;
    private bool _createSolutionPanelOpen;
    private bool _createRequirementPanelOpen;
    private bool _documentationWorkflowModalOpen;
    private bool _savingSolution;
    private bool _savingRequirement;
    private bool _startingDocumentationWorkflow;
    private bool _documentationWorkflowLogLoading;
    private Guid? _runningRequirementId;
    private Guid? _runningBacklogItemId;
    private Guid? _mutatingWorkflowRunId;
    private Guid? _mutatingRequirementId;
    private bool _isRefreshingWorkflowState;
    private long _cockpitRefreshVersion;
    private string? _lastCockpitSnapshot;
    private CancellationTokenSource? _workflowPollingCts;
    private Task? _workflowPollingTask;
    private string? _loadError;
    private string? _createSolutionMessage;
    private Severity _createSolutionMessageSeverity = Severity.Info;
    private string? _createRequirementMessage;
    private Severity _createRequirementMessageSeverity = Severity.Info;
    private string? _documentationWorkflowMessage;
    private Severity _documentationWorkflowMessageSeverity = Severity.Info;
    private RequirementStageCard? _activeCard;
    private SolutionDocumentContent? _activeDocument;
    private WorkflowLogContent? _activeWorkflowLog;
    private WorkflowLogContent? _documentationWorkflowLog;
    private Guid? _currentDocumentationWorkflowRunId;
    private WorkflowArtifactContent? _activeWorkflowArtifact;
    private WorkflowRunDetail? _activeWorkflowRunDetails;
    private bool _activeWorkflowRunDetailsLoading;
    private readonly SetupSolutionModel _solutionRequest = new();
    private readonly CreateRequirementModel _requirementRequest = new();

    private IEnumerable<OverlayOption> OverlayOptions
        => _solutions.SelectMany(solution => solution.Targets.Select(target => new OverlayOption(solution, target)))
            .OrderBy(x => x.Label, StringComparer.OrdinalIgnoreCase);

    private bool CanOpenSolutionPanel
        => !_createRequirementPanelOpen && !_createSolutionPanelOpen && !_savingSolution && !_savingRequirement && !HasActiveWorkflowRuns();

    private bool CanOpenRequirementPanel
        => SelectedSolutionState.CurrentTarget is not null
           && !_createSolutionPanelOpen
           && !_createRequirementPanelOpen
           && !_savingSolution
           && !_savingRequirement
           && !HasActiveWorkflowRuns();

    private bool CanOpenDocumentationWorkflowModal
        => SelectedSolutionState.CurrentTarget is not null
           && !_createSolutionPanelOpen
           && !_createRequirementPanelOpen
           && !_savingSolution
           && !_savingRequirement;

    private bool IsTargetSelectionLocked
        => _createSolutionPanelOpen
           || _createRequirementPanelOpen
           || _savingSolution
           || _savingRequirement
           || HasActiveWorkflowRuns();

    private string SelectedTargetLabel
        => SelectedSolutionState.Current is null || SelectedSolutionState.CurrentTarget is null
            ? string.Empty
            : $"{SelectedSolutionState.Current.Name} / {SelectedSolutionState.CurrentTarget.DisplayName}";

    private IReadOnlyList<RequirementPipelineViewModel> RequirementPipelines
        => _requirements.Select(BuildPipeline).ToList();

    private WorkflowRunRow? CurrentDocumentationWorkflowRun
        => GetCurrentDocumentationWorkflowRun();

    private string ViewerTitle => GetViewerTitle();

    private string ViewerSubtitle => GetViewerSubtitle();

    private bool ActiveCardCanRun => _activeCard is not null && CanRun(_activeCard);

    private bool ActiveCardRunBusy => _activeCard is not null && IsRunBusy(_activeCard);

    private bool ActiveCardCanValidate => _activeCard?.WorkflowRun is not null && CanValidate(_activeCard.WorkflowRun);

    private bool ActiveCardCanCancelWorkflow => _activeCard?.WorkflowRun is not null && CanCancel(_activeCard.WorkflowRun);

    private bool ActiveCardCanViewLog => _activeCard?.WorkflowRun is not null && CanViewWorkflowLog(_activeCard.WorkflowRun);

    private bool ActiveCardCanViewReport => _activeCard?.WorkflowRun is not null && CanViewWorkflowReport(_activeCard.WorkflowRun, _activeCard.Requirement);

    private bool DocumentationWorkflowRunDisabled
        => _startingDocumentationWorkflow || IsDocumentationWorkflowInProgress(CurrentDocumentationWorkflowRun);

    private bool ActiveCardCanDeleteRequirement
        => _activeCard is not null
           && string.Equals(_activeCard.LaneKey, LaneKeys.Requirement, StringComparison.OrdinalIgnoreCase)
           && CanDeleteRequirement(_activeCard.Requirement);

    private bool ActiveCardCanCancelRequirement
        => _activeCard is not null
           && string.Equals(_activeCard.LaneKey, LaneKeys.Requirement, StringComparison.OrdinalIgnoreCase)
           && !ShowRequirementDecisionActions(_activeCard)
           && CanCancelRequirement(_activeCard.Requirement);

    private bool ActiveCardShowRequirementDecisionActions
        => _activeCard is not null && ShowRequirementDecisionActions(_activeCard);

    private IReadOnlyList<string> ActiveRecordedDocumentationUpdates => GetRecordedDocumentationUpdates();

    private IReadOnlyList<string> ActiveRecordedKnowledgeUpdates => GetRecordedKnowledgeUpdates();

    protected override async Task OnInitializedAsync()
    {
        _solutionRequest.ProfileCode = DefaultProfileCode;
        _solutionRequest.TargetCode = DefaultTargetCode;
        SelectedSolutionState.Changed += HandleSolutionChanged;
        await LoadSolutionsAsync();
        await EnsureSelectedSolutionAsync();

        if (SelectedSolutionState.Current is null)
        {
            await LoadCockpitAsync();
        }
    }

    private async void HandleSolutionChanged()
    {
        _selectedTargetId = SelectedSolutionState.CurrentTarget?.Id;
        ResetDocumentationWorkflowModalState();
        StopWorkflowPolling();
        await InvokeAsync(async () =>
        {
            await LoadDocumentsAsync();
            await LoadCockpitAsync();
        });
    }

    private async Task LoadSolutionsAsync()
    {
        var client = HttpClientFactory.CreateClient("api");
        _solutions.Clear();
        _solutions.AddRange(await client.GetFromJsonAsync<List<SolutionSummary>>("api/solutions") ?? []);
    }

    private Task EnsureSelectedSolutionAsync()
    {
        if (SelectedSolutionState.Current is not null)
        {
            _selectedTargetId = SelectedSolutionState.CurrentTarget?.Id;
            return Task.CompletedTask;
        }

        var first = _solutions.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
        if (first is not null)
        {
            _selectedTargetId = first.Targets.Count == 1 ? first.Targets[0].Id : first.Targets.FirstOrDefault()?.Id;
            SelectedSolutionState.SetCurrent(first, _selectedTargetId);
        }

        return Task.CompletedTask;
    }

    private Task OnSolutionSelectionChangedAsync(Guid? targetId)
    {
        _selectedTargetId = targetId;
        var selected = _solutions.FirstOrDefault(x => x.Targets.Any(t => t.Id == targetId));
        ResetDocumentationWorkflowModalState();
        SelectedSolutionState.SetCurrent(selected, targetId);
        return Task.CompletedTask;
    }

    private async Task LoadCockpitAsync(bool showLoading = true)
    {
        if (_isRefreshingWorkflowState)
        {
            return;
        }

        var refreshVersion = Interlocked.Increment(ref _cockpitRefreshVersion);
        _isRefreshingWorkflowState = true;
        var shouldRender = false;
        var previousLoadError = _loadError;
        _loadError = null;

        if (SelectedSolutionState.Current is null)
        {
            if (_requirements.Count > 0 || _backlog.Count > 0 || _runs.Count > 0 || _lastCockpitSnapshot is not null)
            {
                _requirements.Clear();
                _backlog.Clear();
                _runs.Clear();
                _lastCockpitSnapshot = null;
                shouldRender = true;
            }

            _loading = false;
            EnsureWorkflowPollingState();

            if (shouldRender || !string.Equals(previousLoadError, _loadError, StringComparison.Ordinal))
            {
                StateHasChanged();
            }

            _isRefreshingWorkflowState = false;
            return;
        }

        if (showLoading && !_loading)
        {
            _loading = true;
            shouldRender = true;
        }

        if (shouldRender)
        {
            StateHasChanged();
        }

        try
        {
            var client = HttpClientFactory.CreateClient("api");
            var targetSolutionId = SelectedSolutionState.CurrentTarget?.Id;
            if (!targetSolutionId.HasValue)
            {
                return;
            }

            var requirements = await client.GetFromJsonAsync<List<RequirementRow>>($"api/solution-targets/{targetSolutionId.Value}/requirements") ?? [];
            var backlog = await client.GetFromJsonAsync<List<BacklogRow>>($"api/backlog-items?targetSolutionId={targetSolutionId.Value}") ?? [];
            var runs = await client.GetFromJsonAsync<List<WorkflowRunRow>>($"api/workflow-runs?targetSolutionId={targetSolutionId.Value}") ?? [];

            if (refreshVersion != _cockpitRefreshVersion || SelectedSolutionState.CurrentTarget?.Id != targetSolutionId.Value)
            {
                return;
            }

            var snapshot = BuildCockpitSnapshot(targetSolutionId.Value, requirements, backlog, runs);
            if (!string.Equals(snapshot, _lastCockpitSnapshot, StringComparison.Ordinal))
            {
                _requirements.Clear();
                _requirements.AddRange(requirements);
                _backlog.Clear();
                _backlog.AddRange(backlog);
                _runs.Clear();
                _runs.AddRange(runs);
                _lastCockpitSnapshot = snapshot;
                SyncDocumentationWorkflowRunSelection();
                await RefreshActiveCardAsync(client, targetSolutionId.Value, refreshVersion);
                shouldRender = true;
            }

            if (_documentationWorkflowModalOpen && _currentDocumentationWorkflowRunId.HasValue && IsDocumentationWorkflowInProgress(CurrentDocumentationWorkflowRun))
            {
                shouldRender = await RefreshDocumentationWorkflowLogAsync(_currentDocumentationWorkflowRunId, showLoadingIndicator: false) || shouldRender;
            }

            if (!string.Equals(previousLoadError, _loadError, StringComparison.Ordinal))
            {
                shouldRender = true;
            }
        }
        catch (Exception ex)
        {
            _loadError = ex.Message;
            shouldRender = !string.Equals(previousLoadError, _loadError, StringComparison.Ordinal);
        }
        finally
        {
            if (_loading)
            {
                _loading = false;
                shouldRender = true;
            }

            _isRefreshingWorkflowState = false;
            EnsureWorkflowPollingState();

            if (shouldRender)
            {
                StateHasChanged();
            }
        }
    }

    private async Task LoadDocumentsAsync()
    {
        _documents.Clear();

        if (SelectedSolutionState.Current is null)
        {
            return;
        }

        _documentsLoading = true;
        StateHasChanged();

        try
        {
            var client = HttpClientFactory.CreateClient("api");
            _documents.AddRange(await client.GetFromJsonAsync<List<SolutionDocumentSummary>>($"api/solutions/{SelectedSolutionState.Current.SolutionId}/documents") ?? []);
        }
        finally
        {
            _documentsLoading = false;
            StateHasChanged();
        }
    }

    private RequirementPipelineViewModel BuildPipeline(RequirementRow requirement)
    {
        var requirementCard = new RequirementStageCard(
            LaneKeys.Requirement,
            "Requirement",
            requirement.Title,
            requirement.Description,
            requirement.CreatedAtUtc.ToLocalTime().ToString("dd MMM"),
            GetRequirementBadgeLabel(requirement),
            GetRequirementBadgeVisualState(requirement),
            requirement,
            null,
            null);

        var cards = new Dictionary<string, RequirementStageCard>(StringComparer.OrdinalIgnoreCase)
        {
            [LaneKeys.Requirement] = requirementCard
        };

        var requirementRunGroups = _runs
            .Where(x => x.RequirementId == requirement.Id)
            .GroupBy(x => x.WorkflowCode, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(x => x.StartedUtc).First(), StringComparer.OrdinalIgnoreCase);

        AddRequirementWorkflowCard(cards, LaneKeys.Analysis, requirementRunGroups, requirement, "analyze-request", "Analysis");
        AddRequirementWorkflowCard(cards, LaneKeys.Design, requirementRunGroups, requirement, "design-solution-change", "Design");
        AddRequirementWorkflowCard(cards, LaneKeys.Planning, requirementRunGroups, requirement, "plan-implementation", "Planning");

        var backlogItem = _backlog
            .Where(x => x.RequirementId == requirement.Id)
            .OrderBy(x => x.PlanningOrder)
            .ThenBy(x => x.CreatedUtc)
            .FirstOrDefault(x => !string.Equals(x.Status, "Validated", StringComparison.OrdinalIgnoreCase) && !string.Equals(x.Status, "Canceled", StringComparison.OrdinalIgnoreCase))
            ?? _backlog.Where(x => x.RequirementId == requirement.Id).OrderByDescending(x => x.PlanningOrder).FirstOrDefault();

        if (backlogItem is not null)
        {
            var implementationRun = GetLatestRun(requirement.Id, backlogItem.Id);

            cards[LaneKeys.Implementation] = new RequirementStageCard(
                LaneKeys.Implementation,
                "Implementation",
                backlogItem.Title,
                backlogItem.Description,
                backlogItem.CreatedUtc.ToLocalTime().ToString("dd MMM"),
                HumanizeBacklogStatus(backlogItem.Status),
                GetBacklogVisualState(backlogItem.Status),
                requirement,
                backlogItem,
                implementationRun);

            if (string.Equals(backlogItem.Status, "AwaitingValidation", StringComparison.OrdinalIgnoreCase))
            {
                cards[LaneKeys.Test] = new RequirementStageCard(
                    LaneKeys.Test,
                    "Test",
                    backlogItem.Title,
                    "Implementation is waiting for validation before the next slice can continue.",
                    (backlogItem.UpdatedUtc ?? backlogItem.CreatedUtc).ToLocalTime().ToString("dd MMM"),
                    "Awaiting validation",
                    GetBacklogVisualState(backlogItem.Status),
                    requirement,
                    backlogItem,
                    implementationRun);
            }
        }

        var steps = _pipelineDefinitions
            .Select(definition => BuildPipelineStep(definition, cards, requirement, backlogItem))
            .ToList();

        return new RequirementPipelineViewModel(requirement, requirementCard.State, steps);
    }

    private void AddRequirementWorkflowCard(
        IDictionary<string, RequirementStageCard> row,
        string laneKey,
        IReadOnlyDictionary<string, WorkflowRunRow> runs,
        RequirementRow requirement,
        string workflowCode,
        string laneTitle)
    {
        if (!runs.TryGetValue(workflowCode, out var run))
        {
            return;
        }

        row[laneKey] = new RequirementStageCard(
            laneKey,
            laneTitle,
            requirement.Title,
            $"{laneTitle} workflow run recorded for this requirement.",
            run.StartedUtc.ToLocalTime().ToString("dd MMM"),
            HumanizeWorkflowStatus(run.Status),
            GetWorkflowVisualState(run.Status),
            requirement,
            null,
            run);
    }

    private RequirementPipelineStep BuildPipelineStep(
        PipelineDefinition definition,
        IReadOnlyDictionary<string, RequirementStageCard> cards,
        RequirementRow requirement,
        BacklogRow? backlogItem)
    {
        if (cards.TryGetValue(definition.Key, out var existing))
        {
            return new RequirementPipelineStep(definition.Key, definition.Title, definition.Icon, existing, false);
        }

        var isEnabled = IsPipelineStageEnabled(definition.Key, requirement, backlogItem);
        var placeholder = CreatePlaceholderCard(definition, requirement, backlogItem, isEnabled);
        return new RequirementPipelineStep(definition.Key, definition.Title, definition.Icon, placeholder, !isEnabled);
    }

    private RequirementStageCard CreatePlaceholderCard(PipelineDefinition definition, RequirementRow requirement, BacklogRow? backlogItem, bool isEnabled)
        => new(
            definition.Key,
            definition.Title,
            requirement.Title,
            BuildPlaceholderDescription(definition.Key),
            requirement.CreatedAtUtc.ToLocalTime().ToString("dd MMM"),
            isEnabled ? "Available" : "Unavailable",
            isEnabled ? VisualStatePalette.Waiting : VisualStatePalette.Default,
            requirement,
            definition.Key == LaneKeys.Implementation || definition.Key == LaneKeys.Test ? backlogItem : null,
            null);

    private static string BuildPlaceholderDescription(string laneKey)
        => laneKey switch
        {
            LaneKeys.Requirement => "Requirement intake is the starting point for the workflow pipeline.",
            LaneKeys.Analysis => "Analysis has not started for this requirement.",
            LaneKeys.Design => "Design is not available yet for this requirement.",
            LaneKeys.Planning => "Planning is not available yet for this requirement.",
            LaneKeys.Implementation => "Implementation has not started for the active backlog item.",
            LaneKeys.Test => "Validation is not waiting on this requirement right now.",
            LaneKeys.Review => "Review is not available in the current workflow slice.",
            LaneKeys.Deliver => "Delivery is not available in the current workflow slice.",
            _ => "This workflow step is not available."
        };

    private bool IsPipelineStageEnabled(string laneKey, RequirementRow requirement, BacklogRow? backlogItem)
        => laneKey switch
        {
            LaneKeys.Requirement => true,
            LaneKeys.Analysis => CanAnalyze(requirement),
            LaneKeys.Design => CanDesign(requirement),
            LaneKeys.Planning => CanPlan(requirement),
            LaneKeys.Implementation => backlogItem is not null && CanImplement(backlogItem),
            LaneKeys.Test => false,
            _ => false
        };

    private static string BuildPipelineTooltip(RequirementPipelineStep step)
        => $"{step.LaneTitle} - {step.Card.StatusLabel}";

    private WorkflowRunRow? GetLatestRun(Guid requirementId, Guid? backlogItemId)
        => _runs
            .Where(x => x.RequirementId == requirementId && x.BacklogItemId == backlogItemId)
            .OrderByDescending(x => x.StartedUtc)
            .FirstOrDefault();

    private bool HasDocumentation(RequirementRow requirement)
        => _documents.Count > 0 && !string.IsNullOrWhiteSpace(requirement.Status);

    private IReadOnlyList<string> GetRecordedDocumentationUpdates()
        => ParseRecordedUpdates(_activeWorkflowRunDetails?.Report?.DocumentationUpdatesJson);

    private IReadOnlyList<string> GetRecordedKnowledgeUpdates()
        => ParseRecordedUpdates(_activeWorkflowRunDetails?.Report?.KnowledgeUpdatesJson);

    private static IReadOnlyList<string> ParseRecordedUpdates(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            var items = System.Text.Json.JsonSerializer.Deserialize<List<string>>(json);
            return items?
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList() ?? [];
        }
        catch
        {
            return [];
        }
    }

    private async Task RefreshActiveCardAsync(HttpClient client, Guid targetSolutionId, long refreshVersion)
    {
        if (_activeCard is null)
        {
            return;
        }

        var pipeline = _requirements
            .Where(x => x.TargetSolutionId == targetSolutionId)
            .Select(BuildPipeline)
            .FirstOrDefault(x => x.Requirement.Id == _activeCard.Requirement.Id);

        var refreshedCard = pipeline?.Steps
            .Select(x => x.Card)
            .FirstOrDefault(x => string.Equals(x.LaneKey, _activeCard.LaneKey, StringComparison.OrdinalIgnoreCase)
                && x.BacklogItem?.Id == _activeCard.BacklogItem?.Id);

        if (refreshedCard is null)
        {
            _activeCard = null;
            _activeWorkflowRunDetails = null;
            _activeWorkflowRunDetailsLoading = false;
            _drawerOpen = false;
            return;
        }

        _activeCard = refreshedCard;

        if (refreshedCard.WorkflowRun is null)
        {
            _activeWorkflowRunDetails = null;
            _activeWorkflowRunDetailsLoading = false;
            return;
        }

        await LoadWorkflowRunDetailsAsync(client, refreshedCard.WorkflowRun.Id, targetSolutionId, refreshVersion);
    }

    private async Task LoadWorkflowRunDetailsAsync(HttpClient client, Guid workflowRunId, Guid targetSolutionId, long refreshVersion)
    {
        _activeWorkflowRunDetailsLoading = true;
        StateHasChanged();

        try
        {
            var detail = await client.GetFromJsonAsync<WorkflowRunDetailResponse>($"api/workflow-runs/{workflowRunId}");
            if (refreshVersion != _cockpitRefreshVersion)
            {
                return;
            }

            _activeWorkflowRunDetails = new WorkflowRunDetail
            {
                HasOutputPayload = detail?.HasOutputPayload == true,
                ArtifactFiles = detail?.ArtifactFiles ?? [],
                Report = detail?.Report is null
                    ? null
                    : new WorkflowRunReport
                    {
                        Summary = detail.Report.Summary ?? string.Empty,
                        Status = detail.Report.Status ?? string.Empty,
                        DocumentationUpdatesJson = detail.Report.DocumentationUpdatesJson ?? "[]",
                        KnowledgeUpdatesJson = detail.Report.KnowledgeUpdatesJson ?? "[]"
                    }
            };
        }
        catch
        {
            if (refreshVersion == _cockpitRefreshVersion)
            {
                _activeWorkflowRunDetails = null;
            }
        }
        finally
        {
            if (refreshVersion == _cockpitRefreshVersion)
            {
                _activeWorkflowRunDetailsLoading = false;
                StateHasChanged();
            }
        }
    }

    private async Task OpenCardAsync(RequirementStageCard card)
    {
        _activeDocument = null;
        _activeWorkflowLog = null;
        _activeWorkflowArtifact = null;
        _activeWorkflowRunDetails = null;
        _activeCard = card;
        _drawerOpen = true;

        if (card.WorkflowRun is null || SelectedSolutionState.CurrentTarget is null)
        {
            _activeWorkflowRunDetailsLoading = false;
            return;
        }

        var client = HttpClientFactory.CreateClient("api");
        await LoadWorkflowRunDetailsAsync(client, card.WorkflowRun.Id, SelectedSolutionState.CurrentTarget.Id, _cockpitRefreshVersion);
    }

    private async Task OpenDocumentAsync(SolutionDocumentSummary document)
    {
        if (SelectedSolutionState.Current is null)
        {
            return;
        }

        var client = HttpClientFactory.CreateClient("api");
        var path = Uri.EscapeDataString(document.RelativePath);
        _activeDocument = await client.GetFromJsonAsync<SolutionDocumentContent>($"api/solutions/{SelectedSolutionState.Current.SolutionId}/documents/content?path={path}");
        _activeCard = null;
        _activeWorkflowLog = null;
        _activeWorkflowArtifact = null;
        _activeWorkflowRunDetails = null;
        _activeWorkflowRunDetailsLoading = false;
        _drawerOpen = true;
    }

    private async Task OpenWorkflowPromptAsync(Guid workflowRunId)
    {
        await OpenWorkflowArtifactFromEndpointAsync(workflowRunId, $"api/workflow-runs/{workflowRunId}/prompt");
    }

    private async Task OpenWorkflowInputAsync(Guid workflowRunId)
    {
        await OpenWorkflowArtifactFromEndpointAsync(workflowRunId, $"api/workflow-runs/{workflowRunId}/input");
    }

    private async Task OpenWorkflowOutputAsync(Guid workflowRunId)
    {
        await OpenWorkflowArtifactFromEndpointAsync(workflowRunId, $"api/workflow-runs/{workflowRunId}/output");
    }

    private async Task OpenWorkflowLogAsync(Guid workflowRunId)
    {
        var client = HttpClientFactory.CreateClient("api");
        _activeWorkflowLog = await GetWorkflowLogContentAsync(client, workflowRunId, $"api/workflow-runs/{workflowRunId}/log");
        _activeWorkflowArtifact = null;
        _viewerOpen = true;
    }

    private async Task OpenWorkflowArtifactAsync(Guid workflowRunId, string fileName)
    {
        var client = HttpClientFactory.CreateClient("api");
        _activeWorkflowArtifact = await GetWorkflowArtifactContentAsync(client, workflowRunId, $"api/workflow-runs/{workflowRunId}/artifacts/{Uri.EscapeDataString(fileName)}", fileName, LooksLikeJsonFile(fileName));
        _activeWorkflowLog = null;
        _viewerOpen = true;
    }

    private async Task OpenWorkflowArtifactFromEndpointAsync(Guid workflowRunId, string endpoint)
    {
        var client = HttpClientFactory.CreateClient("api");
        var fileName = endpoint.Split('/').LastOrDefault() ?? "artifact";
        var preferJson = fileName.Contains("input", StringComparison.OrdinalIgnoreCase)
            || fileName.Contains("output", StringComparison.OrdinalIgnoreCase)
            || LooksLikeJsonFile(fileName);
        _activeWorkflowArtifact = await GetWorkflowArtifactContentAsync(client, workflowRunId, endpoint, fileName, preferJson);
        _activeWorkflowLog = null;
        _viewerOpen = true;
    }

    private async Task OpenDocumentationWorkflowModalAsync()
    {
        if (!CanOpenDocumentationWorkflowModal)
        {
            return;
        }

        _documentationWorkflowModalOpen = true;
        _documentationWorkflowMessage = null;
        SyncDocumentationWorkflowRunSelection();
        await LoadDocumentationWorkflowLogAsync(_currentDocumentationWorkflowRunId);
    }

    private void CloseDocumentationWorkflowModal()
    {
        _documentationWorkflowModalOpen = false;
        _documentationWorkflowMessage = null;
        _currentDocumentationWorkflowRunId = null;
    }

    private void ResetDocumentationWorkflowModalState()
    {
        _documentationWorkflowModalOpen = false;
        _documentationWorkflowMessage = null;
        _documentationWorkflowLog = null;
        _documentationWorkflowLogLoading = false;
        _startingDocumentationWorkflow = false;
        _currentDocumentationWorkflowRunId = null;
    }

    private WorkflowRunRow? GetCurrentDocumentationWorkflowRun()
        => _currentDocumentationWorkflowRunId.HasValue
            ? _runs.FirstOrDefault(x => x.Id == _currentDocumentationWorkflowRunId.Value)
            : _runs
                .Where(x => string.Equals(x.WorkflowCode, "setup-documentation", StringComparison.OrdinalIgnoreCase)
                    && x.RequirementId is null
                    && x.BacklogItemId is null)
                .OrderByDescending(x => x.StartedUtc)
                .FirstOrDefault();

    private async Task LoadDocumentationWorkflowLogAsync(Guid? workflowRunId)
    {
        await RefreshDocumentationWorkflowLogAsync(workflowRunId, showLoadingIndicator: true);
    }

    private async Task<bool> RefreshDocumentationWorkflowLogAsync(Guid? workflowRunId, bool showLoadingIndicator)
    {
        if (!workflowRunId.HasValue)
        {
            var hadLogState = _documentationWorkflowLog is not null || _documentationWorkflowLogLoading;
            _documentationWorkflowLog = null;
            _documentationWorkflowLogLoading = false;
            return hadLogState;
        }

        if (showLoadingIndicator && !_documentationWorkflowLogLoading)
        {
            _documentationWorkflowLogLoading = true;
            StateHasChanged();
        }

        try
        {
            var client = HttpClientFactory.CreateClient("api");
            var latestLog = await GetWorkflowLogContentAsync(client, workflowRunId.Value, $"api/workflow-runs/{workflowRunId.Value}/log");
            var changed = !AreWorkflowLogsEquivalent(_documentationWorkflowLog, latestLog);
            _documentationWorkflowLog = latestLog;
            return changed;
        }
        finally
        {
            var wasLoading = _documentationWorkflowLogLoading;
            _documentationWorkflowLogLoading = false;

            if (showLoadingIndicator && wasLoading)
            {
                StateHasChanged();
            }
        }
    }

    private void SyncDocumentationWorkflowRunSelection()
    {
        var availableDocumentationRunIds = _runs
            .Where(x => string.Equals(x.WorkflowCode, "setup-documentation", StringComparison.OrdinalIgnoreCase)
                && x.RequirementId is null
                && x.BacklogItemId is null)
            .Select(x => x.Id)
            .ToHashSet();

        if (_currentDocumentationWorkflowRunId.HasValue && availableDocumentationRunIds.Contains(_currentDocumentationWorkflowRunId.Value))
        {
            return;
        }

        _currentDocumentationWorkflowRunId = _runs
            .Where(x => string.Equals(x.WorkflowCode, "setup-documentation", StringComparison.OrdinalIgnoreCase)
                && x.RequirementId is null
                && x.BacklogItemId is null)
            .OrderByDescending(x => x.StartedUtc)
            .Select(x => (Guid?)x.Id)
            .FirstOrDefault();
    }

    private void CloseDrawer()
    {
        _drawerOpen = false;
        _activeCard = null;
        _activeDocument = null;
        _activeWorkflowRunDetails = null;
        _activeWorkflowRunDetailsLoading = false;
    }

    private void CloseViewer()
    {
        _viewerOpen = false;
        _activeWorkflowLog = null;
        _activeWorkflowArtifact = null;
    }

    private string GetViewerTitle()
    {
        if (_activeWorkflowLog is not null)
        {
            return "Workflow log";
        }

        if (_activeWorkflowArtifact is null)
        {
            return "Workflow viewer";
        }

        return _activeWorkflowArtifact.FileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
            || _activeWorkflowArtifact.FileName.Contains("input", StringComparison.OrdinalIgnoreCase)
            || _activeWorkflowArtifact.FileName.Contains("output", StringComparison.OrdinalIgnoreCase)
            ? "Workflow payload"
            : _activeWorkflowArtifact.FileName.Contains("prompt", StringComparison.OrdinalIgnoreCase)
                ? "Workflow prompt"
                : "Workflow artifact";
    }

    private string GetViewerSubtitle()
        => _activeWorkflowLog?.FileName
           ?? _activeWorkflowArtifact?.FileName
           ?? string.Empty;

    private async Task CopyViewerContentToClipboardAsync()
    {
        var content = _activeWorkflowLog?.Content ?? _activeWorkflowArtifact?.Content;
        if (string.IsNullOrWhiteSpace(content))
        {
            return;
        }

        await JS.InvokeVoidAsync("navigator.clipboard.writeText", content);
    }

    private async Task<WorkflowArtifactContent> GetWorkflowArtifactContentAsync(HttpClient client, Guid workflowRunId, string endpoint, string fileName, bool preferJson)
    {
        try
        {
            using var response = await client.GetAsync(endpoint);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return new WorkflowArtifactContent
                {
                    WorkflowRunId = workflowRunId,
                    FileName = fileName,
                    IsUnavailable = true,
                    Message = await ReadApiErrorAsync(response)
                };
            }

            response.EnsureSuccessStatusCode();
            var payload = await response.Content.ReadFromJsonAsync<WorkflowArtifactContent>() ?? new WorkflowArtifactContent
            {
                WorkflowRunId = workflowRunId,
                FileName = fileName,
                IsUnavailable = true,
                Message = "Content not available for this workflow run."
            };

            payload.FileName = string.IsNullOrWhiteSpace(payload.FileName) ? fileName : payload.FileName;
            payload.Content = NormalizeViewerContent(payload.Content, preferJson || LooksLikeJsonFile(payload.FileName));
            return payload;
        }
        catch (Exception ex)
        {
            return new WorkflowArtifactContent
            {
                WorkflowRunId = workflowRunId,
                FileName = fileName,
                IsUnavailable = true,
                Message = $"Failed to load content. {ex.Message}"
            };
        }
    }

    private async Task<WorkflowLogContent> GetWorkflowLogContentAsync(HttpClient client, Guid workflowRunId, string endpoint)
    {
        try
        {
            using var response = await client.GetAsync(endpoint);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return new WorkflowLogContent
                {
                    WorkflowRunId = workflowRunId,
                    FileName = $"{workflowRunId}.log",
                    IsUnavailable = true,
                    Message = await ReadApiErrorAsync(response)
                };
            }

            response.EnsureSuccessStatusCode();
            var payload = await response.Content.ReadFromJsonAsync<WorkflowLogContent>() ?? new WorkflowLogContent
            {
                WorkflowRunId = workflowRunId,
                FileName = $"{workflowRunId}.log",
                IsUnavailable = true,
                Message = "Log not available for this workflow run."
            };

            payload.Content = NormalizeViewerContent(payload.Content, false);
            return payload;
        }
        catch (Exception ex)
        {
            return new WorkflowLogContent
            {
                WorkflowRunId = workflowRunId,
                FileName = $"{workflowRunId}.log",
                IsUnavailable = true,
                Message = $"Failed to load log. {ex.Message}"
            };
        }
    }

    private static string NormalizeViewerContent(string? content, bool preferJson)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return string.Empty;
        }

        var normalized = content.Trim();

        if (preferJson)
        {
            return NormalizeJsonViewerText(normalized);
        }

        var decodedText = TryDecodeJsonString(normalized);
        if (!string.IsNullOrWhiteSpace(decodedText))
        {
            normalized = decodedText;
        }

        return NormalizePlainViewerText(normalized);
    }

    private static string NormalizeJsonViewerText(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        if (TryPrettyPrintJson(value, out var prettyJson))
        {
            return NormalizePlainViewerText(prettyJson);
        }

        var decodedText = TryDecodeJsonString(value);
        if (!string.IsNullOrWhiteSpace(decodedText))
        {
            if (TryPrettyPrintJson(decodedText, out prettyJson))
            {
                return NormalizePlainViewerText(prettyJson);
            }

            return NormalizePlainViewerText(decodedText);
        }

        return NormalizePlainViewerText(value);
    }

    private static string NormalizePlainViewerText(string value)
    {
        if (string.IsNullOrEmpty(value))
            return value;

        const string escapedR = "__ESCAPED_BACKSLASH_R__";
        const string escapedN = "__ESCAPED_BACKSLASH_N__";

        return value
        .Replace("\\r", escapedR)
        .Replace("\\n", escapedN)
        .Replace("\r\n", Environment.NewLine)
        .Replace("\r", Environment.NewLine)
        .Replace("\n", Environment.NewLine)
        .Replace(escapedR, "\\r")
        .Replace(escapedN, "\\n");
    }

    private static string? TryDecodeJsonString(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        try
        {
            var wrapped = raw;
            if (!(wrapped.Length >= 2 && wrapped[0] == '"' && wrapped[^1] == '"'))
            {
                wrapped = System.Text.Json.JsonSerializer.Serialize(raw);
            }

            return System.Text.Json.JsonSerializer.Deserialize<string>(wrapped);
        }
        catch
        {
            return null;
        }
    }

    private static bool LooksLikeJsonFile(string? fileName)
        => !string.IsNullOrWhiteSpace(fileName)
           && fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase);

    private static bool TryPrettyPrintJson(string raw, out string formatted)
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(raw);
            formatted = System.Text.Json.JsonSerializer.Serialize(document.RootElement, new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true
            });
            return true;
        }
        catch
        {
            formatted = string.Empty;
            return false;
        }
    }

    private void OpenCreateSolutionPanel()
    {
        if (!CanOpenSolutionPanel)
        {
            return;
        }

        _createRequirementPanelOpen = false;
        _createRequirementMessage = null;
        _createSolutionMessage = null;
        _createSolutionPanelOpen = true;
        StateHasChanged();
    }

    private void CloseCreateSolutionPanel()
    {
        if (_savingSolution)
        {
            return;
        }

        _createSolutionPanelOpen = false;
    }

    private void OpenCreateRequirementPanel()
    {
        if (!CanOpenRequirementPanel)
        {
            return;
        }

        _createSolutionPanelOpen = false;
        _createSolutionMessage = null;
        _createRequirementMessage = null;
        _createRequirementPanelOpen = true;
        StateHasChanged();
    }

    private void CloseCreateRequirementPanel()
    {
        if (_savingRequirement)
        {
            return;
        }

        _createRequirementPanelOpen = false;
    }

    private async Task CreateSolutionAsync()
    {
        _savingSolution = true;
        _createSolutionMessage = null;

        try
        {
            var client = HttpClientFactory.CreateClient("api");
            var response = await client.PostAsJsonAsync("api/workflow-runs/setup-solution", new
            {
                _solutionRequest.SolutionId,
                _solutionRequest.Name,
                _solutionRequest.Description,
                _solutionRequest.RepositoryPath,
                _solutionRequest.MainSolutionFile,
                _solutionRequest.ProfileCode,
                _solutionRequest.TargetCode,
                _solutionRequest.OverlayTargetId,
                _solutionRequest.RemoteRepositoryUrl,
                RequestedBy = "rui"
            });

            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(await ReadApiErrorAsync(response));
            }

            var createdSolutionName = _solutionRequest.Name;
            _createSolutionMessageSeverity = Severity.Success;
            _createSolutionMessage = "Solution setup workflow completed.";
            ResetSolutionForm();
            await LoadSolutionsAsync();

            var newSelection = _solutions.FirstOrDefault(x => string.Equals(x.Name, createdSolutionName, StringComparison.OrdinalIgnoreCase))
                ?? _solutions.OrderByDescending(x => x.CreatedUtc).FirstOrDefault();

            if (newSelection is not null)
            {
                _selectedTargetId = newSelection.Targets.Count == 1 ? newSelection.Targets[0].Id : newSelection.Targets.FirstOrDefault()?.Id;
                SelectedSolutionState.SetCurrent(newSelection, _selectedTargetId);
            }

            _createSolutionPanelOpen = false;
            await LoadDocumentsAsync();
            await LoadCockpitAsync();
        }
        catch (Exception ex)
        {
            _createSolutionMessageSeverity = Severity.Error;
            _createSolutionMessage = ex.Message;
        }
        finally
        {
            _savingSolution = false;
        }
    }

    private async Task CreateRequirementAsync()
    {
        if (SelectedSolutionState.CurrentTarget is null)
        {
            return;
        }

        _savingRequirement = true;
        _createRequirementMessage = null;

        try
        {
            var client = HttpClientFactory.CreateClient("api");
            var response = await client.PostAsJsonAsync("api/requirements", new
            {
                TargetSolutionId = SelectedSolutionState.CurrentTarget.Id,
                _requirementRequest.Title,
                _requirementRequest.Description,
                _requirementRequest.RequirementType,
                _requirementRequest.Source,
                _requirementRequest.Priority
            });

            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(await ReadApiErrorAsync(response));
            }

            _createRequirementMessageSeverity = Severity.Success;
            _createRequirementMessage = "Requirement created.";
            ResetRequirementForm();
            _createRequirementPanelOpen = false;
            await LoadCockpitAsync();
        }
        catch (Exception ex)
        {
            _createRequirementMessageSeverity = Severity.Error;
            _createRequirementMessage = ex.Message;
        }
        finally
        {
            _savingRequirement = false;
        }
    }

    private async Task RunDocumentationWorkflowAsync()
    {
        if (SelectedSolutionState.CurrentTarget is null)
        {
            return;
        }

        _startingDocumentationWorkflow = true;
        _documentationWorkflowMessage = null;
        _documentationWorkflowLog = null;
        _documentationWorkflowLogLoading = true;

        try
        {
            var client = HttpClientFactory.CreateClient("api");
            var response = await client.PostAsJsonAsync("api/workflow-runs/setup-documentation", new
            {
                TargetSolutionId = SelectedSolutionState.CurrentTarget.Id,
                RequestedBy = "rui"
            });

            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(await ReadApiErrorAsync(response));
            }

            var payload = await response.Content.ReadFromJsonAsync<WorkflowRunStartedResponse>();
            _currentDocumentationWorkflowRunId = payload?.Id;
            _documentationWorkflowMessageSeverity = Severity.Success;
            _documentationWorkflowMessage = "Documentation workflow started.";
            await LoadCockpitAsync(showLoading: false);
            await LoadDocumentationWorkflowLogAsync(_currentDocumentationWorkflowRunId);
        }
        catch (Exception ex)
        {
            _documentationWorkflowMessageSeverity = Severity.Error;
            _documentationWorkflowMessage = ex.Message;
        }
        finally
        {
            _startingDocumentationWorkflow = false;
        }
    }

    private static bool AreWorkflowLogsEquivalent(WorkflowLogContent? left, WorkflowLogContent? right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left is null || right is null)
        {
            return false;
        }

        return left.WorkflowRunId == right.WorkflowRunId
            && string.Equals(left.FileName, right.FileName, StringComparison.Ordinal)
            && left.IsUnavailable == right.IsUnavailable
            && string.Equals(left.Message, right.Message, StringComparison.Ordinal)
            && string.Equals(left.Content, right.Content, StringComparison.Ordinal);
    }


    private static async Task<string> ReadApiErrorAsync(HttpResponseMessage response)
    {
        try
        {
            var error = await response.Content.ReadFromJsonAsync<ApiErrorResponse>();
            if (!string.IsNullOrWhiteSpace(error?.Message))
            {
                return error.Message;
            }
        }
        catch
        {
        }

        return $"Request failed: {(int)response.StatusCode} {response.ReasonPhrase}".Trim();
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(await ReadApiErrorAsync(response));
        }
    }

    private async Task StartAnalyzeAsync(Guid requirementId)
    {
        _runningRequirementId = requirementId;
        await StartRequirementWorkflowAsync("api/workflow-runs/analyze-request", requirementId);
    }

    private async Task StartDesignAsync(Guid requirementId)
    {
        _runningRequirementId = requirementId;
        await StartRequirementWorkflowAsync("api/workflow-runs/design-solution-change", requirementId);
    }

    private async Task StartPlanAsync(Guid requirementId)
    {
        _runningRequirementId = requirementId;
        await StartRequirementWorkflowAsync("api/workflow-runs/plan-implementation", requirementId);
    }

    private async Task StartRequirementWorkflowAsync(string endpoint, Guid requirementId)
    {
        try
        {
            var client = HttpClientFactory.CreateClient("api");
            var response = await client.PostAsJsonAsync(endpoint, new { RequirementId = requirementId, RequestedBy = "rui" });
            
            if (!response.IsSuccessStatusCode)
            {
                _loadError = await ReadApiErrorAsync(response);
                StateHasChanged();
                return;
            }

            _loadError = null;
            await LoadCockpitAsync();
        }
        finally
        {
            _runningRequirementId = null;
        }
    }

    private async Task StartImplementationAsync(Guid backlogItemId)
    {
        _runningBacklogItemId = backlogItemId;
        try
        {
            var client = HttpClientFactory.CreateClient("api");
            var response = await client.PostAsJsonAsync("api/workflow-runs/implement-solution-change", new { BacklogItemId = backlogItemId, RequestedBy = "rui" });

            if (!response.IsSuccessStatusCode)
            {
                _loadError = await ReadApiErrorAsync(response);
                StateHasChanged();
                return;
            }

            _loadError = null;
            await LoadCockpitAsync();
        }
        finally
        {
            _runningBacklogItemId = null;
        }
    }

    private bool CanRun(RequirementStageCard card)
    {
        if (card.BacklogItem is not null)
        {
            return CanImplement(card.BacklogItem);
        }

        return card.LaneKey switch
        {
            LaneKeys.Analysis => CanAnalyze(card.Requirement),
            LaneKeys.Design => CanDesign(card.Requirement),
            LaneKeys.Planning => CanPlan(card.Requirement),
            _ => false
        };
    }

    private bool IsRunBusy(RequirementStageCard card)
    {
        if (card.BacklogItem is not null)
        {
            return _runningBacklogItemId == card.BacklogItem.Id;
        }

        return _runningRequirementId == card.Requirement.Id;
    }

    private async Task RunAsync(RequirementStageCard card)
    {
        if (card.BacklogItem is not null)
        {
            await StartImplementationAsync(card.BacklogItem.Id);
            return;
        }

        switch (card.LaneKey)
        {
            case LaneKeys.Analysis:
                await StartAnalyzeAsync(card.Requirement.Id);
                break;
            case LaneKeys.Design:
                await StartDesignAsync(card.Requirement.Id);
                break;
            case LaneKeys.Planning:
                await StartPlanAsync(card.Requirement.Id);
                break;
        }
    }

    private async Task OpenWorkflowReportAsync(Guid workflowRunId)
    {
        var reportFileName = GetWorkflowReportFileName(_activeWorkflowRunDetails);
        if (string.IsNullOrWhiteSpace(reportFileName))
        {
            return;
        }

        await OpenWorkflowArtifactAsync(workflowRunId, reportFileName);
    }

    private static bool CanViewWorkflowReport(WorkflowRunDetail? detail)
        => !string.IsNullOrWhiteSpace(GetWorkflowReportFileName(detail));

    private static string? GetWorkflowReportFileName(WorkflowRunDetail? detail)
    {
        if (detail is null || detail.ArtifactFiles.Count == 0)
        {
            return null;
        }

        var preferredFiles = new[]
        {
            "analysis-report.md",
            "design-report.json",
            "implementation-plan.json",
            "implementation-result.json"
        };

        foreach (var fileName in preferredFiles)
        {
            var match = detail.ArtifactFiles.FirstOrDefault(x => string.Equals(x, fileName, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(match))
            {
                return match;
            }
        }

        return detail.ArtifactFiles.FirstOrDefault(x => x.EndsWith("-report.md", StringComparison.OrdinalIgnoreCase)
            || x.EndsWith("-report.json", StringComparison.OrdinalIgnoreCase)
            || x.EndsWith("-plan.json", StringComparison.OrdinalIgnoreCase)
            || x.EndsWith("-result.json", StringComparison.OrdinalIgnoreCase));
    }


    private void EnsureWorkflowPollingState()
    {
        if (HasActiveWorkflowRuns())
        {
            StartWorkflowPolling();
            return;
        }

        StopWorkflowPolling();
    }

    private bool HasActiveWorkflowRuns()
        => _runs.Any(x => IsExecutingWorkflowStatus(x.Status));

    private void StartWorkflowPolling()
    {
        if (_workflowPollingTask is { IsCompleted: false })
        {
            return;
        }

        _workflowPollingCts?.Cancel();
        _workflowPollingCts?.Dispose();
        _workflowPollingCts = new CancellationTokenSource();
        _workflowPollingTask = MonitorWorkflowChangesAsync(_workflowPollingCts.Token);
    }

    private void StopWorkflowPolling()
    {
        _workflowPollingCts?.Cancel();
        _workflowPollingCts?.Dispose();
        _workflowPollingCts = null;
        _workflowPollingTask = null;
    }

    private async Task MonitorWorkflowChangesAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(6));

        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                if (_isRefreshingWorkflowState || SelectedSolutionState.CurrentTarget is null)
                {
                    continue;
                }

                await InvokeAsync(async () =>
                {
                    await LoadCockpitAsync(showLoading: false);
                });
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static string BuildCockpitSnapshot(
        Guid targetSolutionId,
        IReadOnlyList<RequirementRow> requirements,
        IReadOnlyList<BacklogRow> backlog,
        IReadOnlyList<WorkflowRunRow> runs)
    {
        var requirementPart = string.Join("|", requirements
            .OrderBy(x => x.Id)
            .Select(x => $"{x.Id}:{x.Status}"));

        var backlogPart = string.Join("|", backlog
            .OrderBy(x => x.Id)
            .Select(x => $"{x.Id}:{x.RequirementId}:{x.Status}"));

        var runPart = string.Join("|", runs
            .OrderBy(x => x.Id)
            .Select(x => $"{x.Id}:{x.RequirementId}:{x.BacklogItemId}:{x.WorkflowCode}:{x.Status}"));

        return $"{targetSolutionId}||{requirementPart}||{backlogPart}||{runPart}";
    }

    private bool CanAnalyze(RequirementRow requirement)
        => (string.Equals(CockpitLifecycleRules.NormalizeRequirementStatus(requirement.Status), CockpitLifecycleRules.Pending, StringComparison.OrdinalIgnoreCase)
           || string.Equals(CockpitLifecycleRules.NormalizeRequirementStatus(requirement.Status), CockpitLifecycleRules.Analyze, StringComparison.OrdinalIgnoreCase))
           && !HasBlockingRun(requirement.Id, null, "analyze-request");

    private bool CanDesign(RequirementRow requirement)
        => string.Equals(CockpitLifecycleRules.NormalizeRequirementStatus(requirement.Status), CockpitLifecycleRules.Design, StringComparison.OrdinalIgnoreCase)
           && !HasBlockingRun(requirement.Id, null, "design-solution-change");

    private bool CanPlan(RequirementRow requirement)
        => string.Equals(CockpitLifecycleRules.NormalizeRequirementStatus(requirement.Status), CockpitLifecycleRules.Plan, StringComparison.OrdinalIgnoreCase)
           && !HasBlockingRun(requirement.Id, null, "plan-implementation");

    private bool CanCommitRequirement(RequirementRow requirement)
        => CockpitLifecycleRules.CanCommitRequirement(requirement.Status);

    private bool CanDeleteRequirement(RequirementRow requirement)
        => CockpitLifecycleRules.CanDeleteRequirement(
            requirement.Status,
            _runs.Any(x => x.RequirementId == requirement.Id),
            _backlog.Any(x => x.RequirementId == requirement.Id));

    private bool CanCancelRequirement(RequirementRow requirement)
        => CockpitLifecycleRules.CanCancelRequirement(requirement.Status, HasRunningRun(requirement.Id));

    private bool ShowRequirementDecisionActions(RequirementStageCard card)
        => string.Equals(card.LaneKey, LaneKeys.Requirement, StringComparison.OrdinalIgnoreCase)
            && IsAwaitingRequirementDecision(card.Requirement);

    private bool CanImplement(BacklogRow backlogItem)
    {
        if (backlogItem.RequirementId.HasValue
            && !string.Equals(CockpitLifecycleRules.NormalizeRequirementStatus(GetRequirementStatus(backlogItem.RequirementId)), CockpitLifecycleRules.Implement, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.Equals(backlogItem.Status, "NotImplemented", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(backlogItem.Status, "ImplementationError", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (backlogItem.RequirementId is null)
        {
            return true;
        }

        var previousItems = _backlog
            .Where(x => x.RequirementId == backlogItem.RequirementId && x.PlanningOrder < backlogItem.PlanningOrder)
            .ToList();

        return previousItems.All(x => string.Equals(x.Status, "Validated", StringComparison.OrdinalIgnoreCase)
            || string.Equals(x.Status, "Canceled", StringComparison.OrdinalIgnoreCase))
            && !HasBlockingRun(backlogItem.RequirementId, backlogItem.Id, "implement-solution-change");
    }


    private static bool IsDocumentationWorkflowInProgress(WorkflowRunRow? run)
        => run is not null && (run.Status == 1 || run.Status == 2);

    private static bool CanValidate(WorkflowRunRow run)
        => CockpitLifecycleRules.CanValidateWorkflowRun(run.Status);

    private static bool CanCancel(WorkflowRunRow run)
        => CockpitLifecycleRules.CanCancelWorkflowRun(run.Status);

    private static bool IsExecutingWorkflowStatus(int status)
        => CockpitLifecycleRules.IsBlockingWorkflowStatus(status);

    private bool HasBlockingRun(Guid? requirementId, Guid? backlogItemId, string workflowCode)
        => _runs.Any(x =>
            x.RequirementId == requirementId
            && x.BacklogItemId == backlogItemId
            && string.Equals(x.WorkflowCode, workflowCode, StringComparison.OrdinalIgnoreCase)
            && CockpitLifecycleRules.IsBlockingWorkflowStatus(x.Status));

    private bool HasAnyBlockingRun(Guid requirementId)
        => _runs.Any(x => x.RequirementId == requirementId && CockpitLifecycleRules.IsBlockingWorkflowStatus(x.Status));

    private bool HasRunningRun(Guid requirementId)
        => _runs.Any(x => x.RequirementId == requirementId && CockpitLifecycleRules.IsRunningWorkflowStatus(x.Status));

    private static bool CanViewWorkflowOutput(WorkflowRunDetail? detail)
        => detail?.HasOutputPayload == true;

    private static bool IsAnalyzeRequestWorkflow(WorkflowRunRow? run)
        => string.Equals(run?.WorkflowCode, "analyze-request", StringComparison.OrdinalIgnoreCase);

    private static bool HasWorkflowArtifact(WorkflowRunDetail? detail, string fileName)
        => detail?.ArtifactFiles.Any(x => string.Equals(x, fileName, StringComparison.OrdinalIgnoreCase)) == true;

    private string GetRequirementStatus(Guid? requirementId)
        => _requirements.FirstOrDefault(x => x.Id == requirementId)?.Status ?? string.Empty;

    private string GetRequirementBadgeLabel(RequirementRow requirement)
        => CockpitLifecycleRules.GetRequirementBadgeLabel(requirement.Status);

    private VisualState GetRequirementBadgeVisualState(RequirementRow requirement)
        => GetRequirementVisualState(requirement.Status);

    private bool IsAwaitingRequirementDecision(RequirementRow requirement)
        => CockpitLifecycleRules.IsAwaitingDecision(requirement.Status);

    private bool IsFinalDecisionRun(WorkflowRunRow run, RequirementRow requirement)
        => run.Status == 3
            && string.Equals(run.WorkflowCode, "implement-solution-change", StringComparison.OrdinalIgnoreCase)
            && string.Equals(CockpitLifecycleRules.NormalizeRequirementStatus(requirement.Status), CockpitLifecycleRules.AwaitingDecision, StringComparison.OrdinalIgnoreCase);

    private bool CanViewWorkflowLog(WorkflowRunRow run)
        => CanViewWorkflowLog(run, _activeCard?.Requirement ?? _requirements.FirstOrDefault(x => x.Id == run.RequirementId));

    private bool CanViewWorkflowLog(WorkflowRunRow run, RequirementRow? requirement)
        => CockpitLifecycleRules.CanViewWorkflowArtifacts(run.Status, requirement is not null && IsFinalDecisionRun(run, requirement));

    private bool CanViewWorkflowReport(WorkflowRunRow run, RequirementRow requirement)
        => CanViewWorkflowLog(run, requirement) && CanViewWorkflowReport(_activeWorkflowRunDetails);

    private WorkflowRunRow? GetLatestRequirementRun(Guid requirementId)
        => _runs
            .Where(x => x.RequirementId == requirementId)
            .OrderByDescending(x => x.StartedUtc)
            .FirstOrDefault();

    private async Task DeleteRequirementAsync(Guid requirementId)
    {
        _mutatingRequirementId = requirementId;
        _loadError = null;

        try
        {
            var client = HttpClientFactory.CreateClient("api");
            var response = await client.DeleteAsync($"api/requirements/{requirementId}");
            await EnsureSuccessAsync(response);
            await LoadCockpitAsync();
            CloseDrawer();
        }
        catch (Exception ex)
        {
            _loadError = ex.Message;
        }
        finally
        {
            _mutatingRequirementId = null;
        }
    }

    private async Task CommitRequirementAsync(Guid requirementId)
    {
        _mutatingRequirementId = requirementId;
        _loadError = null;

        try
        {
            var client = HttpClientFactory.CreateClient("api");
            var response = await client.PostAsync($"api/requirements/{requirementId}/commit", null);
            await EnsureSuccessAsync(response);
            await LoadCockpitAsync();
        }
        catch (Exception ex)
        {
            _loadError = ex.Message;
        }
        finally
        {
            _mutatingRequirementId = null;
        }
    }

    private async Task CancelRequirementAsync(Guid requirementId)
    {
        _mutatingRequirementId = requirementId;
        _loadError = null;

        try
        {
            var client = HttpClientFactory.CreateClient("api");
            var response = await client.PostAsync($"api/requirements/{requirementId}/cancel", null);
            await EnsureSuccessAsync(response);
            await LoadCockpitAsync();
        }
        catch (Exception ex)
        {
            _loadError = ex.Message;
        }
        finally
        {
            _mutatingRequirementId = null;
        }
    }

    private async Task ValidateWorkflowRunAsync(Guid workflowRunId)
    {
        _mutatingWorkflowRunId = workflowRunId;

        try
        {
            var client = HttpClientFactory.CreateClient("api");
            var response = await client.PostAsJsonAsync("api/workflow-runs/validate", new { WorkflowRunId = workflowRunId });

            if (!response.IsSuccessStatusCode)
            {
                _loadError = await ReadApiErrorAsync(response);
                StateHasChanged();
                return;
            }

            _loadError = null;
            await LoadCockpitAsync();
        }
        finally
        {
            _mutatingWorkflowRunId = null;
        }
    }

    private async Task CancelWorkflowRunAsync(Guid workflowRunId)
    {
        _mutatingWorkflowRunId = workflowRunId;

        try
        {
            var client = HttpClientFactory.CreateClient("api");
            var response = await client.PostAsJsonAsync("api/workflow-runs/cancel", new
            {
                WorkflowRunId = workflowRunId,
                TerminateRequirementLifecycle = false,
                Reason = "Cancelled from cockpit"
            });

            if (!response.IsSuccessStatusCode)
            {
                _loadError = await ReadApiErrorAsync(response);
                StateHasChanged();
                return;
            }

            _loadError = null;
            await LoadCockpitAsync();
        }
        finally
        {
            _mutatingWorkflowRunId = null;
        }
    }

    private static string HumanizeWorkflowStatus(int status)
        => status switch
        {
            1 => "Pending",
            2 => "Running",
            3 => "Validated",
            4 => "Error",
            5 => "Cancelled",
            6 => "Completed",
            _ => "Unknown"
        };

    private static string HumanizeRequirementStatus(string status)
        => CockpitLifecycleRules.HumanizeRequirementStatus(status);

    private static string HumanizeBacklogStatus(string status)
        => status switch
        {
            "NotImplemented" => "Pending",
            "AwaitingValidation" => "Awaiting Validation",
            "ImplementationError" => "Error",
            "Validated" => "Validated",
            "Canceled" => "Cancelled",
            _ => status
        };

    private static VisualState GetRequirementVisualState(string status)
    {
        return CockpitLifecycleRules.NormalizeRequirementStatus(status) switch
        {
            var value when string.Equals(value, CockpitLifecycleRules.Pending, StringComparison.OrdinalIgnoreCase) => VisualStatePalette.Waiting,
            var value when string.Equals(value, CockpitLifecycleRules.Analyze, StringComparison.OrdinalIgnoreCase) => VisualStatePalette.Waiting,
            var value when string.Equals(value, CockpitLifecycleRules.Design, StringComparison.OrdinalIgnoreCase) => VisualStatePalette.Waiting,
            var value when string.Equals(value, CockpitLifecycleRules.Plan, StringComparison.OrdinalIgnoreCase) => VisualStatePalette.Waiting,
            var value when string.Equals(value, CockpitLifecycleRules.Implement, StringComparison.OrdinalIgnoreCase) => VisualStatePalette.Waiting,
            var value when string.Equals(value, CockpitLifecycleRules.AwaitingDecision, StringComparison.OrdinalIgnoreCase) => VisualStatePalette.Waiting,
            var value when string.Equals(value, "Test", StringComparison.OrdinalIgnoreCase) => VisualStatePalette.Waiting,
            var value when string.Equals(value, "Review", StringComparison.OrdinalIgnoreCase) => VisualStatePalette.Waiting,
            var value when string.Equals(value, "Deliver", StringComparison.OrdinalIgnoreCase) => VisualStatePalette.Waiting,
            var value when string.Equals(value, "Documentation", StringComparison.OrdinalIgnoreCase) => VisualStatePalette.Waiting,
            var value when string.Equals(value, CockpitLifecycleRules.Completed, StringComparison.OrdinalIgnoreCase) => VisualStatePalette.Success,
            var value when string.Equals(value, "Canceled", StringComparison.OrdinalIgnoreCase) => VisualStatePalette.Error,
            var value when string.Equals(value, CockpitLifecycleRules.Cancelled, StringComparison.OrdinalIgnoreCase) => VisualStatePalette.Error,
            _ => VisualStatePalette.Default
        };
    }

    private static VisualState GetBacklogVisualState(string status)
        => status switch
        {
            "AwaitingValidation" => VisualStatePalette.Waiting,
            "Validated" => VisualStatePalette.Success,
            "ImplementationError" => VisualStatePalette.Error,
            "Canceled" => VisualStatePalette.Error,
            _ => VisualStatePalette.Default
        };

    private static VisualState GetWorkflowVisualState(int status)
        => status switch
        {
            2 => VisualStatePalette.Running,
            3 => VisualStatePalette.Success,
            4 => VisualStatePalette.Error,
            5 => VisualStatePalette.Error,
            6 => VisualStatePalette.Waiting,
            _ => VisualStatePalette.Default
        };

    private static string GetConnectorColor(VisualState current, VisualState next)
    {
        if (next != VisualStatePalette.Default)
        {
            return next.Color;
        }

        return current != VisualStatePalette.Default ? current.Color : VisualStatePalette.Default.Color;
    }

    private Task OnOverlayTargetChangedAsync(Guid? targetId)
    {
        _solutionRequest.OverlayTargetId = targetId;
        return Task.CompletedTask;
    }

    private void ResetSolutionForm()
    {
        _solutionRequest.SolutionId = null;
        _solutionRequest.Name = string.Empty;
        _solutionRequest.Description = string.Empty;
        _solutionRequest.RepositoryPath = string.Empty;
        _solutionRequest.MainSolutionFile = string.Empty;
        _solutionRequest.ProfileCode = DefaultProfileCode;
        _solutionRequest.TargetCode = DefaultTargetCode;
        _solutionRequest.OverlayTargetId = null;
        _solutionRequest.RemoteRepositoryUrl = string.Empty;
    }

    private void ResetRequirementForm()
    {
        _requirementRequest.Title = string.Empty;
        _requirementRequest.Description = string.Empty;
        _requirementRequest.RequirementType = "functional";
        _requirementRequest.Source = "user";
        _requirementRequest.Priority = "medium";
    }

    public void Dispose()
    {
        StopWorkflowPolling();
        SelectedSolutionState.Changed -= HandleSolutionChanged;
    }

    private static class LaneKeys
    {
        public const string Requirement = "requirement";
        public const string Analysis = "analysis";
        public const string Design = "design";
        public const string Planning = "planning";
        public const string Implementation = "implementation";
        public const string Test = "test";
        public const string Review = "review";
        public const string Deliver = "deliver";
    }

    private sealed record PipelineDefinition(string Key, string Title, string Icon);

    private sealed class WorkflowRunDetailResponse
    {
        public bool HasOutputPayload { get; set; }
        public List<string> ArtifactFiles { get; set; } = [];
        public WorkflowRunReportResponse? Report { get; set; }
    }

    private sealed class WorkflowRunReportResponse
    {
        public string? Summary { get; set; }
        public string? Status { get; set; }
        public string? DocumentationUpdatesJson { get; set; }
        public string? KnowledgeUpdatesJson { get; set; }
    }

    private sealed class WorkflowRunStartedResponse
    {
        public Guid Id { get; set; }
    }

    private static class VisualStatePalette
    {
        public static readonly VisualState Default = new("Default", "#94a3b8", "is-default");
        public static readonly VisualState Running = new("Running", "#2563eb", "is-running");
        public static readonly VisualState Success = new("Completed", "#16a34a", "is-complete");
        public static readonly VisualState Waiting = new("Waiting", "#d4a017", "is-waiting");
        public static readonly VisualState Error = new("Error", "#dc2626", "is-error");
    }
}
