-- ============================================================================
-- JIM Change History Test Data - SELF-CONTAINED
-- ============================================================================
-- Creates complete test scenario with User MVOs and change history
-- Uses built-in User type and built-in attributes
-- Requires: Empty or existing JIM database with built-in types
-- Run with: docker compose exec -T jim.database psql -U jim -d jim < test/data/seed-change-history-simple.sql
-- ============================================================================

DO $$
DECLARE
    -- Type IDs
    user_type_id INT;
    group_type_id INT;

    -- Attribute IDs (using built-in attributes)
    attr_displayname_id INT;
    attr_firstname_id INT;
    attr_lastname_id INT;
    attr_email_id INT;
    attr_department_id INT;
    attr_jobtitle_id INT;
    attr_manager_id INT;
    attr_members_id INT;
    attr_accountname_id INT;
    attr_description_id INT;
    attr_grouptype_id INT;
    attr_groupscope_id INT;

    -- MVO IDs
    alice_id UUID;
    bob_id UUID;
    engineers_group_id UUID;

    -- Change IDs
    change_id UUID;
    attr_change_id UUID;
BEGIN
    RAISE NOTICE '=== JIM Change History Seed - Starting ===';

    -- ========================================================================
    -- STEP 1: Get Built-in Type IDs
    -- ========================================================================

    SELECT "Id" INTO user_type_id FROM "MetaverseObjectTypes" WHERE "Name" = 'User' AND "BuiltIn" = true LIMIT 1;
    IF user_type_id IS NULL THEN
        RAISE EXCEPTION 'User type not found. JIM must be initialized with built-in types first.';
    END IF;

    SELECT "Id" INTO group_type_id FROM "MetaverseObjectTypes" WHERE "Name" = 'Group' AND "BuiltIn" = true LIMIT 1;
    IF group_type_id IS NULL THEN
        RAISE EXCEPTION 'Group type not found. JIM must be initialized with built-in types first.';
    END IF;

    RAISE NOTICE 'Found User type: %, Group type: %', user_type_id, group_type_id;

    -- ========================================================================
    -- STEP 2: Get Built-in Attribute IDs
    -- ========================================================================

    SELECT "Id" INTO attr_displayname_id FROM "MetaverseAttributes" WHERE "Name" = 'Display Name' AND "BuiltIn" = true LIMIT 1;
    SELECT "Id" INTO attr_firstname_id FROM "MetaverseAttributes" WHERE "Name" = 'First Name' AND "BuiltIn" = true LIMIT 1;
    SELECT "Id" INTO attr_lastname_id FROM "MetaverseAttributes" WHERE "Name" = 'Last Name' AND "BuiltIn" = true LIMIT 1;
    SELECT "Id" INTO attr_email_id FROM "MetaverseAttributes" WHERE "Name" = 'Email' AND "BuiltIn" = true LIMIT 1;
    SELECT "Id" INTO attr_department_id FROM "MetaverseAttributes" WHERE "Name" = 'Department' AND "BuiltIn" = true LIMIT 1;
    SELECT "Id" INTO attr_jobtitle_id FROM "MetaverseAttributes" WHERE "Name" = 'Job Title' AND "BuiltIn" = true LIMIT 1;
    SELECT "Id" INTO attr_manager_id FROM "MetaverseAttributes" WHERE "Name" = 'Manager' AND "BuiltIn" = true LIMIT 1;
    SELECT "Id" INTO attr_members_id FROM "MetaverseAttributes" WHERE "Name" = 'Static Members' AND "BuiltIn" = true LIMIT 1;
    SELECT "Id" INTO attr_accountname_id FROM "MetaverseAttributes" WHERE "Name" = 'Account Name' AND "BuiltIn" = true LIMIT 1;
    SELECT "Id" INTO attr_description_id FROM "MetaverseAttributes" WHERE "Name" = 'Description' AND "BuiltIn" = true LIMIT 1;
    SELECT "Id" INTO attr_grouptype_id FROM "MetaverseAttributes" WHERE "Name" = 'Group Type' AND "BuiltIn" = true LIMIT 1;
    SELECT "Id" INTO attr_groupscope_id FROM "MetaverseAttributes" WHERE "Name" = 'Group Scope' AND "BuiltIn" = true LIMIT 1;

    IF attr_displayname_id IS NULL OR attr_firstname_id IS NULL OR attr_lastname_id IS NULL OR
       attr_email_id IS NULL OR attr_department_id IS NULL OR attr_jobtitle_id IS NULL OR
       attr_manager_id IS NULL OR attr_members_id IS NULL OR attr_accountname_id IS NULL OR
       attr_description_id IS NULL OR attr_grouptype_id IS NULL OR attr_groupscope_id IS NULL THEN
        RAISE EXCEPTION 'Required built-in attributes not found. JIM must be initialised first.';
    END IF;

    RAISE NOTICE 'Found all required built-in attributes';

    -- ========================================================================
    -- STEP 3: Create User MVOs
    -- ========================================================================

    -- Create Alice
    alice_id := gen_random_uuid();
    INSERT INTO "MetaverseObjects" ("Id", "TypeId", "Origin", "Status", "Created")
    VALUES (alice_id, user_type_id, 1, 1, NOW() - INTERVAL '30 days');

    INSERT INTO "MetaverseObjectAttributeValues" ("Id", "MetaverseObjectId", "AttributeId", "StringValue")
    VALUES (gen_random_uuid(), alice_id, attr_displayname_id, 'Alice Anderson');

    INSERT INTO "MetaverseObjectAttributeValues" ("Id", "MetaverseObjectId", "AttributeId", "StringValue")
    VALUES (gen_random_uuid(), alice_id, attr_firstname_id, 'Alice');

    INSERT INTO "MetaverseObjectAttributeValues" ("Id", "MetaverseObjectId", "AttributeId", "StringValue")
    VALUES (gen_random_uuid(), alice_id, attr_lastname_id, 'Anderson');

    INSERT INTO "MetaverseObjectAttributeValues" ("Id", "MetaverseObjectId", "AttributeId", "StringValue")
    VALUES (gen_random_uuid(), alice_id, attr_email_id, 'alice.anderson@contoso.enterprise.com');

    INSERT INTO "MetaverseObjectAttributeValues" ("Id", "MetaverseObjectId", "AttributeId", "StringValue")
    VALUES (gen_random_uuid(), alice_id, attr_department_id, 'Engineering - Platform Team');

    INSERT INTO "MetaverseObjectAttributeValues" ("Id", "MetaverseObjectId", "AttributeId", "StringValue")
    VALUES (gen_random_uuid(), alice_id, attr_jobtitle_id, 'Engineering Manager');

    RAISE NOTICE 'Created Alice: %', alice_id;

    -- Create Bob
    bob_id := gen_random_uuid();
    INSERT INTO "MetaverseObjects" ("Id", "TypeId", "Origin", "Status", "Created")
    VALUES (bob_id, user_type_id, 1, 1, NOW() - INTERVAL '25 days');

    INSERT INTO "MetaverseObjectAttributeValues" ("Id", "MetaverseObjectId", "AttributeId", "StringValue")
    VALUES (gen_random_uuid(), bob_id, attr_displayname_id, 'Bob Brown');

    INSERT INTO "MetaverseObjectAttributeValues" ("Id", "MetaverseObjectId", "AttributeId", "StringValue")
    VALUES (gen_random_uuid(), bob_id, attr_firstname_id, 'Bob');

    INSERT INTO "MetaverseObjectAttributeValues" ("Id", "MetaverseObjectId", "AttributeId", "StringValue")
    VALUES (gen_random_uuid(), bob_id, attr_lastname_id, 'Brown');

    INSERT INTO "MetaverseObjectAttributeValues" ("Id", "MetaverseObjectId", "AttributeId", "StringValue")
    VALUES (gen_random_uuid(), bob_id, attr_email_id, 'bob.brown@contoso.enterprise.com');

    INSERT INTO "MetaverseObjectAttributeValues" ("Id", "MetaverseObjectId", "AttributeId", "StringValue")
    VALUES (gen_random_uuid(), bob_id, attr_department_id, 'Engineering - Platform Team');

    INSERT INTO "MetaverseObjectAttributeValues" ("Id", "MetaverseObjectId", "AttributeId", "StringValue")
    VALUES (gen_random_uuid(), bob_id, attr_jobtitle_id, 'Senior Software Engineer');

    -- Bob's manager is Alice
    INSERT INTO "MetaverseObjectAttributeValues" ("Id", "MetaverseObjectId", "AttributeId", "ReferenceValueId")
    VALUES (gen_random_uuid(), bob_id, attr_manager_id, alice_id);

    RAISE NOTICE 'Created Bob: %', bob_id;

    -- Create Engineers Group
    engineers_group_id := gen_random_uuid();
    INSERT INTO "MetaverseObjects" ("Id", "TypeId", "Origin", "Status", "Created")
    VALUES (engineers_group_id, group_type_id, 1, 1, NOW() - INTERVAL '20 days');

    INSERT INTO "MetaverseObjectAttributeValues" ("Id", "MetaverseObjectId", "AttributeId", "StringValue")
    VALUES (gen_random_uuid(), engineers_group_id, attr_displayname_id, 'Software Engineers');

    INSERT INTO "MetaverseObjectAttributeValues" ("Id", "MetaverseObjectId", "AttributeId", "StringValue")
    VALUES (gen_random_uuid(), engineers_group_id, attr_accountname_id, 'SoftwareEngineers');

    INSERT INTO "MetaverseObjectAttributeValues" ("Id", "MetaverseObjectId", "AttributeId", "StringValue")
    VALUES (gen_random_uuid(), engineers_group_id, attr_description_id, 'Engineering team group for software developers');

    INSERT INTO "MetaverseObjectAttributeValues" ("Id", "MetaverseObjectId", "AttributeId", "StringValue")
    VALUES (gen_random_uuid(), engineers_group_id, attr_grouptype_id, 'Security');

    INSERT INTO "MetaverseObjectAttributeValues" ("Id", "MetaverseObjectId", "AttributeId", "StringValue")
    VALUES (gen_random_uuid(), engineers_group_id, attr_groupscope_id, 'Global');

    INSERT INTO "MetaverseObjectAttributeValues" ("Id", "MetaverseObjectId", "AttributeId", "StringValue")
    VALUES (gen_random_uuid(), engineers_group_id, attr_email_id, 'engineers@contoso.enterprise.com');

    -- Group has both Alice and Bob as members
    INSERT INTO "MetaverseObjectAttributeValues" ("Id", "MetaverseObjectId", "AttributeId", "ReferenceValueId")
    VALUES (gen_random_uuid(), engineers_group_id, attr_members_id, alice_id);

    INSERT INTO "MetaverseObjectAttributeValues" ("Id", "MetaverseObjectId", "AttributeId", "ReferenceValueId")
    VALUES (gen_random_uuid(), engineers_group_id, attr_members_id, bob_id);

    RAISE NOTICE 'Created Engineers Group: %', engineers_group_id;

    -- ========================================================================
    -- STEP 4: Create Change History - Alice (4 changes)
    -- ========================================================================

    RAISE NOTICE 'Creating Alice change history...';

    -- Change 1: Promotion to Lead Engineer (25 days ago)
    change_id := gen_random_uuid();
    INSERT INTO "MetaverseObjectChanges" ("Id", "MetaverseObjectId", "ChangeType", "ChangeTime", "ChangeInitiatorType")
    VALUES (change_id, alice_id, 2, NOW() - INTERVAL '25 days', 2);  -- Updated, SynchronisationRule

    attr_change_id := gen_random_uuid();
    INSERT INTO "MetaverseObjectChangeAttributes" ("Id", "MetaverseObjectChangeId", "AttributeId")
    VALUES (attr_change_id, change_id, attr_jobtitle_id);

    INSERT INTO "MetaverseObjectChangeAttributeValues" ("Id", "MetaverseObjectChangeAttributeId", "ValueChangeType", "StringValue")
    VALUES (gen_random_uuid(), attr_change_id, 2, 'Senior Software Engineer');  -- Remove

    INSERT INTO "MetaverseObjectChangeAttributeValues" ("Id", "MetaverseObjectChangeAttributeId", "ValueChangeType", "StringValue")
    VALUES (gen_random_uuid(), attr_change_id, 1, 'Lead Software Engineer');  -- Add

    -- Change 2: Promotion to Engineering Manager (20 days ago)
    change_id := gen_random_uuid();
    INSERT INTO "MetaverseObjectChanges" ("Id", "MetaverseObjectId", "ChangeType", "ChangeTime", "ChangeInitiatorType")
    VALUES (change_id, alice_id, 2, NOW() - INTERVAL '20 days', 2);

    attr_change_id := gen_random_uuid();
    INSERT INTO "MetaverseObjectChangeAttributes" ("Id", "MetaverseObjectChangeId", "AttributeId")
    VALUES (attr_change_id, change_id, attr_jobtitle_id);

    INSERT INTO "MetaverseObjectChangeAttributeValues" ("Id", "MetaverseObjectChangeAttributeId", "ValueChangeType", "StringValue")
    VALUES (gen_random_uuid(), attr_change_id, 2, 'Lead Software Engineer');  -- Remove

    INSERT INTO "MetaverseObjectChangeAttributeValues" ("Id", "MetaverseObjectChangeAttributeId", "ValueChangeType", "StringValue")
    VALUES (gen_random_uuid(), attr_change_id, 1, 'Engineering Manager');  -- Add

    -- Change 3: Department change (15 days ago)
    change_id := gen_random_uuid();
    INSERT INTO "MetaverseObjectChanges" ("Id", "MetaverseObjectId", "ChangeType", "ChangeTime", "ChangeInitiatorType")
    VALUES (change_id, alice_id, 2, NOW() - INTERVAL '15 days', 2);

    attr_change_id := gen_random_uuid();
    INSERT INTO "MetaverseObjectChangeAttributes" ("Id", "MetaverseObjectChangeId", "AttributeId")
    VALUES (attr_change_id, change_id, attr_department_id);

    INSERT INTO "MetaverseObjectChangeAttributeValues" ("Id", "MetaverseObjectChangeAttributeId", "ValueChangeType", "StringValue")
    VALUES (gen_random_uuid(), attr_change_id, 2, 'Engineering - Development');  -- Remove

    INSERT INTO "MetaverseObjectChangeAttributeValues" ("Id", "MetaverseObjectChangeAttributeId", "ValueChangeType", "StringValue")
    VALUES (gen_random_uuid(), attr_change_id, 1, 'Engineering - Platform Team');  -- Add

    -- Change 4: Email update (10 days ago)
    change_id := gen_random_uuid();
    INSERT INTO "MetaverseObjectChanges" ("Id", "MetaverseObjectId", "ChangeType", "ChangeTime", "ChangeInitiatorType")
    VALUES (change_id, alice_id, 2, NOW() - INTERVAL '10 days', 2);

    attr_change_id := gen_random_uuid();
    INSERT INTO "MetaverseObjectChangeAttributes" ("Id", "MetaverseObjectChangeId", "AttributeId")
    VALUES (attr_change_id, change_id, attr_email_id);

    INSERT INTO "MetaverseObjectChangeAttributeValues" ("Id", "MetaverseObjectChangeAttributeId", "ValueChangeType", "StringValue")
    VALUES (gen_random_uuid(), attr_change_id, 2, 'a.anderson@contoso.com');  -- Remove

    INSERT INTO "MetaverseObjectChangeAttributeValues" ("Id", "MetaverseObjectChangeAttributeId", "ValueChangeType", "StringValue")
    VALUES (gen_random_uuid(), attr_change_id, 1, 'alice.anderson@contoso.enterprise.com');  -- Add

    -- ========================================================================
    -- STEP 5: Create Change History - Bob (3 changes)
    -- ========================================================================

    RAISE NOTICE 'Creating Bob change history...';

    -- Change 1: Manager assignment (24 days ago)
    change_id := gen_random_uuid();
    INSERT INTO "MetaverseObjectChanges" ("Id", "MetaverseObjectId", "ChangeType", "ChangeTime", "ChangeInitiatorType")
    VALUES (change_id, bob_id, 2, NOW() - INTERVAL '24 days', 2);

    attr_change_id := gen_random_uuid();
    INSERT INTO "MetaverseObjectChangeAttributes" ("Id", "MetaverseObjectChangeId", "AttributeId")
    VALUES (attr_change_id, change_id, attr_manager_id);

    INSERT INTO "MetaverseObjectChangeAttributeValues" ("Id", "MetaverseObjectChangeAttributeId", "ValueChangeType", "ReferenceValueId")
    VALUES (gen_random_uuid(), attr_change_id, 1, alice_id);  -- Add Alice as manager

    -- Change 2: Promotion (18 days ago)
    change_id := gen_random_uuid();
    INSERT INTO "MetaverseObjectChanges" ("Id", "MetaverseObjectId", "ChangeType", "ChangeTime", "ChangeInitiatorType")
    VALUES (change_id, bob_id, 2, NOW() - INTERVAL '18 days', 2);

    attr_change_id := gen_random_uuid();
    INSERT INTO "MetaverseObjectChangeAttributes" ("Id", "MetaverseObjectChangeId", "AttributeId")
    VALUES (attr_change_id, change_id, attr_jobtitle_id);

    INSERT INTO "MetaverseObjectChangeAttributeValues" ("Id", "MetaverseObjectChangeAttributeId", "ValueChangeType", "StringValue")
    VALUES (gen_random_uuid(), attr_change_id, 2, 'Software Engineer');  -- Remove

    INSERT INTO "MetaverseObjectChangeAttributeValues" ("Id", "MetaverseObjectChangeAttributeId", "ValueChangeType", "StringValue")
    VALUES (gen_random_uuid(), attr_change_id, 1, 'Senior Software Engineer');  -- Add

    -- Change 3: Manager change (12 days ago - temporarily removed then re-added)
    change_id := gen_random_uuid();
    INSERT INTO "MetaverseObjectChanges" ("Id", "MetaverseObjectId", "ChangeType", "ChangeTime", "ChangeInitiatorType")
    VALUES (change_id, bob_id, 2, NOW() - INTERVAL '12 days', 2);

    attr_change_id := gen_random_uuid();
    INSERT INTO "MetaverseObjectChangeAttributes" ("Id", "MetaverseObjectChangeId", "AttributeId")
    VALUES (attr_change_id, change_id, attr_manager_id);

    INSERT INTO "MetaverseObjectChangeAttributeValues" ("Id", "MetaverseObjectChangeAttributeId", "ValueChangeType", "ReferenceValueId")
    VALUES (gen_random_uuid(), attr_change_id, 2, alice_id);  -- Remove

    INSERT INTO "MetaverseObjectChangeAttributeValues" ("Id", "MetaverseObjectChangeAttributeId", "ValueChangeType", "ReferenceValueId")
    VALUES (gen_random_uuid(), attr_change_id, 1, alice_id);  -- Re-add (simulating update)

    -- ========================================================================
    -- STEP 6: Create Change History - Engineers Group (2 changes)
    -- ========================================================================

    RAISE NOTICE 'Creating Engineers Group change history...';

    -- Change 1: Group name change (19 days ago)
    change_id := gen_random_uuid();
    INSERT INTO "MetaverseObjectChanges" ("Id", "MetaverseObjectId", "ChangeType", "ChangeTime", "ChangeInitiatorType")
    VALUES (change_id, engineers_group_id, 2, NOW() - INTERVAL '19 days', 2);

    attr_change_id := gen_random_uuid();
    INSERT INTO "MetaverseObjectChangeAttributes" ("Id", "MetaverseObjectChangeId", "AttributeId")
    VALUES (attr_change_id, change_id, attr_displayname_id);

    INSERT INTO "MetaverseObjectChangeAttributeValues" ("Id", "MetaverseObjectChangeAttributeId", "ValueChangeType", "StringValue")
    VALUES (gen_random_uuid(), attr_change_id, 2, 'Engineers');  -- Remove

    INSERT INTO "MetaverseObjectChangeAttributeValues" ("Id", "MetaverseObjectChangeAttributeId", "ValueChangeType", "StringValue")
    VALUES (gen_random_uuid(), attr_change_id, 1, 'Software Engineers');  -- Add

    -- Change 2: Members added (15 days ago)
    change_id := gen_random_uuid();
    INSERT INTO "MetaverseObjectChanges" ("Id", "MetaverseObjectId", "ChangeType", "ChangeTime", "ChangeInitiatorType")
    VALUES (change_id, engineers_group_id, 2, NOW() - INTERVAL '15 days', 2);

    attr_change_id := gen_random_uuid();
    INSERT INTO "MetaverseObjectChangeAttributes" ("Id", "MetaverseObjectChangeId", "AttributeId")
    VALUES (attr_change_id, change_id, attr_members_id);

    INSERT INTO "MetaverseObjectChangeAttributeValues" ("Id", "MetaverseObjectChangeAttributeId", "ValueChangeType", "ReferenceValueId")
    VALUES (gen_random_uuid(), attr_change_id, 1, alice_id);  -- Add Alice

    INSERT INTO "MetaverseObjectChangeAttributeValues" ("Id", "MetaverseObjectChangeAttributeId", "ValueChangeType", "ReferenceValueId")
    VALUES (gen_random_uuid(), attr_change_id, 1, bob_id);  -- Add Bob

    -- ========================================================================
    -- SUCCESS
    -- ========================================================================

    RAISE NOTICE '';
    RAISE NOTICE '=== SUCCESS! Change History Created ===';
    RAISE NOTICE '';
    RAISE NOTICE 'Created:';
    RAISE NOTICE '  - Alice (User) with 4 changes';
    RAISE NOTICE '  - Bob (User) with 3 changes (including Manager reference)';
    RAISE NOTICE '  - Software Engineers (Group) with 2 changes';
    RAISE NOTICE '';
    RAISE NOTICE 'UI Testing URLs:';
    RAISE NOTICE '  Alice: http://localhost:5200/t/users/v/%', alice_id;
    RAISE NOTICE '  Bob: http://localhost:5200/t/users/v/%', bob_id;
    RAISE NOTICE '  Engineers: http://localhost:5200/t/groups/v/%', engineers_group_id;
    RAISE NOTICE '';
END $$;
