// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.Models.Transactional.DTOs;
using JIM.PostgresData;
using JIM.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Real-PostgreSQL coverage for #2039 Phase 2: the <c>AwaitsInitialPassword</c> flag on a Pending Export attribute
/// change through every raw-SQL path that writes it (staging, the export result, the confirming import), and the two
/// lookups the hold and release rely on: which Metaverse Objects are still owed a password on a Connected System, and
/// which Connected System Objects carry a held change (Drift Detection's skip). The in-memory provider cannot see a
/// writer that leaves the column out, writes it in the wrong position, or a query against the wrong table.
/// Opt-in via <c>JIM_TEST_RESET_*</c>; ignored when absent. <c>SetUp</c> TRUNCATEs every table.
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
public class PendingExportAwaitsInitialPasswordDatabaseTests
{
    private string _connectionString = null!;

    private JimDbContext NewContext(IInterceptor? interceptor = null)
    {
        var builder = new DbContextOptionsBuilder<JimDbContext>()
            .UseNpgsql(_connectionString)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning));
        if (interceptor != null)
            builder.AddInterceptors(interceptor);
        return new JimDbContext(builder.Options);
    }

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        var dbName = Environment.GetEnvironmentVariable("JIM_TEST_RESET_DB");
        if (string.IsNullOrEmpty(dbName))
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL Initial Password hold tests.");

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

    [Test]
    public async Task CreatePendingExportsAsync_ChangesWithAndWithoutTheFlag_RoundTripTheFlagAsync()
    {
        var s = await SeedAsync();
        var pendingExport = NewPendingExport(s.SystemId, s.CsoIds[0], s.AttributeId);
        pendingExport.AttributeValueChanges[0].AwaitsInitialPassword = true;
        pendingExport.AttributeValueChanges.Add(NewChange(s.AttributeId, awaitsInitialPassword: false));

        await using (var write = NewContext())
            await new PostgresDataRepository(write).Sync.CreatePendingExportsAsync([pendingExport]);

        var stored = await ReadFlagsAsync(pendingExport.Id);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(stored[pendingExport.AttributeValueChanges[0].Id], Is.True);
            Assert.That(stored[pendingExport.AttributeValueChanges[1].Id], Is.False);
        }
    }

    /// <summary>
    /// The export run marks the flag after the Connector reports an interim value, and persists it through the
    /// export-result bulk update, on a context that already tracks the rows (as the worker's does).
    /// </summary>
    [Test]
    public async Task UpdatePendingExportsAsync_FlagSetAfterTheExport_PersistsItAsync()
    {
        var s = await SeedAsync();
        var pendingExport = NewPendingExport(s.SystemId, s.CsoIds[0], s.AttributeId);
        await using (var write = NewContext())
            await new PostgresDataRepository(write).Sync.CreatePendingExportsAsync([pendingExport]);

        await using (var update = NewContext())
        {
            var tracked = await update.PendingExports.Include(pe => pe.AttributeValueChanges).SingleAsync(pe => pe.Id == pendingExport.Id);
            var change = tracked.AttributeValueChanges.Single();
            change.Status = PendingExportAttributeChangeStatus.ExportedPendingConfirmation;
            change.ExportAttemptCount = 1;
            change.AwaitsInitialPassword = true;
            tracked.Status = PendingExportStatus.Exported;
            await new PostgresDataRepository(update).Sync.UpdatePendingExportsAsync([tracked]);
        }

        var stored = await ReadFlagsAsync(pendingExport.Id);
        Assert.That(stored.Values.Single(), Is.True);
    }

    /// <summary>
    /// A delivery waits for an export to the account while it is in flight, and completes a held enable once it sees the
    /// hold (#2173, #2158). So the hold must already be readable at the moment the export stops being in flight: written
    /// after the Pending Export left Executing, it leaves a moment in which a delivery sees neither, sets the password
    /// without the enable, and the account waits a schedule cycle for it. Probed from another connection, exactly as the
    /// Password Delivery Service reads it, just before the Pending Export's own row is written.
    /// </summary>
    [Test]
    public async Task UpdatePendingExportAsync_WritesTheHeldChangeBeforeThePendingExportLeavesExecutingAsync()
    {
        var s = await SeedAsync();
        var pendingExport = NewPendingExport(s.SystemId, s.CsoIds[0], s.AttributeId);
        pendingExport.Status = PendingExportStatus.Executing;
        pendingExport.LastAttemptedAt = DateTime.UtcNow;
        await using (var write = NewContext())
            await new PostgresDataRepository(write).Sync.CreatePendingExportsAsync([pendingExport]);

        var seenAsTheExportLeftExecuting = new List<AccountEnablementFacts>();
        var probe = new BeforeStatementInterceptor("UPDATE \"PendingExports\"", async () =>
        {
            await using var read = NewContext();
            seenAsTheExportLeftExecuting.Add(await new PostgresDataRepository(read).Sync.GetAccountEnablementFactsAsync(
                s.SystemId, Guid.NewGuid(), s.CsoIds[0], DateTime.UtcNow.AddHours(-1)));
        });

        await using (var update = NewContext(probe))
        {
            var tracked = await update.PendingExports.Include(pe => pe.AttributeValueChanges).SingleAsync(pe => pe.Id == pendingExport.Id);
            var change = tracked.AttributeValueChanges.Single();
            change.Status = PendingExportAttributeChangeStatus.ExportedPendingConfirmation;
            change.ExportAttemptCount = 1;
            change.AwaitsInitialPassword = true;
            tracked.Status = PendingExportStatus.Exported;
            await new PostgresDataRepository(update).Sync.UpdatePendingExportAsync(tracked);
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(seenAsTheExportLeftExecuting, Is.Not.Empty, "precondition: the Pending Export's row was written");
            Assert.That(seenAsTheExportLeftExecuting.Select(facts => facts.ExportInFlight), Is.All.True,
                "precondition: probed while the export was still in flight");
            Assert.That(seenAsTheExportLeftExecuting.Select(facts => facts.HoldsWithheldEnable), Is.All.True,
                "the hold is readable before the export stops being in flight");
        }
    }

    /// <summary>
    /// The confirming import clears the flag on a release through its own bulk update.
    /// </summary>
    [Test]
    public async Task UpdateUntrackedPendingExportsAsync_FlagClearedOnRelease_PersistsItAsync()
    {
        var s = await SeedAsync();
        var pendingExport = NewPendingExport(s.SystemId, s.CsoIds[0], s.AttributeId);
        pendingExport.Status = PendingExportStatus.Exported;
        pendingExport.AttributeValueChanges[0].Status = PendingExportAttributeChangeStatus.ExportedPendingConfirmation;
        pendingExport.AttributeValueChanges[0].AwaitsInitialPassword = true;
        await using (var write = NewContext())
            await new PostgresDataRepository(write).Sync.CreatePendingExportsAsync([pendingExport]);
        Assert.That((await ReadFlagsAsync(pendingExport.Id)).Values.Single(), Is.True, "precondition: the change is stored held");

        await using (var read = NewContext())
        {
            var untracked = await read.PendingExports.AsNoTracking().Include(pe => pe.AttributeValueChanges).SingleAsync(pe => pe.Id == pendingExport.Id);
            untracked.AttributeValueChanges.Single().Status = PendingExportAttributeChangeStatus.Pending;
            untracked.AttributeValueChanges.Single().AwaitsInitialPassword = false;
            untracked.Status = PendingExportStatus.Pending;
            await new PostgresDataRepository(read).Sync.UpdateUntrackedPendingExportsAsync([untracked]);
        }

        await using var verify = NewContext();
        var change = await verify.PendingExportAttributeValueChanges.AsNoTracking().SingleAsync(c => c.PendingExportId == pendingExport.Id);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(change.AwaitsInitialPassword, Is.False);
            Assert.That(change.Status, Is.EqualTo(PendingExportAttributeChangeStatus.Pending));
        }
    }

    /// <summary>
    /// A password is owed while it is Pending, Delivering or Parked, whatever its origin; Expired and Cancelled rows
    /// will never be delivered, and a row on another Connected System is owed to a different account.
    /// </summary>
    [Test]
    public async Task GetMetaverseObjectIdsOwedPasswordAsync_ByStatusAndSystem_ReturnsOnlyThoseStillOwedOnThisSystemAsync()
    {
        var s = await SeedAsync(metaverseObjectCount: 7);
        var otherSystemId = await SeedConnectedSystemAsync("Other Directory");
        var mvo = s.MvoIds;

        await using (var seed = NewContext())
        {
            seed.PendingPasswordChanges.AddRange(
                Password(mvo[0], s.SystemId, PendingPasswordChangeStatus.Pending, PendingPasswordChangeOrigin.Provisioned),
                Password(mvo[1], s.SystemId, PendingPasswordChangeStatus.Delivering, PendingPasswordChangeOrigin.Explicit),
                Password(mvo[2], s.SystemId, PendingPasswordChangeStatus.Parked, PendingPasswordChangeOrigin.Propagated),
                Password(mvo[3], s.SystemId, PendingPasswordChangeStatus.Expired, PendingPasswordChangeOrigin.Provisioned),
                Password(mvo[4], s.SystemId, PendingPasswordChangeStatus.Cancelled, PendingPasswordChangeOrigin.Provisioned),
                Password(mvo[5], otherSystemId, PendingPasswordChangeStatus.Pending, PendingPasswordChangeOrigin.Provisioned));
            await seed.SaveChangesAsync();
        }

        HashSet<Guid> owed;
        await using (var read = NewContext())
            owed = await new PostgresDataRepository(read).Sync.GetMetaverseObjectIdsOwedPasswordAsync(s.SystemId, mvo);

        Assert.That(owed, Is.EquivalentTo(new[] { mvo[0], mvo[1], mvo[2] }));
    }

    [Test]
    public async Task GetMetaverseObjectIdsOwedPasswordAsync_OwedObjectNotAsked_IsNotReturnedAsync()
    {
        var s = await SeedAsync(metaverseObjectCount: 2);
        await using (var seed = NewContext())
        {
            seed.PendingPasswordChanges.AddRange(
                Password(s.MvoIds[0], s.SystemId, PendingPasswordChangeStatus.Pending, PendingPasswordChangeOrigin.Provisioned),
                Password(s.MvoIds[1], s.SystemId, PendingPasswordChangeStatus.Pending, PendingPasswordChangeOrigin.Provisioned));
            await seed.SaveChangesAsync();
        }

        await using var read = NewContext();
        var owed = await new PostgresDataRepository(read).Sync.GetMetaverseObjectIdsOwedPasswordAsync(s.SystemId, [s.MvoIds[0]]);

        Assert.That(owed, Is.EquivalentTo(new[] { s.MvoIds[0] }));
    }

    /// <summary>
    /// Drift Detection's prefetch: only the asked-for objects whose Pending Export carries a flagged change still
    /// awaiting its confirming import. A flagged change that has left that state is not a hold.
    /// </summary>
    [Test]
    public async Task GetConnectedSystemObjectIdsAwaitingInitialPasswordAsync_MixedObjects_ReturnsOnlyThoseWithAHeldChangeAsync()
    {
        var s = await SeedAsync(connectedSystemObjectCount: 5);
        var held = NewPendingExport(s.SystemId, s.CsoIds[0], s.AttributeId);
        held.AttributeValueChanges.Add(HeldChange(s.AttributeId));
        var unflagged = NewPendingExport(s.SystemId, s.CsoIds[1], s.AttributeId);
        var flaggedButNoLongerAwaitingConfirmation = NewPendingExport(s.SystemId, s.CsoIds[2], s.AttributeId);
        flaggedButNoLongerAwaitingConfirmation.AttributeValueChanges[0].AwaitsInitialPassword = true;
        flaggedButNoLongerAwaitingConfirmation.AttributeValueChanges[0].Status = PendingExportAttributeChangeStatus.ExportedNotConfirmed;
        var heldButNotAsked = NewPendingExport(s.SystemId, s.CsoIds[4], s.AttributeId);
        heldButNotAsked.AttributeValueChanges.Add(HeldChange(s.AttributeId));
        await using (var write = NewContext())
            await new PostgresDataRepository(write).Sync.CreatePendingExportsAsync([held, unflagged, flaggedButNoLongerAwaitingConfirmation, heldButNotAsked]);

        await using var read = NewContext();
        var found = await new PostgresDataRepository(read).Sync.GetConnectedSystemObjectIdsAwaitingInitialPasswordAsync(
            s.SystemId, [s.CsoIds[0], s.CsoIds[1], s.CsoIds[2], s.CsoIds[3]]);

        Assert.That(found, Is.EquivalentTo(new[] { s.CsoIds[0] }));
    }

    /// <summary>
    /// The delivery's one read at the attempt (#2064): whether the account holds a withheld enable, and whether its
    /// queued change is marked as having its enabled state written by an export. Each is answered on its own, and a
    /// change that no longer exists reads as unmarked.
    /// </summary>
    [Test]
    public async Task GetAccountEnablementFactsAsync_ReadsTheHoldAndTheMarkAsTheyStandAsync()
    {
        var s = await SeedAsync(connectedSystemObjectCount: 2, metaverseObjectCount: 2);
        var held = NewPendingExport(s.SystemId, s.CsoIds[0], s.AttributeId);
        held.AttributeValueChanges.Add(HeldChange(s.AttributeId));
        var marked = Password(s.MvoIds[1], s.SystemId, PendingPasswordChangeStatus.Delivering, PendingPasswordChangeOrigin.Provisioned);
        marked.ConnectedSystemObjectId = s.CsoIds[1];
        marked.EnabledStateSetByAttributeFlow = true;
        var unmarked = Password(s.MvoIds[0], s.SystemId, PendingPasswordChangeStatus.Delivering, PendingPasswordChangeOrigin.Provisioned);
        unmarked.ConnectedSystemObjectId = s.CsoIds[0];
        await using (var write = NewContext())
        {
            var repository = new PostgresDataRepository(write).Sync;
            await repository.CreatePendingExportsAsync([held]);
            await repository.QueuePasswordChangesAsync([marked, unmarked]);
        }

        await using var read = NewContext();
        var sync = new PostgresDataRepository(read).Sync;
        var since = DateTime.UtcNow.AddHours(-1);
        var heldAndUnmarked = await sync.GetAccountEnablementFactsAsync(s.SystemId, unmarked.Id, s.CsoIds[0], since);
        var markedAndNotHeld = await sync.GetAccountEnablementFactsAsync(s.SystemId, marked.Id, s.CsoIds[1], since);
        var changeGone = await sync.GetAccountEnablementFactsAsync(s.SystemId, Guid.NewGuid(), s.CsoIds[1], since);
        var otherSystem = await sync.GetAccountEnablementFactsAsync(s.SystemId + 1000, unmarked.Id, s.CsoIds[0], since);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(heldAndUnmarked, Is.EqualTo(new AccountEnablementFacts(true, false, false, null)));
            Assert.That(markedAndNotHeld, Is.EqualTo(new AccountEnablementFacts(false, true, false, null)));
            Assert.That(changeGone, Is.EqualTo(new AccountEnablementFacts(false, false, false, null)));
            Assert.That(otherSystem.HoldsWithheldEnable, Is.False, "a hold on another Connected System is not this account's");
        }
    }

    /// <summary>
    /// The export facts (#2173): whether an export to the account is being sent right now. Only a Pending Export in
    /// Executing counts; one waiting to be sent, or sent and awaiting confirmation, has nothing in flight. One marked
    /// Executing before the given time has been there longer than any batch takes: it is reported as stranded, by id,
    /// rather than as in flight.
    /// </summary>
    [Test]
    public async Task GetAccountEnablementFactsAsync_ReadsWhetherAnExportToTheAccountIsInFlightAsync()
    {
        var s = await SeedAsync(connectedSystemObjectCount: 4, metaverseObjectCount: 1);
        var now = DateTime.UtcNow;
        var executing = NewPendingExport(s.SystemId, s.CsoIds[0], s.AttributeId);
        executing.Status = PendingExportStatus.Executing;
        executing.LastAttemptedAt = now.AddMinutes(-5);
        var waiting = NewPendingExport(s.SystemId, s.CsoIds[1], s.AttributeId);
        var exported = NewPendingExport(s.SystemId, s.CsoIds[2], s.AttributeId);
        exported.Status = PendingExportStatus.Exported;
        exported.LastAttemptedAt = now.AddMinutes(-5);
        var stranded = NewPendingExport(s.SystemId, s.CsoIds[3], s.AttributeId);
        stranded.Status = PendingExportStatus.Executing;
        stranded.LastAttemptedAt = now.AddHours(-3);
        await using (var write = NewContext())
            await new PostgresDataRepository(write).Sync.CreatePendingExportsAsync([executing, waiting, exported, stranded]);

        await using var read = NewContext();
        var sync = new PostgresDataRepository(read).Sync;
        var since = now.AddHours(-1);
        var inFlight = await sync.GetAccountEnablementFactsAsync(s.SystemId, Guid.NewGuid(), s.CsoIds[0], since);
        var notYetSent = await sync.GetAccountEnablementFactsAsync(s.SystemId, Guid.NewGuid(), s.CsoIds[1], since);
        var alreadySent = await sync.GetAccountEnablementFactsAsync(s.SystemId, Guid.NewGuid(), s.CsoIds[2], since);
        var strandedFacts = await sync.GetAccountEnablementFactsAsync(s.SystemId, Guid.NewGuid(), s.CsoIds[3], since);
        var otherSystem = await sync.GetAccountEnablementFactsAsync(s.SystemId + 1000, Guid.NewGuid(), s.CsoIds[0], since);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(inFlight.ExportInFlight, Is.True);
            Assert.That(inFlight.StrandedExportId, Is.Null);
            Assert.That(notYetSent.ExportInFlight, Is.False);
            Assert.That(alreadySent.ExportInFlight, Is.False);
            Assert.That(strandedFacts.ExportInFlight, Is.False, "Executing for longer than any batch takes");
            Assert.That(strandedFacts.StrandedExportId, Is.EqualTo(stranded.Id));
            Assert.That(otherSystem.ExportInFlight, Is.False, "an export on another Connected System is not this account's");
        }
    }

    /// <summary>
    /// Whether the export in flight is the account's own Create (#2160): a change owing no first password waits for it,
    /// because the Create stages the account's Initial Password once its batch has come back. Any other export in
    /// flight, and a Create no longer in flight, is not.
    /// </summary>
    [Test]
    public async Task GetAccountEnablementFactsAsync_ReadsWhetherTheExportInFlightIsTheAccountsCreateAsync()
    {
        var s = await SeedAsync(connectedSystemObjectCount: 3, metaverseObjectCount: 1);
        var now = DateTime.UtcNow;
        var createInFlight = NewPendingExport(s.SystemId, s.CsoIds[0], s.AttributeId);
        createInFlight.ChangeType = PendingExportChangeType.Create;
        createInFlight.Status = PendingExportStatus.Executing;
        createInFlight.LastAttemptedAt = now.AddMinutes(-1);
        var updateInFlight = NewPendingExport(s.SystemId, s.CsoIds[1], s.AttributeId);
        updateInFlight.Status = PendingExportStatus.Executing;
        updateInFlight.LastAttemptedAt = now.AddMinutes(-1);
        var createSent = NewPendingExport(s.SystemId, s.CsoIds[2], s.AttributeId);
        createSent.ChangeType = PendingExportChangeType.Create;
        createSent.Status = PendingExportStatus.Exported;
        createSent.LastAttemptedAt = now.AddMinutes(-1);
        await using (var write = NewContext())
            await new PostgresDataRepository(write).Sync.CreatePendingExportsAsync([createInFlight, updateInFlight, createSent]);

        await using var read = NewContext();
        var sync = new PostgresDataRepository(read).Sync;
        var since = now.AddHours(-1);
        var creating = await sync.GetAccountEnablementFactsAsync(s.SystemId, Guid.NewGuid(), s.CsoIds[0], since);
        var updating = await sync.GetAccountEnablementFactsAsync(s.SystemId, Guid.NewGuid(), s.CsoIds[1], since);
        var created = await sync.GetAccountEnablementFactsAsync(s.SystemId, Guid.NewGuid(), s.CsoIds[2], since);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(creating.ExportInFlight, Is.True);
            Assert.That(creating.CreateInFlight, Is.True);
            Assert.That(updating.ExportInFlight, Is.True);
            Assert.That(updating.CreateInFlight, Is.False);
            Assert.That(created.CreateInFlight, Is.False);
        }
    }

    /// <summary>
    /// The import's sweep of the holds it did not reconcile: every held change on the Connected System, one row each,
    /// with its Pending Export, its object, and the Metaverse Object that object is joined to (null when it is joined
    /// to none). Not an unflagged change, not a flagged one that has left ExportedPendingConfirmation, and nothing on
    /// another Connected System.
    /// </summary>
    [Test]
    public async Task GetInitialPasswordHoldSummariesAsync_MixedChangesAcrossSystems_ReturnsEveryHeldChangeOnTheSystemAsync()
    {
        var s = await SeedAsync(connectedSystemObjectCount: 3, metaverseObjectCount: 1);
        var other = await SeedAsync(systemName: "Other Directory");
        await JoinAsync(s.CsoIds[0], s.MvoIds[0]);

        var joined = NewPendingExport(s.SystemId, s.CsoIds[0], s.AttributeId);
        var joinedHeld = HeldChange(s.AttributeId);
        joined.AttributeValueChanges.Add(joinedHeld);
        var unjoined = NewPendingExport(s.SystemId, s.CsoIds[1], s.AttributeId);
        var unjoinedHeld = HeldChange(s.AttributeId);
        unjoined.AttributeValueChanges.Add(unjoinedHeld);
        var noLongerAwaitingConfirmation = NewPendingExport(s.SystemId, s.CsoIds[2], s.AttributeId);
        noLongerAwaitingConfirmation.AttributeValueChanges[0].AwaitsInitialPassword = true;
        noLongerAwaitingConfirmation.AttributeValueChanges[0].Status = PendingExportAttributeChangeStatus.ExportedNotConfirmed;
        var onAnotherSystem = NewPendingExport(other.SystemId, other.CsoIds[0], other.AttributeId);
        onAnotherSystem.AttributeValueChanges.Add(HeldChange(other.AttributeId));
        await using (var write = NewContext())
            await new PostgresDataRepository(write).Sync.CreatePendingExportsAsync([joined, unjoined, noLongerAwaitingConfirmation, onAnotherSystem]);

        List<JIM.Models.Transactional.DTOs.InitialPasswordHoldSummary> holds;
        await using (var read = NewContext())
            holds = await new PostgresDataRepository(read).Sync.GetInitialPasswordHoldSummariesAsync(s.SystemId);

        Assert.That(holds.Select(h => (h.AttributeValueChangeId, h.PendingExportId, h.ConnectedSystemObjectId, h.MetaverseObjectId)),
            Is.EquivalentTo(new (Guid, Guid, Guid, Guid?)[]
            {
                (joinedHeld.Id, joined.Id, s.CsoIds[0], s.MvoIds[0]),
                (unjoinedHeld.Id, unjoined.Id, s.CsoIds[1], null)
            }));
    }

    [Test]
    public async Task GetInitialPasswordHoldSummariesAsync_NothingHeld_ReturnsNothingAsync()
    {
        var s = await SeedAsync();
        await using (var write = NewContext())
            await new PostgresDataRepository(write).Sync.CreatePendingExportsAsync([NewPendingExport(s.SystemId, s.CsoIds[0], s.AttributeId)]);

        await using var read = NewContext();
        var holds = await new PostgresDataRepository(read).Sync.GetInitialPasswordHoldSummariesAsync(s.SystemId);

        Assert.That(holds, Is.Empty);
    }

    private async Task JoinAsync(Guid connectedSystemObjectId, Guid metaverseObjectId)
    {
        await using var join = NewContext();
        await join.Database.ExecuteSqlInterpolatedAsync(
            $"""UPDATE "ConnectedSystemObjects" SET "MetaverseObjectId" = {metaverseObjectId} WHERE "Id" = {connectedSystemObjectId}""");
    }

    private static PendingExportAttributeValueChange HeldChange(int attributeId)
    {
        var change = NewChange(attributeId, awaitsInitialPassword: true);
        change.Status = PendingExportAttributeChangeStatus.ExportedPendingConfirmation;
        change.ExportAttemptCount = 1;
        return change;
    }

    private async Task<Dictionary<Guid, bool>> ReadFlagsAsync(Guid pendingExportId)
    {
        await using var verify = NewContext();
        return await verify.PendingExportAttributeValueChanges.AsNoTracking()
            .Where(c => c.PendingExportId == pendingExportId)
            .ToDictionaryAsync(c => c.Id, c => c.AwaitsInitialPassword);
    }

    private static PendingPasswordChange Password(Guid mvoId, int systemId, PendingPasswordChangeStatus status, PendingPasswordChangeOrigin origin) => new()
    {
        Id = Guid.NewGuid(),
        MetaverseObjectId = mvoId,
        ConnectedSystemId = systemId,
        Status = status,
        Origin = origin,
        CreatedAt = DateTime.UtcNow,
        ExpiresAt = DateTime.UtcNow.AddDays(1),
        ActivityId = Guid.NewGuid()
    };

    private static PendingExport NewPendingExport(int systemId, Guid csoId, int attributeId) => new()
    {
        Id = Guid.NewGuid(),
        ConnectedSystemId = systemId,
        ConnectedSystemObjectId = csoId,
        ChangeType = PendingExportChangeType.Update,
        Status = PendingExportStatus.Pending,
        CreatedAt = DateTime.UtcNow,
        AttributeValueChanges = [NewChange(attributeId, awaitsInitialPassword: false)]
    };

    private static PendingExportAttributeValueChange NewChange(int attributeId, bool awaitsInitialPassword) => new()
    {
        Id = Guid.NewGuid(),
        AttributeId = attributeId,
        ChangeType = PendingExportAttributeChangeType.Update,
        IntValue = 512,
        Status = PendingExportAttributeChangeStatus.Pending,
        AwaitsInitialPassword = awaitsInitialPassword
    };

    /// <summary>
    /// Runs an action just before any statement whose text contains a given fragment, on its way to the database.
    /// </summary>
    private sealed class BeforeStatementInterceptor(string commandTextFragment, Func<Task> before) : DbCommandInterceptor
    {
        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            System.Data.Common.DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains(commandTextFragment, StringComparison.Ordinal))
                await before();
            return await base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    private sealed record Seeded(int SystemId, int AttributeId, List<Guid> CsoIds, List<Guid> MvoIds);

    private async Task<Seeded> SeedAsync(int connectedSystemObjectCount = 1, int metaverseObjectCount = 1, string systemName = "Panoply AD")
    {
        await using var seed = NewContext();

        var connectorDefinition = new ConnectorDefinition { Name = $"{systemName} Connector", BuiltIn = true };
        var system = new ConnectedSystem { Name = systemName, ConnectorDefinition = connectorDefinition };
        var csType = new ConnectedSystemObjectType { Name = "user", ConnectedSystem = system, Selected = true };
        var attribute = new ConnectedSystemObjectTypeAttribute
        {
            Name = "userAccountControl", ConnectedSystemObjectType = csType, Type = AttributeDataType.Number,
            AttributePlurality = AttributePlurality.SingleValued, Selected = true
        };
        csType.Attributes.Add(attribute);
        var mvType = new MetaverseObjectType { Name = $"Person ({systemName})", PluralName = $"People ({systemName})" };
        var csos = Enumerable.Range(0, connectedSystemObjectCount)
            .Select(_ => new ConnectedSystemObject { Type = csType, ConnectedSystem = system, Status = ConnectedSystemObjectStatus.Normal })
            .ToList();
        var mvos = Enumerable.Range(0, metaverseObjectCount)
            .Select(_ => new MetaverseObject { Id = Guid.NewGuid(), Type = mvType, Created = DateTime.UtcNow })
            .ToList();

        seed.AddRange(connectorDefinition, system, csType, mvType);
        seed.AddRange(csos);
        seed.AddRange(mvos);
        await seed.SaveChangesAsync();

        return new Seeded(system.Id, attribute.Id, csos.Select(c => c.Id).ToList(), mvos.Select(m => m.Id).ToList());
    }

    private async Task<int> SeedConnectedSystemAsync(string name)
    {
        await using var seed = NewContext();
        var connectorDefinition = new ConnectorDefinition { Name = $"{name} Connector", BuiltIn = true };
        var system = new ConnectedSystem { Name = name, ConnectorDefinition = connectorDefinition };
        seed.AddRange(connectorDefinition, system);
        await seed.SaveChangesAsync();
        return system.Id;
    }
}
