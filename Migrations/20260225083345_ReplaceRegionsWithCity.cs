using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ServiceTaskPlanningSystem.Migrations
{
    /// <inheritdoc />
    public partial class ReplaceRegionsWithCity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"ALTER TABLE addresses DROP CONSTRAINT IF EXISTS fk_addresses_regions_region_id;");
            migrationBuilder.Sql(@"ALTER TABLE tasks DROP CONSTRAINT IF EXISTS fk_tasks_regions_region_id;");
            migrationBuilder.Sql(@"ALTER TABLE technicians DROP CONSTRAINT IF EXISTS fk_technicians_regions_region_id;");

            migrationBuilder.Sql(@"DROP INDEX IF EXISTS ix_technicians_region_id;");
            migrationBuilder.Sql(@"DROP INDEX IF EXISTS ix_tasks_region_id;");
            migrationBuilder.Sql(@"DROP INDEX IF EXISTS ix_addresses_region_id_street;");

            migrationBuilder.Sql(@"ALTER TABLE technicians DROP COLUMN IF EXISTS region_id;");
            migrationBuilder.Sql(@"ALTER TABLE tasks DROP COLUMN IF EXISTS region_id;");
            migrationBuilder.Sql(@"ALTER TABLE addresses DROP COLUMN IF EXISTS region_id;");

            migrationBuilder.Sql(
                @"ALTER TABLE addresses ADD COLUMN IF NOT EXISTS city character varying(150) NOT NULL DEFAULT '';");

            migrationBuilder.Sql(
                @"CREATE UNIQUE INDEX IF NOT EXISTS ix_addresses_city_street ON addresses (city, street);");

            migrationBuilder.Sql(@"DROP TABLE IF EXISTS regions;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_addresses_city_street",
                table: "addresses");

            migrationBuilder.DropColumn(
                name: "city",
                table: "addresses");

            migrationBuilder.AddColumn<long>(
                name: "region_id",
                table: "technicians",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "region_id",
                table: "tasks",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "region_id",
                table: "addresses",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.CreateTable(
                name: "regions",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    country = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false, defaultValueSql: "NOW()"),
                    latitude = table.Column<decimal>(type: "numeric(9,6)", nullable: true),
                    longitude = table.Column<decimal>(type: "numeric(9,6)", nullable: true),
                    name = table.Column<string>(type: "text", nullable: false),
                    priority_weight = table.Column<int>(type: "integer", nullable: false, defaultValue: 0)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_regions", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_technicians_region_id",
                table: "technicians",
                column: "region_id");

            migrationBuilder.CreateIndex(
                name: "ix_tasks_region_id",
                table: "tasks",
                column: "region_id");

            migrationBuilder.CreateIndex(
                name: "ix_addresses_region_id_street",
                table: "addresses",
                columns: new[] { "region_id", "street" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_regions_name_country",
                table: "regions",
                columns: new[] { "name", "country" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "fk_addresses_regions_region_id",
                table: "addresses",
                column: "region_id",
                principalTable: "regions",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_tasks_regions_region_id",
                table: "tasks",
                column: "region_id",
                principalTable: "regions",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_technicians_regions_region_id",
                table: "technicians",
                column: "region_id",
                principalTable: "regions",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }
    }
}
