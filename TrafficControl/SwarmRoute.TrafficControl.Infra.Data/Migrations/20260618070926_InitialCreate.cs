using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace SwarmRoute.TrafficControl.Infra.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ReservationAudits",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ReservationTableId = table.Column<Guid>(type: "uuid", nullable: false),
                    StateVersion = table.Column<long>(type: "bigint", nullable: false),
                    AgentId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Action = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    LeaseCount = table.Column<int>(type: "integer", nullable: false),
                    LeasesJson = table.Column<string>(type: "jsonb", nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReservationAudits", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ReservationAudits_CreatedAtUtc",
                table: "ReservationAudits",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_ReservationAudits_ReservationTableId",
                table: "ReservationAudits",
                column: "ReservationTableId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ReservationAudits");
        }
    }
}
