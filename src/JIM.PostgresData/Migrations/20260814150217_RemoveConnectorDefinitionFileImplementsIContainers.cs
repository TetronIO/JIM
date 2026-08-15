using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <inheritdoc />
    public partial class RemoveConnectorDefinitionFileImplementsIContainers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ImplementsIContainers",
                table: "ConnectorDefinitionFiles");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "ImplementsIContainers",
                table: "ConnectorDefinitionFiles",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }
    }
}
