using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <inheritdoc />
    public partial class AddDeletedObjectDisplayNameFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DeletedObjectDisplayName",
                table: "MetaverseObjectChanges",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DeletedObjectTypeId",
                table: "MetaverseObjectChanges",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DeletedObjectDisplayName",
                table: "ConnectedSystemObjectChanges",
                type: "text",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_MetaverseObjectChanges_DeletedObjectTypeId",
                table: "MetaverseObjectChanges",
                column: "DeletedObjectTypeId");

            migrationBuilder.AddForeignKey(
                name: "FK_MetaverseObjectChanges_MetaverseObjectTypes_DeletedObjectTy~",
                table: "MetaverseObjectChanges",
                column: "DeletedObjectTypeId",
                principalTable: "MetaverseObjectTypes",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_MetaverseObjectChanges_MetaverseObjectTypes_DeletedObjectTy~",
                table: "MetaverseObjectChanges");

            migrationBuilder.DropIndex(
                name: "IX_MetaverseObjectChanges_DeletedObjectTypeId",
                table: "MetaverseObjectChanges");

            migrationBuilder.DropColumn(
                name: "DeletedObjectDisplayName",
                table: "MetaverseObjectChanges");

            migrationBuilder.DropColumn(
                name: "DeletedObjectTypeId",
                table: "MetaverseObjectChanges");

            migrationBuilder.DropColumn(
                name: "DeletedObjectDisplayName",
                table: "ConnectedSystemObjectChanges");
        }
    }
}
