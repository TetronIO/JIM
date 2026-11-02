// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Collections.Concurrent;
using System.Diagnostics;
using JIM.Models.Core;
using JIM.PostgresData;
using JIM.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Real-PostgreSQL check that both Metaverse Object deleters report their statements as spans (#2669, Phase 0): a
/// <c>Mvo.Delete</c> span carrying the number of objects, with one child per statement, so the audit rewrites the
/// plan removes are measured the same way before and after. The bulk deleter is the synchronisation flush's; the
/// singular one is the portal's and housekeeping's.
/// </summary>
/// <remarks>
/// Spans are read through the trace of a span the test starts itself, so a deletion elsewhere in the run cannot be
/// mistaken for this one. Opt-in via the <c>JIM_TEST_RESET_*</c> environment variables; ignored when
/// <c>JIM_TEST_RESET_DB</c> is absent.
/// </remarks>
[TestFixture]
[Category("RequiresPostgres")]
public class MvoDeletionSpanDatabaseTests
{
    private static readonly ActivitySource TestSource = new("JIM.Tests.MvoDeletionSpans");

    private string _connectionString = null!;
    private ActivityListener _listener = null!;
    private readonly ConcurrentQueue<Activity> _stopped = new();

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        var connectionString = PostgresTestDatabase.ConnectionStringFromEnvironment();
        if (connectionString == null)
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping the real-PostgreSQL Metaverse Object deletion span tests.");
        _connectionString = connectionString!;

        using var ctx = NewContext();
        ctx.Database.Migrate();

        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name.StartsWith("JIM.", StringComparison.Ordinal),
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = _stopped.Enqueue
        };
        ActivitySource.AddActivityListener(_listener);
    }

    [OneTimeTearDown]
    public void OneTimeTearDown() => _listener?.Dispose();

    [SetUp]
    public async Task SetUpAsync() => await PostgresTestDatabase.ResetAsync(_connectionString);

    private JimDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<JimDbContext>()
            .UseNpgsql(_connectionString)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        return new JimDbContext(options);
    }

    [Test]
    public async Task DeleteMetaverseObjectsAsync_TwoObjects_ReportsDeleteSpanWithObjectCountAndOneChildPerStatementAsync()
    {
        var ids = await SeedPeopleAsync(2);
        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);
        var people = await ctx.MetaverseObjects.Where(m => ids.Contains(m.Id)).ToListAsync();

        var spans = await RecordAsync(() => repository.Sync.DeleteMetaverseObjectsAsync(people));

        var delete = spans.Single(s => s.DisplayName == "Mvo.Delete");
        var children = spans.Where(s => s.ParentSpanId == delete.SpanId).ToList();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(delete.GetTagItem("mvoCount"), Is.EqualTo(2), "the parent span counts the objects");
            Assert.That(children.Select(s => s.DisplayName).Distinct(), Is.EquivalentTo(new[]
            {
                "Mvo.Delete.RetireGeneratedValues", "Mvo.Delete.ClearActivities", "Mvo.Delete.RekeyChangeRecords",
                "Mvo.Delete.DetachJoins", "Mvo.Delete.CleanReferenceRows", "Mvo.Delete.ClearChangeValueReferences",
                "Mvo.Delete.Delete", "Mvo.Delete.UpdateTracker", "Mvo.Delete.Commit"
            }), "one child per statement, the tracker work and the commit");
            Assert.That(children.Single(s => s.DisplayName == "Mvo.Delete.Delete").GetTagItem("rowsAffected"), Is.EqualTo(2),
                "the delete statement reports the rows it removed");
        }
    }

    [Test]
    public async Task DeleteMetaverseObjectAsync_OneObject_ReportsDeleteSpanWithObjectCountAndOneChildPerStatementAsync()
    {
        var ids = await SeedPeopleAsync(1);
        await using var ctx = NewContext();
        ctx.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.TrackAll;
        var repository = new PostgresDataRepository(ctx);
        var person = await ctx.MetaverseObjects.SingleAsync(m => m.Id == ids[0]);

        var spans = await RecordAsync(() => repository.Metaverse.DeleteMetaverseObjectAsync(person));

        var delete = spans.Single(s => s.DisplayName == "Mvo.Delete");
        var children = spans.Where(s => s.ParentSpanId == delete.SpanId).ToList();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(delete.GetTagItem("mvoCount"), Is.EqualTo(1), "the parent span counts the object");
            Assert.That(children.Select(s => s.DisplayName).Distinct(), Is.EquivalentTo(new[]
            {
                "Mvo.Delete.RetireGeneratedValues", "Mvo.Delete.ClearActivities", "Mvo.Delete.RekeyChangeRecords",
                "Mvo.Delete.DetachJoins", "Mvo.Delete.CleanReferenceRows", "Mvo.Delete.ClearChangeValueReferences",
                "Mvo.Delete.Delete", "Mvo.Delete.UpdateTracker", "Mvo.Delete.Commit"
            }), "one child per statement, the tracker work and the commit");
        }

        await using var verify = NewContext();
        Assert.That(await verify.MetaverseObjects.AnyAsync(m => m.Id == ids[0]), Is.False, "the object is deleted");
    }

    /// <summary>
    /// Runs <paramref name="action"/> under a span of the test's own and returns every span completed in its trace.
    /// </summary>
    private async Task<List<Activity>> RecordAsync(Func<Task> action)
    {
        ActivityTraceId traceId;
        using (var root = TestSource.StartActivity("Test"))
        {
            Assert.That(root, Is.Not.Null, "the test's listener samples its own source");
            traceId = root!.TraceId;
            await action();
        }
        return [.. _stopped.Where(s => s.TraceId == traceId)];
    }

    private async Task<List<Guid>> SeedPeopleAsync(int count)
    {
        await using var seed = NewContext();
        var personType = new MetaverseObjectType { Name = "Person", PluralName = "People" };
        var people = Enumerable.Range(0, count).Select(_ => new MetaverseObject { Type = personType }).ToList();
        seed.AddRange(personType);
        seed.AddRange(people);
        await seed.SaveChangesAsync();
        return [.. people.Select(p => p.Id)];
    }
}
