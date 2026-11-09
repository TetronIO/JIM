// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Diagnostics;
using JIM.Models.Core;
using JIM.Models.Staging;
using JIM.Models.Utility;
using JIM.PostgresData;
using JIM.TestSupport;
using JIM.Worker.Processors;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using NUnit.Framework;

namespace JIM.Worker.Tests.Workflows;

/// <summary>
/// Measurement only, no behaviour (#2669, Source Full Synchronisation at Scale500k25kGroups): the Full Synchronisation's
/// page source (<see cref="TypeOrderedCsoPager"/> over <c>GetConnectedSystemObjectsAsync</c>, which is what the
/// <c>LoadCsoPage</c> span wraps) walking a connector space whose objects were all created within the closing sweep's
/// five-minute clock-skew margin of the run's start, as an initial load's Full Import followed straight away by its Full
/// Synchronisation leaves them. It times the type walks, then the single <c>NextPageAsync</c> call that runs the closing
/// sweep, and counts the page loads the sweep makes.
/// </summary>
/// <remarks>
/// The walk clears the change tracker after every page, as the Full Synchronisation does at its page boundary. The sweep
/// loads all its pages inside one <c>NextPageAsync</c> call, so nothing clears between them, exactly as in the run. The
/// same walk is then repeated with the creation floor after the objects' creation, which is the sweep the margin was
/// designed for. Explicit: it seeds about 1.6 million rows.
/// </remarks>
[TestFixture]
[Category("RequiresPostgres")]
[Explicit("Benchmark (#2669): seeds about 1.6 million rows; run on its own.")]
public class FullSyncClosingSweepBenchmarkDatabaseTests
{
    private const int Users = 100_000;
    private const int ValuesPerUser = 6;
    private const int PageSize = 500;

    private string _connectionString = null!;
    private int _systemId;
    private int _userTypeId;
    private int _groupTypeId;

    private JimDbContext NewContext()
    {
        var builder = new DbContextOptionsBuilder<JimDbContext>()
            .UseNpgsql(_connectionString)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            // The Worker tracks by default (src/JIM.Worker/Program.cs).
            .UseQueryTrackingBehavior(QueryTrackingBehavior.TrackAll);
        return new JimDbContext(builder.Options);
    }

    [OneTimeSetUp]
    public async Task OneTimeSetUpAsync()
    {
        var connectionString = PostgresTestDatabase.ConnectionStringFromEnvironment();
        if (connectionString == null)
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping the closing sweep benchmark.");
        _connectionString = connectionString!;

        TestUtilities.SetEnvironmentVariables();
        await using (var ctx = NewContext())
            await ctx.Database.MigrateAsync();

        await PostgresTestDatabase.ResetAsync(_connectionString);
        await SeedAsync();
    }

