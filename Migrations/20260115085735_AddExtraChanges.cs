using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ServiceTaskPlanningSystem.Migrations
{
    /// <inheritdoc />
    public partial class AddExtraChanges : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "workplace_address",
                table: "technicians",
                type: "character varying(500)",
                maxLength: 500,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<decimal>(
                name: "workplace_latitude",
                table: "technicians",
                type: "numeric(9,6)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "workplace_longitude",
                table: "technicians",
                type: "numeric(9,6)",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "previous_task_id",
                table: "tasks",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "travel_distance_km",
                table: "tasks",
                type: "numeric(8,2)",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "travel_duration_minutes",
                table: "tasks",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "latitude",
                table: "objects",
                type: "numeric(9,6)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "longitude",
                table: "objects",
                type: "numeric(9,6)",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "travel_distances",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    from_address = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    to_address = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    distance_km = table.Column<decimal>(type: "numeric(8,2)", nullable: false),
                    duration_minutes = table.Column<int>(type: "integer", nullable: false),
                    calculated_at = table.Column<DateTime>(type: "timestamptz", nullable: false, defaultValueSql: "NOW()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_travel_distances", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_tasks_previous_task_id",
                table: "tasks",
                column: "previous_task_id");

            migrationBuilder.CreateIndex(
                name: "ix_travel_distances_from_address_to_address",
                table: "travel_distances",
                columns: new[] { "from_address", "to_address" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "fk_tasks_tasks_previous_task_id",
                table: "tasks",
                column: "previous_task_id",
                principalTable: "tasks",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_tasks_tasks_previous_task_id",
                table: "tasks");

            migrationBuilder.DropTable(
                name: "travel_distances");

            migrationBuilder.DropIndex(
                name: "ix_tasks_previous_task_id",
                table: "tasks");

            migrationBuilder.DropColumn(
                name: "workplace_address",
                table: "technicians");

            migrationBuilder.DropColumn(
                name: "workplace_latitude",
                table: "technicians");

            migrationBuilder.DropColumn(
                name: "workplace_longitude",
                table: "technicians");

            migrationBuilder.DropColumn(
                name: "previous_task_id",
                table: "tasks");

            migrationBuilder.DropColumn(
                name: "travel_distance_km",
                table: "tasks");

            migrationBuilder.DropColumn(
                name: "travel_duration_minutes",
                table: "tasks");

            migrationBuilder.DropColumn(
                name: "latitude",
                table: "objects");

            migrationBuilder.DropColumn(
                name: "longitude",
                table: "objects");
        }
    }
}
