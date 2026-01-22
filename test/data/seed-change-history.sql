-- ============================================================================
-- JIM Change History Test Data Generator
-- ============================================================================
-- Purpose: Seeds realistic MVO and CSO change history for UI testing
-- Usage: Run this against your development database after running integration tests
-- Maintenance: If schema changes, regenerate using instructions in CLAUDE.md
-- ============================================================================

-- This script assumes you have a working JIM database with at least:
-- - Person and Group metaverse object types
-- - Some existing MVOs (from integration tests or manual setup)
-- - Metaverse attributes defined

-- NOTE: Replace the UUIDs below with actual MVOs from your database
-- You can find MVO IDs by querying: SELECT "Id", "DisplayName" FROM "MetaverseObjects" LIMIT 10;

DO $$
DECLARE
    -- REPLACE THESE WITH ACTUAL IDs FROM YOUR DATABASE
    person_type_id INT;
    group_type_id INT;
    alice_mvo_id UUID;
    bob_mvo_id UUID;
    engineers_group_id UUID;
    platform_group_id UUID;

    -- Attribute IDs
    attr_firstname_id INT;
    attr_lastname_id INT;
    attr_email_id INT;
    attr_department_id INT;
    attr_jobtitle_id INT;
    attr_employeenumber_id INT;
    attr_hiredate_id INT;
    attr_salary_id INT;
    attr_isactive_id INT;
    attr_manager_id INT;
    attr_name_id INT;
    attr_description_id INT;
    attr_members_id INT;

    -- Change IDs (for linking)
    alice_change1_id UUID;
    alice_change2_id UUID;
    alice_change3_id UUID;
    alice_change4_id UUID;
    alice_change5_id UUID;
    bob_change1_id UUID;
    bob_change2_id UUID;
    bob_change3_id UUID;
    bob_change4_id UUID;
    bob_change5_id UUID;
    bob_change6_id UUID;
    bob_change7_id UUID;
    engineers_change1_id UUID;
    engineers_change2_id UUID;
    engineers_change3_id UUID;
    engineers_change4_id UUID;
    platform_change1_id UUID;

    -- Attribute change IDs
    alice_change1_attr_jobtitle_id UUID;
    alice_change1_attr_salary_id UUID;
    alice_change2_attr_department_id UUID;

