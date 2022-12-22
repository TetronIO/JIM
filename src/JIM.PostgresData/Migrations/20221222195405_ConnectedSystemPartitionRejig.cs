using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    public partial class ConnectedSystemPartitionRejig : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ConnectedSystemId",
                table: "ConnectedSystemContainer",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ConnectedSystemContainer_ConnectedSystemId",
                table: "ConnectedSystemContainer",
                column: "ConnectedSystemId");

            migrationBuilder.AddForeignKey(
                name: "FK_ConnectedSystemContainer_ConnectedSystems_ConnectedSystemId",
                table: "ConnectedSystemContainer",
                column: "ConnectedSystemId",
                principalTable: "ConnectedSystems",
                principalColumn: "Id");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ConnectedSystemContainer_ConnectedSystems_ConnectedSystemId",
                table: "ConnectedSystemContainer");

            migrationBuilder.DropIndex(
                name: "IX_ConnectedSystemContainer_ConnectedSystemId",
                table: "ConnectedSystemContainer");

            migrationBuilder.DropColumn(
                name: "ConnectedSystemId",
                table: "ConnectedSystemContainer");
        }
    }
}
