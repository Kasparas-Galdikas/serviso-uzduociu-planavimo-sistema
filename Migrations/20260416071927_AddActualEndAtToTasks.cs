using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ServiceTaskPlanningSystem.Migrations
{
    /// <inheritdoc />
    public partial class AddActualEndAtToTasks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Use raw SQL with IF NOT EXISTS so this migration is safe to run against a database
            // that already has the column (e.g., created manually or by an earlier deploy).
            migrationBuilder.Sql(
                "ALTER TABLE tasks ADD COLUMN IF NOT EXISTS actual_end_at timestamptz NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "ALTER TABLE tasks DROP COLUMN IF EXISTS actual_end_at;");
        }
    }
}
