using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <inheritdoc />
    public partial class AddConnectorDefinedAttributesAndFlaggedSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "FeatureFlagKey",
                table: "ConnectorDefinitionSettings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsConnectorDefined",
                table: "ConnectedSystemAttributes",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FeatureFlagKey",
                table: "ConnectorDefinitionSettings");

            migrationBuilder.DropColumn(
                name: "IsConnectorDefined",
                table: "ConnectedSystemAttributes");
        }
    }
}
