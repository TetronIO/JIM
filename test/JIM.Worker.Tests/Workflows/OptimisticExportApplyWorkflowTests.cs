// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Application.Servers;
using JIM.Connectors.Mock;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Interfaces;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.PostgresData;
using JIM.Worker.Processors;
using JIM.Worker.Tests.Models;
using MockQueryable.Moq;
using Moq;
using SyncRepository = JIM.InMemoryData.SyncRepository;

namespace JIM.Worker.Tests.Workflows;

/// <summary>
/// End-to-end workflow tests for optimistic export apply (issue #1079): export success applies
/// the exported attribute values to the Connected System Object, and the subsequent confirming
/// import's diff then finds them already present. Combines the export-execution setup pattern
/// from <c>ExportConfirmationWorkflowTests</c> with the confirming-import setup pattern from
/// <c>Activities/ConfirmingImportOutcomeTests</c>: exports run for real via
/// <c>Jim.ExportExecution.ExecuteExportsAsync</c>, then the confirming import runs for real via
/// <c>SyncImportTaskProcessor.PerformImportAsync</c> - which reconciles Pending Exports as part of
/// the same run (<c>ReconcilePendingExportsAsync</c>), so no separate reconciliation call is made.
/// </summary>
public class OptimisticExportApplyWorkflowTests
{
    #region accessors
    private Mock<JimDbContext> MockJimDbContext { get; set; } = null!;
    private List<ConnectedSystem> ConnectedSystemsData { get; set; } = null!;
    private List<ConnectedSystemRunProfile> RunProfilesData { get; set; } = null!;
    private List<ConnectedSystemObject> ConnectedSystemObjectsData { get; set; } = null!;
    private List<ConnectedSystemObjectType> ConnectedSystemObjectTypesData { get; set; } = null!;
    private List<ConnectedSystemPartition> PartitionsData { get; set; } = null!;
    private List<ServiceSetting> ServiceSettingsData { get; set; } = null!;
    private List<PendingExport> PendingExportsData { get; set; } = null!;
    private List<Activity> ActivitiesData { get; set; } = null!;
    private JimApplication Jim { get; set; } = null!;
    private SyncRepository SyncRepo { get; set; } = null!;
    private ConnectedSystem TargetSystem { get; set; } = null!;
    private ConnectedSystemObjectType TargetUserType { get; set; } = null!;
    private ConnectedSystemObjectTypeAttribute ObjectGuidAttr { get; set; } = null!;
    private ConnectedSystemObjectTypeAttribute DisplayNameAttr { get; set; } = null!;
    private ConnectedSystemObjectTypeAttribute EmployeeIdAttr { get; set; } = null!;
    private MetaverseObject InitiatedBy { get; set; } = null!;
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
        InitiatedBy = TestUtilities.GetInitiatedBy();

        ConnectedSystemsData = TestUtilities.GetConnectedSystemData();
        RunProfilesData = TestUtilities.GetConnectedSystemRunProfileData();
        ConnectedSystemObjectTypesData = TestUtilities.GetConnectedSystemObjectTypeData();
        PartitionsData = TestUtilities.GetConnectedSystemPartitionData();
        ServiceSettingsData = TestUtilities.GetServiceSettingsData();
        ConnectedSystemObjectsData = new List<ConnectedSystemObject>();
        PendingExportsData = new List<PendingExport>();

