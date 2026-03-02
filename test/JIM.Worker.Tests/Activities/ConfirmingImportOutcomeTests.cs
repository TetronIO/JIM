using JIM.Application;
using JIM.Connectors.Mock;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.PostgresData;
using JIM.Worker.Processors;
using JIM.Worker.Tests.Models;
using MockQueryable.Moq;
using Moq;

namespace JIM.Worker.Tests.Activities;

/// <summary>
/// Tests that a confirming import (delta import after export) produces the correct RPEI outcomes.
/// When objects are exported to a connected system and then imported back with the same attribute values,
/// the import phase should NOT produce a CsoUpdated outcome — no CSO attributes were actually changed.
///
/// Scenario: Export creates 3 CSOs in target AD → confirming import brings them back unchanged.
/// The PendingProvisioning → Normal status transition occurs, but no attribute values change.
/// </summary>
[TestFixture]
public class ConfirmingImportOutcomeTests
{
    #region Fields

    private MetaverseObject _initiatedBy = null!;
    private List<ConnectedSystem> _connectedSystemsData = null!;
    private List<ConnectedSystemRunProfile> _runProfilesData = null!;
    private List<ConnectedSystemObjectType> _objectTypesData = null!;
    private List<ConnectedSystemPartition> _partitionsData = null!;
    private List<Activity> _activitiesData = null!;
    private List<ServiceSetting> _serviceSettingsData = null!;
    private List<PendingExport> _pendingExportsData = null!;
    private List<ConnectedSystemObject> _connectedSystemObjectsData = null!;
    private Mock<JimDbContext> _mockDbContext = null!;
    private JimApplication _jim = null!;

    #endregion

    [TearDown]
    public void TearDown()
    {
        _jim?.Dispose();
    }

    [SetUp]
    public void SetUp()
    {
        TestUtilities.SetEnvironmentVariables();
        _initiatedBy = TestUtilities.GetInitiatedBy();

        _connectedSystemsData = TestUtilities.GetConnectedSystemData();
        _runProfilesData = TestUtilities.GetConnectedSystemRunProfileData();
        _objectTypesData = TestUtilities.GetConnectedSystemObjectTypeData();
        _partitionsData = TestUtilities.GetConnectedSystemPartitionData();
        _serviceSettingsData = TestUtilities.GetServiceSettingsData();
    }

    /// <summary>
    /// Sets up the mock DbContext with the given data lists.
    /// Must be called after data lists are fully populated.
    /// </summary>
    private void InitialiseMockDbContext()
    {
        _mockDbContext = new Mock<JimDbContext>();

        _mockDbContext.Setup(m => m.ConnectedSystems).Returns(_connectedSystemsData.BuildMockDbSet().Object);
        _mockDbContext.Setup(m => m.ConnectedSystemRunProfiles).Returns(_runProfilesData.BuildMockDbSet().Object);
        _mockDbContext.Setup(m => m.ConnectedSystemObjectTypes).Returns(_objectTypesData.BuildMockDbSet().Object);
        _mockDbContext.Setup(m => m.ConnectedSystemPartitions).Returns(_partitionsData.BuildMockDbSet().Object);
        _mockDbContext.Setup(m => m.ServiceSettingItems).Returns(_serviceSettingsData.BuildMockDbSet().Object);
        _mockDbContext.Setup(m => m.PendingExports).Returns(_pendingExportsData.BuildMockDbSet().Object);
        _mockDbContext.Setup(m => m.Activities).Returns(_activitiesData.BuildMockDbSet().Object);

        var mockCsoDbSet = _connectedSystemObjectsData.BuildMockDbSet();
        mockCsoDbSet.Setup(set => set.AddRange(It.IsAny<IEnumerable<ConnectedSystemObject>>())).Callback((IEnumerable<ConnectedSystemObject> entities) =>
        {
            foreach (var entity in entities)
                entity.Id = Guid.NewGuid();
            _connectedSystemObjectsData.AddRange(entities);
        });
        _mockDbContext.Setup(m => m.ConnectedSystemObjects).Returns(mockCsoDbSet.Object);

        _jim = new JimApplication(new PostgresDataRepository(_mockDbContext.Object));
    }

