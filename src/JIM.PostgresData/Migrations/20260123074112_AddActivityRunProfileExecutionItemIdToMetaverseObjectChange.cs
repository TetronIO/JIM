using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <inheritdoc />
    public partial class AddActivityRunProfileExecutionItemIdToMetaverseObjectChange : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ActivityRunProfileExecutionItems_MetaverseObjectChanges_Met~",
                table: "ActivityRunProfileExecutionItems");

            migrationBuilder.DropIndex(
                name: "IX_ActivityRunProfileExecutionItems_MetaverseObjectChangeId",
                table: "ActivityRunProfileExecutionItems");

            migrationBuilder.DropColumn(
                name: "MetaverseObjectChangeId",
                table: "ActivityRunProfileExecutionItems");

            migrationBuilder.AddColumn<Guid>(
                name: "ActivityRunProfileExecutionItemId",
                table: "MetaverseObjectChanges",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_MetaverseObjectChanges_ActivityRunProfileExecutionItemId",
                table: "MetaverseObjectChanges",
                column: "ActivityRunProfileExecutionItemId",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_MetaverseObjectChanges_ActivityRunProfileExecutionItems_Act~",
                table: "MetaverseObjectChanges",
                column: "ActivityRunProfileExecutionItemId",
                principalTable: "ActivityRunProfileExecutionItems",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_MetaverseObjectChanges_ActivityRunProfileExecutionItems_Act~",
                table: "MetaverseObjectChanges");

            migrationBuilder.DropIndex(
                name: "IX_MetaverseObjectChanges_ActivityRunProfileExecutionItemId",
                table: "MetaverseObjectChanges");

            migrationBuilder.DropColumn(
                name: "ActivityRunProfileExecutionItemId",
                table: "MetaverseObjectChanges");

            migrationBuilder.AddColumn<Guid>(
                name: "MetaverseObjectChangeId",
                table: "ActivityRunProfileExecutionItems",
                type: "uuid",
                nullable: true);

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
        }
    }
}
