using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    public partial class DbContextConnectedSysSettingVals : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ConnectedSystemSettingValue_ConnectedSystems_ConnectedSyste~",
                table: "ConnectedSystemSettingValue");

            migrationBuilder.DropForeignKey(
                name: "FK_ConnectedSystemSettingValue_ConnectorDefinitionSettings_Set~",
                table: "ConnectedSystemSettingValue");

            migrationBuilder.DropPrimaryKey(
                name: "PK_ConnectedSystemSettingValue",
                table: "ConnectedSystemSettingValue");

            migrationBuilder.RenameTable(
                name: "ConnectedSystemSettingValue",
                newName: "ConnectedSystemSettingValues");

            migrationBuilder.RenameIndex(
                name: "IX_ConnectedSystemSettingValue_SettingId",
                table: "ConnectedSystemSettingValues",
                newName: "IX_ConnectedSystemSettingValues_SettingId");

            migrationBuilder.RenameIndex(
                name: "IX_ConnectedSystemSettingValue_ConnectedSystemId",
                table: "ConnectedSystemSettingValues",
                newName: "IX_ConnectedSystemSettingValues_ConnectedSystemId");

            migrationBuilder.AddPrimaryKey(
                name: "PK_ConnectedSystemSettingValues",
                table: "ConnectedSystemSettingValues",
                column: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_ConnectedSystemSettingValues_ConnectedSystems_ConnectedSyst~",
                table: "ConnectedSystemSettingValues",
                column: "ConnectedSystemId",
                principalTable: "ConnectedSystems",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_ConnectedSystemSettingValues_ConnectorDefinitionSettings_Se~",
                table: "ConnectedSystemSettingValues",
                column: "SettingId",
                principalTable: "ConnectorDefinitionSettings",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ConnectedSystemSettingValues_ConnectedSystems_ConnectedSyst~",
                table: "ConnectedSystemSettingValues");

            migrationBuilder.DropForeignKey(
                name: "FK_ConnectedSystemSettingValues_ConnectorDefinitionSettings_Se~",
                table: "ConnectedSystemSettingValues");

            migrationBuilder.DropPrimaryKey(
                name: "PK_ConnectedSystemSettingValues",
                table: "ConnectedSystemSettingValues");

            migrationBuilder.RenameTable(
                name: "ConnectedSystemSettingValues",
                newName: "ConnectedSystemSettingValue");

            migrationBuilder.RenameIndex(
                name: "IX_ConnectedSystemSettingValues_SettingId",
                table: "ConnectedSystemSettingValue",
                newName: "IX_ConnectedSystemSettingValue_SettingId");

            migrationBuilder.RenameIndex(
                name: "IX_ConnectedSystemSettingValues_ConnectedSystemId",
                table: "ConnectedSystemSettingValue",
                newName: "IX_ConnectedSystemSettingValue_ConnectedSystemId");

            migrationBuilder.AddPrimaryKey(
                name: "PK_ConnectedSystemSettingValue",
                table: "ConnectedSystemSettingValue",
                column: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_ConnectedSystemSettingValue_ConnectedSystems_ConnectedSyste~",
                table: "ConnectedSystemSettingValue",
                column: "ConnectedSystemId",
                principalTable: "ConnectedSystems",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_ConnectedSystemSettingValue_ConnectorDefinitionSettings_Set~",
                table: "ConnectedSystemSettingValue",
                column: "SettingId",
                principalTable: "ConnectorDefinitionSettings",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
