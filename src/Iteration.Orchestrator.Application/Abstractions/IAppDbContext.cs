using Iteration.Orchestrator.Domain.Backlog;
using Iteration.Orchestrator.Domain.Decisions;
using Iteration.Orchestrator.Domain.Questions;
using Iteration.Orchestrator.Domain.Requirements;
using Iteration.Orchestrator.Domain.Reports;
using Iteration.Orchestrator.Domain.Solutions;
using Iteration.Orchestrator.Domain.Workflows;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Iteration.Orchestrator.Application.Abstractions;

public interface IAppDbContext
{
    DbSet<Solution> Solutions { get; }
    DbSet<SolutionTarget> SolutionTargets { get; }
    DbSet<BacklogItem> BacklogItems { get; }
    DbSet<WorkflowRun> WorkflowRuns { get; }
    DbSet<AgentTaskRun> AgentTaskRuns { get; }
    DbSet<AnalysisReport> AnalysisReports { get; }
    DbSet<DesignReport> DesignReports { get; }
    DbSet<PlanReport> PlanReports { get; }
    DbSet<ImplementationReport> ImplementationReports { get; }
    DbSet<OpenQuestion> OpenQuestions { get; }
    DbSet<Decision> Decisions { get; }
    DbSet<Requirement> Requirements { get; }

    Task<IDbContextTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default);
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
