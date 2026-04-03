using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ServiceTaskPlanningSystem.Migrations
{
    public partial class RepairBacklogUnavailabilityColumns : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                ALTER TABLE IF EXISTS public.backlog_items
                    ALTER COLUMN title TYPE character varying(200);

                ALTER TABLE IF EXISTS public.backlog_items
                    ADD COLUMN IF NOT EXISTS source_task_id bigint NULL;

                ALTER TABLE IF EXISTS public.backlog_items
                    ADD COLUMN IF NOT EXISTS source_unavailability_id bigint NULL;

                ALTER TABLE IF EXISTS public.backlog_items
                    ADD COLUMN IF NOT EXISTS original_technician_id bigint NULL;

                ALTER TABLE IF EXISTS public.backlog_items
                    ADD COLUMN IF NOT EXISTS original_start_at timestamptz NULL;

                ALTER TABLE IF EXISTS public.backlog_items
                    ADD COLUMN IF NOT EXISTS original_end_at timestamptz NULL;

                CREATE INDEX IF NOT EXISTS ix_backlog_items_source_unavailability_id
                    ON public.backlog_items (source_unavailability_id);
                """);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DROP INDEX IF EXISTS public.ix_backlog_items_source_unavailability_id;

                ALTER TABLE IF EXISTS public.backlog_items
                    DROP COLUMN IF EXISTS original_end_at;

                ALTER TABLE IF EXISTS public.backlog_items
                    DROP COLUMN IF EXISTS original_start_at;

                ALTER TABLE IF EXISTS public.backlog_items
                    DROP COLUMN IF EXISTS original_technician_id;

                ALTER TABLE IF EXISTS public.backlog_items
                    DROP COLUMN IF EXISTS source_unavailability_id;

                ALTER TABLE IF EXISTS public.backlog_items
                    DROP COLUMN IF EXISTS source_task_id;

                ALTER TABLE IF EXISTS public.backlog_items
                    ALTER COLUMN title TYPE character varying(20);
                """);
        }
    }
}
