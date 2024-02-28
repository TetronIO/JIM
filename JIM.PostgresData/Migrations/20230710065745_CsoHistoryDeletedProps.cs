using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    public partial class CsoHistoryDeletedProps : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "DeletedObjectTypeId",
                table: "ConnectedSystemObjectChanges",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "DeletedObjectUniqueIdentifierAttributeValueId",
                table: "ConnectedSystemObjectChanges",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ConnectedSystemObjectChanges_DeletedObjectTypeId",
                table: "ConnectedSystemObjectChanges",
                column: "DeletedObjectTypeId");

            migrationBuilder.CreateIndex(
                name: "IX_ConnectedSystemObjectChanges_DeletedObjectUniqueIdentifierA~",
                table: "ConnectedSystemObjectChanges",
                column: "DeletedObjectUniqueIdentifierAttributeValueId");

            migrationBuilder.AddForeignKey(
                name: "FK_ConnectedSystemObjectChanges_ConnectedSystemObjectAttribute~",
                table: "ConnectedSystemObjectChanges",
                column: "DeletedObjectUniqueIdentifierAttributeValueId",
                principalTable: "ConnectedSystemObjectAttributeValues",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_ConnectedSystemObjectChanges_ConnectedSystemObjectTypes_Del~",
                table: "ConnectedSystemObjectChanges",
                column: "DeletedObjectTypeId",
                principalTable: "ConnectedSystemObjectTypes",
                principalColumn: "Id");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ConnectedSystemObjectChanges_ConnectedSystemObjectAttribute~",
                table: "ConnectedSystemObjectChanges");

            migrationBuilder.DropForeignKey(
                name: "FK_ConnectedSystemObjectChanges_ConnectedSystemObjectTypes_Del~",
                table: "ConnectedSystemObjectChanges");

            migrationBuilder.DropIndex(
                name: "IX_ConnectedSystemObjectChanges_DeletedObjectTypeId",
                table: "ConnectedSystemObjectChanges");

            migrationBuilder.DropIndex(
                name: "IX_ConnectedSystemObjectChanges_DeletedObjectUniqueIdentifierA~",
                table: "ConnectedSystemObjectChanges");

            migrationBuilder.DropColumn(
                name: "DeletedObjectTypeId",
                table: "ConnectedSystemObjectChanges");

            migrationBuilder.DropColumn(
                name: "DeletedObjectUniqueIdentifierAttributeValueId",
                table: "ConnectedSystemObjectChanges");
        }
    }
}
