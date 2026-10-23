using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MusicRecognition.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPreprocessing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Preprocessing",
                table: "Jobs",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Preprocessing",
                table: "Jobs");
        }
    }
}
