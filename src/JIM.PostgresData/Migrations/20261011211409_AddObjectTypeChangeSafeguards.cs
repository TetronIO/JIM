using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <inheritdoc />
    public partial class AddObjectTypeChangeSafeguards : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "ExternalIdsUniqueAcrossObjectTypes",
                table: "ConnectorDefinitions",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "AllowObjectTypeChanges",
                table: "ConnectedSystemRunProfiles",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ExternalIdsUniqueAcrossObjectTypes",
                table: "ConnectorDefinitions");

            migrationBuilder.DropColumn(
                name: "AllowObjectTypeChanges",
                table: "ConnectedSystemRunProfiles");
        }
    }
}
