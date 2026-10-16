using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <summary>
    /// Data-only repair of history that recorded a change made with an API key as a change made by a person (#2066).
    /// <para>
    /// <b>Why the rows are wrong.</b> REST calls to the Schedules, Example Data Template and factory reset endpoints
    /// resolved their initiator by looking for an API key id in <c>HttpContext.Items</c>, which nothing ever set, so
    /// every call made with an API key fell through to the person branch. That branch read the caller's id from the
    /// <c>NameIdentifier</c> claim, which for an API key is the key's own id, and its name from the <c>Name</c> claim,
    /// which is the key's name. Each such row therefore holds (User, the key's id, the key's name): only the type is
    /// wrong. The rows were written directly (a Schedule's and its steps' audit fields, an Example Data Template's,
    /// the Activity recording each change, the factory reset Activity) and by everything a Schedule run started that
    /// way went on to record: its Schedule Execution, its Worker Tasks, each step's Activity, the Metaverse Object and
    /// Connected System Object change history those steps wrote, the Metaverse Objects they marked for deletion, and
    /// the Connected Systems whose new containers an export step auto-selected. Affected releases: Schedules in every
    /// release from v0.3.0 (the earliest tag checked) to v0.16.0, the factory reset from v0.12.0 to v0.16.0, and
    /// Example Data Templates in v0.16.0. #2147 resolved the initiator from the claims the authentication pipeline
    /// issues, so no new row is written this way; this migration repairs the rows written before it.
    /// </para>
    /// <para>
    /// <b>Why the evidence is certain.</b> A row is changed only when its type is User and its id is one JIM can prove
    /// belonged to an API key: a key that still exists, the target of an API key configuration change
    /// (<c>Activities.ApiKeyId</c>), the key named by an API key Activity's configuration snapshot (the tombstone a
    /// key's deletion records), or an id recorded correctly as an API key (type 2) in any of the schema's 40 initiator
    /// columns. API key ids and Metaverse Object ids are independently generated GUIDs, so a person's own row cannot
    /// match; and a key's id is never a person's, so the type is the only thing to correct.
    /// </para>
    /// <para>
    /// <b>What changes.</b> The type becomes ApiKey (2). The id and name are left exactly as recorded: the id is
    /// already the key's, and the name is the key's name at the time, as every denormalised initiator name is. One
    /// derived column follows the type: a Metaverse Object change whose mechanism was derived from the User type
    /// (<c>ChangeInitiatorType</c> User, 1, shown as a direct edit) gets the NotSet (0) a correctly recorded API key
    /// change carries; any other mechanism is left alone.
    /// </para>
    /// <para>
    /// <b>Known limitation.</b> A factory reset made with an ordinary (not infrastructure) API key deletes that key and
    /// every Activity, with raw SQL, before recording the reset Activity. Unless the key also left its id, recorded
    /// correctly as an API key, on configuration the reset kept (a built-in object it changed, say), nothing proves the
    /// id was an API key's, so that reset Activity stays as recorded, as does any other row whose key was deleted
    /// without trace.
    /// </para>
    /// <para>
    /// Runs as one statement inside the migration's transaction, so the repair is all or nothing, and reports each
    /// table's count as a notice. It is idempotent: a corrected row is no longer typed User. These are targeted
    /// fix-ups of initiator columns, not an entity write path, so the bulk column list rule does not apply.
    /// </para>
    /// </summary>
    public partial class RepairApiKeyInitiatorsRecordedAsUser : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$
                DECLARE
                    affected integer;
                BEGIN
                    -- Every id JIM can prove belonged to an API key. ActivityInitiatorType: 1 = User, 2 = ApiKey;
                    -- ActivityTargetType 15 = ApiKey.
                    CREATE TEMP TABLE api_key_ids (id uuid PRIMARY KEY) ON COMMIT DROP;

                    INSERT INTO api_key_ids (id)
                    SELECT evidence.id
                    FROM (
                        SELECT "Id" AS id FROM "ApiKeys"
                        UNION SELECT "ApiKeyId" FROM "Activities" WHERE "ApiKeyId" IS NOT NULL
                        UNION SELECT CASE
                                         WHEN ("ConfigurationChangeSnapshot" ->> 'objectGuidId')
                                              ~* '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$'
                                         THEN ("ConfigurationChangeSnapshot" ->> 'objectGuidId')::uuid
                                     END
                              FROM "Activities"
                              WHERE "TargetType" = 15
                                AND "ConfigurationChangeSnapshot" ->> 'objectType' = 'ApiKey'
                        UNION SELECT "InitiatedById" FROM "Activities" WHERE "InitiatedByType" = 2
                        UNION SELECT "CreatedById" FROM "ApiKeys" WHERE "CreatedByType" = 2
                        UNION SELECT "LastUpdatedById" FROM "ApiKeys" WHERE "LastUpdatedByType" = 2
                        UNION SELECT "InitiatedById" FROM "ConnectedSystemObjectChanges" WHERE "InitiatedByType" = 2
                        UNION SELECT "CreatedById" FROM "ConnectedSystemRunProfiles" WHERE "CreatedByType" = 2
                        UNION SELECT "LastUpdatedById" FROM "ConnectedSystemRunProfiles" WHERE "LastUpdatedByType" = 2
                        UNION SELECT "CreatedById" FROM "ConnectedSystems" WHERE "CreatedByType" = 2
                        UNION SELECT "LastUpdatedById" FROM "ConnectedSystems" WHERE "LastUpdatedByType" = 2
                        UNION SELECT "CreatedById" FROM "ConnectorDefinitions" WHERE "CreatedByType" = 2
                        UNION SELECT "LastUpdatedById" FROM "ConnectorDefinitions" WHERE "LastUpdatedByType" = 2
                        UNION SELECT "CreatedById" FROM "ExampleDataSets" WHERE "CreatedByType" = 2
                        UNION SELECT "LastUpdatedById" FROM "ExampleDataSets" WHERE "LastUpdatedByType" = 2
                        UNION SELECT "CreatedById" FROM "ExampleDataTemplates" WHERE "CreatedByType" = 2
                        UNION SELECT "LastUpdatedById" FROM "ExampleDataTemplates" WHERE "LastUpdatedByType" = 2
                        UNION SELECT "CreatedById" FROM "MetaverseAttributes" WHERE "CreatedByType" = 2
                        UNION SELECT "LastUpdatedById" FROM "MetaverseAttributes" WHERE "LastUpdatedByType" = 2
                        UNION SELECT "InitiatedById" FROM "MetaverseObjectChanges" WHERE "InitiatedByType" = 2
                        UNION SELECT "CreatedById" FROM "MetaverseObjectTypes" WHERE "CreatedByType" = 2
                        UNION SELECT "LastUpdatedById" FROM "MetaverseObjectTypes" WHERE "LastUpdatedByType" = 2
                        UNION SELECT "DeletionInitiatedById" FROM "MetaverseObjects" WHERE "DeletionInitiatedByType" = 2
                        UNION SELECT "CreatedById" FROM "ObjectMatchingRules" WHERE "CreatedByType" = 2
                        UNION SELECT "LastUpdatedById" FROM "ObjectMatchingRules" WHERE "LastUpdatedByType" = 2
                        UNION SELECT "CreatedById" FROM "PredefinedSearches" WHERE "CreatedByType" = 2
                        UNION SELECT "LastUpdatedById" FROM "PredefinedSearches" WHERE "LastUpdatedByType" = 2
                        UNION SELECT "CreatedById" FROM "Roles" WHERE "CreatedByType" = 2
                        UNION SELECT "LastUpdatedById" FROM "Roles" WHERE "LastUpdatedByType" = 2
                        UNION SELECT "InitiatedById" FROM "ScheduleExecutions" WHERE "InitiatedByType" = 2
                        UNION SELECT "CreatedById" FROM "ScheduleSteps" WHERE "CreatedByType" = 2
                        UNION SELECT "LastUpdatedById" FROM "ScheduleSteps" WHERE "LastUpdatedByType" = 2
                        UNION SELECT "CreatedById" FROM "Schedules" WHERE "CreatedByType" = 2
                        UNION SELECT "LastUpdatedById" FROM "Schedules" WHERE "LastUpdatedByType" = 2
                        UNION SELECT "CreatedById" FROM "ServiceSettingItems" WHERE "CreatedByType" = 2
                        UNION SELECT "LastUpdatedById" FROM "ServiceSettingItems" WHERE "LastUpdatedByType" = 2
                        UNION SELECT "CreatedById" FROM "SyncRuleMappings" WHERE "CreatedByType" = 2
                        UNION SELECT "LastUpdatedById" FROM "SyncRuleMappings" WHERE "LastUpdatedByType" = 2
                        UNION SELECT "CreatedById" FROM "SyncRules" WHERE "CreatedByType" = 2
                        UNION SELECT "LastUpdatedById" FROM "SyncRules" WHERE "LastUpdatedByType" = 2
                        UNION SELECT "CreatedById" FROM "TrustedCertificates" WHERE "CreatedByType" = 2
                        UNION SELECT "LastUpdatedById" FROM "TrustedCertificates" WHERE "LastUpdatedByType" = 2
                        UNION SELECT "InitiatedById" FROM "WorkerTasks" WHERE "InitiatedByType" = 2
                    ) AS evidence
                    WHERE evidence.id IS NOT NULL;

                    GET DIAGNOSTICS affected = ROW_COUNT;
                    RAISE NOTICE 'RepairApiKeyInitiatorsRecordedAsUser: % API key id(s) proven', affected;
                    ANALYZE api_key_ids;

                    -- Every initiator column the affected endpoints, and the Schedule runs they started, wrote to.
                    UPDATE "Activities" SET "InitiatedByType" = 2
                    WHERE "InitiatedByType" = 1 AND "InitiatedById" IN (SELECT id FROM api_key_ids);
                    GET DIAGNOSTICS affected = ROW_COUNT;
                    RAISE NOTICE 'RepairApiKeyInitiatorsRecordedAsUser: % row(s) in Activities.InitiatedBy', affected;

                    UPDATE "Schedules" SET "CreatedByType" = 2
                    WHERE "CreatedByType" = 1 AND "CreatedById" IN (SELECT id FROM api_key_ids);
                    GET DIAGNOSTICS affected = ROW_COUNT;
                    RAISE NOTICE 'RepairApiKeyInitiatorsRecordedAsUser: % row(s) in Schedules.CreatedBy', affected;

                    UPDATE "Schedules" SET "LastUpdatedByType" = 2
                    WHERE "LastUpdatedByType" = 1 AND "LastUpdatedById" IN (SELECT id FROM api_key_ids);
                    GET DIAGNOSTICS affected = ROW_COUNT;
                    RAISE NOTICE 'RepairApiKeyInitiatorsRecordedAsUser: % row(s) in Schedules.LastUpdatedBy', affected;

                    UPDATE "ScheduleSteps" SET "CreatedByType" = 2
                    WHERE "CreatedByType" = 1 AND "CreatedById" IN (SELECT id FROM api_key_ids);
                    GET DIAGNOSTICS affected = ROW_COUNT;
                    RAISE NOTICE 'RepairApiKeyInitiatorsRecordedAsUser: % row(s) in ScheduleSteps.CreatedBy', affected;

                    UPDATE "ScheduleSteps" SET "LastUpdatedByType" = 2
                    WHERE "LastUpdatedByType" = 1 AND "LastUpdatedById" IN (SELECT id FROM api_key_ids);
                    GET DIAGNOSTICS affected = ROW_COUNT;
                    RAISE NOTICE 'RepairApiKeyInitiatorsRecordedAsUser: % row(s) in ScheduleSteps.LastUpdatedBy', affected;

                    UPDATE "ScheduleExecutions" SET "InitiatedByType" = 2
                    WHERE "InitiatedByType" = 1 AND "InitiatedById" IN (SELECT id FROM api_key_ids);
                    GET DIAGNOSTICS affected = ROW_COUNT;
                    RAISE NOTICE 'RepairApiKeyInitiatorsRecordedAsUser: % row(s) in ScheduleExecutions.InitiatedBy', affected;

                    UPDATE "ExampleDataTemplates" SET "CreatedByType" = 2
                    WHERE "CreatedByType" = 1 AND "CreatedById" IN (SELECT id FROM api_key_ids);
                    GET DIAGNOSTICS affected = ROW_COUNT;
                    RAISE NOTICE 'RepairApiKeyInitiatorsRecordedAsUser: % row(s) in ExampleDataTemplates.CreatedBy', affected;

                    UPDATE "ExampleDataTemplates" SET "LastUpdatedByType" = 2
                    WHERE "LastUpdatedByType" = 1 AND "LastUpdatedById" IN (SELECT id FROM api_key_ids);
                    GET DIAGNOSTICS affected = ROW_COUNT;
                    RAISE NOTICE 'RepairApiKeyInitiatorsRecordedAsUser: % row(s) in ExampleDataTemplates.LastUpdatedBy', affected;

                    UPDATE "WorkerTasks" SET "InitiatedByType" = 2
                    WHERE "InitiatedByType" = 1 AND "InitiatedById" IN (SELECT id FROM api_key_ids);
                    GET DIAGNOSTICS affected = ROW_COUNT;
                    RAISE NOTICE 'RepairApiKeyInitiatorsRecordedAsUser: % row(s) in WorkerTasks.InitiatedBy', affected;

                    -- MetaverseObjectChangeInitiatorType: 0 = NotSet, 1 = User. Only a mechanism derived from the User
                    -- type changes; any other (a Synchronisation Rule's, say) was never derived from it.
                    UPDATE "MetaverseObjectChanges"
                    SET "InitiatedByType" = 2,
                        "ChangeInitiatorType" = CASE WHEN "ChangeInitiatorType" = 1 THEN 0 ELSE "ChangeInitiatorType" END
                    WHERE "InitiatedByType" = 1 AND "InitiatedById" IN (SELECT id FROM api_key_ids);
                    GET DIAGNOSTICS affected = ROW_COUNT;
                    RAISE NOTICE 'RepairApiKeyInitiatorsRecordedAsUser: % row(s) in MetaverseObjectChanges.InitiatedBy', affected;

                    UPDATE "ConnectedSystemObjectChanges" SET "InitiatedByType" = 2
                    WHERE "InitiatedByType" = 1 AND "InitiatedById" IN (SELECT id FROM api_key_ids);
                    GET DIAGNOSTICS affected = ROW_COUNT;
                    RAISE NOTICE 'RepairApiKeyInitiatorsRecordedAsUser: % row(s) in ConnectedSystemObjectChanges.InitiatedBy', affected;

                    UPDATE "MetaverseObjects" SET "DeletionInitiatedByType" = 2
                    WHERE "DeletionInitiatedByType" = 1 AND "DeletionInitiatedById" IN (SELECT id FROM api_key_ids);
                    GET DIAGNOSTICS affected = ROW_COUNT;
                    RAISE NOTICE 'RepairApiKeyInitiatorsRecordedAsUser: % row(s) in MetaverseObjects.DeletionInitiatedBy', affected;

                    UPDATE "ConnectedSystems" SET "LastUpdatedByType" = 2
                    WHERE "LastUpdatedByType" = 1 AND "LastUpdatedById" IN (SELECT id FROM api_key_ids);
                    GET DIAGNOSTICS affected = ROW_COUNT;
                    RAISE NOTICE 'RepairApiKeyInitiatorsRecordedAsUser: % row(s) in ConnectedSystems.LastUpdatedBy', affected;
                END $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Deliberately empty: a repaired row cannot be told apart from one recorded correctly as an API key, and
            // restoring the User type would put back the very fault this migration corrects.
        }
    }
}
