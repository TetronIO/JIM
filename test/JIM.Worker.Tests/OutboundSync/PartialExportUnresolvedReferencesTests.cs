// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Models.Core;
using JIM.Models.Interfaces;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.PostgresData;
using JIM.Worker.Tests.Models;
using Microsoft.EntityFrameworkCore;
using MockQueryable.Moq;
using Moq;
using SyncRepository = JIM.InMemoryData.SyncRepository;

namespace JIM.Worker.Tests.OutboundSync;

/// <summary>
/// Issue #1398 (second half). A Pending Export whose references cannot all be resolved used to defer
/// whole, and silently: an account whose manager was out of scope for the rule was never created at
/// all, and nothing said so. Now the export writes what it can (a Create inserts the row without the
/// reference columns), keeps only the unresolved reference changes pending for the existing deferred
/// pass to fill in later, and tells the difference between a reference that is merely waiting for the
/// referenced object's anchor and one that can never resolve because the referenced object has no
/// Connected System Object in the target at all. The latter is surfaced per the Connected System's
/// Unresolved Reference Handling, exactly as the import side does.
/// </summary>
public class PartialExportUnresolvedReferencesTests
{
    private Mock<JimDbContext> MockJimDbContext { get; set; } = null!;
    private List<ConnectedSystem> ConnectedSystemsData { get; set; } = null!;
    private List<ConnectedSystemObject> ConnectedSystemObjectsData { get; set; } = null!;
    private List<ConnectedSystemObjectType> ConnectedSystemObjectTypesData { get; set; } = null!;
    private List<PendingExport> PendingExportsData { get; set; } = null!;
    private JimApplication Jim { get; set; } = null!;
    private SyncRepository SyncRepo { get; set; } = null!;
    private ConnectedSystem TargetSystem { get; set; } = null!;
    private ConnectedSystemObjectType TargetUserType { get; set; } = null!;
    private ConnectedSystemObjectTypeAttribute DisplayNameAttr { get; set; } = null!;
    private ConnectedSystemObjectTypeAttribute ManagerAttr { get; set; } = null!;
    private ConnectedSystemObjectTypeAttribute ObjectGuidAttr { get; set; } = null!;

    [TearDown]
    public void TearDown() => Jim?.Dispose();

    [SetUp]
    public void Setup()
    {
        TestUtilities.SetEnvironmentVariables();

        ConnectedSystemsData = TestUtilities.GetConnectedSystemData();
        ConnectedSystemObjectTypesData = TestUtilities.GetConnectedSystemObjectTypeData();
        ConnectedSystemObjectsData = TestUtilities.GetConnectedSystemObjectData();
        PendingExportsData = [];

        MockJimDbContext = new Mock<JimDbContext>();
        TestUtilities.SetUpEmptyConnectedSystemGraphMocks(MockJimDbContext);
        MockJimDbContext.Setup(m => m.ConnectedSystems).Returns(ConnectedSystemsData.BuildMockDbSet().Object);
        MockJimDbContext.Setup(m => m.ConnectedSystemObjectTypes).Returns(ConnectedSystemObjectTypesData.BuildMockDbSet().Object);
        MockJimDbContext.Setup(m => m.ConnectedSystemObjects).Returns(ConnectedSystemObjectsData.BuildMockDbSet().Object);
        MockJimDbContext.Setup(m => m.PendingExports).Returns(PendingExportsData.BuildMockDbSet().Object);

        SyncRepo = TestUtilities.CreateSyncRepository();
        Jim = new JimApplication(new PostgresDataRepository(MockJimDbContext.Object), syncRepository: SyncRepo);

        TargetSystem = ConnectedSystemsData.Single(s => s.Name == "Dummy Target System");
        TargetSystem.UnresolvedReferenceHandling = UnresolvedReferenceHandling.Error;
        TargetUserType = ConnectedSystemObjectTypesData.Single(t => t.Name == "TARGET_USER");
        DisplayNameAttr = TargetUserType.Attributes.Single(a => a.Name == MockTargetSystemAttributeNames.DisplayName.ToString());
        ManagerAttr = TargetUserType.Attributes.Single(a => a.Name == MockTargetSystemAttributeNames.Manager.ToString());
        ObjectGuidAttr = TargetUserType.Attributes.Single(a => a.Name == MockTargetSystemAttributeNames.ObjectGuid.ToString());
    }

