using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    public partial class SettingValueInt : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "DefaultIntValue",
                table: "ConnectorDefinitionSetting",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "IntValue",
                table: "ConnectedSystemSettingValue",
                type: "integer",
                nullable: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DefaultIntValue",
                table: "ConnectorDefinitionSetting");

            migrationBuilder.DropColumn(
                name: "IntValue",
                table: "ConnectedSystemSettingValue");
        }
    }
}
