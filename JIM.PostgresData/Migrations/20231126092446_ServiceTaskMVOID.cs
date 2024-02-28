using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    public partial class ServiceTaskMVOID : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Activities_MetaverseObjects_InitiatedById",
                table: "Activities");

            migrationBuilder.RenameColumn(
                name: "InitiatedById",
                table: "Activities",
                newName: "MetaverseObjectId");

            migrationBuilder.RenameIndex(
                name: "IX_Activities_InitiatedById",
                table: "Activities",
                newName: "IX_Activities_MetaverseObjectId");

            migrationBuilder.AddColumn<int>(
                name: "SynchronisationRuleId",
                table: "Activities",
                type: "integer",
                nullable: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Activities_MetaverseObjects_MetaverseObjectId",
                table: "Activities",
                column: "MetaverseObjectId",
                principalTable: "MetaverseObjects",
                principalColumn: "Id");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Activities_MetaverseObjects_MetaverseObjectId",
                table: "Activities");

            migrationBuilder.DropColumn(
                name: "SynchronisationRuleId",
                table: "Activities");

            migrationBuilder.RenameColumn(
                name: "MetaverseObjectId",
                table: "Activities",
                newName: "InitiatedById");

            migrationBuilder.RenameIndex(
                name: "IX_Activities_MetaverseObjectId",
                table: "Activities",
                newName: "IX_Activities_InitiatedById");

            migrationBuilder.AddForeignKey(
                name: "FK_Activities_MetaverseObjects_InitiatedById",
                table: "Activities",
                column: "InitiatedById",
                principalTable: "MetaverseObjects",
                principalColumn: "Id");
        }
    }
}
