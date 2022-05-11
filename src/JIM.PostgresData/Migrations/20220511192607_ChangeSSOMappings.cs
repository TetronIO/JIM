using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    public partial class ChangeSSOMappings : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ServiceSettings_MetaverseAttributes_SSONameIDAttributeId",
                table: "ServiceSettings");

            migrationBuilder.RenameColumn(
                name: "SSONameIDAttributeId",
                table: "ServiceSettings",
                newName: "SSOUniqueIdentifierMetaverseAttributeId");

            migrationBuilder.RenameIndex(
                name: "IX_ServiceSettings_SSONameIDAttributeId",
                table: "ServiceSettings",
                newName: "IX_ServiceSettings_SSOUniqueIdentifierMetaverseAttributeId");

            migrationBuilder.AddColumn<string>(
                name: "SSOUniqueIdentifierClaimType",
                table: "ServiceSettings",
                type: "text",
                nullable: true);

            migrationBuilder.AddForeignKey(
                name: "FK_ServiceSettings_MetaverseAttributes_SSOUniqueIdentifierMeta~",
                table: "ServiceSettings",
                column: "SSOUniqueIdentifierMetaverseAttributeId",
                principalTable: "MetaverseAttributes",
                principalColumn: "Id");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ServiceSettings_MetaverseAttributes_SSOUniqueIdentifierMeta~",
                table: "ServiceSettings");

            migrationBuilder.DropColumn(
                name: "SSOUniqueIdentifierClaimType",
                table: "ServiceSettings");

            migrationBuilder.RenameColumn(
                name: "SSOUniqueIdentifierMetaverseAttributeId",
                table: "ServiceSettings",
                newName: "SSONameIDAttributeId");

            migrationBuilder.RenameIndex(
                name: "IX_ServiceSettings_SSOUniqueIdentifierMetaverseAttributeId",
                table: "ServiceSettings",
                newName: "IX_ServiceSettings_SSONameIDAttributeId");

            migrationBuilder.AddForeignKey(
                name: "FK_ServiceSettings_MetaverseAttributes_SSONameIDAttributeId",
                table: "ServiceSettings",
                column: "SSONameIDAttributeId",
                principalTable: "MetaverseAttributes",
                principalColumn: "Id");
        }
    }
}
