using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <inheritdoc />
    public partial class AddPerformanceIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PendingExports_ConnectedSystemId",
                table: "PendingExports");

            migrationBuilder.DropIndex(
                name: "IX_MetaverseObjectAttributeValues_AttributeId",
                table: "MetaverseObjectAttributeValues");

            migrationBuilder.DropIndex(
                name: "IX_ConnectedSystemObjects_ConnectedSystemId",
                table: "ConnectedSystemObjects");

            migrationBuilder.DropIndex(
                name: "IX_ConnectedSystemObjectAttributeValues_ConnectedSystemObjectId",
                table: "ConnectedSystemObjectAttributeValues");

            migrationBuilder.CreateIndex(
                name: "IX_PendingExports_ConnectedSystemId_Status",
                table: "PendingExports",
                columns: new[] { "ConnectedSystemId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_MetaverseObjectAttributeValues_AttributeId_StringValue",
                table: "MetaverseObjectAttributeValues",
                columns: new[] { "AttributeId", "StringValue" });

            migrationBuilder.CreateIndex(
                name: "IX_ConnectedSystemObjects_ConnectedSystemId_TypeId",
                table: "ConnectedSystemObjects",
                columns: new[] { "ConnectedSystemId", "TypeId" });

            migrationBuilder.CreateIndex(
                name: "IX_ConnectedSystemObjectAttributeValues_CsoId_AttributeId",
                table: "ConnectedSystemObjectAttributeValues",
                columns: new[] { "ConnectedSystemObjectId", "AttributeId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PendingExports_ConnectedSystemId_Status",
                table: "PendingExports");

            migrationBuilder.DropIndex(
                name: "IX_MetaverseObjectAttributeValues_AttributeId_StringValue",
                table: "MetaverseObjectAttributeValues");

            migrationBuilder.DropIndex(
                name: "IX_ConnectedSystemObjects_ConnectedSystemId_TypeId",
                table: "ConnectedSystemObjects");

            migrationBuilder.DropIndex(
                name: "IX_ConnectedSystemObjectAttributeValues_CsoId_AttributeId",
                table: "ConnectedSystemObjectAttributeValues");

            migrationBuilder.CreateIndex(
                name: "IX_PendingExports_ConnectedSystemId",
                table: "PendingExports",
                column: "ConnectedSystemId");

            migrationBuilder.CreateIndex(
                name: "IX_MetaverseObjectAttributeValues_AttributeId",
                table: "MetaverseObjectAttributeValues",
                column: "AttributeId");

            migrationBuilder.CreateIndex(
                name: "IX_ConnectedSystemObjects_ConnectedSystemId",
                table: "ConnectedSystemObjects",
                column: "ConnectedSystemId");

            migrationBuilder.CreateIndex(
                name: "IX_ConnectedSystemObjectAttributeValues_ConnectedSystemObjectId",
                table: "ConnectedSystemObjectAttributeValues",
                column: "ConnectedSystemObjectId");
        }
    }
}
