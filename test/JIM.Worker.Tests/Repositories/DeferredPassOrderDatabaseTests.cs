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

    // ─── Helpers ───

    private static string GeneratedAnchor(Guid csoId) => $"generated-{csoId:N}";

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
