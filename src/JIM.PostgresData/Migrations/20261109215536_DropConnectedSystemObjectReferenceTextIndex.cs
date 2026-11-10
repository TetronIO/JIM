using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <inheritdoc />
    public partial class DropConnectedSystemObjectReferenceTextIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ConnectedSystemObjectAttributeValues_UnresolvedReferenceValue",
                table: "ConnectedSystemObjectAttributeValues");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_ConnectedSystemObjectAttributeValues_UnresolvedReferenceValue",
                table: "ConnectedSystemObjectAttributeValues",
                column: "UnresolvedReferenceValue",
                filter: "\"UnresolvedReferenceValue\" IS NOT NULL");
        }
    }
}
