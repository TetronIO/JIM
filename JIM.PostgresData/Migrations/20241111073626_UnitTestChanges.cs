using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    public partial class UnitTestChanges : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Created",
                table: "SyncRules");

            migrationBuilder.DropColumn(
                name: "ErrorCount",
                table: "PendingExportAttributeValueChanges");

            migrationBuilder.AddColumn<string>(
                name: "UnresolvedReferenceValue",
                table: "PendingExportAttributeValueChanges",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "UnresolvedReferenceValueId",
                table: "MetaverseObjectAttributeValues",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "MetaverseObjectChangeId",
                table: "ActivityRunProfileExecutionItems",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Message",
                table: "Activities",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ObjectsProcessed",
                table: "Activities",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "ObjectsToProcess",
                table: "Activities",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_MetaverseObjectAttributeValues_UnresolvedReferenceValueId",
                table: "MetaverseObjectAttributeValues",
                column: "UnresolvedReferenceValueId");

            migrationBuilder.CreateIndex(
                name: "IX_ActivityRunProfileExecutionItems_MetaverseObjectChangeId",
                table: "ActivityRunProfileExecutionItems",
                column: "MetaverseObjectChangeId");

            migrationBuilder.AddForeignKey(
                name: "FK_ActivityRunProfileExecutionItems_MetaverseObjectChanges_Met~",
                table: "ActivityRunProfileExecutionItems",
                column: "MetaverseObjectChangeId",
                principalTable: "MetaverseObjectChanges",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_MetaverseObjectAttributeValues_ConnectedSystemObjects_Unres~",
                table: "MetaverseObjectAttributeValues",
                column: "UnresolvedReferenceValueId",
                principalTable: "ConnectedSystemObjects",
                principalColumn: "Id");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ActivityRunProfileExecutionItems_MetaverseObjectChanges_Met~",
                table: "ActivityRunProfileExecutionItems");

            migrationBuilder.DropForeignKey(
                name: "FK_MetaverseObjectAttributeValues_ConnectedSystemObjects_Unres~",
                table: "MetaverseObjectAttributeValues");

            migrationBuilder.DropIndex(
                name: "IX_MetaverseObjectAttributeValues_UnresolvedReferenceValueId",
                table: "MetaverseObjectAttributeValues");

            migrationBuilder.DropIndex(
                name: "IX_ActivityRunProfileExecutionItems_MetaverseObjectChangeId",
                table: "ActivityRunProfileExecutionItems");

            migrationBuilder.DropColumn(
                name: "UnresolvedReferenceValue",
                table: "PendingExportAttributeValueChanges");

            migrationBuilder.DropColumn(
                name: "UnresolvedReferenceValueId",
                table: "MetaverseObjectAttributeValues");

            migrationBuilder.DropColumn(
                name: "MetaverseObjectChangeId",
                table: "ActivityRunProfileExecutionItems");

            migrationBuilder.DropColumn(
                name: "Message",
                table: "Activities");

            migrationBuilder.DropColumn(
                name: "ObjectsProcessed",
                table: "Activities");

            migrationBuilder.DropColumn(
                name: "ObjectsToProcess",
                table: "Activities");

            migrationBuilder.AddColumn<DateTime>(
                name: "Created",
                table: "SyncRules",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<int>(
                name: "ErrorCount",
                table: "PendingExportAttributeValueChanges",
                type: "integer",
                nullable: true);
        }
    }
}
