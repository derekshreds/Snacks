using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Snacks.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddQueuePriorityAndVerification : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "LastVerifiedAt",
                table: "MediaFiles",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Priority",
                table: "MediaFiles",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_MediaFiles_LastVerifiedAt",
                table: "MediaFiles",
                column: "LastVerifiedAt");

            migrationBuilder.CreateIndex(
                name: "IX_MediaFiles_Status_Priority_Bitrate",
                table: "MediaFiles",
                columns: new[] { "Status", "Priority", "Bitrate" },
                descending: new[] { false, true, true });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_MediaFiles_LastVerifiedAt",
                table: "MediaFiles");

            migrationBuilder.DropIndex(
                name: "IX_MediaFiles_Status_Priority_Bitrate",
                table: "MediaFiles");

            migrationBuilder.DropColumn(
                name: "LastVerifiedAt",
                table: "MediaFiles");

            migrationBuilder.DropColumn(
                name: "Priority",
                table: "MediaFiles");
        }
    }
}
