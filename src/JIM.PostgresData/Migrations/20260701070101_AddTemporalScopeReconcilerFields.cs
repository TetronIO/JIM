using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <inheritdoc />
    public partial class AddTemporalScopeReconcilerFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "BuiltIn",
                table: "Schedules",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastScopeEvaluatedAt",
                table: "MetaverseObjects",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "ScopeReviewPending",
                table: "MetaverseObjects",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastScopeEvaluatedAt",
                table: "ConnectedSystemObjects",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "ScopeReviewPending",
                table: "ConnectedSystemObjects",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_MetaverseObjectAttributeValues_AttributeId_DateTimeValue",
                table: "MetaverseObjectAttributeValues",
                columns: new[] { "AttributeId", "DateTimeValue" },
                filter: "\"DateTimeValue\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ConnectedSystemObjectAttributeValues_AttributeId_DateTimeValue",
                table: "ConnectedSystemObjectAttributeValues",
                columns: new[] { "AttributeId", "DateTimeValue" },
                filter: "\"DateTimeValue\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_MetaverseObjectAttributeValues_AttributeId_DateTimeValue",
                table: "MetaverseObjectAttributeValues");

            migrationBuilder.DropIndex(
                name: "IX_ConnectedSystemObjectAttributeValues_AttributeId_DateTimeValue",
                table: "ConnectedSystemObjectAttributeValues");

            migrationBuilder.DropColumn(
                name: "BuiltIn",
                table: "Schedules");

            migrationBuilder.DropColumn(
                name: "LastScopeEvaluatedAt",
                table: "MetaverseObjects");

            migrationBuilder.DropColumn(
                name: "ScopeReviewPending",
                table: "MetaverseObjects");

            migrationBuilder.DropColumn(
                name: "LastScopeEvaluatedAt",
                table: "ConnectedSystemObjects");

            migrationBuilder.DropColumn(
                name: "ScopeReviewPending",
                table: "ConnectedSystemObjects");
        }
    }
}
