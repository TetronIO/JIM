// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Text;
using System.Text.RegularExpressions;
using JIM.Application;
using JIM.Application.Servers;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Staging;
using JIM.Models.Tasking;
using JIM.Models.Transactional;
using JIM.PostgresData;
using JIM.TestSupport;
using JIM.Worker.Processors;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.Workflows;

/// <summary>
/// What a confirming Full Import's Pending Export reconciliation leaves behind, end to end on real PostgreSQL (#2908): which
/// changes it confirmed, which Pending Exports it deleted, kept or updated, and the execution items, outcomes, causal edges,
/// counters and Activity it recorded. The fixture mixes every shape reconciliation judges: a Pending Export confirmed in
/// full, one confirmed in part, one not confirmed at all, a Create, an Update, a Delete, reference values (single and
/// multi-valued, resolved and unresolved), multi-valued text, a number, an object no attribute changed (so reconciliation
/// raises its own execution item), an object the import found deleted, and a Pending Export nothing was exported for.
/// <para>
/// The snapshot is a characterisation: it was taken from reconciliation as it ran before #2908 (a reload of every Connected
/// System Object after the save phase) and must not change when reconciliation judges the objects the import already holds.
/// Opt-in via the <c>JIM_TEST_RESET_*</c> environment variables; <c>SetUp</c> TRUNCATEs every table.
/// </para>
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
public class ConfirmingImportReconciliationDatabaseTests
{
    private string _connectionString = null!;
    private int _systemId;
    private readonly Dictionary<string, int> _attributeIds = [];
    private readonly Dictionary<Guid, string> _names = [];

    // Users.
    private static readonly Guid Ada = Guid.Parse("00000000-0000-0000-0000-00000000a001");
    private static readonly Guid Grace = Guid.Parse("00000000-0000-0000-0000-00000000a002");
    private static readonly Guid Katherine = Guid.Parse("00000000-0000-0000-0000-00000000a003");
    private static readonly Guid Dorothy = Guid.Parse("00000000-0000-0000-0000-00000000a004");
    private static readonly Guid Mary = Guid.Parse("00000000-0000-0000-0000-00000000a005");
    private static readonly Guid Hedy = Guid.Parse("00000000-0000-0000-0000-00000000a006");
    private static readonly Guid Joan = Guid.Parse("00000000-0000-0000-0000-00000000a007");
    private static readonly Guid Rosalind = Guid.Parse("00000000-0000-0000-0000-00000000a008");

    // Groups.
    private static readonly Guid Alpha = Guid.Parse("00000000-0000-0000-0000-00000000b001");
    private static readonly Guid Beta = Guid.Parse("00000000-0000-0000-0000-00000000b002");

    // Pending Exports.
    private static readonly Guid AdaExport = Guid.Parse("00000000-0000-0000-0000-00000000c001");
    private static readonly Guid GraceExport = Guid.Parse("00000000-0000-0000-0000-00000000c002");
    private static readonly Guid DorothyCreate = Guid.Parse("00000000-0000-0000-0000-00000000c004");
    private static readonly Guid MaryExport = Guid.Parse("00000000-0000-0000-0000-00000000c005");
    private static readonly Guid HedyExport = Guid.Parse("00000000-0000-0000-0000-00000000c006");
    private static readonly Guid JoanExport = Guid.Parse("00000000-0000-0000-0000-00000000c007");
    private static readonly Guid RosalindDelete = Guid.Parse("00000000-0000-0000-0000-00000000c008");
    private static readonly Guid KatherineQueued = Guid.Parse("00000000-0000-0000-0000-00000000c003");
    private static readonly Guid AlphaExport = Guid.Parse("00000000-0000-0000-0000-00000000c101");
    private static readonly Guid BetaExport = Guid.Parse("00000000-0000-0000-0000-00000000c102");

