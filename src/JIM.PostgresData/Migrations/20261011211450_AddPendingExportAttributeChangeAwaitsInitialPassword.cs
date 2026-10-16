using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <inheritdoc />
    public partial class AddPendingExportAttributeChangeAwaitsInitialPassword : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AwaitsInitialPassword",
                table: "PendingExportAttributeValueChanges",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_PendingExportAttributeValueChanges_PendingExportId_AwaitsInitialPassword",
                table: "PendingExportAttributeValueChanges",
                column: "PendingExportId",
                filter: "\"AwaitsInitialPassword\"");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PendingExportAttributeValueChanges_PendingExportId_AwaitsInitialPassword",
                table: "PendingExportAttributeValueChanges");

            migrationBuilder.DropColumn(
                name: "AwaitsInitialPassword",
                table: "PendingExportAttributeValueChanges");
        }
    }
}
