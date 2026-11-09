// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Application.Servers;
using JIM.Models.Core;
using JIM.Models.Interfaces;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.PostgresData;
using MockQueryable.Moq;
using Moq;
using SyncRepository = JIM.InMemoryData.SyncRepository;

namespace JIM.Worker.Tests.OutboundSync;

/// <summary>
/// Exporting a reference recall removal that names the leaver's Connected System Object (#2809). The leaver's own object is
/// deprovisioned by the same leaver run, so by the time the group's removal is exported it may be gone, or going in the
/// same Export. The removal must still send the distinguished name captured before deletion, export as any other change,
/// and update the group's values as the confirming import will find them; naming an object that no longer exists must
/// neither fail it nor hold it back.
/// </summary>
public class RecallRemovalExportTests
{
    private const string LeaverDn = "uid=lena.leaver,ou=People,dc=glitterband,dc=local";
    private const string AliceDn = "uid=alice,ou=People,dc=glitterband,dc=local";

    private Mock<JimDbContext> MockJimDbContext { get; set; } = null!;
    private List<ConnectedSystem> ConnectedSystemsData { get; set; } = null!;
    private List<ConnectedSystemObject> ConnectedSystemObjectsData { get; set; } = null!;
    private List<ConnectedSystemObjectType> ConnectedSystemObjectTypesData { get; set; } = null!;
    private List<PendingExport> PendingExportsData { get; set; } = null!;
    private JimApplication Jim { get; set; } = null!;
    private SyncRepository SyncRepo { get; set; } = null!;
    private ConnectedSystem TargetSystem { get; set; } = null!;
    private ConnectedSystemObjectType TargetType { get; set; } = null!;
    private ConnectedSystemObjectTypeAttribute MemberAttr { get; set; } = null!;

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
        TargetType = ConnectedSystemObjectTypesData.Single(t => t.Name == "TARGET_USER");
        MemberAttr = new ConnectedSystemObjectTypeAttribute
        {
            Id = 900, Name = "member", Type = AttributeDataType.Reference, AttributePlurality = AttributePlurality.MultiValued,
            ConnectedSystemObjectType = TargetType, Selected = true
        };
        TargetType.Attributes.Add(MemberAttr);
    }

    #region helpers

    private ConnectedSystemObject SeedCso()
    {
        var cso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = TargetSystem.Id,
            ConnectedSystem = TargetSystem,
            Type = TargetType,
            TypeId = TargetType.Id,
            Status = ConnectedSystemObjectStatus.Normal,
            AttributeValues = []
        };
        ConnectedSystemObjectsData.Add(cso);
        SyncRepo.SeedConnectedSystemObject(cso);
        return cso;
    }

    private void AddMemberRow(ConnectedSystemObject group, Guid? referenceValueId, string dn) =>
        group.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(), ConnectedSystemObject = group, Attribute = MemberAttr, AttributeId = MemberAttr.Id,
            ReferenceValueId = referenceValueId, UnresolvedReferenceValue = dn
        });

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
            AttributeValueChanges = new(changes)
        };
        foreach (var change in export.AttributeValueChanges)
            change.PendingExportId = export.Id;
        PendingExportsData.Add(export);
        SyncRepo.SeedPendingExport(export);
        return export;
    }

    /// <summary>
    /// The removal reference recall stages for the leaver, as the recall decision makes it.
    /// </summary>
    private PendingExportAttributeValueChange RecallRemoval(Guid leaverCsoId)
    {
        var flow = new ReferenceRecallDirectFlow
        {
            ExportRule = new SyncRule { Id = 1, Name = "Group Export", ConnectedSystemId = TargetSystem.Id, Direction = SyncRuleDirection.Export },
            TargetAttribute = MemberAttr,
            SourcePlurality = AttributePlurality.MultiValued
        };
        var removal = new SyncEngine().DecideRecallRemovalChange(flow, new ResolvedRecallReference(leaverCsoId, LeaverDn))!;

        // Unattributed, so the authority check every export starts with (#1885) leaves it alone without a joined group and
        // an export rule to authorise it: that check never reads the object a change names, and is not under test here.
        removal.SyncRuleId = null;
        removal.SyncRuleName = null;
        return removal;
    }

    /// <summary>
    /// A connector that records each export and the changes it was handed (copied, since the caller may change the
    /// instance afterwards) and answers success.
    /// </summary>
    private static (Mock<IConnector> Connector, List<(PendingExport Export, List<(Guid Id, string? StringValue, Guid? NamedObject)> Changes)> Sent) RecordingConnector()
    {
        var sent = new List<(PendingExport, List<(Guid, string?, Guid?)>)>();
        var connector = new Mock<IConnector>();
        connector.Setup(c => c.Name).Returns("Test Connector");
        connector.As<IConnectorExportUsingCalls>()
            .Setup(c => c.ExportAsync(It.IsAny<List<PendingExport>>(), It.IsAny<CancellationToken>(), It.IsAny<IConnectorProgress>()))
            .ReturnsAsync((List<PendingExport> exports, CancellationToken _, IConnectorProgress _) =>
            {
                sent.AddRange(exports.Select(e => (e, e.AttributeValueChanges.Select(c => (c.Id, c.StringValue, c.ResolvedReferenceCsoId)).ToList())));
                return exports.Select(_ => ConnectedSystemExportResult.Succeeded()).ToList();
            });
        return (connector, sent);
    }

    #endregion

    /// <summary>
    /// The leaver's object is already gone: deleting it left the group's row unresolved, keeping the DN. The removal names
    /// an object that no longer exists, sends the captured DN, and takes the row off the group, as the confirming import
    /// will see it.
    /// </summary>
    [Test]
    public async Task ExecuteExportsAsync_RecallRemovalNamingADeletedObject_SendsTheCapturedDnAndTakesTheRowOffAsync()
    {
        var deletedLeaverCsoId = Guid.NewGuid();
        var aliceCso = SeedCso();
        var group = SeedCso();
        AddMemberRow(group, aliceCso.Id, AliceDn);
        AddMemberRow(group, null, LeaverDn.ToUpperInvariant());
        var removal = RecallRemoval(deletedLeaverCsoId);
        var export = SeedExport(group, PendingExportChangeType.Update, removal);
        var (connector, sent) = RecordingConnector();

        var result = await Jim.ExportExecution.ExecuteExportsAsync(TargetSystem, connector.Object, SyncRunMode.PreviewAndSync);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(sent.Select(s => s.Export.Id), Is.EquivalentTo(new[] { export.Id }));
            Assert.That(sent.Single().Changes, Is.EquivalentTo(new[] { (removal.Id, (string?)LeaverDn, (Guid?)deletedLeaverCsoId) }),
                "The connector is handed the DN captured before deletion, naming the leaver's object");
            Assert.That(result.SuccessCount, Is.EqualTo(1));
            Assert.That(result.DeferredCount, Is.Zero, "Naming an object that no longer exists holds nothing back");
            Assert.That(export.ErrorCount, Is.Zero);
            Assert.That(removal.Status, Is.EqualTo(PendingExportAttributeChangeStatus.ExportedPendingConfirmation));
            Assert.That(group.AttributeValues.Where(av => av.AttributeId == MemberAttr.Id).Select(av => av.UnresolvedReferenceValue),
                Is.EquivalentTo(new[] { AliceDn }), "The unresolved row carrying the leaver's DN, in any case, comes off the group");
        }
    }

    /// <summary>
    /// The leaver's object is deprovisioned in the same Export as the group's removal. Both go; the removal sends the
    /// captured DN, and the group's row resolved to the leaver comes off it, while a stale row resolved to another object
    /// that happens to keep the leaver's DN stays: it names that other object.
    /// </summary>
    [Test]
    public async Task ExecuteExportsAsync_RecallRemovalWithTheLeaverDeprovisionedInTheSameExport_BothSucceedAsync()
    {
        var leaverCso = SeedCso();
        var otherCso = SeedCso();
        var aliceCso = SeedCso();
        var group = SeedCso();
        AddMemberRow(group, aliceCso.Id, AliceDn);
        AddMemberRow(group, leaverCso.Id, LeaverDn);
        AddMemberRow(group, otherCso.Id, LeaverDn);
        var deprovision = SeedExport(leaverCso, PendingExportChangeType.Delete);
        var removal = RecallRemoval(leaverCso.Id);
        var export = SeedExport(group, PendingExportChangeType.Update, removal);
        var (connector, sent) = RecordingConnector();

        var result = await Jim.ExportExecution.ExecuteExportsAsync(TargetSystem, connector.Object, SyncRunMode.PreviewAndSync);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(sent.Select(s => s.Export.Id), Is.EquivalentTo(new[] { export.Id, deprovision.Id }));
            Assert.That(sent.Single(s => s.Export.Id == export.Id).Changes,
                Is.EquivalentTo(new[] { (removal.Id, (string?)LeaverDn, (Guid?)leaverCso.Id) }));
            Assert.That(result.SuccessCount, Is.EqualTo(2));
            Assert.That(export.ErrorCount, Is.Zero);
            Assert.That(removal.Status, Is.EqualTo(PendingExportAttributeChangeStatus.ExportedPendingConfirmation));
            Assert.That(group.AttributeValues.Where(av => av.AttributeId == MemberAttr.Id).Select(av => av.ReferenceValueId),
                Is.EquivalentTo(new Guid?[] { aliceCso.Id, otherCso.Id }),
                "The leaver's row comes off; the stale row names another object and stays");
        }
    }
}
