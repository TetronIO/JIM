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
/// Run Profile Safeguards (#1618): a run that would exceed a limit attempts NONE of that change
/// type. The export server decides, once per run, which change types are withheld, whether an
/// export is attempted in the first (immediate) pass, the deferred-reference pass, or via a
/// files-based connector. A withheld export is left exactly as found: still Pending, given no
/// execution item, untouched.
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

    private static PendingExportAttributeValueChange BuildUnresolvedReferenceChange(ConnectedSystemObjectTypeAttribute attribute, Guid referencedMvoId) => new()
    {
        Id = Guid.NewGuid(),
        ChangeType = PendingExportAttributeChangeType.Update,
        AttributeId = attribute.Id,
        Attribute = attribute,
        UnresolvedReferenceValue = referencedMvoId.ToString(),
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

    /// <summary>
    /// Seeds a resolvable reference target: an MVO with a Connected System Object already anchored
    /// in the target system, so a deferred export's Manager-style reference resolves cleanly.
    /// </summary>
    private Guid SeedResolvableReferenceTarget(ConnectedSystem targetSystem, ConnectedSystemObjectType type, ConnectedSystemObjectTypeAttribute objectGuidAttr)
    {
        var referencedMvoId = Guid.NewGuid();
        SyncRepo.SeedMetaverseObject(new MetaverseObject { Id = referencedMvoId });
        var referencedCso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = targetSystem.Id,
            Type = type,
            TypeId = type.Id,
            MetaverseObjectId = referencedMvoId,
            AttributeValues = new List<ConnectedSystemObjectAttributeValue>
            {
                new() { Id = Guid.NewGuid(), Attribute = objectGuidAttr, AttributeId = objectGuidAttr.Id, GuidValue = Guid.NewGuid() }
            }
        };
        SyncRepo.SeedConnectedSystemObject(referencedCso);
        return referencedMvoId;
    }

    [Test]
    public async Task ExecuteExportsAsync_PendingCountOneOverLimit_WithholdsAllOfThatTypeAsync()
    {
        var targetSystem = ConnectedSystemsData.Single(s => s.Name == "Dummy Target System");
        var targetUserType = ConnectedSystemObjectTypesData.Single(t => t.Name == "TARGET_USER");
        var displayNameAttr = targetUserType.Attributes.Single(a => a.Name == MockTargetSystemAttributeNames.DisplayName.ToString());
        var baseTime = DateTime.UtcNow.AddMinutes(-10);

        // 101 deletes pending against a limit of 100: one over the limit withholds the whole type.
        var deletes = Enumerable.Range(0, 101)
            .Select(i => CreateSeededExport(targetSystem, targetUserType, PendingExportChangeType.Delete, baseTime.AddSeconds(i)))
            .ToList();

        // Creates and updates carry no limit and must be unaffected.
        var creates = Enumerable.Range(0, 2)
            .Select(i => CreateSeededExport(targetSystem, targetUserType, PendingExportChangeType.Create, baseTime.AddSeconds(200 + i)))
            .ToList();
        var updates = Enumerable.Range(0, 2)
            .Select(i => CreateSeededExport(targetSystem, targetUserType, PendingExportChangeType.Update, baseTime.AddSeconds(300 + i),
                attributeValueChanges: [BuildUpdateChange(displayNameAttr, $"Name {i}")]))
            .ToList();

        var mockConnector = CreateSucceedingCallsConnector();
        var options = new ExportExecutionOptions { MaxDeletes = 100 };

        var result = await Jim.ExportExecution.ExecuteExportsAsync(
            targetSystem, mockConnector.Object, SyncRunMode.PreviewAndSync, options, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.DeletesWithheld, Is.EqualTo(101), "the whole type is withheld, not just the excess over the limit");
            Assert.That(result.CreatesWithheld, Is.EqualTo(0));
            Assert.That(result.UpdatesWithheld, Is.EqualTo(0));
            Assert.That(result.SuccessCount, Is.EqualTo(2 + 2), "only the 2 creates and 2 updates are attempted; no delete is");
        }

        Assert.That(deletes, Has.All.Matches<PendingExport>(d => d.Status == PendingExportStatus.Pending),
            "every delete stays Pending, not just the ones beyond a partial cutoff");
        Assert.That(result.ProcessedExportItems.Any(i => i.ChangeType == PendingExportChangeType.Delete), Is.False,
            "a withheld type gets no execution item for any of its exports");

        Assert.That(creates, Has.All.Matches<PendingExport>(c => c.Status == PendingExportStatus.Exported));
        Assert.That(updates, Has.All.Matches<PendingExport>(u => u.Status == PendingExportStatus.Exported));
    }

    [Test]
    public async Task ExecuteExportsAsync_PendingCountEqualToLimit_AttemptsAllAsync()
    {
        var targetSystem = ConnectedSystemsData.Single(s => s.Name == "Dummy Target System");
        var targetUserType = ConnectedSystemObjectTypesData.Single(t => t.Name == "TARGET_USER");
        var baseTime = DateTime.UtcNow.AddMinutes(-10);

        var deletes = Enumerable.Range(0, 100)
            .Select(i => CreateSeededExport(targetSystem, targetUserType, PendingExportChangeType.Delete, baseTime.AddSeconds(i)))
            .ToList();

        var mockConnector = CreateSucceedingCallsConnector();
        var options = new ExportExecutionOptions { MaxDeletes = 100 };

        var result = await Jim.ExportExecution.ExecuteExportsAsync(
            targetSystem, mockConnector.Object, SyncRunMode.PreviewAndSync, options, CancellationToken.None);

        Assert.That(result.DeletesWithheld, Is.EqualTo(0), "a pending count exactly at the limit is not over it");
        Assert.That(result.SuccessCount, Is.EqualTo(100));
        Assert.That(deletes, Has.All.Matches<PendingExport>(d => d.Status == PendingExportStatus.Exported));
    }

    [Test]
    public async Task ExecuteExportsAsync_LimitOfZeroWithPending_WithholdsAllAndCompletesAsync()
    {
        var targetSystem = ConnectedSystemsData.Single(s => s.Name == "Dummy Target System");
        var targetUserType = ConnectedSystemObjectTypesData.Single(t => t.Name == "TARGET_USER");
        var baseTime = DateTime.UtcNow.AddMinutes(-10);

        var deletes = Enumerable.Range(0, 4)
            .Select(i => CreateSeededExport(targetSystem, targetUserType, PendingExportChangeType.Delete, baseTime.AddSeconds(i)))
            .ToList();

        var mockConnector = CreateSucceedingCallsConnector();
        var options = new ExportExecutionOptions { MaxDeletes = 0 };

        // A limit of zero is the shape most at risk of the paging loop never terminating: the run
        // must complete rather than hang.
        ExportExecutionResult result = null!;
        Assert.That(async () => result = await Jim.ExportExecution.ExecuteExportsAsync(
                targetSystem, mockConnector.Object, SyncRunMode.PreviewAndSync, options, CancellationToken.None),
            Throws.Nothing);

        Assert.That(result.DeletesWithheld, Is.EqualTo(4));
        Assert.That(result.SuccessCount, Is.EqualTo(0));
        Assert.That(deletes, Has.All.Matches<PendingExport>(d => d.Status == PendingExportStatus.Pending));
        Assert.That(result.ProcessedExportItems, Is.Empty);
    }

    /// <summary>
    /// The primary batch loop excludes a withheld type from the database query outright (so it is
    /// never paged), but the deferred pass's own fast-path collection
    /// (<c>GetRemainingDeferredExportsAsync</c>) is not change-type-aware, so a withheld type's
    /// deferred exports can still reach <c>ProcessDeferredExportsAsync</c>. This proves the ledger
    /// guard there catches them while an allowed type completes normally in the same pass.
    /// </summary>
    [Test]
    public async Task ExecuteExportsAsync_DeferredPass_WithholdsWithheldTypeWhileAllowedTypeCompletesAsync()
    {
        var targetSystem = ConnectedSystemsData.Single(s => s.Name == "Dummy Target System");
        var targetUserType = ConnectedSystemObjectTypesData.Single(t => t.Name == "TARGET_USER");
        var displayNameAttr = targetUserType.Attributes.Single(a => a.Name == MockTargetSystemAttributeNames.DisplayName.ToString());
        var managerAttr = targetUserType.Attributes.Single(a => a.Name == MockTargetSystemAttributeNames.Manager.ToString());
        var objectGuidAttr = targetUserType.Attributes.Single(a => a.Name == MockTargetSystemAttributeNames.ObjectGuid.ToString());
        var baseTime = DateTime.UtcNow.AddMinutes(-10);

        var referencedMvoId = SeedResolvableReferenceTarget(targetSystem, targetUserType, objectGuidAttr);

        // One allowed-type (Update, no limit) deferred export, earliest in queue order so it is the
        // one read by the primary batch load.
        var deferredUpdate = CreateSeededExport(targetSystem, targetUserType, PendingExportChangeType.Update, baseTime,
            hasUnresolvedReferences: true,
            attributeValueChanges:
            [
                BuildUpdateChange(displayNameAttr, "Resolvable"),
                BuildUnresolvedReferenceChange(managerAttr, referencedMvoId)
            ]);

        // Two withheld-type (Create, limit 1) deferred exports, later in queue order so the fast
        // path's "remaining beyond the cursor" query is what collects them, not the primary load
        // (which excludes Create from the database query entirely).
        var deferredCreates = Enumerable.Range(0, 2)
            .Select(i => CreateSeededExport(targetSystem, targetUserType, PendingExportChangeType.Create, baseTime.AddSeconds(1 + i),
                hasUnresolvedReferences: true,
                attributeValueChanges: [BuildUnresolvedReferenceChange(managerAttr, referencedMvoId)]))
            .ToList();

        var mockConnector = CreateSucceedingCallsConnector();
        var options = new ExportExecutionOptions { MaxCreates = 1 };

        var result = await Jim.ExportExecution.ExecuteExportsAsync(
            targetSystem, mockConnector.Object, SyncRunMode.PreviewAndSync, options, CancellationToken.None);

        Assert.That(result.CreatesWithheld, Is.EqualTo(2), "2 pending creates exceed the limit of 1");
        Assert.That(deferredUpdate.Status, Is.EqualTo(PendingExportStatus.Exported),
            "the allowed type resolves and completes in the deferred pass");
        Assert.That(deferredCreates, Has.All.Matches<PendingExport>(c => c.Status == PendingExportStatus.Pending),
            "the withheld type is caught by the ledger guard in the deferred pass and left untouched");
        Assert.That(result.DeferredCount, Is.EqualTo(0),
            "a withheld export is not the same as one deferred for lack of a reference: neither resolution failed for it");
        Assert.That(result.ProcessedExportItems.Any(i => i.ChangeType == PendingExportChangeType.Create), Is.False,
            "a withheld type gets no execution item");
    }

    [Test]
    public async Task ExecuteExportsAsync_FilesConnector_WithholdsTheWholeTypeWhenOverLimitAsync()
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

        Assert.That(result.CreatesWithheld, Is.EqualTo(3), "3 pending creates exceed the limit of 1: the whole type is withheld");
        Assert.That(exportsHandedToConnector, Is.Not.Null.And.Empty, "the files connector must be handed none of a withheld type");

        Assert.That(creates, Has.All.Matches<PendingExport>(c => c.Status == PendingExportStatus.Pending));
        Assert.That(result.ProcessedExportItems, Is.Empty);
    }
}
