// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Interfaces;
using JIM.Models.Logic;
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
/// Run Profile Safeguards (#1618, Layer 1): the export server honours the Run Profile's Max
/// creates/updates/deletes limits, whether an export is attempted in the first (immediate) pass, the
/// deferred-reference pass, or via a files-based connector. A withheld export is left exactly as
/// found: still Pending, given no execution item, untouched.
/// </summary>
public class ExportChangeLimitTests
{
    #region accessors
    private Mock<JimDbContext> MockJimDbContext { get; set; } = null!;
    private List<Activity> ActivitiesData { get; set; } = null!;
    private Mock<DbSet<Activity>> MockDbSetActivities { get; set; } = null!;
    private List<ConnectedSystem> ConnectedSystemsData { get; set; } = null!;
    private List<ConnectedSystemObject> ConnectedSystemObjectsData { get; set; } = null!;
    private Mock<DbSet<ConnectedSystemObject>> MockDbSetConnectedSystemObjects { get; set; } = null!;
    private List<ConnectedSystemRunProfile> ConnectedSystemRunProfilesData { get; set; } = null!;
    private Mock<DbSet<ConnectedSystemRunProfile>> MockDbSetConnectedSystemRunProfiles { get; set; } = null!;
    private Mock<DbSet<ConnectedSystem>> MockDbSetConnectedSystems { get; set; } = null!;
    private List<ConnectedSystemObjectType> ConnectedSystemObjectTypesData { get; set; } = null!;
    private Mock<DbSet<ConnectedSystemObjectType>> MockDbSetConnectedSystemObjectTypes { get; set; } = null!;
    private List<ConnectedSystemObjectTypeAttribute> ConnectedSystemAttributesData { get; set; } = null!;
    private Mock<DbSet<ConnectedSystemObjectTypeAttribute>> MockDbSetConnectedSystemAttributes { get; set; } = null!;
    private List<ConnectedSystemPartition> ConnectedSystemPartitionsData { get; set; } = null!;
    private Mock<DbSet<ConnectedSystemPartition>> MockDbSetConnectedSystemPartitions { get; set; } = null!;
    private List<PendingExport> PendingExportsData { get; set; } = null!;
    private Mock<DbSet<PendingExport>> MockDbSetPendingExports { get; set; } = null!;
    private List<MetaverseObjectType> MetaverseObjectTypesData { get; set; } = null!;
    private Mock<DbSet<MetaverseObjectType>> MockDbSetMetaverseObjectTypes { get; set; } = null!;
    private List<MetaverseObject> MetaverseObjectsData { get; set; } = null!;
    private Mock<DbSet<MetaverseObject>> MockDbSetMetaverseObjects { get; set; } = null!;
    private List<SyncRule> SyncRulesData { get; set; } = null!;
    private Mock<DbSet<SyncRule>> MockDbSetSyncRules { get; set; } = null!;
    private JimApplication Jim { get; set; } = null!;
    private SyncRepository SyncRepo { get; set; } = null!;
    #endregion

    [TearDown]
    public void TearDown()
    {
        Jim?.Dispose();
    }

