using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <inheritdoc />
    public partial class PartialConnectedSystemObjectReferenceIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ConnectedSystemObjectAttributeValues_ReferenceValueId",
                table: "ConnectedSystemObjectAttributeValues");

            migrationBuilder.CreateIndex(
                name: "IX_ConnectedSystemObjectAttributeValues_ReferenceValueId",
                table: "ConnectedSystemObjectAttributeValues",
                column: "ReferenceValueId",
                filter: "\"ReferenceValueId\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ConnectedSystemObjectAttributeValues_ReferenceValueId",
                table: "ConnectedSystemObjectAttributeValues");

            migrationBuilder.CreateIndex(
                name: "IX_ConnectedSystemObjectAttributeValues_ReferenceValueId",
                table: "ConnectedSystemObjectAttributeValues",
                column: "ReferenceValueId");
        }
    }
}
