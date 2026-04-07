using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ServiceTaskPlanningSystem.Migrations
{
    public partial class AddRepairSnapshotToTasks : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                ALTER TABLE public.tasks
                    ADD COLUMN IF NOT EXISTS source_unavailability_id bigint NULL,
                    ADD COLUMN IF NOT EXISTS original_start_at        timestamptz NULL,
                    ADD COLUMN IF NOT EXISTS original_end_at          timestamptz NULL;

                CREATE INDEX IF NOT EXISTS ix_tasks_source_unavailability_id
                    ON public.tasks (source_unavailability_id)
                    WHERE source_unavailability_id IS NOT NULL;
                """);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DROP INDEX IF EXISTS public.ix_tasks_source_unavailability_id;

                ALTER TABLE public.tasks
                    DROP COLUMN IF EXISTS original_end_at,
                    DROP COLUMN IF EXISTS original_start_at,
                    DROP COLUMN IF EXISTS source_unavailability_id;
                """);
        }
    }
}
