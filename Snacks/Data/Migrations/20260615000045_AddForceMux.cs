using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Snacks.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddForceMux : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "ForceMux",
                table: "MediaFiles",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ForceMux",
                table: "MediaFiles");
        }
    }
}
