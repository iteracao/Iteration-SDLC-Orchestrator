using Iteration.Orchestrator.Application.Abstractions;
using Iteration.Orchestrator.Domain.Backlog;
using Iteration.Orchestrator.Domain.Reports;
using Iteration.Orchestrator.Domain.Solutions;
using Iteration.Orchestrator.Domain.Workflows;
using Microsoft.EntityFrameworkCore;

namespace Iteration.Orchestrator.Infrastructure.Persistence;

public sealed class AppDbContext : DbContext, IAppDbContext
{
    public DbSet<SolutionTarget> SolutionTargets => Set<SolutionTarget>();
    public DbSet<BacklogItem> BacklogItems => Set<BacklogItem>();
    public DbSet<WorkflowRun> WorkflowRuns => Set<WorkflowRun>();
    public DbSet<AgentTaskRun> AgentTaskRuns => Set<AgentTaskRun>();
    public DbSet<AnalysisReport> AnalysisReports => Set<AnalysisReport>();

    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SolutionTarget>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Code).HasMaxLength(100).IsRequired();
            e.HasIndex(x => x.Code).IsUnique();
        });

        modelBuilder.Entity<BacklogItem>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Title).HasMaxLength(300).IsRequired();
        });

        modelBuilder.Entity<WorkflowRun>(e => e.HasKey(x => x.Id));
        modelBuilder.Entity<AgentTaskRun>(e => e.HasKey(x => x.Id));
        modelBuilder.Entity<AnalysisReport>(e => e.HasKey(x => x.Id));
    }
}