    [Test]
    public async Task NextPageAsync_EveryObjectCreatedInsideTheSweepMargin_ReportsSweepReloadingTheWholeConnectorSpaceAsync()
    {
        // The run started now, so the floor is five minutes back, as CreateTypeOrderedPagerAsync sets it; every object was
        // created inside it (the initial load's case).
        var inside = await WalkAsync(DateTime.UtcNow - TimeSpan.FromMinutes(5));

        // The case the margin was designed for: nothing was created inside it.
        var outside = await WalkAsync(DateTime.UtcNow + TimeSpan.FromMinutes(1));

        TestContext.Out.WriteLine($"Closing sweep benchmark: {Users:N0} users of {ValuesPerUser} values each, joined, and one group holding all of them, " +
                                  $"page size {PageSize}.");
        Report("Objects created inside the margin (initial load)", inside);
        Report("Objects created before the margin", outside);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(inside.HandedOut, Is.EqualTo(Users + 1), "the walks hand every object out once");
            Assert.That(outside.HandedOut, Is.EqualTo(Users + 1), "the walks hand every object out once");
            Assert.That(inside.SweepLoads, Is.EqualTo(inside.WalkLoads - 1),
                "the sweep reloads every page the walks loaded (less the walks' two empty end-of-type loads, plus its own empty one)");
            Assert.That(outside.SweepLoads, Is.EqualTo(1), "with nothing inside the margin, the sweep is one empty load");
        }
    }

    private static void Report(string label, WalkResult result)
    {
        TestContext.Out.WriteLine($"  {label}:");
        TestContext.Out.WriteLine($"    Type walks: {result.WalkLoads:N0} loads, {result.WalkMs:N0} ms, {result.HandedOut:N0} objects handed out " +
                                  $"(max page {result.MaxWalkPageMs:N0} ms).");
        TestContext.Out.WriteLine($"    Closing sweep (one NextPageAsync call): {result.SweepLoads:N0} loads, {result.SweepMs:N0} ms, " +
                                  $"{result.SweepTracked:N0} entities left tracked at its end.");
    }

    private sealed record WalkResult(int WalkLoads, long WalkMs, long MaxWalkPageMs, int HandedOut, int SweepLoads, long SweepMs, int SweepTracked);

    private async Task<WalkResult> WalkAsync(DateTime createdFloor)
    {
        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);
        var loads = 0;
        var pager = new TypeOrderedCsoPager((afterId, objectTypeId, createdOnOrAfter) =>
            {
                loads++;
                return repository.Sync.GetConnectedSystemObjectsAsync(_systemId, 1, PageSize, Users + 1, afterId, objectTypeId, createdOnOrAfter);
            },
            [_userTypeId, _groupTypeId], createdFloor);

        var walkMs = 0L;
        var maxWalkPageMs = 0L;
        var handedOut = 0;
        while (true)
        {
            var stopwatch = Stopwatch.StartNew();
            var loadsBefore = loads;
            PagedResultSet<ConnectedSystemObject>? page = await pager.NextPageAsync();
            stopwatch.Stop();

            if (page == null || pager.IsSweeping)
            {
                // This call ran the closing sweep, after the group walk's empty end-of-type load.
                var walkEndLoads = loadsBefore + 1;
                var tracked = ctx.ChangeTracker.Entries().Count();
                Assert.That(page, Is.Null, "nothing was created during the walk, so the sweep hands nothing out");
                return new WalkResult(walkEndLoads, walkMs, maxWalkPageMs, handedOut, loads - walkEndLoads, stopwatch.ElapsedMilliseconds, tracked);
            }

            walkMs += stopwatch.ElapsedMilliseconds;
            maxWalkPageMs = Math.Max(maxWalkPageMs, stopwatch.ElapsedMilliseconds);
            handedOut += page.Results.Count;

            // The Full Synchronisation's page boundary (ClearPageTrackingState).
            ctx.ChangeTracker.Clear();
        }
    }

    /// <summary>
    /// One Connected System with a user and a group Object Type. Every user is joined to its own Metaverse Object, and both
    /// sides hold <see cref="ValuesPerUser"/> text values. One group, joined, holds every user as a member on both sides.
    /// Every Connected System Object is created now.
    /// </summary>
    private async Task SeedAsync()
    {
        int userExternalIdId, userTextId, groupExternalIdId, memberAttributeId, personTypeId, groupMvTypeId, staticMembersId, mvTextId;
        await using (var seed = NewContext())
        {
            var connectorDefinition = new ConnectorDefinition { Name = "JIM Test Connector", BuiltIn = true };
            var system = new ConnectedSystem
            {
                Name = "Source", ConnectorDefinition = connectorDefinition, ObjectMatchingRuleMode = ObjectMatchingRuleMode.SyncRule
            };
            var userType = new ConnectedSystemObjectType { Name = "person", ConnectedSystem = system, Selected = true };
            var userExternalId = new ConnectedSystemObjectTypeAttribute
            {
                Name = "employeeId", ConnectedSystemObjectType = userType, Type = AttributeDataType.Text,
                AttributePlurality = AttributePlurality.SingleValued, Selected = true, IsExternalId = true
            };
            var userText = new ConnectedSystemObjectTypeAttribute
            {
                Name = "detail", ConnectedSystemObjectType = userType, Type = AttributeDataType.Text,
                AttributePlurality = AttributePlurality.MultiValued, Selected = true
            };
            userType.Attributes.Add(userExternalId);
            userType.Attributes.Add(userText);
            var groupType = new ConnectedSystemObjectType { Name = "group", ConnectedSystem = system, Selected = true };
            var groupExternalId = new ConnectedSystemObjectTypeAttribute
            {
                Name = "groupId", ConnectedSystemObjectType = groupType, Type = AttributeDataType.Text,
                AttributePlurality = AttributePlurality.SingleValued, Selected = true, IsExternalId = true
            };
            var member = new ConnectedSystemObjectTypeAttribute
            {
                Name = "member", ConnectedSystemObjectType = groupType, Type = AttributeDataType.Reference,
                AttributePlurality = AttributePlurality.MultiValued, Selected = true
            };
            groupType.Attributes.Add(groupExternalId);
            groupType.Attributes.Add(member);

            var personType = new MetaverseObjectType { Name = "Person", PluralName = "People" };
            var groupMvType = new MetaverseObjectType { Name = "Group", PluralName = "Groups" };
            var staticMembers = new MetaverseAttribute
            {
                Name = "Static Members", Type = AttributeDataType.Reference, AttributePlurality = AttributePlurality.MultiValued,
                MetaverseObjectTypes = [groupMvType]
            };
            var mvText = new MetaverseAttribute
            {
                Name = "Detail", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.MultiValued,
                MetaverseObjectTypes = [personType]
            };

            seed.AddRange(connectorDefinition, system, userType, groupType, personType, groupMvType, staticMembers, mvText);
            await seed.SaveChangesAsync();

            (_systemId, _userTypeId, _groupTypeId) = (system.Id, userType.Id, groupType.Id);
            (userExternalIdId, userTextId, groupExternalIdId, memberAttributeId) = (userExternalId.Id, userText.Id, groupExternalId.Id, member.Id);
            (personTypeId, groupMvTypeId, staticMembersId, mvTextId) = (personType.Id, groupMvType.Id, staticMembers.Id, mvText.Id);
        }

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            CREATE TEMP TABLE seed_users AS
                SELECT n, gen_random_uuid() AS mvo, gen_random_uuid() AS cso FROM generate_series(1, @users) AS n;

            INSERT INTO "MetaverseObjects" ("Id", "Created", "TypeId", "Status", "DeletionInitiatedByType", "Origin")
                SELECT mvo, now(), @personType, 0, 0, 0 FROM seed_users;
            INSERT INTO "ConnectedSystemObjects" ("Id", "Created", "TypeId", "ConnectedSystemId", "ExternalIdAttributeId", "Status",
                                                  "JoinType", "MetaverseObjectId")
                SELECT cso, now(), @userType, @system, @userExternalId, 0, 1, mvo FROM seed_users;
            INSERT INTO "ConnectedSystemObjectAttributeValues" ("Id", "ConnectedSystemObjectId", "AttributeId", "StringValue")
                SELECT gen_random_uuid(), cso, @userExternalId, 'u' || n FROM seed_users;
            INSERT INTO "ConnectedSystemObjectAttributeValues" ("Id", "ConnectedSystemObjectId", "AttributeId", "StringValue")
                SELECT gen_random_uuid(), cso, @userText, 'value ' || k || ' of user ' || n
                FROM seed_users CROSS JOIN generate_series(2, @valuesPerUser) AS k;
            INSERT INTO "MetaverseObjectAttributeValues" ("Id", "MetaverseObjectId", "AttributeId", "StringValue", "ContributedBySystemId")
                SELECT gen_random_uuid(), mvo, @mvText, 'value ' || k || ' of user ' || n, @system
                FROM seed_users CROSS JOIN generate_series(1, @valuesPerUser) AS k;

            CREATE TEMP TABLE seed_group AS SELECT gen_random_uuid() AS mvo, gen_random_uuid() AS cso;
            INSERT INTO "MetaverseObjects" ("Id", "Created", "TypeId", "Status", "DeletionInitiatedByType", "Origin")
                SELECT mvo, now(), @groupMvType, 0, 0, 0 FROM seed_group;
            INSERT INTO "ConnectedSystemObjects" ("Id", "Created", "TypeId", "ConnectedSystemId", "ExternalIdAttributeId", "Status",
                                                  "JoinType", "MetaverseObjectId")
                SELECT cso, now(), @groupType, @system, @groupExternalId, 0, 1, mvo FROM seed_group;
            INSERT INTO "ConnectedSystemObjectAttributeValues" ("Id", "ConnectedSystemObjectId", "AttributeId", "StringValue")
                SELECT gen_random_uuid(), cso, @groupExternalId, 'AllStaff' FROM seed_group;
            INSERT INTO "ConnectedSystemObjectAttributeValues" ("Id", "ConnectedSystemObjectId", "AttributeId", "ReferenceValueId")
                SELECT gen_random_uuid(), g.cso, @member, u.cso FROM seed_group g CROSS JOIN seed_users u;
            INSERT INTO "MetaverseObjectAttributeValues" ("Id", "MetaverseObjectId", "AttributeId", "ReferenceValueId", "ContributedBySystemId")
                SELECT gen_random_uuid(), g.mvo, @staticMembers, u.mvo, @system FROM seed_group g CROSS JOIN seed_users u;

            ANALYZE "MetaverseObjects";
            ANALYZE "ConnectedSystemObjects";
            ANALYZE "ConnectedSystemObjectAttributeValues";
            ANALYZE "MetaverseObjectAttributeValues";
            """, connection);
        command.CommandTimeout = 600;
        command.Parameters.AddWithValue("users", Users);
        command.Parameters.AddWithValue("valuesPerUser", ValuesPerUser);
        command.Parameters.AddWithValue("personType", personTypeId);
        command.Parameters.AddWithValue("groupMvType", groupMvTypeId);
        command.Parameters.AddWithValue("userType", _userTypeId);
        command.Parameters.AddWithValue("groupType", _groupTypeId);
        command.Parameters.AddWithValue("system", _systemId);
        command.Parameters.AddWithValue("userExternalId", userExternalIdId);
        command.Parameters.AddWithValue("userText", userTextId);
        command.Parameters.AddWithValue("groupExternalId", groupExternalIdId);
        command.Parameters.AddWithValue("member", memberAttributeId);
        command.Parameters.AddWithValue("staticMembers", staticMembersId);
        command.Parameters.AddWithValue("mvText", mvTextId);
        await command.ExecuteNonQueryAsync();
    }
}
