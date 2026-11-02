using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <inheritdoc />
    public partial class AddPendingExportAttributeChangeAwaitsExchange : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AwaitsExchange",
                table: "PendingExportAttributeValueChanges",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_PendingExportAttributeValueChanges_PendingExportId_AwaitsExchange",
                table: "PendingExportAttributeValueChanges",
                column: "PendingExportId",
                filter: "\"AwaitsExchange\"");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PendingExportAttributeValueChanges_PendingExportId_AwaitsExchange",
                table: "PendingExportAttributeValueChanges");

            migrationBuilder.DropColumn(
                name: "AwaitsExchange",
                table: "PendingExportAttributeValueChanges");
        }
    }
}
