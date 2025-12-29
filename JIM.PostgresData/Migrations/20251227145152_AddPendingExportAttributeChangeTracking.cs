using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <inheritdoc />
    public partial class AddPendingExportAttributeChangeTracking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ExportAttemptCount",
                table: "PendingExportAttributeValueChanges",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastExportedAt",
                table: "PendingExportAttributeValueChanges",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastImportedValue",
                table: "PendingExportAttributeValueChanges",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Status",
                table: "PendingExportAttributeValueChanges",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ExportAttemptCount",
                table: "PendingExportAttributeValueChanges");

            migrationBuilder.DropColumn(
                name: "LastExportedAt",
                table: "PendingExportAttributeValueChanges");

            migrationBuilder.DropColumn(
                name: "LastImportedValue",
                table: "PendingExportAttributeValueChanges");

            migrationBuilder.DropColumn(
                name: "Status",
                table: "PendingExportAttributeValueChanges");
        }
    }
}
