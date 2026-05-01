using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Snacks.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddMediaFileIsHdr : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsHdr",
                table: "MediaFiles",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsHdr",
                table: "MediaFiles");
        }
    }
}
