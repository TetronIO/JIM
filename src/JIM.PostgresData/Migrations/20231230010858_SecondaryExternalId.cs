using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    public partial class SecondaryExternalId : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "SupportsSecondaryExternalId",
                table: "ConnectorDefinitions",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "SecondaryExternalIdAttributeId",
                table: "ConnectedSystemObjects",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsSecondaryExternalId",
                table: "ConnectedSystemAttributes",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SupportsSecondaryExternalId",
                table: "ConnectorDefinitions");

            migrationBuilder.DropColumn(
                name: "SecondaryExternalIdAttributeId",
                table: "ConnectedSystemObjects");

            migrationBuilder.DropColumn(
                name: "IsSecondaryExternalId",
                table: "ConnectedSystemAttributes");
        }
    }
}
