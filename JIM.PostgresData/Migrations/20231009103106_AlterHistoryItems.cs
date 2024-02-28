using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    public partial class AlterHistoryItems : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ConnectedSystemObjectAttributeValues_ConnectedSystemObjects~",
                table: "ConnectedSystemObjectAttributeValues");

            migrationBuilder.DropColumn(
                name: "CompletionTime",
                table: "SyncRunHistoryDetails");

            migrationBuilder.DropColumn(
                name: "ErrorMessage",
                table: "SyncRunHistoryDetails");

            migrationBuilder.DropColumn(
                name: "ErrorStackTrace",
                table: "SyncRunHistoryDetails");

            migrationBuilder.AddColumn<TimeSpan>(
                name: "CompletionTime",
                table: "HistoryItems",
                type: "interval",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ErrorMessage",
                table: "HistoryItems",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ErrorStackTrace",
                table: "HistoryItems",
                type: "text",
                nullable: true);

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

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ConnectedSystemObjectAttributeValues_ConnectedSystemObjects~",
                table: "ConnectedSystemObjectAttributeValues");

            migrationBuilder.DropColumn(
                name: "CompletionTime",
                table: "HistoryItems");

            migrationBuilder.DropColumn(
                name: "ErrorMessage",
                table: "HistoryItems");

            migrationBuilder.DropColumn(
                name: "ErrorStackTrace",
                table: "HistoryItems");

            migrationBuilder.AddColumn<TimeSpan>(
                name: "CompletionTime",
                table: "SyncRunHistoryDetails",
                type: "interval",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ErrorMessage",
                table: "SyncRunHistoryDetails",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ErrorStackTrace",
                table: "SyncRunHistoryDetails",
                type: "text",
                nullable: true);

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
    }
}
