using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    public partial class RefineActivityRunProfileRel : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Activities_ConnectedSystemRunProfiles_RunProfileId",
                table: "Activities");

            migrationBuilder.RenameColumn(
                name: "RunType",
                table: "Activities",
                newName: "ConnectedSystemRunType");

            migrationBuilder.RenameColumn(
                name: "RunProfileId",
                table: "Activities",
                newName: "ConnectedSystemRunProfileId");

            migrationBuilder.RenameIndex(
                name: "IX_Activities_RunProfileId",
                table: "Activities",
                newName: "IX_Activities_ConnectedSystemRunProfileId");

            migrationBuilder.AddForeignKey(
                name: "FK_Activities_ConnectedSystemRunProfiles_ConnectedSystemRunPro~",
                table: "Activities",
                column: "ConnectedSystemRunProfileId",
                principalTable: "ConnectedSystemRunProfiles",
                principalColumn: "Id");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Activities_ConnectedSystemRunProfiles_ConnectedSystemRunPro~",
                table: "Activities");

            migrationBuilder.RenameColumn(
                name: "ConnectedSystemRunType",
                table: "Activities",
                newName: "RunType");

            migrationBuilder.RenameColumn(
                name: "ConnectedSystemRunProfileId",
                table: "Activities",
                newName: "RunProfileId");

            migrationBuilder.RenameIndex(
                name: "IX_Activities_ConnectedSystemRunProfileId",
                table: "Activities",
                newName: "IX_Activities_RunProfileId");

            migrationBuilder.AddForeignKey(
                name: "FK_Activities_ConnectedSystemRunProfiles_RunProfileId",
                table: "Activities",
                column: "RunProfileId",
                principalTable: "ConnectedSystemRunProfiles",
                principalColumn: "Id");
        }
    }
}
