using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    public partial class CsoavHistoryChange : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ConnectedSystemObjectAttributeValues_ConnectedSystemObjects~",
                table: "ConnectedSystemObjectAttributeValues");

            migrationBuilder.AlterColumn<Guid>(
                name: "ConnectedSystemObjectId",
                table: "ConnectedSystemObjectAttributeValues",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddForeignKey(
                name: "FK_ConnectedSystemObjectAttributeValues_ConnectedSystemObjects~",
                table: "ConnectedSystemObjectAttributeValues",
                column: "ConnectedSystemObjectId",
                principalTable: "ConnectedSystemObjects",
                principalColumn: "Id");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ConnectedSystemObjectAttributeValues_ConnectedSystemObjects~",
                table: "ConnectedSystemObjectAttributeValues");

            migrationBuilder.AlterColumn<Guid>(
                name: "ConnectedSystemObjectId",
                table: "ConnectedSystemObjectAttributeValues",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AddForeignKey(
                name: "FK_ConnectedSystemObjectAttributeValues_ConnectedSystemObjects~",
                table: "ConnectedSystemObjectAttributeValues",
                column: "ConnectedSystemObjectId",
                principalTable: "ConnectedSystemObjects",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