    [SetUp]
    public void Setup()
    {
        TestUtilities.SetEnvironmentVariables();

        ConnectedSystemRunProfilesData = TestUtilities.GetConnectedSystemRunProfileData();
        MockDbSetConnectedSystemRunProfiles = ConnectedSystemRunProfilesData.BuildMockDbSet();

        var exportRunProfile = ConnectedSystemRunProfilesData.Single(rp => rp.Name == "Dummy Target System Export");
        ActivitiesData = TestUtilities.GetActivityData(exportRunProfile.RunType, exportRunProfile.Id);
        MockDbSetActivities = ActivitiesData.BuildMockDbSet();

        ConnectedSystemsData = TestUtilities.GetConnectedSystemData();
        MockDbSetConnectedSystems = ConnectedSystemsData.BuildMockDbSet();

        ConnectedSystemObjectTypesData = TestUtilities.GetConnectedSystemObjectTypeData();
        MockDbSetConnectedSystemObjectTypes = ConnectedSystemObjectTypesData.BuildMockDbSet();

        ConnectedSystemAttributesData = ConnectedSystemObjectTypesData.SelectMany(t => t.Attributes).ToList();
        MockDbSetConnectedSystemAttributes = ConnectedSystemAttributesData.BuildMockDbSet();

        ConnectedSystemObjectsData = TestUtilities.GetConnectedSystemObjectData();
        MockDbSetConnectedSystemObjects = ConnectedSystemObjectsData.BuildMockDbSet();

        ConnectedSystemPartitionsData = TestUtilities.GetConnectedSystemPartitionData();
        MockDbSetConnectedSystemPartitions = ConnectedSystemPartitionsData.BuildMockDbSet();

        PendingExportsData = new List<PendingExport>();
        MockDbSetPendingExports = PendingExportsData.BuildMockDbSet();

        MetaverseObjectTypesData = TestUtilities.GetMetaverseObjectTypeData();
        MockDbSetMetaverseObjectTypes = MetaverseObjectTypesData.BuildMockDbSet();

        MetaverseObjectsData = TestUtilities.GetMetaverseObjectData();
        MockDbSetMetaverseObjects = MetaverseObjectsData.BuildMockDbSet();

        SyncRulesData = TestUtilities.GetSyncRuleData();
        MockDbSetSyncRules = SyncRulesData.BuildMockDbSet();

        MockJimDbContext = new Mock<JimDbContext>();
        TestUtilities.SetUpEmptyConnectedSystemGraphMocks(MockJimDbContext);
        MockJimDbContext.Setup(m => m.Activities).Returns(MockDbSetActivities.Object);
        MockJimDbContext.Setup(m => m.ConnectedSystemAttributes).Returns(MockDbSetConnectedSystemAttributes.Object);
        MockJimDbContext.Setup(m => m.ConnectedSystemObjectTypes).Returns(MockDbSetConnectedSystemObjectTypes.Object);
        MockJimDbContext.Setup(m => m.ConnectedSystemObjects).Returns(MockDbSetConnectedSystemObjects.Object);
        MockJimDbContext.Setup(m => m.ConnectedSystemPartitions).Returns(MockDbSetConnectedSystemPartitions.Object);
        MockJimDbContext.Setup(m => m.ConnectedSystemRunProfiles).Returns(MockDbSetConnectedSystemRunProfiles.Object);
        MockJimDbContext.Setup(m => m.ConnectedSystems).Returns(MockDbSetConnectedSystems.Object);
        MockJimDbContext.Setup(m => m.MetaverseObjectTypes).Returns(MockDbSetMetaverseObjectTypes.Object);
        MockJimDbContext.Setup(m => m.MetaverseObjects).Returns(MockDbSetMetaverseObjects.Object);
        MockJimDbContext.Setup(m => m.PendingExports).Returns(MockDbSetPendingExports.Object);
        MockJimDbContext.Setup(m => m.SyncRules).Returns(MockDbSetSyncRules.Object);
        MockJimDbContext.Setup(m => m.ServiceSettingItems).Returns(new List<ServiceSetting>().BuildMockDbSet().Object);

        SyncRepo = TestUtilities.CreateSyncRepository(activity: ActivitiesData.First());
        Jim = new JimApplication(new PostgresDataRepository(MockJimDbContext.Object), syncRepository: SyncRepo);
    }

    private PendingExport CreateSeededExport(ConnectedSystem targetSystem, ConnectedSystemObjectType type,
        PendingExportChangeType changeType, DateTime createdAt,
        List<PendingExportAttributeValueChange>? attributeValueChanges = null, bool hasUnresolvedReferences = false)
    {
        var cso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = targetSystem.Id,
            Type = type,
            TypeId = type.Id,
            AttributeValues = new List<ConnectedSystemObjectAttributeValue>()
        };
        ConnectedSystemObjectsData.Add(cso);
        SyncRepo.SeedConnectedSystemObject(cso);

