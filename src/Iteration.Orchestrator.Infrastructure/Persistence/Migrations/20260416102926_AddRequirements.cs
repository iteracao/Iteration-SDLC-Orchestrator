using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Iteration.Orchestrator.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRequirements : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "RequirementId",
                table: "OpenQuestions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "RequirementId",
                table: "Decisions",
                type: "TEXT",
                nullable: true);

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
                    table.ForeignKey(
                        name: "FK_Requirements_WorkflowRuns_WorkflowRunId",
                        column: x => x.WorkflowRunId,
                        principalTable: "WorkflowRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OpenQuestions_RequirementId",
                table: "OpenQuestions",
                column: "RequirementId");

            migrationBuilder.CreateIndex(
                name: "IX_Decisions_RequirementId",
                table: "Decisions",
                column: "RequirementId");

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

            migrationBuilder.AddForeignKey(
                name: "FK_Decisions_Requirements_RequirementId",
                table: "Decisions",
                column: "RequirementId",
                principalTable: "Requirements",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_OpenQuestions_Requirements_RequirementId",
                table: "OpenQuestions",
                column: "RequirementId",
                principalTable: "Requirements",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Decisions_Requirements_RequirementId",
                table: "Decisions");

            migrationBuilder.DropForeignKey(
                name: "FK_OpenQuestions_Requirements_RequirementId",
                table: "OpenQuestions");

            migrationBuilder.DropTable(
                name: "Requirements");

            migrationBuilder.DropIndex(
                name: "IX_OpenQuestions_RequirementId",
                table: "OpenQuestions");

            migrationBuilder.DropIndex(
                name: "IX_Decisions_RequirementId",
                table: "Decisions");

            migrationBuilder.DropColumn(
                name: "RequirementId",
                table: "OpenQuestions");

            migrationBuilder.DropColumn(
                name: "RequirementId",
                table: "Decisions");
        }
    }
}
