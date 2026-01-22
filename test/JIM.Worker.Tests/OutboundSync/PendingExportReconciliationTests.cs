using JIM.Application;
using JIM.Application.Services;
using JIM.Models.Core;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.PostgresData;
using JIM.Worker.Tests.Models;
using Microsoft.EntityFrameworkCore;
using MockQueryable.Moq;
using Moq;

namespace JIM.Worker.Tests.OutboundSync;

/// <summary>
/// Tests for PendingExport confirmation via import (reconciliation).
/// Covers the attribute change status lifecycle and retry scenarios.
/// </summary>
public class PendingExportReconciliationTests
{
    #region accessors
    private Mock<JimDbContext> MockJimDbContext { get; set; } = null!;
    private List<ConnectedSystem> ConnectedSystemsData { get; set; } = null!;
    private List<ConnectedSystemObject> ConnectedSystemObjectsData { get; set; } = null!;
    private Mock<DbSet<ConnectedSystemObject>> MockDbSetConnectedSystemObjects { get; set; } = null!;
    private Mock<DbSet<ConnectedSystem>> MockDbSetConnectedSystems { get; set; } = null!;
    private List<ConnectedSystemObjectType> ConnectedSystemObjectTypesData { get; set; } = null!;
    private Mock<DbSet<ConnectedSystemObjectType>> MockDbSetConnectedSystemObjectTypes { get; set; } = null!;
    private List<PendingExport> PendingExportsData { get; set; } = null!;
    private Mock<DbSet<PendingExport>> MockDbSetPendingExports { get; set; } = null!;
    private List<PendingExportAttributeValueChange> PendingExportAttributeValueChangesData { get; set; } = null!;
    private Mock<DbSet<PendingExportAttributeValueChange>> MockDbSetPendingExportAttributeValueChanges { get; set; } = null!;
    private JimApplication Jim { get; set; } = null!;
    private ConnectedSystem TargetSystem { get; set; } = null!;
    private ConnectedSystemObjectType TargetUserType { get; set; } = null!;
    private ConnectedSystemObjectTypeAttribute DisplayNameAttr { get; set; } = null!;
    private ConnectedSystemObjectTypeAttribute MailAttr { get; set; } = null!;
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

        // Set up the Connected Systems mock
        ConnectedSystemsData = TestUtilities.GetConnectedSystemData();
        MockDbSetConnectedSystems = ConnectedSystemsData.BuildMockDbSet();

        // Set up the Connected System Object Types mock
        ConnectedSystemObjectTypesData = TestUtilities.GetConnectedSystemObjectTypeData();
        MockDbSetConnectedSystemObjectTypes = ConnectedSystemObjectTypesData.BuildMockDbSet();

        // Set up the Connected System Objects mock
        ConnectedSystemObjectsData = TestUtilities.GetConnectedSystemObjectData();
        MockDbSetConnectedSystemObjects = ConnectedSystemObjectsData.BuildMockDbSet();

        // Set up the Pending Export objects mock
        PendingExportsData = new List<PendingExport>();
        MockDbSetPendingExports = PendingExportsData.BuildMockDbSet();

        // Set up the Pending Export Attribute Value Changes mock
        PendingExportAttributeValueChangesData = new List<PendingExportAttributeValueChange>();
        MockDbSetPendingExportAttributeValueChanges = PendingExportAttributeValueChangesData.BuildMockDbSet();

        // Mock entity framework calls
        MockJimDbContext = new Mock<JimDbContext>();
        MockJimDbContext.Setup(m => m.ConnectedSystemObjectTypes).Returns(MockDbSetConnectedSystemObjectTypes.Object);
        MockJimDbContext.Setup(m => m.ConnectedSystemObjects).Returns(MockDbSetConnectedSystemObjects.Object);
        MockJimDbContext.Setup(m => m.ConnectedSystems).Returns(MockDbSetConnectedSystems.Object);
        MockJimDbContext.Setup(m => m.PendingExports).Returns(MockDbSetPendingExports.Object);
        MockJimDbContext.Setup(m => m.PendingExportAttributeValueChanges).Returns(MockDbSetPendingExportAttributeValueChanges.Object);

        // Instantiate Jim using the mocked db context
        Jim = new JimApplication(new PostgresDataRepository(MockJimDbContext.Object));

