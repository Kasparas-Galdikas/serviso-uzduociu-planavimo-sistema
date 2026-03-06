using System;
using Microsoft.EntityFrameworkCore.Migrations;
using ServiceTaskPlanningSystem.Data;

#nullable disable

namespace ServiceTaskPlanningSystem.Migrations
{
    /// <inheritdoc />
    public partial class ConsolidateEvaluationSingleSource : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Use idempotent SQL so migration is safe against partially-aligned databases.
            migrationBuilder.Sql(@"ALTER TABLE tasks DROP CONSTRAINT IF EXISTS fk_tasks_tasks_previous_task_id;");
            migrationBuilder.Sql(@"DROP INDEX IF EXISTS ix_tasks_previous_task_id;");

            migrationBuilder.Sql(@"ALTER TABLE technicians DROP COLUMN IF EXISTS rules_summary;");
            migrationBuilder.Sql(@"ALTER TABLE tasks DROP COLUMN IF EXISTS previous_task_id;");

            migrationBuilder.Sql(@"ALTER TABLE task_evaluations DROP COLUMN IF EXISTS available_gap_minutes;");
            migrationBuilder.Sql(@"ALTER TABLE task_evaluations DROP COLUMN IF EXISTS conflict_reason;");
            migrationBuilder.Sql(@"ALTER TABLE task_evaluations DROP COLUMN IF EXISTS daily_hours;");
            migrationBuilder.Sql(@"ALTER TABLE task_evaluations DROP COLUMN IF EXISTS evaluated_at;");
            migrationBuilder.Sql(@"ALTER TABLE task_evaluations DROP COLUMN IF EXISTS has_conflict;");
            migrationBuilder.Sql(@"ALTER TABLE task_evaluations DROP COLUMN IF EXISTS is_conflict;");
            migrationBuilder.Sql(@"ALTER TABLE task_evaluations DROP COLUMN IF EXISTS is_overloaded;");
            migrationBuilder.Sql(@"ALTER TABLE task_evaluations DROP COLUMN IF EXISTS is_travel_infeasible;");
            migrationBuilder.Sql(@"ALTER TABLE task_evaluations DROP COLUMN IF EXISTS load_hours;");
            migrationBuilder.Sql(@"ALTER TABLE task_evaluations DROP COLUMN IF EXISTS required_travel_minutes;");
            migrationBuilder.Sql(@"ALTER TABLE task_evaluations DROP COLUMN IF EXISTS risk_level;");
            migrationBuilder.Sql(@"ALTER TABLE task_evaluations DROP COLUMN IF EXISTS switch_count;");
            migrationBuilder.Sql(@"ALTER TABLE task_evaluations DROP COLUMN IF EXISTS travel_required;");
            migrationBuilder.Sql(@"ALTER TABLE task_evaluations DROP COLUMN IF EXISTS weekly_hours;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "rules_summary",
                table: "technicians",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "previous_task_id",
                table: "tasks",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "available_gap_minutes",
                table: "task_evaluations",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "conflict_reason",
                table: "task_evaluations",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "daily_hours",
                table: "task_evaluations",
                type: "numeric",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "evaluated_at",
                table: "task_evaluations",
                type: "timestamptz",
                nullable: false,
                defaultValueSql: "NOW()");

            migrationBuilder.AddColumn<bool>(
                name: "has_conflict",
                table: "task_evaluations",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "is_conflict",
                table: "task_evaluations",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "is_overloaded",
                table: "task_evaluations",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "is_travel_infeasible",
                table: "task_evaluations",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<decimal>(
                name: "load_hours",
                table: "task_evaluations",
                type: "numeric",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "required_travel_minutes",
                table: "task_evaluations",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<RiskLevel>(
                name: "risk_level",
                table: "task_evaluations",
                type: "risk_level",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "switch_count",
                table: "task_evaluations",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "travel_required",
                table: "task_evaluations",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<decimal>(
                name: "weekly_hours",
                table: "task_evaluations",
                type: "numeric",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_tasks_previous_task_id",
                table: "tasks",
                column: "previous_task_id");

            migrationBuilder.AddForeignKey(
                name: "fk_tasks_tasks_previous_task_id",
                table: "tasks",
                column: "previous_task_id",
                principalTable: "tasks",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }
    }
}
