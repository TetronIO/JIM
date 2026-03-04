using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <inheritdoc />
    public partial class AddExportChangeHistoryAndDropDataSnapshot : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DataSnapshot",
                table: "ActivityRunProfileExecutionItems");

            migrationBuilder.AddColumn<Guid>(
                name: "ConnectedSystemObjectChangeId",
                table: "ActivityRunProfileExecutionItemSyncOutcomes",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ActivityRunProfileExecutionItemSyncOutcomes_ConnectedSystem~",
                table: "ActivityRunProfileExecutionItemSyncOutcomes",
                column: "ConnectedSystemObjectChangeId");

            migrationBuilder.AddForeignKey(
                name: "FK_SyncOutcomes_ConnectedSystemObjectChange",
                table: "ActivityRunProfileExecutionItemSyncOutcomes",
                column: "ConnectedSystemObjectChangeId",
                principalTable: "ConnectedSystemObjectChanges",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_SyncOutcomes_ConnectedSystemObjectChange",
                table: "ActivityRunProfileExecutionItemSyncOutcomes");

            migrationBuilder.DropIndex(
                name: "IX_ActivityRunProfileExecutionItemSyncOutcomes_ConnectedSystem~",
                table: "ActivityRunProfileExecutionItemSyncOutcomes");

            migrationBuilder.DropColumn(
                name: "ConnectedSystemObjectChangeId",
                table: "ActivityRunProfileExecutionItemSyncOutcomes");

            migrationBuilder.AddColumn<string>(
                name: "DataSnapshot",
                table: "ActivityRunProfileExecutionItems",
                type: "text",
                nullable: true);
        }
    }
}
