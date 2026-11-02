using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <inheritdoc />
    public partial class RemoveDeletedObjectExternalIdAttributeValueReference : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ConnectedSystemObjectChanges_ConnectedSystemObjectAttribute~",
                table: "ConnectedSystemObjectChanges");

            migrationBuilder.DropIndex(
                name: "IX_ConnectedSystemObjectChanges_DeletedObjectExternalIdAttribu~",
                table: "ConnectedSystemObjectChanges");

            migrationBuilder.DropColumn(
                name: "DeletedObjectExternalIdAttributeValueId",
                table: "ConnectedSystemObjectChanges");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "DeletedObjectExternalIdAttributeValueId",
                table: "ConnectedSystemObjectChanges",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ConnectedSystemObjectChanges_DeletedObjectExternalIdAttribu~",
                table: "ConnectedSystemObjectChanges",
                column: "DeletedObjectExternalIdAttributeValueId");

            migrationBuilder.AddForeignKey(
                name: "FK_ConnectedSystemObjectChanges_ConnectedSystemObjectAttribute~",
                table: "ConnectedSystemObjectChanges",
                column: "DeletedObjectExternalIdAttributeValueId",
                principalTable: "ConnectedSystemObjectAttributeValues",
                principalColumn: "Id");
        }
    }
}
