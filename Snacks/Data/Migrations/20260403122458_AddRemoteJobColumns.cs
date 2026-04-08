using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Snacks.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddRemoteJobColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AssignedNodeId",
                table: "MediaFiles",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AssignedNodeIp",
                table: "MediaFiles",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AssignedNodeName",
                table: "MediaFiles",
                type: "TEXT",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "AssignedNodePort",
                table: "MediaFiles",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RemoteFailureCount",
                table: "MediaFiles",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "RemoteJobPhase",
                table: "MediaFiles",
                type: "TEXT",
                maxLength: 32,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AssignedNodeId",
                table: "MediaFiles");

            migrationBuilder.DropColumn(
                name: "AssignedNodeIp",
                table: "MediaFiles");

            migrationBuilder.DropColumn(
                name: "AssignedNodeName",
                table: "MediaFiles");

            migrationBuilder.DropColumn(
                name: "AssignedNodePort",
                table: "MediaFiles");

            migrationBuilder.DropColumn(
                name: "RemoteFailureCount",
                table: "MediaFiles");

            migrationBuilder.DropColumn(
                name: "RemoteJobPhase",
                table: "MediaFiles");
        }
    }
}
