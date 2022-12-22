using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    public partial class ConnectedSystemSettingRejig2 : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DefaultCheckboxValue",
                table: "ConnectedSystemSettings");

            migrationBuilder.DropColumn(
                name: "DefaultStringValue",
                table: "ConnectedSystemSettings");

            migrationBuilder.DropColumn(
                name: "DropDownValues",
                table: "ConnectedSystemSettings");

            migrationBuilder.AlterColumn<string>(
                name: "Name",
                table: "ConnectedSystemSettings",
                type: "text",
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "Description",
                table: "ConnectedSystemSettings",
                type: "text",
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "Name",
                table: "ConnectedSystemSettings",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AlterColumn<string>(
                name: "Description",
                table: "ConnectedSystemSettings",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AddColumn<bool>(
                name: "DefaultCheckboxValue",
                table: "ConnectedSystemSettings",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DefaultStringValue",
                table: "ConnectedSystemSettings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<List<string>>(
                name: "DropDownValues",
                table: "ConnectedSystemSettings",
                type: "text[]",
                nullable: true);
        }
    }
}
