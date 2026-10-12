// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Services;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.PostgresData;
using JIM.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using NpgsqlTypes;

namespace JIM.Worker.Tests.Migrations;

/// <summary>
/// Proves the <c>RepairApiKeyInitiatorsRecordedAsUser</c> migration's data repair (#2066): REST calls made with an API
/// key through the Schedules, Example Data Template and factory reset endpoints recorded their initiator as a person
/// (User) carrying the API key's id, as did every record a Schedule run started that way went on to write. After the
/// upgrade every such row names the API key, wherever JIM can still prove the id was an API key's, and nothing else
/// changes.
/// <para>
/// Runs against its own scratch database, created and dropped per fixture, migrated in two phases exactly as
/// <see cref="AddScheduleFailureHandlingMigrationDatabaseTests"/> does: first to the previous migration, where the
/// rows are seeded as an upgrading customer's database holds them, then to head, so the migration under test runs.
/// The seeding is raw SQL against the previous migration's schema rather than the EF model, which describes head and
/// drifts from that schema as soon as a later migration touches one of these tables. Opt-in via the same
/// <c>JIM_TEST_RESET_*</c> environment variables as the other <c>RequiresPostgres</c> fixtures.
/// </para>
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
public class RepairApiKeyInitiatorsRecordedAsUserMigrationDatabaseTests
{
    private const string ScratchDatabaseName = "jim_2066_initiator_repair_test";
    private const string PreviousMigrationId = "20261012092508_AddConfigurationChangePreviewDeltaMatchCandidates";

    private const string LiveKeyName = "ci-key";
    private const string UserName = "Alice";

    private static readonly int UserType = (int)ActivityInitiatorType.User;
    private static readonly int ApiKeyType = (int)ActivityInitiatorType.ApiKey;
    private static readonly int SystemType = (int)ActivityInitiatorType.System;

    // The ids JIM holds. LiveKeyId is an API key that still exists; UserId is a genuine person's Metaverse Object.
    private readonly Guid _liveKeyId = Guid.NewGuid();
    private readonly Guid _userId = Guid.NewGuid();

    // Deleted API keys, each surviving in exactly one place (or none): the delete Activity's snapshot, an initiator
    // recorded correctly as an API key elsewhere, the configuration target of an API key Activity, and nowhere.
    private readonly Guid _keyInDeletionSnapshot = Guid.NewGuid();
    private readonly Guid _keyRecordedAsApiKeyElsewhere = Guid.NewGuid();
    private readonly Guid _keyAsConfigurationTarget = Guid.NewGuid();
    private readonly Guid _keyWithNoTrace = Guid.NewGuid();

    /// <summary>Every seeded row whose initiator the tests read, by label, with where to find it.</summary>
    private readonly Dictionary<string, SeededInitiator> _seeded = new();

    /// <summary>The initiator of every seeded row as it read straight after the upgrade.</summary>
    private Dictionary<string, InitiatorValues> _afterUpgrade = new();

    private string _adminConnectionString = null!;
    private string _scratchConnectionString = null!;

    /// <summary>
    /// Every triad the pre-#2147 helper's callers could reach, directly or through a Schedule run they started. Each
    /// row is seeded as (User, live key id, key name) and must read (ApiKey, live key id, key name) after the upgrade.
    /// </summary>
    private static IEnumerable<TestCaseData> RepairedTriads()
    {
        yield return Triad("Activities.InitiatedBy");
        yield return Triad("Schedules.CreatedBy");
        yield return Triad("Schedules.LastUpdatedBy");
        yield return Triad("ScheduleSteps.CreatedBy");
        yield return Triad("ScheduleSteps.LastUpdatedBy");
        yield return Triad("ScheduleExecutions.InitiatedBy");
        yield return Triad("ExampleDataTemplates.CreatedBy");
        yield return Triad("ExampleDataTemplates.LastUpdatedBy");
        yield return Triad("WorkerTasks.InitiatedBy");
        yield return Triad("MetaverseObjectChanges.InitiatedBy");
        yield return Triad("ConnectedSystemObjectChanges.InitiatedBy");
        yield return Triad("MetaverseObjects.DeletionInitiatedBy");
        // An export step that creates containers auto-selects them and stamps the Connected System with the run's
        // initiator (ConnectedSystemServer.RefreshAndAutoSelectContainersWithTriadAsync).
        yield return Triad("ConnectedSystems.LastUpdatedBy");

        static TestCaseData Triad(string label) => new TestCaseData(label).SetArgDisplayNames(label);
    }

