using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SwarmRoute.Map.Infra.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Roadmaps",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    StateVersion = table.Column<long>(type: "bigint", nullable: false),
                    StateChangedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Roadmaps", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RoadmapBlocks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BlockId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    MinPos = table.Column<string>(type: "jsonb", nullable: false),
                    MaxPos = table.Column<string>(type: "jsonb", nullable: false),
                    ContainedSiteIds = table.Column<string>(type: "jsonb", nullable: false),
                    ContainedLineIds = table.Column<string>(type: "jsonb", nullable: false),
                    RoadmapId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RoadmapBlocks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RoadmapBlocks_Roadmaps_RoadmapId",
                        column: x => x.RoadmapId,
                        principalTable: "Roadmaps",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RoadmapLines",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    LineId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    StartStationId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    EndStationId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Distance = table.Column<double>(type: "double precision", nullable: false),
                    LineType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ControlPos1 = table.Column<string>(type: "jsonb", nullable: true),
                    ControlPos2 = table.Column<string>(type: "jsonb", nullable: true),
                    InterferenceSiteIds = table.Column<string>(type: "jsonb", nullable: false),
                    InterferenceLineIds = table.Column<string>(type: "jsonb", nullable: false),
                    RoadmapId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RoadmapLines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RoadmapLines_Roadmaps_RoadmapId",
                        column: x => x.RoadmapId,
                        principalTable: "Roadmaps",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RoadmapSites",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SiteId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    SiteType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Pos = table.Column<string>(type: "jsonb", nullable: false),
                    Enable = table.Column<bool>(type: "boolean", nullable: false),
                    InterferenceSiteIds = table.Column<string>(type: "jsonb", nullable: false),
                    InterferenceLineIds = table.Column<string>(type: "jsonb", nullable: false),
                    RoadmapId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RoadmapSites", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RoadmapSites_Roadmaps_RoadmapId",
                        column: x => x.RoadmapId,
                        principalTable: "Roadmaps",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RoadmapBlocks_RoadmapId_BlockId",
                table: "RoadmapBlocks",
                columns: new[] { "RoadmapId", "BlockId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RoadmapLines_RoadmapId_LineId",
                table: "RoadmapLines",
                columns: new[] { "RoadmapId", "LineId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Roadmaps_Name",
                table: "Roadmaps",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RoadmapSites_RoadmapId_SiteId",
                table: "RoadmapSites",
                columns: new[] { "RoadmapId", "SiteId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RoadmapBlocks");

            migrationBuilder.DropTable(
                name: "RoadmapLines");

            migrationBuilder.DropTable(
                name: "RoadmapSites");

            migrationBuilder.DropTable(
                name: "Roadmaps");
        }
    }
}
