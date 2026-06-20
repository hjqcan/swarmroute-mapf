using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SwarmRoute.Map.Infra.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSiteRole : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SiteRole",
                table: "RoadmapSites",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "Transit");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SiteRole",
                table: "RoadmapSites");
        }
    }
}
