using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <inheritdoc />
    public partial class AddConnectedSystemObjectAttributeValueTypedValueIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_ConnectedSystemObjectAttributeValues_AttributeId_DecimalValue",
                table: "ConnectedSystemObjectAttributeValues",
                columns: new[] { "AttributeId", "DecimalValue" },
                filter: "\"DecimalValue\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ConnectedSystemObjectAttributeValues_AttributeId_GuidValue",
                table: "ConnectedSystemObjectAttributeValues",
                columns: new[] { "AttributeId", "GuidValue" },
                filter: "\"GuidValue\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ConnectedSystemObjectAttributeValues_AttributeId_IntValue",
                table: "ConnectedSystemObjectAttributeValues",
                columns: new[] { "AttributeId", "IntValue" },
                filter: "\"IntValue\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ConnectedSystemObjectAttributeValues_AttributeId_LongValue",
                table: "ConnectedSystemObjectAttributeValues",
                columns: new[] { "AttributeId", "LongValue" },
                filter: "\"LongValue\" IS NOT NULL");

            // #2705: statistics on lower("StringValue"), so the planner can estimate a lookup that compares it. The
            // (AttributeId, LOWER("StringValue")) index is partial, and PostgreSQL does not use a partial index's
            // expression statistics for estimates, so without these it assumes every lowered value is shared by 1 row in
            // 200: a Delta Import's per-page External ID lookup was then costed at thousands of rows per value, planned
            // as a hash join over every Connected System Object and compiled with JIT (220 ms for 500 values at 500,000
            // objects, against 5 ms with these). Expression statistics are available from PostgreSQL 14.
            migrationBuilder.Sql(
                "CREATE STATISTICS \"ST_ConnectedSystemObjectAttributeValues_LowerStringValue\" " +
                "ON (LOWER(\"StringValue\")) FROM \"ConnectedSystemObjectAttributeValues\";");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP STATISTICS IF EXISTS \"ST_ConnectedSystemObjectAttributeValues_LowerStringValue\";");

            migrationBuilder.DropIndex(
                name: "IX_ConnectedSystemObjectAttributeValues_AttributeId_DecimalValue",
                table: "ConnectedSystemObjectAttributeValues");

            migrationBuilder.DropIndex(
                name: "IX_ConnectedSystemObjectAttributeValues_AttributeId_GuidValue",
                table: "ConnectedSystemObjectAttributeValues");

            migrationBuilder.DropIndex(
                name: "IX_ConnectedSystemObjectAttributeValues_AttributeId_IntValue",
                table: "ConnectedSystemObjectAttributeValues");

            migrationBuilder.DropIndex(
                name: "IX_ConnectedSystemObjectAttributeValues_AttributeId_LongValue",
                table: "ConnectedSystemObjectAttributeValues");
        }
    }
}