        // Store references to commonly used objects
        TargetSystem = ConnectedSystemsData.Single(s => s.Name == "Dummy Target System");
        TargetUserType = ConnectedSystemObjectTypesData.Single(t => t.Name == "TARGET_USER");
        DisplayNameAttr = TargetUserType.Attributes.Single(a => a.Name == MockTargetSystemAttributeNames.DisplayName.ToString());
        MailAttr = TargetUserType.Attributes.Single(a => a.Name == MockTargetSystemAttributeNames.Mail.ToString());
    }

    #region Helper Methods

    private ConnectedSystemObject CreateTestCso()
    {
        var cso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = TargetSystem.Id,
            ConnectedSystem = TargetSystem,
            Type = TargetUserType,
            TypeId = TargetUserType.Id,
            Status = ConnectedSystemObjectStatus.Normal,
            AttributeValues = new List<ConnectedSystemObjectAttributeValue>()
        };
        ConnectedSystemObjectsData.Add(cso);
        return cso;
    }

    private PendingExport CreateTestPendingExport(
        ConnectedSystemObject cso,
        PendingExportStatus status = PendingExportStatus.Exported)
    {
        var pendingExport = new PendingExport
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = TargetSystem.Id,
            ConnectedSystem = TargetSystem,
            ConnectedSystemObject = cso,
            Status = status,
            ChangeType = PendingExportChangeType.Update,
            CreatedAt = DateTime.UtcNow,
            AttributeValueChanges = new List<PendingExportAttributeValueChange>()
        };
        PendingExportsData.Add(pendingExport);
        return pendingExport;
    }

    private PendingExportAttributeValueChange CreateTestAttributeChange(
        PendingExport pendingExport,
        ConnectedSystemObjectTypeAttribute attribute,
        string stringValue,
        PendingExportAttributeChangeStatus status = PendingExportAttributeChangeStatus.ExportedPendingConfirmation,
        int exportAttemptCount = 1)
    {
        var attrChange = new PendingExportAttributeValueChange
        {
            Id = Guid.NewGuid(),
            AttributeId = attribute.Id,
            Attribute = attribute,
            ChangeType = PendingExportAttributeChangeType.Update,
            StringValue = stringValue,
            Status = status,
            ExportAttemptCount = exportAttemptCount,
            LastExportedAt = DateTime.UtcNow.AddMinutes(-5)
        };
        pendingExport.AttributeValueChanges.Add(attrChange);
        PendingExportAttributeValueChangesData.Add(attrChange);
        return attrChange;
    }

    private void AddCsoAttributeValue(ConnectedSystemObject cso, ConnectedSystemObjectTypeAttribute attribute, string stringValue)
    {
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            ConnectedSystemObject = cso,
            Attribute = attribute,
            AttributeId = attribute.Id,
            StringValue = stringValue
        });
    }

    #endregion

    #region Single Attribute Change Tests

    /// <summary>
    /// Tests that a single attribute change is confirmed when the imported value matches.
    /// </summary>
    [Test]
    public async Task ReconcileAsync_SingleAttributeChange_ConfirmsWhenValueMatchesAsync()
    {
        // Arrange
        var cso = CreateTestCso();
        var pendingExport = CreateTestPendingExport(cso);
        var attrChange = CreateTestAttributeChange(pendingExport, DisplayNameAttr, "John Doe");

        // Import has the same value
        AddCsoAttributeValue(cso, DisplayNameAttr, "John Doe");

        var service = new PendingExportReconciliationService(Jim);

        // Act
        var result = await service.ReconcileAsync(cso);

        // Assert
        Assert.That(result.ConfirmedChanges.Count, Is.EqualTo(1), "One attribute change should be confirmed");
        Assert.That(result.RetryChanges.Count, Is.EqualTo(0), "No changes should need retry");
        Assert.That(result.FailedChanges.Count, Is.EqualTo(0), "No changes should have failed");
        Assert.That(result.PendingExportDeleted, Is.True, "PendingExport should be deleted when all changes confirmed");
    }

    /// <summary>
    /// Tests that a single attribute change is marked for retry when the imported value doesn't match.
    /// </summary>
    [Test]
    public async Task ReconcileAsync_SingleAttributeChange_MarksForRetryWhenValueDoesNotMatchAsync()
    {
        // Arrange
        var cso = CreateTestCso();
        var pendingExport = CreateTestPendingExport(cso);
        var attrChange = CreateTestAttributeChange(pendingExport, DisplayNameAttr, "John Doe", exportAttemptCount: 1);

        // Import has a different value
        AddCsoAttributeValue(cso, DisplayNameAttr, "Jane Doe");

        var service = new PendingExportReconciliationService(Jim);

        // Act
        var result = await service.ReconcileAsync(cso);

        // Assert
        Assert.That(result.ConfirmedChanges.Count, Is.EqualTo(0), "No attribute changes should be confirmed");
        Assert.That(result.RetryChanges.Count, Is.EqualTo(1), "One change should need retry");
        Assert.That(result.FailedChanges.Count, Is.EqualTo(0), "No changes should have failed yet");
        Assert.That(attrChange.Status, Is.EqualTo(PendingExportAttributeChangeStatus.ExportedNotConfirmed),
            "Attribute change should be marked for retry");
        Assert.That(attrChange.LastImportedValue, Is.EqualTo("Jane Doe"),
            "Last imported value should be captured for debugging");
    }

    /// <summary>
    /// Tests that a single attribute change is marked as Failed when max retries are exceeded.
    /// </summary>
    [Test]
    public async Task ReconcileAsync_SingleAttributeChange_MarksAsFailedWhenMaxRetriesExceededAsync()
    {
        // Arrange
        var cso = CreateTestCso();
        var pendingExport = CreateTestPendingExport(cso);
        var attrChange = CreateTestAttributeChange(
            pendingExport, DisplayNameAttr, "John Doe",
            exportAttemptCount: PendingExportReconciliationService.DefaultMaxRetries);

        // Import has a different value
        AddCsoAttributeValue(cso, DisplayNameAttr, "Wrong Value");

        var service = new PendingExportReconciliationService(Jim);

        // Act
        var result = await service.ReconcileAsync(cso);

        // Assert
        Assert.That(result.ConfirmedChanges.Count, Is.EqualTo(0), "No attribute changes should be confirmed");
        Assert.That(result.RetryChanges.Count, Is.EqualTo(0), "No changes should be marked for retry");
        Assert.That(result.FailedChanges.Count, Is.EqualTo(1), "One change should have failed");
        Assert.That(attrChange.Status, Is.EqualTo(PendingExportAttributeChangeStatus.Failed),
            "Attribute change should be marked as Failed");
    }

    #endregion

    #region Multiple Attribute Change Tests

    /// <summary>
    /// Tests that multiple attribute changes are all confirmed when all values match.
    /// </summary>
    [Test]
    public async Task ReconcileAsync_MultipleAttributeChanges_ConfirmsAllWhenAllMatchAsync()
    {
        // Arrange
        var cso = CreateTestCso();
        var pendingExport = CreateTestPendingExport(cso);
        var displayNameChange = CreateTestAttributeChange(pendingExport, DisplayNameAttr, "John Doe");
        var mailChange = CreateTestAttributeChange(pendingExport, MailAttr, "john@example.com");

        // Import has matching values
        AddCsoAttributeValue(cso, DisplayNameAttr, "John Doe");
        AddCsoAttributeValue(cso, MailAttr, "john@example.com");

        var service = new PendingExportReconciliationService(Jim);

        // Act
        var result = await service.ReconcileAsync(cso);

        // Assert
        Assert.That(result.ConfirmedChanges.Count, Is.EqualTo(2), "Both attribute changes should be confirmed");
        Assert.That(result.RetryChanges.Count, Is.EqualTo(0), "No changes should need retry");
        Assert.That(result.PendingExportDeleted, Is.True, "PendingExport should be deleted");
    }

    /// <summary>
    /// Tests that some attribute changes confirm and others are marked for retry.
    /// </summary>
    [Test]
    public async Task ReconcileAsync_MultipleAttributeChanges_SomeConfirmedSomeRetryAsync()
    {
        // Arrange
        var cso = CreateTestCso();
        var pendingExport = CreateTestPendingExport(cso);
        var displayNameChange = CreateTestAttributeChange(pendingExport, DisplayNameAttr, "John Doe");
        var mailChange = CreateTestAttributeChange(pendingExport, MailAttr, "john@example.com");

        // Import has one matching value and one different
        AddCsoAttributeValue(cso, DisplayNameAttr, "John Doe"); // Matches
        AddCsoAttributeValue(cso, MailAttr, "wrong@example.com"); // Doesn't match

        var service = new PendingExportReconciliationService(Jim);

        // Act
        var result = await service.ReconcileAsync(cso);

        // Assert
        Assert.That(result.ConfirmedChanges.Count, Is.EqualTo(1), "One attribute change should be confirmed");
        Assert.That(result.RetryChanges.Count, Is.EqualTo(1), "One change should need retry");
        Assert.That(result.PendingExportDeleted, Is.False, "PendingExport should NOT be deleted");
        Assert.That(displayNameChange.Status, Is.EqualTo(PendingExportAttributeChangeStatus.ExportedPendingConfirmation),
            "Confirmed change should have been removed from the pending export (not in changes list anymore)");
        Assert.That(mailChange.Status, Is.EqualTo(PendingExportAttributeChangeStatus.ExportedNotConfirmed),
            "Non-matching change should be marked for retry");
    }

    /// <summary>
    /// Tests retry scenario where a problematic change eventually confirms.
    /// </summary>
    [Test]
    public async Task ReconcileAsync_RetryScenario_ProblemChangeResolvesOnSecondAttemptAsync()
    {
        // Arrange
        var cso = CreateTestCso();
        var pendingExport = CreateTestPendingExport(cso);
        var mailChange = CreateTestAttributeChange(
            pendingExport, MailAttr, "john@example.com",
            status: PendingExportAttributeChangeStatus.ExportedPendingConfirmation,
            exportAttemptCount: 2);

        // After second export attempt, import now shows the correct value
        AddCsoAttributeValue(cso, MailAttr, "john@example.com");

        var service = new PendingExportReconciliationService(Jim);

        // Act
        var result = await service.ReconcileAsync(cso);

        // Assert
        Assert.That(result.ConfirmedChanges.Count, Is.EqualTo(1), "Attribute change should now be confirmed");
        Assert.That(result.RetryChanges.Count, Is.EqualTo(0), "No changes should need retry");
        Assert.That(result.PendingExportDeleted, Is.True, "PendingExport should be deleted");
    }

    /// <summary>
    /// Tests scenario where new attribute changes are added while existing ones are still pending.
    /// </summary>
    [Test]
    public async Task ReconcileAsync_NewChangesAddedWhileExistingPending_HandlesCorrectlyAsync()
    {
        // Arrange
        var cso = CreateTestCso();
        var pendingExport = CreateTestPendingExport(cso);

        // Existing change that was already exported (awaiting confirmation)
        var existingChange = CreateTestAttributeChange(
            pendingExport, DisplayNameAttr, "John Doe",
            status: PendingExportAttributeChangeStatus.ExportedPendingConfirmation,
            exportAttemptCount: 1);

        // New change that was just added (pending export)
        var newChange = CreateTestAttributeChange(
            pendingExport, MailAttr, "john@example.com",
            status: PendingExportAttributeChangeStatus.Pending,
            exportAttemptCount: 0);

        // Import confirms the existing change
        AddCsoAttributeValue(cso, DisplayNameAttr, "John Doe");
        // New change hasn't been exported yet, so it won't be on the CSO

        var service = new PendingExportReconciliationService(Jim);

        // Act
        var result = await service.ReconcileAsync(cso);

        // Assert
        Assert.That(result.ConfirmedChanges.Count, Is.EqualTo(1),
            "Existing exported change should be confirmed");
        Assert.That(result.RetryChanges.Count, Is.EqualTo(0),
            "New pending changes shouldn't be processed during reconciliation");
        Assert.That(result.PendingExportDeleted, Is.False,
            "PendingExport should NOT be deleted because there are pending changes");

        // The pending change should still be in Pending status
        Assert.That(newChange.Status, Is.EqualTo(PendingExportAttributeChangeStatus.Pending),
            "New pending change should remain in Pending status");
    }

    #endregion

    #region Add/Remove Change Type Tests

    /// <summary>
    /// Tests that Add change type is confirmed when value exists on CSO.
    /// </summary>
    [Test]
    public async Task ReconcileAsync_AddChangeType_ConfirmsWhenValueExistsAsync()
    {
        // Arrange
        var cso = CreateTestCso();
        var pendingExport = CreateTestPendingExport(cso);
        var attrChange = new PendingExportAttributeValueChange
        {
            Id = Guid.NewGuid(),
            AttributeId = DisplayNameAttr.Id,
            Attribute = DisplayNameAttr,
            ChangeType = PendingExportAttributeChangeType.Add,
            StringValue = "John Doe",
            Status = PendingExportAttributeChangeStatus.ExportedPendingConfirmation,
            ExportAttemptCount = 1
        };
        pendingExport.AttributeValueChanges.Add(attrChange);

        // Value exists on CSO
        AddCsoAttributeValue(cso, DisplayNameAttr, "John Doe");

        var service = new PendingExportReconciliationService(Jim);

        // Act
        var result = await service.ReconcileAsync(cso);

        // Assert
        Assert.That(result.ConfirmedChanges.Count, Is.EqualTo(1));
    }

    /// <summary>
    /// Tests that Remove change type is confirmed when value does NOT exist on CSO.
    /// </summary>
    [Test]
    public async Task ReconcileAsync_RemoveChangeType_ConfirmsWhenValueRemovedAsync()
    {
        // Arrange
        var cso = CreateTestCso();
        var pendingExport = CreateTestPendingExport(cso);
        var attrChange = new PendingExportAttributeValueChange
        {
            Id = Guid.NewGuid(),
            AttributeId = DisplayNameAttr.Id,
            Attribute = DisplayNameAttr,
            ChangeType = PendingExportAttributeChangeType.Remove,
            StringValue = "Old Value",
            Status = PendingExportAttributeChangeStatus.ExportedPendingConfirmation,
            ExportAttemptCount = 1
        };
        pendingExport.AttributeValueChanges.Add(attrChange);

        // Value does NOT exist on CSO (was removed)
        // Don't add any value for DisplayNameAttr

        var service = new PendingExportReconciliationService(Jim);

        // Act
        var result = await service.ReconcileAsync(cso);

        // Assert
        Assert.That(result.ConfirmedChanges.Count, Is.EqualTo(1), "Remove should be confirmed when value is gone");
    }

    /// <summary>
    /// Tests that Remove change type is NOT confirmed when value still exists on CSO.
    /// </summary>
    [Test]
    public async Task ReconcileAsync_RemoveChangeType_RetriesWhenValueStillExistsAsync()
    {
        // Arrange
        var cso = CreateTestCso();
        var pendingExport = CreateTestPendingExport(cso);
        var attrChange = new PendingExportAttributeValueChange
        {
            Id = Guid.NewGuid(),
            AttributeId = DisplayNameAttr.Id,
            Attribute = DisplayNameAttr,
            ChangeType = PendingExportAttributeChangeType.Remove,
            StringValue = "Old Value",
            Status = PendingExportAttributeChangeStatus.ExportedPendingConfirmation,
            ExportAttemptCount = 1
        };
        pendingExport.AttributeValueChanges.Add(attrChange);

        // Value STILL exists on CSO (wasn't removed)
        AddCsoAttributeValue(cso, DisplayNameAttr, "Old Value");

        var service = new PendingExportReconciliationService(Jim);

        // Act
        var result = await service.ReconcileAsync(cso);

        // Assert
        Assert.That(result.ConfirmedChanges.Count, Is.EqualTo(0));
        Assert.That(result.RetryChanges.Count, Is.EqualTo(1), "Should retry when value wasn't removed");
    }

    /// <summary>
    /// Tests that RemoveAll change type is confirmed when no values exist on CSO.
    /// </summary>
    [Test]
    public async Task ReconcileAsync_RemoveAllChangeType_ConfirmsWhenNoValuesExistAsync()
    {
        // Arrange
        var cso = CreateTestCso();
        var pendingExport = CreateTestPendingExport(cso);
        var attrChange = new PendingExportAttributeValueChange
        {
            Id = Guid.NewGuid(),
            AttributeId = DisplayNameAttr.Id,
            Attribute = DisplayNameAttr,
            ChangeType = PendingExportAttributeChangeType.RemoveAll,
            Status = PendingExportAttributeChangeStatus.ExportedPendingConfirmation,
            ExportAttemptCount = 1
        };
        pendingExport.AttributeValueChanges.Add(attrChange);

        // No values exist for the attribute
        // Don't add any value for DisplayNameAttr

        var service = new PendingExportReconciliationService(Jim);

        // Act
        var result = await service.ReconcileAsync(cso);

        // Assert
        Assert.That(result.ConfirmedChanges.Count, Is.EqualTo(1), "RemoveAll should be confirmed when no values exist");
    }

    #endregion

    #region Edge Cases

    /// <summary>
    /// Tests that reconciliation is skipped for PendingExports not in Exported status.
    /// </summary>
    [Test]
    public async Task ReconcileAsync_PendingExportNotInExportedStatus_SkipsReconciliationAsync()
    {
        // Arrange
        var cso = CreateTestCso();
        var pendingExport = CreateTestPendingExport(cso, PendingExportStatus.Pending);
        var attrChange = CreateTestAttributeChange(
            pendingExport, DisplayNameAttr, "John Doe",
            status: PendingExportAttributeChangeStatus.Pending);

        AddCsoAttributeValue(cso, DisplayNameAttr, "John Doe");

        var service = new PendingExportReconciliationService(Jim);

        // Act
        var result = await service.ReconcileAsync(cso);

        // Assert
        Assert.That(result.HasChanges, Is.False, "No reconciliation should happen for Pending exports");
        Assert.That(result.PendingExportDeleted, Is.False);
    }

    /// <summary>
    /// Tests that reconciliation is skipped when no PendingExport exists for the CSO.
    /// </summary>
    [Test]
    public async Task ReconcileAsync_NoPendingExport_ReturnsEmptyResultAsync()
    {
        // Arrange
        var cso = CreateTestCso();
        // Don't create any pending export

        var service = new PendingExportReconciliationService(Jim);

        // Act
        var result = await service.ReconcileAsync(cso);

        // Assert
        Assert.That(result.HasChanges, Is.False);
        Assert.That(result.PendingExportDeleted, Is.False);
    }

    /// <summary>
    /// Tests that PendingExport status is updated to Failed when all attribute changes fail.
    /// </summary>
    [Test]
    public async Task ReconcileAsync_AllAttributeChangesFail_PendingExportStatusIsFailedAsync()
    {
        // Arrange
        var cso = CreateTestCso();
        var pendingExport = CreateTestPendingExport(cso);

        // Both changes have hit max retries
        var displayNameChange = CreateTestAttributeChange(
            pendingExport, DisplayNameAttr, "John Doe",
            exportAttemptCount: PendingExportReconciliationService.DefaultMaxRetries);
        var mailChange = CreateTestAttributeChange(
            pendingExport, MailAttr, "john@example.com",
            exportAttemptCount: PendingExportReconciliationService.DefaultMaxRetries);

        // Import has different values
        AddCsoAttributeValue(cso, DisplayNameAttr, "Wrong Name");
        AddCsoAttributeValue(cso, MailAttr, "wrong@example.com");

        var service = new PendingExportReconciliationService(Jim);

        // Act
        var result = await service.ReconcileAsync(cso);

        // Assert
        Assert.That(result.FailedChanges.Count, Is.EqualTo(2), "Both changes should have failed");
        Assert.That(pendingExport.Status, Is.EqualTo(PendingExportStatus.Failed),
            "PendingExport should be marked as Failed");
    }

    /// <summary>
    /// Tests that PendingExport status is updated to ExportNotConfirmed when there are changes needing retry.
    /// </summary>
    [Test]
    public async Task ReconcileAsync_SomeChangesNeedRetry_PendingExportStatusIsExportNotConfirmedAsync()
    {
        // Arrange
        var cso = CreateTestCso();
        var pendingExport = CreateTestPendingExport(cso);
        var attrChange = CreateTestAttributeChange(
            pendingExport, DisplayNameAttr, "John Doe",
            exportAttemptCount: 1);

        // Import has different value
        AddCsoAttributeValue(cso, DisplayNameAttr, "Wrong Name");

        var service = new PendingExportReconciliationService(Jim);

        // Act
        var result = await service.ReconcileAsync(cso);

        // Assert
        Assert.That(result.RetryChanges.Count, Is.EqualTo(1));
        Assert.That(pendingExport.Status, Is.EqualTo(PendingExportStatus.ExportNotConfirmed),
            "PendingExport should be marked as ExportNotConfirmed for retry");
    }

    #endregion

    #region Create to Update Transition Tests

    /// <summary>
    /// Tests that a Create pending export transitions to Update when the Secondary External ID attribute
    /// is confirmed but other attribute changes remain. This is critical for retry scenarios where the
    /// object was successfully created (Secondary External ID confirmed) but some attributes weren't
    /// applied correctly. Without this transition, retries would fail because connectors require the
    /// Secondary External ID in attribute changes for Create operations.
    /// </summary>
    [Test]
    public async Task ReconcileAsync_CreateWithSecondaryExternalIdConfirmed_TransitionsToUpdateAsync()
    {
        // Arrange
        var cso = CreateTestCso();
        var pendingExport = CreateTestPendingExport(cso, PendingExportStatus.Exported);
        pendingExport.ChangeType = PendingExportChangeType.Create; // This is a Create operation

        // Add Secondary External ID attribute (e.g., distinguishedName for LDAP systems)
        var secondaryExtIdAttr = new ConnectedSystemObjectTypeAttribute
        {
            Id = 999,
            Name = "distinguishedName",
            Type = AttributeDataType.Text,
            ConnectedSystemObjectType = TargetUserType,
            IsSecondaryExternalId = true
        };
        TargetUserType.Attributes.Add(secondaryExtIdAttr);

        // Secondary External ID attribute change - will be confirmed
        var secondaryExtIdChange = CreateTestAttributeChange(pendingExport, secondaryExtIdAttr, "CN=John Doe,OU=Users,DC=test,DC=local");

        // Other attribute change - will NOT be confirmed (simulating a retry scenario)
        var displayNameChange = CreateTestAttributeChange(pendingExport, DisplayNameAttr, "John Doe");

        // Import confirms the Secondary External ID (object was created successfully)
        AddCsoAttributeValue(cso, secondaryExtIdAttr, "CN=John Doe,OU=Users,DC=test,DC=local");
        // But displayName doesn't match - needs retry
        AddCsoAttributeValue(cso, DisplayNameAttr, "Wrong Name");

        var service = new PendingExportReconciliationService(Jim);

        // Act
        var result = await service.ReconcileAsync(cso);

        // Assert
        Assert.That(result.ConfirmedChanges.Count, Is.EqualTo(1), "Secondary External ID should be confirmed");
        Assert.That(result.RetryChanges.Count, Is.EqualTo(1), "DisplayName should need retry");
        Assert.That(pendingExport.ChangeType, Is.EqualTo(PendingExportChangeType.Update),
            "PendingExport should transition from Create to Update after Secondary External ID is confirmed");
        Assert.That(pendingExport.AttributeValueChanges.Count, Is.EqualTo(1),
            "Only the unconfirmed attribute change should remain");
    }

    /// <summary>
    /// Tests that a Create pending export does NOT transition to Update when the Secondary External ID
    /// is NOT confirmed.
    /// </summary>
    [Test]
    public async Task ReconcileAsync_CreateWithSecondaryExternalIdNotConfirmed_RemainsCreateAsync()
    {
        // Arrange
        var cso = CreateTestCso();
        var pendingExport = CreateTestPendingExport(cso, PendingExportStatus.Exported);
        pendingExport.ChangeType = PendingExportChangeType.Create;

        // Add Secondary External ID attribute
        var secondaryExtIdAttr = new ConnectedSystemObjectTypeAttribute
        {
            Id = 998,
            Name = "distinguishedName",
            Type = AttributeDataType.Text,
            ConnectedSystemObjectType = TargetUserType,
            IsSecondaryExternalId = true
        };
        TargetUserType.Attributes.Add(secondaryExtIdAttr);

        // Secondary External ID attribute change - will NOT be confirmed
        var secondaryExtIdChange = CreateTestAttributeChange(pendingExport, secondaryExtIdAttr, "CN=John Doe,OU=Users,DC=test,DC=local");

        // Other attribute change - also won't be confirmed
        var displayNameChange = CreateTestAttributeChange(pendingExport, DisplayNameAttr, "John Doe");

        // Nothing is on the CSO (object creation failed)
        // Don't add any attribute values

        var service = new PendingExportReconciliationService(Jim);

        // Act
        var result = await service.ReconcileAsync(cso);

        // Assert
        Assert.That(result.ConfirmedChanges.Count, Is.EqualTo(0), "Nothing should be confirmed");
        Assert.That(result.RetryChanges.Count, Is.EqualTo(2), "Both changes should need retry");
        Assert.That(pendingExport.ChangeType, Is.EqualTo(PendingExportChangeType.Create),
            "PendingExport should remain as Create when Secondary External ID is not confirmed");
    }

    /// <summary>
    /// Tests that an Update pending export is not affected by the Create->Update transition logic.
    /// </summary>
    [Test]
    public async Task ReconcileAsync_UpdateWithSecondaryExternalIdConfirmed_RemainsUpdateAsync()
    {
        // Arrange
        var cso = CreateTestCso();
        var pendingExport = CreateTestPendingExport(cso, PendingExportStatus.Exported);
        pendingExport.ChangeType = PendingExportChangeType.Update; // Already an Update

        // Add Secondary External ID attribute
        var secondaryExtIdAttr = new ConnectedSystemObjectTypeAttribute
        {
            Id = 997,
            Name = "distinguishedName",
            Type = AttributeDataType.Text,
            ConnectedSystemObjectType = TargetUserType,
            IsSecondaryExternalId = true
        };
        TargetUserType.Attributes.Add(secondaryExtIdAttr);

        // Secondary External ID attribute change (rename scenario) - will be confirmed
        var secondaryExtIdChange = CreateTestAttributeChange(pendingExport, secondaryExtIdAttr, "CN=John Doe,OU=Users,DC=test,DC=local");

        // Import confirms the Secondary External ID
        AddCsoAttributeValue(cso, secondaryExtIdAttr, "CN=John Doe,OU=Users,DC=test,DC=local");

        var service = new PendingExportReconciliationService(Jim);

        // Act
        var result = await service.ReconcileAsync(cso);

        // Assert
        Assert.That(result.ConfirmedChanges.Count, Is.EqualTo(1), "Secondary External ID should be confirmed");
        Assert.That(pendingExport.ChangeType, Is.EqualTo(PendingExportChangeType.Update),
            "PendingExport should remain as Update");
        Assert.That(result.PendingExportDeleted, Is.True,
            "PendingExport should be deleted when all changes confirmed");
    }

    /// <summary>
    /// Tests that a Create with only Secondary External ID confirmed (and no other changes) is deleted,
    /// not transitioned.
    /// </summary>
    [Test]
    public async Task ReconcileAsync_CreateWithOnlySecondaryExternalIdConfirmed_IsDeletedAsync()
    {
        // Arrange
        var cso = CreateTestCso();
        var pendingExport = CreateTestPendingExport(cso, PendingExportStatus.Exported);
        pendingExport.ChangeType = PendingExportChangeType.Create;

        // Add Secondary External ID attribute
        var secondaryExtIdAttr = new ConnectedSystemObjectTypeAttribute
        {
            Id = 996,
            Name = "distinguishedName",
            Type = AttributeDataType.Text,
            ConnectedSystemObjectType = TargetUserType,
            IsSecondaryExternalId = true
        };
        TargetUserType.Attributes.Add(secondaryExtIdAttr);

        // Only the Secondary External ID attribute change
        var secondaryExtIdChange = CreateTestAttributeChange(pendingExport, secondaryExtIdAttr, "CN=John Doe,OU=Users,DC=test,DC=local");

        // Import confirms the Secondary External ID
        AddCsoAttributeValue(cso, secondaryExtIdAttr, "CN=John Doe,OU=Users,DC=test,DC=local");

        var service = new PendingExportReconciliationService(Jim);

        // Act
        var result = await service.ReconcileAsync(cso);

        // Assert
        Assert.That(result.ConfirmedChanges.Count, Is.EqualTo(1), "Secondary External ID should be confirmed");
        Assert.That(result.PendingExportDeleted, Is.True,
            "PendingExport should be deleted when all changes (just Secondary External ID) are confirmed");
    }

    #endregion

    #region Integer Value Tests

    /// <summary>
    /// Tests that integer attribute values are correctly compared.
    /// </summary>
    [Test]
    public async Task ReconcileAsync_IntegerValue_ConfirmsWhenMatchesAsync()
    {
        // Arrange
        var cso = CreateTestCso();
        var pendingExport = CreateTestPendingExport(cso);
        var uacAttr = TargetUserType.Attributes.Single(a => a.Name == MockTargetSystemAttributeNames.UserAccountControl.ToString());

        var attrChange = new PendingExportAttributeValueChange
        {
            Id = Guid.NewGuid(),
            AttributeId = uacAttr.Id,
            Attribute = uacAttr,
            ChangeType = PendingExportAttributeChangeType.Update,
            IntValue = 512,
            Status = PendingExportAttributeChangeStatus.ExportedPendingConfirmation,
            ExportAttemptCount = 1
        };
        pendingExport.AttributeValueChanges.Add(attrChange);

        // Add matching integer value
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            ConnectedSystemObject = cso,
            Attribute = uacAttr,
            AttributeId = uacAttr.Id,
            IntValue = 512
        });

        var service = new PendingExportReconciliationService(Jim);

        // Act
        var result = await service.ReconcileAsync(cso);

        // Assert
        Assert.That(result.ConfirmedChanges.Count, Is.EqualTo(1));
    }

    #endregion

    #region Boolean Value Tests

    /// <summary>
    /// Tests that boolean attribute values are correctly compared when they match.
    /// </summary>
    [Test]
    public async Task ReconcileAsync_BooleanValue_ConfirmsWhenMatchesAsync()
    {
        // Arrange
        var cso = CreateTestCso();
        var pendingExport = CreateTestPendingExport(cso);

        // Add a boolean attribute to the type
        var isActiveAttr = new ConnectedSystemObjectTypeAttribute
        {
            Id = 1001,
            Name = "isActive",
            Type = AttributeDataType.Boolean,
            ConnectedSystemObjectType = TargetUserType
        };
        TargetUserType.Attributes.Add(isActiveAttr);

        var attrChange = new PendingExportAttributeValueChange
        {
            Id = Guid.NewGuid(),
            AttributeId = isActiveAttr.Id,
            Attribute = isActiveAttr,
            ChangeType = PendingExportAttributeChangeType.Update,
            BoolValue = true,
            Status = PendingExportAttributeChangeStatus.ExportedPendingConfirmation,
            ExportAttemptCount = 1
        };
        pendingExport.AttributeValueChanges.Add(attrChange);

        // Add matching boolean value
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            ConnectedSystemObject = cso,
            Attribute = isActiveAttr,
            AttributeId = isActiveAttr.Id,
            BoolValue = true
        });

        var service = new PendingExportReconciliationService(Jim);

        // Act
        var result = await service.ReconcileAsync(cso);

        // Assert
        Assert.That(result.ConfirmedChanges.Count, Is.EqualTo(1), "Boolean value should be confirmed when matches");
    }

    /// <summary>
    /// Tests that boolean attribute values are marked for retry when they don't match.
    /// </summary>
    [Test]
    public async Task ReconcileAsync_BooleanValue_RetriesWhenDoesNotMatchAsync()
    {
        // Arrange
        var cso = CreateTestCso();
        var pendingExport = CreateTestPendingExport(cso);

        var isActiveAttr = new ConnectedSystemObjectTypeAttribute
        {
            Id = 1002,
            Name = "isActive",
            Type = AttributeDataType.Boolean,
            ConnectedSystemObjectType = TargetUserType
        };
        TargetUserType.Attributes.Add(isActiveAttr);

        var attrChange = new PendingExportAttributeValueChange
        {
            Id = Guid.NewGuid(),
            AttributeId = isActiveAttr.Id,
            Attribute = isActiveAttr,
            ChangeType = PendingExportAttributeChangeType.Update,
            BoolValue = true,
            Status = PendingExportAttributeChangeStatus.ExportedPendingConfirmation,
            ExportAttemptCount = 1
        };
        pendingExport.AttributeValueChanges.Add(attrChange);

        // Add non-matching boolean value
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            ConnectedSystemObject = cso,
            Attribute = isActiveAttr,
            AttributeId = isActiveAttr.Id,
            BoolValue = false // Different value
        });

        var service = new PendingExportReconciliationService(Jim);

        // Act
        var result = await service.ReconcileAsync(cso);

        // Assert
        Assert.That(result.ConfirmedChanges.Count, Is.EqualTo(0), "Boolean value should not be confirmed when different");
        Assert.That(result.RetryChanges.Count, Is.EqualTo(1), "Boolean value should be marked for retry");
    }

    /// <summary>
    /// Tests that false boolean values are correctly compared.
    /// </summary>
    [Test]
    public async Task ReconcileAsync_BooleanFalseValue_ConfirmsWhenMatchesAsync()
    {
        // Arrange
        var cso = CreateTestCso();
        var pendingExport = CreateTestPendingExport(cso);

        var isDisabledAttr = new ConnectedSystemObjectTypeAttribute
        {
            Id = 1003,
            Name = "isDisabled",
            Type = AttributeDataType.Boolean,
            ConnectedSystemObjectType = TargetUserType
        };
        TargetUserType.Attributes.Add(isDisabledAttr);

        var attrChange = new PendingExportAttributeValueChange
        {
            Id = Guid.NewGuid(),
            AttributeId = isDisabledAttr.Id,
            Attribute = isDisabledAttr,
            ChangeType = PendingExportAttributeChangeType.Update,
            BoolValue = false,
            Status = PendingExportAttributeChangeStatus.ExportedPendingConfirmation,
            ExportAttemptCount = 1
        };
        pendingExport.AttributeValueChanges.Add(attrChange);

        // Add matching boolean value (false)
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            ConnectedSystemObject = cso,
            Attribute = isDisabledAttr,
            AttributeId = isDisabledAttr.Id,
            BoolValue = false
        });

        var service = new PendingExportReconciliationService(Jim);

        // Act
        var result = await service.ReconcileAsync(cso);

        // Assert
        Assert.That(result.ConfirmedChanges.Count, Is.EqualTo(1), "Boolean false value should be confirmed when matches");
    }

    #endregion

    #region Guid Value Tests

    /// <summary>
    /// Tests that GUID attribute values are correctly compared when they match.
    /// </summary>
    [Test]
    public async Task ReconcileAsync_GuidValue_ConfirmsWhenMatchesAsync()
    {
        // Arrange
        var cso = CreateTestCso();
        var pendingExport = CreateTestPendingExport(cso);

        var objectGuidAttr = new ConnectedSystemObjectTypeAttribute
        {
            Id = 1004,
            Name = "objectGuid",
            Type = AttributeDataType.Guid,
            ConnectedSystemObjectType = TargetUserType
        };
        TargetUserType.Attributes.Add(objectGuidAttr);

        var testGuid = Guid.NewGuid();

        var attrChange = new PendingExportAttributeValueChange
        {
            Id = Guid.NewGuid(),
            AttributeId = objectGuidAttr.Id,
            Attribute = objectGuidAttr,
            ChangeType = PendingExportAttributeChangeType.Update,
            GuidValue = testGuid,
            Status = PendingExportAttributeChangeStatus.ExportedPendingConfirmation,
            ExportAttemptCount = 1
        };
        pendingExport.AttributeValueChanges.Add(attrChange);

        // Add matching GUID value
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            ConnectedSystemObject = cso,
            Attribute = objectGuidAttr,
            AttributeId = objectGuidAttr.Id,
            GuidValue = testGuid
        });

        var service = new PendingExportReconciliationService(Jim);

        // Act
        var result = await service.ReconcileAsync(cso);

        // Assert
        Assert.That(result.ConfirmedChanges.Count, Is.EqualTo(1), "GUID value should be confirmed when matches");
    }

    /// <summary>
    /// Tests that GUID attribute values are marked for retry when they don't match.
    /// </summary>
    [Test]
    public async Task ReconcileAsync_GuidValue_RetriesWhenDoesNotMatchAsync()
    {
        // Arrange
        var cso = CreateTestCso();
        var pendingExport = CreateTestPendingExport(cso);

        var objectGuidAttr = new ConnectedSystemObjectTypeAttribute
        {
            Id = 1005,
            Name = "objectGuid",
            Type = AttributeDataType.Guid,
            ConnectedSystemObjectType = TargetUserType
        };
        TargetUserType.Attributes.Add(objectGuidAttr);

        var attrChange = new PendingExportAttributeValueChange
        {
            Id = Guid.NewGuid(),
            AttributeId = objectGuidAttr.Id,
            Attribute = objectGuidAttr,
            ChangeType = PendingExportAttributeChangeType.Update,
            GuidValue = Guid.NewGuid(), // One GUID
            Status = PendingExportAttributeChangeStatus.ExportedPendingConfirmation,
            ExportAttemptCount = 1
        };
        pendingExport.AttributeValueChanges.Add(attrChange);

        // Add different GUID value
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            ConnectedSystemObject = cso,
            Attribute = objectGuidAttr,
            AttributeId = objectGuidAttr.Id,
            GuidValue = Guid.NewGuid() // Different GUID
        });

        var service = new PendingExportReconciliationService(Jim);

        // Act
        var result = await service.ReconcileAsync(cso);

        // Assert
        Assert.That(result.ConfirmedChanges.Count, Is.EqualTo(0), "GUID value should not be confirmed when different");
        Assert.That(result.RetryChanges.Count, Is.EqualTo(1), "GUID value should be marked for retry");
    }

    /// <summary>
    /// Tests that empty GUID values are correctly compared.
    /// </summary>
    [Test]
    public async Task ReconcileAsync_EmptyGuidValue_ConfirmsWhenMatchesAsync()
    {
        // Arrange
        var cso = CreateTestCso();
        var pendingExport = CreateTestPendingExport(cso);

        var objectGuidAttr = new ConnectedSystemObjectTypeAttribute
        {
            Id = 1006,
            Name = "objectGuid",
            Type = AttributeDataType.Guid,
            ConnectedSystemObjectType = TargetUserType
        };
        TargetUserType.Attributes.Add(objectGuidAttr);

        var attrChange = new PendingExportAttributeValueChange
        {
            Id = Guid.NewGuid(),
            AttributeId = objectGuidAttr.Id,
            Attribute = objectGuidAttr,
            ChangeType = PendingExportAttributeChangeType.Update,
            GuidValue = Guid.Empty,
            Status = PendingExportAttributeChangeStatus.ExportedPendingConfirmation,
            ExportAttemptCount = 1
        };
        pendingExport.AttributeValueChanges.Add(attrChange);

        // Add matching empty GUID value
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            ConnectedSystemObject = cso,
            Attribute = objectGuidAttr,
            AttributeId = objectGuidAttr.Id,
            GuidValue = Guid.Empty
        });

        var service = new PendingExportReconciliationService(Jim);

        // Act
        var result = await service.ReconcileAsync(cso);

        // Assert
        Assert.That(result.ConfirmedChanges.Count, Is.EqualTo(1), "Empty GUID value should be confirmed when matches");
    }

    #endregion

    #region LongNumber Value Tests

    /// <summary>
    /// Tests that LongNumber (Int64) attribute values are correctly compared when they match.
    /// This is critical for AD attributes like accountExpires that use large integer values.
    /// </summary>
    [Test]
    public async Task ReconcileAsync_LongNumberValue_ConfirmsWhenMatchesAsync()
    {
        // Arrange
        var cso = CreateTestCso();
        var pendingExport = CreateTestPendingExport(cso);
        var accountExpiresAttr = TargetUserType.Attributes.Single(a => a.Name == "accountExpires");

        var attrChange = new PendingExportAttributeValueChange
        {
            Id = Guid.NewGuid(),
            AttributeId = accountExpiresAttr.Id,
            Attribute = accountExpiresAttr,
            ChangeType = PendingExportAttributeChangeType.Update,
            LongValue = 133456789012345678L, // A specific file time value
            Status = PendingExportAttributeChangeStatus.ExportedPendingConfirmation,
            ExportAttemptCount = 1
        };
        pendingExport.AttributeValueChanges.Add(attrChange);

        // Add matching long value
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            ConnectedSystemObject = cso,
            Attribute = accountExpiresAttr,
            AttributeId = accountExpiresAttr.Id,
            LongValue = 133456789012345678L
        });

        var service = new PendingExportReconciliationService(Jim);

        // Act
        var result = await service.ReconcileAsync(cso);

        // Assert
        Assert.That(result.ConfirmedChanges.Count, Is.EqualTo(1), "LongNumber value should be confirmed when matches");
    }

    /// <summary>
    /// Tests that LongNumber attribute values are marked for retry when they don't match.
    /// </summary>
    [Test]
    public async Task ReconcileAsync_LongNumberValue_RetriesWhenDoesNotMatchAsync()
    {
        // Arrange
        var cso = CreateTestCso();
        var pendingExport = CreateTestPendingExport(cso);
        var accountExpiresAttr = TargetUserType.Attributes.Single(a => a.Name == "accountExpires");

        var attrChange = new PendingExportAttributeValueChange
        {
            Id = Guid.NewGuid(),
            AttributeId = accountExpiresAttr.Id,
            Attribute = accountExpiresAttr,
            ChangeType = PendingExportAttributeChangeType.Update,
            LongValue = 133456789012345678L,
            Status = PendingExportAttributeChangeStatus.ExportedPendingConfirmation,
            ExportAttemptCount = 1
        };
        pendingExport.AttributeValueChanges.Add(attrChange);

        // Add different long value
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            ConnectedSystemObject = cso,
            Attribute = accountExpiresAttr,
            AttributeId = accountExpiresAttr.Id,
            LongValue = 9223372036854775807L // Different value (never expires)
        });

        var service = new PendingExportReconciliationService(Jim);

        // Act
        var result = await service.ReconcileAsync(cso);

        // Assert
        Assert.That(result.ConfirmedChanges.Count, Is.EqualTo(0), "LongNumber value should not be confirmed when different");
        Assert.That(result.RetryChanges.Count, Is.EqualTo(1), "LongNumber value should be marked for retry");
    }

    /// <summary>
    /// Tests that Int64.MaxValue (never expires) value for accountExpires is correctly matched.
    /// This is the default value used by AD when accountExpires is "not set".
    /// </summary>
    [Test]
    public async Task ReconcileAsync_AccountExpiresNeverExpires_ConfirmsWhenMatchesAsync()
    {
        // Arrange
        var cso = CreateTestCso();
        var pendingExport = CreateTestPendingExport(cso);
        var accountExpiresAttr = TargetUserType.Attributes.Single(a => a.Name == "accountExpires");

        // This represents the "never expires" value that the LDAP connector substitutes
        // when a sync rule returns null for accountExpires
        var attrChange = new PendingExportAttributeValueChange
        {
            Id = Guid.NewGuid(),
            AttributeId = accountExpiresAttr.Id,
            Attribute = accountExpiresAttr,
            ChangeType = PendingExportAttributeChangeType.Update,
            LongValue = long.MaxValue, // 9223372036854775807 = "never expires"
            Status = PendingExportAttributeChangeStatus.ExportedPendingConfirmation,
            ExportAttemptCount = 1
        };
        pendingExport.AttributeValueChanges.Add(attrChange);

        // CSO has the "never expires" value (as it would after import from AD)
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            ConnectedSystemObject = cso,
            Attribute = accountExpiresAttr,
            AttributeId = accountExpiresAttr.Id,
            LongValue = long.MaxValue
        });

        var service = new PendingExportReconciliationService(Jim);

        // Act
        var result = await service.ReconcileAsync(cso);

        // Assert
        Assert.That(result.ConfirmedChanges.Count, Is.EqualTo(1),
            "accountExpires 'never expires' value should be confirmed when matches");
    }

    #endregion

    #region Protected Attribute Substitution Tests

    /// <summary>
    /// Tests the scenario where a protected attribute (accountExpires) had its value substituted
    /// by the LDAP connector. The pending export's LongValue should have been updated to the
    /// substituted value, allowing reconciliation to confirm successfully.
    ///
    /// This simulates what happens when:
    /// 1. Sync rule evaluates ToFileTime(mv["Employee End Date"]) and returns null
    /// 2. Drift detection creates a pending export with no value (clearing the attribute)
    /// 3. LDAP connector substitutes the "never expires" default (9223372036854775807)
    /// 4. LDAP connector updates the PendingExportAttributeValueChange.LongValue
    /// 5. Confirming import reads the value from AD
    /// 6. Reconciliation confirms because LongValue now matches the CSO value
    /// </summary>
    [Test]
    public async Task ReconcileAsync_ProtectedAttributeSubstituted_ConfirmsAfterSubstitutionAsync()
    {
        // Arrange
        var cso = CreateTestCso();
        var pendingExport = CreateTestPendingExport(cso);
        var accountExpiresAttr = TargetUserType.Attributes.Single(a => a.Name == "accountExpires");

        // Simulating what the LDAP connector does after substitution:
        // The pending export attribute change has been updated with the substituted value
        var attrChange = new PendingExportAttributeValueChange
        {
            Id = Guid.NewGuid(),
            AttributeId = accountExpiresAttr.Id,
            Attribute = accountExpiresAttr,
            ChangeType = PendingExportAttributeChangeType.Update,
            // After LDAP connector substitution, LongValue is set to the "never expires" default
            LongValue = 9223372036854775807L,
            Status = PendingExportAttributeChangeStatus.ExportedPendingConfirmation,
            ExportAttemptCount = 1
        };
        pendingExport.AttributeValueChanges.Add(attrChange);

        // The CSO has the "never expires" value (unchanged from what was already in AD)
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            ConnectedSystemObject = cso,
            Attribute = accountExpiresAttr,
            AttributeId = accountExpiresAttr.Id,
            LongValue = 9223372036854775807L
        });

        var service = new PendingExportReconciliationService(Jim);

        // Act
        var result = await service.ReconcileAsync(cso);

        // Assert
        Assert.That(result.ConfirmedChanges.Count, Is.EqualTo(1),
            "Protected attribute with substituted value should be confirmed");
        Assert.That(result.PendingExportDeleted, Is.True,
            "PendingExport should be deleted after all changes confirmed");
    }

    /// <summary>
    /// Tests that userAccountControl (another protected attribute) works correctly when
    /// its value has been substituted to the default "normal enabled account" value (512).
    /// </summary>
    [Test]
    public async Task ReconcileAsync_UserAccountControlSubstituted_ConfirmsWhenMatchesAsync()
    {
        // Arrange
        var cso = CreateTestCso();
        var pendingExport = CreateTestPendingExport(cso);
        var uacAttr = TargetUserType.Attributes.Single(a => a.Name == MockTargetSystemAttributeNames.UserAccountControl.ToString());

        // Simulating what happens when userAccountControl is set to the default value
        // after LDAP connector substitution (if clearing was attempted)
        var attrChange = new PendingExportAttributeValueChange
        {
            Id = Guid.NewGuid(),
            AttributeId = uacAttr.Id,
            Attribute = uacAttr,
            ChangeType = PendingExportAttributeChangeType.Update,
            IntValue = 512, // ADS_UF_NORMAL_ACCOUNT - the protected attribute default
            Status = PendingExportAttributeChangeStatus.ExportedPendingConfirmation,
            ExportAttemptCount = 1
        };
        pendingExport.AttributeValueChanges.Add(attrChange);

        // CSO has the normal account value
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            ConnectedSystemObject = cso,
            Attribute = uacAttr,
            AttributeId = uacAttr.Id,
            IntValue = 512
        });

        var service = new PendingExportReconciliationService(Jim);

        // Act
        var result = await service.ReconcileAsync(cso);

        // Assert
        Assert.That(result.ConfirmedChanges.Count, Is.EqualTo(1),
            "userAccountControl with substituted default value should be confirmed");
    }

    #endregion
}
