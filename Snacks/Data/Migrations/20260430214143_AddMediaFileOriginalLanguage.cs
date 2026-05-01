using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Snacks.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddMediaFileOriginalLanguage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "OriginalLanguage",
                table: "MediaFiles",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "OriginalLanguage",
                table: "MediaFiles");
        }
    }
}
