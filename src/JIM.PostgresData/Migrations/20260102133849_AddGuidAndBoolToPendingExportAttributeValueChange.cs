using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <inheritdoc />
    public partial class AddGuidAndBoolToPendingExportAttributeValueChange : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "BoolValue",
                table: "PendingExportAttributeValueChanges",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "GuidValue",
                table: "PendingExportAttributeValueChanges",
                type: "uuid",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BoolValue",
                table: "PendingExportAttributeValueChanges");

            migrationBuilder.DropColumn(
                name: "GuidValue",
                table: "PendingExportAttributeValueChanges");
        }
    }
}
