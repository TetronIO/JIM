using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <summary>
    /// One-off clean-up for #1019: deleting a Metaverse Object used to null the ReferenceValueId on
    /// surviving objects' reference attribute rows instead of deleting them, leaving informationless
    /// "ghost" rows that rendered as blank entries in member lists, inflated member counts, and staged
    /// all-null attribute changes on later exports. Deletion now removes such rows at source
    /// (MetaverseReferenceRowCleanup); this migration removes the ones already left behind.
    /// A ghost row is a reference-typed attribute row with no reference, no staged unresolved
    /// reference, no payload in any value column, and not an asserted-null marker; the predicate must
    /// stay in step with MetaverseObjectAttributeValue.IsValuelessReferenceRow(). Deletes in 50,000
    /// row batches with a commit per batch (the DO block runs outside the migration transaction) so
    /// large historical accumulations never hold one giant transaction.
    /// </summary>
    public partial class DeleteGhostMetaverseReferenceAttributeValues : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$
                DECLARE
                    deleted_count integer;
                    total_deleted bigint := 0;
                BEGIN
                    LOOP
                        DELETE FROM "MetaverseObjectAttributeValues"
                        WHERE ctid IN (
                            SELECT av.ctid
                            FROM "MetaverseObjectAttributeValues" av
                            JOIN "MetaverseAttributes" a ON a."Id" = av."AttributeId"
                            WHERE a."Type" = 5 -- AttributeDataType.Reference
                              AND av."ReferenceValueId" IS NULL
                              AND av."UnresolvedReferenceValueId" IS NULL
                              AND av."StringValue" IS NULL
                              AND av."DateTimeValue" IS NULL
                              AND av."IntValue" IS NULL
                              AND av."LongValue" IS NULL
                              AND av."ByteValue" IS NULL
                              AND av."GuidValue" IS NULL
                              AND av."BoolValue" IS NULL
                              AND av."NullValue" = false
                            LIMIT 50000
                        );
                        GET DIAGNOSTICS deleted_count = ROW_COUNT;
                        total_deleted := total_deleted + deleted_count;
                        EXIT WHEN deleted_count = 0;
                        COMMIT;
                    END LOOP;
                    RAISE NOTICE 'DeleteGhostMetaverseReferenceAttributeValues: removed % ghost reference attribute value row(s)', total_deleted;
                END $$;
                """, suppressTransaction: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Irreversible data clean-up: the deleted rows carried no information (that is the
            // definition of a ghost row), so there is nothing to restore.
        }
    }
}