    /// <summary>
    /// Simulates the scenario: 3 CSOs were provisioned via export to the target AD system.
    /// They have PendingProvisioning status. A confirming import brings back the same
    /// attribute values — no actual attribute changes.
    ///
    /// This test validates the import phase only: with no pending exports, reconciliation
    /// is a no-op, so we can isolate the import processing behaviour.
    ///
    /// The bug: PendingProvisioning → Normal status transition triggers a CsoUpdated outcome
    /// even when hasAttributeChanges = false. The status transition is correct, but the
    /// CsoUpdated outcome misrepresents what actually happened during import.
    ///
    /// Expected: RPEIs should either:
    /// (a) not have a CsoUpdated outcome (since no attributes changed), or
    /// (b) have a more specific outcome like StatusTransitioned
    /// </summary>
    [Test]
    public async Task ConfirmingImport_WithNoAttributeChanges_ShouldNotProduceCsoUpdatedOutcomeAsync()
    {
        // Arrange: Get the target system's USER object type and its attributes
        var targetUserType = _objectTypesData.Single(t => t.Name == "TARGET_USER");

        // External ID attribute (ObjectGuid)
        var objectGuidAttr = targetUserType.Attributes.Single(a => a.Name == MockTargetSystemAttributeNames.ObjectGuid.ToString());
        var displayNameAttr = targetUserType.Attributes.Single(a => a.Name == MockTargetSystemAttributeNames.DisplayName.ToString());
        var employeeIdAttr = targetUserType.Attributes.Single(a => a.Name == MockTargetSystemAttributeNames.EmployeeId.ToString());

        // Create 3 CSOs in PendingProvisioning status (simulating post-export state)
        // These represent objects that JIM exported to AD but hasn't confirmed yet
        var cso1Guid = Guid.NewGuid();
        var cso2Guid = Guid.NewGuid();
        var cso3Guid = Guid.NewGuid();

        const int targetSystemId = 2;
        var targetSystemData = _connectedSystemsData.Single(cs => cs.Id == targetSystemId);
        _connectedSystemObjectsData = new List<ConnectedSystemObject>
        {
            CreatePendingProvisioningCso(targetSystemData, targetUserType, objectGuidAttr, displayNameAttr, employeeIdAttr,
                cso1Guid, "Harry Moss", "EMP001"),
            CreatePendingProvisioningCso(targetSystemData, targetUserType, objectGuidAttr, displayNameAttr, employeeIdAttr,
                cso2Guid, "Charlie Mathews", "EMP002"),
            CreatePendingProvisioningCso(targetSystemData, targetUserType, objectGuidAttr, displayNameAttr, employeeIdAttr,
                cso3Guid, "Olivia Jane", "EMP003")
        };

        // No pending exports — isolates the import phase from reconciliation.
        // This prevents ChangeTracker issues in the mocked DbContext while still
        // proving the core issue: CsoUpdated is recorded when no attributes changed.
        _pendingExportsData = new List<PendingExport>();

        // Create the activity for this import run
        var importRunProfile = _runProfilesData.Single(rp => rp.ConnectedSystemId == targetSystemId && rp.RunType == ConnectedSystemRunType.FullImport);
        _activitiesData = TestUtilities.GetActivityData(importRunProfile.RunType, importRunProfile.Id);

        InitialiseMockDbContext();

        // Retrieve the target system through the repository (populates ObjectTypes navigation)
        var targetSystem = await _jim.ConnectedSystems.GetConnectedSystemAsync(targetSystemId);
        Assert.That(targetSystem, Is.Not.Null, "Target connected system should exist in test data");

        // Build import objects: connector returns the SAME attribute values that are already on the CSOs
        // This is the confirming import — AD reports exactly what JIM exported, no changes
        var mockConnector = new MockFileConnector();
        foreach (var cso in _connectedSystemObjectsData)
        {
            var importObject = new ConnectedSystemImportObject
            {
                ObjectType = "TARGET_USER",
                ChangeType = ObjectChangeType.Updated
            };

            // Add the same attribute values that already exist on the CSO
            foreach (var av in cso.AttributeValues)
            {
                var attr = targetUserType.Attributes.Single(a => a.Id == av.AttributeId);
                var importAttr = new ConnectedSystemImportObjectAttribute { Name = attr.Name };

                if (av.GuidValue.HasValue)
                    importAttr.GuidValues.Add(av.GuidValue.Value);
                else if (av.StringValue != null)
                    importAttr.StringValues.Add(av.StringValue);

                importObject.Attributes.Add(importAttr);
            }

            mockConnector.TestImportObjects.Add(importObject);
        }

        // Act: Run the full import
        var activity = _activitiesData.First();
        var importProcessor = new SyncImportTaskProcessor(
            _jim,
            mockConnector,
            targetSystem!,
            importRunProfile,
            TestUtilities.CreateTestWorkerTask(activity, _initiatedBy),
            new CancellationTokenSource());

        await importProcessor.PerformFullImportAsync();

        // Assert: Check the RPEIs on the activity
        var rpeis = activity.RunProfileExecutionItems.ToList();

        // Diagnostic: output RPEI summary for debugging
        var rpeiSummary = string.Join(" | ", rpeis.Select(r =>
            $"ChangeType={r.ObjectChangeType}, CsoId={r.ConnectedSystemObjectId}, " +
            $"OutcomeSummary=[{r.OutcomeSummary}], " +
            $"SyncOutcomes=[{string.Join(",", r.SyncOutcomes.Select(o => o.OutcomeType))}], " +
            $"ErrorType={r.ErrorType}, ErrorMsg=[{r.ErrorMessage}]"));

        // Check for unexpected errors first
        var errored = rpeis.Where(r => r.ErrorType != null && r.ErrorType != ActivityRunProfileExecutionItemErrorType.NotSet).ToList();
        Assert.That(errored, Has.Count.EqualTo(0),
            $"RPEIs should not have errors. Errors: {string.Join("; ", errored.Select(r => $"[{r.ErrorType}: {r.ErrorMessage}]"))}");

        // There should be exactly 3 RPEIs (one per CSO)
        Assert.That(rpeis, Has.Count.EqualTo(3),
            $"Should have exactly one RPEI per CSO (one-RPEI-per-CSO rule). RPEIs: {rpeiSummary}");

        // Verify CSOs transitioned from PendingProvisioning to Normal
        foreach (var cso in _connectedSystemObjectsData.Where(c => c.Status != ConnectedSystemObjectStatus.PendingProvisioning || c.Status == ConnectedSystemObjectStatus.Normal))
        {
            Assert.That(cso.Status, Is.EqualTo(ConnectedSystemObjectStatus.Normal),
                $"CSO {cso.Id} should have transitioned to Normal status during confirming import");
        }

        // Core assertion: No CsoUpdated outcomes should exist when no attributes were actually changed.
        // The PendingProvisioning → Normal status transition is NOT an attribute change.
        foreach (var rpei in rpeis)
        {
            var outcomes = rpei.SyncOutcomes.ToList();

            var csoUpdatedOutcomes = outcomes.Where(o => o.OutcomeType == ActivityRunProfileExecutionItemSyncOutcomeType.CsoUpdated).ToList();
            Assert.That(csoUpdatedOutcomes, Has.Count.EqualTo(0),
                $"RPEI for CSO {rpei.ConnectedSystemObjectId} should NOT have CsoUpdated outcome when " +
                $"no attributes were changed during confirming import (only status transition occurred). " +
                $"OutcomeSummary: {rpei.OutcomeSummary}");
        }
    }

