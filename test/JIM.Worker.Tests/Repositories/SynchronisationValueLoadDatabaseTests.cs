// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Staging;
using JIM.PostgresData;
using JIM.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// #2818: the Delta Synchronisation page load, given a plan, leaves a large group's member attribute out where Drift
/// Detection can answer it from the database when it runs, and loads every other object whole. What it does load is
/// tracked exactly as the whole load tracks it, and it never reads the members it leaves out.
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
public class SynchronisationValueLoadDatabaseTests
{
    private const int Threshold = 20;

    private string _connectionString = null!;

    private JimDbContext NewContext(bool trackAll = false)
    {
        var builder = new DbContextOptionsBuilder<JimDbContext>()
            .UseNpgsql(_connectionString)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning));
        // The Worker tracks by default (src/JIM.Worker/Program.cs).
        if (trackAll)
            builder.UseQueryTrackingBehavior(QueryTrackingBehavior.TrackAll);
        return new JimDbContext(builder.Options);
    }

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        var connectionString = PostgresTestDatabase.ConnectionStringFromEnvironment();
        if (connectionString == null)
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL synchronisation value load tests.");
        _connectionString = connectionString!;

        TestUtilities.SetEnvironmentVariables();
        using var ctx = NewContext();
        ctx.Database.Migrate();
    }

    [SetUp]
    public async Task SetUpAsync() => await PostgresTestDatabase.ResetAsync(_connectionString);

    /// <summary>
    /// One page of groups: a large joined group, the only one left out; and one loaded whole for each reason the load
    /// checks: too small, not joined, obsolete, and one whose members include another group on the page.
    /// </summary>
    [Test]
    public async Task GetConnectedSystemObjectsModifiedSinceAsync_WithPlan_LeavesOutOnlyWhatDriftCanAnswerWhenItRunsAsync()
    {
        var seed = await DriftComparisonSeed.CreateAsync(() => NewContext(), _connectionString);
        var users = Enumerable.Range(0, Threshold * 2).Select(_ => (CsoId: Guid.NewGuid(), MvoId: (Guid?)Guid.NewGuid())).ToList();
        await seed.AddUsersAsync(users);

        var large = (CsoId: Guid.NewGuid(), MvoId: (Guid?)Guid.NewGuid(), Status: ConnectedSystemObjectStatus.Normal);
        var small = (CsoId: Guid.NewGuid(), MvoId: (Guid?)Guid.NewGuid(), Status: ConnectedSystemObjectStatus.Normal);
        var unjoined = (CsoId: Guid.NewGuid(), MvoId: (Guid?)null, Status: ConnectedSystemObjectStatus.Normal);
        var obsolete = (CsoId: Guid.NewGuid(), MvoId: (Guid?)Guid.NewGuid(), Status: ConnectedSystemObjectStatus.Obsolete);
        var nesting = (CsoId: Guid.NewGuid(), MvoId: (Guid?)Guid.NewGuid(), Status: ConnectedSystemObjectStatus.Normal);
        await seed.AddGroupsAsync([large, small, unjoined, obsolete, nesting]);
        foreach (var (group, count) in new[] { (large.CsoId, Threshold), (small.CsoId, Threshold - 1), (unjoined.CsoId, Threshold), (obsolete.CsoId, Threshold), (nesting.CsoId, Threshold) })
            await seed.AddMemberRowsAsync(group, Members(users.Take(count)));
        await seed.AddMemberRowsAsync(nesting.CsoId, [new DriftComparisonSeed.MemberRow(Guid.NewGuid(), ReferenceValueId: small.CsoId)]);
        await seed.AddStaticMembersAsync(large.MvoId!.Value, users.Select(u => u.MvoId!.Value).ToList());

        await using var ctx = NewContext(trackAll: true);
        var page = await new PostgresDataRepository(ctx).Sync.GetConnectedSystemObjectsModifiedSinceAsync(
            seed.SystemId, DateTime.UtcNow.AddHours(-1), 1, 100, knownTotalCount: 5, afterId: Guid.Empty, objectTypeId: seed.GroupTypeId,
            valueLoadPlan: Plan(seed));
        var byId = page.Results.ToDictionary(cso => cso.Id);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(page.Results, Has.Count.EqualTo(5), "the page holds every group");
            Assert.That(page.Results.Where(cso => cso.IsPartiallyLoaded).Select(cso => cso.Id), Is.EqualTo(new[] { large.CsoId }),
                "only the large joined group referencing no object on the page is left out");

            var leftOut = byId[large.CsoId];
            Assert.That(leftOut.PartiallyLoadedAttributes![seed.MemberAttributeId], Is.EqualTo(
                new SynchronisationPartiallyLoadedAttribute(seed.MemberAttributeId, "member", HoldsAny: true)));
            Assert.That(leftOut.AttributeValues.Select(v => v.AttributeId), Is.EqualTo(new[] { seed.GroupExternalIdAttributeId }),
                "its other values are loaded, its members are not");
            Assert.That(leftOut.AttributeValues.Single().Attribute, Is.Not.Null.And.Property("Id").EqualTo(seed.GroupExternalIdAttributeId));
            Assert.That(leftOut.MetaverseObject!.AttributeValues, Has.Count.EqualTo(users.Count), "its Metaverse Object stays whole");

            foreach (var (whole, members) in new[] { (small.CsoId, Threshold - 1), (unjoined.CsoId, Threshold), (obsolete.CsoId, Threshold), (nesting.CsoId, Threshold + 1) })
            {
                Assert.That(byId[whole].IsPartiallyLoaded, Is.False, $"group {whole} is loaded whole");
                Assert.That(byId[whole].AttributeValues.Count(v => v.AttributeId == seed.MemberAttributeId), Is.EqualTo(members), $"group {whole} holds every member");
            }

            // Tracked as the whole load tracks: every object and every value it loaded, so nothing under a tracked object
            // is untracked and no save could take a loaded value for a new one.
            foreach (var cso in page.Results)
            {
                Assert.That(ctx.Entry(cso).State, Is.EqualTo(EntityState.Unchanged), $"object {cso.Id} is tracked");
                Assert.That(cso.AttributeValues.Select(v => ctx.Entry(v).State), Is.All.EqualTo(EntityState.Unchanged), $"object {cso.Id}'s values are tracked");
                Assert.That(cso.AttributeValues.Select(v => v.Id), Is.EqualTo(cso.AttributeValues.Select(v => v.Id).OrderBy(PostgresOrder, StringComparer.Ordinal)),
                    $"object {cso.Id}'s values are in value id order, as the whole load gives them");
            }
        }
    }

    /// <summary>
    /// With the threshold at zero, a joined group holding no members is left out too, and says it holds none.
    /// </summary>
    [Test]
    public async Task GetConnectedSystemObjectsModifiedSinceAsync_ThresholdZero_LeavesOutAnEmptyGroupHoldingNothingAsync()
    {
        var seed = await DriftComparisonSeed.CreateAsync(() => NewContext(), _connectionString);
        var empty = (CsoId: Guid.NewGuid(), MvoId: (Guid?)Guid.NewGuid(), Status: ConnectedSystemObjectStatus.Normal);
        await seed.AddGroupsAsync([empty]);

        await using var ctx = NewContext(trackAll: true);
        var page = await new PostgresDataRepository(ctx).Sync.GetConnectedSystemObjectsModifiedSinceAsync(
            seed.SystemId, DateTime.UtcNow.AddHours(-1), 1, 100, knownTotalCount: 1, afterId: Guid.Empty, objectTypeId: seed.GroupTypeId,
            valueLoadPlan: Plan(seed, threshold: 0));

        var group = page.Results.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(group.IsPartiallyLoaded, Is.True);
            Assert.That(group.HoldsAnyValue("member"), Is.False, "it holds no member, and says so");
        }
    }

    /// <summary>
    /// Without a plan, every object is loaded whole, exactly as before.
    /// </summary>
    [Test]
    public async Task GetConnectedSystemObjectsModifiedSinceAsync_WithoutPlan_LoadsEveryObjectWholeAsync()
    {
        var seed = await DriftComparisonSeed.CreateAsync(() => NewContext(), _connectionString);
        var users = Enumerable.Range(0, Threshold).Select(_ => (CsoId: Guid.NewGuid(), MvoId: (Guid?)Guid.NewGuid())).ToList();
        await seed.AddUsersAsync(users);
        var large = (CsoId: Guid.NewGuid(), MvoId: (Guid?)Guid.NewGuid(), Status: ConnectedSystemObjectStatus.Normal);
        await seed.AddGroupsAsync([large]);
        await seed.AddMemberRowsAsync(large.CsoId, Members(users));

        await using var ctx = NewContext(trackAll: true);
        var page = await new PostgresDataRepository(ctx).Sync.GetConnectedSystemObjectsModifiedSinceAsync(
            seed.SystemId, DateTime.UtcNow.AddHours(-1), 1, 100, knownTotalCount: 1, afterId: Guid.Empty, objectTypeId: seed.GroupTypeId);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(page.Results.Single().IsPartiallyLoaded, Is.False);
            Assert.That(page.Results.Single().AttributeValues, Has.Count.EqualTo(Threshold + 1));
        }
    }

    /// <summary>
    /// The row-count guard: loading a page holding a group of 20,000 members reads at most the threshold's worth of member
    /// rows (its capped count), never the members it leaves out; the same page loaded whole reads every one.
    /// </summary>
    [Test]
    public async Task GetConnectedSystemObjectsModifiedSinceAsync_LargeGroupLeftOut_ReadsNoMemberRowsBeyondTheThresholdAsync()
    {
        const int members = 20_000;
        const int threshold = 1_000;
        var seed = await DriftComparisonSeed.CreateAsync(() => NewContext(), _connectionString);
        var users = Enumerable.Range(0, members).Select(_ => (CsoId: Guid.NewGuid(), MvoId: (Guid?)Guid.NewGuid())).ToList();
        await seed.AddUsersAsync(users);
        var group = (CsoId: Guid.NewGuid(), MvoId: (Guid?)Guid.NewGuid(), Status: ConnectedSystemObjectStatus.Normal);
        await seed.AddGroupsAsync([group]);
        await seed.AddMemberRowsAsync(group.CsoId, Members(users));
        await seed.AnalyseAsync();

        var leftOutRead = await ValueRowsReadLoadingAsync(seed, Plan(seed, threshold));
        var wholeRead = await ValueRowsReadLoadingAsync(seed, valueLoadPlan: null);

        TestContext.Out.WriteLine($"#2818 row-count guard: {leftOutRead:N0} value rows read leaving the members out, {wholeRead:N0} loading them.");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(wholeRead, Is.GreaterThanOrEqualTo(members), "the whole load reads every member");
            Assert.That(leftOutRead, Is.LessThanOrEqualTo(threshold + 10), "leaving the members out reads at most the capped count's rows");
        }
    }

    /// <summary>
    /// How many rows of the value table one page load reads, from the transaction's own statistics.
    /// </summary>
    private async Task<long> ValueRowsReadLoadingAsync(DriftComparisonSeed seed, SynchronisationValueLoadPlan? valueLoadPlan)
    {
        await using var ctx = NewContext(trackAll: true);
        await using var transaction = await ctx.Database.BeginTransactionAsync();
        await new PostgresDataRepository(ctx).Sync.GetConnectedSystemObjectsModifiedSinceAsync(
            seed.SystemId, DateTime.UtcNow.AddHours(-1), 1, 100, knownTotalCount: 1, afterId: Guid.Empty, objectTypeId: seed.GroupTypeId,
            valueLoadPlan: valueLoadPlan);

        var connection = (NpgsqlConnection)ctx.Database.GetDbConnection();
        await using var command = new NpgsqlCommand("""
            SELECT coalesce(seq_tup_read, 0) + coalesce(idx_tup_fetch, 0)
            FROM pg_stat_xact_user_tables WHERE relname = 'ConnectedSystemObjectAttributeValues'
            """, connection, (NpgsqlTransaction)transaction.GetDbTransaction());
        var read = (long)(await command.ExecuteScalarAsync() ?? 0L);
        await transaction.RollbackAsync();
        return read;
    }

    /// <summary>A uuid as PostgreSQL orders it: its bytes in the order they are written.</summary>
    private static string PostgresOrder(Guid id) => Convert.ToHexString(id.ToByteArray(bigEndian: true));

    private static SynchronisationValueLoadPlan Plan(DriftComparisonSeed seed, int threshold = Threshold) =>
        new(new Dictionary<int, IReadOnlyList<ConnectedSystemObjectTypeAttribute>> { [seed.GroupTypeId] = [seed.MemberAttribute] }, threshold);

    private static List<DriftComparisonSeed.MemberRow> Members(IEnumerable<(Guid CsoId, Guid? MvoId)> users) =>
        users.Select(u => new DriftComparisonSeed.MemberRow(Guid.NewGuid(), ReferenceValueId: u.CsoId, UnresolvedReferenceValue: $"uid={u.CsoId:N}")).ToList();
}