    #region helpers

    /// <summary>A Metaverse Object with a display name, so the surfacing can name it.</summary>
    private MetaverseObject SeedMvo(string displayName)
    {
        var mvo = new MetaverseObject { Id = Guid.NewGuid(), CachedDisplayName = displayName };
        SyncRepo.SeedMetaverseObject(mvo);
        return mvo;
    }

    /// <summary>
    /// A Connected System Object in the target for <paramref name="mvo"/>: with an anchor when
    /// <paramref name="withAnchor"/> (the referenced object's export has been confirmed), or without one
    /// (its own Create has not gone yet).
    /// </summary>
    private ConnectedSystemObject SeedTargetCso(MetaverseObject mvo, bool withAnchor)
    {
        var cso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = TargetSystem.Id,
            ConnectedSystem = TargetSystem,
            Type = TargetUserType,
            TypeId = TargetUserType.Id,
            MetaverseObjectId = mvo.Id,
            Status = ConnectedSystemObjectStatus.Normal,
            AttributeValues = []
        };
        if (withAnchor)
        {
            cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
            {
                Id = Guid.NewGuid(),
                ConnectedSystemObject = cso,
                Attribute = ObjectGuidAttr,
                AttributeId = ObjectGuidAttr.Id,
                GuidValue = Guid.NewGuid()
            });
        }
        ConnectedSystemObjectsData.Add(cso);
        SyncRepo.SeedConnectedSystemObject(cso);
        return cso;
    }

    private ConnectedSystemObject NewProvisionedCso()
    {
        var cso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = TargetSystem.Id,
            ConnectedSystem = TargetSystem,
            Type = TargetUserType,
            TypeId = TargetUserType.Id,
            ExternalIdAttributeId = ObjectGuidAttr.Id,
            Status = ConnectedSystemObjectStatus.Normal,
            AttributeValues = []
        };
        ConnectedSystemObjectsData.Add(cso);
        SyncRepo.SeedConnectedSystemObject(cso);
        return cso;
    }

    private static PendingExportAttributeValueChange TextChange(ConnectedSystemObjectTypeAttribute attribute, string value) => new()
    {
        Id = Guid.NewGuid(),
        AttributeId = attribute.Id,
        Attribute = attribute,
        ChangeType = PendingExportAttributeChangeType.Update,
        StringValue = value,
        Status = PendingExportAttributeChangeStatus.Pending
    };

    private static PendingExportAttributeValueChange ReferenceChange(ConnectedSystemObjectTypeAttribute attribute, MetaverseObject referenced, PendingExportAttributeChangeType changeType = PendingExportAttributeChangeType.Update) => new()
    {
        Id = Guid.NewGuid(),
        AttributeId = attribute.Id,
        Attribute = attribute,
        ChangeType = changeType,
        UnresolvedReferenceValue = referenced.Id.ToString(),
        Status = PendingExportAttributeChangeStatus.Pending
    };

    private PendingExport SeedExport(ConnectedSystemObject cso, PendingExportChangeType changeType, params PendingExportAttributeValueChange[] changes)
    {
        var export = new PendingExport
        {
            Id = Guid.NewGuid(),
            ConnectedSystemObjectId = cso.Id,
            ConnectedSystemObject = cso,
            ConnectedSystemId = TargetSystem.Id,
            ConnectedSystem = TargetSystem,
            Status = PendingExportStatus.Pending,
            ChangeType = changeType,
            CreatedAt = DateTime.UtcNow,
            HasUnresolvedReferences = changes.Any(c => c.UnresolvedReferenceValue != null),
            AttributeValueChanges = changes.ToList()
        };
        foreach (var change in export.AttributeValueChanges)
            change.PendingExportId = export.Id;
        PendingExportsData.Add(export);
        SyncRepo.SeedPendingExport(export);
        return export;
    }

    /// <summary>
    /// A connector that records exactly what it was handed (a copy per call, since the caller may go on
    /// to change the instance) and answers success, returning a generated anchor for a Create.
    /// </summary>
    private static (Mock<IConnector> Connector, List<List<(PendingExport Export, List<PendingExportAttributeValueChange> Changes)>> Calls) RecordingConnector(
        Func<PendingExport, ConnectedSystemExportResult>? resultFor = null)
    {
        var calls = new List<List<(PendingExport, List<PendingExportAttributeValueChange>)>>();
        var connector = new Mock<IConnector>();
        connector.Setup(c => c.Name).Returns("Test Connector");
        connector.As<IConnectorExportUsingCalls>()
            .Setup(c => c.ExportAsync(It.IsAny<List<PendingExport>>(), It.IsAny<CancellationToken>(), It.IsAny<IConnectorProgress>()))
            .ReturnsAsync((List<PendingExport> exports, CancellationToken _, IConnectorProgress _) =>
            {
                calls.Add(exports.Select(e => (e, e.AttributeValueChanges.ToList())).ToList());
                return exports.Select(e => resultFor?.Invoke(e)
                    ?? (e.ChangeType == PendingExportChangeType.Create
                        ? ConnectedSystemExportResult.Succeeded(Guid.NewGuid().ToString())
                        : ConnectedSystemExportResult.Succeeded())).ToList();
            });
        return (connector, calls);
    }

    private Task<ExportExecutionResult> RunExportAsync(IConnector connector) =>
        Jim.ExportExecution.ExecuteExportsAsync(TargetSystem, connector, SyncRunMode.PreviewAndSync);

    #endregion

    [Test]
    public async Task Create_ManagerHasNoObjectInTarget_InsertsRowWithoutTheReferenceAndKeepsItPendingAsync()
    {
        // The Scenario 16 shape: the manager is disabled, so out of scope for the rule and never
        // provisioned into this system. The employee's row must still be created.
        var manager = SeedMvo("Disabled Manager");
        var cso = NewProvisionedCso();
        var nameChange = TextChange(DisplayNameAttr, "Employee Sixteen");
        var managerChange = ReferenceChange(ManagerAttr, manager);
        var export = SeedExport(cso, PendingExportChangeType.Create, nameChange, managerChange);
        var (connector, calls) = RecordingConnector();

        var result = await RunExportAsync(connector.Object);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(calls, Has.Count.EqualTo(1), "One connector call: the row is written now.");
            Assert.That(calls[0][0].Export.ChangeType, Is.EqualTo(PendingExportChangeType.Create), "The connector saw a Create.");
            Assert.That(calls[0][0].Changes.Select(c => c.Id), Is.EquivalentTo(new[] { nameChange.Id }),
                "The connector was handed only the change it can write; the unresolved reference stays behind.");

            Assert.That(export.Status, Is.EqualTo(PendingExportStatus.Pending), "The export is not finished: a reference is still owed.");
            Assert.That(export.HasUnresolvedReferences, Is.True);
            Assert.That(export.ChangeType, Is.EqualTo(PendingExportChangeType.Update),
                "The row exists now, so anything sent later must be an Update, never a second insert.");
            Assert.That(export.NextRetryAt, Is.Not.Null, "Retried on the deferred cadence.");
            Assert.That(export.ErrorCount, Is.Zero, "Nothing failed.");
            Assert.That(nameChange.Status, Is.EqualTo(PendingExportAttributeChangeStatus.ExportedPendingConfirmation));
            Assert.That(managerChange.Status, Is.EqualTo(PendingExportAttributeChangeStatus.Pending));
            Assert.That(managerChange.UnresolvedReferenceValue, Is.EqualTo(manager.Id.ToString()), "The reference change is untouched.");
            Assert.That(export.AttributeValueChanges, Has.Count.EqualTo(2), "Both changes remain on the export until the confirming import removes the written one.");

            Assert.That(cso.AttributeValues.Any(av => av.AttributeId == ObjectGuidAttr.Id), Is.True,
                "The Create's returned anchor lands on the Connected System Object as it does for any Create.");
            Assert.That(result.SuccessCount, Is.EqualTo(1));
            Assert.That(result.DeferredCount, Is.Zero, "Deferred counts exports that wrote nothing.");
            Assert.That(result.PartiallyExportedCount, Is.EqualTo(1));
        }
    }

    [Test]
    public async Task Create_ManagerObjectExistsButHasNoAnchorYet_WritesRowAndIsSilentAsync()
    {
        // The manager's own Create has not gone yet: its Connected System Object exists in JIM but
        // carries no anchor. That is the ordinary ordering case, not a problem to report.
        var manager = SeedMvo("Manager Awaiting Anchor");
        SeedTargetCso(manager, withAnchor: false);
        var cso = NewProvisionedCso();
        var export = SeedExport(cso, PendingExportChangeType.Create, TextChange(DisplayNameAttr, "Report"), ReferenceChange(ManagerAttr, manager));
        var (connector, calls) = RecordingConnector();

        var result = await RunExportAsync(connector.Object);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(calls, Has.Count.EqualTo(1));
            Assert.That(export.Status, Is.EqualTo(PendingExportStatus.Pending));
            Assert.That(export.HasUnresolvedReferences, Is.True);
            Assert.That(result.UnresolvableReferenceCount, Is.Zero, "A reference waiting on an anchor is pending, not unresolvable.");
            var item = result.ProcessedExportItems.Single();
            Assert.That(item.Succeeded, Is.True);
            Assert.That(item.UnresolvedReferenceMessage, Is.Null, "Nothing to warn about.");
        }
    }

    [Test]
    public async Task Create_ManagerHasNoObjectInTarget_HandlingError_ItemNamesTheAttributeAndTheReferencedObjectAsync()
    {
        TargetSystem.UnresolvedReferenceHandling = UnresolvedReferenceHandling.Error;
        var manager = SeedMvo("Disabled Manager");
        var cso = NewProvisionedCso();
        SeedExport(cso, PendingExportChangeType.Create, TextChange(DisplayNameAttr, "Report"), ReferenceChange(ManagerAttr, manager));
        var (connector, _) = RecordingConnector();

        var result = await RunExportAsync(connector.Object);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.UnresolvableReferenceCount, Is.EqualTo(1));
            var item = result.ProcessedExportItems.Single();
            Assert.That(item.Succeeded, Is.True, "The row was written; the item reports the write and the outstanding reference on one item, as the import side does.");
            Assert.That(item.UnresolvedReferenceMessage, Does.Contain(ManagerAttr.Name).And.Contain("Disabled Manager"),
                "The message names the attribute and the referenced object.");
        }
    }

    [TestCase(UnresolvedReferenceHandling.Warn)]
    [TestCase(UnresolvedReferenceHandling.Ignore)]
    public async Task Create_ManagerHasNoObjectInTarget_HandlingWarnOrIgnore_CountsWithoutAnItemMessageAsync(UnresolvedReferenceHandling handling)
    {
        TargetSystem.UnresolvedReferenceHandling = handling;
        var manager = SeedMvo("Disabled Manager");
        var cso = NewProvisionedCso();
        SeedExport(cso, PendingExportChangeType.Create, TextChange(DisplayNameAttr, "Report"), ReferenceChange(ManagerAttr, manager));
        var (connector, _) = RecordingConnector();

        var result = await RunExportAsync(connector.Object);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.UnresolvableReferenceCount, Is.EqualTo(1), "The count is a fact for the Warn summary; the mode decides what is done with it.");
            Assert.That(result.ProcessedExportItems.Single().UnresolvedReferenceMessage, Is.Null,
                "Only Error mode marks the referrer's item.");
        }
    }

    [Test]
    public async Task Update_MultiValuedReference_ResolvableMembersWriteNowAndTheRestStayPendingAsync()
    {
        // A group gaining three members: two are provisioned with anchors, one has no object in the
        // target. The two go now; the third waits, and only the third.
        var memberAttr = new ConnectedSystemObjectTypeAttribute { Id = 900, Name = "member", Type = AttributeDataType.Reference, AttributePlurality = AttributePlurality.MultiValued };
        var alice = SeedMvo("Alice");
        var bob = SeedMvo("Bob");
        var carol = SeedMvo("Carol (not provisioned)");
        var aliceCso = SeedTargetCso(alice, withAnchor: true);
        var bobCso = SeedTargetCso(bob, withAnchor: true);
        var group = NewProvisionedCso();
        var aliceChange = ReferenceChange(memberAttr, alice, PendingExportAttributeChangeType.Add);
        var bobChange = ReferenceChange(memberAttr, bob, PendingExportAttributeChangeType.Add);
        var carolChange = ReferenceChange(memberAttr, carol, PendingExportAttributeChangeType.Add);
        var export = SeedExport(group, PendingExportChangeType.Update, aliceChange, bobChange, carolChange);
        var (connector, calls) = RecordingConnector();

        await RunExportAsync(connector.Object);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(calls, Has.Count.EqualTo(1));
            Assert.That(calls[0][0].Changes.Select(c => c.Id), Is.EquivalentTo(new[] { aliceChange.Id, bobChange.Id }));
            Assert.That(aliceChange.StringValue, Is.EqualTo(aliceCso.AttributeValues.Single().GuidValue.ToString()), "Resolved to the member's anchor.");
            Assert.That(aliceChange.ResolvedReferenceCsoId, Is.EqualTo(aliceCso.Id));
            Assert.That(bobChange.ResolvedReferenceCsoId, Is.EqualTo(bobCso.Id));
            Assert.That(aliceChange.Status, Is.EqualTo(PendingExportAttributeChangeStatus.ExportedPendingConfirmation));
            Assert.That(bobChange.Status, Is.EqualTo(PendingExportAttributeChangeStatus.ExportedPendingConfirmation));
            Assert.That(carolChange.Status, Is.EqualTo(PendingExportAttributeChangeStatus.Pending));
            Assert.That(carolChange.UnresolvedReferenceValue, Is.EqualTo(carol.Id.ToString()));
            Assert.That(export.Status, Is.EqualTo(PendingExportStatus.Pending));
            Assert.That(export.HasUnresolvedReferences, Is.True);
            Assert.That(export.ChangeType, Is.EqualTo(PendingExportChangeType.Update));
        }
    }

    [Test]
    public async Task Create_OnlyUnresolvedChanges_WritesNothingAndSurfacesTheUnresolvableReferenceAsync()
    {
        // Nothing can be written, so nothing is: the export defers whole as before, but the referrer
        // gets a deferred item naming the reference that can never resolve (Error mode).
        var manager = SeedMvo("Disabled Manager");
        var cso = NewProvisionedCso();
        var export = SeedExport(cso, PendingExportChangeType.Create, ReferenceChange(ManagerAttr, manager));
        var (connector, calls) = RecordingConnector();

        var result = await RunExportAsync(connector.Object);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(calls, Is.Empty, "There was nothing to hand the connector.");
            Assert.That(export.Status, Is.EqualTo(PendingExportStatus.Pending));
            Assert.That(export.ChangeType, Is.EqualTo(PendingExportChangeType.Create), "Nothing was inserted, so it is still a Create.");
            Assert.That(result.DeferredCount, Is.EqualTo(1));
            Assert.That(result.SuccessCount, Is.Zero);
            var item = result.ProcessedExportItems.Single();
            Assert.That(item.Deferred, Is.True);
            Assert.That(item.PendingExportId, Is.EqualTo(export.Id));
            Assert.That(item.UnresolvedReferenceMessage, Does.Contain("Disabled Manager"));
        }
    }

    [Test]
    public async Task Create_OnlyAwaitingAnchorChanges_WritesNothingAndRaisesNoItemAsync()
    {
        // The wholly deferred, merely-waiting case is the pre-existing behaviour and stays quiet.
        var manager = SeedMvo("Manager Awaiting Anchor");
        SeedTargetCso(manager, withAnchor: false);
        var cso = NewProvisionedCso();
        SeedExport(cso, PendingExportChangeType.Create, ReferenceChange(ManagerAttr, manager));
        var (connector, calls) = RecordingConnector();

        var result = await RunExportAsync(connector.Object);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(calls, Is.Empty);
            Assert.That(result.DeferredCount, Is.EqualTo(1));
            Assert.That(result.ProcessedExportItems, Is.Empty, "A reference that is only waiting raises nothing.");
            Assert.That(result.UnresolvableReferenceCount, Is.Zero);
        }
    }

    [Test]
    public async Task PartialCreate_ConnectorRefusesTheRow_FailsTheObjectWithAnOrdinaryErrorAsync()
    {
        // The reference column is NOT NULL, say: the connector reports the failure and the export is
        // failed like any other, retry and all. It is not silently deferred.
        var manager = SeedMvo("Disabled Manager");
        var cso = NewProvisionedCso();
        var nameChange = TextChange(DisplayNameAttr, "Report");
        var export = SeedExport(cso, PendingExportChangeType.Create, nameChange, ReferenceChange(ManagerAttr, manager));
        var (connector, _) = RecordingConnector(_ => ConnectedSystemExportResult.Failed("Cannot insert the value NULL into column 'MANAGER_ID'"));

        var result = await RunExportAsync(connector.Object);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.FailedCount, Is.EqualTo(1));
            Assert.That(export.ErrorCount, Is.EqualTo(1));
            Assert.That(export.LastErrorMessage, Does.Contain("MANAGER_ID"));
            Assert.That(export.ChangeType, Is.EqualTo(PendingExportChangeType.Create), "The row was not inserted, so the next attempt is still a Create.");
            Assert.That(nameChange.Status, Is.EqualTo(PendingExportAttributeChangeStatus.Pending), "Nothing was written.");
            var item = result.ProcessedExportItems.Single();
            Assert.That(item.Succeeded, Is.False);
            Assert.That(item.ErrorMessage, Does.Contain("MANAGER_ID"));
        }
    }

    [Test]
    public async Task PartialCreate_OptimisticApply_DoesNotStampTheUnresolvedReferenceOnTheObjectAsync()
    {
        var manager = SeedMvo("Disabled Manager");
        var cso = NewProvisionedCso();
        SeedExport(cso, PendingExportChangeType.Create, TextChange(DisplayNameAttr, "Report"), ReferenceChange(ManagerAttr, manager));
        var (connector, _) = RecordingConnector();

        await RunExportAsync(connector.Object);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cso.AttributeValues.Any(av => av.AttributeId == DisplayNameAttr.Id && av.StringValue == "Report"), Is.True,
                "The written value is applied optimistically as usual.");
            Assert.That(cso.AttributeValues.Any(av => av.AttributeId == ManagerAttr.Id), Is.False,
                "The reference was not written, so it must not appear on the object as though it had been.");
        }
    }

    [Test]
    public async Task PartialCreate_ThenTheReferenceResolves_NextRunSendsTheRemainderAsAnUpdateAsync()
    {
        var manager = SeedMvo("Late Manager");
        var cso = NewProvisionedCso();
        var nameChange = TextChange(DisplayNameAttr, "Report");
        var managerChange = ReferenceChange(ManagerAttr, manager);
        var export = SeedExport(cso, PendingExportChangeType.Create, nameChange, managerChange);
        var (connector, calls) = RecordingConnector();

        await RunExportAsync(connector.Object);
        Assume.That(export.ChangeType, Is.EqualTo(PendingExportChangeType.Update));

        // The manager is provisioned and confirmed between runs; the deferred cadence has elapsed.
        var managerCso = SeedTargetCso(manager, withAnchor: true);
        export.NextRetryAt = DateTime.UtcNow.AddMinutes(-1);

        await RunExportAsync(connector.Object);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(calls, Has.Count.EqualTo(2));
            var second = calls[1].Single();
            Assert.That(second.Export.ChangeType, Is.EqualTo(PendingExportChangeType.Update));
            Assert.That(second.Changes.Select(c => c.Id), Is.EquivalentTo(new[] { managerChange.Id }),
                "The second write carries the reference only; the name was written last time and awaits confirmation, not re-sending.");
            Assert.That(managerChange.StringValue, Is.EqualTo(managerCso.AttributeValues.Single().GuidValue.ToString()));
            Assert.That(managerChange.Status, Is.EqualTo(PendingExportAttributeChangeStatus.ExportedPendingConfirmation));
            Assert.That(export.Status, Is.EqualTo(PendingExportStatus.Exported));
            Assert.That(export.HasUnresolvedReferences, Is.False);
        }
    }
}
