using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ServiceTaskPlanningSystem.Migrations
{
    /// <inheritdoc />
    public partial class AddSourceTaskIdToTasks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "source_task_id",
                table: "tasks",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_tasks_source_task_id",
                table: "tasks",
                column: "source_task_id",
                filter: "source_task_id IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_tasks_source_task_id",
                table: "tasks");

            migrationBuilder.DropColumn(
                name: "source_task_id",
                table: "tasks");
        }
    }
}
