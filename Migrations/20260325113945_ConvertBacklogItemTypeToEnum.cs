using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ServiceTaskPlanningSystem.Migrations
{
    /// <inheritdoc />
    public partial class ConvertBacklogItemTypeToEnum : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Create the PostgreSQL enum type
            migrationBuilder.Sql("CREATE TYPE backlog_item_type AS ENUM ('temporary', 'permanent');");

            // Add new enum column alongside the existing integer column
            migrationBuilder.Sql(@"
                ALTER TABLE backlog_items
                ADD COLUMN item_type_new backlog_item_type NOT NULL DEFAULT 'temporary';
            ");

            // Migrate data: 0 → temporary, 1 → permanent
            migrationBuilder.Sql(@"
                UPDATE backlog_items SET item_type_new = CASE
                    WHEN item_type = 1 THEN 'permanent'::backlog_item_type
                    ELSE 'temporary'::backlog_item_type
                END;
            ");

            // Swap: drop integer column, rename enum column
            migrationBuilder.Sql("ALTER TABLE backlog_items DROP COLUMN item_type;");
            migrationBuilder.Sql("ALTER TABLE backlog_items RENAME COLUMN item_type_new TO item_type;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Add integer column alongside the enum column
            migrationBuilder.Sql(@"
                ALTER TABLE backlog_items
                ADD COLUMN item_type_old integer NOT NULL DEFAULT 0;
            ");

            // Migrate data back: temporary → 0, permanent → 1
            migrationBuilder.Sql(@"
                UPDATE backlog_items SET item_type_old = CASE
                    WHEN item_type = 'permanent' THEN 1
                    ELSE 0
                END;
            ");

            // Swap: drop enum column, rename integer column
            migrationBuilder.Sql("ALTER TABLE backlog_items DROP COLUMN item_type;");
            migrationBuilder.Sql("ALTER TABLE backlog_items RENAME COLUMN item_type_old TO item_type;");

            // Drop the enum type
            migrationBuilder.Sql("DROP TYPE backlog_item_type;");
        }
    }
}
