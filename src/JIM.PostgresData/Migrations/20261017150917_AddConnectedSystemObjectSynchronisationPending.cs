using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <inheritdoc />
    public partial class AddConnectedSystemObjectSynchronisationPending : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "SynchronisationPending",
                table: "ConnectedSystemObjects",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_ConnectedSystemObjects_ConnectedSystemId_SynchronisationPending",
                table: "ConnectedSystemObjects",
                column: "ConnectedSystemId",
                filter: "\"SynchronisationPending\"");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ConnectedSystemObjects_ConnectedSystemId_SynchronisationPending",
                table: "ConnectedSystemObjects");

            migrationBuilder.DropColumn(
                name: "SynchronisationPending",
                table: "ConnectedSystemObjects");
        }
    }
}