    /// <summary>
    /// Deleted API keys whose id JIM can still prove was an API key's, each by a different piece of evidence.
    /// </summary>
    private static IEnumerable<TestCaseData> DeletedKeysJimCanStillProve()
    {
        yield return new TestCaseData("Activity by key named in its deletion snapshot")
            .SetArgDisplayNames("deletion snapshot");
        yield return new TestCaseData("Activity by key recorded as an API key elsewhere")
            .SetArgDisplayNames("recorded as an API key elsewhere");
        yield return new TestCaseData("Activity by key that was a configuration change's target")
            .SetArgDisplayNames("configuration change target");
    }

    [OneTimeSetUp]
    public async Task OneTimeSetUpAsync()
    {
        var dbName = Environment.GetEnvironmentVariable("JIM_TEST_RESET_DB");
        if (string.IsNullOrEmpty(dbName))
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL migration data-repair tests.");

        var host = Environment.GetEnvironmentVariable("JIM_TEST_RESET_HOST") ?? "localhost";
        var user = Environment.GetEnvironmentVariable("JIM_TEST_RESET_USER") ?? "postgres";
        var pass = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PASSWORD") ?? "postgres";
        var port = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PORT") ?? "5432";

        _adminConnectionString = $"Host={host};Port={port};Database={dbName};Username={user};Password={pass}";
        _scratchConnectionString = $"Host={host};Port={port};Database={ScratchDatabaseName};Username={user};Password={pass}";

        // WITH (FORCE) terminates any connection a previous aborted run left behind.
        await ExecuteAdminSqlAsync($"DROP DATABASE IF EXISTS {ScratchDatabaseName} WITH (FORCE)");
        await ExecuteAdminSqlAsync($"CREATE DATABASE {ScratchDatabaseName}");

        // Phase one: bring the scratch database to just before the migration under test and seed it as an upgrading
        // customer's database would hold the rows.
        await using (var priorContext = NewScratchContext())
        {
            await priorContext.GetService<IMigrator>().MigrateAsync(PreviousMigrationId);
        }

        await SeedAsync();

        // Phase two: a fresh context (a new app process, as an upgrade is) applies the migration under test.
        await using (var headContext = NewScratchContext())
        {
            await headContext.Database.MigrateAsync();
        }

        _afterUpgrade = await ReadAllSeededAsync();
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDownAsync()
    {
        if (_adminConnectionString != null)
            await ExecuteAdminSqlAsync($"DROP DATABASE IF EXISTS {ScratchDatabaseName} WITH (FORCE)");
    }

    [TestCaseSource(nameof(RepairedTriads))]
    public void Migration_UserInitiatorCarryingALiveApiKeyId_IsRecordedAsTheApiKey(string triad)
    {
        var initiator = _afterUpgrade[triad];

        using (Assert.EnterMultipleScope())
        {
            Assert.That(initiator.Type, Is.EqualTo(ApiKeyType), $"{triad} must name the API key, not a person");
            Assert.That(initiator.Id, Is.EqualTo(_liveKeyId), $"{triad} keeps the API key's id");
            Assert.That(initiator.Name, Is.EqualTo(LiveKeyName), $"{triad} keeps the name the key had at the time");
        }
    }

    [Test]
    public void Migration_UserInitiatorCarryingAMetaverseObjectId_IsLeftAsTheUser()
    {
        Assert.That(_afterUpgrade["Activity by a person"], Is.EqualTo(new InitiatorValues(UserType, _userId, UserName)),
            "a person's own change is not an API key's and must be left as recorded");
    }

    [TestCaseSource(nameof(DeletedKeysJimCanStillProve))]
    public void Migration_UserInitiatorCarryingADeletedApiKeyIdJimCanStillProve_IsRecordedAsTheApiKey(string row)
    {
        var expected = _seeded[row].Expected;

        Assert.That(_afterUpgrade[row], Is.EqualTo(expected with { Type = ApiKeyType }),
            "a deleted key's id that JIM can still prove was an API key's must be recorded as that API key");
    }

    [Test]
    public void Migration_UserInitiatorCarryingADeletedApiKeyIdWithNoTrace_IsLeftAsRecorded()
    {
        // A factory reset made with an ordinary key deletes that key, and every Activity that could have named it,
        // before recording the reset Activity, so nothing is left to prove the id was ever an API key's.
        Assert.That(_afterUpgrade["Factory reset by a key with no trace"],
            Is.EqualTo(new InitiatorValues(UserType, _keyWithNoTrace, "ordinary-key")),
            "an id JIM cannot prove was an API key's must be left exactly as recorded");
    }

    [Test]
    public void Migration_InitiatorsOfOtherTypesOrWithoutAnId_AreUnchanged()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(_afterUpgrade["Activity by the key, recorded correctly"],
                Is.EqualTo(new InitiatorValues(ApiKeyType, _liveKeyId, LiveKeyName)));
            Assert.That(_afterUpgrade["Activity by System"], Is.EqualTo(new InitiatorValues(SystemType, null, "System")));
            Assert.That(_afterUpgrade["Activity by a person with no id"],
                Is.EqualTo(new InitiatorValues(UserType, null, "Someone")));
        }
    }

    [Test]
    public async Task Migration_MetaverseObjectChangeTheApiKeyRecorded_NoLongerReadsAsAPersonsDirectEditAsync()
    {
        // A deletion recorded with a User initiator derived its change mechanism from that type as well (User, shown
        // as a Direct Edit); recorded correctly as an API key it would have carried none (NotSet).
        var deletion = await ReadChangeInitiatorTypeAsync("MetaverseObjectChanges.InitiatedBy");
        var synchronisation = await ReadChangeInitiatorTypeAsync("Synchronisation change by the key");
        var directEdit = await ReadChangeInitiatorTypeAsync("Direct edit by a person");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(deletion, Is.EqualTo((int)MetaverseObjectChangeInitiatorType.NotSet),
                "a change the API key made must not read as a person's direct edit");
            Assert.That(_afterUpgrade["Synchronisation change by the key"].Type, Is.EqualTo(ApiKeyType));
            Assert.That(synchronisation, Is.EqualTo((int)MetaverseObjectChangeInitiatorType.SynchronisationRule),
                "a mechanism that was not derived from the initiator type is left as recorded");
            Assert.That(_afterUpgrade["Direct edit by a person"], Is.EqualTo(new InitiatorValues(UserType, _userId, UserName)));
            Assert.That(directEdit, Is.EqualTo((int)MetaverseObjectChangeInitiatorType.User),
                "a person's own direct edit is left as recorded");
        }
    }

    [Test]
    public async Task Migration_ReappliedAfterRollingBack_RepairsNewRowsAndLeavesEveryOtherRowAsItWasAsync()
    {
        // Roll back past the migration (its Down changes nothing), record two more rows the way the bug did, and upgrade
        // again: the new key row is repaired, the new person row is not, and nothing repaired the first time moves.
        await using (var rollbackContext = NewScratchContext())
        {
            await rollbackContext.GetService<IMigrator>().MigrateAsync(PreviousMigrationId);
        }

        var freshKeyActivity = Guid.NewGuid();
        var freshUserActivity = Guid.NewGuid();
        await using (var connection = await OpenAsync())
        {
            await InsertActivityAsync(connection, freshKeyActivity, ActivityTargetType.Schedule,
                ActivityTargetOperationType.Update, UserType, _liveKeyId, LiveKeyName);
            await InsertActivityAsync(connection, freshUserActivity, ActivityTargetType.Schedule,
                ActivityTargetOperationType.Update, UserType, _userId, UserName);
        }

        await using (var headContext = NewScratchContext())
        {
            await headContext.Database.MigrateAsync();
        }

        var afterReapplying = await ReadAllSeededAsync();
        await using var verifyConnection = await OpenAsync();
        var freshKey = await ReadInitiatorAsync(verifyConnection, "Activities", "InitiatedBy", freshKeyActivity);
        var freshUser = await ReadInitiatorAsync(verifyConnection, "Activities", "InitiatedBy", freshUserActivity);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(freshKey, Is.EqualTo(new InitiatorValues(ApiKeyType, _liveKeyId, LiveKeyName)),
                "re-applying the migration repairs a row recorded with the bug since it last ran");
            Assert.That(freshUser, Is.EqualTo(new InitiatorValues(UserType, _userId, UserName)),
                "re-applying the migration keeps to its scope");
            Assert.That(afterReapplying, Is.EquivalentTo(_afterUpgrade),
                "re-applying the migration changes nothing it has already repaired or left alone");
        }
    }

    private async Task SeedAsync()
    {
        var now = DateTime.UtcNow;
        await using var connection = await OpenAsync();

        // The live key was created by a key since deleted, recorded correctly as an API key: that is the only trace
        // of _keyRecordedAsApiKeyElsewhere.
        await ExecuteAsync(connection,
            @"INSERT INTO ""ApiKeys""
                (""Id"", ""Name"", ""KeyHash"", ""KeyPrefix"", ""Created"", ""CreatedByType"", ""CreatedById"", ""CreatedByName"",
                 ""LastUpdatedByType"", ""IsEnabled"", ""IsInfrastructureKey"")
              VALUES (@id, @name, 'not-a-real-hash', 'jim_ak_2066', @now, @apiKeyType, @creatorId, 'retired-key', 0, true, false)",
            ("id", _liveKeyId), ("name", LiveKeyName), ("now", now), ("apiKeyType", ApiKeyType),
            ("creatorId", _keyRecordedAsApiKeyElsewhere));

        var metaverseObjectTypeId = (int)(await ScalarAsync(connection,
            @"INSERT INTO ""MetaverseObjectTypes""
                (""Name"", ""PluralName"", ""Created"", ""CreatedByType"", ""LastUpdatedByType"", ""BuiltIn"", ""DeletionRule"",
                 ""DeletionTriggerConnectedSystemIds"")
              VALUES ('2066 Person', '2066 People', @now, @systemType, 0, false, 0, '{}')
              RETURNING ""Id""",
            ("now", now), ("systemType", SystemType)))!;

        // The genuine person, and a Metaverse Object a run the key started marked for deletion.
        var pendingDeletionId = Guid.NewGuid();
        await ExecuteAsync(connection,
            @"INSERT INTO ""MetaverseObjects"" (""Id"", ""Created"", ""TypeId"", ""Status"", ""DeletionInitiatedByType"", ""Origin"")
              VALUES (@id, @now, @typeId, 0, 0, 0)",
            ("id", _userId), ("now", now), ("typeId", metaverseObjectTypeId));
        await ExecuteAsync(connection,
            @"INSERT INTO ""MetaverseObjects""
                (""Id"", ""Created"", ""TypeId"", ""Status"", ""DeletionInitiatedByType"", ""DeletionInitiatedById"",
                 ""DeletionInitiatedByName"", ""Origin"")
              VALUES (@id, @now, @typeId, 0, @userType, @keyId, @keyName, 0)",
            ("id", pendingDeletionId), ("now", now), ("typeId", metaverseObjectTypeId), ("userType", UserType),
            ("keyId", _liveKeyId), ("keyName", LiveKeyName));
        Track("MetaverseObjects.DeletionInitiatedBy", "MetaverseObjects", "DeletionInitiatedBy", pendingDeletionId);

        // A Schedule the key created and last updated, its step, and a run of it the key started.
        var scheduleId = Guid.NewGuid();
        await ExecuteAsync(connection,
            @"INSERT INTO ""Schedules""
                (""Id"", ""Name"", ""TriggerType"", ""PatternType"", ""IsEnabled"", ""Created"",
                 ""CreatedByType"", ""CreatedById"", ""CreatedByName"", ""LastUpdatedByType"", ""LastUpdatedById"", ""LastUpdatedByName"")
              VALUES (@id, '2066 nightly', 0, 0, false, @now, @userType, @keyId, @keyName, @userType, @keyId, @keyName)",
            ("id", scheduleId), ("now", now), ("userType", UserType), ("keyId", _liveKeyId), ("keyName", LiveKeyName));
        Track("Schedules.CreatedBy", "Schedules", "CreatedBy", scheduleId);
        Track("Schedules.LastUpdatedBy", "Schedules", "LastUpdatedBy", scheduleId);

        var stepId = Guid.NewGuid();
        await ExecuteAsync(connection,
            @"INSERT INTO ""ScheduleSteps""
                (""Id"", ""ScheduleId"", ""StepIndex"", ""ExecutionMode"", ""StepType"", ""Created"",
                 ""CreatedByType"", ""CreatedById"", ""CreatedByName"", ""LastUpdatedByType"", ""LastUpdatedById"", ""LastUpdatedByName"")
              VALUES (@id, @scheduleId, 0, 0, 0, @now, @userType, @keyId, @keyName, @userType, @keyId, @keyName)",
            ("id", stepId), ("scheduleId", scheduleId), ("now", now), ("userType", UserType), ("keyId", _liveKeyId),
            ("keyName", LiveKeyName));
        Track("ScheduleSteps.CreatedBy", "ScheduleSteps", "CreatedBy", stepId);
        Track("ScheduleSteps.LastUpdatedBy", "ScheduleSteps", "LastUpdatedBy", stepId);

        var executionId = Guid.NewGuid();
        await ExecuteAsync(connection,
            @"INSERT INTO ""ScheduleExecutions""
                (""Id"", ""ScheduleId"", ""ScheduleName"", ""Status"", ""CurrentStepIndex"", ""TotalSteps"", ""QueuedAt"",
                 ""InitiatedByType"", ""InitiatedById"", ""InitiatedByName"")
              VALUES (@id, @scheduleId, '2066 nightly', 0, 0, 1, @now, @userType, @keyId, @keyName)",
            ("id", executionId), ("scheduleId", scheduleId), ("now", now), ("userType", UserType), ("keyId", _liveKeyId),
            ("keyName", LiveKeyName));
        Track("ScheduleExecutions.InitiatedBy", "ScheduleExecutions", "InitiatedBy", executionId);

        // An Example Data Template the key created and last updated.
        var templateId = (int)(await ScalarAsync(connection,
            @"INSERT INTO ""ExampleDataTemplates""
                (""Name"", ""BuiltIn"", ""Created"", ""CreatedByType"", ""CreatedById"", ""CreatedByName"",
                 ""LastUpdatedByType"", ""LastUpdatedById"", ""LastUpdatedByName"")
              VALUES ('2066 template', false, @now, @userType, @keyId, @keyName, @userType, @keyId, @keyName)
              RETURNING ""Id""",
            ("now", now), ("userType", UserType), ("keyId", _liveKeyId), ("keyName", LiveKeyName)))!;
        Track("ExampleDataTemplates.CreatedBy", "ExampleDataTemplates", "CreatedBy", templateId);
        Track("ExampleDataTemplates.LastUpdatedBy", "ExampleDataTemplates", "LastUpdatedBy", templateId);

        // A Connected System whose containers an export the key's Schedule run started auto-selected.
        var connectorDefinitionId = (int)(await ScalarAsync(connection,
            @"INSERT INTO ""ConnectorDefinitions""
                (""Name"", ""Created"", ""CreatedByType"", ""LastUpdatedByType"", ""BuiltIn"", ""SupportsFullImport"",
                 ""SupportsDeltaImport"", ""SupportsExport"", ""SupportsPartitions"", ""SupportsPartitionContainers"",
                 ""SupportsSecondaryExternalId"", ""SupportsUserSelectedExternalId"", ""SupportsUserSelectedAttributeTypes"",
                 ""SupportsAutoConfirmExport"", ""SupportsParallelExport"")
              VALUES ('2066 connector', @now, @systemType, 0, false, true, false, true, true, true, false, false, false, false, false)
              RETURNING ""Id""",
            ("now", now), ("systemType", SystemType)))!;
        var connectedSystemId = (int)(await ScalarAsync(connection,
            @"INSERT INTO ""ConnectedSystems""
                (""Name"", ""Created"", ""CreatedByType"", ""CreatedById"", ""CreatedByName"",
                 ""LastUpdatedByType"", ""LastUpdatedById"", ""LastUpdatedByName"",
                 ""Status"", ""ConnectorDefinitionId"", ""SettingValuesValid"", ""ObjectMatchingRuleMode"")
              VALUES ('2066 directory', @now, @userType, @userId, @userName, @userType, @keyId, @keyName, 0, @definitionId, true, 0)
              RETURNING ""Id""",
            ("now", now), ("userType", UserType), ("userId", _userId), ("userName", UserName), ("keyId", _liveKeyId),
            ("keyName", LiveKeyName), ("definitionId", connectorDefinitionId)))!;
        Track("ConnectedSystems.LastUpdatedBy", "ConnectedSystems", "LastUpdatedBy", connectedSystemId);

        // Activities: the key's change, its run's step (with the run's Worker Task), and the rows the repair must leave.
        var keyActivityId = Guid.NewGuid();
        await InsertActivityAsync(connection, keyActivityId, ActivityTargetType.Schedule, ActivityTargetOperationType.Update,
            UserType, _liveKeyId, LiveKeyName);
        Track("Activities.InitiatedBy", "Activities", "InitiatedBy", keyActivityId);

        var stepActivityId = Guid.NewGuid();
        await InsertActivityAsync(connection, stepActivityId, ActivityTargetType.ConnectedSystemRunProfile,
            ActivityTargetOperationType.Execute, UserType, _liveKeyId, LiveKeyName);
        var workerTaskId = Guid.NewGuid();
        await ExecuteAsync(connection,
            @"INSERT INTO ""WorkerTasks""
                (""Id"", ""Timestamp"", ""Status"", ""ExecutionMode"", ""InitiatedByType"", ""InitiatedById"", ""InitiatedByName"",
                 ""ActivityId"", ""Discriminator"", ""ScheduleExecutionId"")
              VALUES (@id, @now, 0, 0, @userType, @keyId, @keyName, @activityId, 'SynchronisationWorkerTask', @executionId)",
            ("id", workerTaskId), ("now", now), ("userType", UserType), ("keyId", _liveKeyId), ("keyName", LiveKeyName),
            ("activityId", stepActivityId), ("executionId", executionId));
        Track("WorkerTasks.InitiatedBy", "WorkerTasks", "InitiatedBy", workerTaskId);

        await TrackActivityAsync(connection, "Activity by a person", ActivityTargetType.Schedule,
            ActivityTargetOperationType.Update, UserType, _userId, UserName);
        await TrackActivityAsync(connection, "Activity by the key, recorded correctly", ActivityTargetType.ConnectedSystem,
            ActivityTargetOperationType.Update, ApiKeyType, _liveKeyId, LiveKeyName);
        await TrackActivityAsync(connection, "Activity by System", ActivityTargetType.HistoryRetentionCleanup,
            ActivityTargetOperationType.Execute, SystemType, null, "System");
        await TrackActivityAsync(connection, "Activity by a person with no id", ActivityTargetType.Schedule,
            ActivityTargetOperationType.Update, UserType, null, "Someone");

        // Deleted keys. The first is named by its own delete Activity's tombstone snapshot (which, like the real one,
        // does not set ApiKeyId); the second only by the live key's CreatedBy above; the third only as the target of
        // an API key Activity; the fourth by nothing at all, as a factory reset made with an ordinary key leaves it.
        await InsertActivityAsync(connection, Guid.NewGuid(), ActivityTargetType.ApiKey, ActivityTargetOperationType.Delete,
            UserType, _userId, UserName, snapshotJson: ConfigurationSnapshotService.Serialise(new ConfigurationSnapshot
            {
                ObjectType = ConfigurationSnapshotService.ApiKeyObjectType,
                ObjectGuidId = _keyInDeletionSnapshot,
                ObjectName = "rotated-key",
                Root = ConfigurationSnapshotNode.ObjectNode("apiKey", [], "API Key")
            }));
        await TrackActivityAsync(connection, "Activity by key named in its deletion snapshot", ActivityTargetType.Schedule,
            ActivityTargetOperationType.Create, UserType, _keyInDeletionSnapshot, "rotated-key");

        await TrackActivityAsync(connection, "Activity by key recorded as an API key elsewhere", ActivityTargetType.Schedule,
            ActivityTargetOperationType.Delete, UserType, _keyRecordedAsApiKeyElsewhere, "retired-key");

        await InsertActivityAsync(connection, Guid.NewGuid(), ActivityTargetType.ApiKey, ActivityTargetOperationType.Update,
            UserType, _userId, UserName, apiKeyId: _keyAsConfigurationTarget);
        await TrackActivityAsync(connection, "Activity by key that was a configuration change's target",
            ActivityTargetType.Schedule, ActivityTargetOperationType.Update, UserType, _keyAsConfigurationTarget, "renamed-key");

        await TrackActivityAsync(connection, "Factory reset by a key with no trace", ActivityTargetType.System,
            ActivityTargetOperationType.Reset, UserType, _keyWithNoTrace, "ordinary-key");

        // Metaverse Object change history: a deletion a run the key started recorded (its mechanism derived from the
        // User type), a synchronisation change by the same run, and a person's own direct edit.
        await TrackMetaverseObjectChangeAsync(connection, "MetaverseObjectChanges.InitiatedBy", ObjectChangeType.Deleted,
            UserType, _liveKeyId, LiveKeyName, MetaverseObjectChangeInitiatorType.User);
        await TrackMetaverseObjectChangeAsync(connection, "Synchronisation change by the key", ObjectChangeType.Updated,
            UserType, _liveKeyId, LiveKeyName, MetaverseObjectChangeInitiatorType.SynchronisationRule);
        await TrackMetaverseObjectChangeAsync(connection, "Direct edit by a person", ObjectChangeType.Updated,
            UserType, _userId, UserName, MetaverseObjectChangeInitiatorType.User);

        var csoChangeId = Guid.NewGuid();
        await ExecuteAsync(connection,
            @"INSERT INTO ""ConnectedSystemObjectChanges""
                (""Id"", ""ConnectedSystemId"", ""ChangeTime"", ""ChangeType"", ""InitiatedByType"", ""InitiatedById"", ""InitiatedByName"")
              VALUES (@id, @connectedSystemId, @now, @changeType, @userType, @keyId, @keyName)",
            ("id", csoChangeId), ("connectedSystemId", connectedSystemId), ("now", now),
            ("changeType", (int)ObjectChangeType.Updated), ("userType", UserType), ("keyId", _liveKeyId),
            ("keyName", LiveKeyName));
        Track("ConnectedSystemObjectChanges.InitiatedBy", "ConnectedSystemObjectChanges", "InitiatedBy", csoChangeId);
    }

    private async Task TrackActivityAsync(NpgsqlConnection connection, string label, ActivityTargetType targetType,
        ActivityTargetOperationType operation, int initiatorType, Guid? initiatorId, string initiatorName)
    {
        var id = Guid.NewGuid();
        await InsertActivityAsync(connection, id, targetType, operation, initiatorType, initiatorId, initiatorName);
        Track(label, "Activities", "InitiatedBy", id, new InitiatorValues(initiatorType, initiatorId, initiatorName));
    }

    private async Task TrackMetaverseObjectChangeAsync(NpgsqlConnection connection, string label, ObjectChangeType changeType,
        int initiatorType, Guid initiatorId, string initiatorName, MetaverseObjectChangeInitiatorType changeInitiatorType)
    {
        var id = Guid.NewGuid();
        await ExecuteAsync(connection,
            @"INSERT INTO ""MetaverseObjectChanges""
                (""Id"", ""ChangeTime"", ""ChangeType"", ""InitiatedByType"", ""InitiatedById"", ""InitiatedByName"", ""ChangeInitiatorType"")
              VALUES (@id, @now, @changeType, @initiatorType, @initiatorId, @initiatorName, @changeInitiatorType)",
            ("id", id), ("now", DateTime.UtcNow), ("changeType", (int)changeType), ("initiatorType", initiatorType),
            ("initiatorId", initiatorId), ("initiatorName", initiatorName), ("changeInitiatorType", (int)changeInitiatorType));
        Track(label, "MetaverseObjectChanges", "InitiatedBy", id);
    }

    private static Task InsertActivityAsync(NpgsqlConnection connection, Guid id, ActivityTargetType targetType,
        ActivityTargetOperationType operation, int initiatorType, Guid? initiatorId, string initiatorName,
        Guid? apiKeyId = null, string? snapshotJson = null) =>
        // Every NOT NULL column of the previous migration's Activities table, plus the initiator and evidence columns.
        ExecuteAsync(connection,
            @"INSERT INTO ""Activities""
                (""Id"", ""Created"", ""Executed"", ""Status"", ""TargetType"", ""TargetOperationType"",
                 ""InitiatedByType"", ""InitiatedById"", ""InitiatedByName"", ""ApiKeyId"", ""ConfigurationChangeSnapshot"",
                 ""ObjectsToProcess"", ""ObjectsProcessed"", ""TotalAdded"", ""TotalUpdated"", ""TotalDeleted"", ""TotalProjected"",
                 ""TotalJoined"", ""TotalAttributeFlows"", ""TotalDisconnected"", ""TotalDisconnectedOutOfScope"",
                 ""TotalOutOfScopeRetainJoin"", ""TotalDriftCorrections"", ""TotalProvisioned"", ""TotalExported"",
                 ""TotalDeprovisioned"", ""TotalCreated"", ""TotalPendingExports"", ""TotalErrors"", ""PendingExportsConfirmed"")
              VALUES (@id, @now, @now, @status, @targetType, @operation, @initiatorType, @initiatorId, @initiatorName,
                      @apiKeyId, @snapshot, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0)",
            ("id", id), ("now", DateTime.UtcNow), ("status", (int)ActivityStatus.Complete), ("targetType", (int)targetType),
            ("operation", (int)operation), ("initiatorType", initiatorType), ("initiatorId", Uuid("initiatorId", initiatorId)),
            ("initiatorName", initiatorName), ("apiKeyId", Uuid("apiKeyId", apiKeyId)),
            ("snapshot", new NpgsqlParameter("snapshot", NpgsqlDbType.Jsonb) { Value = (object?)snapshotJson ?? DBNull.Value }));

    private static NpgsqlParameter Uuid(string name, Guid? value) =>
        new(name, NpgsqlDbType.Uuid) { Value = value.HasValue ? value.Value : DBNull.Value };

    private void Track(string label, string table, string prefix, object rowId, InitiatorValues? expected = null) =>
        _seeded[label] = new SeededInitiator(table, prefix, rowId, expected ?? new InitiatorValues(UserType, _liveKeyId, LiveKeyName));

    private async Task<Dictionary<string, InitiatorValues>> ReadAllSeededAsync()
    {
        await using var connection = await OpenAsync();
        var values = new Dictionary<string, InitiatorValues>();
        foreach (var (label, seeded) in _seeded)
            values[label] = await ReadInitiatorAsync(connection, seeded.Table, seeded.Prefix, seeded.RowId);
        return values;
    }

    private static async Task<InitiatorValues> ReadInitiatorAsync(NpgsqlConnection connection, string table, string prefix, object rowId)
    {
        // Table and column names come from this fixture's own constants, never from input.
        await using var command = connection.CreateCommand();
        command.CommandText = $@"SELECT ""{prefix}Type"", ""{prefix}Id"", ""{prefix}Name"" FROM ""{table}"" WHERE ""Id"" = @rowId";
        command.Parameters.Add(new NpgsqlParameter("rowId", rowId));
        await using var reader = await command.ExecuteReaderAsync();
        Assert.That(await reader.ReadAsync(), Is.True, $"the seeded {table} row is missing");
        return new InitiatorValues(
            reader.GetInt32(0),
            await reader.IsDBNullAsync(1) ? null : reader.GetGuid(1),
            await reader.IsDBNullAsync(2) ? null : reader.GetString(2));
    }

    private async Task<int> ReadChangeInitiatorTypeAsync(string label)
    {
        await using var connection = await OpenAsync();
        var value = await ScalarAsync(connection,
            @"SELECT ""ChangeInitiatorType"" FROM ""MetaverseObjectChanges"" WHERE ""Id"" = @id", ("id", _seeded[label].RowId));
        return (int)value!;
    }

    private async Task<NpgsqlConnection> OpenAsync()
    {
        var connection = new NpgsqlConnection(_scratchConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, params (string Name, object? Value)[] parameters)
    {
        await using var command = BuildCommand(connection, sql, parameters);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<object?> ScalarAsync(NpgsqlConnection connection, string sql, params (string Name, object? Value)[] parameters)
    {
        await using var command = BuildCommand(connection, sql, parameters);
        return await command.ExecuteScalarAsync();
    }

    private static NpgsqlCommand BuildCommand(NpgsqlConnection connection, string sql, (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            // A nullable value is passed as an already typed parameter (see Uuid), so a null here is a seeding mistake.
            command.Parameters.Add(value switch
            {
                NpgsqlParameter typed => typed,
                null => throw new ArgumentNullException(name, "Pass a nullable value as a typed NpgsqlParameter."),
                _ => new NpgsqlParameter(name, value)
            });
        }
        return command;
    }

    private JimDbContext NewScratchContext()
    {
        var options = new DbContextOptionsBuilder<JimDbContext>()
            .UseNpgsql(_scratchConnectionString)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        return new JimDbContext(options);
    }

    // CREATE/DROP DATABASE cannot be parameterised or run in a transaction; the name is a constant above, never input.
    private Task ExecuteAdminSqlAsync(string sql) => PostgresTestDatabase.ExecuteDatabaseCreateDropAsync(_adminConnectionString, sql);

    private sealed record InitiatorValues(int Type, Guid? Id, string? Name);

    private sealed record SeededInitiator(string Table, string Prefix, object RowId, InitiatorValues Expected);
}