BEGIN
    -- ========================================================================
    -- STEP 1: Find existing object types and MVOs
    -- ========================================================================

    -- Find Person type
    SELECT "Id" INTO person_type_id FROM "MetaverseObjectTypes" WHERE "Name" = 'Person' LIMIT 1;
    IF person_type_id IS NULL THEN
        RAISE NOTICE 'WARNING: Person type not found. Creating placeholder...';
        INSERT INTO "MetaverseObjectTypes" ("Name", "PluralName", "Origin", "DeletionRule", "DateCreated")
        VALUES ('Person', 'People', 1, 1, NOW())
        RETURNING "Id" INTO person_type_id;
    END IF;

    -- Find Group type
    SELECT "Id" INTO group_type_id FROM "MetaverseObjectTypes" WHERE "Name" = 'Group' LIMIT 1;
    IF group_type_id IS NULL THEN
        RAISE NOTICE 'WARNING: Group type not found. Creating placeholder...';
        INSERT INTO "MetaverseObjectTypes" ("Name", "PluralName", "Origin", "DeletionRule", "DateCreated")
        VALUES ('Group', 'Groups', 1, 1, NOW())
        RETURNING "Id" INTO group_type_id;
    END IF;

    -- Find or create Alice MVO
    SELECT "Id" INTO alice_mvo_id FROM "MetaverseObjects" WHERE "TypeId" = person_type_id AND "DisplayName" ILIKE '%Alice%' LIMIT 1;
    IF alice_mvo_id IS NULL THEN
        RAISE NOTICE 'Creating Alice MVO...';
        INSERT INTO "MetaverseObjects" ("Id", "TypeId", "Origin", "DateCreated")
        VALUES (gen_random_uuid(), person_type_id, 1, NOW() - INTERVAL '30 days')
        RETURNING "Id" INTO alice_mvo_id;
    END IF;

    -- Find or create Bob MVO
    SELECT "Id" INTO bob_mvo_id FROM "MetaverseObjects" WHERE "TypeId" = person_type_id AND "DisplayName" ILIKE '%Bob%' LIMIT 1;
    IF bob_mvo_id IS NULL THEN
        RAISE NOTICE 'Creating Bob MVO...';
        INSERT INTO "MetaverseObjects" ("Id", "TypeId", "Origin", "DateCreated")
        VALUES (gen_random_uuid(), person_type_id, 1, NOW() - INTERVAL '25 days')
        RETURNING "Id" INTO bob_mvo_id;
    END IF;

    -- Find or create Engineers Group
    SELECT "Id" INTO engineers_group_id FROM "MetaverseObjects" WHERE "TypeId" = group_type_id AND "DisplayName" ILIKE '%Engineer%' LIMIT 1;
    IF engineers_group_id IS NULL THEN
        RAISE NOTICE 'Creating Engineers Group MVO...';
        INSERT INTO "MetaverseObjects" ("Id", "TypeId", "Origin", "DateCreated")
        VALUES (gen_random_uuid(), group_type_id, 1, NOW() - INTERVAL '20 days')
        RETURNING "Id" INTO engineers_group_id;
    END IF;

    -- Find or create Platform Team Group
    SELECT "Id" INTO platform_group_id FROM "MetaverseObjects" WHERE "TypeId" = group_type_id AND "DisplayName" ILIKE '%Platform%' LIMIT 1;
    IF platform_group_id IS NULL THEN
        RAISE NOTICE 'Creating Platform Team Group MVO...';
        INSERT INTO "MetaverseObjects" ("Id", "TypeId", "Origin", "DateCreated")
        VALUES (gen_random_uuid(), group_type_id, 1, NOW() - INTERVAL '15 days')
        RETURNING "Id" INTO platform_group_id;
    END IF;

    -- ========================================================================
    -- STEP 2: Find or create attributes
    -- ========================================================================

    -- Person attributes
    SELECT "Id" INTO attr_firstname_id FROM "MetaverseAttributes" WHERE "Name" = 'FirstName' LIMIT 1;
    IF attr_firstname_id IS NULL THEN
        INSERT INTO "MetaverseAttributes" ("Name", "Type", "AttributePlurality") VALUES ('FirstName', 1, 1) RETURNING "Id" INTO attr_firstname_id;
    END IF;

    SELECT "Id" INTO attr_lastname_id FROM "MetaverseAttributes" WHERE "Name" = 'LastName' LIMIT 1;
    IF attr_lastname_id IS NULL THEN
        INSERT INTO "MetaverseAttributes" ("Name", "Type", "AttributePlurality") VALUES ('LastName', 1, 1) RETURNING "Id" INTO attr_lastname_id;
    END IF;

    SELECT "Id" INTO attr_email_id FROM "MetaverseAttributes" WHERE "Name" = 'Email' LIMIT 1;
    IF attr_email_id IS NULL THEN
        INSERT INTO "MetaverseAttributes" ("Name", "Type", "AttributePlurality") VALUES ('Email', 1, 1) RETURNING "Id" INTO attr_email_id;
    END IF;

    SELECT "Id" INTO attr_department_id FROM "MetaverseAttributes" WHERE "Name" = 'Department' LIMIT 1;
    IF attr_department_id IS NULL THEN
        INSERT INTO "MetaverseAttributes" ("Name", "Type", "AttributePlurality") VALUES ('Department', 1, 1) RETURNING "Id" INTO attr_department_id;
    END IF;

    SELECT "Id" INTO attr_jobtitle_id FROM "MetaverseAttributes" WHERE "Name" = 'JobTitle' LIMIT 1;
    IF attr_jobtitle_id IS NULL THEN
        INSERT INTO "MetaverseAttributes" ("Name", "Type", "AttributePlurality") VALUES ('JobTitle', 1, 1) RETURNING "Id" INTO attr_jobtitle_id;
    END IF;

    SELECT "Id" INTO attr_employeenumber_id FROM "MetaverseAttributes" WHERE "Name" = 'EmployeeNumber' LIMIT 1;
    IF attr_employeenumber_id IS NULL THEN
        INSERT INTO "MetaverseAttributes" ("Name", "Type", "AttributePlurality") VALUES ('EmployeeNumber', 2, 1) RETURNING "Id" INTO attr_employeenumber_id;
    END IF;

    SELECT "Id" INTO attr_hiredate_id FROM "MetaverseAttributes" WHERE "Name" = 'HireDate' LIMIT 1;
    IF attr_hiredate_id IS NULL THEN
        INSERT INTO "MetaverseAttributes" ("Name", "Type", "AttributePlurality") VALUES ('HireDate', 4, 1) RETURNING "Id" INTO attr_hiredate_id;
    END IF;

    SELECT "Id" INTO attr_salary_id FROM "MetaverseAttributes" WHERE "Name" = 'Salary' LIMIT 1;
    IF attr_salary_id IS NULL THEN
        INSERT INTO "MetaverseAttributes" ("Name", "Type", "AttributePlurality") VALUES ('Salary', 3, 1) RETURNING "Id" INTO attr_salary_id;
    END IF;

    SELECT "Id" INTO attr_isactive_id FROM "MetaverseAttributes" WHERE "Name" = 'IsActive' LIMIT 1;
    IF attr_isactive_id IS NULL THEN
        INSERT INTO "MetaverseAttributes" ("Name", "Type", "AttributePlurality") VALUES ('IsActive', 8, 1) RETURNING "Id" INTO attr_isactive_id;
    END IF;

    SELECT "Id" INTO attr_manager_id FROM "MetaverseAttributes" WHERE "Name" = 'Manager' LIMIT 1;
    IF attr_manager_id IS NULL THEN
        INSERT INTO "MetaverseAttributes" ("Name", "Type", "AttributePlurality") VALUES ('Manager', 7, 1) RETURNING "Id" INTO attr_manager_id;
    END IF;

    -- Group attributes
    SELECT "Id" INTO attr_name_id FROM "MetaverseAttributes" WHERE "Name" = 'Name' LIMIT 1;
    IF attr_name_id IS NULL THEN
        INSERT INTO "MetaverseAttributes" ("Name", "Type", "AttributePlurality") VALUES ('Name', 1, 1) RETURNING "Id" INTO attr_name_id;
    END IF;

    SELECT "Id" INTO attr_description_id FROM "MetaverseAttributes" WHERE "Name" = 'Description' LIMIT 1;
    IF attr_description_id IS NULL THEN
        INSERT INTO "MetaverseAttributes" ("Name", "Type", "AttributePlurality") VALUES ('Description', 1, 1) RETURNING "Id" INTO attr_description_id;
    END IF;

    SELECT "Id" INTO attr_members_id FROM "MetaverseAttributes" WHERE "Name" = 'Members' LIMIT 1;
    IF attr_members_id IS NULL THEN
        INSERT INTO "MetaverseAttributes" ("Name", "Type", "AttributePlurality") VALUES ('Members', 7, 2) RETURNING "Id" INTO attr_members_id;
    END IF;

    RAISE NOTICE '=== Found/Created IDs ===';
    RAISE NOTICE 'Person Type: %, Group Type: %', person_type_id, group_type_id;
    RAISE NOTICE 'Alice: %, Bob: %', alice_mvo_id, bob_mvo_id;
    RAISE NOTICE 'Engineers: %, Platform: %', engineers_group_id, platform_group_id;

    -- ========================================================================
    -- STEP 3: Create Change History for Alice (7 changes)
    -- ========================================================================

    RAISE NOTICE 'Creating Alice change history...';

    -- Change 1: Initial projection (30 days ago)
    alice_change1_id := gen_random_uuid();
    INSERT INTO "MetaverseObjectChanges" ("Id", "MetaverseObjectId", "ChangeType", "ChangeTime", "ChangeInitiatorType")
    VALUES (alice_change1_id, alice_mvo_id, 1, NOW() - INTERVAL '30 days', 2); -- Added, SynchronisationRule

    -- Change 2: Promotion to Lead Engineer (25 days ago)
    alice_change2_id := gen_random_uuid();
    INSERT INTO "MetaverseObjectChanges" ("Id", "MetaverseObjectId", "ChangeType", "ChangeTime", "ChangeInitiatorType")
    VALUES (alice_change2_id, alice_mvo_id, 2, NOW() - INTERVAL '25 days', 2); -- Updated, SynchronisationRule

    alice_change1_attr_jobtitle_id := gen_random_uuid();
    INSERT INTO "MetaverseObjectChangeAttributes" ("Id", "MetaverseObjectChangeId", "AttributeId")
    VALUES (alice_change1_attr_jobtitle_id, alice_change2_id, attr_jobtitle_id);

    INSERT INTO "MetaverseObjectChangeAttributeValues" ("MetaverseObjectChangeAttributeId", "ValueChangeType", "StringValue")
    VALUES (alice_change1_attr_jobtitle_id, 2, 'Senior Software Engineer'); -- Remove old

    INSERT INTO "MetaverseObjectChangeAttributeValues" ("MetaverseObjectChangeAttributeId", "ValueChangeType", "StringValue")
    VALUES (alice_change1_attr_jobtitle_id, 1, 'Lead Software Engineer'); -- Add new

    alice_change1_attr_salary_id := gen_random_uuid();
    INSERT INTO "MetaverseObjectChangeAttributes" ("Id", "MetaverseObjectChangeId", "AttributeId")
    VALUES (alice_change1_attr_salary_id, alice_change2_id, attr_salary_id);

    INSERT INTO "MetaverseObjectChangeAttributeValues" ("MetaverseObjectChangeAttributeId", "ValueChangeType", "IntValue")
    VALUES (alice_change1_attr_salary_id, 2, 125000); -- Remove old

    INSERT INTO "MetaverseObjectChangeAttributeValues" ("MetaverseObjectChangeAttributeId", "ValueChangeType", "IntValue")
    VALUES (alice_change1_attr_salary_id, 1, 135000); -- Add new

    -- Change 3: Department change (20 days ago)
    alice_change3_id := gen_random_uuid();
    INSERT INTO "MetaverseObjectChanges" ("Id", "MetaverseObjectId", "ChangeType", "ChangeTime", "ChangeInitiatorType")
    VALUES (alice_change3_id, alice_mvo_id, 2, NOW() - INTERVAL '20 days', 2);

    alice_change2_attr_department_id := gen_random_uuid();
    INSERT INTO "MetaverseObjectChangeAttributes" ("Id", "MetaverseObjectChangeId", "AttributeId")
    VALUES (alice_change2_attr_department_id, alice_change3_id, attr_department_id);

    INSERT INTO "MetaverseObjectChangeAttributeValues" ("MetaverseObjectChangeAttributeId", "ValueChangeType", "StringValue")
    VALUES (alice_change2_attr_department_id, 2, 'Engineering');

    INSERT INTO "MetaverseObjectChangeAttributeValues" ("MetaverseObjectChangeAttributeId", "ValueChangeType", "StringValue")
    VALUES (alice_change2_attr_department_id, 1, 'Engineering - Platform Team');

    -- Change 4: Email update (15 days ago)
    alice_change4_id := gen_random_uuid();
    INSERT INTO "MetaverseObjectChanges" ("Id", "MetaverseObjectId", "ChangeType", "ChangeTime", "ChangeInitiatorType")
    VALUES (alice_change4_id, alice_mvo_id, 2, NOW() - INTERVAL '15 days', 2);

    DECLARE
        alice_change4_attr_email_id UUID := gen_random_uuid();
    BEGIN
        INSERT INTO "MetaverseObjectChangeAttributes" ("Id", "MetaverseObjectChangeId", "AttributeId")
        VALUES (alice_change4_attr_email_id, alice_change4_id, attr_email_id);

        INSERT INTO "MetaverseObjectChangeAttributeValues" ("MetaverseObjectChangeAttributeId", "ValueChangeType", "StringValue")
        VALUES (alice_change4_attr_email_id, 2, 'alice.anderson@contoso.com');

        INSERT INTO "MetaverseObjectChangeAttributeValues" ("MetaverseObjectChangeAttributeId", "ValueChangeType", "StringValue")
        VALUES (alice_change4_attr_email_id, 1, 'alice.anderson@contoso.enterprise.com');
    END;

    -- Change 5: Promotion to Manager (10 days ago)
    alice_change5_id := gen_random_uuid();
    INSERT INTO "MetaverseObjectChanges" ("Id", "MetaverseObjectId", "ChangeType", "ChangeTime", "ChangeInitiatorType")
    VALUES (alice_change5_id, alice_mvo_id, 2, NOW() - INTERVAL '10 days', 2);

    DECLARE
        alice_change5_attr_jobtitle_id UUID := gen_random_uuid();
        alice_change5_attr_salary_id UUID := gen_random_uuid();
    BEGIN
        INSERT INTO "MetaverseObjectChangeAttributes" ("Id", "MetaverseObjectChangeId", "AttributeId")
        VALUES (alice_change5_attr_jobtitle_id, alice_change5_id, attr_jobtitle_id);

        INSERT INTO "MetaverseObjectChangeAttributeValues" ("MetaverseObjectChangeAttributeId", "ValueChangeType", "StringValue")
        VALUES (alice_change5_attr_jobtitle_id, 2, 'Lead Software Engineer');

        INSERT INTO "MetaverseObjectChangeAttributeValues" ("MetaverseObjectChangeAttributeId", "ValueChangeType", "StringValue")
        VALUES (alice_change5_attr_jobtitle_id, 1, 'Engineering Manager');

        INSERT INTO "MetaverseObjectChangeAttributes" ("Id", "MetaverseObjectChangeId", "AttributeId")
        VALUES (alice_change5_attr_salary_id, alice_change5_id, attr_salary_id);

        INSERT INTO "MetaverseObjectChangeAttributeValues" ("MetaverseObjectChangeAttributeId", "ValueChangeType", "IntValue")
        VALUES (alice_change5_attr_salary_id, 2, 135000);

        INSERT INTO "MetaverseObjectChangeAttributeValues" ("MetaverseObjectChangeAttributeId", "ValueChangeType", "IntValue")
        VALUES (alice_change5_attr_salary_id, 1, 155000);
    END;

    -- ========================================================================
    -- STEP 4: Create Change History for Bob (7 changes including manager ref)
    -- ========================================================================

    RAISE NOTICE 'Creating Bob change history...';

    -- Change 1: Initial projection (25 days ago)
    bob_change1_id := gen_random_uuid();
    INSERT INTO "MetaverseObjectChanges" ("Id", "MetaverseObjectId", "ChangeType", "ChangeTime", "ChangeInitiatorType")
    VALUES (bob_change1_id, bob_mvo_id, 1, NOW() - INTERVAL '25 days', 2);

    -- Change 2: Manager assigned - Alice (23 days ago)
    bob_change2_id := gen_random_uuid();
    INSERT INTO "MetaverseObjectChanges" ("Id", "MetaverseObjectId", "ChangeType", "ChangeTime", "ChangeInitiatorType")
    VALUES (bob_change2_id, bob_mvo_id, 2, NOW() - INTERVAL '23 days', 2);

    DECLARE
        bob_change2_attr_manager_id UUID := gen_random_uuid();
    BEGIN
        INSERT INTO "MetaverseObjectChangeAttributes" ("Id", "MetaverseObjectChangeId", "AttributeId")
        VALUES (bob_change2_attr_manager_id, bob_change2_id, attr_manager_id);

        INSERT INTO "MetaverseObjectChangeAttributeValues" ("MetaverseObjectChangeAttributeId", "ValueChangeType", "ReferenceValueId")
        VALUES (bob_change2_attr_manager_id, 1, alice_mvo_id); -- Add Alice as manager
    END;

    -- Change 3: Department change (18 days ago)
    bob_change3_id := gen_random_uuid();
    INSERT INTO "MetaverseObjectChanges" ("Id", "MetaverseObjectId", "ChangeType", "ChangeTime", "ChangeInitiatorType")
    VALUES (bob_change3_id, bob_mvo_id, 2, NOW() - INTERVAL '18 days', 2);

    DECLARE
        bob_change3_attr_dept_id UUID := gen_random_uuid();
    BEGIN
        INSERT INTO "MetaverseObjectChangeAttributes" ("Id", "MetaverseObjectChangeId", "AttributeId")
        VALUES (bob_change3_attr_dept_id, bob_change3_id, attr_department_id);

        INSERT INTO "MetaverseObjectChangeAttributeValues" ("MetaverseObjectChangeAttributeId", "ValueChangeType", "StringValue")
        VALUES (bob_change3_attr_dept_id, 2, 'Engineering');

        INSERT INTO "MetaverseObjectChangeAttributeValues" ("MetaverseObjectChangeAttributeId", "ValueChangeType", "StringValue")
        VALUES (bob_change3_attr_dept_id, 1, 'Engineering - Backend Services');
    END;

    -- Change 4: Promotion (14 days ago)
    bob_change4_id := gen_random_uuid();
    INSERT INTO "MetaverseObjectChanges" ("Id", "MetaverseObjectId", "ChangeType", "ChangeTime", "ChangeInitiatorType")
    VALUES (bob_change4_id, bob_mvo_id, 2, NOW() - INTERVAL '14 days', 2);

    DECLARE
        bob_change4_attr_title_id UUID := gen_random_uuid();
    BEGIN
        INSERT INTO "MetaverseObjectChangeAttributes" ("Id", "MetaverseObjectChangeId", "AttributeId")
        VALUES (bob_change4_attr_title_id, bob_change4_id, attr_jobtitle_id);

        INSERT INTO "MetaverseObjectChangeAttributeValues" ("MetaverseObjectChangeAttributeId", "ValueChangeType", "StringValue")
        VALUES (bob_change4_attr_title_id, 2, 'Software Engineer');

        INSERT INTO "MetaverseObjectChangeAttributeValues" ("MetaverseObjectChangeAttributeId", "ValueChangeType", "StringValue")
        VALUES (bob_change4_attr_title_id, 1, 'Senior Software Engineer');
    END;

    -- Change 5: Salary increase (10 days ago)
    bob_change5_id := gen_random_uuid();
    INSERT INTO "MetaverseObjectChanges" ("Id", "MetaverseObjectId", "ChangeType", "ChangeTime", "ChangeInitiatorType")
    VALUES (bob_change5_id, bob_mvo_id, 2, NOW() - INTERVAL '10 days', 2);

    DECLARE
        bob_change5_attr_salary_id UUID := gen_random_uuid();
    BEGIN
        INSERT INTO "MetaverseObjectChangeAttributes" ("Id", "MetaverseObjectChangeId", "AttributeId")
        VALUES (bob_change5_attr_salary_id, bob_change5_id, attr_salary_id);

        INSERT INTO "MetaverseObjectChangeAttributeValues" ("MetaverseObjectChangeAttributeId", "ValueChangeType", "IntValue")
        VALUES (bob_change5_attr_salary_id, 2, 95000);

        INSERT INTO "MetaverseObjectChangeAttributeValues" ("MetaverseObjectChangeAttributeId", "ValueChangeType", "IntValue")
        VALUES (bob_change5_attr_salary_id, 1, 110000);
    END;

    -- Change 6: Manager removed (7 days ago)
    bob_change6_id := gen_random_uuid();
    INSERT INTO "MetaverseObjectChanges" ("Id", "MetaverseObjectId", "ChangeType", "ChangeTime", "ChangeInitiatorType")
    VALUES (bob_change6_id, bob_mvo_id, 2, NOW() - INTERVAL '7 days', 2);

    DECLARE
        bob_change6_attr_manager_id UUID := gen_random_uuid();
    BEGIN
        INSERT INTO "MetaverseObjectChangeAttributes" ("Id", "MetaverseObjectChangeId", "AttributeId")
        VALUES (bob_change6_attr_manager_id, bob_change6_id, attr_manager_id);

        INSERT INTO "MetaverseObjectChangeAttributeValues" ("MetaverseObjectChangeAttributeId", "ValueChangeType", "ReferenceValueId")
        VALUES (bob_change6_attr_manager_id, 2, alice_mvo_id); -- Remove Alice
    END;

    -- Change 7: Manager reassigned (3 days ago)
    bob_change7_id := gen_random_uuid();
    INSERT INTO "MetaverseObjectChanges" ("Id", "MetaverseObjectId", "ChangeType", "ChangeTime", "ChangeInitiatorType")
    VALUES (bob_change7_id, bob_mvo_id, 2, NOW() - INTERVAL '3 days', 2);

    DECLARE
        bob_change7_attr_manager_id UUID := gen_random_uuid();
    BEGIN
        INSERT INTO "MetaverseObjectChangeAttributes" ("Id", "MetaverseObjectChangeId", "AttributeId")
        VALUES (bob_change7_attr_manager_id, bob_change7_id, attr_manager_id);

        INSERT INTO "MetaverseObjectChangeAttributeValues" ("MetaverseObjectChangeAttributeId", "ValueChangeType", "ReferenceValueId")
        VALUES (bob_change7_attr_manager_id, 1, alice_mvo_id); -- Add Alice back
    END;

    -- ========================================================================
    -- STEP 5: Create Change History for Groups
    -- ========================================================================

    RAISE NOTICE 'Creating group change history...';

    -- Engineers Group - Change 1: Initial projection (20 days ago)
    engineers_change1_id := gen_random_uuid();
    INSERT INTO "MetaverseObjectChanges" ("Id", "MetaverseObjectId", "ChangeType", "ChangeTime", "ChangeInitiatorType")
    VALUES (engineers_change1_id, engineers_group_id, 1, NOW() - INTERVAL '20 days', 2);

    -- Engineers Group - Change 2: Name changed (18 days ago)
    engineers_change2_id := gen_random_uuid();
    INSERT INTO "MetaverseObjectChanges" ("Id", "MetaverseObjectId", "ChangeType", "ChangeTime", "ChangeInitiatorType")
    VALUES (engineers_change2_id, engineers_group_id, 2, NOW() - INTERVAL '18 days', 2);

    DECLARE
        engineers_change2_attr_name_id UUID := gen_random_uuid();
    BEGIN
        INSERT INTO "MetaverseObjectChangeAttributes" ("Id", "MetaverseObjectChangeId", "AttributeId")
        VALUES (engineers_change2_attr_name_id, engineers_change2_id, attr_name_id);

        INSERT INTO "MetaverseObjectChangeAttributeValues" ("MetaverseObjectChangeAttributeId", "ValueChangeType", "StringValue")
        VALUES (engineers_change2_attr_name_id, 2, 'Engineers');

        INSERT INTO "MetaverseObjectChangeAttributeValues" ("MetaverseObjectChangeAttributeId", "ValueChangeType", "StringValue")
        VALUES (engineers_change2_attr_name_id, 1, 'Software Engineers');
    END;

    -- Engineers Group - Change 3: Alice added as member (15 days ago)
    engineers_change3_id := gen_random_uuid();
    INSERT INTO "MetaverseObjectChanges" ("Id", "MetaverseObjectId", "ChangeType", "ChangeTime", "ChangeInitiatorType")
    VALUES (engineers_change3_id, engineers_group_id, 2, NOW() - INTERVAL '15 days', 2);

    DECLARE
        engineers_change3_attr_members_id UUID := gen_random_uuid();
    BEGIN
        INSERT INTO "MetaverseObjectChangeAttributes" ("Id", "MetaverseObjectChangeId", "AttributeId")
        VALUES (engineers_change3_attr_members_id, engineers_change3_id, attr_members_id);

        INSERT INTO "MetaverseObjectChangeAttributeValues" ("MetaverseObjectChangeAttributeId", "ValueChangeType", "ReferenceValueId")
        VALUES (engineers_change3_attr_members_id, 1, alice_mvo_id);
    END;

    -- Engineers Group - Change 4: Bob added as member (10 days ago)
    engineers_change4_id := gen_random_uuid();
    INSERT INTO "MetaverseObjectChanges" ("Id", "MetaverseObjectId", "ChangeType", "ChangeTime", "ChangeInitiatorType")
    VALUES (engineers_change4_id, engineers_group_id, 2, NOW() - INTERVAL '10 days', 2);

    DECLARE
        engineers_change4_attr_members_id UUID := gen_random_uuid();
    BEGIN
        INSERT INTO "MetaverseObjectChangeAttributes" ("Id", "MetaverseObjectChangeId", "AttributeId")
        VALUES (engineers_change4_attr_members_id, engineers_change4_id, attr_members_id);

        INSERT INTO "MetaverseObjectChangeAttributeValues" ("MetaverseObjectChangeAttributeId", "ValueChangeType", "ReferenceValueId")
        VALUES (engineers_change4_attr_members_id, 1, bob_mvo_id);
    END;

    -- Platform Team - Change 1: Initial projection (15 days ago)
    platform_change1_id := gen_random_uuid();
    INSERT INTO "MetaverseObjectChanges" ("Id", "MetaverseObjectId", "ChangeType", "ChangeTime", "ChangeInitiatorType")
    VALUES (platform_change1_id, platform_group_id, 1, NOW() - INTERVAL '15 days', 2);

    RAISE NOTICE '=== Change History Created Successfully ===';
    RAISE NOTICE 'Alice changes: 5, Bob changes: 7, Engineers changes: 4, Platform changes: 1';
    RAISE NOTICE '';
    RAISE NOTICE 'UI Testing URLs:';
    RAISE NOTICE '  Alice: /t/people/v/%', alice_mvo_id;
    RAISE NOTICE '  Bob: /t/people/v/%', bob_mvo_id;
    RAISE NOTICE '  Engineers: /t/groups/v/%', engineers_group_id;
    RAISE NOTICE '  Platform Team: /t/groups/v/%', platform_group_id;

END $$;
