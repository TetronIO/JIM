using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <inheritdoc />
    public partial class AddConnectedSystemObjectDerivedInputChangePending : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "DerivedInputChangePending",
                table: "ConnectedSystemObjects",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_ConnectedSystemObjects_ConnectedSystemId_DerivedInputChangePending",
                table: "ConnectedSystemObjects",
                column: "ConnectedSystemId",
                filter: "\"DerivedInputChangePending\"");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ConnectedSystemObjects_ConnectedSystemId_DerivedInputChangePending",
                table: "ConnectedSystemObjects");

            migrationBuilder.DropColumn(
                name: "DerivedInputChangePending",
                table: "ConnectedSystemObjects");
        }
    }
}
