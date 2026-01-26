-- =============================================================================
-- Seed Script: History Retention Cleanup Activities
-- =============================================================================
-- Creates sample HistoryRetentionCleanup activities for UI validation.
-- Run against your development/test database:
--   docker compose exec jim.database psql -U jim -d jim -f /workspaces/JIM/test/data/seed-history-cleanup-activity.sql
-- =============================================================================

DO $$
DECLARE
    v_activity_id_1 uuid := gen_random_uuid();
    v_activity_id_2 uuid := gen_random_uuid();
    v_activity_id_3 uuid := gen_random_uuid();
BEGIN
    -- =========================================================================
    -- Activity 1: Completed housekeeping cleanup with records deleted
    -- =========================================================================
    INSERT INTO "Activities" (
        "Id",
        "ParentActivityId",
        "Created",
        "Executed",
        "InitiatedByType",
        "InitiatedByName",
        "TargetName",
        "TargetType",
        "TargetOperationType",
        "Status",
        "Message",
        "ExecutionTime",
        "TotalActivityTime",
        "ObjectsToProcess",
        "ObjectsProcessed",
        "DeletedCsoChangeCount",
        "DeletedMvoChangeCount",
        "DeletedActivityCount",
        "DeletedRecordsFromDate",
        "DeletedRecordsToDate"
    ) VALUES (
        v_activity_id_1,
        NULL,
        NOW() - INTERVAL '2 hours',
        NOW() - INTERVAL '2 hours',
        0,  -- NotSet (system/housekeeping)
        'System',
        'Change history and activity retention cleanup',
        10, -- HistoryRetentionCleanup
        3,  -- Delete
        2,  -- Complete
        'Change history and activity retention cleanup',
        INTERVAL '3.5 seconds',
        INTERVAL '3.5 seconds',
        0,      -- ObjectsToProcess (not applicable for cleanup)
        0,      -- ObjectsProcessed (not applicable for cleanup)
        847,    -- CSO changes deleted
        312,    -- MVO changes deleted
        45,     -- Activities deleted
        NOW() - INTERVAL '180 days',   -- oldest record deleted
        NOW() - INTERVAL '91 days'     -- newest record deleted
    );

    RAISE NOTICE 'Created Activity 1 (housekeeping, records deleted): %', v_activity_id_1;

    -- =========================================================================
    -- Activity 2: Completed housekeeping cleanup with zero records
    -- =========================================================================
    INSERT INTO "Activities" (
        "Id",
        "ParentActivityId",
        "Created",
        "Executed",
        "InitiatedByType",
        "InitiatedByName",
        "TargetName",
        "TargetType",
        "TargetOperationType",
        "Status",
        "Message",
        "ExecutionTime",
        "TotalActivityTime",
        "ObjectsToProcess",
        "ObjectsProcessed",
        "DeletedCsoChangeCount",
        "DeletedMvoChangeCount",
        "DeletedActivityCount",
        "DeletedRecordsFromDate",
        "DeletedRecordsToDate"
    ) VALUES (
        v_activity_id_2,
        NULL,
        NOW() - INTERVAL '1 hour',
        NOW() - INTERVAL '1 hour',
        0,  -- NotSet (system/housekeeping)
        'System',
        'Change history and activity retention cleanup',
        10, -- HistoryRetentionCleanup
        3,  -- Delete
        2,  -- Complete
        'Change history and activity retention cleanup',
        INTERVAL '0.2 seconds',
        INTERVAL '0.2 seconds',
        0,      -- ObjectsToProcess (not applicable for cleanup)
        0,      -- ObjectsProcessed (not applicable for cleanup)
        0,      -- no CSO changes
        0,      -- no MVO changes
        0,      -- no activities
        NULL,   -- no date range
        NULL
    );

    RAISE NOTICE 'Created Activity 2 (housekeeping, zero records): %', v_activity_id_2;

    -- =========================================================================
    -- Activity 3: Manual API-triggered cleanup with records deleted
    -- =========================================================================
    INSERT INTO "Activities" (
        "Id",
        "ParentActivityId",
        "Created",
        "Executed",
        "InitiatedByType",
        "InitiatedByName",
        "TargetName",
        "TargetType",
        "TargetOperationType",
        "Status",
        "Message",
        "ExecutionTime",
        "TotalActivityTime",
        "ObjectsToProcess",
        "ObjectsProcessed",
        "DeletedCsoChangeCount",
        "DeletedMvoChangeCount",
        "DeletedActivityCount",
        "DeletedRecordsFromDate",
        "DeletedRecordsToDate"
    ) VALUES (
        v_activity_id_3,
        NULL,
        NOW() - INTERVAL '30 minutes',
        NOW() - INTERVAL '30 minutes',
        2,  -- ApiKey
        'Infrastructure API Key',
        'Change history and activity retention cleanup',
        10, -- HistoryRetentionCleanup
        3,  -- Delete
        2,  -- Complete
        'Change history and activity retention cleanup',
        INTERVAL '8.7 seconds',
        INTERVAL '8.7 seconds',
        0,      -- ObjectsToProcess (not applicable for cleanup)
        0,      -- ObjectsProcessed (not applicable for cleanup)
        2341,   -- CSO changes deleted
        1089,   -- MVO changes deleted
        178,    -- Activities deleted
        NOW() - INTERVAL '365 days',   -- oldest record deleted
        NOW() - INTERVAL '90 days'     -- newest record deleted
    );

    RAISE NOTICE 'Created Activity 3 (API-triggered, records deleted): %', v_activity_id_3;

    -- =========================================================================
    -- Output navigation URLs
    -- =========================================================================
    RAISE NOTICE '';
    RAISE NOTICE '=== View in JIM ===';
    RAISE NOTICE 'Activity List:  /activity (search "History Retention Cleanup")';
    RAISE NOTICE 'Activity 1:     /activity/%', v_activity_id_1;
    RAISE NOTICE 'Activity 2:     /activity/%', v_activity_id_2;
    RAISE NOTICE 'Activity 3:     /activity/%', v_activity_id_3;
END $$;
