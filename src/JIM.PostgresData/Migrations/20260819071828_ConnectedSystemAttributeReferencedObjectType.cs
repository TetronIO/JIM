using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <inheritdoc />
    public partial class ConnectedSystemAttributeReferencedObjectType : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ReferencedObjectTypeId",
                table: "ConnectedSystemAttributes",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ConnectedSystemAttributes_ReferencedObjectTypeId",
                table: "ConnectedSystemAttributes",
                column: "ReferencedObjectTypeId");

            migrationBuilder.AddForeignKey(
                name: "FK_ConnectedSystemAttributes_ConnectedSystemObjectTypes_Refere~",
                table: "ConnectedSystemAttributes",
                column: "ReferencedObjectTypeId",
                principalTable: "ConnectedSystemObjectTypes",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ConnectedSystemAttributes_ConnectedSystemObjectTypes_Refere~",
                table: "ConnectedSystemAttributes");

            migrationBuilder.DropIndex(
                name: "IX_ConnectedSystemAttributes_ReferencedObjectTypeId",
                table: "ConnectedSystemAttributes");

            migrationBuilder.DropColumn(
                name: "ReferencedObjectTypeId",
                table: "ConnectedSystemAttributes");
        }
    }
}
