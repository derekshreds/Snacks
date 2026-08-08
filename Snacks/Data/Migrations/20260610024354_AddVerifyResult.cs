using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Snacks.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddVerifyResult : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "LastVerifyResult",
                table: "MediaFiles",
                type: "TEXT",
                maxLength: 2048,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastVerifyResult",
                table: "MediaFiles");
        }
    }
}
