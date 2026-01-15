using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ServiceTaskPlanningSystem.Migrations
{
    /// <inheritdoc />
    public partial class AddObjectsTableAndRemoveObjectFieldsFromTasks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "object_address",
                table: "tasks");

            migrationBuilder.DropColumn(
                name: "object_name",
                table: "tasks");

            migrationBuilder.AddColumn<long>(
                name: "object_id",
                table: "tasks",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "objects",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    address = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    region_id = table.Column<long>(type: "bigint", nullable: true),
                    description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false, defaultValueSql: "NOW()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_objects", x => x.id);
                    table.ForeignKey(
                        name: "fk_objects_regions_region_id",
                        column: x => x.region_id,
                        principalTable: "regions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "ix_tasks_object_id",
                table: "tasks",
                column: "object_id");

            migrationBuilder.CreateIndex(
                name: "ix_objects_name_address",
                table: "objects",
                columns: new[] { "name", "address" });

            migrationBuilder.CreateIndex(
                name: "ix_objects_region_id",
                table: "objects",
                column: "region_id");

            migrationBuilder.AddForeignKey(
                name: "fk_tasks_objects_object_id",
                table: "tasks",
                column: "object_id",
                principalTable: "objects",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_tasks_objects_object_id",
                table: "tasks");

            migrationBuilder.DropTable(
                name: "objects");

            migrationBuilder.DropIndex(
                name: "ix_tasks_object_id",
                table: "tasks");

            migrationBuilder.DropColumn(
                name: "object_id",
                table: "tasks");

            migrationBuilder.AddColumn<string>(
                name: "object_address",
                table: "tasks",
                type: "character varying(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "object_name",
                table: "tasks",
                type: "character varying(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: "");
        }
    }
}
