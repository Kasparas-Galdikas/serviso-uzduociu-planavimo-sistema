using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ServiceTaskPlanningSystem.Migrations
{
    /// <inheritdoc />
    public partial class AddTaskInProgressEndAtIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_tasks_inprogress_end_at",
                table: "tasks",
                column: "end_at",
                filter: "status = 'in_progress'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_tasks_inprogress_end_at",
                table: "tasks");
        }
    }
}
