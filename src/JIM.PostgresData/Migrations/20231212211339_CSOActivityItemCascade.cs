using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    public partial class CSOActivityItemCascade : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ActivityRunProfileExecutionItems_ConnectedSystemObjects_Con~",
                table: "ActivityRunProfileExecutionItems");

            migrationBuilder.AddForeignKey(
                name: "FK_ActivityRunProfileExecutionItems_ConnectedSystemObjects_Con~",
                table: "ActivityRunProfileExecutionItems",
                column: "ConnectedSystemObjectId",
                principalTable: "ConnectedSystemObjects",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ActivityRunProfileExecutionItems_ConnectedSystemObjects_Con~",
                table: "ActivityRunProfileExecutionItems");

            migrationBuilder.AddForeignKey(
                name: "FK_ActivityRunProfileExecutionItems_ConnectedSystemObjects_Con~",
                table: "ActivityRunProfileExecutionItems",
                column: "ConnectedSystemObjectId",
                principalTable: "ConnectedSystemObjects",
                principalColumn: "Id");
        }
    }
}
