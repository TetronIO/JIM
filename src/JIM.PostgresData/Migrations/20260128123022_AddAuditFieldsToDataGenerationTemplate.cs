using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <inheritdoc />
    public partial class AddAuditFieldsToDataGenerationTemplate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "CreatedById",
                table: "DataGenerationTemplates",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CreatedByName",
                table: "DataGenerationTemplates",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CreatedByType",
                table: "DataGenerationTemplates",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastUpdated",
                table: "DataGenerationTemplates",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "LastUpdatedById",
                table: "DataGenerationTemplates",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastUpdatedByName",
                table: "DataGenerationTemplates",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "LastUpdatedByType",
                table: "DataGenerationTemplates",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CreatedById",
                table: "DataGenerationTemplates");

            migrationBuilder.DropColumn(
                name: "CreatedByName",
                table: "DataGenerationTemplates");

            migrationBuilder.DropColumn(
                name: "CreatedByType",
                table: "DataGenerationTemplates");

            migrationBuilder.DropColumn(
                name: "LastUpdated",
                table: "DataGenerationTemplates");

            migrationBuilder.DropColumn(
                name: "LastUpdatedById",
                table: "DataGenerationTemplates");

            migrationBuilder.DropColumn(
                name: "LastUpdatedByName",
                table: "DataGenerationTemplates");

            migrationBuilder.DropColumn(
                name: "LastUpdatedByType",
                table: "DataGenerationTemplates");
        }
    }
}
