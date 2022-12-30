using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    public partial class ConnectorDefinitionSrttings : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ConnectorDefinitionId",
                table: "ConnectorDefinitionSetting",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ConnectorDefinitionSetting_ConnectorDefinitionId",
                table: "ConnectorDefinitionSetting",
                column: "ConnectorDefinitionId");

            migrationBuilder.AddForeignKey(
                name: "FK_ConnectorDefinitionSetting_ConnectorDefinitions_ConnectorDe~",
                table: "ConnectorDefinitionSetting",
                column: "ConnectorDefinitionId",
                principalTable: "ConnectorDefinitions",
                principalColumn: "Id");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ConnectorDefinitionSetting_ConnectorDefinitions_ConnectorDe~",
                table: "ConnectorDefinitionSetting");

            migrationBuilder.DropIndex(
                name: "IX_ConnectorDefinitionSetting_ConnectorDefinitionId",
                table: "ConnectorDefinitionSetting");

            migrationBuilder.DropColumn(
                name: "ConnectorDefinitionId",
                table: "ConnectorDefinitionSetting");
        }
    }
}
