using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Iteration.Orchestrator.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSolutions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Decisions_SolutionTargets_TargetSolutionId",
                table: "Decisions");

            migrationBuilder.DropForeignKey(
                name: "FK_OpenQuestions_SolutionTargets_TargetSolutionId",
                table: "OpenQuestions");

            migrationBuilder.DropForeignKey(
                name: "FK_Requirements_SolutionTargets_TargetSolutionId",
                table: "Requirements");

            migrationBuilder.AddColumn<Guid>(
                name: "SolutionId",
                table: "SolutionTargets",
                type: "TEXT",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

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

            migrationBuilder.Sql("""
                INSERT INTO "Solutions" ("Id", "Name", "Description", "ProfileCode", "CreatedAtUtc")
                SELECT "Id", "Name", '', "ProfileCode", "CreatedUtc"
                FROM "SolutionTargets";
                """);

            migrationBuilder.Sql("""
                UPDATE "SolutionTargets"
                SET "SolutionId" = "Id"
                WHERE "SolutionId" = '00000000-0000-0000-0000-000000000000';
                """);

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowRuns_TargetSolutionId",
                table: "WorkflowRuns",
                column: "TargetSolutionId");

            migrationBuilder.CreateIndex(
                name: "IX_SolutionTargets_SolutionId",
                table: "SolutionTargets",
                column: "SolutionId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BacklogItems_TargetSolutionId",
                table: "BacklogItems",
                column: "TargetSolutionId");

            migrationBuilder.AddForeignKey(
                name: "FK_BacklogItems_Solutions_TargetSolutionId",
                table: "BacklogItems",
                column: "TargetSolutionId",
                principalTable: "Solutions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_Decisions_Solutions_TargetSolutionId",
                table: "Decisions",
                column: "TargetSolutionId",
                principalTable: "Solutions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_OpenQuestions_Solutions_TargetSolutionId",
                table: "OpenQuestions",
                column: "TargetSolutionId",
                principalTable: "Solutions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_Requirements_Solutions_TargetSolutionId",
                table: "Requirements",
                column: "TargetSolutionId",
                principalTable: "Solutions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_SolutionTargets_Solutions_SolutionId",
                table: "SolutionTargets",
                column: "SolutionId",
                principalTable: "Solutions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_WorkflowRuns_Solutions_TargetSolutionId",
                table: "WorkflowRuns",
                column: "TargetSolutionId",
                principalTable: "Solutions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_BacklogItems_Solutions_TargetSolutionId",
                table: "BacklogItems");

            migrationBuilder.DropForeignKey(
                name: "FK_Decisions_Solutions_TargetSolutionId",
                table: "Decisions");

            migrationBuilder.DropForeignKey(
                name: "FK_OpenQuestions_Solutions_TargetSolutionId",
                table: "OpenQuestions");

            migrationBuilder.DropForeignKey(
                name: "FK_Requirements_Solutions_TargetSolutionId",
                table: "Requirements");

            migrationBuilder.DropForeignKey(
                name: "FK_SolutionTargets_Solutions_SolutionId",
                table: "SolutionTargets");

            migrationBuilder.DropForeignKey(
                name: "FK_WorkflowRuns_Solutions_TargetSolutionId",
                table: "WorkflowRuns");

            migrationBuilder.DropTable(
                name: "Solutions");

            migrationBuilder.DropIndex(
                name: "IX_WorkflowRuns_TargetSolutionId",
                table: "WorkflowRuns");

            migrationBuilder.DropIndex(
                name: "IX_SolutionTargets_SolutionId",
                table: "SolutionTargets");

            migrationBuilder.DropIndex(
                name: "IX_BacklogItems_TargetSolutionId",
                table: "BacklogItems");

            migrationBuilder.DropColumn(
                name: "SolutionId",
                table: "SolutionTargets");

            migrationBuilder.AddForeignKey(
                name: "FK_Decisions_SolutionTargets_TargetSolutionId",
                table: "Decisions",
                column: "TargetSolutionId",
                principalTable: "SolutionTargets",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_OpenQuestions_SolutionTargets_TargetSolutionId",
                table: "OpenQuestions",
                column: "TargetSolutionId",
                principalTable: "SolutionTargets",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_Requirements_SolutionTargets_TargetSolutionId",
                table: "Requirements",
                column: "TargetSolutionId",
                principalTable: "SolutionTargets",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
