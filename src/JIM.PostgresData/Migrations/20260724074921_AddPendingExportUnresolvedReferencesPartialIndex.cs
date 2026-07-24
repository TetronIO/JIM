using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <inheritdoc />
    public partial class AddPendingExportUnresolvedReferencesPartialIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_PendingExports_ConnectedSystemId_HasUnresolvedReferences",
                table: "PendingExports",
                column: "ConnectedSystemId",
                filter: "\"HasUnresolvedReferences\"");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PendingExports_ConnectedSystemId_HasUnresolvedReferences",
                table: "PendingExports");
        }
    }
}
