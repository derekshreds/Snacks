using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Snacks.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAdvancedVideoHistoryLabels : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "AdvancedProfileId",
                table: "EncodeHistory",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AdvancedProfileName",
                table: "EncodeHistory",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AdvancedRuleName",
                table: "EncodeHistory",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AdvancedProfileId",
                table: "EncodeHistory");

            migrationBuilder.DropColumn(
                name: "AdvancedProfileName",
                table: "EncodeHistory");

            migrationBuilder.DropColumn(
                name: "AdvancedRuleName",
                table: "EncodeHistory");
        }
    }
}
