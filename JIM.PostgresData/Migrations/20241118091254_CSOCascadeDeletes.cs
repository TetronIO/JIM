using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    public partial class CSOCascadeDeletes : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ActivityRunProfileExecutionItems_ConnectedSystemObjects_Con~",
                table: "ActivityRunProfileExecutionItems");

            migrationBuilder.DropForeignKey(
                name: "FK_ConnectedSystemObjectChanges_ConnectedSystemObjects_Connect~",
                table: "ConnectedSystemObjectChanges");

            migrationBuilder.AddForeignKey(
                name: "FK_ActivityRunProfileExecutionItems_ConnectedSystemObjects_Con~",
                table: "ActivityRunProfileExecutionItems",
                column: "ConnectedSystemObjectId",
                principalTable: "ConnectedSystemObjects",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_ConnectedSystemObjectChanges_ConnectedSystemObjects_Connect~",
                table: "ConnectedSystemObjectChanges",
                column: "ConnectedSystemObjectId",
                principalTable: "ConnectedSystemObjects",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ActivityRunProfileExecutionItems_ConnectedSystemObjects_Con~",
                table: "ActivityRunProfileExecutionItems");

            migrationBuilder.DropForeignKey(
                name: "FK_ConnectedSystemObjectChanges_ConnectedSystemObjects_Connect~",
                table: "ConnectedSystemObjectChanges");

            migrationBuilder.AddForeignKey(
                name: "FK_ActivityRunProfileExecutionItems_ConnectedSystemObjects_Con~",
                table: "ActivityRunProfileExecutionItems",
                column: "ConnectedSystemObjectId",
                principalTable: "ConnectedSystemObjects",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_ConnectedSystemObjectChanges_ConnectedSystemObjects_Connect~",
                table: "ConnectedSystemObjectChanges",
                column: "ConnectedSystemObjectId",
                principalTable: "ConnectedSystemObjects",
                principalColumn: "Id");
        }
    }
}
