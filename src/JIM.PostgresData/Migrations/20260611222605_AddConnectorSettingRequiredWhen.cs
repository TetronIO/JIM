using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <inheritdoc />
    public partial class AddConnectorSettingRequiredWhen : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "RequiredWhenSetting",
                table: "ConnectorDefinitionSettings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RequiredWhenValue",
                table: "ConnectorDefinitionSettings",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RequiredWhenSetting",
                table: "ConnectorDefinitionSettings");

            migrationBuilder.DropColumn(
                name: "RequiredWhenValue",
                table: "ConnectorDefinitionSettings");
        }
    }
}
