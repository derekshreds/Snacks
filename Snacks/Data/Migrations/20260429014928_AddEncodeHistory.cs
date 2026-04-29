using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Snacks.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddEncodeHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "EncodeHistory",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    JobId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    FilePath = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    FileName = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    OriginalSizeBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    EncodedSizeBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    BytesSaved = table.Column<long>(type: "INTEGER", nullable: false),
                    OriginalCodec = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    EncodedCodec = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    OriginalBitrateKbps = table.Column<long>(type: "INTEGER", nullable: false),
                    EncodedBitrateKbps = table.Column<long>(type: "INTEGER", nullable: false),
                    DurationSeconds = table.Column<double>(type: "REAL", nullable: false),
                    EncodeSeconds = table.Column<double>(type: "REAL", nullable: false),
                    DeviceId = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    NodeId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    NodeHostname = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    WasRemote = table.Column<bool>(type: "INTEGER", nullable: false),
                    Is4K = table.Column<bool>(type: "INTEGER", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Outcome = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EncodeHistory", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EncodeHistory_CompletedAt",
                table: "EncodeHistory",
                column: "CompletedAt");

            migrationBuilder.CreateIndex(
                name: "IX_EncodeHistory_DeviceId_CompletedAt",
                table: "EncodeHistory",
                columns: new[] { "DeviceId", "CompletedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EncodeHistory");
        }
    }
}
