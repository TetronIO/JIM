using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <inheritdoc />
    public partial class AddConnectorDefinitionIdentifier : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Identifier",
                table: "ConnectorDefinitions",
                type: "text",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ConnectorDefinitions_Identifier",
                table: "ConnectorDefinitions",
                column: "Identifier",
                unique: true,
                filter: "\"Identifier\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ConnectorDefinitions_Identifier",
                table: "ConnectorDefinitions");

            migrationBuilder.DropColumn(
                name: "Identifier",
                table: "ConnectorDefinitions");
        }
    }
}