        TargetSystem = ConnectedSystemsData.Single(s => s.Name == "Dummy Target System");
        TargetUserType = ConnectedSystemObjectTypesData.Single(t => t.Name == "TARGET_USER");
        ObjectGuidAttr = TargetUserType.Attributes.Single(a => a.Name == MockTargetSystemAttributeNames.ObjectGuid.ToString());
        DisplayNameAttr = TargetUserType.Attributes.Single(a => a.Name == MockTargetSystemAttributeNames.DisplayName.ToString());
        EmployeeIdAttr = TargetUserType.Attributes.Single(a => a.Name == MockTargetSystemAttributeNames.EmployeeId.ToString());
    }

    /// <summary>
    /// Builds the mock DbContext and SyncRepo/JimApplication once ConnectedSystemObjectsData,
    /// PendingExportsData, and ActivitiesData are fully populated for the test.
    /// </summary>
    private void InitialiseApplication()
    {
        MockJimDbContext = new Mock<JimDbContext>();
        TestUtilities.SetUpEmptyConnectedSystemGraphMocks(MockJimDbContext);
        MockJimDbContext.Setup(m => m.ConnectedSystems).Returns(ConnectedSystemsData.BuildMockDbSet().Object);
        MockJimDbContext.Setup(m => m.ConnectedSystemRunProfiles).Returns(RunProfilesData.BuildMockDbSet().Object);
        MockJimDbContext.Setup(m => m.ConnectedSystemObjectTypes).Returns(ConnectedSystemObjectTypesData.BuildMockDbSet().Object);
        MockJimDbContext.Setup(m => m.ConnectedSystemPartitions).Returns(PartitionsData.BuildMockDbSet().Object);
        MockJimDbContext.Setup(m => m.ServiceSettingItems).Returns(ServiceSettingsData.BuildMockDbSet().Object);
        MockJimDbContext.Setup(m => m.ConnectedSystemObjects).Returns(ConnectedSystemObjectsData.BuildMockDbSet().Object);
        MockJimDbContext.Setup(m => m.PendingExports).Returns(PendingExportsData.BuildMockDbSet().Object);
        MockJimDbContext.Setup(m => m.PendingExportAttributeValueChanges)
            .Returns(PendingExportsData.SelectMany(pe => pe.AttributeValueChanges).ToList().BuildMockDbSet().Object);
        MockJimDbContext.Setup(m => m.Activities).Returns(ActivitiesData.BuildMockDbSet().Object);

        SyncRepo = TestUtilities.CreateSyncRepository(csos: ConnectedSystemObjectsData, pendingExports: PendingExportsData, activity: ActivitiesData.First());
        Jim = new JimApplication(new PostgresDataRepository(MockJimDbContext.Object), syncRepository: SyncRepo);
    }

    private static Mock<IConnector> CreateSucceedingExportConnector(string? externalId = null)
    {
        var mockConnector = new Mock<IConnector>();
        var mockExportConnector = mockConnector.As<IConnectorExportUsingCalls>();
        mockConnector.Setup(c => c.Name).Returns("Test Export Connector");
        mockExportConnector.Setup(c => c.ExportAsync(It.IsAny<IList<PendingExport>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IList<PendingExport> exports, CancellationToken _) =>
                exports.Select(_ => externalId != null
                    ? ConnectedSystemExportResult.Succeeded(externalId)
                    : ConnectedSystemExportResult.Succeeded()).ToList());
        return mockConnector;
    }

    /// <summary>
    /// Builds a confirming-import object reporting back exactly the values a Connected System
    /// would have if it had faithfully stored what the export sent (i.e. what optimistic apply
    /// already staged onto <paramref name="cso"/>.AttributeValues).
    /// </summary>
    private ConnectedSystemImportObject BuildConfirmingImportObject(ConnectedSystemObject cso)
    {
        var importObject = new ConnectedSystemImportObject
        {
            ObjectType = "TARGET_USER",
            ChangeType = ObjectChangeType.Updated
        };

        foreach (var av in cso.AttributeValues)
        {
            // Resolve the name via the schema, not av.Attribute.Name: BatchUpdateCsosAfterSuccessfulExportAsync
            // creates the external-Id row with AttributeId set but no Attribute navigation.
            var attrName = TargetUserType.Attributes.Single(a => a.Id == av.AttributeId).Name;
            var importAttr = new ConnectedSystemImportObjectAttribute { Name = attrName };
            if (av.GuidValue.HasValue)
                importAttr.GuidValues.Add(av.GuidValue.Value);
            else if (av.StringValue != null)
                importAttr.StringValues.Add(av.StringValue);
            importObject.Attributes.Add(importAttr);
        }

        return importObject;
    }

    private async Task<Activity> RunConfirmingImportAsync(ConnectedSystemImportObject importObject)
    {
        var importRunProfile = RunProfilesData.Single(rp => rp.ConnectedSystemId == TargetSystem.Id && rp.RunType == ConnectedSystemRunType.FullImport);
        ActivitiesData = TestUtilities.GetActivityData(importRunProfile.RunType, importRunProfile.Id);
        InitialiseApplication();

        var targetSystem = await Jim.ConnectedSystems.GetConnectedSystemAsync(TargetSystem.Id);
        Assert.That(targetSystem, Is.Not.Null);

        var mockConnector = new MockFileConnector();
        mockConnector.TestImportObjects.Add(importObject);

        var activity = ActivitiesData.First();
        var importProcessor = new SyncImportTaskProcessor(
            Jim,
            SyncRepo,
            new SyncServer(Jim),
            new JIM.Application.Servers.SyncEngine(),
            mockConnector,
            targetSystem!,
            importRunProfile,
            TestUtilities.CreateTestWorkerTask(activity, InitiatedBy),
            new CancellationTokenSource());

        await importProcessor.PerformImportAsync();
        return activity;
    }

    /// <summary>
    /// Create/provisioning cycle: a PendingProvisioning CSO's Create export applies its attribute
    /// values (D10) without transitioning status (only the confirming import does that). The
    /// confirming import then finds the values already matching (no CsoUpdated outcome) but DOES
    /// legitimately restamp LastUpdated, because PendingProvisioning -&gt; Normal is itself a real
    /// status transition (SyncImportTaskProcessor stamps on hasAttributeChanges OR statusTransitioned).
    /// Reconciliation then confirms and deletes the Pending Export.
    /// </summary>
    [Test]
    public async Task Workflow_CreateExport_ConfirmingImportAppliesNoUpdateButStampsStatusTransitionAsync()
    {
        // Arrange: a skeletal PendingProvisioning CSO with no attribute values yet.
        var cso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = TargetSystem.Id,
            ConnectedSystem = TargetSystem,
            Type = TargetUserType,
            TypeId = TargetUserType.Id,
            Status = ConnectedSystemObjectStatus.PendingProvisioning,
            ExternalIdAttributeId = ObjectGuidAttr.Id,
            AttributeValues = new List<ConnectedSystemObjectAttributeValue>()
        };
        // Pre-seed the external Id: the connector-assigned-external-Id path
        // (BatchUpdateCsosAfterSuccessfulExportAsync) is pre-existing, unrelated production code,
        // not exercised via ConnectedSystemExportResult here so this test can focus on optimistic
        // apply's own attribute changes without depending on that path's attribute-type lookup.
        var objectGuid = Guid.NewGuid();
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(), ConnectedSystemObject = cso, AttributeId = ObjectGuidAttr.Id, Attribute = ObjectGuidAttr, GuidValue = objectGuid
        });
        ConnectedSystemObjectsData.Add(cso);

        var pendingExport = new PendingExport
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = TargetSystem.Id,
            ConnectedSystem = TargetSystem,
            ConnectedSystemObject = cso,
            ConnectedSystemObjectId = cso.Id,
            Status = PendingExportStatus.Pending,
            ChangeType = PendingExportChangeType.Create,
            CreatedAt = DateTime.UtcNow,
            AttributeValueChanges = new List<PendingExportAttributeValueChange>
            {
                new()
                {
                    Id = Guid.NewGuid(), ChangeType = PendingExportAttributeChangeType.Update,
                    AttributeId = DisplayNameAttr.Id, Attribute = DisplayNameAttr, StringValue = "Harry Moss",
                    Status = PendingExportAttributeChangeStatus.Pending
                },
                new()
                {
                    Id = Guid.NewGuid(), ChangeType = PendingExportAttributeChangeType.Update,
                    AttributeId = EmployeeIdAttr.Id, Attribute = EmployeeIdAttr, StringValue = "EMP001",
                    Status = PendingExportAttributeChangeStatus.Pending
                }
            }
        };
        PendingExportsData.Add(pendingExport);

        ActivitiesData = TestUtilities.GetActivityData(ConnectedSystemRunType.Export, 5);
        InitialiseApplication();

        // Act 1: Export.
        var mockExportConnector = CreateSucceedingExportConnector();
        await Jim.ExportExecution.ExecuteExportsAsync(TargetSystem, mockExportConnector.Object, SyncRunMode.PreviewAndSync);

        // Assert 1: optimistic apply populated the exported values; status/LastUpdated untouched (D2).
        Assert.That(cso.AttributeValues.Any(av => av.AttributeId == DisplayNameAttr.Id && av.StringValue == "Harry Moss"), Is.True);
        Assert.That(cso.AttributeValues.Any(av => av.AttributeId == EmployeeIdAttr.Id && av.StringValue == "EMP001"), Is.True);
        // D9 dedupe: optimistic apply must not touch or duplicate the pre-existing external Id row,
        // since it is not one of this Pending Export's AttributeValueChanges.
        Assert.That(cso.AttributeValues.Count(av => av.AttributeId == ObjectGuidAttr.Id), Is.EqualTo(1));
        Assert.That(cso.AttributeValues.Single(av => av.AttributeId == ObjectGuidAttr.Id).GuidValue, Is.EqualTo(objectGuid));
        Assert.That(cso.Status, Is.EqualTo(ConnectedSystemObjectStatus.PendingProvisioning), "export must never transition CSO status");
        Assert.That(cso.LastUpdated, Is.Null, "export must never stamp LastUpdated (D2)");

        // Act 2: confirming import reports back exactly what was just exported.
        var importObject = BuildConfirmingImportObject(cso);
        var activity = await RunConfirmingImportAsync(importObject);

        // Assert 2: SyncImportTaskProcessor.PerformImportAsync reconciles Pending Exports as part
        // of the SAME run (ReconcilePendingExportsAsync), merging ExportConfirmed outcomes onto the
        // RPEI created for the status transition. No CsoUpdated outcome must appear (the attribute
        // values already matched), and LastUpdated IS stamped - this is the one legitimate restamp
        // case, since PendingProvisioning -> Normal is itself a genuine status transition.
        var rpeis = activity.RunProfileExecutionItems.ToList();
        Assert.That(rpeis, Has.Count.EqualTo(1));
        Assert.That(rpeis[0].ErrorType, Is.EqualTo(ActivityRunProfileExecutionItemErrorType.NotSet).Or.Null,
            $"RPEI should not have errored: {rpeis[0].ErrorMessage}");
        Assert.That(rpeis[0].SyncOutcomes.Any(o => o.OutcomeType == ActivityRunProfileExecutionItemSyncOutcomeType.CsoUpdated), Is.False,
            "matching attribute values must not produce a CsoUpdated outcome");
        Assert.That(rpeis[0].SyncOutcomes.Any(o => o.OutcomeType == ActivityRunProfileExecutionItemSyncOutcomeType.ExportConfirmed), Is.True,
            "reconciliation must confirm the exported attribute changes");
        Assert.That(cso.Status, Is.EqualTo(ConnectedSystemObjectStatus.Normal), "the confirming import must transition PendingProvisioning to Normal");
        Assert.That(cso.LastUpdated, Is.Not.Null, "a genuine status transition legitimately stamps LastUpdated");

        // Assert 3: reconciliation (run internally by PerformImportAsync) confirmed and deleted the
        // Pending Export.
        Assert.That(await SyncRepo.GetPendingExportByConnectedSystemObjectIdAsync(cso.Id), Is.Null,
            "the confirmed Pending Export must have been deleted by reconciliation");
    }

    /// <summary>
    /// Update cycle: a Normal CSO's Update export applies its attribute value in place. The
    /// confirming import's own diff finds nothing changed (no CsoUpdated outcome, no LastUpdated
    /// restamp); reconciliation, run internally by the same import as its confirming-import phase,
    /// still confirms and deletes the Pending Export.
    /// </summary>
    [Test]
    public async Task Workflow_UpdateExport_ConfirmingImportProducesNoRpeiAndDoesNotRestampLastUpdatedAsync()
    {
        // Arrange: an already-Normal, already-synced CSO.
        var originalLastUpdated = DateTime.UtcNow.AddDays(-3);
        var objectGuid = Guid.NewGuid();
        var cso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = TargetSystem.Id,
            ConnectedSystem = TargetSystem,
            Type = TargetUserType,
            TypeId = TargetUserType.Id,
            Status = ConnectedSystemObjectStatus.Normal,
            ExternalIdAttributeId = ObjectGuidAttr.Id,
            LastUpdated = originalLastUpdated,
            AttributeValues = new List<ConnectedSystemObjectAttributeValue>()
        };
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(), ConnectedSystemObject = cso, AttributeId = ObjectGuidAttr.Id, Attribute = ObjectGuidAttr, GuidValue = objectGuid
        });
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(), ConnectedSystemObject = cso, AttributeId = DisplayNameAttr.Id, Attribute = DisplayNameAttr, StringValue = "Old Name"
        });
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(), ConnectedSystemObject = cso, AttributeId = EmployeeIdAttr.Id, Attribute = EmployeeIdAttr, StringValue = "EMP002"
        });
        ConnectedSystemObjectsData.Add(cso);

        var pendingExport = new PendingExport
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = TargetSystem.Id,
            ConnectedSystem = TargetSystem,
            ConnectedSystemObject = cso,
            ConnectedSystemObjectId = cso.Id,
            Status = PendingExportStatus.Pending,
            ChangeType = PendingExportChangeType.Update,
            CreatedAt = DateTime.UtcNow,
            AttributeValueChanges = new List<PendingExportAttributeValueChange>
            {
                new()
                {
                    Id = Guid.NewGuid(), ChangeType = PendingExportAttributeChangeType.Update,
                    AttributeId = DisplayNameAttr.Id, Attribute = DisplayNameAttr, StringValue = "New Name",
                    Status = PendingExportAttributeChangeStatus.Pending
                }
            }
        };
        PendingExportsData.Add(pendingExport);

        ActivitiesData = TestUtilities.GetActivityData(ConnectedSystemRunType.Export, 5);
        InitialiseApplication();

        // Act 1: Export.
        var mockExportConnector = CreateSucceedingExportConnector();
        await Jim.ExportExecution.ExecuteExportsAsync(TargetSystem, mockExportConnector.Object, SyncRunMode.PreviewAndSync);

        // Assert 1: optimistic apply replaced the single-valued DisplayName; LastUpdated untouched (D2).
        Assert.That(cso.AttributeValues.Count(av => av.AttributeId == DisplayNameAttr.Id), Is.EqualTo(1));
        Assert.That(cso.AttributeValues.Single(av => av.AttributeId == DisplayNameAttr.Id).StringValue, Is.EqualTo("New Name"));
        Assert.That(cso.LastUpdated, Is.EqualTo(originalLastUpdated), "export must never stamp LastUpdated (D2)");

        // Act 2: confirming import reports back exactly what was just exported.
        var importObject = BuildConfirmingImportObject(cso);
        var activity = await RunConfirmingImportAsync(importObject);

        // Assert 2: the import's own diff finds no attribute changes and no status transition, so
        // its slot in the RPEI list carries no CsoUpdated outcome (reconciliation, run internally by
        // the same PerformImportAsync call, merges an ExportConfirmed outcome onto it instead), and
        // LastUpdated must stay exactly as it was - neither the diff nor reconciliation touches it.
        var rpeis = activity.RunProfileExecutionItems.Where(r => r.ConnectedSystemObjectId == cso.Id).ToList();
        Assert.That(rpeis.Any(r => r.SyncOutcomes.Any(o => o.OutcomeType == ActivityRunProfileExecutionItemSyncOutcomeType.CsoUpdated)), Is.False,
            "a fully-matching confirming import must not produce a CsoUpdated outcome");
        Assert.That(rpeis.Any(r => r.SyncOutcomes.Any(o => o.OutcomeType == ActivityRunProfileExecutionItemSyncOutcomeType.ExportConfirmed)), Is.True,
            "reconciliation must confirm the exported attribute change");
        Assert.That(cso.LastUpdated, Is.EqualTo(originalLastUpdated), "neither the import diff nor reconciliation may restamp LastUpdated when nothing changed");

        // Assert 3: reconciliation (run internally by PerformImportAsync) confirmed and deleted the
        // Pending Export.
        Assert.That(await SyncRepo.GetPendingExportByConnectedSystemObjectIdAsync(cso.Id), Is.Null,
            "the confirmed Pending Export must have been deleted by reconciliation");
    }
}
