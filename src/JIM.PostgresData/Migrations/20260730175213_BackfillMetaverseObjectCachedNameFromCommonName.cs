using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <summary>
    /// One-off backfill for the shared object naming policy (JIM.Models.Core.ObjectNaming).
    /// "CachedDisplayName" is the denormalised sort key behind the Metaverse list, its ORDER BY and
    /// change-history reference labels; those paths read the column without materialising attribute
    /// values. It now caches the first present value from the ordered Metaverse naming attributes
    /// (Display Name, then Common Name) rather than Display Name alone, so Group objects carrying only
    /// a Common Name are named consistently everywhere instead of resolving on a detail page but
    /// reading as unnamed on the list.
    /// Application code maintains the column going forward (MetaverseServer, SyncEngine,
    /// ExampleDataServer); this migration repairs rows written before the widening, i.e. those with no
    /// cached value but with a Common Name. Rows that already have a cached value are left alone.
    /// Updates in 50,000 row batches with a commit per batch (the DO block runs outside the migration
    /// transaction) so large existing datasets never hold one giant transaction.
    /// </summary>
    public partial class BackfillMetaverseObjectCachedNameFromCommonName : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$
                DECLARE
                    updated_count integer;
                    total_updated bigint := 0;
                    common_name_attribute_id integer;
                BEGIN
                    SELECT "Id" INTO common_name_attribute_id
                    FROM "MetaverseAttributes"
                    WHERE "Name" = 'Common Name'
                    LIMIT 1;

                    IF common_name_attribute_id IS NULL THEN
                        RAISE NOTICE 'BackfillMetaverseObjectCachedNameFromCommonName: no Common Name attribute present; nothing to backfill';
                        RETURN;
                    END IF;

                    LOOP
                        UPDATE "MetaverseObjects" m
                        SET "CachedDisplayName" = cn."StringValue"
                        FROM "MetaverseObjectAttributeValues" cn
                        WHERE cn."MetaverseObjectId" = m."Id"
                          AND cn."AttributeId" = common_name_attribute_id
                          AND cn."StringValue" IS NOT NULL
                          AND btrim(cn."StringValue") <> ''
                          AND m."CachedDisplayName" IS NULL
                          AND m."Id" IN (
                              SELECT m2."Id"
                              FROM "MetaverseObjects" m2
                              JOIN "MetaverseObjectAttributeValues" cn2
                                ON cn2."MetaverseObjectId" = m2."Id"
                               AND cn2."AttributeId" = common_name_attribute_id
                               AND cn2."StringValue" IS NOT NULL
                               AND btrim(cn2."StringValue") <> ''
                              WHERE m2."CachedDisplayName" IS NULL
                              LIMIT 50000
                          );
                        GET DIAGNOSTICS updated_count = ROW_COUNT;
                        total_updated := total_updated + updated_count;
                        EXIT WHEN updated_count = 0;
                        COMMIT;
                    END LOOP;

                    RAISE NOTICE 'BackfillMetaverseObjectCachedNameFromCommonName: backfilled % Metaverse Object(s) from Common Name', total_updated;
                END $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Deliberately empty: the backfill writes values that are correct under the current naming
            // policy and indistinguishable from values the application would itself have written, so
            // there is nothing safe or meaningful to reverse.
        }
    }
}
