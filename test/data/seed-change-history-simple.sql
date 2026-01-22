-- ============================================================================
-- JIM Change History Test Data - SIMPLE VERSION
-- ============================================================================
-- Creates minimal test data for the Change History UI
-- Run with: docker compose exec -T jim.database psql -U jim -d jim < test/data/seed-change-history-simple.sql
-- ============================================================================

DO $$
DECLARE
    mvo_id UUID;
    change1_id UUID;
    change2_id UUID;
    attr_id INT;
    attr_change_id UUID;
BEGIN
    RAISE NOTICE 'Starting simple change history seed...';

    -- Find an existing MVO (any type)
    SELECT "Id" INTO mvo_id FROM "MetaverseObjects" LIMIT 1;

    IF mvo_id IS NULL THEN
        RAISE NOTICE 'No MVOs found. Please run an integration test first or import some data.';
        RETURN;
    END IF;

    RAISE NOTICE 'Found MVO: %', mvo_id;

    -- Find an existing text attribute
    SELECT "Id" INTO attr_id FROM "MetaverseAttributes" WHERE "Type" = 1 LIMIT 1;

    IF attr_id IS NULL THEN
        RAISE NOTICE 'No attributes found. Creating test attribute...';
        INSERT INTO "MetaverseAttributes" ("Name", "Type", "AttributePlurality", "BuiltIn", "Created")
        VALUES ('TestAttribute', 1, 1, false, NOW())
        RETURNING "Id" INTO attr_id;
    END IF;

    -- Create Change 1: Update (5 days ago)
    change1_id := gen_random_uuid();
    INSERT INTO "MetaverseObjectChanges" ("Id", "MetaverseObjectId", "ChangeType", "ChangeTime", "ChangeInitiatorType")
    VALUES (change1_id, mvo_id, 2, NOW() - INTERVAL '5 days', 2);

    attr_change_id := gen_random_uuid();
    INSERT INTO "MetaverseObjectChangeAttributes" ("Id", "MetaverseObjectChangeId", "AttributeId")
    VALUES (attr_change_id, change1_id, attr_id);

    INSERT INTO "MetaverseObjectChangeAttributeValues" ("Id", "MetaverseObjectChangeAttributeId", "ValueChangeType", "StringValue")
    VALUES (gen_random_uuid(), attr_change_id, 2, 'Old Value');

    INSERT INTO "MetaverseObjectChangeAttributeValues" ("Id", "MetaverseObjectChangeAttributeId", "ValueChangeType", "StringValue")
    VALUES (gen_random_uuid(), attr_change_id, 1, 'New Value');

    -- Create Change 2: Update (2 days ago)
    change2_id := gen_random_uuid();
    INSERT INTO "MetaverseObjectChanges" ("Id", "MetaverseObjectId", "ChangeType", "ChangeTime", "ChangeInitiatorType")
    VALUES (change2_id, mvo_id, 2, NOW() - INTERVAL '2 days', 2);

    attr_change_id := gen_random_uuid();
    INSERT INTO "MetaverseObjectChangeAttributes" ("Id", "MetaverseObjectChangeId", "AttributeId")
    VALUES (attr_change_id, change2_id, attr_id);

    INSERT INTO "MetaverseObjectChangeAttributeValues" ("Id", "MetaverseObjectChangeAttributeId", "ValueChangeType", "StringValue")
    VALUES (gen_random_uuid(), attr_change_id, 2, 'New Value');

    INSERT INTO "MetaverseObjectChangeAttributeValues" ("Id", "MetaverseObjectChangeAttributeId", "ValueChangeType", "StringValue")
    VALUES (gen_random_uuid(), attr_change_id, 1, 'Newer Value');

    RAISE NOTICE '=== Success! ===';
    RAISE NOTICE 'Created 2 changes for MVO: %', mvo_id;
    RAISE NOTICE '';
    RAISE NOTICE 'View in UI at: http://localhost:5200/t/people/v/% (or appropriate type)', mvo_id;

END $$;
