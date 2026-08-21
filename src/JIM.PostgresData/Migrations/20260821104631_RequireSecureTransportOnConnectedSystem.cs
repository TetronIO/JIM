using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <inheritdoc />
    public partial class RequireSecureTransportOnConnectedSystem : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RequireSecureTransport",
                table: "ConnectedSystemPasswordSynchronisations");

            migrationBuilder.AddColumn<bool>(
                name: "RequireSecureTransport",
                table: "ConnectedSystems",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RequireSecureTransport",
                table: "ConnectedSystems");

            migrationBuilder.AddColumn<bool>(
                name: "RequireSecureTransport",
                table: "ConnectedSystemPasswordSynchronisations",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }
    }
}
