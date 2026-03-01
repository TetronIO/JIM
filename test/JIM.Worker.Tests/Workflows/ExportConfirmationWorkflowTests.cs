using JIM.Application;
using JIM.Application.Services;
using JIM.Models.Core;
using JIM.Models.Interfaces;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.PostgresData;
using JIM.Worker.Tests.Models;
using Microsoft.EntityFrameworkCore;
using MockQueryable.Moq;
using Moq;

namespace JIM.Worker.Tests.Workflows;

/// <summary>
/// End-to-end workflow tests for the export confirmation lifecycle.
/// Simulates the complete Export → Import → Confirm cycle with various scenarios.
/// </summary>
public class ExportConfirmationWorkflowTests
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

    private PendingExport CreateTestPendingExport(ConnectedSystemObject cso)
    {
        var pendingExport = new PendingExport
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = TargetSystem.Id,
            ConnectedSystem = TargetSystem,
            ConnectedSystemObject = cso,
            Status = PendingExportStatus.Pending,
            ChangeType = PendingExportChangeType.Update,
            CreatedAt = DateTime.UtcNow,
            AttributeValueChanges = new List<PendingExportAttributeValueChange>()
        };
        PendingExportsData.Add(pendingExport);
        return pendingExport;
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

    private async Task SimulateExportAsync(PendingExport pendingExport, bool success = true)
    {
        // Mock connector that succeeds or fails
        var mockConnector = new Mock<IConnector>();
        var mockExportConnector = mockConnector.As<IConnectorExportUsingCalls>();
        mockConnector.Setup(c => c.Name).Returns("Test Connector");

        if (success)
        {
            mockExportConnector.Setup(c => c.ExportAsync(It.IsAny<IList<PendingExport>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new List<ConnectedSystemExportResult> { ConnectedSystemExportResult.Succeeded() });
        }
        else
        {
            mockExportConnector.Setup(c => c.ExportAsync(It.IsAny<IList<PendingExport>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new List<ConnectedSystemExportResult> { ConnectedSystemExportResult.Failed("Export failed") });
        }

        MockDbSetPendingExports.Setup(set => set.Update(It.IsAny<PendingExport>()));

        await Jim.ExportExecution.ExecuteExportsAsync(
            TargetSystem,
            mockConnector.Object,
            SyncRunMode.PreviewAndSync);
    }

    private async Task<PendingExportReconciliationResult> SimulateImportAndReconcileAsync(ConnectedSystemObject cso)
    {
        var reconciliationService = new PendingExportReconciliationService(Jim);
        return await reconciliationService.ReconcileAsync(cso);
    }

    #endregion

    #region Happy Path Workflows

    /// <summary>
    /// Workflow: Single attribute change exports and confirms successfully on first import.
    /// Flow: Create PendingExport → Export → Import with matching value → Confirm → Delete PendingExport
    /// </summary>
    [Test]
    public async Task Workflow_SingleAttributeChange_ConfirmsOnFirstImportAsync()
    {
        // Arrange: Create pending export with one attribute change
        var cso = CreateTestCso();
        var pendingExport = CreateTestPendingExport(cso);
        var attrChange = new PendingExportAttributeValueChange
        {
            Id = Guid.NewGuid(),
            AttributeId = DisplayNameAttr.Id,
            Attribute = DisplayNameAttr,
            ChangeType = PendingExportAttributeChangeType.Update,
            StringValue = "John Doe",
            Status = PendingExportAttributeChangeStatus.Pending,
            ExportAttemptCount = 0
        };
        pendingExport.AttributeValueChanges.Add(attrChange);

        // Act Step 1: Export
        await SimulateExportAsync(pendingExport);

        // Assert Step 1: Attribute change should be ExportedPendingConfirmation
        Assert.That(attrChange.Status, Is.EqualTo(PendingExportAttributeChangeStatus.ExportedPendingConfirmation));
        Assert.That(attrChange.ExportAttemptCount, Is.EqualTo(1));
        Assert.That(pendingExport.Status, Is.EqualTo(PendingExportStatus.Exported));

        // Act Step 2: Import with matching value
        AddCsoAttributeValue(cso, DisplayNameAttr, "John Doe");
        var result = await SimulateImportAndReconcileAsync(cso);

        // Assert Step 2: Attribute change should be confirmed and PendingExport deleted
        Assert.That(result.ConfirmedChanges.Count, Is.EqualTo(1));
        Assert.That(result.PendingExportDeleted, Is.True);
    }

    /// <summary>
    /// Workflow: Multiple attribute changes all export and confirm successfully.
    /// Flow: Create PendingExport with 2 changes → Export → Import with matching values → Confirm all → Delete PendingExport
    /// </summary>
    [Test]
    public async Task Workflow_MultipleAttributeChanges_AllConfirmSuccessfullyAsync()
    {
        // Arrange: Create pending export with multiple attribute changes
        var cso = CreateTestCso();
        var pendingExport = CreateTestPendingExport(cso);

        var displayNameChange = new PendingExportAttributeValueChange
        {
            Id = Guid.NewGuid(),
            AttributeId = DisplayNameAttr.Id,
            Attribute = DisplayNameAttr,
            ChangeType = PendingExportAttributeChangeType.Update,
            StringValue = "John Doe",
            Status = PendingExportAttributeChangeStatus.Pending
        };
        pendingExport.AttributeValueChanges.Add(displayNameChange);

        var mailChange = new PendingExportAttributeValueChange
        {
            Id = Guid.NewGuid(),
            AttributeId = MailAttr.Id,
            Attribute = MailAttr,
            ChangeType = PendingExportAttributeChangeType.Update,
            StringValue = "john@example.com",
            Status = PendingExportAttributeChangeStatus.Pending
        };
        pendingExport.AttributeValueChanges.Add(mailChange);

        // Act Step 1: Export
        await SimulateExportAsync(pendingExport);

        // Assert Step 1: Both changes should be ExportedPendingConfirmation
        Assert.That(displayNameChange.Status, Is.EqualTo(PendingExportAttributeChangeStatus.ExportedPendingConfirmation));
        Assert.That(mailChange.Status, Is.EqualTo(PendingExportAttributeChangeStatus.ExportedPendingConfirmation));

        // Act Step 2: Import with matching values
        AddCsoAttributeValue(cso, DisplayNameAttr, "John Doe");
        AddCsoAttributeValue(cso, MailAttr, "john@example.com");
        var result = await SimulateImportAndReconcileAsync(cso);

        // Assert Step 2: Both should be confirmed
        Assert.That(result.ConfirmedChanges.Count, Is.EqualTo(2));
        Assert.That(result.PendingExportDeleted, Is.True);
    }

    #endregion

    #region Retry Workflows

    /// <summary>
    /// Workflow: Attribute change fails confirmation, retries, then confirms.
    /// Flow: Export → Import with wrong value → Mark for retry → Re-export → Import with correct value → Confirm
    /// </summary>
    [Test]
    public async Task Workflow_AttributeChange_FailsThenConfirmsOnRetryAsync()
    {
        // Arrange: Create pending export
        var cso = CreateTestCso();
        var pendingExport = CreateTestPendingExport(cso);
        var attrChange = new PendingExportAttributeValueChange
        {
            Id = Guid.NewGuid(),
            AttributeId = DisplayNameAttr.Id,
            Attribute = DisplayNameAttr,
            ChangeType = PendingExportAttributeChangeType.Update,
            StringValue = "John Doe",
            Status = PendingExportAttributeChangeStatus.Pending
        };
        pendingExport.AttributeValueChanges.Add(attrChange);

        // Act Step 1: First export
        await SimulateExportAsync(pendingExport);
        Assert.That(attrChange.Status, Is.EqualTo(PendingExportAttributeChangeStatus.ExportedPendingConfirmation));

        // Act Step 2: Import with wrong value
        AddCsoAttributeValue(cso, DisplayNameAttr, "Wrong Name");
        var result1 = await SimulateImportAndReconcileAsync(cso);

        // Assert Step 2: Should be marked for retry
        Assert.That(result1.RetryChanges.Count, Is.EqualTo(1));
        Assert.That(attrChange.Status, Is.EqualTo(PendingExportAttributeChangeStatus.ExportedNotConfirmed));
        Assert.That(attrChange.LastImportedValue, Is.EqualTo("Wrong Name"));
        Assert.That(pendingExport.Status, Is.EqualTo(PendingExportStatus.ExportNotConfirmed));

        // Act Step 3: Re-export (simulated by resetting status and re-exporting)
        await SimulateExportAsync(pendingExport);

        // Assert Step 3: Attempt count should be incremented
        Assert.That(attrChange.Status, Is.EqualTo(PendingExportAttributeChangeStatus.ExportedPendingConfirmation));
        Assert.That(attrChange.ExportAttemptCount, Is.EqualTo(2));

        // Act Step 4: Import with correct value this time
        cso.AttributeValues.Clear();
        AddCsoAttributeValue(cso, DisplayNameAttr, "John Doe");
        var result2 = await SimulateImportAndReconcileAsync(cso);

        // Assert Step 4: Should now be confirmed
        Assert.That(result2.ConfirmedChanges.Count, Is.EqualTo(1));
        Assert.That(result2.PendingExportDeleted, Is.True);
    }

    /// <summary>
    /// Workflow: Multiple attribute changes, some confirm immediately, one requires retry.
    /// Flow: Export 2 changes → Import with 1 matching → Confirm 1, retry 1 → Re-export → Import both matching → Confirm all
    /// </summary>
    [Test]
    public async Task Workflow_MultipleChanges_PartialConfirmThenFullConfirmAsync()
    {
        // Arrange
        var cso = CreateTestCso();
        var pendingExport = CreateTestPendingExport(cso);

        var displayNameChange = new PendingExportAttributeValueChange
        {
            Id = Guid.NewGuid(),
            AttributeId = DisplayNameAttr.Id,
            Attribute = DisplayNameAttr,
            ChangeType = PendingExportAttributeChangeType.Update,
            StringValue = "John Doe",
            Status = PendingExportAttributeChangeStatus.Pending
        };
        pendingExport.AttributeValueChanges.Add(displayNameChange);

        var mailChange = new PendingExportAttributeValueChange
        {
            Id = Guid.NewGuid(),
            AttributeId = MailAttr.Id,
            Attribute = MailAttr,
            ChangeType = PendingExportAttributeChangeType.Update,
            StringValue = "john@example.com",
            Status = PendingExportAttributeChangeStatus.Pending
        };
        pendingExport.AttributeValueChanges.Add(mailChange);

        // Act Step 1: Export
        await SimulateExportAsync(pendingExport);

        // Act Step 2: Import with one matching value
        AddCsoAttributeValue(cso, DisplayNameAttr, "John Doe"); // Matches
        AddCsoAttributeValue(cso, MailAttr, "wrong@example.com"); // Doesn't match
        var result1 = await SimulateImportAndReconcileAsync(cso);

        // Assert Step 2: One confirmed, one retry
        Assert.That(result1.ConfirmedChanges.Count, Is.EqualTo(1));
        Assert.That(result1.RetryChanges.Count, Is.EqualTo(1));
        Assert.That(result1.PendingExportDeleted, Is.False);
        Assert.That(mailChange.Status, Is.EqualTo(PendingExportAttributeChangeStatus.ExportedNotConfirmed));

        // Act Step 3: Re-export (only the failing change should be re-exported)
        await SimulateExportAsync(pendingExport);
        Assert.That(mailChange.Status, Is.EqualTo(PendingExportAttributeChangeStatus.ExportedPendingConfirmation));

        // Act Step 4: Import with correct mail value
        cso.AttributeValues.RemoveAll(av => av.AttributeId == MailAttr.Id);
        AddCsoAttributeValue(cso, MailAttr, "john@example.com");
        var result2 = await SimulateImportAndReconcileAsync(cso);

        // Assert Step 4: Remaining change confirmed, PendingExport deleted
        Assert.That(result2.ConfirmedChanges.Count, Is.EqualTo(1));
        Assert.That(result2.PendingExportDeleted, Is.True);
    }

    #endregion

    #region Failure Workflows

    /// <summary>
    /// Workflow: Attribute change never confirms and eventually fails after max retries.
    /// </summary>
    [Test]
    public async Task Workflow_AttributeChange_ExceedsMaxRetriesAndFailsAsync()
    {
        // Arrange
        var cso = CreateTestCso();
        var pendingExport = CreateTestPendingExport(cso);
        var attrChange = new PendingExportAttributeValueChange
        {
            Id = Guid.NewGuid(),
            AttributeId = DisplayNameAttr.Id,
            Attribute = DisplayNameAttr,
            ChangeType = PendingExportAttributeChangeType.Update,
            StringValue = "John Doe",
            Status = PendingExportAttributeChangeStatus.Pending
        };
        pendingExport.AttributeValueChanges.Add(attrChange);

        // Simulate max retries of exports
        for (int attempt = 1; attempt <= PendingExportReconciliationService.DefaultMaxRetries; attempt++)
        {
            await SimulateExportAsync(pendingExport);

            // Import with wrong value each time
            cso.AttributeValues.Clear();
            AddCsoAttributeValue(cso, DisplayNameAttr, $"Wrong Name {attempt}");
            var result = await SimulateImportAndReconcileAsync(cso);

            if (attempt < PendingExportReconciliationService.DefaultMaxRetries)
            {
                // Should be marked for retry
                Assert.That(result.RetryChanges.Count, Is.EqualTo(1),
                    $"Attempt {attempt}: Should be marked for retry");
                Assert.That(attrChange.Status, Is.EqualTo(PendingExportAttributeChangeStatus.ExportedNotConfirmed));
            }
            else
            {
                // Final attempt - should fail
                Assert.That(result.FailedChanges.Count, Is.EqualTo(1),
                    $"Attempt {attempt}: Should have failed");
                Assert.That(attrChange.Status, Is.EqualTo(PendingExportAttributeChangeStatus.Failed));
                Assert.That(pendingExport.Status, Is.EqualTo(PendingExportStatus.Failed));
            }
        }
    }

    /// <summary>
    /// Workflow: New attribute changes are added while existing problematic change is being retried.
    /// Flow: Export change1 (fails) → Add change2 → Export both → Import confirms both
    /// </summary>
    [Test]
    public async Task Workflow_NewChangesAddedDuringRetryAsync()
    {
        // Arrange
        var cso = CreateTestCso();
        var pendingExport = CreateTestPendingExport(cso);

        // Initial change
        var displayNameChange = new PendingExportAttributeValueChange
        {
            Id = Guid.NewGuid(),
            AttributeId = DisplayNameAttr.Id,
            Attribute = DisplayNameAttr,
            ChangeType = PendingExportAttributeChangeType.Update,
            StringValue = "John Doe",
            Status = PendingExportAttributeChangeStatus.Pending
        };
        pendingExport.AttributeValueChanges.Add(displayNameChange);

        // Act Step 1: First export
        await SimulateExportAsync(pendingExport);

        // Act Step 2: Import fails to confirm
        AddCsoAttributeValue(cso, DisplayNameAttr, "Wrong Name");
        await SimulateImportAndReconcileAsync(cso);
        Assert.That(displayNameChange.Status, Is.EqualTo(PendingExportAttributeChangeStatus.ExportedNotConfirmed));

        // Act Step 3: New change added before retry
        var mailChange = new PendingExportAttributeValueChange
        {
            Id = Guid.NewGuid(),
            AttributeId = MailAttr.Id,
            Attribute = MailAttr,
            ChangeType = PendingExportAttributeChangeType.Update,
            StringValue = "john@example.com",
            Status = PendingExportAttributeChangeStatus.Pending
        };
        pendingExport.AttributeValueChanges.Add(mailChange);

        // Act Step 4: Re-export (both changes should be exported)
        await SimulateExportAsync(pendingExport);
        Assert.That(displayNameChange.Status, Is.EqualTo(PendingExportAttributeChangeStatus.ExportedPendingConfirmation));
        Assert.That(mailChange.Status, Is.EqualTo(PendingExportAttributeChangeStatus.ExportedPendingConfirmation));
        Assert.That(displayNameChange.ExportAttemptCount, Is.EqualTo(2));
        Assert.That(mailChange.ExportAttemptCount, Is.EqualTo(1));

        // Act Step 5: Import confirms both
        cso.AttributeValues.Clear();
        AddCsoAttributeValue(cso, DisplayNameAttr, "John Doe");
        AddCsoAttributeValue(cso, MailAttr, "john@example.com");
        var finalResult = await SimulateImportAndReconcileAsync(cso);

        // Assert: Both confirmed
        Assert.That(finalResult.ConfirmedChanges.Count, Is.EqualTo(2));
        Assert.That(finalResult.PendingExportDeleted, Is.True);
    }

    /// <summary>
    /// Workflow: Multiple retries with some new changes also not confirming.
    /// Complex scenario testing mixed status transitions.
    /// </summary>
    [Test]
    public async Task Workflow_MultipleRetriesWithNewFailingChangesAsync()
    {
        // Arrange
        var cso = CreateTestCso();
        var pendingExport = CreateTestPendingExport(cso);

        // Initial change that will fail
        var displayNameChange = new PendingExportAttributeValueChange
        {
            Id = Guid.NewGuid(),
            AttributeId = DisplayNameAttr.Id,
            Attribute = DisplayNameAttr,
            ChangeType = PendingExportAttributeChangeType.Update,
            StringValue = "John Doe",
            Status = PendingExportAttributeChangeStatus.Pending
        };
        pendingExport.AttributeValueChanges.Add(displayNameChange);

        // Export and fail first change
        await SimulateExportAsync(pendingExport);
        AddCsoAttributeValue(cso, DisplayNameAttr, "Wrong Name");
        await SimulateImportAndReconcileAsync(cso);

        // Add a new change that will also fail
        var mailChange = new PendingExportAttributeValueChange
        {
            Id = Guid.NewGuid(),
            AttributeId = MailAttr.Id,
            Attribute = MailAttr,
            ChangeType = PendingExportAttributeChangeType.Update,
            StringValue = "john@example.com",
            Status = PendingExportAttributeChangeStatus.Pending
        };
        pendingExport.AttributeValueChanges.Add(mailChange);

        // Export both changes
        await SimulateExportAsync(pendingExport);

        // Import - first one confirms, second one fails
        cso.AttributeValues.Clear();
        AddCsoAttributeValue(cso, DisplayNameAttr, "John Doe"); // Now correct
        AddCsoAttributeValue(cso, MailAttr, "wrong@example.com"); // Still wrong
        var result = await SimulateImportAndReconcileAsync(cso);

        // Assert
        Assert.That(result.ConfirmedChanges.Count, Is.EqualTo(1), "displayName should confirm");
        Assert.That(result.RetryChanges.Count, Is.EqualTo(1), "mail should need retry");
        Assert.That(result.PendingExportDeleted, Is.False, "PendingExport should still exist");

        // The confirmed change should have been removed from pending export
        Assert.That(pendingExport.AttributeValueChanges.Count, Is.EqualTo(1));
        Assert.That(pendingExport.AttributeValueChanges[0].AttributeId, Is.EqualTo(MailAttr.Id));
    }

    #endregion
}
