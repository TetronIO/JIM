using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <inheritdoc />
    public partial class AddUnresolvedReferenceObjectIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_ConnectedSystemObjectAttributeValues_CsoId_Unresolved",
                table: "ConnectedSystemObjectAttributeValues",
                column: "ConnectedSystemObjectId",
                filter: "\"UnresolvedReferenceValue\" IS NOT NULL AND \"ReferenceValueId\" IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ConnectedSystemObjectAttributeValues_CsoId_Unresolved",
                table: "ConnectedSystemObjectAttributeValues");
        }
    }
}
