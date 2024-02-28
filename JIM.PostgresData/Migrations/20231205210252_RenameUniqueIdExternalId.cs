using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    public partial class RenameUniqueIdExternalId : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "UniqueIdentifierAttributeId",
                table: "ConnectedSystemObjects",
                newName: "ExternalIdAttributeId");

            migrationBuilder.RenameColumn(
                name: "IsUniqueIdentifier",
                table: "ConnectedSystemAttributes",
                newName: "IsExternalId");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "ExternalIdAttributeId",
                table: "ConnectedSystemObjects",
                newName: "UniqueIdentifierAttributeId");

            migrationBuilder.RenameColumn(
                name: "IsExternalId",
                table: "ConnectedSystemAttributes",
                newName: "IsUniqueIdentifier");
        }
    }
}
