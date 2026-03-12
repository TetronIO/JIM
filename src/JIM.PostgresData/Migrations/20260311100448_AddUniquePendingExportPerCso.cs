using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <inheritdoc />
    public partial class AddUniquePendingExportPerCso : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PendingExports_ConnectedSystemObjectId",
                table: "PendingExports");

            migrationBuilder.CreateIndex(
                name: "IX_PendingExports_ConnectedSystemObjectId_Unique",
                table: "PendingExports",
                column: "ConnectedSystemObjectId",
                unique: true,
                filter: "\"ConnectedSystemObjectId\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PendingExports_ConnectedSystemObjectId_Unique",
                table: "PendingExports");

            migrationBuilder.CreateIndex(
                name: "IX_PendingExports_ConnectedSystemObjectId",
                table: "PendingExports",
                column: "ConnectedSystemObjectId");
        }
    }
}
