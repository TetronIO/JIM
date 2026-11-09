// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Diagnostics;
using JIM.Models.Core;
using JIM.Models.Staging;
using JIM.PostgresData;
using JIM.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using NUnit.Framework;

namespace JIM.Worker.Tests.Workflows;

/// <summary>
/// #2818 measurement only, no behaviour: a Delta Synchronisation page holding one group of 200,000 members that lost 1,000
/// of them. It times the page load as it stands (<c>GetConnectedSystemObjectsModifiedSinceAsync</c>, which is what the
/// <c>LoadModifiedCsoPage</c> span wraps: the object with every value, the referenced objects' join state and the joined
/// Metaverse Object with every value), then the statements a set-based design would run instead: a comparison in the
/// database of the group's members with its Metaverse Object's, returning only the values the two sides disagree on, and
/// the two loads that leave the agreed members out.
/// </summary>
/// <remarks>
/// The Connected System Object holds 199,000 members, all joined; its Metaverse Object still holds 200,000, so the
/// comparison answers with the 1,000 departed members on the Metaverse side, as the Source system's Delta Synchronisation
/// sees a leaver step before its Attribute Flow. Explicit: it seeds about 800,000 rows.
/// </remarks>
[TestFixture]
[Category("RequiresPostgres")]
[Explicit("Benchmark (#2818): seeds about 800,000 rows; run on its own.")]
public class DeltaSyncLargeGroupLoadBenchmarkDatabaseTests
{
    private const int Members = 200_000;
    private const int Departed = 1_000;
    private const int Runs = 3;

    private string _connectionString = null!;
    private int _systemId;
    private int _groupTypeId;
    private int _memberAttributeId;
    private int _staticMembersAttributeId;
    private Guid _groupCsoId;
    private Guid _groupMvoId;

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
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping the #2818 large-group load benchmark.");
        _connectionString = connectionString!;

        TestUtilities.SetEnvironmentVariables();
        await using (var ctx = NewContext())
            await ctx.Database.MigrateAsync();

