using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    public partial class ConnectorDefinitionNewCaps : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "SupportsUserSelectedExternalId",
                table: "ConnectorDefinitions",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "SupportsUserSeletedAttributeTypes",
                table: "ConnectorDefinitions",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SupportsUserSelectedExternalId",
                table: "ConnectorDefinitions");

            migrationBuilder.DropColumn(
                name: "SupportsUserSeletedAttributeTypes",
                table: "ConnectorDefinitions");
        }
    }
}
