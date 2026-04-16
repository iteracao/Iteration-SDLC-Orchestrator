using Iteration.Orchestrator.Application.Abstractions;
using Iteration.Orchestrator.Domain.Backlog;
using Iteration.Orchestrator.Domain.Decisions;
using Iteration.Orchestrator.Domain.Questions;
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
    public DbSet<OpenQuestion> OpenQuestions => Set<OpenQuestion>();
    public DbSet<Decision> Decisions => Set<Decision>();

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
        modelBuilder.Entity<OpenQuestion>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Title).HasMaxLength(300).IsRequired();
            e.Property(x => x.Description).HasMaxLength(4000).IsRequired();
            e.Property(x => x.Category).HasMaxLength(50);
            e.Property(x => x.Status).HasMaxLength(50).IsRequired();
            e.Property(x => x.ResolutionNotes).HasMaxLength(4000);
            e.HasIndex(x => x.TargetSolutionId);
            e.HasIndex(x => x.WorkflowRunId);
            e.HasOne<SolutionTarget>()
                .WithMany()
                .HasForeignKey(x => x.TargetSolutionId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne<WorkflowRun>()
                .WithMany()
                .HasForeignKey(x => x.WorkflowRunId)
                .OnDelete(DeleteBehavior.SetNull);
            e.HasOne<BacklogItem>()
                .WithMany()
                .HasForeignKey(x => x.BacklogItemId)
                .OnDelete(DeleteBehavior.SetNull);
        });
        modelBuilder.Entity<Decision>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Title).HasMaxLength(300).IsRequired();
            e.Property(x => x.Summary).HasMaxLength(4000).IsRequired();
            e.Property(x => x.DecisionType).HasMaxLength(50).IsRequired();
            e.Property(x => x.Status).HasMaxLength(50).IsRequired();
            e.Property(x => x.Rationale).HasMaxLength(4000);
            e.Property(x => x.ConsequencesJson).IsRequired();
            e.Property(x => x.AlternativesConsideredJson).IsRequired();
            e.HasIndex(x => x.TargetSolutionId);
            e.HasIndex(x => x.WorkflowRunId);
            e.HasOne<SolutionTarget>()
                .WithMany()
                .HasForeignKey(x => x.TargetSolutionId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne<WorkflowRun>()
                .WithMany()
                .HasForeignKey(x => x.WorkflowRunId)
                .OnDelete(DeleteBehavior.SetNull);
            e.HasOne<BacklogItem>()
                .WithMany()
                .HasForeignKey(x => x.BacklogItemId)
                .OnDelete(DeleteBehavior.SetNull);
        });
    }
}
