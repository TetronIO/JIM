using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    public partial class ContainerParent2 : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ConnectedSystemContainers_ConnectedSystemContainers_Connect~",
                table: "ConnectedSystemContainers");

            migrationBuilder.RenameColumn(
                name: "ConnectedSystemContainerId",
                table: "ConnectedSystemContainers",
                newName: "ParentContainerId");

            migrationBuilder.RenameIndex(
                name: "IX_ConnectedSystemContainers_ConnectedSystemContainerId",
                table: "ConnectedSystemContainers",
                newName: "IX_ConnectedSystemContainers_ParentContainerId");

            migrationBuilder.AddForeignKey(
                name: "FK_ConnectedSystemContainers_ConnectedSystemContainers_ParentC~",
                table: "ConnectedSystemContainers",
                column: "ParentContainerId",
                principalTable: "ConnectedSystemContainers",
                principalColumn: "Id");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ConnectedSystemContainers_ConnectedSystemContainers_ParentC~",
                table: "ConnectedSystemContainers");

            migrationBuilder.RenameColumn(
                name: "ParentContainerId",
                table: "ConnectedSystemContainers",
                newName: "ConnectedSystemContainerId");

            migrationBuilder.RenameIndex(
                name: "IX_ConnectedSystemContainers_ParentContainerId",
                table: "ConnectedSystemContainers",
                newName: "IX_ConnectedSystemContainers_ConnectedSystemContainerId");

            migrationBuilder.AddForeignKey(
                name: "FK_ConnectedSystemContainers_ConnectedSystemContainers_Connect~",
                table: "ConnectedSystemContainers",
                column: "ConnectedSystemContainerId",
                principalTable: "ConnectedSystemContainers",
                principalColumn: "Id");
        }
    }
}
