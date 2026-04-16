using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Iteration.Orchestrator.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialOrchestratorModel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AgentTaskRuns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    WorkflowRunId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AgentCode = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    StartedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CompletedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    InputPayloadJson = table.Column<string>(type: "TEXT", nullable: false),
                    OutputPayloadJson = table.Column<string>(type: "TEXT", nullable: true),
                    FailureReason = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentTaskRuns", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AnalysisReports",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    WorkflowRunId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Summary = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    ArtifactsJson = table.Column<string>(type: "TEXT", nullable: false),
                    GeneratedRequirementsJson = table.Column<string>(type: "TEXT", nullable: false),
                    GeneratedOpenQuestionsJson = table.Column<string>(type: "TEXT", nullable: false),
                    GeneratedDecisionsJson = table.Column<string>(type: "TEXT", nullable: false),
                    DocumentationUpdatesJson = table.Column<string>(type: "TEXT", nullable: false),
                    KnowledgeUpdatesJson = table.Column<string>(type: "TEXT", nullable: false),
                    RecommendedNextWorkflowCodesJson = table.Column<string>(type: "TEXT", nullable: false),
                    RawOutputJson = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AnalysisReports", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "BacklogItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    Description = table.Column<string>(type: "TEXT", nullable: false),
                    TargetSolutionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    WorkflowCode = table.Column<string>(type: "TEXT", nullable: false),
                    Priority = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BacklogItems", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SolutionTargets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Code = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    RepositoryPath = table.Column<string>(type: "TEXT", nullable: false),
                    MainSolutionFile = table.Column<string>(type: "TEXT", nullable: false),
                    ProfileCode = table.Column<string>(type: "TEXT", nullable: false),
                    SolutionOverlayCode = table.Column<string>(type: "TEXT", nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SolutionTargets", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WorkflowRuns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    BacklogItemId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TargetSolutionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    WorkflowCode = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    CurrentStage = table.Column<string>(type: "TEXT", nullable: false),
                    StartedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CompletedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    RequestedBy = table.Column<string>(type: "TEXT", nullable: false),
                    FailureReason = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkflowRuns", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SolutionTargets_Code",
                table: "SolutionTargets",
                column: "Code",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AgentTaskRuns");

            migrationBuilder.DropTable(
                name: "AnalysisReports");

            migrationBuilder.DropTable(
                name: "BacklogItems");

            migrationBuilder.DropTable(
                name: "SolutionTargets");

            migrationBuilder.DropTable(
                name: "WorkflowRuns");
        }
    }
}