        await PostgresTestDatabase.ResetAsync(_connectionString);
        await SeedAsync();
    }

    [Test]
    public async Task LoadModifiedCsoPage_LargeGroupMissingAThousandMembers_ReportsWholeLoadAgainstSetBasedComparisonAsync()
    {
        var modifiedSince = DateTime.UtcNow.AddHours(-1);

        // As it stands: the page load the Delta Synchronisation runs.
        var wholeLoadMs = new List<long>();
        var loadedCsoValues = 0;
        var loadedMvoValues = 0;
        for (var run = 0; run < Runs; run++)
        {
            await using var ctx = NewContext();
            var repository = new PostgresDataRepository(ctx);
            var stopwatch = Stopwatch.StartNew();
            var page = await repository.Sync.GetConnectedSystemObjectsModifiedSinceAsync(
                _systemId, modifiedSince, 1, 500, knownTotalCount: 1, afterId: Guid.Empty, objectTypeId: _groupTypeId);
            stopwatch.Stop();
            wholeLoadMs.Add(stopwatch.ElapsedMilliseconds);

            var group = page.Results.Single();
            loadedCsoValues = group.AttributeValues.Count;
            loadedMvoValues = group.MetaverseObject!.AttributeValues.Count;
            Assert.That(group.AttributeValues.Count(v => v.ResolvedReferenceMetaverseObjectId.HasValue), Is.EqualTo(Members - Departed));
        }

        // Set-based: the values the two sides disagree on, with each side's count, in one statement.
        var compareMs = new List<long>();
        List<(char Side, Guid Id)> disagreements = [];
        long csoCount = 0, mvoCount = 0;
        for (var run = 0; run < Runs; run++)
        {
            var stopwatch = Stopwatch.StartNew();
            (disagreements, csoCount, mvoCount) = await CompareInDatabaseAsync();
            stopwatch.Stop();
            compareMs.Add(stopwatch.ElapsedMilliseconds);
        }

        // The lean loads: the object without the agreed members (EF, filtered include, tracked as the page load is), and
        // the Metaverse Object's values without its agreed members (raw, as MetaverseObjectRawReader reads).
        var csoDisagreeing = disagreements.Where(d => d.Side == 'c').Select(d => d.Id).ToList();
        var mvoDisagreeing = disagreements.Where(d => d.Side == 'm').Select(d => d.Id).ToArray();
        var leanCsoMs = new List<long>();
        var leanMvoMs = new List<long>();
        var leanCsoValues = 0;
        var leanMvoValues = 0;
        for (var run = 0; run < Runs; run++)
        {
            await using var ctx = NewContext();
            var memberAttributeId = _memberAttributeId;
            var groupCsoId = _groupCsoId;
            var stopwatch = Stopwatch.StartNew();
            var group = await ctx.ConnectedSystemObjects
                .AsTracking()
                .Include(cso => cso.Type)
                .Include(cso => cso.AttributeValues.Where(av => av.AttributeId != memberAttributeId || csoDisagreeing.Contains(av.Id)))
                    .ThenInclude(av => av.Attribute)
                .SingleAsync(cso => cso.Id == groupCsoId);
            stopwatch.Stop();
            leanCsoMs.Add(stopwatch.ElapsedMilliseconds);
            leanCsoValues = group.AttributeValues.Count;

            stopwatch.Restart();
            leanMvoValues = await ReadMetaverseValuesLeavingOutAgreedAsync(mvoDisagreeing);
            stopwatch.Stop();
            leanMvoMs.Add(stopwatch.ElapsedMilliseconds);
        }

        // As built: the page load leaving the members out (the default threshold), and the database's answer to Drift
        // Detection's question, asked with the expected values read from the Metaverse Object the load brought, as the run
        // asks it.
        var builtLoadMs = new List<long>();
        var builtCompareMs = new List<long>();
        var builtCsoValues = 0;
        var builtMvoValues = 0;
        DriftHeldReferenceAnswer? builtAnswer = null;
        for (var run = 0; run < Runs; run++)
        {
            await using var ctx = NewContext();
            var repository = new PostgresDataRepository(ctx);
            var member = await ctx.ConnectedSystemAttributes.AsNoTracking().SingleAsync(a => a.Id == _memberAttributeId);
            var plan = new SynchronisationValueLoadPlan(new Dictionary<int, IReadOnlyList<ConnectedSystemObjectTypeAttribute>> { [_groupTypeId] = [member] },
                SynchronisationValueLoadPlan.DefaultMinimumHeldValueCount);

            var stopwatch = Stopwatch.StartNew();
            var page = await repository.Sync.GetConnectedSystemObjectsModifiedSinceAsync(
                _systemId, modifiedSince, 1, 500, knownTotalCount: 1, afterId: Guid.Empty, objectTypeId: _groupTypeId, valueLoadPlan: plan);
            stopwatch.Stop();
            builtLoadMs.Add(stopwatch.ElapsedMilliseconds);

            var group = page.Results.Single();
            Assert.That(group.IsPartiallyLoaded, Is.True, "the group is loaded without its members");
            builtCsoValues = group.AttributeValues.Count;
            builtMvoValues = group.MetaverseObject!.AttributeValues.Count;
            var expected = group.MetaverseObject.AttributeValues
                .Where(v => v.AttributeId == _staticMembersAttributeId && !v.NullValue && v.ReferenceValueId.HasValue)
                .Select(v => v.ReferenceValueId!.Value).Distinct().ToList();

            stopwatch.Restart();
            builtAnswer = (await repository.Sync.GetDriftHeldReferenceAnswersAsync(
                [new DriftHeldReferenceQuestion(group.Id, _memberAttributeId, _staticMembersAttributeId, expected)])).Single();
            stopwatch.Stop();
            builtCompareMs.Add(stopwatch.ElapsedMilliseconds);
        }

        TestContext.Out.WriteLine($"#2818 benchmark: group of {Members:N0} members, {Departed:N0} departed (CSO holds {Members - Departed:N0}, MVO {Members:N0}).");
        TestContext.Out.WriteLine($"  As built, page load leaving the members out: {string.Join(", ", builtLoadMs)} ms; {builtCsoValues:N0} CSO values, " +
                                  $"{builtMvoValues:N0} MVO values materialised.");
        TestContext.Out.WriteLine($"  As built, Drift Detection's question answered in the database: {string.Join(", ", builtCompareMs)} ms; " +
                                  $"{builtAnswer!.HeldValueCount:N0} held, {builtAnswer.HeldNotExpected.Count:N0} held but not expected, " +
                                  $"{builtAnswer.ExpectedNotHeld.Count:N0} expected but not held.");
        TestContext.Out.WriteLine($"  Whole page load (GetConnectedSystemObjectsModifiedSinceAsync): {string.Join(", ", wholeLoadMs)} ms; " +
                                  $"{loadedCsoValues:N0} CSO values, {loadedMvoValues:N0} MVO values materialised.");
        TestContext.Out.WriteLine($"  Set-based comparison in the database: {string.Join(", ", compareMs)} ms; counted {csoCount:N0} CSO members, " +
                                  $"{mvoCount:N0} MVO members; {csoDisagreeing.Count:N0} CSO and {mvoDisagreeing.Length:N0} MVO values disagree.");
        TestContext.Out.WriteLine($"  Lean CSO load (EF, filtered include): {string.Join(", ", leanCsoMs)} ms; {leanCsoValues:N0} values.");
        TestContext.Out.WriteLine($"  Lean MVO value read (raw): {string.Join(", ", leanMvoMs)} ms; {leanMvoValues:N0} values.");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(csoCount, Is.EqualTo(Members - Departed));
            Assert.That(mvoCount, Is.EqualTo(Members));
            Assert.That(csoDisagreeing, Is.Empty, "every member the object holds is one the Metaverse Object holds");
            Assert.That(mvoDisagreeing, Has.Length.EqualTo(Departed), "exactly the departed members are the Metaverse Object's alone");
            Assert.That(builtAnswer.HeldNotExpected, Is.Empty, "as built: no member the group holds is unexpected");
            Assert.That(builtAnswer.ExpectedNotHeld, Has.Count.EqualTo(Departed), "as built: exactly the departed members are expected and not held");
        }
    }

    /// <summary>
    /// The comparison a set-based design asks: the object's member values whose referenced object is unjoined or joined to
    /// a Metaverse Object the Metaverse Object's members do not name ('c'), and the Metaverse Object's member values naming
    /// no object the group's joined members are joined to ('m'), with each side's count.
    /// </summary>
    private async Task<(List<(char Side, Guid Id)> Rows, long CsoCount, long MvoCount)> CompareInDatabaseAsync()
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            WITH cm AS MATERIALIZED (
                SELECT av."Id", r."MetaverseObjectId" AS ref_mvo
                FROM "ConnectedSystemObjectAttributeValues" av
                LEFT JOIN "ConnectedSystemObjects" r ON r."Id" = av."ReferenceValueId"
                WHERE av."ConnectedSystemObjectId" = @cso AND av."AttributeId" = @a),
            mm AS MATERIALIZED (
                SELECT v."Id", v."ReferenceValueId" AS ref_mvo
                FROM "MetaverseObjectAttributeValues" v
                WHERE v."MetaverseObjectId" = @mvo AND v."AttributeId" = @ma)
            SELECT 'c' AS side, cm."Id", NULL::bigint AS n FROM cm
             WHERE cm.ref_mvo IS NULL OR NOT EXISTS (SELECT 1 FROM mm WHERE mm.ref_mvo = cm.ref_mvo)
            UNION ALL
            SELECT 'm', mm."Id", NULL FROM mm
             WHERE mm.ref_mvo IS NULL OR NOT EXISTS (SELECT 1 FROM cm WHERE cm.ref_mvo = mm.ref_mvo)
            UNION ALL SELECT 'C', NULL, (SELECT count(*) FROM cm)
            UNION ALL SELECT 'M', NULL, (SELECT count(*) FROM mm)
            """, connection);
        command.Parameters.AddWithValue("cso", _groupCsoId);
        command.Parameters.AddWithValue("a", _memberAttributeId);
        command.Parameters.AddWithValue("mvo", _groupMvoId);
        command.Parameters.AddWithValue("ma", _staticMembersAttributeId);

        var rows = new List<(char, Guid)>();
        long csoCount = 0, mvoCount = 0;
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var side = reader.GetString(0)[0];
            switch (side)
            {
                case 'C': csoCount = reader.GetInt64(2); break;
                case 'M': mvoCount = reader.GetInt64(2); break;
                default: rows.Add((side, reader.GetGuid(1))); break;
            }
        }

        return (rows, csoCount, mvoCount);
    }

    private async Task<int> ReadMetaverseValuesLeavingOutAgreedAsync(Guid[] disagreeingIds)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            SELECT v."Id", v."AttributeId", v."StringValue", v."ReferenceValueId", v."ContributedBySystemId", v."ContributedBySyncRuleId"
            FROM "MetaverseObjectAttributeValues" v
            WHERE v."MetaverseObjectId" = @mvo AND (v."AttributeId" <> @ma OR v."Id" = ANY(@ids))
            """, connection);
        command.Parameters.AddWithValue("mvo", _groupMvoId);
        command.Parameters.AddWithValue("ma", _staticMembersAttributeId);
        command.Parameters.AddWithValue("ids", disagreeingIds);
        var count = 0;
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            count++;
        return count;
    }

    /// <summary>
    /// One Connected System with a user and a group Object Type, 200,000 users each joined to its own Metaverse Object,
    /// and one group marked for synchronisation, joined to a group Metaverse Object. The group holds 199,000 of the users
    /// as members; its Metaverse Object holds all 200,000.
    /// </summary>
    private async Task SeedAsync()
    {
        int userTypeId, userExternalIdId, groupExternalIdId, personTypeId, groupMvTypeId;
        await using (var seed = NewContext())
        {
            var connectorDefinition = new ConnectorDefinition { Name = "JIM Test Connector", BuiltIn = true };
            var system = new ConnectedSystem
            {
                Name = "Target", ConnectorDefinition = connectorDefinition, ObjectMatchingRuleMode = ObjectMatchingRuleMode.SyncRule
            };
            var userType = new ConnectedSystemObjectType { Name = "user", ConnectedSystem = system, Selected = true };
            var userExternalId = new ConnectedSystemObjectTypeAttribute
            {
                Name = "objectGUID", ConnectedSystemObjectType = userType, Type = AttributeDataType.Text,
                AttributePlurality = AttributePlurality.SingleValued, Selected = true, IsExternalId = true
            };
            userType.Attributes.Add(userExternalId);
            var groupType = new ConnectedSystemObjectType { Name = "group", ConnectedSystem = system, Selected = true };
            var groupExternalId = new ConnectedSystemObjectTypeAttribute
            {
                Name = "objectGUID", ConnectedSystemObjectType = groupType, Type = AttributeDataType.Text,
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

            seed.AddRange(connectorDefinition, system, userType, groupType, personType, groupMvType, staticMembers);
            await seed.SaveChangesAsync();

            (_systemId, userTypeId, _groupTypeId, userExternalIdId, groupExternalIdId, _memberAttributeId) =
                (system.Id, userType.Id, groupType.Id, userExternalId.Id, groupExternalId.Id, member.Id);
            (personTypeId, groupMvTypeId, _staticMembersAttributeId) = (personType.Id, groupMvType.Id, staticMembers.Id);
        }

        _groupCsoId = Guid.NewGuid();
        _groupMvoId = Guid.NewGuid();

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            CREATE TEMP TABLE seed_users AS
                SELECT n, gen_random_uuid() AS mvo, gen_random_uuid() AS cso FROM generate_series(1, @members) AS n;

            INSERT INTO "MetaverseObjects" ("Id", "Created", "TypeId", "Status", "DeletionInitiatedByType", "Origin")
                SELECT mvo, now() - interval '1 day', @personType, 0, 0, 0 FROM seed_users;
            INSERT INTO "ConnectedSystemObjects" ("Id", "Created", "TypeId", "ConnectedSystemId", "ExternalIdAttributeId", "Status",
                                                  "JoinType", "MetaverseObjectId")
                SELECT cso, now() - interval '1 day', @userType, @system, @userExternalId, 0, 1, mvo FROM seed_users;
            INSERT INTO "ConnectedSystemObjectAttributeValues" ("Id", "ConnectedSystemObjectId", "AttributeId", "StringValue")
                SELECT gen_random_uuid(), cso, @userExternalId, 'u' || n FROM seed_users;

            INSERT INTO "MetaverseObjects" ("Id", "Created", "TypeId", "Status", "DeletionInitiatedByType", "Origin")
                VALUES (@groupMvo, now() - interval '1 day', @groupMvType, 0, 0, 0);
            INSERT INTO "ConnectedSystemObjects" ("Id", "Created", "TypeId", "ConnectedSystemId", "ExternalIdAttributeId", "Status",
                                                  "JoinType", "MetaverseObjectId", "SynchronisationPending")
                VALUES (@groupCso, now() - interval '1 day', @groupType, @system, @groupExternalId, 0, 1, @groupMvo, true);
            INSERT INTO "ConnectedSystemObjectAttributeValues" ("Id", "ConnectedSystemObjectId", "AttributeId", "StringValue")
                VALUES (gen_random_uuid(), @groupCso, @groupExternalId, 'AllStaff');

            -- The group lost the first @departed users; its Metaverse Object has not yet.
            INSERT INTO "ConnectedSystemObjectAttributeValues" ("Id", "ConnectedSystemObjectId", "AttributeId", "ReferenceValueId",
                                                                "UnresolvedReferenceValue")
                SELECT gen_random_uuid(), @groupCso, @member, cso, 'uid=u' || n || ',ou=People' FROM seed_users WHERE n > @departed;
            INSERT INTO "MetaverseObjectAttributeValues" ("Id", "MetaverseObjectId", "AttributeId", "ReferenceValueId",
                                                          "ContributedBySystemId")
                SELECT gen_random_uuid(), @groupMvo, @staticMembers, mvo, @system FROM seed_users;

            ANALYZE "MetaverseObjects";
            ANALYZE "ConnectedSystemObjects";
            ANALYZE "ConnectedSystemObjectAttributeValues";
            ANALYZE "MetaverseObjectAttributeValues";
            """, connection);
        command.CommandTimeout = 600;
        command.Parameters.AddWithValue("members", Members);
        command.Parameters.AddWithValue("departed", Departed);
        command.Parameters.AddWithValue("personType", personTypeId);
        command.Parameters.AddWithValue("groupMvType", groupMvTypeId);
        command.Parameters.AddWithValue("userType", userTypeId);
        command.Parameters.AddWithValue("groupType", _groupTypeId);
        command.Parameters.AddWithValue("system", _systemId);
        command.Parameters.AddWithValue("userExternalId", userExternalIdId);
        command.Parameters.AddWithValue("groupExternalId", groupExternalIdId);
        command.Parameters.AddWithValue("member", _memberAttributeId);
        command.Parameters.AddWithValue("staticMembers", _staticMembersAttributeId);
        command.Parameters.AddWithValue("groupCso", _groupCsoId);
        command.Parameters.AddWithValue("groupMvo", _groupMvoId);
        await command.ExecuteNonQueryAsync();
    }
}