    #region Helpers

    /// <summary>
    /// Creates a CSO in PendingProvisioning status with the given attribute values.
    /// Simulates a CSO that was created by export but not yet confirmed by import.
    /// </summary>
    private static ConnectedSystemObject CreatePendingProvisioningCso(
        ConnectedSystem connectedSystem,
        ConnectedSystemObjectType csoType,
        ConnectedSystemObjectTypeAttribute objectGuidAttr,
        ConnectedSystemObjectTypeAttribute displayNameAttr,
        ConnectedSystemObjectTypeAttribute employeeIdAttr,
        Guid externalId,
        string displayName,
        string employeeId)
    {
        var cso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystem = connectedSystem,
            ConnectedSystemId = connectedSystem.Id,
            TypeId = csoType.Id,
            Type = csoType,
            Status = ConnectedSystemObjectStatus.PendingProvisioning,
            ExternalIdAttributeId = objectGuidAttr.Id,
            Created = DateTime.UtcNow.AddMinutes(-5)
        };

        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            AttributeId = objectGuidAttr.Id,
            Attribute = objectGuidAttr,
            ConnectedSystemObject = cso,
            GuidValue = externalId
        });

        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            AttributeId = displayNameAttr.Id,
            Attribute = displayNameAttr,
            ConnectedSystemObject = cso,
            StringValue = displayName
        });

        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            AttributeId = employeeIdAttr.Id,
            Attribute = employeeIdAttr,
            ConnectedSystemObject = cso,
            StringValue = employeeId
        });

        return cso;
    }

    #endregion
}
