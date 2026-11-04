using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <inheritdoc />
    public partial class AddConnectedSystemPartitionWatermarks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ImportPartitionWatermarksFrom",
                table: "Activities",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ImportPartitionWatermarksTo",
                table: "Activities",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ConnectedSystemPartitionWatermarks",
                columns: table => new
                {
                    PartitionId = table.Column<int>(type: "integer", nullable: false),
                    ConnectedSystemId = table.Column<int>(type: "integer", nullable: false),
                    Value = table.Column<string>(type: "text", nullable: false),
                    RecordedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConnectedSystemPartitionWatermarks", x => x.PartitionId);
                    table.ForeignKey(
                        name: "FK_ConnectedSystemPartitionWatermarks_ConnectedSystemPartition~",
                        column: x => x.PartitionId,
                        principalTable: "ConnectedSystemPartitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ConnectedSystemPartitionWatermarks_ConnectedSystems_Connect~",
                        column: x => x.ConnectedSystemId,
                        principalTable: "ConnectedSystems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ConnectedSystemPartitionWatermarks_ConnectedSystemId",
                table: "ConnectedSystemPartitionWatermarks",
                column: "ConnectedSystemId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ConnectedSystemPartitionWatermarks");

            migrationBuilder.DropColumn(
                name: "ImportPartitionWatermarksFrom",
                table: "Activities");

            migrationBuilder.DropColumn(
                name: "ImportPartitionWatermarksTo",
                table: "Activities");
        }
    }
}
