using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ServiceTaskPlanningSystem.Migrations
{
    /// <inheritdoc />
    public partial class AddUserNamesAndDropTechnicianNames : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "name",
                table: "users",
                type: "text",
                nullable: false,
                defaultValue: string.Empty);

            migrationBuilder.Sql(@"
UPDATE users u
SET name = COALESCE(NULLIF(u.name, ''), t.name, u.email)
FROM technicians t
WHERE t.user_id = u.id;
");

            migrationBuilder.DropColumn(
                name: "name",
                table: "technicians");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "name",
                table: "technicians",
                type: "text",
                nullable: false,
                defaultValue: string.Empty);

            migrationBuilder.Sql(@"
UPDATE technicians t
SET name = COALESCE(NULLIF(t.name, ''), u.name, u.email)
FROM users u
WHERE t.user_id = u.id;
");

            migrationBuilder.DropColumn(
                name: "name",
                table: "users");
        }
    }
}
