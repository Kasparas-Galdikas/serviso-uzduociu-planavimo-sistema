using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;
using ServiceTaskPlanningSystem.Data;

#nullable disable

namespace ServiceTaskPlanningSystem.Migrations
{
    /// <inheritdoc />
    public partial class AddScheduleSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:Enum:load_state", "normal,load,conflict")
                .Annotation("Npgsql:Enum:plan_status", "draft,preview,active,superseded")
                .Annotation("Npgsql:Enum:risk_level", "low,medium,high")
                .Annotation("Npgsql:Enum:task_priority", "low,medium,high")
                .Annotation("Npgsql:Enum:task_status", "canceled,completed,in_progress,planned")
                .Annotation("Npgsql:Enum:task_status.schedule_task_status", "planned,in_progress,completed,canceled")
                .Annotation("Npgsql:Enum:user_role", "manager,technician")
                .Annotation("Npgsql:PostgresExtension:citext", ",,");

            migrationBuilder.CreateTable(
                name: "regions",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    name = table.Column<string>(type: "text", nullable: false),
                    priority_weight = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false, defaultValueSql: "NOW()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_regions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "users",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    email = table.Column<string>(type: "citext", nullable: false),
                    role = table.Column<UserRole>(type: "user_role", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false, defaultValueSql: "NOW()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_users", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "plans",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    period_start = table.Column<DateTime>(type: "date", nullable: false),
                    period_end = table.Column<DateTime>(type: "date", nullable: false),
                    weeks = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    created_by = table.Column<long>(type: "bigint", nullable: true),
                    status = table.Column<PlanStatus>(type: "plan_status", nullable: false),
                    superseded_by_plan_id = table.Column<long>(type: "bigint", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false, defaultValueSql: "NOW()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_plans", x => x.id);
                    table.CheckConstraint("ck_plans_period", "period_end >= period_start");
                    table.CheckConstraint("ck_plans_weeks_positive", "weeks > 0");
                    table.ForeignKey(
                        name: "fk_plans_plans_superseded_by_plan_id",
                        column: x => x.superseded_by_plan_id,
                        principalTable: "plans",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_plans_users_created_by",
                        column: x => x.created_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "technicians",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    user_id = table.Column<long>(type: "bigint", nullable: true),
                    name = table.Column<string>(type: "text", nullable: false),
                    region_id = table.Column<long>(type: "bigint", nullable: true),
                    daily_limit = table.Column<decimal>(type: "numeric(5,2)", nullable: false, defaultValue: 8m),
                    weekly_limit = table.Column<decimal>(type: "numeric(6,2)", nullable: false, defaultValue: 40m),
                    travel_switch_limit = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    rules_summary = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false, defaultValueSql: "NOW()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_technicians", x => x.id);
                    table.CheckConstraint("ck_technicians_daily_limit_positive", "daily_limit > 0");
                    table.CheckConstraint("ck_technicians_travel_switch_non_negative", "travel_switch_limit >= 0");
                    table.CheckConstraint("ck_technicians_weekly_limit_positive", "weekly_limit > 0");
                    table.ForeignKey(
                        name: "fk_technicians_regions_region_id",
                        column: x => x.region_id,
                        principalTable: "regions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_technicians_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "tasks",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    plan_id = table.Column<long>(type: "bigint", nullable: true),
                    technician_id = table.Column<long>(type: "bigint", nullable: false),
                    title = table.Column<string>(type: "text", nullable: false),
                    location = table.Column<string>(type: "text", nullable: false),
                    region_id = table.Column<long>(type: "bigint", nullable: true),
                    start_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    end_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    priority = table.Column<TaskPriority>(type: "task_priority", nullable: false, defaultValueSql: "'medium'::task_priority"),
                    status = table.Column<ScheduleTaskStatus>(type: "task_status", nullable: false, defaultValueSql: "'planned'::task_status"),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false, defaultValueSql: "NOW()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tasks", x => x.id);
                    table.CheckConstraint("ck_tasks_time_range", "end_at > start_at");
                    table.ForeignKey(
                        name: "fk_tasks_plans_plan_id",
                        column: x => x.plan_id,
                        principalTable: "plans",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_tasks_regions_region_id",
                        column: x => x.region_id,
                        principalTable: "regions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_tasks_technicians_technician_id",
                        column: x => x.technician_id,
                        principalTable: "technicians",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "task_evaluations",
                columns: table => new
                {
                    task_id = table.Column<long>(type: "bigint", nullable: false),
                    risk_level = table.Column<RiskLevel>(type: "risk_level", nullable: true),
                    is_conflict = table.Column<bool>(type: "boolean", nullable: false),
                    is_overloaded = table.Column<bool>(type: "boolean", nullable: false),
                    travel_required = table.Column<bool>(type: "boolean", nullable: false),
                    load_hours = table.Column<decimal>(type: "numeric", nullable: true),
                    evaluated_at = table.Column<DateTime>(type: "timestamptz", nullable: false, defaultValueSql: "NOW()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_task_evaluations", x => x.task_id);
                    table.ForeignKey(
                        name: "fk_task_evaluations_tasks_task_id",
                        column: x => x.task_id,
                        principalTable: "tasks",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_plans_created_by",
                table: "plans",
                column: "created_by");

            migrationBuilder.CreateIndex(
                name: "ix_plans_status",
                table: "plans",
                column: "status",
                unique: true,
                filter: "status = 'active'");

            migrationBuilder.CreateIndex(
                name: "ix_plans_superseded_by_plan_id",
                table: "plans",
                column: "superseded_by_plan_id");

            migrationBuilder.CreateIndex(
                name: "ix_regions_name",
                table: "regions",
                column: "name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_tasks_plan_id_start_at",
                table: "tasks",
                columns: new[] { "plan_id", "start_at" });

            migrationBuilder.CreateIndex(
                name: "ix_tasks_plan_id_technician_id_start_at",
                table: "tasks",
                columns: new[] { "plan_id", "technician_id", "start_at" });

            migrationBuilder.CreateIndex(
                name: "ix_tasks_region_id",
                table: "tasks",
                column: "region_id");

            migrationBuilder.CreateIndex(
                name: "ix_tasks_technician_id_start_at",
                table: "tasks",
                columns: new[] { "technician_id", "start_at" });

            migrationBuilder.CreateIndex(
                name: "ix_technicians_region_id",
                table: "technicians",
                column: "region_id");

            migrationBuilder.CreateIndex(
                name: "ix_technicians_user_id",
                table: "technicians",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_users_email",
                table: "users",
                column: "email",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "task_evaluations");

            migrationBuilder.DropTable(
                name: "tasks");

            migrationBuilder.DropTable(
                name: "plans");

            migrationBuilder.DropTable(
                name: "technicians");

            migrationBuilder.DropTable(
                name: "regions");

            migrationBuilder.DropTable(
                name: "users");
        }
    }
}
