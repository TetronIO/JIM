// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Staging;
using JIM.Models.Sync;
using JIM.PostgresData;
using JIM.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Real-PostgreSQL verification of the synchronisation-pending mark (#2092): <c>ConnectedSystemObjects.SynchronisationPending</c>
/// is set by every writer that changes what synchronisation must see, in the same statement as the change; selected
/// by Delta Synchronisation and treated as changed by Full Synchronisation's unchanged-object skip; never written by
/// a whole-entity save; and cleared only under the row version (<c>xmin</c>) the synchronisation's page load read,
/// so a change committed after that load survives the clear. The guard depends on <c>xmin</c>, which the in-memory
/// provider can only model. Opt-in via the same <c>JIM_TEST_RESET_*</c> environment variables as the other
/// RequiresPostgres fixtures.
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
public class SynchronisationPendingDatabaseTests
{
    private string _connectionString = null!;

    private JimDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<JimDbContext>()
            .UseNpgsql(_connectionString)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        return new JimDbContext(options);
    }

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        var dbName = Environment.GetEnvironmentVariable("JIM_TEST_RESET_DB");
        if (string.IsNullOrEmpty(dbName))
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL synchronisation-pending mark tests.");

        var host = Environment.GetEnvironmentVariable("JIM_TEST_RESET_HOST") ?? "localhost";
        var user = Environment.GetEnvironmentVariable("JIM_TEST_RESET_USER") ?? "postgres";
        var pass = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PASSWORD") ?? "postgres";
        var port = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PORT") ?? "5432";
        _connectionString = $"Host={host};Port={port};Database={dbName};Username={user};Password={pass}";

        using var ctx = NewContext();
        ctx.Database.Migrate();
    }

    [SetUp]
    public async Task SetUpAsync()
    {
        await PostgresTestDatabase.ResetAsync(_connectionString);
    }

    private sealed record Seeded(int SystemId, int TypeId, int ExternalIdAttributeId, Guid MvoId, Guid UnjoinedMvoId, Guid JoinedCsoId, Guid UnjoinedCsoId);

    /// <summary>
    /// One Connected System with a joined and an unjoined object, both created and last updated two hours ago and
    /// unmarked, so a watermark of one hour ago sees neither as changed.
    /// </summary>
    private async Task<Seeded> SeedAsync()
    {
        await using var seed = NewContext();
        var twoHoursAgo = DateTime.UtcNow.AddHours(-2);

        var connectorDefinition = new ConnectorDefinition { Name = "Test Connector", BuiltIn = true };
        var system = new ConnectedSystem { Name = "HR", ConnectorDefinition = connectorDefinition };
        var type = new ConnectedSystemObjectType { Name = "person", ConnectedSystem = system, Selected = true };
        var idAttribute = new ConnectedSystemObjectTypeAttribute
        {
            Name = "employeeId", ConnectedSystemObjectType = type, Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued, Selected = true, IsExternalId = true
        };
        type.Attributes.Add(idAttribute);
        var mvoType = new MetaverseObjectType { Name = "Person", PluralName = "People" };
        var mvo = new MetaverseObject { Id = Guid.NewGuid(), Type = mvoType, Created = twoHoursAgo };
        var unjoinedMvo = new MetaverseObject { Id = Guid.NewGuid(), Type = mvoType, Created = twoHoursAgo };
        seed.AddRange(connectorDefinition, system, type, mvoType, mvo, unjoinedMvo);
        await seed.SaveChangesAsync();

        ConnectedSystemObject Cso(MetaverseObject? joinedTo, string value)
        {
            var cso = new ConnectedSystemObject
            {
                Id = Guid.NewGuid(), ConnectedSystem = system, Type = type, ExternalIdAttributeId = idAttribute.Id,
                Status = ConnectedSystemObjectStatus.Normal, Created = twoHoursAgo, LastUpdated = twoHoursAgo,
                MetaverseObject = joinedTo,
                JoinType = joinedTo == null ? ConnectedSystemObjectJoinType.NotJoined : ConnectedSystemObjectJoinType.Joined
            };
            cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Attribute = idAttribute, AttributeId = idAttribute.Id, StringValue = value });
            seed.Add(cso);
            return cso;
        }

        var joined = Cso(mvo, "E1");
        var unjoined = Cso(null, "E2");
        await seed.SaveChangesAsync();

        return new Seeded(system.Id, type.Id, idAttribute.Id, mvo.Id, unjoinedMvo.Id, joined.Id, unjoined.Id);
    }

    private async Task<Dictionary<Guid, bool>> ReadMarksAsync()
    {
        await using var read = NewContext();
        return await read.ConnectedSystemObjects.AsNoTracking().ToDictionaryAsync(c => c.Id, c => c.SynchronisationPending);
    }

    private async Task<uint> ReadRowVersionAsync(Guid csoId)
    {
        await using var read = NewContext();
        return await read.ConnectedSystemObjects.AsNoTracking().Where(c => c.Id == csoId).Select(c => c.xmin).SingleAsync();
    }

    private async Task SetMarkDirectlyAsync(Guid csoId, bool value)
    {
        await using var ctx = NewContext();
        await ctx.Database.ExecuteSqlRawAsync(
            @"UPDATE ""ConnectedSystemObjects"" SET ""SynchronisationPending"" = {0} WHERE ""Id"" = {1}", value, csoId);
    }

    private static ConnectedSystemObject NewUnsavedCso(Seeded s, string value)
    {
        var cso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(), ConnectedSystemId = s.SystemId, TypeId = s.TypeId, ExternalIdAttributeId = s.ExternalIdAttributeId,
            Status = ConnectedSystemObjectStatus.Normal, Created = DateTime.UtcNow.AddHours(-3),
            SynchronisationPending = false
        };
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = s.ExternalIdAttributeId, StringValue = value });
        return cso;
    }

    #region Writers set the mark

    /// <summary>
    /// Import create (the COPY writer, also used for objects export evaluation provisions at page flush): the mark is
    /// written as a constant, whatever the entity holds, in the same statement as the row.
    /// </summary>
    [Test]
    public async Task CreateConnectedSystemObjectsAsync_SyncBulkCopy_WritesTheMarkAsync()
    {
        var s = await SeedAsync();
        var created = NewUnsavedCso(s, "E9");

        await using (var ctx = NewContext())
            await new PostgresDataRepository(ctx).Sync.CreateConnectedSystemObjectsAsync([created]);

        Assert.That((await ReadMarksAsync())[created.Id], Is.True, "a created object is pending synchronisation");
    }

    [Test]
    public async Task CreateConnectedSystemObjectsAsync_ParameterisedInsert_WritesTheMarkAsync()
    {
        var s = await SeedAsync();
        var created = NewUnsavedCso(s, "E9");

        await using (var ctx = NewContext())
            await new PostgresDataRepository(ctx).ConnectedSystems.CreateConnectedSystemObjectsAsync([created]);

        Assert.That((await ReadMarksAsync())[created.Id], Is.True, "a created object is pending synchronisation");
    }

    /// <summary>
    /// The single-object EF insert export evaluation uses when it provisions without deferring the save.
    /// </summary>
    [Test]
    public async Task CreateConnectedSystemObjectAsync_EfInsert_WritesTheMarkAsync()
    {
        var s = await SeedAsync();
        var created = NewUnsavedCso(s, "E9");

        await using (var ctx = NewContext())
            await new PostgresDataRepository(ctx).ConnectedSystems.CreateConnectedSystemObjectAsync(created);

        Assert.That((await ReadMarksAsync())[created.Id], Is.True, "a provisioned object is pending synchronisation");
    }

    /// <summary>
    /// Import update: the bulk update writes a constant true, never the entity's value, so an import that holds an
    /// object loaded before a synchronisation cleared it still marks the change it is committing.
    /// </summary>
    [Test]
    public async Task UpdateConnectedSystemObjectsAsync_EntityHoldsFalse_WritesTheMarkAsync()
    {
        var s = await SeedAsync();

        await using (var ctx = NewContext())
        {
            var cso = await ctx.ConnectedSystemObjects.AsNoTracking().SingleAsync(c => c.Id == s.JoinedCsoId);
            Assert.That(cso.SynchronisationPending, Is.False, "precondition");
            cso.LastUpdated = DateTime.UtcNow;
            await new PostgresDataRepository(ctx).Sync.UpdateConnectedSystemObjectsAsync([cso]);
        }

        var marks = await ReadMarksAsync();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(marks[s.JoinedCsoId], Is.True, "an updated object is pending synchronisation");
            Assert.That(marks[s.UnjoinedCsoId], Is.False, "an object the import did not write is not marked");
        }
    }

    /// <summary>
    /// Full Import deletion detection and an explicit Delta Import delete both persist the Obsolete status through the
    /// import's bulk update, so the deletion carries the mark with it.
    /// </summary>
    [Test]
    public async Task UpdateConnectedSystemObjectsAsync_ImportMarksObjectObsolete_WritesTheMarkAsync()
    {
        var s = await SeedAsync();

        await using (var ctx = NewContext())
        {
            var cso = await ctx.ConnectedSystemObjects.AsNoTracking().SingleAsync(c => c.Id == s.JoinedCsoId);
            cso.Status = ConnectedSystemObjectStatus.Obsolete;
            cso.LastUpdated = DateTime.UtcNow;
            await new PostgresDataRepository(ctx).Sync.UpdateConnectedSystemObjectsAsync([cso]);
        }

        Assert.That((await ReadMarksAsync())[s.JoinedCsoId], Is.True, "a deletion is pending synchronisation");
    }

    [Test]
    public async Task ObsoleteConnectedSystemObjectsByIdsAsync_SchemaRefreshRemoval_WritesTheMarkAsync()
    {
        var s = await SeedAsync();

        await using (var ctx = NewContext())
            await new PostgresDataRepository(ctx).ConnectedSystems.ObsoleteConnectedSystemObjectsByIdsAsync([s.JoinedCsoId]);

        var marks = await ReadMarksAsync();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(marks[s.JoinedCsoId], Is.True, "an object Schema Refresh Removal obsoleted is pending synchronisation");
            Assert.That(marks[s.UnjoinedCsoId], Is.False, "an object it did not obsolete is not marked");
        }
    }

    [Test]
    public async Task MarkConnectedSystemObjectsSynchronisationPendingAsync_MarksTheJoinedObjectAndMovesItsRowVersionAsync()
    {
        // A Unique Value Generation commit loser (#2309): its own data has not changed, but its next synchronisation
        // must move it to another candidate. Addressed by join, already-marked rows re-written so xmin moves.
        var s = await SeedAsync();
        await SetMarkDirectlyAsync(s.JoinedCsoId, true);
        var versionBefore = await ReadRowVersionAsync(s.JoinedCsoId);
        await SetMarkDirectlyAsync(s.JoinedCsoId, false);
        var versionUnmarked = await ReadRowVersionAsync(s.JoinedCsoId);

        await using var ctx = NewContext();
        var tracked = await ctx.ConnectedSystemObjects.AsTracking().SingleAsync(c => c.Id == s.JoinedCsoId);
        var newlyMarked = await new PostgresDataRepository(ctx).Sync.MarkConnectedSystemObjectsSynchronisationPendingAsync(
            [new SynchronisationPendingMark(s.MvoId, s.SystemId), new SynchronisationPendingMark(s.MvoId, s.SystemId + 1000),
             new SynchronisationPendingMark(s.UnjoinedMvoId, s.SystemId)]);

        var marks = await ReadMarksAsync();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(newlyMarked, Is.EqualTo(1), "only the object joined to the Metaverse Object in the named system matches");
            Assert.That(marks[s.JoinedCsoId], Is.True);
            Assert.That(marks[s.UnjoinedCsoId], Is.False, "an unjoined object is never addressed by a join");
            Assert.That(await ReadRowVersionAsync(s.JoinedCsoId), Is.Not.EqualTo(versionUnmarked).And.Not.EqualTo(versionBefore),
                "the write moves xmin, so a run that loaded the object earlier cannot clear the mark");
            Assert.That(tracked.SynchronisationPending, Is.True, "the tracked instance must reflect the row the raw UPDATE wrote");
            Assert.That(ctx.Entry(tracked).Property(c => c.SynchronisationPending).IsModified, Is.False,
                "the fix-up records database state, not a pending change");
        }

        var again = await new PostgresDataRepository(ctx).Sync.MarkConnectedSystemObjectsSynchronisationPendingAsync(
            [new SynchronisationPendingMark(s.MvoId, s.SystemId)]);
        Assert.That(again, Is.Zero, "an already-marked row is re-written but not counted");
    }

    /// <summary>
    /// The mark is written by raw SQL on a context that may hold the object tracked; the tracked instance must reflect
    /// it, so nothing reading the instance later in the run sees a stale false.
    /// </summary>
    [Test]
    public async Task MarkingWriters_TrackedInstance_IsFixedUpAsync()
    {
        var s = await SeedAsync();

        await using var ctx = NewContext();
        var tracked = await ctx.ConnectedSystemObjects.AsTracking().SingleAsync(c => c.Id == s.JoinedCsoId);
        await new PostgresDataRepository(ctx).ConnectedSystems.ObsoleteConnectedSystemObjectsByIdsAsync([s.JoinedCsoId]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(tracked.SynchronisationPending, Is.True, "the tracked instance must reflect the row the raw UPDATE wrote");
            Assert.That(ctx.Entry(tracked).Property(c => c.SynchronisationPending).IsModified, Is.False,
                "the fix-up records database state, not a pending change");
        }
    }

    #endregion

    #region No whole-entity save writes the mark

    /// <summary>
    /// <c>UpdateConnectedSystemObjectAsync</c> marks every column Modified (<c>UpdateDetachedSafe</c>). An instance
    /// loaded before an import marked the object holds false; saving it must not write that false over the mark.
    /// </summary>
    [Test]
    public async Task UpdateConnectedSystemObjectAsync_InstanceLoadedBeforeAConcurrentMark_DoesNotOverwriteItAsync()
    {
        var s = await SeedAsync();

        await using var staleContext = NewContext();
        var stale = await staleContext.ConnectedSystemObjects.AsTracking().SingleAsync(c => c.Id == s.JoinedCsoId);
        Assert.That(stale.SynchronisationPending, Is.False, "precondition: loaded before the mark");

        // An import on another connection marks the object.
        await SetMarkDirectlyAsync(s.JoinedCsoId, true);

        stale.LastUpdated = DateTime.UtcNow;
        await new PostgresDataRepository(staleContext).ConnectedSystems.UpdateConnectedSystemObjectAsync(stale);

        Assert.That((await ReadMarksAsync())[s.JoinedCsoId], Is.True, "a whole-entity save must not clear a concurrently set mark");
    }

    /// <summary>
    /// The same exposure for the derived-input mark (#1750): it had no EF save-behaviour configuration, so a whole-entity
    /// save of an instance loaded before another system's mark wrote the stale false back.
    /// </summary>
    [Test]
    public async Task UpdateConnectedSystemObjectAsync_InstanceLoadedBeforeAConcurrentDerivedInputMark_DoesNotOverwriteItAsync()
    {
        var s = await SeedAsync();

        await using var staleContext = NewContext();
        var stale = await staleContext.ConnectedSystemObjects.AsTracking().SingleAsync(c => c.Id == s.JoinedCsoId);

        await using (var other = NewContext())
        {
            await new PostgresDataRepository(other).Sync.MarkConnectedSystemObjectsDerivedInputChangePendingAsync(
                [new DerivedInputChangeMark(s.MvoId, s.SystemId)]);
        }

        await new PostgresDataRepository(staleContext).ConnectedSystems.UpdateConnectedSystemObjectAsync(stale);

        await using var read = NewContext();
        var stored = await read.ConnectedSystemObjects.AsNoTracking().SingleAsync(c => c.Id == s.JoinedCsoId);
        Assert.That(stored.DerivedInputChangePending, Is.True, "a whole-entity save must not clear a concurrently set derived-input mark");
    }

    #endregion

    #region Selection

    [Test]
    public async Task GetConnectedSystemObjectsModifiedSinceAsync_UnchangedButMarkedObject_IsSelectedCountedAndLoadedAsync()
    {
        var s = await SeedAsync();
        var watermark = DateTime.UtcNow.AddHours(-1);
        await SetMarkDirectlyAsync(s.JoinedCsoId, true);

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);
        var count = await repository.Sync.GetConnectedSystemObjectModifiedSinceCountAsync(s.SystemId, watermark);
        var page = await repository.Sync.GetConnectedSystemObjectsModifiedSinceAsync(s.SystemId, watermark, 1, 100, count, Guid.Empty);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(count, Is.EqualTo(1), "the marked object is counted although its timestamps are older than the watermark");
            Assert.That(page.Results.Select(c => c.Id), Is.EquivalentTo(new[] { s.JoinedCsoId }));
            Assert.That(page.Results.Single().AttributeValues, Has.Count.EqualTo(1), "a marked object is loaded with its attribute values");
        }
    }

    [Test]
    public async Task GetConnectedSystemObjectsAsync_WithWatermarkUnchangedButMarkedObject_IsPartitionedAsChangedAsync()
    {
        var s = await SeedAsync();
        var watermark = DateTime.UtcNow.AddHours(-1);
        await SetMarkDirectlyAsync(s.JoinedCsoId, true);

        await using var ctx = NewContext();
        var page = await new PostgresDataRepository(ctx).Sync.GetConnectedSystemObjectsAsync(
            s.SystemId, 1, 100, knownTotalCount: 2, lastSyncTimestamp: watermark, afterId: Guid.Empty);

        var marked = page.Results.Single(c => c.Id == s.JoinedCsoId);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(marked.IsUnchangedSinceLastSync, Is.False, "Full Synchronisation's unchanged skip must not skip a marked object");
            Assert.That(marked.AttributeValues, Has.Count.EqualTo(1), "a marked object's attribute values are loaded");
        }
    }

    /// <summary>
    /// Paging is by keyset until a page comes back empty, so a row that entered the selection after the count was
    /// taken must still be returned beyond the page count that count implies.
    /// </summary>
    [Test]
    public async Task GetConnectedSystemObjectsModifiedSinceAsync_KeysetPageBeyondTheCountedPages_StillReturnsRowsAsync()
    {
        var s = await SeedAsync();
        var watermark = DateTime.UtcNow.AddHours(-1);
        await SetMarkDirectlyAsync(s.JoinedCsoId, true);
        await SetMarkDirectlyAsync(s.UnjoinedCsoId, true);

        await using var ctx = NewContext();
        var page = await new PostgresDataRepository(ctx).Sync.GetConnectedSystemObjectsModifiedSinceAsync(
            s.SystemId, watermark, page: 3, pageSize: 1, knownTotalCount: 1, afterId: Guid.Empty);

        Assert.That(page.Results, Has.Count.EqualTo(1), "a keyset page is returned whatever the count taken at the start implied");
    }

    [Test]
    public async Task GetConnectedSystemObjectsAsync_KeysetPageBeyondTheCountedPages_StillReturnsRowsAsync()
    {
        var s = await SeedAsync();

        await using var ctx = NewContext();
        var page = await new PostgresDataRepository(ctx).Sync.GetConnectedSystemObjectsAsync(
            s.SystemId, page: 3, pageSize: 1, knownTotalCount: 1, lastSyncTimestamp: null, afterId: Guid.Empty);

        Assert.That(page.Results, Has.Count.EqualTo(1), "a keyset page is returned whatever the count taken at the start implied");
    }

    #endregion

    #region Guarded clear

    [Test]
    public async Task ClearConnectedSystemObjectMarksAsync_RowUnchangedSinceThePageLoad_ClearsAndFixesUpAsync()
    {
        var s = await SeedAsync();
        var watermark = DateTime.UtcNow.AddHours(-1);
        await SetMarkDirectlyAsync(s.JoinedCsoId, true);
        await SetMarkDirectlyAsync(s.UnjoinedCsoId, true);

        await using var run = NewContext();
        var repository = new PostgresDataRepository(run);
        var page = await repository.Sync.GetConnectedSystemObjectsModifiedSinceAsync(s.SystemId, watermark, 1, 100, null, Guid.Empty);
        var loaded = page.Results.Single(c => c.Id == s.JoinedCsoId);

        var result = await repository.Sync.ClearConnectedSystemObjectMarksAsync(
            [new ConnectedSystemObjectMarkClear(s.JoinedCsoId, loaded.xmin, SynchronisationPending: true, DerivedInputChangePending: false)]);

        var marks = await ReadMarksAsync();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.SynchronisationPendingCleared, Is.EqualTo(1));
            Assert.That(marks[s.JoinedCsoId], Is.False, "nobody wrote the row after the load, so the clear applies");
            Assert.That(marks[s.UnjoinedCsoId], Is.True, "an object not named keeps its mark");
            Assert.That(loaded.SynchronisationPending, Is.False, "the tracked instance is fixed up");
        }
    }

    /// <summary>
    /// The race the guard exists for: the synchronisation loads a marked object, an import then commits a further
    /// change to it (re-marking it), and only then does the synchronisation clear what it processed. It processed the
    /// old values, so the mark must survive and the next run process the new ones.
    /// </summary>
    [Test]
    public async Task ClearConnectedSystemObjectMarksAsync_RowWrittenAfterThePageLoad_StaysMarkedAsync()
    {
        var s = await SeedAsync();
        var watermark = DateTime.UtcNow.AddHours(-1);
        await SetMarkDirectlyAsync(s.JoinedCsoId, true);

        await using var run = NewContext();
        var repository = new PostgresDataRepository(run);
        var page = await repository.Sync.GetConnectedSystemObjectsModifiedSinceAsync(s.SystemId, watermark, 1, 100, null, Guid.Empty);
        var loaded = page.Results.Single(c => c.Id == s.JoinedCsoId);

        // An import commits a further change on its own connection.
        await using (var import = NewContext())
        {
            var cso = await import.ConnectedSystemObjects.AsNoTracking().SingleAsync(c => c.Id == s.JoinedCsoId);
            cso.LastUpdated = DateTime.UtcNow;
            await new PostgresDataRepository(import).Sync.UpdateConnectedSystemObjectsAsync([cso]);
        }

        var result = await repository.Sync.ClearConnectedSystemObjectMarksAsync(
            [new ConnectedSystemObjectMarkClear(s.JoinedCsoId, loaded.xmin, SynchronisationPending: true, DerivedInputChangePending: false)]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.SynchronisationPendingCleared, Is.Zero, "the row was written after this run read it");
            Assert.That((await ReadMarksAsync())[s.JoinedCsoId], Is.True, "the mark survives, so the next run processes the change");
        }
    }

    /// <summary>
    /// An import that is mid-commit holds its rows locked. The clear must never wait for it: waiting is how the clear
    /// and the import deadlocked at runtime (each holding rows the other wanted). A locked row is about to be written,
    /// which moves its row version anyway, so skipping it is the guard's own outcome: it stays marked and the next run
    /// processes it. The rows nobody holds are cleared as usual.
    /// </summary>
    [Test]
    public async Task ClearConnectedSystemObjectMarksAsync_RowLockedByAnUncommittedWriter_IsSkippedWithoutWaitingAsync()
    {
        var s = await SeedAsync();
        await SetMarkDirectlyAsync(s.JoinedCsoId, true);
        await SetMarkDirectlyAsync(s.UnjoinedCsoId, true);
        var seenJoined = await ReadRowVersionAsync(s.JoinedCsoId);
        var seenUnjoined = await ReadRowVersionAsync(s.UnjoinedCsoId);

        // An import's transaction has written one of the rows and not yet committed.
        await using var import = NewContext();
        await using var importTransaction = await import.Database.BeginTransactionAsync();
        await import.Database.ExecuteSqlRawAsync(
            @"UPDATE ""ConnectedSystemObjects"" SET ""LastUpdated"" = now() WHERE ""Id"" = {0}", s.JoinedCsoId);

        await using var run = NewContext();
        run.Database.SetCommandTimeout(5);
        ConnectedSystemObjectMarkClearResult result = default;
        Assert.That(async () => result = await new PostgresDataRepository(run).Sync.ClearConnectedSystemObjectMarksAsync(
            [
                new ConnectedSystemObjectMarkClear(s.JoinedCsoId, seenJoined, SynchronisationPending: true, DerivedInputChangePending: false),
                new ConnectedSystemObjectMarkClear(s.UnjoinedCsoId, seenUnjoined, SynchronisationPending: true, DerivedInputChangePending: false)
            ]), Throws.Nothing, "the clear must not wait on a row another transaction holds");

        await importTransaction.CommitAsync();
        var marks = await ReadMarksAsync();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.SynchronisationPendingCleared, Is.EqualTo(1));
            Assert.That(marks[s.JoinedCsoId], Is.True, "the row the import held keeps its mark");
            Assert.That(marks[s.UnjoinedCsoId], Is.False, "the row nobody held is cleared");
        }
    }

    /// <summary>
    /// The derived-input mark (#1750) is cleared by the same statement, and only where asked: a row carrying both marks
    /// whose derived flows failed keeps its derived-input mark while its synchronisation-pending mark is cleared.
    /// </summary>
    [Test]
    public async Task ClearConnectedSystemObjectMarksAsync_ClearsEachMarkOnlyWhereAskedAsync()
    {
        var s = await SeedAsync();
        await using (var mark = NewContext())
        {
            await new PostgresDataRepository(mark).Sync.MarkConnectedSystemObjectsDerivedInputChangePendingAsync(
                [new DerivedInputChangeMark(s.MvoId, s.SystemId)]);
        }
        await SetMarkDirectlyAsync(s.JoinedCsoId, true);
        var seen = await ReadRowVersionAsync(s.JoinedCsoId);

        ConnectedSystemObjectMarkClearResult result;
        await using (var run = NewContext())
        {
            result = await new PostgresDataRepository(run).Sync.ClearConnectedSystemObjectMarksAsync(
                [new ConnectedSystemObjectMarkClear(s.JoinedCsoId, seen, SynchronisationPending: true, DerivedInputChangePending: false)]);
        }

        await using var read = NewContext();
        var stored = await read.ConnectedSystemObjects.AsNoTracking().SingleAsync(c => c.Id == s.JoinedCsoId);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.SynchronisationPendingCleared, Is.EqualTo(1));
            Assert.That(result.DerivedInputChangePendingCleared, Is.Zero);
            Assert.That(stored.SynchronisationPending, Is.False);
            Assert.That(stored.DerivedInputChangePending, Is.True, "the derived-input mark was not asked to be cleared");
        }
    }

    /// <summary>
    /// The synchronisation's own join-state write moves the row's xmin, so a guarded clear issued after it would always
    /// miss and every newly joined object would be processed again by the next run. The clear is folded into the
    /// join-state statement, guarded by the version the page load read.
    /// </summary>
    [Test]
    public async Task UpdateConnectedSystemObjectJoinStatesAsync_WithSeenRowVersions_ClearsTheMarkInTheSameStatementAsync()
    {
        var s = await SeedAsync();
        var watermark = DateTime.UtcNow.AddHours(-1);
        await SetMarkDirectlyAsync(s.UnjoinedCsoId, true);

        await using var run = NewContext();
        var repository = new PostgresDataRepository(run);
        var page = await repository.Sync.GetConnectedSystemObjectsModifiedSinceAsync(s.SystemId, watermark, 1, 100, null, Guid.Empty);
        var loaded = page.Results.Single(c => c.Id == s.UnjoinedCsoId);
        var seen = new Dictionary<Guid, uint> { [loaded.Id] = loaded.xmin };

        loaded.MetaverseObjectId = s.UnjoinedMvoId;
        loaded.JoinType = ConnectedSystemObjectJoinType.Joined;
        loaded.DateJoined = DateTime.UtcNow;
        var cleared = await repository.Sync.UpdateConnectedSystemObjectJoinStatesAsync([loaded], seen);

        await using var read = NewContext();
        var stored = await read.ConnectedSystemObjects.AsNoTracking().SingleAsync(c => c.Id == s.UnjoinedCsoId);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(cleared, Is.EqualTo(1));
            Assert.That(stored.MetaverseObjectId, Is.EqualTo(s.UnjoinedMvoId), "the join is written");
            Assert.That(stored.SynchronisationPending, Is.False, "and the mark is cleared with it");
            Assert.That(loaded.SynchronisationPending, Is.False, "the tracked instance is fixed up");
        }
    }

    [Test]
    public async Task UpdateConnectedSystemObjectJoinStatesAsync_RowWrittenAfterThePageLoad_WritesTheJoinButKeepsTheMarkAsync()
    {
        var s = await SeedAsync();
        var watermark = DateTime.UtcNow.AddHours(-1);
        await SetMarkDirectlyAsync(s.UnjoinedCsoId, true);

        await using var run = NewContext();
        var repository = new PostgresDataRepository(run);
        var page = await repository.Sync.GetConnectedSystemObjectsModifiedSinceAsync(s.SystemId, watermark, 1, 100, null, Guid.Empty);
        var loaded = page.Results.Single(c => c.Id == s.UnjoinedCsoId);
        var seen = new Dictionary<Guid, uint> { [loaded.Id] = loaded.xmin };

        await SetMarkDirectlyAsync(s.UnjoinedCsoId, true);

        loaded.MetaverseObjectId = s.UnjoinedMvoId;
        loaded.JoinType = ConnectedSystemObjectJoinType.Joined;
        loaded.DateJoined = DateTime.UtcNow;
        var cleared = await repository.Sync.UpdateConnectedSystemObjectJoinStatesAsync([loaded], seen);

        await using var read = NewContext();
        var stored = await read.ConnectedSystemObjects.AsNoTracking().SingleAsync(c => c.Id == s.UnjoinedCsoId);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(cleared, Is.Zero);
            Assert.That(stored.MetaverseObjectId, Is.EqualTo(s.UnjoinedMvoId), "the join is written regardless");
            Assert.That(stored.SynchronisationPending, Is.True, "a change committed after the load keeps its mark");
        }
    }

    #endregion
}
