using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MusicRecognition.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddDocumentPages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "PageCount",
                table: "Jobs",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "PageNumber",
                table: "Jobs",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<bool>(
                name: "PagesPrepared",
                table: "Jobs",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "ParentId",
                table: "Jobs",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Jobs_ParentId_PageNumber",
                table: "Jobs",
                columns: new[] { "ParentId", "PageNumber" },
                unique: true,
                filter: "\"ParentId\" IS NOT NULL");

            migrationBuilder.AddForeignKey(
                name: "FK_Jobs_Jobs_ParentId",
                table: "Jobs",
                column: "ParentId",
                principalTable: "Jobs",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Jobs_Jobs_ParentId",
                table: "Jobs");

            migrationBuilder.DropIndex(
                name: "IX_Jobs_ParentId_PageNumber",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "PageCount",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "PageNumber",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "PagesPrepared",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "ParentId",
                table: "Jobs");
        }
    }
}
