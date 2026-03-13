using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <inheritdoc />
    public partial class AddUnresolvedReferenceValueIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ConnectedSystemObjectAttributeValues_AttributeId",
                table: "ConnectedSystemObjectAttributeValues");

            migrationBuilder.CreateIndex(
                name: "IX_ConnectedSystemObjectAttributeValues_AttributeId_StringValue",
                table: "ConnectedSystemObjectAttributeValues",
                columns: new[] { "AttributeId", "StringValue" },
                filter: "\"StringValue\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ConnectedSystemObjectAttributeValues_UnresolvedReferenceValue",
                table: "ConnectedSystemObjectAttributeValues",
                column: "UnresolvedReferenceValue",
                filter: "\"UnresolvedReferenceValue\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ConnectedSystemObjectAttributeValues_AttributeId_StringValue",
                table: "ConnectedSystemObjectAttributeValues");

            migrationBuilder.DropIndex(
                name: "IX_ConnectedSystemObjectAttributeValues_UnresolvedReferenceValue",
                table: "ConnectedSystemObjectAttributeValues");

            migrationBuilder.CreateIndex(
                name: "IX_ConnectedSystemObjectAttributeValues_AttributeId",
                table: "ConnectedSystemObjectAttributeValues",
                column: "AttributeId");
        }
    }
}
