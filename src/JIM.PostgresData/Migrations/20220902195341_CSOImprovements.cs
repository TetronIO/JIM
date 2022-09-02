using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    public partial class CSOImprovements : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_PendingExports_ConnectedSystemObjects_ConnectedSystemObject~",
                table: "PendingExports");

            migrationBuilder.AlterColumn<long>(
                name: "ConnectedSystemObjectId",
                table: "PendingExports",
                type: "bigint",
                nullable: true,
                oldClrType: typeof(long),
                oldType: "bigint");

            migrationBuilder.AddColumn<int>(
                name: "ConnectedSystemId",
                table: "PendingExports",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "DateJoined",
                table: "ConnectedSystemObjects",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "JoinType",
                table: "ConnectedSystemObjects",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "MetaverseObjectId",
                table: "ConnectedSystemObjects",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_PendingExports_ConnectedSystemId",
                table: "PendingExports",
                column: "ConnectedSystemId");

            migrationBuilder.CreateIndex(
                name: "IX_ConnectedSystemObjects_MetaverseObjectId",
                table: "ConnectedSystemObjects",
                column: "MetaverseObjectId");

            migrationBuilder.AddForeignKey(
                name: "FK_ConnectedSystemObjects_MetaverseObjects_MetaverseObjectId",
                table: "ConnectedSystemObjects",
                column: "MetaverseObjectId",
                principalTable: "MetaverseObjects",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_PendingExports_ConnectedSystemObjects_ConnectedSystemObject~",
                table: "PendingExports",
                column: "ConnectedSystemObjectId",
                principalTable: "ConnectedSystemObjects",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_PendingExports_ConnectedSystems_ConnectedSystemId",
                table: "PendingExports",
                column: "ConnectedSystemId",
                principalTable: "ConnectedSystems",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ConnectedSystemObjects_MetaverseObjects_MetaverseObjectId",
                table: "ConnectedSystemObjects");

            migrationBuilder.DropForeignKey(
                name: "FK_PendingExports_ConnectedSystemObjects_ConnectedSystemObject~",
                table: "PendingExports");

            migrationBuilder.DropForeignKey(
                name: "FK_PendingExports_ConnectedSystems_ConnectedSystemId",
                table: "PendingExports");

            migrationBuilder.DropIndex(
                name: "IX_PendingExports_ConnectedSystemId",
                table: "PendingExports");

            migrationBuilder.DropIndex(
                name: "IX_ConnectedSystemObjects_MetaverseObjectId",
                table: "ConnectedSystemObjects");

            migrationBuilder.DropColumn(
                name: "ConnectedSystemId",
                table: "PendingExports");

            migrationBuilder.DropColumn(
                name: "DateJoined",
                table: "ConnectedSystemObjects");

            migrationBuilder.DropColumn(
                name: "JoinType",
                table: "ConnectedSystemObjects");

            migrationBuilder.DropColumn(
                name: "MetaverseObjectId",
                table: "ConnectedSystemObjects");

            migrationBuilder.AlterColumn<long>(
                name: "ConnectedSystemObjectId",
                table: "PendingExports",
                type: "bigint",
                nullable: false,
                defaultValue: 0L,
                oldClrType: typeof(long),
                oldType: "bigint",
                oldNullable: true);

            migrationBuilder.AddForeignKey(
                name: "FK_PendingExports_ConnectedSystemObjects_ConnectedSystemObject~",
                table: "PendingExports",
                column: "ConnectedSystemObjectId",
                principalTable: "ConnectedSystemObjects",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
