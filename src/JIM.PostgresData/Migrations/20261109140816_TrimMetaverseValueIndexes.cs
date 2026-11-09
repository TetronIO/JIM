using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <inheritdoc />
    public partial class TrimMetaverseValueIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_MetaverseObjectAttributeValues_DateTimeValue",
                table: "MetaverseObjectAttributeValues");

            migrationBuilder.DropIndex(
                name: "IX_MetaverseObjectAttributeValues_DecimalValue",
                table: "MetaverseObjectAttributeValues");

            migrationBuilder.DropIndex(
                name: "IX_MetaverseObjectAttributeValues_GuidValue",
                table: "MetaverseObjectAttributeValues");

            migrationBuilder.DropIndex(
                name: "IX_MetaverseObjectAttributeValues_IntValue",
                table: "MetaverseObjectAttributeValues");

            migrationBuilder.DropIndex(
                name: "IX_MetaverseObjectAttributeValues_LongValue",
                table: "MetaverseObjectAttributeValues");

            migrationBuilder.DropIndex(
                name: "IX_MetaverseObjectAttributeValues_ReferenceValueId",
                table: "MetaverseObjectAttributeValues");

            migrationBuilder.DropIndex(
                name: "IX_MetaverseObjectAttributeValues_StringValue",
                table: "MetaverseObjectAttributeValues");

            migrationBuilder.DropIndex(
                name: "IX_MetaverseObjectAttributeValues_UnresolvedReferenceValueId",
                table: "MetaverseObjectAttributeValues");

            migrationBuilder.CreateIndex(
                name: "IX_MetaverseObjectAttributeValues_DecimalValue",
                table: "MetaverseObjectAttributeValues",
                column: "DecimalValue",
                filter: "\"DecimalValue\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_MetaverseObjectAttributeValues_GuidValue",
                table: "MetaverseObjectAttributeValues",
                column: "GuidValue",
                filter: "\"GuidValue\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_MetaverseObjectAttributeValues_IntValue",
                table: "MetaverseObjectAttributeValues",
                column: "IntValue",
                filter: "\"IntValue\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_MetaverseObjectAttributeValues_LongValue",
                table: "MetaverseObjectAttributeValues",
                column: "LongValue",
                filter: "\"LongValue\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_MetaverseObjectAttributeValues_ReferenceValueId",
                table: "MetaverseObjectAttributeValues",
                column: "ReferenceValueId",
                filter: "\"ReferenceValueId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_MetaverseObjectAttributeValues_UnresolvedReferenceValueId",
                table: "MetaverseObjectAttributeValues",
                column: "UnresolvedReferenceValueId",
                filter: "\"UnresolvedReferenceValueId\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_MetaverseObjectAttributeValues_DecimalValue",
                table: "MetaverseObjectAttributeValues");

            migrationBuilder.DropIndex(
                name: "IX_MetaverseObjectAttributeValues_GuidValue",
                table: "MetaverseObjectAttributeValues");

            migrationBuilder.DropIndex(
                name: "IX_MetaverseObjectAttributeValues_IntValue",
                table: "MetaverseObjectAttributeValues");

            migrationBuilder.DropIndex(
                name: "IX_MetaverseObjectAttributeValues_LongValue",
                table: "MetaverseObjectAttributeValues");

            migrationBuilder.DropIndex(
                name: "IX_MetaverseObjectAttributeValues_ReferenceValueId",
                table: "MetaverseObjectAttributeValues");

            migrationBuilder.DropIndex(
                name: "IX_MetaverseObjectAttributeValues_UnresolvedReferenceValueId",
                table: "MetaverseObjectAttributeValues");

            migrationBuilder.CreateIndex(
                name: "IX_MetaverseObjectAttributeValues_DateTimeValue",
                table: "MetaverseObjectAttributeValues",
                column: "DateTimeValue");

            migrationBuilder.CreateIndex(
                name: "IX_MetaverseObjectAttributeValues_DecimalValue",
                table: "MetaverseObjectAttributeValues",
                column: "DecimalValue");

            migrationBuilder.CreateIndex(
                name: "IX_MetaverseObjectAttributeValues_GuidValue",
                table: "MetaverseObjectAttributeValues",
                column: "GuidValue");

            migrationBuilder.CreateIndex(
                name: "IX_MetaverseObjectAttributeValues_IntValue",
                table: "MetaverseObjectAttributeValues",
                column: "IntValue");

            migrationBuilder.CreateIndex(
                name: "IX_MetaverseObjectAttributeValues_LongValue",
                table: "MetaverseObjectAttributeValues",
                column: "LongValue");

            migrationBuilder.CreateIndex(
                name: "IX_MetaverseObjectAttributeValues_ReferenceValueId",
                table: "MetaverseObjectAttributeValues",
                column: "ReferenceValueId");

            migrationBuilder.CreateIndex(
                name: "IX_MetaverseObjectAttributeValues_StringValue",
                table: "MetaverseObjectAttributeValues",
                column: "StringValue");

            migrationBuilder.CreateIndex(
                name: "IX_MetaverseObjectAttributeValues_UnresolvedReferenceValueId",
                table: "MetaverseObjectAttributeValues",
                column: "UnresolvedReferenceValueId");
        }
    }
}
