using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    public partial class UniqueIdToExternalId : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "DeletedObjectUniqueIdentifierAttributeValueId",
                table: "ConnectedSystemObjectChanges",
                newName: "DeletedObjectExternalIdAttributeValueId");

            migrationBuilder.RenameIndex(
                name: "IX_ConnectedSystemObjectChanges_DeletedObjectUniqueIdentifierA~",
                table: "ConnectedSystemObjectChanges",
                newName: "IX_ConnectedSystemObjectChanges_DeletedObjectExternalIdAttribu~");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "DeletedObjectExternalIdAttributeValueId",
                table: "ConnectedSystemObjectChanges",
                newName: "DeletedObjectUniqueIdentifierAttributeValueId");

            migrationBuilder.RenameIndex(
                name: "IX_ConnectedSystemObjectChanges_DeletedObjectExternalIdAttribu~",
                table: "ConnectedSystemObjectChanges",
                newName: "IX_ConnectedSystemObjectChanges_DeletedObjectUniqueIdentifierA~");
        }
    }
}
