using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <inheritdoc />
    public partial class AddFunctionalIndexesForCrossBatchReferenceFixup : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Functional index on LOWER(StringValue) for secondary external ID lookups.
            // FixupCrossBatchReferenceIdsAsync uses LOWER() for case-insensitive DN matching
            // (RFC 4514). Without this index, the JOIN scans the full attribute values table.
            // With 294K+ unresolved references, this reduces the fixup from 300+ seconds to ~6 seconds.
            migrationBuilder.Sql("""
                CREATE INDEX CONCURRENTLY IF NOT EXISTS "IX_CsoAttrVal_Lower_StringValue"
                ON "ConnectedSystemObjectAttributeValues" (LOWER("StringValue"))
                WHERE "StringValue" IS NOT NULL
                """);

            // Functional index on LOWER(UnresolvedReferenceValue) for the source side of the fixup.
            // Only includes rows that actually need resolution (ReferenceValueId IS NULL).
            migrationBuilder.Sql("""
                CREATE INDEX CONCURRENTLY IF NOT EXISTS "IX_CsoAttrVal_Lower_UnresolvedRef"
                ON "ConnectedSystemObjectAttributeValues" (LOWER("UnresolvedReferenceValue"))
                WHERE "UnresolvedReferenceValue" IS NOT NULL AND "ReferenceValueId" IS NULL
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""DROP INDEX IF EXISTS "IX_CsoAttrVal_Lower_StringValue" """);
            migrationBuilder.Sql("""DROP INDEX IF EXISTS "IX_CsoAttrVal_Lower_UnresolvedRef" """);
        }
    }
}
