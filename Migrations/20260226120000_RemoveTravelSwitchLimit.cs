using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ServiceTaskPlanningSystem.Migrations
{
    public partial class RemoveTravelSwitchLimit : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                @"ALTER TABLE technicians DROP CONSTRAINT IF EXISTS ck_technicians_travel_switch_non_negative;");

            migrationBuilder.Sql(
                @"ALTER TABLE technicians DROP COLUMN IF EXISTS travel_switch_limit;");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "travel_switch_limit",
                table: "technicians",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddCheckConstraint(
                name: "ck_technicians_travel_switch_non_negative",
                table: "technicians",
                sql: "travel_switch_limit >= 0");
        }
    }
}
