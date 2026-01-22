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
    charlie_id UUID;
    diana_id UUID;
    eve_id UUID;
    frank_id UUID;
    grace_id UUID;
    henry_id UUID;
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

    -- Create Charlie
    charlie_id := gen_random_uuid();
    INSERT INTO "MetaverseObjects" ("Id", "TypeId", "Origin", "Status", "Created")
    VALUES (charlie_id, user_type_id, 1, 1, NOW() - INTERVAL '28 days');

    INSERT INTO "MetaverseObjectAttributeValues" ("Id", "MetaverseObjectId", "AttributeId", "StringValue")
    VALUES (gen_random_uuid(), charlie_id, attr_displayname_id, 'Charlie Cooper');
    INSERT INTO "MetaverseObjectAttributeValues" ("Id", "MetaverseObjectId", "AttributeId", "StringValue")
    VALUES (gen_random_uuid(), charlie_id, attr_firstname_id, 'Charlie');
    INSERT INTO "MetaverseObjectAttributeValues" ("Id", "MetaverseObjectId", "AttributeId", "StringValue")
    VALUES (gen_random_uuid(), charlie_id, attr_lastname_id, 'Cooper');
    INSERT INTO "MetaverseObjectAttributeValues" ("Id", "MetaverseObjectId", "AttributeId", "StringValue")
    VALUES (gen_random_uuid(), charlie_id, attr_email_id, 'charlie.cooper@contoso.enterprise.com');
    INSERT INTO "MetaverseObjectAttributeValues" ("Id", "MetaverseObjectId", "AttributeId", "StringValue")
    VALUES (gen_random_uuid(), charlie_id, attr_department_id, 'Engineering - Platform Team');
    INSERT INTO "MetaverseObjectAttributeValues" ("Id", "MetaverseObjectId", "AttributeId", "StringValue")
    VALUES (gen_random_uuid(), charlie_id, attr_jobtitle_id, 'Software Engineer');

    RAISE NOTICE 'Created Charlie: %', charlie_id;

    -- Create Diana
    diana_id := gen_random_uuid();
    INSERT INTO "MetaverseObjects" ("Id", "TypeId", "Origin", "Status", "Created")
    VALUES (diana_id, user_type_id, 1, 1, NOW() - INTERVAL '26 days');

    INSERT INTO "MetaverseObjectAttributeValues" ("Id", "MetaverseObjectId", "AttributeId", "StringValue")
    VALUES (gen_random_uuid(), diana_id, attr_displayname_id, 'Diana Davis');
    INSERT INTO "MetaverseObjectAttributeValues" ("Id", "MetaverseObjectId", "AttributeId", "StringValue")
    VALUES (gen_random_uuid(), diana_id, attr_firstname_id, 'Diana');
    INSERT INTO "MetaverseObjectAttributeValues" ("Id", "MetaverseObjectId", "AttributeId", "StringValue")
    VALUES (gen_random_uuid(), diana_id, attr_lastname_id, 'Davis');
    INSERT INTO "MetaverseObjectAttributeValues" ("Id", "MetaverseObjectId", "AttributeId", "StringValue")
    VALUES (gen_random_uuid(), diana_id, attr_email_id, 'diana.davis@contoso.enterprise.com');
    INSERT INTO "MetaverseObjectAttributeValues" ("Id", "MetaverseObjectId", "AttributeId", "StringValue")
    VALUES (gen_random_uuid(), diana_id, attr_department_id, 'Engineering - Frontend');
    INSERT INTO "MetaverseObjectAttributeValues" ("Id", "MetaverseObjectId", "AttributeId", "StringValue")
    VALUES (gen_random_uuid(), diana_id, attr_jobtitle_id, 'Senior Software Engineer');

    RAISE NOTICE 'Created Diana: %', diana_id;

    -- Create Eve
    eve_id := gen_random_uuid();
    INSERT INTO "MetaverseObjects" ("Id", "TypeId", "Origin", "Status", "Created")
    VALUES (eve_id, user_type_id, 1, 1, NOW() - INTERVAL '24 days');

    INSERT INTO "MetaverseObjectAttributeValues" ("Id", "MetaverseObjectId", "AttributeId", "StringValue")
    VALUES (gen_random_uuid(), eve_id, attr_displayname_id, 'Eve Evans');
    INSERT INTO "MetaverseObjectAttributeValues" ("Id", "MetaverseObjectId", "AttributeId", "StringValue")
    VALUES (gen_random_uuid(), eve_id, attr_firstname_id, 'Eve');
    INSERT INTO "MetaverseObjectAttributeValues" ("Id", "MetaverseObjectId", "AttributeId", "StringValue")
    VALUES (gen_random_uuid(), eve_id, attr_lastname_id, 'Evans');
    INSERT INTO "MetaverseObjectAttributeValues" ("Id", "MetaverseObjectId", "AttributeId", "StringValue")
    VALUES (gen_random_uuid(), eve_id, attr_email_id, 'eve.evans@contoso.enterprise.com');
    INSERT INTO "MetaverseObjectAttributeValues" ("Id", "MetaverseObjectId", "AttributeId", "StringValue")
    VALUES (gen_random_uuid(), eve_id, attr_department_id, 'Engineering - Backend');
    INSERT INTO "MetaverseObjectAttributeValues" ("Id", "MetaverseObjectId", "AttributeId", "StringValue")
    VALUES (gen_random_uuid(), eve_id, attr_jobtitle_id, 'Software Engineer');

    RAISE NOTICE 'Created Eve: %', eve_id;

    -- Create Frank
    frank_id := gen_random_uuid();
    INSERT INTO "MetaverseObjects" ("Id", "TypeId", "Origin", "Status", "Created")
    VALUES (frank_id, user_type_id, 1, 1, NOW() - INTERVAL '22 days');

    INSERT INTO "MetaverseObjectAttributeValues" ("Id", "MetaverseObjectId", "AttributeId", "StringValue")
    VALUES (gen_random_uuid(), frank_id, attr_displayname_id, 'Frank Foster');
    INSERT INTO "MetaverseObjectAttributeValues" ("Id", "MetaverseObjectId", "AttributeId", "StringValue")
    VALUES (gen_random_uuid(), frank_id, attr_firstname_id, 'Frank');
    INSERT INTO "MetaverseObjectAttributeValues" ("Id", "MetaverseObjectId", "AttributeId", "StringValue")
    VALUES (gen_random_uuid(), frank_id, attr_lastname_id, 'Foster');
    INSERT INTO "MetaverseObjectAttributeValues" ("Id", "MetaverseObjectId", "AttributeId", "StringValue")
    VALUES (gen_random_uuid(), frank_id, attr_email_id, 'frank.foster@contoso.enterprise.com');
    INSERT INTO "MetaverseObjectAttributeValues" ("Id", "MetaverseObjectId", "AttributeId", "StringValue")
    VALUES (gen_random_uuid(), frank_id, attr_department_id, 'Engineering - Infrastructure');
    INSERT INTO "MetaverseObjectAttributeValues" ("Id", "MetaverseObjectId", "AttributeId", "StringValue")
    VALUES (gen_random_uuid(), frank_id, attr_jobtitle_id, 'DevOps Engineer');

    RAISE NOTICE 'Created Frank: %', frank_id;

    -- Create Grace
    grace_id := gen_random_uuid();
    INSERT INTO "MetaverseObjects" ("Id", "TypeId", "Origin", "Status", "Created")
    VALUES (grace_id, user_type_id, 1, 1, NOW() - INTERVAL '21 days');

    INSERT INTO "MetaverseObjectAttributeValues" ("Id", "MetaverseObjectId", "AttributeId", "StringValue")
    VALUES (gen_random_uuid(), grace_id, attr_displayname_id, 'Grace Green');
    INSERT INTO "MetaverseObjectAttributeValues" ("Id", "MetaverseObjectId", "AttributeId", "StringValue")
    VALUES (gen_random_uuid(), grace_id, attr_firstname_id, 'Grace');
    INSERT INTO "MetaverseObjectAttributeValues" ("Id", "MetaverseObjectId", "AttributeId", "StringValue")
    VALUES (gen_random_uuid(), grace_id, attr_lastname_id, 'Green');
    INSERT INTO "MetaverseObjectAttributeValues" ("Id", "MetaverseObjectId", "AttributeId", "StringValue")
    VALUES (gen_random_uuid(), grace_id, attr_email_id, 'grace.green@contoso.enterprise.com');
    INSERT INTO "MetaverseObjectAttributeValues" ("Id", "MetaverseObjectId", "AttributeId", "StringValue")
    VALUES (gen_random_uuid(), grace_id, attr_department_id, 'Engineering - QA');
    INSERT INTO "MetaverseObjectAttributeValues" ("Id", "MetaverseObjectId", "AttributeId", "StringValue")
    VALUES (gen_random_uuid(), grace_id, attr_jobtitle_id, 'Test Engineer');

    RAISE NOTICE 'Created Grace: %', grace_id;

    -- Create Henry
    henry_id := gen_random_uuid();
    INSERT INTO "MetaverseObjects" ("Id", "TypeId", "Origin", "Status", "Created")
    VALUES (henry_id, user_type_id, 1, 1, NOW() - INTERVAL '19 days');

    INSERT INTO "MetaverseObjectAttributeValues" ("Id", "MetaverseObjectId", "AttributeId", "StringValue")
    VALUES (gen_random_uuid(), henry_id, attr_displayname_id, 'Henry Harris');
    INSERT INTO "MetaverseObjectAttributeValues" ("Id", "MetaverseObjectId", "AttributeId", "StringValue")
    VALUES (gen_random_uuid(), henry_id, attr_firstname_id, 'Henry');
    INSERT INTO "MetaverseObjectAttributeValues" ("Id", "MetaverseObjectId", "AttributeId", "StringValue")
    VALUES (gen_random_uuid(), henry_id, attr_lastname_id, 'Harris');
    INSERT INTO "MetaverseObjectAttributeValues" ("Id", "MetaverseObjectId", "AttributeId", "StringValue")
    VALUES (gen_random_uuid(), henry_id, attr_email_id, 'henry.harris@contoso.enterprise.com');
    INSERT INTO "MetaverseObjectAttributeValues" ("Id", "MetaverseObjectId", "AttributeId", "StringValue")
    VALUES (gen_random_uuid(), henry_id, attr_department_id, 'Engineering - Security');
    INSERT INTO "MetaverseObjectAttributeValues" ("Id", "MetaverseObjectId", "AttributeId", "StringValue")
    VALUES (gen_random_uuid(), henry_id, attr_jobtitle_id, 'Security Engineer');

    RAISE NOTICE 'Created Henry: %', henry_id;

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
    VALUES (change_id, alice_id, 2, NOW() - INTERVAL '25 days', 4);  -- Updated, SynchronisationRule

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
    VALUES (change_id, alice_id, 2, NOW() - INTERVAL '20 days', 4);

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
    VALUES (change_id, alice_id, 2, NOW() - INTERVAL '15 days', 4);

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
    VALUES (change_id, alice_id, 2, NOW() - INTERVAL '10 days', 4);

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
    VALUES (change_id, bob_id, 2, NOW() - INTERVAL '24 days', 4);

    attr_change_id := gen_random_uuid();
    INSERT INTO "MetaverseObjectChangeAttributes" ("Id", "MetaverseObjectChangeId", "AttributeId")
    VALUES (attr_change_id, change_id, attr_manager_id);

    INSERT INTO "MetaverseObjectChangeAttributeValues" ("Id", "MetaverseObjectChangeAttributeId", "ValueChangeType", "ReferenceValueId")
    VALUES (gen_random_uuid(), attr_change_id, 1, alice_id);  -- Add Alice as manager

    -- Change 2: Promotion (18 days ago)
    change_id := gen_random_uuid();
    INSERT INTO "MetaverseObjectChanges" ("Id", "MetaverseObjectId", "ChangeType", "ChangeTime", "ChangeInitiatorType")
    VALUES (change_id, bob_id, 2, NOW() - INTERVAL '18 days', 4);

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
    VALUES (change_id, bob_id, 2, NOW() - INTERVAL '12 days', 4);

    attr_change_id := gen_random_uuid();
    INSERT INTO "MetaverseObjectChangeAttributes" ("Id", "MetaverseObjectChangeId", "AttributeId")
    VALUES (attr_change_id, change_id, attr_manager_id);

    INSERT INTO "MetaverseObjectChangeAttributeValues" ("Id", "MetaverseObjectChangeAttributeId", "ValueChangeType", "ReferenceValueId")
    VALUES (gen_random_uuid(), attr_change_id, 2, alice_id);  -- Remove

    INSERT INTO "MetaverseObjectChangeAttributeValues" ("Id", "MetaverseObjectChangeAttributeId", "ValueChangeType", "ReferenceValueId")
    VALUES (gen_random_uuid(), attr_change_id, 1, alice_id);  -- Re-add (simulating update)

    -- ========================================================================
    -- STEP 6: Create Change History - Engineers Group (11 changes with bulk operations)
    -- ========================================================================

    RAISE NOTICE 'Creating Engineers Group change history...';

    -- Change 1: Initial group name (19 days ago)
    change_id := gen_random_uuid();
    INSERT INTO "MetaverseObjectChanges" ("Id", "MetaverseObjectId", "ChangeType", "ChangeTime", "ChangeInitiatorType")
    VALUES (change_id, engineers_group_id, 2, NOW() - INTERVAL '19 days', 4);

    attr_change_id := gen_random_uuid();
    INSERT INTO "MetaverseObjectChangeAttributes" ("Id", "MetaverseObjectChangeId", "AttributeId")
    VALUES (attr_change_id, change_id, attr_displayname_id);

    INSERT INTO "MetaverseObjectChangeAttributeValues" ("Id", "MetaverseObjectChangeAttributeId", "ValueChangeType", "StringValue")
    VALUES (gen_random_uuid(), attr_change_id, 2, 'Engineers');  -- Remove

    INSERT INTO "MetaverseObjectChangeAttributeValues" ("Id", "MetaverseObjectChangeAttributeId", "ValueChangeType", "StringValue")
    VALUES (gen_random_uuid(), attr_change_id, 1, 'Software Engineers');  -- Add

    -- Change 2: Bulk initial members added - Alice, Bob, Charlie (18 days ago)
    change_id := gen_random_uuid();
    INSERT INTO "MetaverseObjectChanges" ("Id", "MetaverseObjectId", "ChangeType", "ChangeTime", "ChangeInitiatorType")
    VALUES (change_id, engineers_group_id, 2, NOW() - INTERVAL '18 days', 4);

    attr_change_id := gen_random_uuid();
    INSERT INTO "MetaverseObjectChangeAttributes" ("Id", "MetaverseObjectChangeId", "AttributeId")
    VALUES (attr_change_id, change_id, attr_members_id);

    INSERT INTO "MetaverseObjectChangeAttributeValues" ("Id", "MetaverseObjectChangeAttributeId", "ValueChangeType", "ReferenceValueId")
    VALUES (gen_random_uuid(), attr_change_id, 1, alice_id);  -- Add Alice

    INSERT INTO "MetaverseObjectChangeAttributeValues" ("Id", "MetaverseObjectChangeAttributeId", "ValueChangeType", "ReferenceValueId")
    VALUES (gen_random_uuid(), attr_change_id, 1, bob_id);  -- Add Bob

    INSERT INTO "MetaverseObjectChangeAttributeValues" ("Id", "MetaverseObjectChangeAttributeId", "ValueChangeType", "ReferenceValueId")
    VALUES (gen_random_uuid(), attr_change_id, 1, charlie_id);  -- Add Charlie

    -- Change 3: (removed - consolidated into change 2)

    -- Change 4: Description update (16 days ago)
    change_id := gen_random_uuid();
    INSERT INTO "MetaverseObjectChanges" ("Id", "MetaverseObjectId", "ChangeType", "ChangeTime", "ChangeInitiatorType")
    VALUES (change_id, engineers_group_id, 2, NOW() - INTERVAL '16 days', 4);

    attr_change_id := gen_random_uuid();
    INSERT INTO "MetaverseObjectChangeAttributes" ("Id", "MetaverseObjectChangeId", "AttributeId")
    VALUES (attr_change_id, change_id, attr_description_id);

    INSERT INTO "MetaverseObjectChangeAttributeValues" ("Id", "MetaverseObjectChangeAttributeId", "ValueChangeType", "StringValue")
    VALUES (gen_random_uuid(), attr_change_id, 2, 'Engineering team');  -- Remove

    INSERT INTO "MetaverseObjectChangeAttributeValues" ("Id", "MetaverseObjectChangeAttributeId", "ValueChangeType", "StringValue")
    VALUES (gen_random_uuid(), attr_change_id, 1, 'Engineering team group for software developers');  -- Add

    -- Change 5: Bulk add Diana, Eve, Frank (15 days ago)
    change_id := gen_random_uuid();
    INSERT INTO "MetaverseObjectChanges" ("Id", "MetaverseObjectId", "ChangeType", "ChangeTime", "ChangeInitiatorType")
    VALUES (change_id, engineers_group_id, 2, NOW() - INTERVAL '15 days', 4);

    attr_change_id := gen_random_uuid();
    INSERT INTO "MetaverseObjectChangeAttributes" ("Id", "MetaverseObjectChangeId", "AttributeId")
    VALUES (attr_change_id, change_id, attr_members_id);

    INSERT INTO "MetaverseObjectChangeAttributeValues" ("Id", "MetaverseObjectChangeAttributeId", "ValueChangeType", "ReferenceValueId")
    VALUES (gen_random_uuid(), attr_change_id, 1, diana_id);  -- Add Diana

    INSERT INTO "MetaverseObjectChangeAttributeValues" ("Id", "MetaverseObjectChangeAttributeId", "ValueChangeType", "ReferenceValueId")
    VALUES (gen_random_uuid(), attr_change_id, 1, eve_id);  -- Add Eve

    INSERT INTO "MetaverseObjectChangeAttributeValues" ("Id", "MetaverseObjectChangeAttributeId", "ValueChangeType", "ReferenceValueId")
    VALUES (gen_random_uuid(), attr_change_id, 1, frank_id);  -- Add Frank

    -- Change 6: Group name refinement (14 days ago)
    change_id := gen_random_uuid();
    INSERT INTO "MetaverseObjectChanges" ("Id", "MetaverseObjectId", "ChangeType", "ChangeTime", "ChangeInitiatorType")
    VALUES (change_id, engineers_group_id, 2, NOW() - INTERVAL '14 days', 4);

    attr_change_id := gen_random_uuid();
    INSERT INTO "MetaverseObjectChangeAttributes" ("Id", "MetaverseObjectChangeId", "AttributeId")
    VALUES (attr_change_id, change_id, attr_displayname_id);

    INSERT INTO "MetaverseObjectChangeAttributeValues" ("Id", "MetaverseObjectChangeAttributeId", "ValueChangeType", "StringValue")
    VALUES (gen_random_uuid(), attr_change_id, 2, 'Software Engineers');  -- Remove

    INSERT INTO "MetaverseObjectChangeAttributeValues" ("Id", "MetaverseObjectChangeAttributeId", "ValueChangeType", "StringValue")
    VALUES (gen_random_uuid(), attr_change_id, 1, 'Platform Engineering Team');  -- Add

    -- Change 7: (removed - Frank already added in Change 5)

    -- Change 8: Remove Charlie (temporarily) (12 days ago)
    change_id := gen_random_uuid();
    INSERT INTO "MetaverseObjectChanges" ("Id", "MetaverseObjectId", "ChangeType", "ChangeTime", "ChangeInitiatorType")
    VALUES (change_id, engineers_group_id, 2, NOW() - INTERVAL '12 days', 4);

    attr_change_id := gen_random_uuid();
    INSERT INTO "MetaverseObjectChangeAttributes" ("Id", "MetaverseObjectChangeId", "AttributeId")
    VALUES (attr_change_id, change_id, attr_members_id);

    INSERT INTO "MetaverseObjectChangeAttributeValues" ("Id", "MetaverseObjectChangeAttributeId", "ValueChangeType", "ReferenceValueId")
    VALUES (gen_random_uuid(), attr_change_id, 2, charlie_id);  -- Remove Charlie

    -- Change 9: Description update (11 days ago)
    change_id := gen_random_uuid();
    INSERT INTO "MetaverseObjectChanges" ("Id", "MetaverseObjectId", "ChangeType", "ChangeTime", "ChangeInitiatorType")
    VALUES (change_id, engineers_group_id, 2, NOW() - INTERVAL '11 days', 4);

    attr_change_id := gen_random_uuid();
    INSERT INTO "MetaverseObjectChangeAttributes" ("Id", "MetaverseObjectChangeId", "AttributeId")
    VALUES (attr_change_id, change_id, attr_description_id);

    INSERT INTO "MetaverseObjectChangeAttributeValues" ("Id", "MetaverseObjectChangeAttributeId", "ValueChangeType", "StringValue")
    VALUES (gen_random_uuid(), attr_change_id, 2, 'Engineering team group for software developers');  -- Remove

    INSERT INTO "MetaverseObjectChangeAttributeValues" ("Id", "MetaverseObjectChangeAttributeId", "ValueChangeType", "StringValue")
    VALUES (gen_random_uuid(), attr_change_id, 1, 'Core platform engineering team responsible for infrastructure and DevOps');  -- Add

    -- Change 10: Bulk add Grace, Henry, and re-add Charlie (10 days ago)
    change_id := gen_random_uuid();
    INSERT INTO "MetaverseObjectChanges" ("Id", "MetaverseObjectId", "ChangeType", "ChangeTime", "ChangeInitiatorType")
    VALUES (change_id, engineers_group_id, 2, NOW() - INTERVAL '10 days', 4);

    attr_change_id := gen_random_uuid();
    INSERT INTO "MetaverseObjectChangeAttributes" ("Id", "MetaverseObjectChangeId", "AttributeId")
    VALUES (attr_change_id, change_id, attr_members_id);

    INSERT INTO "MetaverseObjectChangeAttributeValues" ("Id", "MetaverseObjectChangeAttributeId", "ValueChangeType", "ReferenceValueId")
    VALUES (gen_random_uuid(), attr_change_id, 1, grace_id);  -- Add Grace

    INSERT INTO "MetaverseObjectChangeAttributeValues" ("Id", "MetaverseObjectChangeAttributeId", "ValueChangeType", "ReferenceValueId")
    VALUES (gen_random_uuid(), attr_change_id, 1, henry_id);  -- Add Henry

    INSERT INTO "MetaverseObjectChangeAttributeValues" ("Id", "MetaverseObjectChangeAttributeId", "ValueChangeType", "ReferenceValueId")
    VALUES (gen_random_uuid(), attr_change_id, 1, charlie_id);  -- Re-add Charlie

    -- Change 11: (removed - consolidated into Change 10)

    -- Change 12: Bulk member removal - remove Diana, Eve, Frank (8 days ago)
    change_id := gen_random_uuid();
    INSERT INTO "MetaverseObjectChanges" ("Id", "MetaverseObjectId", "ChangeType", "ChangeTime", "ChangeInitiatorType")
    VALUES (change_id, engineers_group_id, 2, NOW() - INTERVAL '8 days', 4);

    attr_change_id := gen_random_uuid();
    INSERT INTO "MetaverseObjectChangeAttributes" ("Id", "MetaverseObjectChangeId", "AttributeId")
    VALUES (attr_change_id, change_id, attr_members_id);

    INSERT INTO "MetaverseObjectChangeAttributeValues" ("Id", "MetaverseObjectChangeAttributeId", "ValueChangeType", "ReferenceValueId")
    VALUES (gen_random_uuid(), attr_change_id, 2, diana_id);  -- Remove Diana

    INSERT INTO "MetaverseObjectChangeAttributeValues" ("Id", "MetaverseObjectChangeAttributeId", "ValueChangeType", "ReferenceValueId")
    VALUES (gen_random_uuid(), attr_change_id, 2, eve_id);  -- Remove Eve

    INSERT INTO "MetaverseObjectChangeAttributeValues" ("Id", "MetaverseObjectChangeAttributeId", "ValueChangeType", "ReferenceValueId")
    VALUES (gen_random_uuid(), attr_change_id, 2, frank_id);  -- Remove Frank

    -- Change 13: Group name final update (7 days ago)
    change_id := gen_random_uuid();
    INSERT INTO "MetaverseObjectChanges" ("Id", "MetaverseObjectId", "ChangeType", "ChangeTime", "ChangeInitiatorType")
    VALUES (change_id, engineers_group_id, 2, NOW() - INTERVAL '7 days', 4);

    attr_change_id := gen_random_uuid();
    INSERT INTO "MetaverseObjectChangeAttributes" ("Id", "MetaverseObjectChangeId", "AttributeId")
    VALUES (attr_change_id, change_id, attr_displayname_id);

    INSERT INTO "MetaverseObjectChangeAttributeValues" ("Id", "MetaverseObjectChangeAttributeId", "ValueChangeType", "StringValue")
    VALUES (gen_random_uuid(), attr_change_id, 2, 'Platform Engineering Team');  -- Remove

    INSERT INTO "MetaverseObjectChangeAttributeValues" ("Id", "MetaverseObjectChangeAttributeId", "ValueChangeType", "StringValue")
    VALUES (gen_random_uuid(), attr_change_id, 1, 'Software Engineers');  -- Add (back to original)

    -- Change 14: (removed - consolidated into Change 12)

    -- ========================================================================
    -- SUCCESS
    -- ========================================================================

    RAISE NOTICE '';
    RAISE NOTICE '=== SUCCESS! Change History Created ===';
    RAISE NOTICE '';
    RAISE NOTICE 'Created:';
    RAISE NOTICE '  - 8 Users: Alice, Bob, Charlie, Diana, Eve, Frank, Grace, Henry';
    RAISE NOTICE '  - Alice (User) with 4 changes';
    RAISE NOTICE '  - Bob (User) with 3 changes (including Manager reference)';
    RAISE NOTICE '  - Software Engineers (Group) with 11 changes';
    RAISE NOTICE '    * Name changes: Engineers -> Software Engineers -> Platform Engineering Team -> Software Engineers';
    RAISE NOTICE '    * Description updates showing group evolution';
    RAISE NOTICE '    * BULK member additions: Change 2 (3 users), Change 5 (3 users), Change 10 (3 users)';
    RAISE NOTICE '    * BULK member removal: Change 12 (3 users)';
    RAISE NOTICE '    * Member operations: Alice, Bob, Charlie (3x), Diana, Eve, Frank, Grace, Henry';
    RAISE NOTICE '    * Final members: Alice, Bob, Charlie, Grace, Henry';
    RAISE NOTICE '';
    RAISE NOTICE 'UI Testing URLs:';
    RAISE NOTICE '  Alice: http://localhost:5200/t/users/v/%', alice_id;
    RAISE NOTICE '  Bob: http://localhost:5200/t/users/v/%', bob_id;
    RAISE NOTICE '  Engineers: http://localhost:5200/t/groups/v/%', engineers_group_id;
    RAISE NOTICE '';
END $$;
