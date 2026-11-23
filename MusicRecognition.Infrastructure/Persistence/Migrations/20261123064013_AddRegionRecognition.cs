using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MusicRecognition.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddRegionRecognition : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "RegionBaseRevision",
                table: "Jobs",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "RegionHeight",
                table: "Jobs",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "RegionWidth",
                table: "Jobs",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "RegionX",
                table: "Jobs",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "RegionY",
                table: "Jobs",
                type: "double precision",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RegionBaseRevision",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "RegionHeight",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "RegionWidth",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "RegionX",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "RegionY",
                table: "Jobs");
        }
    }
}
