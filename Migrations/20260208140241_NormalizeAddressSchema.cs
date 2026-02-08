using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ServiceTaskPlanningSystem.Migrations
{
    /// <inheritdoc />
    public partial class NormalizeAddressSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_tasks_objects_object_id",
                table: "tasks");

            migrationBuilder.DropForeignKey(
                name: "fk_tasks_regions_region_id",
                table: "tasks");

            migrationBuilder.DropTable(
                name: "objects");

            migrationBuilder.DropIndex(
                name: "ix_tasks_object_id",
                table: "tasks");

            migrationBuilder.DropIndex(
                name: "ix_regions_name",
                table: "regions");

            migrationBuilder.DropIndex(
                name: "ix_addresses_full_address",
                table: "addresses");

            migrationBuilder.DropColumn(
                name: "workplace_latitude",
                table: "technicians");

            migrationBuilder.DropColumn(
                name: "workplace_longitude",
                table: "technicians");

            migrationBuilder.DropColumn(
                name: "location",
                table: "tasks");

            migrationBuilder.DropColumn(
                name: "object_id",
                table: "tasks");

            migrationBuilder.DropColumn(
                name: "city",
                table: "addresses");

            migrationBuilder.DropColumn(
                name: "country",
                table: "addresses");

            migrationBuilder.AlterColumn<long>(
                name: "region_id",
                table: "tasks",
                type: "bigint",
                nullable: false,
                defaultValue: 0L,
                oldClrType: typeof(long),
                oldType: "bigint",
                oldNullable: true);

            migrationBuilder.AddColumn<long>(
                name: "service_object_id",
                table: "tasks",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<string>(
                name: "country",
                table: "regions",
                type: "character varying(100)",
                maxLength: 100,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<decimal>(
                name: "latitude",
                table: "regions",
                type: "numeric(9,6)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "longitude",
                table: "regions",
                type: "numeric(9,6)",
                nullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "street",
                table: "addresses",
                type: "character varying(300)",
                maxLength: 300,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "character varying(200)",
                oldMaxLength: 200,
                oldNullable: true);

            migrationBuilder.AddColumn<long>(
                name: "region_id",
                table: "addresses",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.CreateTable(
                name: "service_objects",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    name = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    address_id = table.Column<long>(type: "bigint", nullable: false),
                    description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false, defaultValueSql: "NOW()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_service_objects", x => x.id);
                    table.ForeignKey(
                        name: "fk_service_objects_addresses_address_id",
                        column: x => x.address_id,
                        principalTable: "addresses",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_tasks_service_object_id_start_at",
                table: "tasks",
                columns: new[] { "service_object_id", "start_at" });

            migrationBuilder.CreateIndex(
                name: "ix_regions_name_country",
                table: "regions",
                columns: new[] { "name", "country" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_addresses_region_id_street",
                table: "addresses",
                columns: new[] { "region_id", "street" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_service_objects_address_id",
                table: "service_objects",
                column: "address_id");

            migrationBuilder.CreateIndex(
                name: "ix_service_objects_name_address_id",
                table: "service_objects",
                columns: new[] { "name", "address_id" });

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
                name: "fk_tasks_service_objects_service_object_id",
                table: "tasks",
                column: "service_object_id",
                principalTable: "service_objects",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_addresses_regions_region_id",
                table: "addresses");

            migrationBuilder.DropForeignKey(
                name: "fk_tasks_regions_region_id",
                table: "tasks");

            migrationBuilder.DropForeignKey(
                name: "fk_tasks_service_objects_service_object_id",
                table: "tasks");

            migrationBuilder.DropTable(
                name: "service_objects");

            migrationBuilder.DropIndex(
                name: "ix_tasks_service_object_id_start_at",
                table: "tasks");

            migrationBuilder.DropIndex(
                name: "ix_regions_name_country",
                table: "regions");

            migrationBuilder.DropIndex(
                name: "ix_addresses_region_id_street",
                table: "addresses");

            migrationBuilder.DropColumn(
                name: "service_object_id",
                table: "tasks");

            migrationBuilder.DropColumn(
                name: "country",
                table: "regions");

            migrationBuilder.DropColumn(
                name: "latitude",
                table: "regions");

            migrationBuilder.DropColumn(
                name: "longitude",
                table: "regions");

            migrationBuilder.DropColumn(
                name: "region_id",
                table: "addresses");

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

            migrationBuilder.AlterColumn<long>(
                name: "region_id",
                table: "tasks",
                type: "bigint",
                nullable: true,
                oldClrType: typeof(long),
                oldType: "bigint");

            migrationBuilder.AddColumn<string>(
                name: "location",
                table: "tasks",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<long>(
                name: "object_id",
                table: "tasks",
                type: "bigint",
                nullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "street",
                table: "addresses",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(300)",
                oldMaxLength: 300);

            migrationBuilder.AddColumn<string>(
                name: "city",
                table: "addresses",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "country",
                table: "addresses",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "objects",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    address_id = table.Column<long>(type: "bigint", nullable: true),
                    region_id = table.Column<long>(type: "bigint", nullable: true),
                    address = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false, defaultValueSql: "NOW()"),
                    description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    latitude = table.Column<decimal>(type: "numeric(9,6)", nullable: true),
                    longitude = table.Column<decimal>(type: "numeric(9,6)", nullable: true),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_objects", x => x.id);
                    table.ForeignKey(
                        name: "fk_objects_addresses_address_id",
                        column: x => x.address_id,
                        principalTable: "addresses",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
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
                name: "ix_regions_name",
                table: "regions",
                column: "name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_addresses_full_address",
                table: "addresses",
                column: "full_address",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_objects_address_id",
                table: "objects",
                column: "address_id");

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

            migrationBuilder.AddForeignKey(
                name: "fk_tasks_regions_region_id",
                table: "tasks",
                column: "region_id",
                principalTable: "regions",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }
    }
}
