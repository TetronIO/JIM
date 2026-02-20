using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <inheritdoc />
    public partial class AddAuditFieldsToPredefinedSearch : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "CreatedById",
                table: "PredefinedSearches",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CreatedByName",
                table: "PredefinedSearches",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CreatedByType",
                table: "PredefinedSearches",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastUpdated",
                table: "PredefinedSearches",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "LastUpdatedById",
                table: "PredefinedSearches",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastUpdatedByName",
                table: "PredefinedSearches",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "LastUpdatedByType",
                table: "PredefinedSearches",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CreatedById",
                table: "PredefinedSearches");

            migrationBuilder.DropColumn(
                name: "CreatedByName",
                table: "PredefinedSearches");

            migrationBuilder.DropColumn(
                name: "CreatedByType",
                table: "PredefinedSearches");

            migrationBuilder.DropColumn(
                name: "LastUpdated",
                table: "PredefinedSearches");

            migrationBuilder.DropColumn(
                name: "LastUpdatedById",
                table: "PredefinedSearches");

            migrationBuilder.DropColumn(
                name: "LastUpdatedByName",
                table: "PredefinedSearches");

            migrationBuilder.DropColumn(
                name: "LastUpdatedByType",
                table: "PredefinedSearches");
        }
    }
}