    private JimDbContext NewContext(bool trackAll = false)
    {
        var builder = new DbContextOptionsBuilder<JimDbContext>()
            .UseNpgsql(_connectionString)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning));
        // The Worker tracks by default (src/JIM.Worker/Program.cs); an import run must too.
        if (trackAll)
            builder.UseQueryTrackingBehavior(QueryTrackingBehavior.TrackAll);
        return new JimDbContext(builder.Options);
    }

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        var dbName = Environment.GetEnvironmentVariable("JIM_TEST_RESET_DB");
        if (string.IsNullOrEmpty(dbName))
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL confirming import reconciliation tests.");

        var host = Environment.GetEnvironmentVariable("JIM_TEST_RESET_HOST") ?? "localhost";
        var user = Environment.GetEnvironmentVariable("JIM_TEST_RESET_USER") ?? "postgres";
        var pass = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PASSWORD") ?? "postgres";
        var port = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PORT") ?? "5432";
        _connectionString = $"Host={host};Port={port};Database={dbName};Username={user};Password={pass}";

        TestUtilities.SetEnvironmentVariables();
        using var ctx = NewContext();
        ctx.Database.Migrate();
    }

    [SetUp]
    public async Task SetUpAsync()
    {
        await PostgresTestDatabase.ResetAsync(_connectionString);
        _attributeIds.Clear();
        _names.Clear();
        await SeedConfigurationAsync();
        await SeedObjectsAsync();
        await SeedPendingExportsAsync();
    }

    /// <param name="importBatchSize">The save phase's batch size: the default, which saves every object in one batch, or
    /// three, so reconciliation runs once per batch as the save crosses four (#2908).</param>
    [TestCase(2000)]
    [TestCase(3)]
    public async Task FullImport_ConfirmingAMixOfExports_LeavesExactlyTheCharacterisedOutcomeAsync(int importBatchSize)
    {
        var (activity, _) = await RunConfirmingFullImportAsync(importBatchSize);

        var snapshot = await SnapshotAsync(activity.Id);

        Assert.That(snapshot, Is.EqualTo(ExpectedSnapshot),
            "Reconciliation's output changed. Every line is something an administrator or a later run relies on: which " +
            "changes were confirmed, which Pending Exports were kept, and what the Activity records. Actual:\n" + snapshot);
    }

    /// <summary>
    /// An object no attribute changed has no execution item from the import, so reconciliation raises its own to record the
    /// confirmation (Hedy) or the miss (Joan). It must name the object's type as every other item does: the Activity's
    /// breakdown by Object Type counts items by it, and left out it counted 5 of the run's 7 users.
    /// </summary>
    [Test]
    public async Task FullImport_ExportJudgedOnAnObjectTheImportDidNotChange_SnapshotsItsObjectTypeAsync()
    {
        var (activity, _) = await RunConfirmingFullImportAsync();

        await using var ctx = NewContext();
        var snapshots = await ctx.ActivityRunProfileExecutionItems.AsNoTracking()
            .Where(i => i.ActivityId == activity.Id && (i.ConnectedSystemObjectId == Hedy || i.ConnectedSystemObjectId == Joan))
            .Select(i => i.ObjectTypeSnapshot)
            .ToListAsync();
        var usersCounted = await ctx.ActivityStatCounters.AsNoTracking()
            .Where(c => c.ActivityId == activity.Id && c.Dimension == ActivityStatDimension.ObjectTypeName && c.Key == "user")
            .Select(c => c.Count)
            .SingleAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(snapshots, Is.EqualTo(new[] { "user", "user" }));
            Assert.That(usersCounted, Is.EqualTo(7), "ada, grace, dorothy, mary, zelda, hedy and joan");
        }
    }

    /// <summary>
    /// The save phase's objects are reconciled as each batch is saved, from the values the import holds for them, never
    /// reloaded (#2908): only Beta is read back, because it held an outstanding reference the run's cross-batch fix-up
    /// resolved after it was loaded. Nothing is probed in the database: Alpha's member changes are answered from the
    /// members the import holds. And the outcomes merged onto each batch's execution items are written with that batch,
    /// not all at the end: the batches of three hold Ada and Grace, then Dorothy, then Alpha and Beta.
    /// </summary>
    [Test]
    public async Task FullImport_ConfirmingAcrossSaveBatches_JudgesHeldObjectsWithoutReloadingAndWritesOutcomesPerBatchAsync()
    {
        var (_, recorder) = await RunConfirmingFullImportAsync(importBatchSize: 3);

        var calls = recorder.Calls.ToList();
        var reloaded = calls.Where(c => c.Method == nameof(ISyncRepository.GetConnectedSystemObjectsCoreByIdsNoTrackingAsync))
            .SelectMany(c => c.Ids[1]!).ToList();
        var probes = calls.Count(c => c.Method == nameof(ISyncRepository.GetReconciliationProbeValuesAsync));
        var outcomeWrites = calls.Where(c => c.Method == nameof(ISyncRepository.BulkUpdateRpeiOutcomesAsync))
            .Select(c => c.CollectionCounts[0]).ToList();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(reloaded, Is.EqualTo(new[] { Beta }), "only Beta is read back for reconciliation");
            Assert.That(probes, Is.Zero, "Alpha's member changes are answered from the values the import holds");
            Assert.That(outcomeWrites, Is.EqualTo(new int?[] { 2, 1, 2 }), "one write per save batch with merged outcomes");
        }
    }

    /// <summary>
    /// A save batch that fails after earlier batches were reconciled must not leave a Pending Export deleted with nothing
    /// recording why (#2908): each confirmation an earlier batch made is on an execution item, with the causal edge from its
    /// Pending Export, whatever happens to the run afterwards. Here the third of four update batches fails as its items are
    /// written; Hedy's Pending Export, confirmed by the second batch on an item reconciliation raised itself, is the one at risk.
    /// </summary>
    [Test]
    public async Task FullImport_LaterSaveBatchFails_EveryPendingExportReconciliationDeletedHasItsExecutionItemAsync()
    {
        var (activity, _, failure) = await RunImportAsync(ConnectedSystemRunType.FullImport, DirectoryPages(), importBatchSize: 3,
            arrange: (recorder, _) => recorder.FailWhen = (method, args) =>
                method == nameof(ISyncRepository.BulkInsertRpeisAsync) && WritesItemFor(args, Alpha)
                    ? new InvalidOperationException("Simulated failure writing the third update batch's execution items.")
                    : null);

        Assert.That(failure, Is.TypeOf<InvalidOperationException>(), "precondition: the run fails at the third update batch");
        await AssertEveryDeletedPendingExportHasItsExecutionItemAsync(activity.Id);
    }

    /// <summary>
    /// The same for a failure in reconciling a later batch itself: the third batch's reload (Beta's) fails as a probe that
    /// cannot be answered does, which fails the run by design.
    /// </summary>
    [Test]
    public async Task FullImport_LaterBatchReconciliationFails_EveryPendingExportReconciliationDeletedHasItsExecutionItemAsync()
    {
        var (activity, _, failure) = await RunImportAsync(ConnectedSystemRunType.FullImport, DirectoryPages(), importBatchSize: 3,
            arrange: (recorder, _) => recorder.FailWhen = (method, _) =>
                method == nameof(ISyncRepository.GetConnectedSystemObjectsCoreByIdsNoTrackingAsync)
                    ? new JIM.Models.Exceptions.ReconciliationProbeIncompleteException("Simulated: a probe that cannot be answered.")
                    : null);

        Assert.That(failure, Is.TypeOf<JIM.Models.Exceptions.ReconciliationProbeIncompleteException>(),
            "precondition: the run fails reconciling the third update batch");
        await AssertEveryDeletedPendingExportHasItsExecutionItemAsync(activity.Id);
    }

    /// <summary>
    /// And for a run cancelled while it saves: reconciliation stops at the next batch, and what the batches before it
    /// confirmed is recorded.
    /// </summary>
    [Test]
    public async Task FullImport_CancelledWhileSaving_EveryPendingExportReconciliationDeletedHasItsExecutionItemAsync()
    {
        var (activity, _, failure) = await RunImportAsync(ConnectedSystemRunType.FullImport, DirectoryPages(), importBatchSize: 3,
            arrange: (recorder, cancellation) => recorder.BeforeCall = (method, args) =>
            {
                if (method == nameof(ISyncRepository.BulkInsertRpeisAsync) && WritesItemFor(args, Alpha))
                    cancellation.Cancel();
            });

        Assert.That(failure, Is.Null);
        await AssertEveryDeletedPendingExportHasItsExecutionItemAsync(activity.Id);
    }

    /// <summary>
    /// A Delta Import's reconciliation, characterised as the Full Import's is above: Alpha's members compared in part
    /// (#2817), Grace read on both pages (#2074), Joan's delete reported under the other Object Type (#2049) and
    /// Rosalind's under her own. Taken from reconciliation as it ran before #2908 (with the Object Type correction), at
    /// the default batch size and with a batch per object.
    /// </summary>
    [TestCase(2000)]
    [TestCase(1)]
    public async Task DeltaImport_ConfirmingWithPartialLoadsSupersessionAndDeletes_LeavesExactlyTheCharacterisedOutcomeAsync(int importBatchSize)
    {
        var (activity, _, failure) = await RunImportAsync(ConnectedSystemRunType.DeltaImport, DeltaPages(), importBatchSize);
        Assert.That(failure, Is.Null);

        var snapshot = await SnapshotAsync(activity.Id);

        Assert.That(snapshot, Is.EqualTo(ExpectedDeltaSnapshot), "Reconciliation's output changed. Actual:\n" + snapshot);
    }

    /// <summary>
    /// Which objects reconciliation judges from the values the import holds, and which it reloads (#2908). Ada and Hedy were
    /// loaded whole by their page's hydration and matched there, so they are judged from memory. Alpha's members were loaded
    /// in part, Grace was compared on two pages, and Joan and Rosalind were deleted (Joan as reported under the other Object
    /// Type), so each is reloaded: none of them can be shown to hold what was saved.
    /// </summary>
    [Test]
    public async Task DeltaImport_ConfirmingWithPartialLoadsSupersessionAndDeletes_ReloadsExactlyTheObjectsNotHeldAsSavedAsync()
    {
        var (_, recorder, failure) = await RunImportAsync(ConnectedSystemRunType.DeltaImport, DeltaPages());
        Assert.That(failure, Is.Null);

        var reloaded = recorder.Calls.Where(c => c.Method == nameof(ISyncRepository.GetConnectedSystemObjectsCoreByIdsNoTrackingAsync))
            .SelectMany(c => c.Ids[1]!).Select(id => _names[id]).ToList();

        Assert.That(reloaded, Is.EquivalentTo(new[] { "alpha", "grace", "joan", "rosalind" }));
    }

    private static bool WritesItemFor(object?[] args, Guid connectedSystemObjectId) =>
        args.Length > 0 && args[0] is IEnumerable<ActivityRunProfileExecutionItem> items && items.Any(i => i.ConnectedSystemObjectId == connectedSystemObjectId);

    /// <summary>The Connected System Object each seeded Pending Export is for.</summary>
    private static readonly Dictionary<Guid, Guid> SeededExports = new()
    {
        [AdaExport] = Ada, [GraceExport] = Grace, [KatherineQueued] = Katherine, [DorothyCreate] = Dorothy, [MaryExport] = Mary,
        [HedyExport] = Hedy, [JoanExport] = Joan, [RosalindDelete] = Rosalind, [AlphaExport] = Alpha, [BetaExport] = Beta
    };

    /// <summary>
    /// Every Pending Export the run deleted on confirming it has an execution item of the run carrying the causal edge from
    /// it. Mary's is left out: deletion detection removes it, and records her deletion instead.
    /// </summary>
    private async Task AssertEveryDeletedPendingExportHasItsExecutionItemAsync(Guid activityId)
    {
        await using var ctx = NewContext();
        var remaining = await ctx.PendingExports.AsNoTracking().Select(p => p.Id).ToListAsync();
        var itemIds = await ctx.ActivityRunProfileExecutionItems.AsNoTracking().Where(i => i.ActivityId == activityId).Select(i => i.Id).ToListAsync();
        var confirmedExports = await ctx.CausalEdges.AsNoTracking()
            .Where(e => itemIds.Contains(e.EffectRunProfileExecutionItemId) && e.EdgeType == CausalEdgeType.ExportCausedImportConfirmation)
            .Select(e => e.CausePendingExportId)
            .ToListAsync();
        var deleted = SeededExports.Keys.Except(remaining).Where(id => id != MaryExport).ToList();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(deleted, Does.Contain(HedyExport), "precondition: an earlier batch confirmed Hedy's export on an item of its own");
            Assert.That(deleted.Where(id => !confirmedExports.Contains(id)).Select(id => _names[id]), Is.Empty,
                "deleted with no execution item recording the confirmation");
        }
    }

    /// <summary>
    /// Taken from reconciliation as it ran before #2908, with the Object Type correction. Joan's miss is merged onto the item
    /// her delete raised, as the import's own item for her.
    /// </summary>
    private const string ExpectedDeltaSnapshot = """
        Pending Exports:
          ada:Update on ada: Update ExportNotConfirmed errors=0
            user.mail#2 Update ExportedNotConfirmed attempts=1 lastImported=ada@other.example
            user.proxyAddresses#5 Remove ExportedNotConfirmed attempts=1 lastImported=smtp:ada@a.example, smtp:ada@b.example
          alpha:Update on alpha: Update ExportNotConfirmed errors=0
            group.member#17 Add ExportedNotConfirmed attempts=1 lastImported=(value not present)
          beta:Update on beta: Update Exported errors=0
            group.displayName#18 Update ExportedPendingConfirmation attempts=1 lastImported=-
            group.member#19 Add ExportedPendingConfirmation attempts=1 lastImported=-
          dorothy:Create on dorothy: Create Exported errors=0
            user.id#9 Add ExportedPendingConfirmation attempts=1 lastImported=-
            user.displayName#10 Add ExportedPendingConfirmation attempts=1 lastImported=-
          joan:Update on joan: Update ExportNotConfirmed errors=0
            user.mail#13 Update ExportedNotConfirmed attempts=2 lastImported=joan@old.example
          katherine:Update on katherine: Update Pending errors=0
            user.displayName#8 Update Pending attempts=1 lastImported=-
          mary:Update on mary: Update Exported errors=0
            user.displayName#11 Update ExportedPendingConfirmation attempts=1 lastImported=-
          rosalind:Delete on rosalind: Delete Exported errors=0
        Connected System Objects:
          ada: Normal
          alpha: Normal
          beta: Normal
          dorothy: PendingProvisioning
          grace: Normal
          hedy: Normal
          joan: Obsolete
          katherine: Normal
          mary: Normal
          rosalind: Obsolete
        Execution items:
          ada: Updated error=ExportNotConfirmed summary=CsoUpdated:1,ExportConfirmed:1,ExportFailed:1 snapshots=[ada|Ada Lovelace|user]
            message: We exported a change, but did not get confirmation of it when a confirming import was performed. Details: 2 attribute(s): mail, proxyAddresses. Will attempt to reassert the change on the next export run.
            outcome CsoUpdated ordinal=0 detail=9 parent=-
            outcome ExportConfirmed ordinal=1 detail=4 parent=-
            outcome ExportFailed ordinal=2 detail=2 parent=-
            edge ExportCausedImportConfirmation reason=NotSet export=ada:Update object=ada display=Ada King system=Target Directory effect=ExportConfirmed
          alpha: Updated error=ExportNotConfirmed summary=CsoUpdated:1,ExportConfirmed:1,ExportFailed:1 snapshots=[alpha|Team Alpha|group]
            message: We exported a change, but did not get confirmation of it when a confirming import was performed. Details: 1 attribute(s): member. Will attempt to reassert the change on the next export run.
            outcome CsoUpdated ordinal=0 detail=3 parent=-
            outcome ExportConfirmed ordinal=1 detail=3 parent=-
            outcome ExportFailed ordinal=2 detail=1 parent=-
            edge ExportCausedImportConfirmation reason=NotSet export=alpha:Update object=alpha display=Team Alpha system=Target Directory effect=ExportConfirmed
          grace: Updated error=NotSet summary=CsoUpdated:1,ExportConfirmed:1 snapshots=[grace|Grace Hopper|user]
            outcome CsoUpdated ordinal=0 detail=2 parent=-
            outcome ExportConfirmed ordinal=1 detail=1 parent=-
            edge ExportCausedImportConfirmation reason=NotSet export=grace:Update object=grace display=Grace Brewster Hopper system=Target Directory effect=ExportConfirmed
          hedy: Updated error=NotSet summary=ExportConfirmed:1 snapshots=[hedy|Hedy Lamarr|user]
            outcome ExportConfirmed ordinal=0 detail=1 parent=-
            edge ExportCausedImportConfirmation reason=NotSet export=hedy:Update object=hedy display=Hedy Lamarr system=Target Directory effect=ExportConfirmed
          joan: Deleted error=ExportNotConfirmed summary=DeletionDetected:1,ExportFailed:1 snapshots=[joan|Joan Clarke|user]
            message: We exported a change, but did not get confirmation of it when a confirming import was performed. Details: 1 attribute(s): mail. Will attempt to reassert the change on the next export run.
            outcome DeletionDetected ordinal=0 detail=- parent=-
            outcome ExportFailed ordinal=1 detail=1 parent=-
          rosalind: Deleted error=NotSet summary=DeletionDetected:1 snapshots=[rosalind|Rosalind Franklin|user]
            outcome DeletionDetected ordinal=0 detail=- parent=-
        Activity:
          confirmed=2 errors=0 added=0 updated=3 deleted=2 created=0 processed=7/7
          message: Import complete: 7 objects (0 created, 3 updated, 2 unchanged, 2 deleted)
        Stat counters:
          ObjectChangeType 2=4
          ObjectChangeType 3=2
          ObjectTypeName group=1
          ObjectTypeName user=5
          OutcomeType 1=3
          OutcomeType 3=2
          OutcomeType 4=4
          OutcomeType 5=3

        """;

    /// <summary>
    /// Taken from reconciliation as it ran before #2908, with the one correction made beside it: the execution items
    /// reconciliation raises for Hedy and Joan name their Object Type (see the test above). Names stand in for ids, which
    /// differ from run to run.
    /// </summary>
    private const string ExpectedSnapshot = """
        Pending Exports:
          ada:Update on ada: Update ExportNotConfirmed errors=0
            user.mail#2 Update ExportedNotConfirmed attempts=1 lastImported=ada@other.example
            user.proxyAddresses#5 Remove ExportedNotConfirmed attempts=1 lastImported=smtp:ada@a.example, smtp:ada@b.example
          alpha:Update on alpha: Update ExportNotConfirmed errors=0
            group.member#17 Add ExportedNotConfirmed attempts=1 lastImported=(value not present)
          joan:Update on joan: Update ExportNotConfirmed errors=0
            user.mail#13 Update ExportedNotConfirmed attempts=2 lastImported=joan@old.example
          katherine:Update on katherine: Update Pending errors=0
            user.displayName#8 Update Pending attempts=1 lastImported=-
          rosalind:Delete on rosalind: Delete Exported errors=0
        Connected System Objects:
          ada: Normal
          alpha: Normal
          beta: Normal
          dorothy: Normal
          grace: Normal
          hedy: Normal
          joan: Normal
          katherine: Normal
          mary: Obsolete
          rosalind: Normal
          zelda: Normal
        Execution items:
          ada: Updated error=ExportNotConfirmed summary=CsoUpdated:1,ExportConfirmed:1,ExportFailed:1 snapshots=[ada|Ada Lovelace|user]
            message: We exported a change, but did not get confirmation of it when a confirming import was performed. Details: 2 attribute(s): mail, proxyAddresses. Will attempt to reassert the change on the next export run.
            outcome CsoUpdated ordinal=0 detail=9 parent=-
            outcome ExportConfirmed ordinal=1 detail=4 parent=-
            outcome ExportFailed ordinal=2 detail=2 parent=-
            edge ExportCausedImportConfirmation reason=NotSet export=ada:Update object=ada display=Ada King system=Target Directory effect=ExportConfirmed
          alpha: Updated error=ExportNotConfirmed summary=CsoUpdated:1,ExportConfirmed:1,ExportFailed:1 snapshots=[alpha|Team Alpha|group]
            message: We exported a change, but did not get confirmation of it when a confirming import was performed. Details: 1 attribute(s): member. Will attempt to reassert the change on the next export run.
            outcome CsoUpdated ordinal=0 detail=3 parent=-
            outcome ExportConfirmed ordinal=1 detail=3 parent=-
            outcome ExportFailed ordinal=2 detail=1 parent=-
            edge ExportCausedImportConfirmation reason=NotSet export=alpha:Update object=alpha display=Team Alpha system=Target Directory effect=ExportConfirmed
          beta: Updated error=NotSet summary=CsoUpdated:1,ExportConfirmed:1 snapshots=[beta|Team B|group]
            outcome CsoUpdated ordinal=0 detail=2 parent=-
            outcome ExportConfirmed ordinal=1 detail=2 parent=-
            edge ExportCausedImportConfirmation reason=NotSet export=beta:Update object=beta display=Team Beta system=Target Directory effect=ExportConfirmed
          dorothy: Updated error=NotSet summary=CsoUpdated:1,ExportConfirmed:1 snapshots=[dorothy|Dorothy|user]
            outcome CsoUpdated ordinal=0 detail=2 parent=-
            outcome ExportConfirmed ordinal=1 detail=2 parent=-
            edge ExportCausedImportConfirmation reason=NotSet export=dorothy:Create object=dorothy display=Dorothy Vaughan system=Target Directory effect=ExportConfirmed
          grace: Updated error=NotSet summary=CsoUpdated:1,ExportConfirmed:1 snapshots=[grace|Grace Hopper|user]
            outcome CsoUpdated ordinal=0 detail=2 parent=-
            outcome ExportConfirmed ordinal=1 detail=1 parent=-
            edge ExportCausedImportConfirmation reason=NotSet export=grace:Update object=grace display=Grace Brewster Hopper system=Target Directory effect=ExportConfirmed
          hedy: Updated error=NotSet summary=ExportConfirmed:1 snapshots=[hedy|Hedy Lamarr|user]
            outcome ExportConfirmed ordinal=0 detail=1 parent=-
            edge ExportCausedImportConfirmation reason=NotSet export=hedy:Update object=hedy display=Hedy Lamarr system=Target Directory effect=ExportConfirmed
          joan: Updated error=ExportNotConfirmed summary=ExportFailed:1 snapshots=[joan|Joan Clarke|user]
            message: We exported a change, but did not get confirmation of it when a confirming import was performed. Details: 1 attribute(s): mail. Will attempt to reassert the change on the next export run.
            outcome ExportFailed ordinal=0 detail=1 parent=-
          mary: Deleted error=NotSet summary=DeletionDetected:1 snapshots=[mary|Mary Jackson|user]
            outcome DeletionDetected ordinal=0 detail=- parent=-
          zelda: Added error=NotSet summary=CsoAdded:1 snapshots=[zelda|Zelda Fitzgerald|user]
            outcome CsoAdded ordinal=0 detail=3 parent=-
        Activity:
          confirmed=4 errors=1 added=1 updated=5 deleted=1 created=0 processed=10/10
          message: Import complete: 10 objects (1 created, 5 updated, 4 unchanged, 1 error); 1 no longer found and marked as deleted
        Stat counters:
          ObjectChangeType 1=1
          ObjectChangeType 2=7
          ObjectChangeType 3=1
          ObjectTypeName group=2
          ObjectTypeName user=7
          ErrorType 9=1
          OutcomeType 0=1
          OutcomeType 1=5
          OutcomeType 3=1
          OutcomeType 4=6
          OutcomeType 5=3

        """;

    #region Harness

    /// <summary>
    /// One Connected System with User and Group types. A reconciliation page holds two objects, so the run crosses pages.
    /// </summary>
    private async Task SeedConfigurationAsync()
    {
        await using var seed = NewContext();

        seed.ServiceSettingItems.Add(new ServiceSetting
        {
            Key = Constants.SettingKeys.SyncPageSize, DisplayName = "Sync Page Size", Category = ServiceSettingCategory.Synchronisation,
            ValueType = ServiceSettingValueType.Integer, DefaultValue = "2", Value = "2"
        });

        // External IDs name one object across every Object Type, as a directory's do, so a Delta Import can apply a delete
        // reported under another type to the object JIM holds (#2049).
        var connectorDefinition = new ConnectorDefinition { Name = "JIM Test Connector", BuiltIn = true, ExternalIdsUniqueAcrossObjectTypes = true };
        var system = new ConnectedSystem { Name = "Target Directory", ConnectorDefinition = connectorDefinition };

        var user = new ConnectedSystemObjectType { Name = "user", ConnectedSystem = system, Selected = true };
        var group = new ConnectedSystemObjectType { Name = "group", ConnectedSystem = system, Selected = true };

        ConnectedSystemObjectTypeAttribute Attribute(ConnectedSystemObjectType type, string name, AttributeDataType dataType,
            AttributePlurality plurality = AttributePlurality.SingleValued, bool externalId = false, bool secondaryExternalId = false)
        {
            var attribute = new ConnectedSystemObjectTypeAttribute
            {
                Name = name, ConnectedSystemObjectType = type, Type = dataType, AttributePlurality = plurality,
                Selected = true, IsExternalId = externalId, IsSecondaryExternalId = secondaryExternalId
            };
            type.Attributes.Add(attribute);
            return attribute;
        }

        Attribute(user, "id", AttributeDataType.Text, externalId: true);
        Attribute(user, "uid", AttributeDataType.Text, secondaryExternalId: true);
        Attribute(user, "displayName", AttributeDataType.Text);
        Attribute(user, "mail", AttributeDataType.Text);
        Attribute(user, "employeeNumber", AttributeDataType.Number);
        Attribute(user, "proxyAddresses", AttributeDataType.Text, AttributePlurality.MultiValued);
        Attribute(user, "manager", AttributeDataType.Reference);
        Attribute(group, "id", AttributeDataType.Text, externalId: true);
        Attribute(group, "uid", AttributeDataType.Text, secondaryExternalId: true);
        Attribute(group, "displayName", AttributeDataType.Text);
        Attribute(group, "member", AttributeDataType.Reference, AttributePlurality.MultiValued);

        seed.AddRange(connectorDefinition, system, user, group);
        await seed.SaveChangesAsync();
        seed.ConnectedSystemRunProfiles.Add(new ConnectedSystemRunProfile
            { ConnectedSystemId = system.Id, Name = "Full Import", RunType = ConnectedSystemRunType.FullImport });
        seed.ConnectedSystemRunProfiles.Add(new ConnectedSystemRunProfile
            { ConnectedSystemId = system.Id, Name = "Delta Import", RunType = ConnectedSystemRunType.DeltaImport });
        await seed.SaveChangesAsync();

        _systemId = system.Id;
        foreach (var attribute in user.Attributes)
            _attributeIds[$"user.{attribute.Name}"] = attribute.Id;
        foreach (var attribute in group.Attributes)
            _attributeIds[$"group.{attribute.Name}"] = attribute.Id;
        _attributeIds["user"] = user.Id;
        _attributeIds["group"] = group.Id;
    }

    /// <summary>
    /// What JIM holds before the confirming import, written through the import's own create writer. Values are held as an
    /// import leaves them: a resolved reference keeps the string it was resolved from.
    /// </summary>
    private async Task SeedObjectsAsync()
    {
        await using var ctx = NewContext();
        var created = DateTime.UtcNow.AddHours(-2);

        ConnectedSystemObject Cso(Guid id, string type, string externalId, ConnectedSystemObjectStatus status = ConnectedSystemObjectStatus.Normal)
        {
            _names[id] = externalId;
            var cso = new ConnectedSystemObject
            {
                Id = id, ConnectedSystemId = _systemId, TypeId = _attributeIds[type], ExternalIdAttributeId = _attributeIds[$"{type}.id"],
                Status = status, Created = created
            };
            cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = _attributeIds[$"{type}.id"], StringValue = externalId });
            // References name objects by this, the Secondary External ID, as a directory's distinguished names do.
            cso.SecondaryExternalIdAttributeId = _attributeIds[$"{type}.uid"];
            cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = _attributeIds[$"{type}.uid"], StringValue = externalId });
            return cso;
        }

        void Text(ConnectedSystemObject cso, string attribute, string value) =>
            cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = _attributeIds[attribute], StringValue = value });

        void Reference(ConnectedSystemObject cso, string attribute, Guid? referenced, string value) =>
            cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
                { Id = Guid.NewGuid(), AttributeId = _attributeIds[attribute], ReferenceValueId = referenced, UnresolvedReferenceValue = value });

        var ada = Cso(Ada, "user", "ada");
        Text(ada, "user.displayName", "Ada Lovelace");
        Text(ada, "user.mail", "ada@old.example");
        ada.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = _attributeIds["user.employeeNumber"], IntValue = 100 });
        Text(ada, "user.proxyAddresses", "smtp:ada@a.example");
        Reference(ada, "user.manager", Grace, "grace");

        var grace = Cso(Grace, "user", "grace");
        Text(grace, "user.displayName", "Grace Hopper");

        var katherine = Cso(Katherine, "user", "katherine");
        Text(katherine, "user.displayName", "Katherine Johnson");

        // Provisioned by an export JIM has not yet seen confirmed.
        var dorothy = Cso(Dorothy, "user", "dorothy", ConnectedSystemObjectStatus.PendingProvisioning);
        Text(dorothy, "user.displayName", "Dorothy");

        // Gone from the directory: the import finds it deleted.
        var mary = Cso(Mary, "user", "mary");
        Text(mary, "user.displayName", "Mary Jackson");

        // Already holds what was exported (written optimistically at export), so the import changes nothing.
        var hedy = Cso(Hedy, "user", "hedy");
        Text(hedy, "user.displayName", "Hedy Lamarr");
        Text(hedy, "user.mail", "hedy@new.example");

        var joan = Cso(Joan, "user", "joan");
        Text(joan, "user.displayName", "Joan Clarke");
        Text(joan, "user.mail", "joan@old.example");

        var rosalind = Cso(Rosalind, "user", "rosalind");
        Text(rosalind, "user.displayName", "Rosalind Franklin");

        var alpha = Cso(Alpha, "group", "alpha");
        Text(alpha, "group.displayName", "Team Alpha");
        Reference(alpha, "group.member", Grace, "grace");

        // Holds a member JIM has no object for yet; the import creates it, and resolves this value to it.
        var beta = Cso(Beta, "group", "beta");
        Text(beta, "group.displayName", "Team B");
        Reference(beta, "group.member", null, "zelda");

        await new PostgresDataRepository(ctx).Sync.CreateConnectedSystemObjectsAsync(
            [ada, grace, katherine, dorothy, mary, hedy, joan, rosalind, alpha, beta]);
    }

    private async Task SeedPendingExportsAsync()
    {
        await using var seed = NewContext();
        var exportedAt = DateTime.UtcNow.AddMinutes(-30);
        var changeIndex = 0;

        PendingExport Export(Guid id, Guid csoId, PendingExportChangeType changeType, PendingExportStatus status)
        {
            _names[id] = $"{_names[csoId]}:{changeType}";
            var export = new PendingExport
            {
                Id = id, ConnectedSystemId = _systemId, ConnectedSystemObjectId = csoId, ChangeType = changeType, Status = status,
                CreatedAt = exportedAt.AddMinutes(-5), LastAttemptedAt = exportedAt
            };
            seed.PendingExports.Add(export);
            return export;
        }

        PendingExportAttributeValueChange Change(PendingExport export, string attribute, PendingExportAttributeChangeType changeType,
            PendingExportAttributeChangeStatus status = PendingExportAttributeChangeStatus.ExportedPendingConfirmation)
        {
            var change = new PendingExportAttributeValueChange
            {
                Id = Guid.Parse($"00000000-0000-0000-0000-{++changeIndex:x12}"), AttributeId = _attributeIds[attribute], ChangeType = changeType,
                Status = status, ExportAttemptCount = 1, LastExportedAt = status == PendingExportAttributeChangeStatus.Pending ? null : exportedAt
            };
            _names[change.Id] = $"{attribute}#{changeIndex}";
            export.AttributeValueChanges.Add(change);
            return change;
        }

        var ada = Export(AdaExport, Ada, PendingExportChangeType.Update, PendingExportStatus.Exported);
        Change(ada, "user.displayName", PendingExportAttributeChangeType.Update).StringValue = "Ada King";
        Change(ada, "user.mail", PendingExportAttributeChangeType.Update).StringValue = "ada@new.example";
        Change(ada, "user.employeeNumber", PendingExportAttributeChangeType.Update).IntValue = 101;
        Change(ada, "user.proxyAddresses", PendingExportAttributeChangeType.Add).StringValue = "smtp:ada@b.example";
        Change(ada, "user.proxyAddresses", PendingExportAttributeChangeType.Remove).StringValue = "smtp:ada@a.example";
        var manager = Change(ada, "user.manager", PendingExportAttributeChangeType.Update);
        manager.ResolvedReferenceCsoId = Katherine;
        manager.UnresolvedReferenceValue = "katherine";

        var grace = Export(GraceExport, Grace, PendingExportChangeType.Update, PendingExportStatus.Exported);
        Change(grace, "user.displayName", PendingExportAttributeChangeType.Update).StringValue = "Grace Brewster Hopper";

        // Only queued: nothing exported yet, so nothing to confirm.
        var katherine = Export(KatherineQueued, Katherine, PendingExportChangeType.Update, PendingExportStatus.Pending);
        Change(katherine, "user.displayName", PendingExportAttributeChangeType.Update, PendingExportAttributeChangeStatus.Pending)
            .StringValue = "Katherine Goble";

        var dorothy = Export(DorothyCreate, Dorothy, PendingExportChangeType.Create, PendingExportStatus.Exported);
        Change(dorothy, "user.id", PendingExportAttributeChangeType.Add).StringValue = "dorothy";
        Change(dorothy, "user.displayName", PendingExportAttributeChangeType.Add).StringValue = "Dorothy Vaughan";

        var mary = Export(MaryExport, Mary, PendingExportChangeType.Update, PendingExportStatus.Exported);
        Change(mary, "user.displayName", PendingExportAttributeChangeType.Update).StringValue = "Mary Winston Jackson";

        var hedy = Export(HedyExport, Hedy, PendingExportChangeType.Update, PendingExportStatus.Exported);
        Change(hedy, "user.mail", PendingExportAttributeChangeType.Update).StringValue = "hedy@new.example";

        // Missed by an earlier confirming import already.
        var joan = Export(JoanExport, Joan, PendingExportChangeType.Update, PendingExportStatus.ExportNotConfirmed);
        var joanMail = Change(joan, "user.mail", PendingExportAttributeChangeType.Update, PendingExportAttributeChangeStatus.ExportedNotConfirmed);
        joanMail.StringValue = "joan@new.example";
        joanMail.ExportAttemptCount = 2;
        joanMail.LastImportedValue = "joan@old.example";

        Export(RosalindDelete, Rosalind, PendingExportChangeType.Delete, PendingExportStatus.Exported);

        var alpha = Export(AlphaExport, Alpha, PendingExportChangeType.Update, PendingExportStatus.Exported);
        foreach (var (changeType, referenced) in new[]
                 {
                     (PendingExportAttributeChangeType.Add, Ada), (PendingExportAttributeChangeType.Add, Katherine),
                     (PendingExportAttributeChangeType.Remove, Grace), (PendingExportAttributeChangeType.Add, Joan)
                 })
        {
            var member = Change(alpha, "group.member", changeType);
            member.ResolvedReferenceCsoId = referenced;
            member.UnresolvedReferenceValue = _names[referenced];
        }

        var beta = Export(BetaExport, Beta, PendingExportChangeType.Update, PendingExportStatus.Exported);
        Change(beta, "group.displayName", PendingExportAttributeChangeType.Update).StringValue = "Team Beta";
        Change(beta, "group.member", PendingExportAttributeChangeType.Add).UnresolvedReferenceValue = "zelda";

        await seed.SaveChangesAsync();
    }

    private static ConnectedSystemImportObjectAttribute ImportText(string name, params string[] values) =>
        new() { Name = name, Type = AttributeDataType.Text, StringValues = [.. values] };

    private static ConnectedSystemImportObjectAttribute ImportReference(string name, params string[] values) =>
        new() { Name = name, Type = AttributeDataType.Reference, ReferenceValues = [.. values] };

    /// <summary>An object a Delta Import reads: updated, with its Secondary External ID, or deleted, with only its External ID.</summary>
    private static ConnectedSystemImportObject DeltaObject(string type, ObjectChangeType changeType, params ConnectedSystemImportObjectAttribute[] attributes) =>
        new()
        {
            ObjectType = type, ChangeType = changeType,
            Attributes = changeType == ObjectChangeType.Deleted ? [.. attributes] : [.. attributes, ImportText("uid", attributes[0].StringValues[0])]
        };

    private static ConnectedSystemImportObject AdaUpdated() => DeltaObject("user", ObjectChangeType.Updated, ImportText("id", "ada"),
        ImportText("displayName", "Ada King"), ImportText("mail", "ada@other.example"),
        new ConnectedSystemImportObjectAttribute { Name = "employeeNumber", Type = AttributeDataType.Number, IntValues = [101] },
        ImportText("proxyAddresses", "smtp:ada@a.example", "smtp:ada@b.example"), ImportReference("manager", "katherine"));

    private static ConnectedSystemImportObject HedyUpdated() => DeltaObject("user", ObjectChangeType.Updated, ImportText("id", "hedy"),
        ImportText("displayName", "Hedy Lamarr"), ImportText("mail", "hedy@new.example"));

    private static ConnectedSystemImportObject GraceUpdated() => DeltaObject("user", ObjectChangeType.Updated, ImportText("id", "grace"),
        ImportText("displayName", "Grace Brewster Hopper"));

    /// <summary>
    /// A Delta Import's changes, over two pages: Ada and Alpha as the Full Import reads them (Alpha's members compared in
    /// part, #2817), Grace on both pages (the later copy takes the earlier's place, #2074), Hedy unchanged, Joan deleted as
    /// reported under the other Object Type (#2049), and Rosalind deleted as reported under her own.
    /// </summary>
    private static List<List<ConnectedSystemImportObject>> DeltaPages() =>
    [
        [
            AdaUpdated(),
            DeltaObject("group", ObjectChangeType.Updated, ImportText("id", "alpha"), ImportText("displayName", "Team Alpha"),
                ImportReference("member", "ada", "katherine")),
            GraceUpdated()
        ],
        [
            GraceUpdated(),
            HedyUpdated(),
            DeltaObject("group", ObjectChangeType.Deleted, ImportText("id", "joan")),
            DeltaObject("user", ObjectChangeType.Deleted, ImportText("id", "rosalind"))
        ]
    ];

    /// <summary>What the directory returns, over two pages. Mary is gone; Zelda is new.</summary>
    private static List<List<ConnectedSystemImportObject>> DirectoryPages()
    {
        static ConnectedSystemImportObjectAttribute Text(string name, params string[] values) =>
            new() { Name = name, Type = AttributeDataType.Text, StringValues = [.. values] };

        static ConnectedSystemImportObjectAttribute Reference(string name, params string[] values) =>
            new() { Name = name, Type = AttributeDataType.Reference, ReferenceValues = [.. values] };

        // Every object carries its Secondary External ID, the same as its External ID.
        static ConnectedSystemImportObject Object(string type, params ConnectedSystemImportObjectAttribute[] attributes) =>
            new() { ObjectType = type, ChangeType = ObjectChangeType.NotSet, Attributes = [.. attributes, Text("uid", attributes[0].StringValues[0])] };

        return
        [
            [
                Object("user", Text("id", "ada"), Text("displayName", "Ada King"), Text("mail", "ada@other.example"),
                    new ConnectedSystemImportObjectAttribute { Name = "employeeNumber", Type = AttributeDataType.Number, IntValues = [101] },
                    Text("proxyAddresses", "smtp:ada@a.example", "smtp:ada@b.example"), Reference("manager", "katherine")),
                Object("user", Text("id", "grace"), Text("displayName", "Grace Brewster Hopper")),
                Object("user", Text("id", "katherine"), Text("displayName", "Katherine Johnson")),
                Object("user", Text("id", "dorothy"), Text("displayName", "Dorothy Vaughan")),
                Object("user", Text("id", "hedy"), Text("displayName", "Hedy Lamarr"), Text("mail", "hedy@new.example"))
            ],
            [
                Object("user", Text("id", "joan"), Text("displayName", "Joan Clarke"), Text("mail", "joan@old.example")),
                Object("user", Text("id", "rosalind"), Text("displayName", "Rosalind Franklin")),
                Object("user", Text("id", "zelda"), Text("displayName", "Zelda Fitzgerald")),
                Object("group", Text("id", "alpha"), Text("displayName", "Team Alpha"), Reference("member", "ada", "katherine")),
                Object("group", Text("id", "beta"), Text("displayName", "Team Beta"), Reference("member", "zelda"))
            ]
        ];
    }

    /// <summary>Runs the confirming Full Import on its own tracking context, as the Worker does.</summary>
    private async Task<(Activity Activity, RecordingSyncRepository Recorder)> RunConfirmingFullImportAsync(int importBatchSize = 2000)
    {
        var (activity, recorder, failure) = await RunImportAsync(ConnectedSystemRunType.FullImport, DirectoryPages(), importBatchSize);
        if (failure != null)
            throw failure;
        return (activity, recorder);
    }

    /// <summary>
    /// Runs an import of <paramref name="pages"/> on its own tracking context, as the Worker does, returning what it
    /// recorded and the exception it failed with, if any. <paramref name="arrange"/> can make a repository call fail or
    /// cancel the run part-way, as a database fault or an administrator would.
    /// </summary>
    private async Task<(Activity Activity, RecordingSyncRepository Recorder, Exception? Failure)> RunImportAsync(ConnectedSystemRunType runType,
        List<List<ConnectedSystemImportObject>> pages, int importBatchSize = 2000, Action<RecordingSyncRepository, CancellationTokenSource>? arrange = null)
    {
        await using var ctx = NewContext(trackAll: true);
        var repository = new PostgresDataRepository(ctx);
        using var jim = new JimApplication(repository, syncRepository: repository.Sync);
        var system = await jim.ConnectedSystems.GetConnectedSystemAsync(_systemId, withChangeTracking: true)
            ?? throw new InvalidOperationException("Connected System not found.");
        var runProfile = system.RunProfiles!.Single(rp => rp.RunType == runType);
        var activity = new Activity
        {
            TargetName = runProfile.Name, Status = ActivityStatus.InProgress, ConnectedSystemId = system.Id,
            ConnectedSystemRunProfileId = runProfile.Id, ConnectedSystemRunType = runType
        };
        ctx.Activities.Add(activity);
        await ctx.SaveChangesAsync();

        var workerTask = new SynchronisationWorkerTask(system.Id, runProfile.Id)
        {
            Id = Guid.NewGuid(), Status = WorkerTaskStatus.Processing, Activity = activity
        };
        var (syncRepository, recorder) = RecordingSyncRepository.Wrap(jim.SyncRepository);
        using var cancellation = new CancellationTokenSource();
        arrange?.Invoke(recorder, cancellation);
        var processor = new SyncImportTaskProcessor(jim, syncRepository, new SyncServer(jim), new SyncEngine(),
            new PagedImportConnector(pages), system, runProfile, workerTask, cancellation)
        {
            ImportBatchSize = importBatchSize
        };

        try
        {
            await processor.PerformImportAsync();
        }
        catch (Exception ex) when (arrange != null)
        {
            return (activity, recorder, ex);
        }

        await jim.SyncRepository.UpdateActivityAsync(activity);
        return (activity, recorder, null);
    }

    /// <summary>
    /// Everything reconciliation decides, as text: the Pending Exports left and every change on them, every execution item
    /// the import recorded with its outcomes and causal edges, the Activity's counters, and the stat counters. Names stand
    /// in for ids.
    /// </summary>
    private async Task<string> SnapshotAsync(Guid activityId)
    {
        await using var ctx = NewContext();
        foreach (var cso in await ctx.ConnectedSystemObjects.AsNoTracking().Include(c => c.AttributeValues)
                     .Where(c => c.ConnectedSystemId == _systemId).ToListAsync())
        {
            var externalId = cso.AttributeValues.SingleOrDefault(v => v.AttributeId == cso.ExternalIdAttributeId)?.StringValue;
            if (externalId != null)
                _names.TryAdd(cso.Id, externalId);
        }

        string Name(Guid? id) => id is not { } value ? "-" : _names.TryGetValue(value, out var name) ? name : "(unnamed)";

        var text = new StringBuilder();

        text.AppendLine("Pending Exports:");
        var exports = await ctx.PendingExports.AsNoTracking().Include(p => p.AttributeValueChanges)
            .Where(p => p.ConnectedSystemId == _systemId).ToListAsync();
        foreach (var export in exports.OrderBy(p => Name(p.Id), StringComparer.Ordinal))
        {
            text.AppendLine($"  {Name(export.Id)} on {Name(export.ConnectedSystemObjectId)}: {export.ChangeType} {export.Status} errors={export.ErrorCount}");
            foreach (var change in export.AttributeValueChanges.OrderBy(c => c.Id))
                text.AppendLine($"    {Name(change.Id)} {change.ChangeType} {change.Status} attempts={change.ExportAttemptCount} " +
                    $"lastImported={change.LastImportedValue ?? "-"}");
        }

        text.AppendLine("Connected System Objects:");
        foreach (var cso in (await ctx.ConnectedSystemObjects.AsNoTracking().Where(c => c.ConnectedSystemId == _systemId).ToListAsync())
                 .OrderBy(c => Name(c.Id), StringComparer.Ordinal))
            text.AppendLine($"  {Name(cso.Id)}: {cso.Status}");

        text.AppendLine("Execution items:");
        var items = await ctx.ActivityRunProfileExecutionItems.AsNoTracking().Where(i => i.ActivityId == activityId).ToListAsync();
        var itemIds = items.Select(i => i.Id).ToList();
        var outcomes = await ctx.ActivityRunProfileExecutionItemSyncOutcomes.AsNoTracking()
            .Where(o => itemIds.Contains(o.ActivityRunProfileExecutionItemId)).ToListAsync();
        var edges = await ctx.CausalEdges.AsNoTracking().Where(e => itemIds.Contains(e.EffectRunProfileExecutionItemId)).ToListAsync();
        var outcomeTypes = outcomes.ToDictionary(o => o.Id, o => o.OutcomeType);
        foreach (var item in items.OrderBy(i => Name(i.ConnectedSystemObjectId), StringComparer.Ordinal).ThenBy(i => i.ObjectChangeType))
        {
            text.AppendLine($"  {Name(item.ConnectedSystemObjectId)}: {item.ObjectChangeType} error={item.ErrorType?.ToString() ?? "-"} " +
                $"summary={item.OutcomeSummary ?? "-"} snapshots=[{item.ExternalIdSnapshot ?? "-"}|{item.DisplayNameSnapshot ?? "-"}|{item.ObjectTypeSnapshot ?? "-"}]");
            if (!string.IsNullOrEmpty(item.ErrorMessage))
                text.AppendLine($"    message: {item.ErrorMessage}");
            foreach (var outcome in outcomes.Where(o => o.ActivityRunProfileExecutionItemId == item.Id)
                         .OrderBy(o => o.ParentSyncOutcomeId.HasValue).ThenBy(o => o.Ordinal).ThenBy(o => o.OutcomeType))
                text.AppendLine($"    outcome {outcome.OutcomeType} ordinal={outcome.Ordinal} detail={outcome.DetailCount?.ToString() ?? "-"} " +
                    $"parent={(outcome.ParentSyncOutcomeId is { } parent ? outcomeTypes[parent].ToString() : "-")}");
            foreach (var edge in edges.Where(e => e.EffectRunProfileExecutionItemId == item.Id).OrderBy(e => e.EdgeType))
                text.AppendLine($"    edge {edge.EdgeType} reason={edge.ReasonCode} export={Name(edge.CausePendingExportId)} " +
                    $"object={Name(edge.CauseConnectedSystemObjectId)} display={edge.CauseDisplayName ?? "-"} system={edge.ConnectedSystemName ?? "-"} " +
                    $"effect={(edge.EffectSyncOutcomeId is { } effect ? outcomeTypes[effect].ToString() : "-")}");
        }

        var activity = await ctx.Activities.AsNoTracking().SingleAsync(a => a.Id == activityId);
        text.AppendLine("Activity:");
        text.AppendLine($"  confirmed={activity.PendingExportsConfirmed} errors={activity.TotalErrors} added={activity.TotalAdded} " +
            $"updated={activity.TotalUpdated} deleted={activity.TotalDeleted} created={activity.TotalCreated} " +
            $"processed={activity.ObjectsProcessed}/{activity.ObjectsToProcess}");
        // The message ends with the run's throughput, which differs from run to run.
        text.AppendLine($"  message: {Regex.Replace(activity.Message ?? "-", @" in [^()]+ \(avg [^)]+\)", "")}");

        text.AppendLine("Stat counters:");
        foreach (var counter in (await ctx.ActivityStatCounters.AsNoTracking().Where(c => c.ActivityId == activityId).ToListAsync())
                 .OrderBy(c => c.Dimension).ThenBy(c => c.Key, StringComparer.Ordinal))
            text.AppendLine($"  {counter.Dimension} {counter.Key}={counter.Count}");

        return text.ToString().Replace("\r\n", "\n");
    }

    #endregion
}
