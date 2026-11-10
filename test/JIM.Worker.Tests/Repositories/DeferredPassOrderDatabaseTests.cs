// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Collections.Concurrent;
using JIM.Application;
using JIM.Data.Repositories;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Interfaces;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.PostgresData;
using JIM.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Moq;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Real-PostgreSQL coverage of the order the export's deferred pass works in (#2905): the raw SQL it orders by (each
/// export's change count, and which exports wait on another export of the pass for their object's anchor), and a whole
/// pass over nested groups whose anchors the target system generates, which must write the same exports and leave the
/// same references unresolved as queue order did, while starting with the largest work.
/// Opt-in via <c>JIM_TEST_RESET_*</c>; ignored when absent. <c>SetUp</c> TRUNCATEs every table.
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
public class DeferredPassOrderDatabaseTests
{
    private static readonly DateTime T0 = new(2026, 11, 10, 9, 0, 0, DateTimeKind.Utc);

    private string _connectionString = null!;

    private JimDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<JimDbContext>()
            .UseNpgsql(_connectionString)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        return new JimDbContext(options);
    }

    /// <summary>
    /// A context tracking by default, as the worker's contexts are (JIM.Worker's Program.cs).
    /// </summary>
    private JimDbContext NewWorkerContext()
    {
        var context = NewContext();
        context.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.TrackAll;
        return context;
    }

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        var dbName = Environment.GetEnvironmentVariable("JIM_TEST_RESET_DB");
        if (string.IsNullOrEmpty(dbName))
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL deferred pass order tests.");

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

    // ─── GetDeferredExportOrderingSummaryAsync ───

    [Test]
    public async Task GetDeferredExportOrderingSummaryAsync_SomeExports_CountsEachExportsChangesAsync()
    {
        var s = await SeedSchemaAsync();
        var small = await SeedGroupCreateAsync(s, T0, memberMvoIds: [Guid.NewGuid()]);
        var large = await SeedGroupCreateAsync(s, T0.AddSeconds(1), memberMvoIds: [Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()]);

        var summary = await ReadSummaryAsync(s, small.ExportId, large.ExportId);

        Assert.That(summary.ChangeCounts, Is.EquivalentTo(new Dictionary<Guid, int> { [small.ExportId] = 2, [large.ExportId] = 4 }),
            "each export's display name and member changes");
    }

    [Test]
    public async Task GetDeferredExportOrderingSummaryAsync_ReferenceToAnExportWhoseObjectHoldsNoAnchorYet_IsADependencyAsync()
    {
        // The target system generates the anchor, so the referenced group has none until its own Create is written.
        var s = await SeedSchemaAsync();
        var nested = await SeedGroupCreateAsync(s, T0, memberMvoIds: [Guid.NewGuid()]);
        var parent = await SeedGroupCreateAsync(s, T0.AddSeconds(1), memberMvoIds: [nested.MvoId, Guid.NewGuid()]);

        var summary = await ReadSummaryAsync(s, nested.ExportId, parent.ExportId);

        Assert.That(summary.Dependencies, Is.EqualTo(new[] { new DeferredExportDependency(parent.ExportId, nested.ExportId) }));
    }

    [Test]
    public async Task GetDeferredExportOrderingSummaryAsync_ReferenceToAnExportWhoseObjectHoldsItsAnchor_IsNoDependencyAsync()
    {
        // An LDAP directory's object holds the DN it was provisioned with: a reference to it resolves wherever it falls.
        var s = await SeedSchemaAsync();
        var nested = await SeedGroupCreateAsync(s, T0, memberMvoIds: [Guid.NewGuid()], anchor: "cn=nested,ou=groups,dc=glitterband,dc=local");
        var parent = await SeedGroupCreateAsync(s, T0.AddSeconds(1), memberMvoIds: [nested.MvoId]);

        var summary = await ReadSummaryAsync(s, nested.ExportId, parent.ExportId);

        Assert.That(summary.Dependencies, Is.Empty);
    }

    [Test]
    public async Task GetDeferredExportOrderingSummaryAsync_ReferenceToAnUpdateChangingItsObjectsAnchor_IsADependencyAsync()
    {
        // A rename changes what a reference to the object is written as, so a reference resolved before it is written would
        // carry the old name.
        var s = await SeedSchemaAsync();
        var renamed = await SeedGroupCreateAsync(s, T0, memberMvoIds: [Guid.NewGuid()], anchor: "cn=old,ou=groups,dc=glitterband,dc=local",
            changeType: PendingExportChangeType.Update, newAnchor: "cn=new,ou=groups,dc=glitterband,dc=local");
        var parent = await SeedGroupCreateAsync(s, T0.AddSeconds(1), memberMvoIds: [renamed.MvoId]);

        var summary = await ReadSummaryAsync(s, renamed.ExportId, parent.ExportId);

        Assert.That(summary.Dependencies, Is.EqualTo(new[] { new DeferredExportDependency(parent.ExportId, renamed.ExportId) }));
    }

    [Test]
    public async Task GetDeferredExportOrderingSummaryAsync_ReferencesToItselfOrNotAnId_AreIgnoredAsync()
    {
        var s = await SeedSchemaAsync();
        var group = await SeedGroupCreateAsync(s, T0, memberMvoIds: [Guid.NewGuid()]);
        await using (var seed = NewContext())
        {
            seed.PendingExportAttributeValueChanges.AddRange(
                new PendingExportAttributeValueChange
                {
                    Id = Guid.NewGuid(), PendingExportId = group.ExportId, AttributeId = s.MemberAttributeId, ChangeType = PendingExportAttributeChangeType.Add,
                    UnresolvedReferenceValue = group.MvoId.ToString()
                },
                new PendingExportAttributeValueChange
                {
                    Id = Guid.NewGuid(), PendingExportId = group.ExportId, AttributeId = s.MemberAttributeId, ChangeType = PendingExportAttributeChangeType.Add,
                    UnresolvedReferenceValue = "cn=not an identifier"
                });
            await seed.SaveChangesAsync();
        }

        var summary = await ReadSummaryAsync(s, group.ExportId);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(summary.Dependencies, Is.Empty);
            Assert.That(summary.ChangeCounts[group.ExportId], Is.EqualTo(4));
        }
    }

    [Test]
    public async Task GetDeferredExportOrderingSummaryAsync_ExportsSupplyingTheirObjectsAnchor_NamesTheirMetaverseObjectsAsync()
    {
        // #2605: the pass never answers a reference to one of these from an anchor it read earlier.
        var s = await SeedSchemaAsync();
        var generated = await SeedGroupCreateAsync(s, T0, memberMvoIds: [Guid.NewGuid()]);
        var held = await SeedGroupCreateAsync(s, T0.AddSeconds(1), memberMvoIds: [Guid.NewGuid()], anchor: "cn=held,ou=groups,dc=glitterband,dc=local");
        var renamed = await SeedGroupCreateAsync(s, T0.AddSeconds(2), memberMvoIds: [Guid.NewGuid()], anchor: "cn=old,ou=groups,dc=glitterband,dc=local",
            changeType: PendingExportChangeType.Update, newAnchor: "cn=new,ou=groups,dc=glitterband,dc=local");

        var summary = await ReadSummaryAsync(s, generated.ExportId, held.ExportId, renamed.ExportId);

        Assert.That(summary.AnchorSupplierMetaverseObjectIds, Is.EquivalentTo(new[] { generated.MvoId, renamed.MvoId }));
    }

    // ─── The whole pass ───

    /// <summary>
    /// Nested groups whose anchors the target system generates, eight deferred Creates in chunks of two. In queue order a
    /// group's reference to another group resolves within the run when that group was in an earlier chunk, and not when it
    /// shares the chunk or comes later. The pass must write the same exports, send the same members and leave the same
    /// references unresolved (unwritten at the end of the pass) as queue order did, and start with the largest export no
    /// other waits behind. The oracle below states queue order's outcome independently of the code; run against queue
    /// order on one worker it matched exactly. On two workers queue order fell short of it: the next chunk was resolved
    /// while the batch it waited on was still running.
    /// </summary>
    [TestCase(1)]
    [TestCase(2)]
    public async Task ExecuteExportsAsync_NestedGroupsWithGeneratedAnchors_ResolveWhatQueueOrderDidAndStartLargestFirstAsync(int maxParallelism)
    {
        const int chunkSize = 2;
        var s = await SeedSchemaAsync();

        // Users already in the directory, holding their anchors.
        var users = new List<Guid>();
        for (var u = 0; u < 20; u++)
            users.Add(await SeedUserAsync(s, $"user-{u}"));

        // Per group, in queue order: the users and the groups it contains.
        (int[] Users, int[] Groups)[] spec =
        [
            ([0], []),                                   // G0
            ([1, 2], []),                                // G1
            ([3], [0]),                                  // G2: G0 is in an earlier chunk, so resolves
            ([4, 5, 6, 7, 8], []),                       // G3: the largest no other waits behind
            ([9], [6]),                                  // G4: G6 comes later, so never resolves within the run
            ([10], [3]),                                 // G5: G3 earlier, resolves
            ([], [3, 7]),                                // G6: G3 earlier resolves; G7 shares its chunk, so does not
            ([11, 12, 13, 14, 15, 16, 17, 18, 19], [2])  // G7: the largest of all, waits on G2, which resolves
        ];

        var groups = new List<(Guid ExportId, Guid MvoId, Guid CsoId)>();
        var mvoIds = spec.Select(_ => Guid.NewGuid()).ToList();
        for (var g = 0; g < spec.Length; g++)
        {
            var members = spec[g].Users.Select(u => users[u]).Concat(spec[g].Groups.Select(other => mvoIds[other])).ToArray();
            groups.Add(await SeedGroupCreateAsync(s, T0.AddSeconds(g), members, mvoId: mvoIds[g]));
        }

        // Queue order's outcome: a member resolves when it is a user, or a group in an earlier chunk, which was written
        // (and so given its generated anchor) before this group's chunk was resolved.
        // A member the pass leaves unresolved is not written; once every group has been written the run's closing
        // resolution resolves it for the next run, so it is left behind pending, naming the group's generated anchor.
        var expectedSent = new Dictionary<Guid, string[]>();
        var expectedLeftPending = new List<(Guid ExportId, string Reference)>();
        for (var g = 0; g < spec.Length; g++)
        {
            var sent = spec[g].Users.Select(u => $"user-{u}").ToList();
            foreach (var other in spec[g].Groups)
            {
                if (other / chunkSize < g / chunkSize)
                    sent.Add(GeneratedAnchor(groups[other].CsoId));
                else
                    expectedLeftPending.Add((groups[g].ExportId, GeneratedAnchor(groups[other].CsoId)));
            }
            expectedSent[groups[g].ExportId] = sent.ToArray();
        }

        // The first wave's batches are held until every worker has started one, so the first calls are exactly the first
        // wave, however the workers' timing falls.
        var batchesStarted = new List<List<Guid>>();
        var firstWaveStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sentMembers = new ConcurrentDictionary<Guid, string[]>();
        var writtenTwice = 0;
        Mock<IConnector> CreateConnector()
        {
            var connector = new Mock<IConnector>();
            connector.Setup(c => c.Name).Returns("Generating Connector");
            connector.As<IConnectorExportUsingCalls>()
                .Setup(c => c.ExportAsync(It.IsAny<IList<PendingExport>>(), It.IsAny<CancellationToken>(), It.IsAny<IConnectorProgress>()))
                .Returns(async (IList<PendingExport> exports, CancellationToken _, IConnectorProgress _) =>
                {
                    lock (batchesStarted)
                    {
                        batchesStarted.Add(exports.Select(pe => pe.Id).ToList());
                        if (batchesStarted.Count == maxParallelism)
                            firstWaveStarted.TrySetResult();
                    }
                    await firstWaveStarted.Task.WaitAsync(TimeSpan.FromSeconds(30));
                    foreach (var export in exports)
                    {
                        if (!sentMembers.TryAdd(export.Id, export.AttributeValueChanges
                                .Where(c => c.AttributeId == s.MemberAttributeId && c.StringValue != null)
                                .Select(c => c.StringValue!).ToArray()))
                            Interlocked.Increment(ref writtenTwice);
                    }
                    return exports.Select(export => ConnectedSystemExportResult.Succeeded(GeneratedAnchor(export.ConnectedSystemObjectId!.Value))).ToList();
                });
            return connector;
        }

        await using var callerContext = NewWorkerContext();
        var callerRepository = new PostgresDataRepository(callerContext);
        using var jim = new JimApplication(callerRepository, syncRepository: callerRepository.Sync);
        var connectedSystem = (await jim.ConnectedSystems.GetConnectedSystemAsync(s.SystemId))!;

        // Act
        var result = await jim.ExportExecution.ExecuteExportsAsync(connectedSystem, CreateConnector().Object, SyncRunMode.PreviewAndSync,
            new ExportExecutionOptions { BatchSize = 2, MaxParallelism = maxParallelism, DeferredChunkSize = chunkSize }, CancellationToken.None,
            connectorFactory: () => CreateConnector().Object,
            repositoryFactory: () =>
            {
                var batchRepository = new PostgresDataRepository(NewWorkerContext());
                return new SyncRepositoryScope(batchRepository.Sync, batchRepository);
            });

        // Assert
        await using var verify = NewContext();
        var leftPending = (await verify.PendingExportAttributeValueChanges
                .Where(c => c.AttributeId == s.MemberAttributeId && c.Status == PendingExportAttributeChangeStatus.Pending)
                .Select(c => new { c.PendingExportId, c.StringValue, c.UnresolvedReferenceValue })
                .ToListAsync())
            .Select(c => (c.PendingExportId!.Value, c.StringValue ?? c.UnresolvedReferenceValue!))
            .ToList();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.FailedCount, Is.Zero);
            Assert.That(writtenTwice, Is.Zero, "each export is written once");
            Assert.That(sentMembers.Keys, Is.EquivalentTo(groups.Select(group => group.ExportId)), "the same exports are written");
            foreach (var (exportId, expected) in expectedSent)
                Assert.That(sentMembers.GetValueOrDefault(exportId), Is.EquivalentTo(expected), "the same members are sent for each group");
            Assert.That(leftPending, Is.EquivalentTo(expectedLeftPending), "the same references are left unwritten at the end of the pass");
            Assert.That(batchesStarted.Take(maxParallelism).Any(batch => batch.Contains(groups[3].ExportId)), Is.True,
                "the largest export nothing waits behind starts in the first wave");
        }
    }

    /// <summary>
    /// #2605: a chunk is prepared while the batches of the chunk before it are still being written, so the workers do not
    /// wait for it. Eight groups of users whose anchors the directory already holds, in chunks of four: both workers' first
    /// batches are held until the second chunk's resolutions have been saved, which happens only if it is prepared ahead.
    /// Everything is still written, with every member, and nothing is left pending.
    /// </summary>
    [Test]
    public async Task ExecuteExportsAsync_ParallelDeferredPass_PreparesTheNextChunkWhileTheCurrentChunksBatchesRunAsync()
    {
        const int groupCount = 8;
        var s = await SeedSchemaAsync();
        var users = new List<Guid>();
        for (var u = 0; u < 4; u++)
            users.Add(await SeedUserAsync(s, $"user-{u}"));

        var groups = new List<(Guid ExportId, Guid MvoId, Guid CsoId)>();
        for (var g = 0; g < groupCount; g++)
            groups.Add(await SeedGroupCreateAsync(s, T0.AddSeconds(g), [users[g % 4]], anchor: $"cn=group-{g},ou=groups,dc=glitterband,dc=local"));

        var nextChunkPreparedWhileHeld = false;
        var started = 0;
        var sentMembers = new ConcurrentDictionary<Guid, string[]>();
        Mock<IConnector> CreateConnector()
        {
            var connector = new Mock<IConnector>();
            connector.Setup(c => c.Name).Returns("Holding Connector");
            connector.As<IConnectorExportUsingCalls>()
                .Setup(c => c.ExportAsync(It.IsAny<IList<PendingExport>>(), It.IsAny<CancellationToken>(), It.IsAny<IConnectorProgress>()))
                .Returns(async (IList<PendingExport> exports, CancellationToken _, IConnectorProgress _) =>
                {
                    // The first wave, both batches of the first chunk, is held until every group's resolutions are saved.
                    if (Interlocked.Increment(ref started) <= 2
                        && await WaitUntilAsync(async probe => await probe.PendingExports.CountAsync(pe => !pe.HasUnresolvedReferences) == groupCount))
                    {
                        nextChunkPreparedWhileHeld = true;
                    }
                    RecordMembers(s, exports, sentMembers);
                    return exports.Select(_ => ConnectedSystemExportResult.Succeeded()).ToList();
                });
            return connector;
        }

        var result = await ExecuteAsync(s, CreateConnector, maxParallelism: 2, batchSize: 1, chunkSize: 4);

        var leftPending = await CountMembersLeftPendingAsync(s);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(nextChunkPreparedWhileHeld, Is.True, "the second chunk was prepared while the first chunk's batches were held");
            Assert.That(result.FailedCount, Is.Zero);
            for (var g = 0; g < groupCount; g++)
                Assert.That(sentMembers.GetValueOrDefault(groups[g].ExportId), Is.EqualTo(new[] { $"user-{g % 4}" }), $"group {g}'s member is sent");
            Assert.That(leftPending, Is.Zero);
        }
    }

    /// <summary>
    /// #2605: a chunk is not prepared ahead when it refers to an object whose anchor an export in an earlier chunk of its
    /// stage supplies. R refers to T, queued after it, so the reference imposes no stage; T is the largest and goes first,
    /// and R's chunk is loaded, as it always was, once T's chunk has been handed out, by which time T has been written (the
    /// other batch of T's chunk waits for T's anchor). R must send T's generated anchor, as the pass did before it prepared
    /// chunks ahead; prepared ahead, R's chunk would be resolved before T was written and leave the reference pending.
    /// </summary>
    [TestCase(1)]
    [TestCase(2)]
    public async Task ExecuteExportsAsync_ReferenceToAnExportInAnEarlierChunkOfItsStage_ResolvesAsTheLoadedChunkAlwaysDidAsync(int maxParallelism)
    {
        var s = await SeedSchemaAsync();
        var users = new List<Guid>();
        for (var u = 0; u < 9; u++)
            users.Add(await SeedUserAsync(s, $"user-{u}"));

        // Queue order: R, X1, X2, T. Weights (display name plus members): R 2, X1 9, X2 9, T 10. Chunks of three in queue
        // order weigh 20 and 10, so the plan's chunks are [T, X1] and [X2, R], all in one stage.
        var tMvoId = Guid.NewGuid();
        var r = await SeedGroupCreateAsync(s, T0, [tMvoId]);
        var x1 = await SeedGroupCreateAsync(s, T0.AddSeconds(1), users.Take(8).ToArray());
        var x2 = await SeedGroupCreateAsync(s, T0.AddSeconds(2), users.Skip(1).Take(8).ToArray());
        var t = await SeedGroupCreateAsync(s, T0.AddSeconds(3), users.ToArray(), mvoId: tMvoId);

        var sentMembers = new ConcurrentDictionary<Guid, string[]>();
        Mock<IConnector> CreateConnector()
        {
            var connector = new Mock<IConnector>();
            connector.Setup(c => c.Name).Returns("Generating Connector");
            connector.As<IConnectorExportUsingCalls>()
                .Setup(c => c.ExportAsync(It.IsAny<IList<PendingExport>>(), It.IsAny<CancellationToken>(), It.IsAny<IConnectorProgress>()))
                .Returns(async (IList<PendingExport> exports, CancellationToken _, IConnectorProgress _) =>
                {
                    // T's chunk's other batch finishes only once T has been written and given its anchor.
                    if (exports.Any(export => export.Id == x1.ExportId))
                        await WaitUntilAsync(probe => probe.ConnectedSystemObjectAttributeValues.AnyAsync(av => av.ConnectedSystemObject.Id == t.CsoId));
                    RecordMembers(s, exports, sentMembers);
                    return exports.Select(export => ConnectedSystemExportResult.Succeeded(GeneratedAnchor(export.ConnectedSystemObjectId!.Value))).ToList();
                });
            return connector;
        }

        var result = await ExecuteAsync(s, CreateConnector, maxParallelism, batchSize: 1, chunkSize: 3);

        var leftPending = await CountMembersLeftPendingAsync(s);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.FailedCount, Is.Zero);
            Assert.That(sentMembers.Keys, Is.EquivalentTo(new[] { r.ExportId, x1.ExportId, x2.ExportId, t.ExportId }));
            Assert.That(sentMembers.GetValueOrDefault(r.ExportId), Is.EqualTo(new[] { GeneratedAnchor(t.CsoId) }), "R resolves T's generated anchor within the run");
            Assert.That(leftPending, Is.Zero);
        }
    }

    /// <summary>
    /// #2605: the pass remembers the anchors it has read, but never one an export of the pass changes. O is renamed by an
    /// export of the first stage; X, in O's chunk, is resolved before the rename is written and so names O by its old name,
    /// as the pass always has. R waits on O's export, so it is in the next stage, resolved once the rename has been
    /// written: it must name O by its new name, not the old one X's chunk read.
    /// </summary>
    [TestCase(1)]
    [TestCase(2)]
    public async Task ExecuteExportsAsync_ReferenceToAnObjectRenamedEarlierInThePass_ResolvesToItsNewNameAsync(int maxParallelism)
    {
        const string oldName = "cn=old,ou=groups,dc=glitterband,dc=local";
        const string newName = "cn=new,ou=groups,dc=glitterband,dc=local";
        var s = await SeedSchemaAsync();
        var user = await SeedUserAsync(s, "user-0");

        var oMvoId = Guid.NewGuid();
        var x = await SeedGroupCreateAsync(s, T0, [user, oMvoId], anchor: "cn=x,ou=groups,dc=glitterband,dc=local");
        var o = await SeedGroupCreateAsync(s, T0.AddSeconds(1), [user], mvoId: oMvoId, anchor: oldName,
            changeType: PendingExportChangeType.Update, newAnchor: newName);
        var r = await SeedGroupCreateAsync(s, T0.AddSeconds(2), [oMvoId], anchor: "cn=r,ou=groups,dc=glitterband,dc=local");

        var sentMembers = new ConcurrentDictionary<Guid, string[]>();
        Mock<IConnector> CreateConnector()
        {
            var connector = new Mock<IConnector>();
            connector.Setup(c => c.Name).Returns("Renaming Connector");
            connector.As<IConnectorExportUsingCalls>()
                .Setup(c => c.ExportAsync(It.IsAny<IList<PendingExport>>(), It.IsAny<CancellationToken>(), It.IsAny<IConnectorProgress>()))
                .ReturnsAsync((IList<PendingExport> exports, CancellationToken _, IConnectorProgress _) =>
                {
                    RecordMembers(s, exports, sentMembers);
                    return exports.Select(_ => ConnectedSystemExportResult.Succeeded()).ToList();
                });
            return connector;
        }

        var result = await ExecuteAsync(s, CreateConnector, maxParallelism, batchSize: 1, chunkSize: 2);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.FailedCount, Is.Zero);
            Assert.That(sentMembers.GetValueOrDefault(x.ExportId), Is.EquivalentTo(new[] { "user-0", oldName }), "X is resolved before the rename is written");
            Assert.That(sentMembers.GetValueOrDefault(o.ExportId), Is.EqualTo(new[] { "user-0" }));
            Assert.That(sentMembers.GetValueOrDefault(r.ExportId), Is.EqualTo(new[] { newName }), "R names O by the name its export gave it");
        }
    }

    // ─── Helpers ───

    private static string GeneratedAnchor(Guid csoId) => $"generated-{csoId:N}";

    /// <summary>Runs the export over a worker-tracking caller context, each batch on a context of its own.</summary>
    private async Task<ExportExecutionResult> ExecuteAsync(Schema s, Func<Mock<IConnector>> createConnector, int maxParallelism, int batchSize, int chunkSize)
    {
        await using var callerContext = NewWorkerContext();
        var callerRepository = new PostgresDataRepository(callerContext);
        using var jim = new JimApplication(callerRepository, syncRepository: callerRepository.Sync);
        var connectedSystem = (await jim.ConnectedSystems.GetConnectedSystemAsync(s.SystemId))!;
        return await jim.ExportExecution.ExecuteExportsAsync(connectedSystem, createConnector().Object, SyncRunMode.PreviewAndSync,
            new ExportExecutionOptions { BatchSize = batchSize, MaxParallelism = maxParallelism, DeferredChunkSize = chunkSize }, CancellationToken.None,
            connectorFactory: () => createConnector().Object,
            repositoryFactory: () =>
            {
                var batchRepository = new PostgresDataRepository(NewWorkerContext());
                return new SyncRepositoryScope(batchRepository.Sync, batchRepository);
            });
    }

    /// <summary>The member values each export sends, recorded by export id.</summary>
    private static void RecordMembers(Schema s, IEnumerable<PendingExport> exports, ConcurrentDictionary<Guid, string[]> sentMembers)
    {
        foreach (var export in exports)
        {
            sentMembers[export.Id] = export.AttributeValueChanges
                .Where(c => c.AttributeId == s.MemberAttributeId && c.StringValue != null)
                .Select(c => c.StringValue!).ToArray();
        }
    }

    /// <summary>How many member changes are left pending once the run has finished.</summary>
    private async Task<int> CountMembersLeftPendingAsync(Schema s)
    {
        await using var verify = NewContext();
        return await verify.PendingExportAttributeValueChanges
            .CountAsync(c => c.AttributeId == s.MemberAttributeId && c.Status == PendingExportAttributeChangeStatus.Pending);
    }

    /// <summary>Waits, for up to 15 seconds, until <paramref name="condition"/> holds; returns whether it did.</summary>
    private async Task<bool> WaitUntilAsync(Func<JimDbContext, Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            await using (var probe = NewContext())
            {
                if (await condition(probe))
                    return true;
            }
            await Task.Delay(50);
        }
        return false;
    }

    private sealed record Schema(int SystemId, int TypeId, int MvTypeId, int ObjectIdAttributeId, int DisplayNameAttributeId, int MemberAttributeId);

    private async Task<DeferredExportOrderingSummary> ReadSummaryAsync(Schema s, params Guid[] exportIds)
    {
        await using var ctx = NewContext();
        return await new PostgresDataRepository(ctx).Sync.GetDeferredExportOrderingSummaryAsync(s.SystemId, exportIds);
    }

    private async Task<Schema> SeedSchemaAsync()
    {
        await using var seed = NewContext();
        var connectorDefinition = new ConnectorDefinition { Name = "Test Connector", BuiltIn = true };
        var system = new ConnectedSystem { Name = "Glitterband EMEA", ConnectorDefinition = connectorDefinition };
        var type = new ConnectedSystemObjectType { Name = "entry", ConnectedSystem = system, Selected = true };

        ConnectedSystemObjectTypeAttribute Attribute(string name, AttributeDataType dataType, bool externalId = false,
            AttributePlurality plurality = AttributePlurality.SingleValued)
        {
            var attribute = new ConnectedSystemObjectTypeAttribute
            {
                Name = name, ConnectedSystemObjectType = type, Type = dataType, AttributePlurality = plurality, Selected = true, IsExternalId = externalId
            };
            type.Attributes.Add(attribute);
            return attribute;
        }

        var objectId = Attribute("objectId", AttributeDataType.Text, externalId: true);
        var displayName = Attribute("displayName", AttributeDataType.Text);
        var member = Attribute("member", AttributeDataType.Reference, plurality: AttributePlurality.MultiValued);
        var mvType = new MetaverseObjectType { Name = "Group", PluralName = "Groups" };

        seed.AddRange(connectorDefinition, system, type, mvType);
        await seed.SaveChangesAsync();
        return new Schema(system.Id, type.Id, mvType.Id, objectId.Id, displayName.Id, member.Id);
    }

    private async Task<Guid> SeedMetaverseObjectAsync(Schema s, Guid? id = null)
    {
        await using var seed = NewContext();
        var mvo = new MetaverseObject
        {
            Id = id ?? Guid.NewGuid(), Created = DateTime.UtcNow,
            Type = await seed.MetaverseObjectTypes.AsTracking().SingleAsync(t => t.Id == s.MvTypeId)
        };
        seed.Add(mvo);
        await seed.SaveChangesAsync();
        return mvo.Id;
    }

    /// <summary>A user already in the directory, holding its anchor; returns its Metaverse Object's id.</summary>
    private async Task<Guid> SeedUserAsync(Schema s, string anchor)
    {
        var mvoId = await SeedMetaverseObjectAsync(s);
        await using var seed = NewContext();
        var cso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(), ConnectedSystemId = s.SystemId, TypeId = s.TypeId, MetaverseObjectId = mvoId, ExternalIdAttributeId = s.ObjectIdAttributeId,
            Status = ConnectedSystemObjectStatus.Normal, JoinType = ConnectedSystemObjectJoinType.Provisioned
        };
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = s.ObjectIdAttributeId, StringValue = anchor });
        seed.Add(cso);
        await seed.SaveChangesAsync();
        return mvoId;
    }

    /// <summary>
    /// A group waiting on its members' references: a Create by default, of an object holding no anchor (the target system
    /// generates it), with a display name to write and a member change per Metaverse Object it contains. With
    /// <paramref name="anchor"/> the object holds one already; with <paramref name="newAnchor"/> the export changes it.
    /// </summary>
    private async Task<(Guid ExportId, Guid MvoId, Guid CsoId)> SeedGroupCreateAsync(Schema s, DateTime createdAt, Guid[] memberMvoIds,
        Guid? mvoId = null, string? anchor = null, PendingExportChangeType changeType = PendingExportChangeType.Create, string? newAnchor = null)
    {
        var groupMvoId = await SeedMetaverseObjectAsync(s, mvoId);
        await using var seed = NewContext();
        var cso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(), ConnectedSystemId = s.SystemId, TypeId = s.TypeId, MetaverseObjectId = groupMvoId, ExternalIdAttributeId = s.ObjectIdAttributeId,
            Status = changeType == PendingExportChangeType.Create ? ConnectedSystemObjectStatus.PendingProvisioning : ConnectedSystemObjectStatus.Normal,
            JoinType = ConnectedSystemObjectJoinType.Provisioned
        };
        if (anchor != null)
            cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = s.ObjectIdAttributeId, StringValue = anchor });

        var export = new PendingExport
        {
            Id = Guid.NewGuid(), ConnectedSystemId = s.SystemId, ConnectedSystemObjectId = cso.Id, ChangeType = changeType,
            Status = PendingExportStatus.Pending, HasUnresolvedReferences = true, CreatedAt = createdAt
        };
        export.AttributeValueChanges.Add(new PendingExportAttributeValueChange
        {
            Id = Guid.NewGuid(), AttributeId = s.DisplayNameAttributeId, ChangeType = PendingExportAttributeChangeType.Add,
            Status = PendingExportAttributeChangeStatus.Pending, StringValue = $"Group {createdAt:HHmmss}"
        });
        if (newAnchor != null)
        {
            export.AttributeValueChanges.Add(new PendingExportAttributeValueChange
            {
                Id = Guid.NewGuid(), AttributeId = s.ObjectIdAttributeId, ChangeType = PendingExportAttributeChangeType.Update,
                Status = PendingExportAttributeChangeStatus.Pending, StringValue = newAnchor
            });
        }
        foreach (var memberChange in memberMvoIds.Select(memberMvoId => new PendingExportAttributeValueChange
                 {
                     Id = Guid.NewGuid(), AttributeId = s.MemberAttributeId, ChangeType = PendingExportAttributeChangeType.Add,
                     Status = PendingExportAttributeChangeStatus.Pending, UnresolvedReferenceValue = memberMvoId.ToString()
                 }))
            export.AttributeValueChanges.Add(memberChange);

        seed.AddRange(cso, export);
        await seed.SaveChangesAsync();
        return (export.Id, groupMvoId, cso.Id);
    }
}
