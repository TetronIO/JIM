using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    public partial class SyncRunObjPe : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "PendingExportId",
                table: "SyncRunObject",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "PendingExport",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ConnectedSystemObjectId = table.Column<int>(type: "integer", nullable: false),
                    ChangeType = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    ErrorCount = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PendingExport", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PendingExport_ConnectedSystemObjects_ConnectedSystemObjectId",
                        column: x => x.ConnectedSystemObjectId,
                        principalTable: "ConnectedSystemObjects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PendingExportAttributeValueChange",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AttributeId = table.Column<int>(type: "integer", nullable: false),
                    StringValue = table.Column<string>(type: "text", nullable: true),
                    DateTimeValue = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IntValue = table.Column<int>(type: "integer", nullable: true),
                    ByteValue = table.Column<byte[]>(type: "bytea", nullable: true),
                    ChangeType = table.Column<int>(type: "integer", nullable: false),
                    ErrorCount = table.Column<int>(type: "integer", nullable: true),
                    PendingExportId = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PendingExportAttributeValueChange", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PendingExportAttributeValueChange_ConnectedSystemAttributes~",
                        column: x => x.AttributeId,
                        principalTable: "ConnectedSystemAttributes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PendingExportAttributeValueChange_PendingExport_PendingExpo~",
                        column: x => x.PendingExportId,
                        principalTable: "PendingExport",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_SyncRunObject_PendingExportId",
                table: "SyncRunObject",
                column: "PendingExportId");

            migrationBuilder.CreateIndex(
                name: "IX_PendingExport_ConnectedSystemObjectId",
                table: "PendingExport",
                column: "ConnectedSystemObjectId");

            migrationBuilder.CreateIndex(
                name: "IX_PendingExportAttributeValueChange_AttributeId",
                table: "PendingExportAttributeValueChange",
                column: "AttributeId");

            migrationBuilder.CreateIndex(
                name: "IX_PendingExportAttributeValueChange_PendingExportId",
                table: "PendingExportAttributeValueChange",
                column: "PendingExportId");

            migrationBuilder.AddForeignKey(
                name: "FK_SyncRunObject_PendingExport_PendingExportId",
                table: "SyncRunObject",
                column: "PendingExportId",
                principalTable: "PendingExport",
                principalColumn: "Id");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_SyncRunObject_PendingExport_PendingExportId",
                table: "SyncRunObject");

            migrationBuilder.DropTable(
                name: "PendingExportAttributeValueChange");

            migrationBuilder.DropTable(
                name: "PendingExport");

            migrationBuilder.DropIndex(
                name: "IX_SyncRunObject_PendingExportId",
                table: "SyncRunObject");

            migrationBuilder.DropColumn(
                name: "PendingExportId",
                table: "SyncRunObject");
        }
    }
}
