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
/// Real-PostgreSQL coverage of what a successful export records of the names the Connector reports: the External
/// ID a Create is given, and the Secondary External ID (the distinguished name, for LDAP) a Create or a rename leaves the
/// object with. Each must be what the database holds once the batch's results are written, not only what the run's
/// in-memory object holds: a reference to the object resolved later in the run, a later run, and the portal all read
/// the stored value. The in-memory store hands out the instances it holds, so it cannot see the difference.
/// Opt-in via <c>JIM_TEST_RESET_*</c>; ignored when absent. <c>SetUp</c> TRUNCATEs every table.
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
public class ExportRenameAnchorPersistenceDatabaseTests
{
    private const string OldName = "CN=Joe Bloggs,OU=Sales,DC=panoply,DC=local";
    private const string NewName = "CN=Joe Bloggs,OU=Finance,DC=panoply,DC=local";

    private static readonly DateTime T0 = new(2026, 11, 12, 9, 0, 0, DateTimeKind.Utc);

    private string _connectionString = null!;

    private JimDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<JimDbContext>()
            .UseNpgsql(_connectionString)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        return new JimDbContext(options);
    }

    /// <summary>A context tracking by default, as the worker's contexts are (JIM.Worker's Program.cs).</summary>
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
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL export rename persistence tests.");

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

    // ─── Export execution ───

    /// <summary>
    /// A move between containers: the export changes the distinguished name, and the Connector renames the object and
    /// reports the name it now has. The database must hold the new name, and only it.
    /// </summary>
    [Test]
    public async Task ExecuteExportsAsync_UpdateTheConnectorRenames_StoresTheNewDistinguishedNameAsync()
    {
        var s = await SeedSchemaAsync();
        var user = await SeedUserAsync(s, "user-guid-1", OldName);
        await SeedUpdateAsync(s, user, T0, newDistinguishedName: NewName);

        var result = await ExecuteAsync(s, () => CreateConnector(_ => ConnectedSystemExportResult.Succeeded(null, NewName)));

        Assert.That(result.FailedCount, Is.Zero);
        Assert.That(await ReadStoredValuesAsync(user.CsoId, s.DistinguishedNameAttributeId), Is.EqualTo(new[] { NewName }),
            "the stored distinguished name is the one the rename gave the object");
    }

    /// <summary>
    /// The same, for a rename the export's own changes do not name (the Connector renamed the object for a change to its
    /// naming attribute): what the Connector reports is what the object is now called.
    /// </summary>
    [Test]
    public async Task ExecuteExportsAsync_RenameTheExportDoesNotName_StoresTheNameTheConnectorReportsAsync()
    {
        var s = await SeedSchemaAsync();
        var user = await SeedUserAsync(s, "user-guid-1", OldName);
        await SeedUpdateAsync(s, user, T0, newDistinguishedName: null);

        var result = await ExecuteAsync(s, () => CreateConnector(_ => ConnectedSystemExportResult.Succeeded(null, NewName)));

        Assert.That(result.FailedCount, Is.Zero);
        Assert.That(await ReadStoredValuesAsync(user.CsoId, s.DistinguishedNameAttributeId), Is.EqualTo(new[] { NewName }));
    }

    /// <summary>
    /// A Create written under a name other than the one provisioning planned for the object (the person moved before
    /// the Create was exported, so the export names the new container): the object holds the planned name from
    /// provisioning, and must be left holding the name it was created with, beside the External ID the target system
    /// assigned.
    /// </summary>
    [Test]
    public async Task ExecuteExportsAsync_CreateWrittenUnderAnotherName_StoresTheNameItWasCreatedWithAsync()
    {
        var s = await SeedSchemaAsync();
        var user = await SeedProvisionedUserAsync(s, plannedName: OldName, externalId: null);
        await SeedCreateAsync(s, user, T0, distinguishedName: NewName);

        var result = await ExecuteAsync(s, () => CreateConnector(_ => ConnectedSystemExportResult.Succeeded("user-guid-1", NewName)));

        Assert.That(result.FailedCount, Is.Zero);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(await ReadStoredValuesAsync(user.CsoId, s.DistinguishedNameAttributeId), Is.EqualTo(new[] { NewName }));
            Assert.That(await ReadStoredValuesAsync(user.CsoId, s.ObjectGuidAttributeId), Is.EqualTo(new[] { "user-guid-1" }));
        }
    }

    /// <summary>
    /// A Create of an object already holding an External ID (one flowed to it, where the target's anchor is not
    /// generated) that the Connector reports under another: the reported one is the object's anchor from now on.
    /// </summary>
    [Test]
    public async Task ExecuteExportsAsync_CreateReportedUnderAnotherExternalId_StoresTheReportedExternalIdAsync()
    {
        var s = await SeedSchemaAsync();
        var user = await SeedProvisionedUserAsync(s, plannedName: OldName, externalId: "planned-anchor");
        await SeedCreateAsync(s, user, T0, distinguishedName: OldName);

        var result = await ExecuteAsync(s, () => CreateConnector(_ => ConnectedSystemExportResult.Succeeded("assigned-anchor", OldName)));

        Assert.That(result.FailedCount, Is.Zero);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(await ReadStoredValuesAsync(user.CsoId, s.ObjectGuidAttributeId), Is.EqualTo(new[] { "assigned-anchor" }));
            Assert.That(await ReadStoredValuesAsync(user.CsoId, s.DistinguishedNameAttributeId), Is.EqualTo(new[] { OldName }));
        }
    }

    /// <summary>
    /// The ordinary LDAP Create: written under exactly the name provisioning planned, which the Connector reports back.
    /// Nothing is written: the stored row is the one provisioning wrote, and the object's import-state hash stands.
    /// </summary>
    [Test]
    public async Task ExecuteExportsAsync_CreateReportedUnderItsPlannedName_LeavesTheStoredNameAndImportStateUntouchedAsync()
    {
        var s = await SeedSchemaAsync();
        var user = await SeedProvisionedUserAsync(s, plannedName: OldName, externalId: null);
        var importState = await StampImportStateAsync(user.CsoId);
        var plannedRowId = (await ReadStoredRowsAsync(user.CsoId, s.DistinguishedNameAttributeId)).Single().Id;
        await SaveExportAsync(WithChanges(NewExport(s, user, T0, PendingExportChangeType.Create, hasUnresolvedReferences: false),
            NewChange(s.DistinguishedNameAttributeId, PendingExportAttributeChangeType.Add, OldName)));

        var result = await ExecuteAsync(s, () => CreateConnector(_ => ConnectedSystemExportResult.Succeeded(null, OldName)));

        Assert.That(result.FailedCount, Is.Zero);
        var rows = await ReadStoredRowsAsync(user.CsoId, s.DistinguishedNameAttributeId);
        await using var verify = NewContext();
        var stored = await verify.ConnectedSystemObjects.SingleAsync(cso => cso.Id == user.CsoId);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(rows.Select(row => (row.Id, row.StringValue)), Is.EqualTo(new[] { (plannedRowId, (string?)OldName) }),
                "the row provisioning wrote is kept, not replaced by an identical one");
            Assert.That((stored.ImportStateHash, stored.ImportStateFingerprint), Is.EqualTo(importState), "nothing changed, so the import-state hash stands");
        }
    }

    /// <summary>
    /// A rename that changes only the case of a name is still a rename: the name the object now has is recorded, as one
    /// value, although optimistic apply compares identifiers without regard to case unless the attribute is case-exact.
    /// </summary>
    [Test]
    public async Task ExecuteExportsAsync_CaseOnlyRename_RecordsTheNewCaseAsTheOnlyValueAsync()
    {
        const string heldName = "CN=Bob,OU=Sales,DC=panoply,DC=local";
        const string renamedName = "CN=bob,OU=Sales,DC=panoply,DC=local";
        var s = await SeedSchemaAsync();
        var user = await SeedUserAsync(s, "user-guid-1", heldName);
        await SeedUpdateAsync(s, user, T0, newDistinguishedName: renamedName);

        var result = await ExecuteAsync(s, () => CreateConnector(_ => ConnectedSystemExportResult.Succeeded(null, renamedName)));

        Assert.That(result.FailedCount, Is.Zero);
        Assert.That(await ReadStoredValuesAsync(user.CsoId, s.DistinguishedNameAttributeId), Is.EqualTo(new[] { renamedName }),
            "compared exactly: the held name differs from the reported one only in case");
    }

    /// <summary>
    /// A Create whose anchor JIM staged in one form and the Connector reports in another (a key the target formats, such
    /// as a zero-padded number): the reported value is the anchor. Optimistic apply recording the staged one beside it
    /// would leave the object two anchors, and every later read of its External ID would fail.
    /// </summary>
    [Test]
    public async Task ExecuteExportsAsync_CreateStagedUnderAnotherExternalIdThanReported_StoresOnlyTheReportedOneAsync()
    {
        var s = await SeedSchemaAsync();
        var user = await SeedProvisionedUserAsync(s, plannedName: OldName, externalId: null);
        await SaveExportAsync(WithChanges(NewExport(s, user, T0, PendingExportChangeType.Create, hasUnresolvedReferences: false),
            NewChange(s.ObjectGuidAttributeId, PendingExportAttributeChangeType.Add, "1001"),
            NewChange(s.DistinguishedNameAttributeId, PendingExportAttributeChangeType.Add, OldName)));

        var result = await ExecuteAsync(s, () => CreateConnector(_ => ConnectedSystemExportResult.Succeeded("00001001", OldName)));

        Assert.That(result.FailedCount, Is.Zero);
        Assert.That(await ReadStoredValuesAsync(user.CsoId, s.ObjectGuidAttributeId), Is.EqualTo(new[] { "00001001" }));
    }

    /// <summary>
    /// The same for the Secondary External ID: a Create whose staged name the Connector reports differently leaves the
    /// object holding the reported name alone.
    /// </summary>
    [Test]
    public async Task ExecuteExportsAsync_CreateStagedUnderAnotherNameThanReported_StoresOnlyTheReportedNameAsync()
    {
        var s = await SeedSchemaAsync();
        var user = await SeedProvisionedUserAsync(s, plannedName: OldName, externalId: null);
        await SeedCreateAsync(s, user, T0, distinguishedName: OldName);

        var result = await ExecuteAsync(s, () => CreateConnector(_ => ConnectedSystemExportResult.Succeeded("user-guid-1", NewName)));

        Assert.That(result.FailedCount, Is.Zero);
        Assert.That(await ReadStoredValuesAsync(user.CsoId, s.DistinguishedNameAttributeId), Is.EqualTo(new[] { NewName }));
    }

    /// <summary>
    /// The file-based export path records exported values too (#1936), so it must leave the same single anchor.
    /// </summary>
    [Test]
    public async Task ExecuteExportsAsync_FileConnectorCreateStagedUnderAnotherExternalIdThanReported_StoresOnlyTheReportedOneAsync()
    {
        var s = await SeedSchemaAsync();
        var user = await SeedProvisionedUserAsync(s, plannedName: OldName, externalId: null);
        await SaveExportAsync(WithChanges(NewExport(s, user, T0, PendingExportChangeType.Create, hasUnresolvedReferences: false),
            NewChange(s.ObjectGuidAttributeId, PendingExportAttributeChangeType.Add, "1001"),
            NewChange(s.DisplayNameAttributeId, PendingExportAttributeChangeType.Add, "Joe Bloggs")));

        var connector = new Mock<IConnector>();
        connector.Setup(c => c.Name).Returns("File Connector");
        connector.As<IConnectorExportUsingFiles>()
            .Setup(c => c.ExportAsync(It.IsAny<IList<ConnectedSystemSettingValue>>(), It.IsAny<IList<PendingExport>>(), It.IsAny<CancellationToken>(), It.IsAny<IConnectorProgress>()))
            .ReturnsAsync((IList<ConnectedSystemSettingValue> _, IList<PendingExport> exports, CancellationToken _, IConnectorProgress _) =>
                exports.Select(_ => ConnectedSystemExportResult.Succeeded("00001001", null)).ToList());

        var result = await ExecuteAsync(s, () => connector);

        Assert.That(result.FailedCount, Is.Zero);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(await ReadStoredValuesAsync(user.CsoId, s.ObjectGuidAttributeId), Is.EqualTo(new[] { "00001001" }));
            Assert.That(await ReadStoredValuesAsync(user.CsoId, s.DisplayNameAttributeId), Is.EqualTo(new[] { "Joe Bloggs" }),
                "the export's other values are still recorded");
        }
    }

    /// <summary>
    /// A mover: the user moves to another container and joins a group in the same synchronisation, so one export run
    /// carries both. The group's member reference is resolved in the deferred pass, after the rename, from the stored
    /// distinguished name; it must name the user as it is now called, or the directory is given a member that does not
    /// exist (refused by Active Directory, kept as a dangling member by a directory without referential integrity).
    /// </summary>
    [TestCase(1)]
    [TestCase(2)]
    public async Task ExecuteExportsAsync_GroupReferencingAUserRenamedEarlierInTheRun_SendsTheNewNameAsync(int maxParallelism)
    {
        var s = await SeedSchemaAsync();
        var user = await SeedUserAsync(s, "user-guid-1", OldName);
        await SeedUpdateAsync(s, user, T0, newDistinguishedName: NewName);
        var group = await SeedUserAsync(s, "group-guid-1", "CN=Finance Team,OU=Groups,DC=panoply,DC=local");
        var groupExportId = await SeedMembershipUpdateAsync(s, group, T0.AddSeconds(1), memberMvoId: user.MvoId);

        var sentMembers = new ConcurrentDictionary<Guid, string[]>();
        var result = await ExecuteAsync(s, () => CreateConnector(export =>
        {
            sentMembers[export.Id] = export.AttributeValueChanges
                .Where(c => c.AttributeId == s.MemberAttributeId && c.StringValue != null)
                .Select(c => c.StringValue!).ToArray();
            return export.ConnectedSystemObjectId == user.CsoId
                ? ConnectedSystemExportResult.Succeeded(null, NewName)
                : ConnectedSystemExportResult.Succeeded();
        }), maxParallelism);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.FailedCount, Is.Zero);
            Assert.That(sentMembers.GetValueOrDefault(groupExportId), Is.EqualTo(new[] { NewName }),
                "the group's member is the user under the name the rename gave it");
        }
    }

    // ─── Repository round trip ───

    /// <summary>
    /// The writer itself: a value superseded by a new one is replaced in the database, every column of the new row is
    /// written, and a context still tracking the superseded row (the worker's long-lived context) can save afterwards.
    /// </summary>
    [Test]
    public async Task UpdateConnectedSystemObjectsWithNewAttributeValuesAsync_SupersededValue_ReplacesTheRowAndLeavesTheContextSaveableAsync()
    {
        var s = await SeedSchemaAsync();
        var user = await SeedUserAsync(s, "user-guid-1", OldName);

        await using (var ctx = NewWorkerContext())
        {
            // The superseded row, tracked as the worker's context would hold it from an earlier read in the run.
            var tracked = await ctx.ConnectedSystemObjectAttributeValues
                .SingleAsync(av => av.ConnectedSystemObject.Id == user.CsoId && av.AttributeId == s.DistinguishedNameAttributeId);
            var cso = new ConnectedSystemObject { Id = user.CsoId, ConnectedSystemId = s.SystemId, TypeId = s.TypeId };
            var replacement = new ConnectedSystemObjectAttributeValue
            {
                ConnectedSystemObject = cso, AttributeId = s.DistinguishedNameAttributeId, StringValue = NewName
            };

            var repository = new PostgresDataRepository(ctx);
            await using (var transaction = await repository.Sync.BeginTransactionAsync())
            {
                await repository.Sync.UpdateConnectedSystemObjectsWithNewAttributeValuesAsync([(cso, [replacement])], [tracked.Id]);
                await transaction!.CommitAsync();
            }

            Assert.That(ctx.Entry(tracked).State, Is.EqualTo(EntityState.Detached), "the superseded row's tracked instance is detached");
            Assert.That(() => ctx.SaveChangesAsync(), Throws.Nothing);
        }

        await using var verify = NewContext();
        var stored = await verify.ConnectedSystemObjectAttributeValues
            .Where(av => av.ConnectedSystemObject.Id == user.CsoId && av.AttributeId == s.DistinguishedNameAttributeId)
            .ToListAsync();
        Assert.That(stored, Has.Count.EqualTo(1));
        var row = stored[0];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(row.Id, Is.Not.EqualTo(Guid.Empty));
            Assert.That(row.StringValue, Is.EqualTo(NewName));
            Assert.That(row.GuidValue, Is.Null);
            Assert.That(row.IntValue, Is.Null);
            Assert.That(row.LongValue, Is.Null);
            Assert.That(row.DecimalValue, Is.Null);
            Assert.That(row.DateTimeValue, Is.Null);
            Assert.That(row.ByteValue, Is.Null);
            Assert.That(row.BoolValue, Is.Null);
            Assert.That(row.ReferenceValueId, Is.Null);
            Assert.That(row.UnresolvedReferenceValue, Is.Null);
        }
    }

    /// <summary>A failure after the superseded row is deleted leaves it in place: the object is never left nameless.</summary>
    [Test]
    public async Task UpdateConnectedSystemObjectsWithNewAttributeValuesAsync_InsertFailsAfterTheDelete_LeavesTheSupersededRowAsync()
    {
        var s = await SeedSchemaAsync();
        var user = await SeedUserAsync(s, "user-guid-1", OldName);
        Guid supersededId;
        await using (var read = NewContext())
        {
            supersededId = await read.ConnectedSystemObjectAttributeValues
                .Where(av => av.ConnectedSystemObject.Id == user.CsoId && av.AttributeId == s.DistinguishedNameAttributeId)
                .Select(av => av.Id).SingleAsync();
        }

        await using (var ctx = NewWorkerContext())
        {
            var cso = new ConnectedSystemObject { Id = user.CsoId, ConnectedSystemId = s.SystemId, TypeId = s.TypeId };
            // An attribute that does not exist: the insert breaks its foreign key, after the delete has run.
            var replacement = new ConnectedSystemObjectAttributeValue { ConnectedSystemObject = cso, AttributeId = int.MaxValue, StringValue = NewName };

            Assert.ThrowsAsync<Npgsql.PostgresException>(() =>
                new PostgresDataRepository(ctx).Sync.UpdateConnectedSystemObjectsWithNewAttributeValuesAsync([(cso, [replacement])], [supersededId]));
        }

        Assert.That(await ReadStoredValuesAsync(user.CsoId, s.DistinguishedNameAttributeId), Is.EqualTo(new[] { OldName }));
    }

    // ─── Helpers ───

    private sealed record Schema(int SystemId, int TypeId, int MvTypeId, int ObjectGuidAttributeId, int DistinguishedNameAttributeId,
        int DisplayNameAttributeId, int MemberAttributeId);

    private sealed record SeededObject(Guid CsoId, Guid MvoId);

    private static Mock<IConnector> CreateConnector(Func<PendingExport, ConnectedSystemExportResult> resultFor)
    {
        var connector = new Mock<IConnector>();
        connector.Setup(c => c.Name).Returns("Renaming Connector");
        connector.As<IConnectorExportUsingCalls>()
            .Setup(c => c.ExportAsync(It.IsAny<IList<PendingExport>>(), It.IsAny<CancellationToken>(), It.IsAny<IConnectorProgress>()))
            .ReturnsAsync((IList<PendingExport> exports, CancellationToken _, IConnectorProgress _) => exports.Select(resultFor).ToList());
        return connector;
    }

    /// <summary>Runs the export over a worker-tracking caller context, each parallel batch on a context of its own.</summary>
    private async Task<ExportExecutionResult> ExecuteAsync(Schema s, Func<Mock<IConnector>> createConnector, int maxParallelism = 1)
    {
        await using var callerContext = NewWorkerContext();
        var callerRepository = new PostgresDataRepository(callerContext);
        using var jim = new JimApplication(callerRepository, syncRepository: callerRepository.Sync);
        var connectedSystem = (await jim.ConnectedSystems.GetConnectedSystemAsync(s.SystemId))!;
        return await jim.ExportExecution.ExecuteExportsAsync(connectedSystem, createConnector().Object, SyncRunMode.PreviewAndSync,
            new ExportExecutionOptions { BatchSize = 1, MaxParallelism = maxParallelism, DeferredChunkSize = 1 }, CancellationToken.None,
            connectorFactory: () => createConnector().Object,
            repositoryFactory: () =>
            {
                var batchRepository = new PostgresDataRepository(NewWorkerContext());
                return new SyncRepositoryScope(batchRepository.Sync, batchRepository);
            });
    }

    /// <summary>The values the database holds for one attribute of one object, read on a fresh context.</summary>
    private async Task<string?[]> ReadStoredValuesAsync(Guid csoId, int attributeId)
    {
        await using var verify = NewContext();
        return await verify.ConnectedSystemObjectAttributeValues
            .Where(av => av.ConnectedSystemObject.Id == csoId && av.AttributeId == attributeId)
            .Select(av => av.StringValue)
            .ToArrayAsync();
    }

    /// <summary>Every row the database holds for one attribute of one object, read on a fresh context.</summary>
    private async Task<List<ConnectedSystemObjectAttributeValue>> ReadStoredRowsAsync(Guid csoId, int attributeId)
    {
        await using var verify = NewContext();
        return await verify.ConnectedSystemObjectAttributeValues
            .Where(av => av.ConnectedSystemObject.Id == csoId && av.AttributeId == attributeId)
            .ToListAsync();
    }

    /// <summary>Gives the object an import-state hash and fingerprint, as an import stamps them; returns the pair.</summary>
    private async Task<(Guid? Hash, Guid? Fingerprint)> StampImportStateAsync(Guid csoId)
    {
        var stamp = ((Guid?)Guid.NewGuid(), (Guid?)Guid.NewGuid());
        await using var seed = NewContext();
        await seed.ConnectedSystemObjects.Where(cso => cso.Id == csoId).ExecuteUpdateAsync(set => set
            .SetProperty(cso => cso.ImportStateHash, stamp.Item1)
            .SetProperty(cso => cso.ImportStateFingerprint, stamp.Item2));
        return stamp;
    }

    private async Task<Schema> SeedSchemaAsync()
    {
        await using var seed = NewContext();
        var connectorDefinition = new ConnectorDefinition { Name = "Test Connector", BuiltIn = true };
        var system = new ConnectedSystem { Name = "Panoply AD", ConnectorDefinition = connectorDefinition };
        var type = new ConnectedSystemObjectType { Name = "entry", ConnectedSystem = system, Selected = true };

        ConnectedSystemObjectTypeAttribute Attribute(string name, AttributeDataType dataType, bool externalId = false, bool secondaryExternalId = false,
            AttributePlurality plurality = AttributePlurality.SingleValued)
        {
            var attribute = new ConnectedSystemObjectTypeAttribute
            {
                Name = name, ConnectedSystemObjectType = type, Type = dataType, AttributePlurality = plurality, Selected = true,
                IsExternalId = externalId, IsSecondaryExternalId = secondaryExternalId
            };
            type.Attributes.Add(attribute);
            return attribute;
        }

        var objectGuid = Attribute("objectGUID", AttributeDataType.Text, externalId: true);
        var distinguishedName = Attribute("distinguishedName", AttributeDataType.Text, secondaryExternalId: true);
        var displayName = Attribute("displayName", AttributeDataType.Text);
        var member = Attribute("member", AttributeDataType.Reference, plurality: AttributePlurality.MultiValued);
        var mvType = new MetaverseObjectType { Name = "User", PluralName = "Users" };

        seed.AddRange(connectorDefinition, system, type, mvType);
        await seed.SaveChangesAsync();
        return new Schema(system.Id, type.Id, mvType.Id, objectGuid.Id, distinguishedName.Id, displayName.Id, member.Id);
    }

    private async Task<Guid> SeedMetaverseObjectAsync(Schema s)
    {
        await using var seed = NewContext();
        var mvo = new MetaverseObject
        {
            Id = Guid.NewGuid(), Created = DateTime.UtcNow,
            Type = await seed.MetaverseObjectTypes.AsTracking().SingleAsync(t => t.Id == s.MvTypeId)
        };
        seed.Add(mvo);
        await seed.SaveChangesAsync();
        return mvo.Id;
    }

    /// <summary>An object already in the directory, holding its External ID and distinguished name.</summary>
    private async Task<SeededObject> SeedUserAsync(Schema s, string externalId, string distinguishedName)
    {
        var mvoId = await SeedMetaverseObjectAsync(s);
        await using var seed = NewContext();
        var cso = NewCso(s, mvoId, ConnectedSystemObjectStatus.Normal);
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = s.ObjectGuidAttributeId, StringValue = externalId });
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = s.DistinguishedNameAttributeId, StringValue = distinguishedName });
        seed.Add(cso);
        await seed.SaveChangesAsync();
        return new SeededObject(cso.Id, mvoId);
    }

    /// <summary>
    /// An object provisioning has created but no export has written yet: it holds the distinguished name provisioning
    /// planned for it, and an External ID only where one was flowed to it.
    /// </summary>
    private async Task<SeededObject> SeedProvisionedUserAsync(Schema s, string plannedName, string? externalId)
    {
        var mvoId = await SeedMetaverseObjectAsync(s);
        await using var seed = NewContext();
        var cso = NewCso(s, mvoId, ConnectedSystemObjectStatus.PendingProvisioning);
        if (externalId != null)
            cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = s.ObjectGuidAttributeId, StringValue = externalId });
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = s.DistinguishedNameAttributeId, StringValue = plannedName });
        seed.Add(cso);
        await seed.SaveChangesAsync();
        return new SeededObject(cso.Id, mvoId);
    }

    private static ConnectedSystemObject NewCso(Schema s, Guid mvoId, ConnectedSystemObjectStatus status) => new()
    {
        Id = Guid.NewGuid(), ConnectedSystemId = s.SystemId, TypeId = s.TypeId, MetaverseObjectId = mvoId,
        ExternalIdAttributeId = s.ObjectGuidAttributeId, SecondaryExternalIdAttributeId = s.DistinguishedNameAttributeId,
        Status = status, JoinType = ConnectedSystemObjectJoinType.Provisioned
    };

    /// <summary>An Update changing the display name and, with <paramref name="newDistinguishedName"/>, the distinguished name.</summary>
    private async Task SeedUpdateAsync(Schema s, SeededObject target, DateTime createdAt, string? newDistinguishedName)
    {
        var export = NewExport(s, target, createdAt, PendingExportChangeType.Update, hasUnresolvedReferences: false);
        export.AttributeValueChanges.Add(NewChange(s.DisplayNameAttributeId, PendingExportAttributeChangeType.Update, "Joe Bloggs (Finance)"));
        if (newDistinguishedName != null)
            export.AttributeValueChanges.Add(NewChange(s.DistinguishedNameAttributeId, PendingExportAttributeChangeType.Update, newDistinguishedName));
        await SaveExportAsync(export);
    }

    /// <summary>A Create writing the object's display name and the distinguished name it is created under.</summary>
    private async Task SeedCreateAsync(Schema s, SeededObject target, DateTime createdAt, string distinguishedName)
    {
        var export = NewExport(s, target, createdAt, PendingExportChangeType.Create, hasUnresolvedReferences: false);
        export.AttributeValueChanges.Add(NewChange(s.DisplayNameAttributeId, PendingExportAttributeChangeType.Add, "Joe Bloggs"));
        export.AttributeValueChanges.Add(NewChange(s.DistinguishedNameAttributeId, PendingExportAttributeChangeType.Add, distinguishedName));
        await SaveExportAsync(export);
    }

    /// <summary>An Update adding one member to a group, its reference awaiting resolution at export.</summary>
    private async Task<Guid> SeedMembershipUpdateAsync(Schema s, SeededObject group, DateTime createdAt, Guid memberMvoId)
    {
        var export = NewExport(s, group, createdAt, PendingExportChangeType.Update, hasUnresolvedReferences: true);
        var change = NewChange(s.MemberAttributeId, PendingExportAttributeChangeType.Add, null);
        change.UnresolvedReferenceValue = memberMvoId.ToString();
        export.AttributeValueChanges.Add(change);
        await SaveExportAsync(export);
        return export.Id;
    }

    private static PendingExport NewExport(Schema s, SeededObject target, DateTime createdAt, PendingExportChangeType changeType, bool hasUnresolvedReferences) => new()
    {
        Id = Guid.NewGuid(), ConnectedSystemId = s.SystemId, ConnectedSystemObjectId = target.CsoId, SourceMetaverseObjectId = target.MvoId,
        ChangeType = changeType, Status = PendingExportStatus.Pending, HasUnresolvedReferences = hasUnresolvedReferences, CreatedAt = createdAt
    };

    private static PendingExport WithChanges(PendingExport export, params PendingExportAttributeValueChange[] changes)
    {
        export.AttributeValueChanges.AddRange(changes);
        return export;
    }

    private static PendingExportAttributeValueChange NewChange(int attributeId, PendingExportAttributeChangeType changeType, string? value) => new()
    {
        Id = Guid.NewGuid(), AttributeId = attributeId, ChangeType = changeType, Status = PendingExportAttributeChangeStatus.Pending, StringValue = value
    };

    private async Task SaveExportAsync(PendingExport export)
    {
        await using var seed = NewContext();
        seed.Add(export);
        await seed.SaveChangesAsync();
    }
}
