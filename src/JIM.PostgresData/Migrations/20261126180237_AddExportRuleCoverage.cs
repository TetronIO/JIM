using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <inheritdoc />
    public partial class AddExportRuleCoverage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "CoverageRecordedAt",
                table: "SyncRules",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ExportRuleCoverages",
                columns: table => new
                {
                    ConnectedSystemObjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    SyncRuleId = table.Column<int>(type: "integer", nullable: false),
                    Recorded = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExportRuleCoverages", x => new { x.ConnectedSystemObjectId, x.SyncRuleId });
                    table.ForeignKey(
                        name: "FK_ExportRuleCoverages_ConnectedSystemObjects_ConnectedSystemO~",
                        column: x => x.ConnectedSystemObjectId,
                        principalTable: "ConnectedSystemObjects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ExportRuleCoverages_SyncRules_SyncRuleId",
                        column: x => x.SyncRuleId,
                        principalTable: "SyncRules",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ExportRuleCoverages_SyncRuleId",
                table: "ExportRuleCoverages",
                column: "SyncRuleId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ExportRuleCoverages");

            migrationBuilder.DropColumn(
                name: "CoverageRecordedAt",
                table: "SyncRules");
        }
    }
}
