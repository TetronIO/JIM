using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    public partial class DbContextConnectorDefSettings : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ConnectedSystemSettingValue_ConnectorDefinitionSetting_Sett~",
                table: "ConnectedSystemSettingValue");

            migrationBuilder.DropForeignKey(
                name: "FK_ConnectorDefinitionSetting_ConnectorDefinitions_ConnectorDe~",
                table: "ConnectorDefinitionSetting");

            migrationBuilder.DropPrimaryKey(
                name: "PK_ConnectorDefinitionSetting",
                table: "ConnectorDefinitionSetting");

            migrationBuilder.RenameTable(
                name: "ConnectorDefinitionSetting",
                newName: "ConnectorDefinitionSettings");

            migrationBuilder.RenameIndex(
                name: "IX_ConnectorDefinitionSetting_ConnectorDefinitionId",
                table: "ConnectorDefinitionSettings",
                newName: "IX_ConnectorDefinitionSettings_ConnectorDefinitionId");

            migrationBuilder.AddPrimaryKey(
                name: "PK_ConnectorDefinitionSettings",
                table: "ConnectorDefinitionSettings",
                column: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_ConnectedSystemSettingValue_ConnectorDefinitionSettings_Set~",
                table: "ConnectedSystemSettingValue",
                column: "SettingId",
                principalTable: "ConnectorDefinitionSettings",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_ConnectorDefinitionSettings_ConnectorDefinitions_ConnectorD~",
                table: "ConnectorDefinitionSettings",
                column: "ConnectorDefinitionId",
                principalTable: "ConnectorDefinitions",
                principalColumn: "Id");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ConnectedSystemSettingValue_ConnectorDefinitionSettings_Set~",
                table: "ConnectedSystemSettingValue");

            migrationBuilder.DropForeignKey(
                name: "FK_ConnectorDefinitionSettings_ConnectorDefinitions_ConnectorD~",
                table: "ConnectorDefinitionSettings");

            migrationBuilder.DropPrimaryKey(
                name: "PK_ConnectorDefinitionSettings",
                table: "ConnectorDefinitionSettings");

            migrationBuilder.RenameTable(
                name: "ConnectorDefinitionSettings",
                newName: "ConnectorDefinitionSetting");

            migrationBuilder.RenameIndex(
                name: "IX_ConnectorDefinitionSettings_ConnectorDefinitionId",
                table: "ConnectorDefinitionSetting",
                newName: "IX_ConnectorDefinitionSetting_ConnectorDefinitionId");

            migrationBuilder.AddPrimaryKey(
                name: "PK_ConnectorDefinitionSetting",
                table: "ConnectorDefinitionSetting",
                column: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_ConnectedSystemSettingValue_ConnectorDefinitionSetting_Sett~",
                table: "ConnectedSystemSettingValue",
                column: "SettingId",
                principalTable: "ConnectorDefinitionSetting",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_ConnectorDefinitionSetting_ConnectorDefinitions_ConnectorDe~",
                table: "ConnectorDefinitionSetting",
                column: "ConnectorDefinitionId",
                principalTable: "ConnectorDefinitions",
                principalColumn: "Id");
        }
    }
}
