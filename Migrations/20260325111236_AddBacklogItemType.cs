using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ServiceTaskPlanningSystem.Migrations
{
    /// <inheritdoc />
    public partial class AddBacklogItemType : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "item_type",
                table: "backlog_items",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "item_type",
                table: "backlog_items");
        }
    }
}
