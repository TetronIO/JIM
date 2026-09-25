using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <inheritdoc />
    public partial class CascadePendingExportAttributeValueChangeDeletes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_PendingExportAttributeValueChanges_PendingExports_PendingEx~",
                table: "PendingExportAttributeValueChanges");

            migrationBuilder.AddForeignKey(
                name: "FK_PendingExportAttributeValueChanges_PendingExports_PendingEx~",
                table: "PendingExportAttributeValueChanges",
                column: "PendingExportId",
                principalTable: "PendingExports",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_PendingExportAttributeValueChanges_PendingExports_PendingEx~",
                table: "PendingExportAttributeValueChanges");

            migrationBuilder.AddForeignKey(
                name: "FK_PendingExportAttributeValueChanges_PendingExports_PendingEx~",
                table: "PendingExportAttributeValueChanges",
                column: "PendingExportId",
                principalTable: "PendingExports",
                principalColumn: "Id");
        }
    }
}
