using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <inheritdoc />
    public partial class AddGeneratedValueDecisionDetail : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "NeedsDecisionReason",
                table: "GeneratedValueAssignments",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "RemediatedAt",
                table: "GeneratedValueAssignments",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_GeneratedValueAssignments_RemediatedAt",
                table: "GeneratedValueAssignments",
                column: "RemediatedAt",
                filter: "\"RemediatedAt\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_GeneratedValueAssignments_RenameAuthorised",
                table: "GeneratedValueAssignments",
                column: "RenameAuthorised",
                filter: "\"RenameAuthorised\"");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_GeneratedValueAssignments_RemediatedAt",
                table: "GeneratedValueAssignments");

            migrationBuilder.DropIndex(
                name: "IX_GeneratedValueAssignments_RenameAuthorised",
                table: "GeneratedValueAssignments");

            migrationBuilder.DropColumn(
                name: "NeedsDecisionReason",
                table: "GeneratedValueAssignments");

            migrationBuilder.DropColumn(
                name: "RemediatedAt",
                table: "GeneratedValueAssignments");
        }
    }
}
