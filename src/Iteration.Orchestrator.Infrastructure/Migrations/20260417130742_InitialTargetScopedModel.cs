using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Iteration.Orchestrator.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialTargetScopedModel : Migration
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
                name: "Solutions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: false),
                    ProfileCode = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Solutions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SolutionTargets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SolutionId = table.Column<Guid>(type: "TEXT", nullable: false),
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
                    table.ForeignKey(
                        name: "FK_SolutionTargets_Solutions_SolutionId",
                        column: x => x.SolutionId,
                        principalTable: "Solutions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "BacklogItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TargetSolutionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RequirementId = table.Column<Guid>(type: "TEXT", nullable: true),
                    PlanWorkflowRunId = table.Column<Guid>(type: "TEXT", nullable: true),
                    PlanningOrder = table.Column<int>(type: "INTEGER", nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: false),
                    WorkflowCode = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Priority = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BacklogItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BacklogItems_SolutionTargets_TargetSolutionId",
                        column: x => x.TargetSolutionId,
                        principalTable: "SolutionTargets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Decisions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TargetSolutionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RequirementId = table.Column<Guid>(type: "TEXT", nullable: true),
                    WorkflowRunId = table.Column<Guid>(type: "TEXT", nullable: true),
                    BacklogItemId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Title = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    Summary = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: false),
                    DecisionType = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    Rationale = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    ConsequencesJson = table.Column<string>(type: "TEXT", nullable: false),
                    AlternativesConsideredJson = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Decisions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Decisions_BacklogItems_BacklogItemId",
                        column: x => x.BacklogItemId,
                        principalTable: "BacklogItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Decisions_SolutionTargets_TargetSolutionId",
                        column: x => x.TargetSolutionId,
                        principalTable: "SolutionTargets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DesignReports",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    WorkflowRunId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RequirementId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Summary = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    ArtifactsJson = table.Column<string>(type: "TEXT", nullable: false),
                    GeneratedOpenQuestionsJson = table.Column<string>(type: "TEXT", nullable: false),
                    GeneratedDecisionsJson = table.Column<string>(type: "TEXT", nullable: false),
                    DocumentationUpdatesJson = table.Column<string>(type: "TEXT", nullable: false),
                    KnowledgeUpdatesJson = table.Column<string>(type: "TEXT", nullable: false),
                    RecommendedNextWorkflowCodesJson = table.Column<string>(type: "TEXT", nullable: false),
                    RawOutputJson = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DesignReports", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ImplementationReports",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    WorkflowRunId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RequirementId = table.Column<Guid>(type: "TEXT", nullable: false),
                    BacklogItemId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Summary = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    ImplementedChangesJson = table.Column<string>(type: "TEXT", nullable: false),
                    FilesTouchedJson = table.Column<string>(type: "TEXT", nullable: false),
                    TestsExecutedJson = table.Column<string>(type: "TEXT", nullable: false),
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
                    table.PrimaryKey("PK_ImplementationReports", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ImplementationReports_BacklogItems_BacklogItemId",
                        column: x => x.BacklogItemId,
                        principalTable: "BacklogItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "OpenQuestions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TargetSolutionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RequirementId = table.Column<Guid>(type: "TEXT", nullable: true),
                    WorkflowRunId = table.Column<Guid>(type: "TEXT", nullable: true),
                    BacklogItemId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Title = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: false),
                    Category = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    Status = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    ResolutionNotes = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ResolvedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OpenQuestions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OpenQuestions_BacklogItems_BacklogItemId",
                        column: x => x.BacklogItemId,
                        principalTable: "BacklogItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_OpenQuestions_SolutionTargets_TargetSolutionId",
                        column: x => x.TargetSolutionId,
                        principalTable: "SolutionTargets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PlanReports",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    WorkflowRunId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RequirementId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Summary = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    ArtifactsJson = table.Column<string>(type: "TEXT", nullable: false),
                    GeneratedBacklogItemsJson = table.Column<string>(type: "TEXT", nullable: false),
                    GeneratedOpenQuestionsJson = table.Column<string>(type: "TEXT", nullable: false),
                    GeneratedDecisionsJson = table.Column<string>(type: "TEXT", nullable: false),
                    DocumentationUpdatesJson = table.Column<string>(type: "TEXT", nullable: false),
                    KnowledgeUpdatesJson = table.Column<string>(type: "TEXT", nullable: false),
                    RecommendedNextWorkflowCodesJson = table.Column<string>(type: "TEXT", nullable: false),
                    RawOutputJson = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlanReports", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Requirements",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TargetSolutionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    OriginatingBacklogItemId = table.Column<Guid>(type: "TEXT", nullable: true),
                    WorkflowRunId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ParentRequirementId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Title = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: false),
                    RequirementType = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    Source = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    Priority = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    AcceptanceCriteriaJson = table.Column<string>(type: "TEXT", nullable: false),
                    ConstraintsJson = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Requirements", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Requirements_BacklogItems_OriginatingBacklogItemId",
                        column: x => x.OriginatingBacklogItemId,
                        principalTable: "BacklogItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Requirements_Requirements_ParentRequirementId",
                        column: x => x.ParentRequirementId,
                        principalTable: "Requirements",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Requirements_SolutionTargets_TargetSolutionId",
                        column: x => x.TargetSolutionId,
                        principalTable: "SolutionTargets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WorkflowRuns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    RequirementId = table.Column<Guid>(type: "TEXT", nullable: true),
                    BacklogItemId = table.Column<Guid>(type: "TEXT", nullable: true),
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
                    table.ForeignKey(
                        name: "FK_WorkflowRuns_BacklogItems_BacklogItemId",
                        column: x => x.BacklogItemId,
                        principalTable: "BacklogItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_WorkflowRuns_Requirements_RequirementId",
                        column: x => x.RequirementId,
                        principalTable: "Requirements",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_WorkflowRuns_SolutionTargets_TargetSolutionId",
                        column: x => x.TargetSolutionId,
                        principalTable: "SolutionTargets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BacklogItems_PlanWorkflowRunId",
                table: "BacklogItems",
                column: "PlanWorkflowRunId");

            migrationBuilder.CreateIndex(
                name: "IX_BacklogItems_RequirementId",
                table: "BacklogItems",
                column: "RequirementId");

            migrationBuilder.CreateIndex(
                name: "IX_BacklogItems_TargetSolutionId",
                table: "BacklogItems",
                column: "TargetSolutionId");

            migrationBuilder.CreateIndex(
                name: "IX_Decisions_BacklogItemId",
                table: "Decisions",
                column: "BacklogItemId");

            migrationBuilder.CreateIndex(
                name: "IX_Decisions_RequirementId",
                table: "Decisions",
                column: "RequirementId");

            migrationBuilder.CreateIndex(
                name: "IX_Decisions_TargetSolutionId",
                table: "Decisions",
                column: "TargetSolutionId");

            migrationBuilder.CreateIndex(
                name: "IX_Decisions_WorkflowRunId",
                table: "Decisions",
                column: "WorkflowRunId");

            migrationBuilder.CreateIndex(
                name: "IX_DesignReports_RequirementId",
                table: "DesignReports",
                column: "RequirementId");

            migrationBuilder.CreateIndex(
                name: "IX_DesignReports_WorkflowRunId",
                table: "DesignReports",
                column: "WorkflowRunId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ImplementationReports_BacklogItemId",
                table: "ImplementationReports",
                column: "BacklogItemId");

            migrationBuilder.CreateIndex(
                name: "IX_ImplementationReports_RequirementId",
                table: "ImplementationReports",
                column: "RequirementId");

            migrationBuilder.CreateIndex(
                name: "IX_ImplementationReports_WorkflowRunId",
                table: "ImplementationReports",
                column: "WorkflowRunId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OpenQuestions_BacklogItemId",
                table: "OpenQuestions",
                column: "BacklogItemId");

            migrationBuilder.CreateIndex(
                name: "IX_OpenQuestions_RequirementId",
                table: "OpenQuestions",
                column: "RequirementId");

            migrationBuilder.CreateIndex(
                name: "IX_OpenQuestions_TargetSolutionId",
                table: "OpenQuestions",
                column: "TargetSolutionId");

            migrationBuilder.CreateIndex(
                name: "IX_OpenQuestions_WorkflowRunId",
                table: "OpenQuestions",
                column: "WorkflowRunId");

            migrationBuilder.CreateIndex(
                name: "IX_PlanReports_RequirementId",
                table: "PlanReports",
                column: "RequirementId");

            migrationBuilder.CreateIndex(
                name: "IX_PlanReports_WorkflowRunId",
                table: "PlanReports",
                column: "WorkflowRunId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Requirements_OriginatingBacklogItemId",
                table: "Requirements",
                column: "OriginatingBacklogItemId");

            migrationBuilder.CreateIndex(
                name: "IX_Requirements_ParentRequirementId",
                table: "Requirements",
                column: "ParentRequirementId");

            migrationBuilder.CreateIndex(
                name: "IX_Requirements_TargetSolutionId",
                table: "Requirements",
                column: "TargetSolutionId");

            migrationBuilder.CreateIndex(
                name: "IX_Requirements_WorkflowRunId",
                table: "Requirements",
                column: "WorkflowRunId");

            migrationBuilder.CreateIndex(
                name: "IX_Solutions_Name",
                table: "Solutions",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SolutionTargets_Code",
                table: "SolutionTargets",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SolutionTargets_SolutionId",
                table: "SolutionTargets",
                column: "SolutionId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowRuns_BacklogItemId",
                table: "WorkflowRuns",
                column: "BacklogItemId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowRuns_RequirementId",
                table: "WorkflowRuns",
                column: "RequirementId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowRuns_TargetSolutionId",
                table: "WorkflowRuns",
                column: "TargetSolutionId");

            migrationBuilder.AddForeignKey(
                name: "FK_BacklogItems_Requirements_RequirementId",
                table: "BacklogItems",
                column: "RequirementId",
                principalTable: "Requirements",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_BacklogItems_WorkflowRuns_PlanWorkflowRunId",
                table: "BacklogItems",
                column: "PlanWorkflowRunId",
                principalTable: "WorkflowRuns",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_Decisions_Requirements_RequirementId",
                table: "Decisions",
                column: "RequirementId",
                principalTable: "Requirements",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_Decisions_WorkflowRuns_WorkflowRunId",
                table: "Decisions",
                column: "WorkflowRunId",
                principalTable: "WorkflowRuns",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_DesignReports_Requirements_RequirementId",
                table: "DesignReports",
                column: "RequirementId",
                principalTable: "Requirements",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_DesignReports_WorkflowRuns_WorkflowRunId",
                table: "DesignReports",
                column: "WorkflowRunId",
                principalTable: "WorkflowRuns",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_ImplementationReports_Requirements_RequirementId",
                table: "ImplementationReports",
                column: "RequirementId",
                principalTable: "Requirements",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_ImplementationReports_WorkflowRuns_WorkflowRunId",
                table: "ImplementationReports",
                column: "WorkflowRunId",
                principalTable: "WorkflowRuns",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_OpenQuestions_Requirements_RequirementId",
                table: "OpenQuestions",
                column: "RequirementId",
                principalTable: "Requirements",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_OpenQuestions_WorkflowRuns_WorkflowRunId",
                table: "OpenQuestions",
                column: "WorkflowRunId",
                principalTable: "WorkflowRuns",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_PlanReports_Requirements_RequirementId",
                table: "PlanReports",
                column: "RequirementId",
                principalTable: "Requirements",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_PlanReports_WorkflowRuns_WorkflowRunId",
                table: "PlanReports",
                column: "WorkflowRunId",
                principalTable: "WorkflowRuns",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_Requirements_WorkflowRuns_WorkflowRunId",
                table: "Requirements",
                column: "WorkflowRunId",
                principalTable: "WorkflowRuns",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_BacklogItems_Requirements_RequirementId",
                table: "BacklogItems");

            migrationBuilder.DropForeignKey(
                name: "FK_WorkflowRuns_Requirements_RequirementId",
                table: "WorkflowRuns");

            migrationBuilder.DropForeignKey(
                name: "FK_BacklogItems_SolutionTargets_TargetSolutionId",
                table: "BacklogItems");

            migrationBuilder.DropForeignKey(
                name: "FK_WorkflowRuns_SolutionTargets_TargetSolutionId",
                table: "WorkflowRuns");

            migrationBuilder.DropForeignKey(
                name: "FK_BacklogItems_WorkflowRuns_PlanWorkflowRunId",
                table: "BacklogItems");

            migrationBuilder.DropTable(
                name: "AgentTaskRuns");

            migrationBuilder.DropTable(
                name: "AnalysisReports");

            migrationBuilder.DropTable(
                name: "Decisions");

            migrationBuilder.DropTable(
                name: "DesignReports");

            migrationBuilder.DropTable(
                name: "ImplementationReports");

            migrationBuilder.DropTable(
                name: "OpenQuestions");

            migrationBuilder.DropTable(
                name: "PlanReports");

            migrationBuilder.DropTable(
                name: "Requirements");

            migrationBuilder.DropTable(
                name: "SolutionTargets");

            migrationBuilder.DropTable(
                name: "Solutions");

            migrationBuilder.DropTable(
                name: "WorkflowRuns");

            migrationBuilder.DropTable(
                name: "BacklogItems");
        }
    }
}
