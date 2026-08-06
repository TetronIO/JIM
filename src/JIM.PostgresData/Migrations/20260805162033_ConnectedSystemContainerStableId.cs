using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <inheritdoc />
    public partial class ConnectedSystemContainerStableId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "StableId",
                table: "ConnectorContainers",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StableId",
                table: "ConnectedSystemContainers",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "StableId",
                table: "ConnectorContainers");

            migrationBuilder.DropColumn(
                name: "StableId",
                table: "ConnectedSystemContainers");
        }
    }
}
