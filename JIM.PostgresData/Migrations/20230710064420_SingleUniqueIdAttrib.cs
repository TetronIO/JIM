using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    public partial class SingleUniqueIdAttrib : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ConnectedSystemObjectChanges_ConnectedSystems_ConnectedSyst~",
                table: "ConnectedSystemObjectChanges");

            migrationBuilder.DropForeignKey(
                name: "FK_ConnectedSystemObjects_ConnectedSystemAttributes_UniqueIden~",
                table: "ConnectedSystemObjects");

            migrationBuilder.DropIndex(
                name: "IX_ConnectedSystemObjects_UniqueIdentifierAttributeId",
                table: "ConnectedSystemObjects");

            migrationBuilder.DropIndex(
                name: "IX_ConnectedSystemObjectChanges_ConnectedSystemId",
                table: "ConnectedSystemObjectChanges");

            migrationBuilder.AddColumn<Guid>(
                name: "ConnectedSystemObjectId",
                table: "SyncRunHistoryDetailItem",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ObjectChangeType",
                table: "SyncRunHistoryDetailItem",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_SyncRunHistoryDetailItem_ConnectedSystemObjectId",
                table: "SyncRunHistoryDetailItem",
                column: "ConnectedSystemObjectId");

            migrationBuilder.AddForeignKey(
                name: "FK_SyncRunHistoryDetailItem_ConnectedSystemObjects_ConnectedSy~",
                table: "SyncRunHistoryDetailItem",
                column: "ConnectedSystemObjectId",
                principalTable: "ConnectedSystemObjects",
                principalColumn: "Id");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_SyncRunHistoryDetailItem_ConnectedSystemObjects_ConnectedSy~",
                table: "SyncRunHistoryDetailItem");

            migrationBuilder.DropIndex(
                name: "IX_SyncRunHistoryDetailItem_ConnectedSystemObjectId",
                table: "SyncRunHistoryDetailItem");

            migrationBuilder.DropColumn(
                name: "ConnectedSystemObjectId",
                table: "SyncRunHistoryDetailItem");

            migrationBuilder.DropColumn(
                name: "ObjectChangeType",
                table: "SyncRunHistoryDetailItem");

            migrationBuilder.CreateIndex(
                name: "IX_ConnectedSystemObjects_UniqueIdentifierAttributeId",
                table: "ConnectedSystemObjects",
                column: "UniqueIdentifierAttributeId");

            migrationBuilder.CreateIndex(
                name: "IX_ConnectedSystemObjectChanges_ConnectedSystemId",
                table: "ConnectedSystemObjectChanges",
                column: "ConnectedSystemId");

            migrationBuilder.AddForeignKey(
                name: "FK_ConnectedSystemObjectChanges_ConnectedSystems_ConnectedSyst~",
                table: "ConnectedSystemObjectChanges",
                column: "ConnectedSystemId",
                principalTable: "ConnectedSystems",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_ConnectedSystemObjects_ConnectedSystemAttributes_UniqueIden~",
                table: "ConnectedSystemObjects",
                column: "UniqueIdentifierAttributeId",
                principalTable: "ConnectedSystemAttributes",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
