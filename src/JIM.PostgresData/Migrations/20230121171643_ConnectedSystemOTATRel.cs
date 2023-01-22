using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    public partial class ConnectedSystemOTATRel : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ConnectedSystemAttributes_ConnectedSystemObjectTypes_Connec~",
                table: "ConnectedSystemAttributes");

            migrationBuilder.DropForeignKey(
                name: "FK_ConnectedSystemAttributes_ConnectedSystems_ConnectedSystemId",
                table: "ConnectedSystemAttributes");

            migrationBuilder.DropIndex(
                name: "IX_ConnectedSystemAttributes_ConnectedSystemId",
                table: "ConnectedSystemAttributes");

            migrationBuilder.DropColumn(
                name: "ConnectedSystemId",
                table: "ConnectedSystemAttributes");

            migrationBuilder.AddColumn<bool>(
                name: "Selected",
                table: "ConnectedSystemObjectTypes",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AlterColumn<int>(
                name: "ConnectedSystemObjectTypeId",
                table: "ConnectedSystemAttributes",
                type: "integer",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "integer",
                oldNullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "Selected",
                table: "ConnectedSystemAttributes",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddForeignKey(
                name: "FK_ConnectedSystemAttributes_ConnectedSystemObjectTypes_Connec~",
                table: "ConnectedSystemAttributes",
                column: "ConnectedSystemObjectTypeId",
                principalTable: "ConnectedSystemObjectTypes",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ConnectedSystemAttributes_ConnectedSystemObjectTypes_Connec~",
                table: "ConnectedSystemAttributes");

            migrationBuilder.DropColumn(
                name: "Selected",
                table: "ConnectedSystemObjectTypes");

            migrationBuilder.DropColumn(
                name: "Selected",
                table: "ConnectedSystemAttributes");

            migrationBuilder.AlterColumn<int>(
                name: "ConnectedSystemObjectTypeId",
                table: "ConnectedSystemAttributes",
                type: "integer",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "integer");

            migrationBuilder.AddColumn<int>(
                name: "ConnectedSystemId",
                table: "ConnectedSystemAttributes",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_ConnectedSystemAttributes_ConnectedSystemId",
                table: "ConnectedSystemAttributes",
                column: "ConnectedSystemId");

            migrationBuilder.AddForeignKey(
                name: "FK_ConnectedSystemAttributes_ConnectedSystemObjectTypes_Connec~",
                table: "ConnectedSystemAttributes",
                column: "ConnectedSystemObjectTypeId",
                principalTable: "ConnectedSystemObjectTypes",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_ConnectedSystemAttributes_ConnectedSystems_ConnectedSystemId",
                table: "ConnectedSystemAttributes",
                column: "ConnectedSystemId",
                principalTable: "ConnectedSystems",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
