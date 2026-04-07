using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ServiceTaskPlanningSystem.Migrations
{
    /// <inheritdoc />
    public partial class RemoveUnusedFieldsMetadataSync : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "correlation_id",
                table: "task_action_logs");

            migrationBuilder.DropColumn(
                name: "source_task_id",
                table: "backlog_items");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "correlation_id",
                table: "task_action_logs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "source_task_id",
                table: "backlog_items",
                type: "bigint",
                nullable: true);
        }
    }
}
