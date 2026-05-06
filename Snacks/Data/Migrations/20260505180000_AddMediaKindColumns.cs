using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Snacks.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddMediaKindColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 0 == MediaKind.Video. Existing rows are all video files (only video
            // extensions were scanned before this migration), so the default backfills
            // them correctly without an explicit UPDATE.
            migrationBuilder.AddColumn<int>(
                name: "Kind",
                table: "MediaFiles",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Kind",
                table: "EncodeHistory",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Kind",
                table: "MediaFiles");

            migrationBuilder.DropColumn(
                name: "Kind",
                table: "EncodeHistory");
        }
    }
}
