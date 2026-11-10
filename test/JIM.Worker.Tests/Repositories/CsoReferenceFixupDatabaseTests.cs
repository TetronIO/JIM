// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Collections.Concurrent;
using System.Diagnostics;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Staging;
using JIM.PostgresData;
using JIM.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Real-PostgreSQL checks of the Connected System Object reference fix-up (#2706): which outstanding references it
/// resolves (a value holding a reference by identifier, with no id yet), which it leaves for a later run, that it reads
/// nothing of the objects references could name when nothing is outstanding, and the spans it reports.
/// </summary>
/// <remarks>
/// Objects and their values are written through the import's own create writer, so the tests see the state a run
/// leaves rather than a hand-built one. Spans are read through the trace of a span the test starts itself, so a
/// fix-up elsewhere in the run cannot be mistaken for this one. The case-exact rule (#2051) is covered by
/// <see cref="CaseExactIdentifierLookupDatabaseTests"/>. Opt-in via the <c>JIM_TEST_RESET_*</c> environment variables;
/// ignored when <c>JIM_TEST_RESET_DB</c> is absent.
/// </remarks>
[TestFixture]
[Category("RequiresPostgres")]
public class CsoReferenceFixupDatabaseTests
{
    private const string ManagerDn = "CN=Mo Manager,OU=People,DC=glitterband,DC=local";

    private static readonly ActivitySource TestSource = new("JIM.Tests.CsoReferenceFixupSpans");

    private string _connectionString = null!;
    private ActivityListener _listener = null!;
    private readonly ConcurrentQueue<Activity> _stopped = new();

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        var connectionString = PostgresTestDatabase.ConnectionStringFromEnvironment();
        if (connectionString == null)
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping the real-PostgreSQL Connected System Object reference fix-up tests.");
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

    private JimDbContext NewContext() => NewContext(_connectionString);

    private static JimDbContext NewContext(string connectionString) => NewContext(options => options.UseNpgsql(connectionString));

    private static JimDbContext NewContext(NpgsqlConnection connection) => NewContext(options => options.UseNpgsql(connection));

    private static JimDbContext NewContext(Action<DbContextOptionsBuilder<JimDbContext>> useNpgsql)
    {
        var builder = new DbContextOptionsBuilder<JimDbContext>();
        useNpgsql(builder);
        return new JimDbContext(builder
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options);
    }

    /// <summary>
    /// A reference whose object is not in the system yet (a group imported before the person it names, in an earlier
    /// run) is held by DN alone; when the object arrives in a later run, that run's fix-up resolves it.
    /// </summary>
    [Test]
    public async Task FixupCrossBatchReferenceIdsAsync_TargetArrivesInALaterRun_IsResolvedByThatRunAsync()
    {
        var seed = await SeedSystemAsync();
        var (_, valueIds) = await WriteObjectWithReferencesAsync(seed.Glitterband, "CN=Staff,OU=Groups,DC=glitterband,DC=local", ManagerDn.ToLowerInvariant());

        var firstRun = await FixupAsync(seed.Glitterband.SystemId);
        var managerId = await WritePersonAsync(seed.Glitterband, ManagerDn);
        var laterRun = await FixupAsync(seed.Glitterband.SystemId);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(firstRun, Is.Zero, "nothing holds the DN yet");
            Assert.That(laterRun, Is.EqualTo(1), "the later run resolves it");
            Assert.That(await ReadReferenceValueIdAsync(valueIds[0]), Is.EqualTo(managerId), "ignoring case, as DNs compare");
        }
    }

    [Test]
    public async Task FixupCrossBatchReferenceIdsAsync_ReferenceNamingNoObject_StaysUnresolvedAsync()
    {
        var seed = await SeedSystemAsync();
        await WritePersonAsync(seed.Glitterband, ManagerDn);
        var (_, valueIds) = await WriteObjectWithReferencesAsync(seed.Glitterband, "CN=Staff,OU=Groups,DC=glitterband,DC=local",
            "CN=Nobody,OU=People,DC=glitterband,DC=local");

        var resolved = await FixupAsync(seed.Glitterband.SystemId);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(resolved, Is.Zero);
            Assert.That(await ReadReferenceValueIdAsync(valueIds[0]), Is.Null, "a later run may yet find the object it names");
        }
    }

    /// <summary>
    /// The resolutions are applied in bounded statements; with a batch smaller than the backlog every outstanding
    /// reference must still be resolved to the object it names, once. A reference that already holds an id is not
    /// re-pointed, even where its DN now names another object, and another system's references are left alone, even
    /// where their DN names an object in this one.
    /// </summary>
    [Test]
    public async Task FixupCrossBatchReferenceIdsAsync_BatchSizeSmallerThanBacklog_ResolvesEachOutstandingReferenceOnceAsync()
    {
        var seed = await SeedSystemAsync();
        var people = new List<(string Dn, Guid Id)>();
        for (var n = 1; n <= 7; n++)
        {
            var dn = $"CN=Person {n},OU=People,DC=glitterband,DC=local";
            people.Add((dn, await WritePersonAsync(seed.Glitterband, dn)));
        }
        var (_, outstandingIds) = await WriteObjectWithReferencesAsync(seed.Glitterband, "CN=Staff,OU=Groups,DC=glitterband,DC=local",
            [.. people.Select(p => p.Dn.ToUpperInvariant())]);
        var formerManagerId = await WritePersonAsync(seed.Glitterband, "CN=Former Manager,OU=People,DC=glitterband,DC=local");
        var resolvedValueId = await WriteResolvedReferenceAsync(seed, people[0].Dn, formerManagerId);
        var (_, otherSystemIds) = await WriteObjectWithReferencesAsync(seed.Yellowstone, "uid=staff,ou=Groups,dc=yellowstone,dc=local", people[0].Dn);

        int resolved;
        await using (var ctx = NewContext())
            resolved = await new PostgresDataRepository(ctx).Sync.FixupCrossBatchReferenceIdsAsync(seed.Glitterband.SystemId, batchSize: 2);

        var actual = new List<Guid?>();
        foreach (var valueId in outstandingIds)
            actual.Add(await ReadReferenceValueIdAsync(valueId));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(resolved, Is.EqualTo(people.Count), "each outstanding reference is resolved and counted once");
            Assert.That(actual, Is.EqualTo(people.Select(p => (Guid?)p.Id)), "each resolves to the person whose DN it names, ignoring case");
            Assert.That(await ReadReferenceValueIdAsync(resolvedValueId), Is.EqualTo(formerManagerId), "a reference holding an id keeps it");
            Assert.That(await ReadReferenceValueIdAsync(otherSystemIds[0]), Is.Null, "another system's references are not resolved");
        }
    }

    /// <summary>
    /// Two objects holding the same DN (a duplicated Secondary External ID) both match a reference naming it; the
    /// reference takes one of them and is counted once, as the single UPDATE the fix-up replaced left it, even when the
    /// two matches would fall in different batches.
    /// </summary>
    [Test]
    public async Task FixupCrossBatchReferenceIdsAsync_ReferenceNamingTwoObjectsWithTheSameDn_ResolvesToOneAndCountsItOnceAsync()
    {
        var seed = await SeedSystemAsync();
        var firstId = await WritePersonAsync(seed.Glitterband, ManagerDn);
        var secondId = await WritePersonAsync(seed.Glitterband, ManagerDn);
        var (_, valueIds) = await WriteObjectWithReferencesAsync(seed.Glitterband, "CN=Staff,OU=Groups,DC=glitterband,DC=local", ManagerDn);

        int resolved;
        await using (var ctx = NewContext())
            resolved = await new PostgresDataRepository(ctx).Sync.FixupCrossBatchReferenceIdsAsync(seed.Glitterband.SystemId, batchSize: 1);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(resolved, Is.EqualTo(1), "one reference, resolved once");
            Assert.That(await ReadReferenceValueIdAsync(valueIds[0]), Is.AnyOf(firstId, secondId));
        }
    }

    /// <summary>
    /// #2849: reading the outstanding references and finding their resolutions run with JIT off, each for its own batch's
    /// transaction and no longer (the resolutions are applied with it on again), and turning it off raises no warning in
    /// the database log (a <c>SET LOCAL</c> logs one wherever it is the first statement of a batch outside a transaction
    /// block). The value the statement that turns it off returns is not read as either count.
    /// </summary>
    [Test]
    public async Task FixupCrossBatchReferenceIdsAsync_FindingTheResolutions_RunsWithJitOffForEachBatchWithoutAWarningAsync()
    {
        var seed = await SeedSystemAsync();
        var managerId = await WritePersonAsync(seed.Glitterband, ManagerDn);
        var (_, valueIds) = await WriteObjectWithReferencesAsync(seed.Glitterband, "CN=Staff,OU=Groups,DC=glitterband,DC=local",
            ManagerDn.ToUpperInvariant(), "CN=Nobody,OU=People,DC=glitterband,DC=local");

        await using var recorder = await StatementPlanRecorder.OpenAsync(_connectionString);
        Assert.That(await recorder.ShowAsync("jit"), Is.EqualTo("on"), "precondition: JIT is on outside the batches, or the test proves nothing");

        int resolved;
        await using (var ctx = NewContext(recorder.Connection))
            resolved = await new PostgresDataRepository(ctx).Sync.FixupCrossBatchReferenceIdsAsync(seed.Glitterband.SystemId);

        var jitAfterwards = await recorder.ShowAsync("jit");
        var readOutstanding = recorder.PlansOf("CREATE TEMP TABLE \"_jim_cso_reference_outstanding\"");
        var findResolutions = recorder.PlansOf("CREATE TEMP TABLE \"_jim_cso_reference_fixup\"");
        var applyResolutions = recorder.PlansOf("UPDATE \"ConnectedSystemObjectAttributeValues\" av");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(recorder.Notices.Where(n => n.InvariantSeverity == "WARNING").Select(n => n.MessageText), Is.Empty,
                "turning JIT off raises no warning");
            Assert.That(readOutstanding, Has.Count.EqualTo(1).And.All.Contains("jit = 'off'"), "reading the outstanding references runs with JIT off");
            Assert.That(findResolutions, Has.Count.EqualTo(1).And.All.Contains("jit = 'off'"), "finding their resolutions runs with JIT off");
            Assert.That(applyResolutions, Is.Not.Empty.And.None.Contains("jit = 'off'"), "applying them runs with JIT on again");
            Assert.That(jitAfterwards, Is.EqualTo("on"), "JIT is off for each batch's transaction only");

            Assert.That(resolved, Is.EqualTo(1), "the one resolvable reference is resolved and counted");
            Assert.That(await ReadReferenceValueIdAsync(valueIds[0]), Is.EqualTo(managerId), "to the object it names, ignoring case");
            Assert.That(await ReadReferenceValueIdAsync(valueIds[1]), Is.Null, "the reference naming no object stays outstanding");
        }
    }

    [Test]
    public async Task FixupCrossBatchReferenceIdsAsync_NonPositiveBatchSize_ThrowsAsync()
    {
        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);

        Assert.That(async () => await repository.Sync.FixupCrossBatchReferenceIdsAsync(1, batchSize: 0),
            Throws.TypeOf<ArgumentOutOfRangeException>());
    }

    /// <summary>
    /// The common case after most imports: nothing is outstanding, so the fix-up must not read the objects references
    /// could name at all. Another session holds the Connected System attribute table exclusively, which only matching a
    /// reference against Secondary External IDs needs, so a statement that so much as names it waits, and the fix-up's
    /// short lock timeout turns that wait into a failure rather than a hang.
    /// </summary>
    [Test]
    public async Task FixupCrossBatchReferenceIdsAsync_NothingOutstanding_DoesNotTouchTheTargetSideAsync()
    {
        var seed = await SeedSystemAsync();
        var managerId = await WritePersonAsync(seed.Glitterband, ManagerDn);
        await WriteResolvedReferenceAsync(seed, ManagerDn, managerId);

        await using var locker = new NpgsqlConnection(_connectionString);
        await locker.OpenAsync();
        await using var lockTransaction = await locker.BeginTransactionAsync();
        await using (var lockCommand = new NpgsqlCommand("""LOCK TABLE "ConnectedSystemAttributes" IN ACCESS EXCLUSIVE MODE""", locker, lockTransaction))
            await lockCommand.ExecuteNonQueryAsync();

        var shortLockTimeout = new NpgsqlConnectionStringBuilder(_connectionString) { Options = "-c lock_timeout=2s" };
        int resolved;
        await using (var ctx = NewContext(shortLockTimeout.ConnectionString))
            resolved = await new PostgresDataRepository(ctx).Sync.FixupCrossBatchReferenceIdsAsync(seed.Glitterband.SystemId);

        Assert.That(resolved, Is.Zero, "with nothing outstanding there is nothing to resolve, and nothing to read it from");
    }

    [Test]
    public async Task FixupCrossBatchReferenceIdsAsync_OneResolvableOfTwoReferences_ReportsResolveSpanWithCountsAndAChildPerStepAsync()
    {
        var seed = await SeedSystemAsync();
        await WritePersonAsync(seed.Glitterband, ManagerDn);
        await WriteObjectWithReferencesAsync(seed.Glitterband, "CN=Staff,OU=Groups,DC=glitterband,DC=local",
            ManagerDn, "CN=Nobody,OU=People,DC=glitterband,DC=local");
        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);

        var spans = await RecordAsync(() => repository.Sync.FixupCrossBatchReferenceIdsAsync(seed.Glitterband.SystemId));

        var resolve = spans.Single(s => s.DisplayName == "Cso.ResolveReferences");
        var children = spans.Where(s => s.ParentSpanId == resolve.SpanId).ToList();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(resolve.GetTagItem("connectedSystemId"), Is.EqualTo(seed.Glitterband.SystemId));
            Assert.That(resolve.GetTagItem("markedCount"), Is.EqualTo(2), "the parent span counts the outstanding references read");
            Assert.That(resolve.GetTagItem("candidateCount"), Is.EqualTo(1), "the parent span counts the resolutions found");
            Assert.That(resolve.GetTagItem("resolvedCount"), Is.EqualTo(1), "the parent span counts the resolutions applied");
            Assert.That(children.Select(s => s.DisplayName), Is.EquivalentTo(new[]
            {
                "Cso.ResolveReferences.FindCandidates", "Cso.ResolveReferences.Apply"
            }), "one child for finding the resolutions and one for applying them");
            var find = children.Single(s => s.DisplayName == "Cso.ResolveReferences.FindCandidates");
            Assert.That(find.GetTagItem("markedCount"), Is.EqualTo(2), "the finding step reports how many outstanding references it read");
            Assert.That(find.GetTagItem("candidateCount"), Is.EqualTo(1), "the finding step reports what it found");
            Assert.That(children.Single(s => s.DisplayName == "Cso.ResolveReferences.Apply").GetTagItem("resolvedCount"),
                Is.EqualTo(1), "the applying step reports what it resolved");
        }
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

    #region seeding

    private sealed record SeededType(int SystemId, int TypeId, int DnId, int MemberId);

    private sealed record SeededSystems(SeededType Glitterband, SeededType Yellowstone);

    /// <summary>
    /// Two Connected Systems whose objects carry their DN as the Secondary External ID and a multi-valued Reference
    /// attribute.
    /// </summary>
    private async Task<SeededSystems> SeedSystemAsync()
    {
        await using var seed = NewContext();
        var connectorDefinition = new ConnectorDefinition { Name = "Test Connector", BuiltIn = true };
        var glitterband = NewSystem(connectorDefinition, "Glitterband");
        var yellowstone = NewSystem(connectorDefinition, "Yellowstone");
        seed.AddRange(connectorDefinition, glitterband.ObjectType, yellowstone.ObjectType);
        await seed.SaveChangesAsync();

        return new SeededSystems(glitterband.ToSeeded(), yellowstone.ToSeeded());
    }

    private sealed record NewSystemSchema(ConnectedSystemObjectType ObjectType, ConnectedSystemObjectTypeAttribute Dn, ConnectedSystemObjectTypeAttribute Member)
    {
        public SeededType ToSeeded() => new(ObjectType.ConnectedSystem.Id, ObjectType.Id, Dn.Id, Member.Id);
    }

    private static NewSystemSchema NewSystem(ConnectorDefinition connectorDefinition, string name)
    {
        var system = new ConnectedSystem { Name = name, ConnectorDefinition = connectorDefinition };
        var objectType = new ConnectedSystemObjectType { Name = "entry", ConnectedSystem = system, Selected = true };
        var dn = new ConnectedSystemObjectTypeAttribute
        {
            Name = "distinguishedName", ConnectedSystemObjectType = objectType, Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued, Selected = true, IsSecondaryExternalId = true
        };
        var member = new ConnectedSystemObjectTypeAttribute
        {
            Name = "member", ConnectedSystemObjectType = objectType, Type = AttributeDataType.Reference,
            AttributePlurality = AttributePlurality.MultiValued, Selected = true
        };
        objectType.Attributes.AddRange([dn, member]);
        return new NewSystemSchema(objectType, dn, member);
    }

    /// <summary>
    /// An object in the given system whose Secondary External ID is the given DN, through the import's create writer.
    /// </summary>
    private async Task<Guid> WritePersonAsync(SeededType type, string dn)
    {
        var person = NewObject(type, dn);
        await using var ctx = NewContext();
        await new PostgresDataRepository(ctx).Sync.CreateConnectedSystemObjectsAsync([person]);
        return person.Id;
    }

    /// <summary>
    /// An object in the given system holding references by DN alone, as an import writes references to objects it did
    /// not know; returns the object's id and its reference values' ids, in the order given.
    /// </summary>
    private async Task<(Guid ObjectId, List<Guid> ValueIds)> WriteObjectWithReferencesAsync(
        SeededType type, string dn, params string[] referenceDns)
    {
        var owner = NewObject(type, dn);
        var values = referenceDns
            .Select(referenceDn => new ConnectedSystemObjectAttributeValue
            {
                Id = Guid.NewGuid(), AttributeId = type.MemberId, UnresolvedReferenceValue = referenceDn
            })
            .ToList();
        owner.AttributeValues.AddRange(values);

        await using var ctx = NewContext();
        await new PostgresDataRepository(ctx).Sync.CreateConnectedSystemObjectsAsync([owner]);
        return (owner.Id, values.Select(v => v.Id).ToList());
    }

    /// <summary>
    /// An object in the Glitterband system holding a reference the import resolved: its DN and the id of the object
    /// it was resolved to, which the writer keeps because that object is already written.
    /// </summary>
    private async Task<Guid> WriteResolvedReferenceAsync(SeededSystems seed, string referenceDn, Guid referencedObjectId)
    {
        var owner = NewObject(seed.Glitterband, $"CN=Holder {Guid.NewGuid():N},OU=Groups,DC=glitterband,DC=local");
        var value = new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(), AttributeId = seed.Glitterband.MemberId, UnresolvedReferenceValue = referenceDn, ReferenceValueId = referencedObjectId
        };
        owner.AttributeValues.Add(value);

        await using var ctx = NewContext();
        await new PostgresDataRepository(ctx).Sync.CreateConnectedSystemObjectsAsync([owner], previouslyCommittedCsoIds: [referencedObjectId]);
        Assert.That(await ReadReferenceValueIdAsync(value.Id), Is.EqualTo(referencedObjectId), "precondition: the writer keeps the id");
        return value.Id;
    }

    private static ConnectedSystemObject NewObject(SeededType type, string dn)
    {
        var cso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(), ConnectedSystemId = type.SystemId, TypeId = type.TypeId, ExternalIdAttributeId = type.DnId,
            SecondaryExternalIdAttributeId = type.DnId, Status = ConnectedSystemObjectStatus.Normal
        };
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = type.DnId, StringValue = dn });
        return cso;
    }

    private async Task<int> FixupAsync(int connectedSystemId)
    {
        await using var ctx = NewContext();
        return await new PostgresDataRepository(ctx).Sync.FixupCrossBatchReferenceIdsAsync(connectedSystemId);
    }

    private async Task<Guid?> ReadReferenceValueIdAsync(Guid valueId)
    {
        await using var ctx = NewContext();
        return await ctx.ConnectedSystemObjectAttributeValues
            .AsNoTracking()
            .Where(av => av.Id == valueId)
            .Select(av => av.ReferenceValueId)
            .SingleAsync();
    }

    #endregion
}
