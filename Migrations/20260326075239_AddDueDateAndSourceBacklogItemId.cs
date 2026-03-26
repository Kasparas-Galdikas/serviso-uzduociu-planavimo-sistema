using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ServiceTaskPlanningSystem.Migrations
{
    /// <inheritdoc />
    public partial class AddDueDateAndSourceBacklogItemId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "source_backlog_item_id",
                table: "tasks",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "due_date",
                table: "backlog_items",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "source_backlog_item_id",
                table: "tasks");

            migrationBuilder.DropColumn(
                name: "due_date",
                table: "backlog_items");
        }
    }
}
