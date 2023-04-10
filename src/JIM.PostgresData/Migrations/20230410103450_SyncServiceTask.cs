using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    public partial class SyncServiceTask : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ConnectedSystemRunProfileId",
                table: "ServiceTasks",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<uint>(
                name: "xmin",
                table: "MetaverseObjects",
                type: "xid",
                rowVersion: true,
                nullable: false,
                defaultValue: 0u);

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

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ServiceTasks_ConnectedSystemRunProfiles_ConnectedSystemRunP~",
                table: "ServiceTasks");

            migrationBuilder.DropIndex(
                name: "IX_ServiceTasks_ConnectedSystemRunProfileId",
                table: "ServiceTasks");

            migrationBuilder.DropColumn(
                name: "ConnectedSystemRunProfileId",
                table: "ServiceTasks");

            migrationBuilder.DropColumn(
                name: "xmin",
                table: "MetaverseObjects");
        }
    }
}
