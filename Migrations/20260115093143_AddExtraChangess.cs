using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ServiceTaskPlanningSystem.Migrations
{
    /// <inheritdoc />
    public partial class AddExtraChangess : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "travel_distances");

            migrationBuilder.DropColumn(
                name: "workplace_address",
                table: "technicians");

            migrationBuilder.AddColumn<long>(
                name: "workplace_address_id",
                table: "technicians",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "address_id",
                table: "objects",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "addresses",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    full_address = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    street = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    city = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    country = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    latitude = table.Column<decimal>(type: "numeric(9,6)", nullable: true),
                    longitude = table.Column<decimal>(type: "numeric(9,6)", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false, defaultValueSql: "NOW()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_addresses", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "travel_routes",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    from_address_id = table.Column<long>(type: "bigint", nullable: false),
                    to_address_id = table.Column<long>(type: "bigint", nullable: false),
                    distance_km = table.Column<decimal>(type: "numeric(8,2)", nullable: false),
                    duration_minutes = table.Column<int>(type: "integer", nullable: false),
                    calculated_at = table.Column<DateTime>(type: "timestamptz", nullable: false, defaultValueSql: "NOW()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_travel_routes", x => x.id);
                    table.ForeignKey(
                        name: "fk_travel_routes_addresses_from_address_id",
                        column: x => x.from_address_id,
                        principalTable: "addresses",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_travel_routes_addresses_to_address_id",
                        column: x => x.to_address_id,
                        principalTable: "addresses",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_technicians_workplace_address_id",
                table: "technicians",
                column: "workplace_address_id");

            migrationBuilder.CreateIndex(
                name: "ix_objects_address_id",
                table: "objects",
                column: "address_id");

            migrationBuilder.CreateIndex(
                name: "ix_addresses_full_address",
                table: "addresses",
                column: "full_address",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_travel_routes_from_address_id_to_address_id",
                table: "travel_routes",
                columns: new[] { "from_address_id", "to_address_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_travel_routes_to_address_id",
                table: "travel_routes",
                column: "to_address_id");

            migrationBuilder.AddForeignKey(
                name: "fk_objects_addresses_address_id",
                table: "objects",
                column: "address_id",
                principalTable: "addresses",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "fk_technicians_addresses_workplace_address_id",
                table: "technicians",
                column: "workplace_address_id",
                principalTable: "addresses",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_objects_addresses_address_id",
                table: "objects");

            migrationBuilder.DropForeignKey(
                name: "fk_technicians_addresses_workplace_address_id",
                table: "technicians");

            migrationBuilder.DropTable(
                name: "travel_routes");

            migrationBuilder.DropTable(
                name: "addresses");

            migrationBuilder.DropIndex(
                name: "ix_technicians_workplace_address_id",
                table: "technicians");

            migrationBuilder.DropIndex(
                name: "ix_objects_address_id",
                table: "objects");

            migrationBuilder.DropColumn(
                name: "workplace_address_id",
                table: "technicians");

            migrationBuilder.DropColumn(
                name: "address_id",
                table: "objects");

            migrationBuilder.AddColumn<string>(
                name: "workplace_address",
                table: "technicians",
                type: "character varying(500)",
                maxLength: 500,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "travel_distances",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    calculated_at = table.Column<DateTime>(type: "timestamptz", nullable: false, defaultValueSql: "NOW()"),
                    distance_km = table.Column<decimal>(type: "numeric(8,2)", nullable: false),
                    duration_minutes = table.Column<int>(type: "integer", nullable: false),
                    from_address = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    to_address = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_travel_distances", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_travel_distances_from_address_to_address",
                table: "travel_distances",
                columns: new[] { "from_address", "to_address" },
                unique: true);
        }
    }
}
