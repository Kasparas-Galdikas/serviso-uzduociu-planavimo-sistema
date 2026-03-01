using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ServiceTaskPlanningSystem.Migrations
{
    /// <inheritdoc />
    public partial class AddTravelFeasibilityToEvaluation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "available_gap_minutes",
                table: "task_evaluations",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "is_travel_infeasible",
                table: "task_evaluations",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "required_travel_minutes",
                table: "task_evaluations",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "available_gap_minutes",
                table: "task_evaluations");

            migrationBuilder.DropColumn(
                name: "is_travel_infeasible",
                table: "task_evaluations");

            migrationBuilder.DropColumn(
                name: "required_travel_minutes",
                table: "task_evaluations");
        }
    }
}
