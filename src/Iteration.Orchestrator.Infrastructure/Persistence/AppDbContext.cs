using Iteration.Orchestrator.Application.Abstractions;
using Iteration.Orchestrator.Domain.Backlog;
using Iteration.Orchestrator.Domain.Decisions;
using Iteration.Orchestrator.Domain.Questions;
using Iteration.Orchestrator.Domain.Requirements;
using Iteration.Orchestrator.Domain.Reports;
using Iteration.Orchestrator.Domain.Solutions;
using Iteration.Orchestrator.Domain.Workflows;
using Microsoft.EntityFrameworkCore;

namespace Iteration.Orchestrator.Infrastructure.Persistence;

public sealed class AppDbContext : DbContext, IAppDbContext
{
    public DbSet<Solution> Solutions => Set<Solution>();
    public DbSet<SolutionTarget> SolutionTargets => Set<SolutionTarget>();
    public DbSet<BacklogItem> BacklogItems => Set<BacklogItem>();
    public DbSet<WorkflowRun> WorkflowRuns => Set<WorkflowRun>();
    public DbSet<AgentTaskRun> AgentTaskRuns => Set<AgentTaskRun>();
    public DbSet<AnalysisReport> AnalysisReports => Set<AnalysisReport>();
    public DbSet<DesignReport> DesignReports => Set<DesignReport>();
    public DbSet<PlanReport> PlanReports => Set<PlanReport>();
    public DbSet<ImplementationReport> ImplementationReports => Set<ImplementationReport>();
    public DbSet<OpenQuestion> OpenQuestions => Set<OpenQuestion>();
    public DbSet<Decision> Decisions => Set<Decision>();
    public DbSet<Requirement> Requirements => Set<Requirement>();

    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Solution>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.Description).HasMaxLength(4000).IsRequired();
            e.Property(x => x.ProfileCode).HasMaxLength(100).IsRequired();
            e.HasIndex(x => x.Name).IsUnique();
        });

        modelBuilder.Entity<SolutionTarget>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.SolutionId).IsRequired();
            e.Property(x => x.Code).HasMaxLength(100).IsRequired();
            e.HasIndex(x => x.Code).IsUnique();
            e.HasIndex(x => x.SolutionId).IsUnique();
            e.HasOne<Solution>()
                .WithMany()
                .HasForeignKey(x => x.SolutionId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Requirement>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Title).HasMaxLength(300).IsRequired();
            e.Property(x => x.Description).HasMaxLength(4000).IsRequired();
            e.Property(x => x.RequirementType).HasMaxLength(50).IsRequired();
            e.Property(x => x.Source).HasMaxLength(50).IsRequired();
            e.Property(x => x.Status).HasMaxLength(50).IsRequired();
            e.Property(x => x.Priority).HasMaxLength(50).IsRequired();
            e.Property(x => x.AcceptanceCriteriaJson).IsRequired();
            e.Property(x => x.ConstraintsJson).IsRequired();
            e.HasIndex(x => x.TargetSolutionId);
            e.HasIndex(x => x.OriginatingBacklogItemId);
            e.HasIndex(x => x.WorkflowRunId);
            e.HasIndex(x => x.ParentRequirementId);
            e.HasOne<SolutionTarget>()
                .WithMany()
                .HasForeignKey(x => x.TargetSolutionId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne<BacklogItem>()
                .WithMany()
                .HasForeignKey(x => x.OriginatingBacklogItemId)
                .OnDelete(DeleteBehavior.SetNull);
            e.HasOne<WorkflowRun>()
                .WithMany()
                .HasForeignKey(x => x.WorkflowRunId)
                .OnDelete(DeleteBehavior.SetNull);
            e.HasOne<Requirement>()
                .WithMany()
                .HasForeignKey(x => x.ParentRequirementId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<BacklogItem>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Title).HasMaxLength(300).IsRequired();
            e.Property(x => x.Description).HasMaxLength(4000).IsRequired();
            e.Property(x => x.WorkflowCode).HasMaxLength(100).IsRequired();
            e.HasIndex(x => x.TargetSolutionId);
            e.HasIndex(x => x.RequirementId);
            e.HasIndex(x => x.PlanWorkflowRunId);
            e.HasOne<SolutionTarget>()
                .WithMany()
                .HasForeignKey(x => x.TargetSolutionId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Requirement>()
                .WithMany()
                .HasForeignKey(x => x.RequirementId)
                .OnDelete(DeleteBehavior.SetNull);
            e.HasOne<WorkflowRun>()
                .WithMany()
                .HasForeignKey(x => x.PlanWorkflowRunId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<WorkflowRun>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.TargetSolutionId);
            e.HasIndex(x => x.RequirementId);
            e.HasIndex(x => x.BacklogItemId);
            e.HasOne<SolutionTarget>()
                .WithMany()
                .HasForeignKey(x => x.TargetSolutionId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Requirement>()
                .WithMany()
                .HasForeignKey(x => x.RequirementId)
                .OnDelete(DeleteBehavior.SetNull);
            e.HasOne<BacklogItem>()
                .WithMany()
                .HasForeignKey(x => x.BacklogItemId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<AgentTaskRun>(e => e.HasKey(x => x.Id));
        modelBuilder.Entity<AnalysisReport>(e => e.HasKey(x => x.Id));
        modelBuilder.Entity<DesignReport>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.WorkflowRunId).IsUnique();
            e.HasIndex(x => x.RequirementId);
            e.HasOne<WorkflowRun>()
                .WithMany()
                .HasForeignKey(x => x.WorkflowRunId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Requirement>()
                .WithMany()
                .HasForeignKey(x => x.RequirementId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<PlanReport>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.WorkflowRunId).IsUnique();
            e.HasIndex(x => x.RequirementId);
            e.HasOne<WorkflowRun>()
                .WithMany()
                .HasForeignKey(x => x.WorkflowRunId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Requirement>()
                .WithMany()
                .HasForeignKey(x => x.RequirementId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ImplementationReport>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.WorkflowRunId).IsUnique();
            e.HasIndex(x => x.RequirementId);
            e.HasIndex(x => x.BacklogItemId);
            e.HasOne<WorkflowRun>()
                .WithMany()
                .HasForeignKey(x => x.WorkflowRunId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Requirement>()
                .WithMany()
                .HasForeignKey(x => x.RequirementId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne<BacklogItem>()
                .WithMany()
                .HasForeignKey(x => x.BacklogItemId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<OpenQuestion>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Title).HasMaxLength(300).IsRequired();
            e.Property(x => x.Description).HasMaxLength(4000).IsRequired();
            e.Property(x => x.Category).HasMaxLength(50);
            e.Property(x => x.Status).HasMaxLength(50).IsRequired();
            e.Property(x => x.ResolutionNotes).HasMaxLength(4000);
            e.HasIndex(x => x.TargetSolutionId);
            e.HasIndex(x => x.RequirementId);
            e.HasIndex(x => x.WorkflowRunId);
            e.HasIndex(x => x.BacklogItemId);
            e.HasOne<SolutionTarget>()
                .WithMany()
                .HasForeignKey(x => x.TargetSolutionId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Requirement>()
                .WithMany()
                .HasForeignKey(x => x.RequirementId)
                .OnDelete(DeleteBehavior.SetNull);
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
            e.HasIndex(x => x.RequirementId);
            e.HasIndex(x => x.WorkflowRunId);
            e.HasIndex(x => x.BacklogItemId);
            e.HasOne<SolutionTarget>()
                .WithMany()
                .HasForeignKey(x => x.TargetSolutionId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Requirement>()
                .WithMany()
                .HasForeignKey(x => x.RequirementId)
                .OnDelete(DeleteBehavior.SetNull);
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
