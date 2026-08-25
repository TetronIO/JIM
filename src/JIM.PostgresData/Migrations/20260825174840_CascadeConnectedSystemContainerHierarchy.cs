using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <inheritdoc />
    public partial class CascadeConnectedSystemContainerHierarchy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ConnectedSystemContainers_ConnectedSystemContainers_ParentC~",
                table: "ConnectedSystemContainers");

            migrationBuilder.AddForeignKey(
                name: "FK_ConnectedSystemContainers_ConnectedSystemContainers_ParentC~",
                table: "ConnectedSystemContainers",
                column: "ParentContainerId",
                principalTable: "ConnectedSystemContainers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ConnectedSystemContainers_ConnectedSystemContainers_ParentC~",
                table: "ConnectedSystemContainers");

            migrationBuilder.AddForeignKey(
                name: "FK_ConnectedSystemContainers_ConnectedSystemContainers_ParentC~",
                table: "ConnectedSystemContainers",
                column: "ParentContainerId",
                principalTable: "ConnectedSystemContainers",
                principalColumn: "Id");
        }
    }
}
