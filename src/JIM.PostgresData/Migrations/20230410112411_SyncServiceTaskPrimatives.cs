using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    public partial class SyncServiceTaskPrimatives : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ServiceTasks_ConnectedSystemRunProfiles_ConnectedSystemRunP~",
                table: "ServiceTasks");

            migrationBuilder.DropIndex(
                name: "IX_ServiceTasks_ConnectedSystemRunProfileId",
                table: "ServiceTasks");

            migrationBuilder.AddColumn<int>(
                name: "ConnectedSystemId",
                table: "ServiceTasks",
                type: "integer",
                nullable: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ConnectedSystemId",
                table: "ServiceTasks");

            migrationBuilder.CreateIndex(
                name: "IX_ServiceTasks_ConnectedSystemRunProfileId",
                table: "ServiceTasks",
                column: "ConnectedSystemRunProfileId");

            migrationBuilder.AddForeignKey(
                name: "FK_ServiceTasks_ConnectedSystemRunProfiles_ConnectedSystemRunP~",
                table: "ServiceTasks",
                column: "ConnectedSystemRunProfileId",
                principalTable: "ConnectedSystemRunProfiles",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
