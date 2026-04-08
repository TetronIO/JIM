using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <inheritdoc />
    public partial class AddCompositeIndexForMvoAttributeValues : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_MetaverseObjectAttributeValues_MetaverseObjectId",
                table: "MetaverseObjectAttributeValues");

            migrationBuilder.CreateIndex(
                name: "IX_MetaverseObjectAttributeValues_MvoId_AttributeId",
                table: "MetaverseObjectAttributeValues",
                columns: new[] { "MetaverseObjectId", "AttributeId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_MetaverseObjectAttributeValues_MvoId_AttributeId",
                table: "MetaverseObjectAttributeValues");

            migrationBuilder.CreateIndex(
                name: "IX_MetaverseObjectAttributeValues_MetaverseObjectId",
                table: "MetaverseObjectAttributeValues",
                column: "MetaverseObjectId");
        }
    }
}
