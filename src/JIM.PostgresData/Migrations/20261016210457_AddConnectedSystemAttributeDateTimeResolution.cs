using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <inheritdoc />
    public partial class AddConnectedSystemAttributeDateTimeResolution : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "DateTimeResolution",
                table: "ConnectedSystemAttributes",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "DateTimeResolutionSetByAdministrator",
                table: "ConnectedSystemAttributes",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DateTimeResolution",
                table: "ConnectedSystemAttributes");

            migrationBuilder.DropColumn(
                name: "DateTimeResolutionSetByAdministrator",
                table: "ConnectedSystemAttributes");
        }
    }
}