        var pendingExport = new PendingExport
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = targetSystem.Id,
            ConnectedSystem = targetSystem,
            ConnectedSystemObject = cso,
            ConnectedSystemObjectId = cso.Id,
            Status = PendingExportStatus.Pending,
            ChangeType = changeType,
            CreatedAt = createdAt,
            HasUnresolvedReferences = hasUnresolvedReferences,
            MaxRetries = 3,
            AttributeValueChanges = attributeValueChanges ?? new List<PendingExportAttributeValueChange>()
        };
        PendingExportsData.Add(pendingExport);
        SyncRepo.SeedPendingExport(pendingExport);
        return pendingExport;
    }

    private static PendingExportAttributeValueChange BuildUpdateChange(ConnectedSystemObjectTypeAttribute attribute, string value) => new()
    {
        Id = Guid.NewGuid(),
        ChangeType = PendingExportAttributeChangeType.Update,
        AttributeId = attribute.Id,
        Attribute = attribute,
        StringValue = value,
        Status = PendingExportAttributeChangeStatus.Pending
    };

    private static Mock<IConnector> CreateSucceedingCallsConnector()
    {
        var mockConnector = new Mock<IConnector>();
        var mockExportConnector = mockConnector.As<IConnectorExportUsingCalls>();
        mockConnector.Setup(c => c.Name).Returns("Test Connector");
        mockExportConnector.Setup(c => c.ExportAsync(It.IsAny<IList<PendingExport>>(), It.IsAny<CancellationToken>(), It.IsAny<IConnectorProgress>()))
            .ReturnsAsync((IList<PendingExport> exports, CancellationToken _, IConnectorProgress _) =>
                exports.Select(_ => ConnectedSystemExportResult.Succeeded()).ToList());
        return mockConnector;
    }

    [Test]
    public async Task ExecuteExportsAsync_MaxDeletesBelowQueue_ExcessDeletesRemainPendingUntouchedAsync()
    {
        var targetSystem = ConnectedSystemsData.Single(s => s.Name == "Dummy Target System");
        var targetUserType = ConnectedSystemObjectTypesData.Single(t => t.Name == "TARGET_USER");
        var displayNameAttr = targetUserType.Attributes.Single(a => a.Name == MockTargetSystemAttributeNames.DisplayName.ToString());
        var baseTime = DateTime.UtcNow.AddMinutes(-10);

        var deletes = Enumerable.Range(0, 5)
            .Select(i => CreateSeededExport(targetSystem, targetUserType, PendingExportChangeType.Delete, baseTime.AddSeconds(i)))
            .ToList();
        var creates = Enumerable.Range(0, 2)
            .Select(i => CreateSeededExport(targetSystem, targetUserType, PendingExportChangeType.Create, baseTime.AddSeconds(10 + i)))
            .ToList();
        var updates = Enumerable.Range(0, 2)
            .Select(i => CreateSeededExport(targetSystem, targetUserType, PendingExportChangeType.Update, baseTime.AddSeconds(20 + i),
                attributeValueChanges: [BuildUpdateChange(displayNameAttr, $"Name {i}")]))
            .ToList();

        var mockConnector = CreateSucceedingCallsConnector();
        var options = new ExportExecutionOptions { MaxDeletes = 2 };

        var result = await Jim.ExportExecution.ExecuteExportsAsync(
            targetSystem, mockConnector.Object, SyncRunMode.PreviewAndSync, options, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.DeletesWithheld, Is.EqualTo(3));
            Assert.That(result.CreatesWithheld, Is.EqualTo(0));
            Assert.That(result.UpdatesWithheld, Is.EqualTo(0));
            Assert.That(result.SuccessCount, Is.EqualTo(2 + 2 + 2), "2 deletes + 2 creates + 2 updates attempted and succeeded");
        }

        // Queue order (CreatedAt) decides which deletes are granted: the first two.
        Assert.That(deletes[0].Status, Is.EqualTo(PendingExportStatus.Exported));
        Assert.That(deletes[1].Status, Is.EqualTo(PendingExportStatus.Exported));

        // The remaining three stay Pending, untouched, with no execution item.
        foreach (var withheldDelete in deletes.Skip(2))
        {
            Assert.That(withheldDelete.Status, Is.EqualTo(PendingExportStatus.Pending),
                "a withheld delete must not be marked in any way");
            Assert.That(result.ProcessedExportItems.Any(i => i.PendingExportId == withheldDelete.Id), Is.False,
                "a withheld delete must be given no execution item");
        }

        // Creates and updates are unaffected by the delete limit.
        Assert.That(creates, Has.All.Matches<PendingExport>(c => c.Status == PendingExportStatus.Exported));
        Assert.That(updates, Has.All.Matches<PendingExport>(u => u.Status == PendingExportStatus.Exported));
    }

    [Test]
    public async Task ExecuteExportsAsync_MaxDeletesOfZero_CompletesTheRunAndWithholdsEveryDeleteAsync()
    {
        var targetSystem = ConnectedSystemsData.Single(s => s.Name == "Dummy Target System");
        var targetUserType = ConnectedSystemObjectTypesData.Single(t => t.Name == "TARGET_USER");
        var baseTime = DateTime.UtcNow.AddMinutes(-10);

        var deletes = Enumerable.Range(0, 4)
            .Select(i => CreateSeededExport(targetSystem, targetUserType, PendingExportChangeType.Delete, baseTime.AddSeconds(i)))
            .ToList();

        var mockConnector = CreateSucceedingCallsConnector();
        var options = new ExportExecutionOptions { MaxDeletes = 0 };

        // The limit-of-zero case is the one most at risk of the paging loop never terminating
        // (a risk called out in the plan): the run must complete rather than hang.
        ExportExecutionResult result = null!;
        Assert.That(async () => result = await Jim.ExportExecution.ExecuteExportsAsync(
                targetSystem, mockConnector.Object, SyncRunMode.PreviewAndSync, options, CancellationToken.None),
            Throws.Nothing);

        Assert.That(result.DeletesWithheld, Is.EqualTo(4));
        Assert.That(result.SuccessCount, Is.EqualTo(0));
        Assert.That(deletes, Has.All.Matches<PendingExport>(d => d.Status == PendingExportStatus.Pending));
        Assert.That(result.ProcessedExportItems, Is.Empty);
    }

    [Test]
    public async Task ExecuteExportsAsync_MaxDeletesEqualToQueue_WithholdsNoneAsync()
    {
        var targetSystem = ConnectedSystemsData.Single(s => s.Name == "Dummy Target System");
        var targetUserType = ConnectedSystemObjectTypesData.Single(t => t.Name == "TARGET_USER");
        var baseTime = DateTime.UtcNow.AddMinutes(-10);

        var deletes = Enumerable.Range(0, 3)
            .Select(i => CreateSeededExport(targetSystem, targetUserType, PendingExportChangeType.Delete, baseTime.AddSeconds(i)))
            .ToList();

        var mockConnector = CreateSucceedingCallsConnector();
        var options = new ExportExecutionOptions { MaxDeletes = 3 };

        var result = await Jim.ExportExecution.ExecuteExportsAsync(
            targetSystem, mockConnector.Object, SyncRunMode.PreviewAndSync, options, CancellationToken.None);

        Assert.That(result.DeletesWithheld, Is.EqualTo(0));
        Assert.That(result.SuccessCount, Is.EqualTo(3));
        Assert.That(deletes, Has.All.Matches<PendingExport>(d => d.Status == PendingExportStatus.Exported));
    }

    /// <summary>
    /// The deferred (reference-resolution) pass shares the same ledger as the first, immediate pass: a
    /// deferred export that resolves cleanly in the second pass still competes for whatever capacity
    /// the first pass left, rather than getting a budget of its own.
    /// </summary>
    [Test]
    public async Task ExecuteExportsAsync_DeferredExportResolvedInPassTwo_ConsumesTheSameLedgerSlotAsync()
    {
        var targetSystem = ConnectedSystemsData.Single(s => s.Name == "Dummy Target System");
        var targetUserType = ConnectedSystemObjectTypesData.Single(t => t.Name == "TARGET_USER");
        var displayNameAttr = targetUserType.Attributes.Single(a => a.Name == MockTargetSystemAttributeNames.DisplayName.ToString());
        var managerAttr = targetUserType.Attributes.Single(a => a.Name == MockTargetSystemAttributeNames.Manager.ToString());
        var objectGuidAttr = targetUserType.Attributes.Single(a => a.Name == MockTargetSystemAttributeNames.ObjectGuid.ToString());
        var baseTime = DateTime.UtcNow.AddMinutes(-10);

        // The referenced MVO and its already-anchored target CSO, so the deferred export's reference
        // resolves cleanly in pass two.
        var referencedMvoId = Guid.NewGuid();
        SyncRepo.SeedMetaverseObject(new MetaverseObject { Id = referencedMvoId });
        var referencedCso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = targetSystem.Id,
            Type = targetUserType,
            TypeId = targetUserType.Id,
            MetaverseObjectId = referencedMvoId,
            AttributeValues = new List<ConnectedSystemObjectAttributeValue>
            {
                new() { Id = Guid.NewGuid(), Attribute = objectGuidAttr, AttributeId = objectGuidAttr.Id, GuidValue = Guid.NewGuid() }
            }
        };
        SyncRepo.SeedConnectedSystemObject(referencedCso);

        // One immediate update, first in queue order, that alone consumes the whole limit.
        var immediateUpdate = CreateSeededExport(targetSystem, targetUserType, PendingExportChangeType.Update, baseTime,
            attributeValueChanges: [BuildUpdateChange(displayNameAttr, "Immediate")]);

        // One deferred update, later in queue order, whose reference resolves in pass two.
        var deferredUpdate = CreateSeededExport(targetSystem, targetUserType, PendingExportChangeType.Update, baseTime.AddSeconds(1),
            hasUnresolvedReferences: true,
            attributeValueChanges:
            [
                BuildUpdateChange(displayNameAttr, "Deferred"),
                new()
                {
                    Id = Guid.NewGuid(),
                    ChangeType = PendingExportAttributeChangeType.Update,
                    AttributeId = managerAttr.Id,
                    Attribute = managerAttr,
                    UnresolvedReferenceValue = referencedMvoId.ToString(),
                    Status = PendingExportAttributeChangeStatus.Pending
                }
            ]);

        var mockConnector = CreateSucceedingCallsConnector();
        var options = new ExportExecutionOptions { MaxUpdates = 1 };

        var result = await Jim.ExportExecution.ExecuteExportsAsync(
            targetSystem, mockConnector.Object, SyncRunMode.PreviewAndSync, options, CancellationToken.None);

        Assert.That(immediateUpdate.Status, Is.EqualTo(PendingExportStatus.Exported),
            "the first-pass update consumes the run's only slot for the type");
        Assert.That(deferredUpdate.Status, Is.EqualTo(PendingExportStatus.Pending),
            "the deferred update resolved cleanly, but the ledger had nothing left for it");
        Assert.That(result.UpdatesWithheld, Is.EqualTo(1));
        Assert.That(result.DeferredCount, Is.EqualTo(0), "a withheld export is not the same as one deferred for lack of a reference");
        Assert.That(result.ProcessedExportItems.Any(i => i.PendingExportId == deferredUpdate.Id), Is.False);
    }

    [Test]
    public async Task ExecuteExportsAsync_FilesConnector_HonoursTheLimitAsync()
    {
        var targetSystem = ConnectedSystemsData.Single(s => s.Name == "Dummy Target System");
        var targetUserType = ConnectedSystemObjectTypesData.Single(t => t.Name == "TARGET_USER");
        var baseTime = DateTime.UtcNow.AddMinutes(-10);

        var creates = Enumerable.Range(0, 3)
            .Select(i => CreateSeededExport(targetSystem, targetUserType, PendingExportChangeType.Create, baseTime.AddSeconds(i)))
            .ToList();

        List<PendingExport>? exportsHandedToConnector = null;
        var mockConnector = new Mock<IConnector>();
        var mockFileConnector = mockConnector.As<IConnectorExportUsingFiles>();
        mockConnector.Setup(c => c.Name).Returns("Test File Connector");
        mockFileConnector.Setup(c => c.ExportAsync(
                It.IsAny<IList<ConnectedSystemSettingValue>>(),
                It.IsAny<IList<PendingExport>>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<IConnectorProgress>()))
            .ReturnsAsync((IList<ConnectedSystemSettingValue> _, IList<PendingExport> exports, CancellationToken _, IConnectorProgress _) =>
            {
                exportsHandedToConnector = exports.ToList();
                return exports.Select(_ => ConnectedSystemExportResult.Succeeded()).ToList();
            });

        var options = new ExportExecutionOptions { MaxCreates = 1 };

        var result = await Jim.ExportExecution.ExecuteExportsAsync(
            targetSystem, mockConnector.Object, SyncRunMode.PreviewAndSync, options, CancellationToken.None);

        Assert.That(result.CreatesWithheld, Is.EqualTo(2));
        Assert.That(exportsHandedToConnector, Has.Count.EqualTo(1), "the files connector must only be handed the granted head of the queue");
        Assert.That(exportsHandedToConnector![0].Id, Is.EqualTo(creates[0].Id), "queue order (CreatedAt) decides which export is granted");

        Assert.That(creates[0].Status, Is.EqualTo(PendingExportStatus.Exported));
        Assert.That(creates[1].Status, Is.EqualTo(PendingExportStatus.Pending));
        Assert.That(creates[2].Status, Is.EqualTo(PendingExportStatus.Pending));
        Assert.That(result.ProcessedExportItems, Has.Count.EqualTo(1));
    }
}
