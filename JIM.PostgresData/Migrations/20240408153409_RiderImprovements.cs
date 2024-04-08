using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    public partial class RiderImprovements : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "SupportsUserSeletedAttributeTypes",
                table: "ConnectorDefinitions",
                newName: "SupportsUserSelectedAttributeTypes");

            migrationBuilder.AlterColumn<string>(
                name: "StringValue",
                table: "SyncRuleScopingCriteria",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AddColumn<bool>(
                name: "BoolValue",
                table: "SyncRuleScopingCriteria",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "DateTimeValue",
                table: "SyncRuleScopingCriteria",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "GuidValue",
                table: "SyncRuleScopingCriteria",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "IntValue",
                table: "SyncRuleScopingCriteria",
                type: "integer",
                nullable: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BoolValue",
                table: "SyncRuleScopingCriteria");

            migrationBuilder.DropColumn(
                name: "DateTimeValue",
                table: "SyncRuleScopingCriteria");

            migrationBuilder.DropColumn(
                name: "GuidValue",
                table: "SyncRuleScopingCriteria");

            migrationBuilder.DropColumn(
                name: "IntValue",
                table: "SyncRuleScopingCriteria");

            migrationBuilder.RenameColumn(
                name: "SupportsUserSelectedAttributeTypes",
                table: "ConnectorDefinitions",
                newName: "SupportsUserSeletedAttributeTypes");

            migrationBuilder.AlterColumn<string>(
                name: "StringValue",
                table: "SyncRuleScopingCriteria",
                type: "text",
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);
        }
    }
}
