using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ServiceTaskPlanningSystem.Migrations
{
    /// <inheritdoc />
    public partial class EnumMigrations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:Enum:load_state", "normal,load,conflict")
                .Annotation("Npgsql:Enum:plan_status", "draft,active,superseded")
                .Annotation("Npgsql:Enum:risk_level", "low,medium,high")
                .Annotation("Npgsql:Enum:task_priority", "low,medium,high")
                .Annotation("Npgsql:Enum:task_status", "canceled,completed,conflict,in_progress")
                .Annotation("Npgsql:Enum:task_status.schedule_task_status", "in_progress,completed,conflict,canceled")
                .Annotation("Npgsql:Enum:user_role", "manager,technician,anonymous")
                .Annotation("Npgsql:PostgresExtension:citext", ",,")
                .OldAnnotation("Npgsql:Enum:load_state", "normal,load,conflict")
                .OldAnnotation("Npgsql:Enum:plan_status", "draft,active,superseded")
                .OldAnnotation("Npgsql:Enum:risk_level", "low,medium,high")
                .OldAnnotation("Npgsql:Enum:task_priority", "low,medium,high")
                .OldAnnotation("Npgsql:Enum:task_status", "canceled,completed,conflict,in_progress")
                .OldAnnotation("Npgsql:Enum:task_status.schedule_task_status", "in_progress,completed,conflict,canceled")
                .OldAnnotation("Npgsql:Enum:user_role", "manager,technician")
                .OldAnnotation("Npgsql:PostgresExtension:citext", ",,");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:Enum:load_state", "normal,load,conflict")
                .Annotation("Npgsql:Enum:plan_status", "draft,active,superseded")
                .Annotation("Npgsql:Enum:risk_level", "low,medium,high")
                .Annotation("Npgsql:Enum:task_priority", "low,medium,high")
                .Annotation("Npgsql:Enum:task_status", "canceled,completed,conflict,in_progress")
                .Annotation("Npgsql:Enum:task_status.schedule_task_status", "in_progress,completed,conflict,canceled")
                .Annotation("Npgsql:Enum:user_role", "manager,technician")
                .Annotation("Npgsql:PostgresExtension:citext", ",,")
                .OldAnnotation("Npgsql:Enum:load_state", "normal,load,conflict")
                .OldAnnotation("Npgsql:Enum:plan_status", "draft,active,superseded")
                .OldAnnotation("Npgsql:Enum:risk_level", "low,medium,high")
                .OldAnnotation("Npgsql:Enum:task_priority", "low,medium,high")
                .OldAnnotation("Npgsql:Enum:task_status", "canceled,completed,conflict,in_progress")
                .OldAnnotation("Npgsql:Enum:task_status.schedule_task_status", "in_progress,completed,conflict,canceled")
                .OldAnnotation("Npgsql:Enum:user_role", "manager,technician,anonymous")
                .OldAnnotation("Npgsql:PostgresExtension:citext", ",,");
        }
    }
}
