using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ServiceTaskPlanningSystem.Migrations
{
    /// <inheritdoc />
    public partial class NewEnumForTaskStatus : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_type t JOIN pg_namespace n ON n.oid = t.typnamespace WHERE t.typname = 'task_status') THEN
        UPDATE tasks SET status = 'in_progress' WHERE status = 'planned';

        CREATE TYPE task_status_new AS ENUM ('in_progress', 'completed', 'conflict', 'canceled');
        ALTER TABLE tasks ALTER COLUMN status DROP DEFAULT;
        ALTER TABLE tasks ALTER COLUMN status TYPE task_status_new USING status::text::task_status_new;
        ALTER TABLE tasks ALTER COLUMN status SET DEFAULT 'in_progress'::task_status_new;

        DROP TYPE task_status;
        ALTER TYPE task_status_new RENAME TO task_status;
    END IF;
END $$;
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_type t JOIN pg_namespace n ON n.oid = t.typnamespace WHERE t.typname = 'task_status') THEN
        UPDATE tasks SET status = 'planned' WHERE status = 'conflict';

        CREATE TYPE task_status_old AS ENUM ('planned', 'in_progress', 'completed', 'canceled');
        ALTER TABLE tasks ALTER COLUMN status DROP DEFAULT;
        ALTER TABLE tasks ALTER COLUMN status TYPE task_status_old USING status::text::task_status_old;
        ALTER TABLE tasks ALTER COLUMN status SET DEFAULT 'planned'::task_status_old;

        DROP TYPE task_status;
        ALTER TYPE task_status_old RENAME TO task_status;
    END IF;
END $$;
");
        }
    }
}
