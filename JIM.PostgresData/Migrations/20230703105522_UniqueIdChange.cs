using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    public partial class UniqueIdChange : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ConnectedSystemObjectTypes_ConnectedSystemAttributes_Unique~",
                table: "ConnectedSystemObjectTypes");

            migrationBuilder.DropIndex(
                name: "IX_ConnectedSystemObjectTypes_UniqueIdentifierAttributeId",
                table: "ConnectedSystemObjectTypes");

            migrationBuilder.DropColumn(
                name: "UniqueIdentifierAttributeId",
                table: "ConnectedSystemObjectTypes");

            migrationBuilder.AddColumn<bool>(
                name: "IsUniqueIdentifier",
                table: "ConnectedSystemAttributes",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsUniqueIdentifier",
                table: "ConnectedSystemAttributes");

            migrationBuilder.AddColumn<int>(
                name: "UniqueIdentifierAttributeId",
                table: "ConnectedSystemObjectTypes",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ConnectedSystemObjectTypes_UniqueIdentifierAttributeId",
                table: "ConnectedSystemObjectTypes",
                column: "UniqueIdentifierAttributeId");

            migrationBuilder.AddForeignKey(
                name: "FK_ConnectedSystemObjectTypes_ConnectedSystemAttributes_Unique~",
                table: "ConnectedSystemObjectTypes",
                column: "UniqueIdentifierAttributeId",
                principalTable: "ConnectedSystemAttributes",
                principalColumn: "Id");
        }
    }
}
