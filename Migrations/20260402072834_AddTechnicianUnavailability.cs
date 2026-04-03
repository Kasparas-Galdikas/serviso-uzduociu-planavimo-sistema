using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ServiceTaskPlanningSystem.Migrations
{
    /// <inheritdoc />
    public partial class AddTechnicianUnavailability : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "title",
                table: "backlog_items",
                type: "character varying(200)",
                maxLength: 200,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(20)",
                oldMaxLength: 20);

            migrationBuilder.AddColumn<long>(
                name: "source_task_id",
                table: "backlog_items",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "original_end_at",
                table: "backlog_items",
                type: "timestamptz",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "original_technician_id",
                table: "backlog_items",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "original_start_at",
                table: "backlog_items",
                type: "timestamptz",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "source_unavailability_id",
                table: "backlog_items",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "technician_unavailabilities",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    technician_id = table.Column<long>(type: "bigint", nullable: false),
                    period_start = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    period_end = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false, defaultValueSql: "NOW()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_technician_unavailabilities", x => x.id);
                    table.CheckConstraint("ck_unavailability_period", "period_end >= period_start");
                    table.ForeignKey(
                        name: "fk_technician_unavailabilities_technicians_technician_id",
                        column: x => x.technician_id,
                        principalTable: "technicians",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_unavailability_tech_period",
                table: "technician_unavailabilities",
                columns: new[] { "technician_id", "period_start" });

            migrationBuilder.CreateIndex(
                name: "ix_backlog_items_source_unavailability_id",
                table: "backlog_items",
                column: "source_unavailability_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "technician_unavailabilities");

            migrationBuilder.DropIndex(
                name: "ix_backlog_items_source_unavailability_id",
                table: "backlog_items");

            migrationBuilder.DropColumn(
                name: "original_end_at",
                table: "backlog_items");

            migrationBuilder.DropColumn(
                name: "original_technician_id",
                table: "backlog_items");

            migrationBuilder.DropColumn(
                name: "original_start_at",
                table: "backlog_items");

            migrationBuilder.DropColumn(
                name: "source_task_id",
                table: "backlog_items");

            migrationBuilder.DropColumn(
                name: "source_unavailability_id",
                table: "backlog_items");

            migrationBuilder.AlterColumn<string>(
                name: "title",
                table: "backlog_items",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(200)",
                oldMaxLength: 200);
        }
    }
}
