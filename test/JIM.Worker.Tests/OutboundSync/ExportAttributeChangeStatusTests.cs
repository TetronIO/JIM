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

namespace JIM.Worker.Tests.OutboundSync;

/// <summary>
/// Tests for attribute change status handling during export execution.
/// Tests the IsReadyForExecution logic and status transitions.
/// </summary>
public class ExportAttributeChangeStatusTests
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
    private JimApplication Jim { get; set; } = null!;
    private ConnectedSystem TargetSystem { get; set; } = null!;
    private ConnectedSystemObjectType TargetUserType { get; set; } = null!;
    private ConnectedSystemObjectTypeAttribute DisplayNameAttr { get; set; } = null!;
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

        // Mock entity framework calls
        MockJimDbContext = new Mock<JimDbContext>();
        MockJimDbContext.Setup(m => m.ConnectedSystemObjectTypes).Returns(MockDbSetConnectedSystemObjectTypes.Object);
        MockJimDbContext.Setup(m => m.ConnectedSystemObjects).Returns(MockDbSetConnectedSystemObjects.Object);
        MockJimDbContext.Setup(m => m.ConnectedSystems).Returns(MockDbSetConnectedSystems.Object);
        MockJimDbContext.Setup(m => m.PendingExports).Returns(MockDbSetPendingExports.Object);

        // Instantiate Jim using the mocked db context
        Jim = new JimApplication(new PostgresDataRepository(MockJimDbContext.Object));

        // Store references to commonly used objects
        TargetSystem = ConnectedSystemsData.Single(s => s.Name == "Dummy Target System");
        TargetUserType = ConnectedSystemObjectTypesData.Single(t => t.Name == "TARGET_USER");
        DisplayNameAttr = TargetUserType.Attributes.Single(a => a.Name == MockTargetSystemAttributeNames.DisplayName.ToString());
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
        PendingExportStatus status = PendingExportStatus.Pending)
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

    #endregion

    #region IsReadyForExecution Tests

    /// <summary>
    /// Tests that exports with Pending attribute changes are included in export execution.
    /// </summary>
    [Test]
    public async Task ExecuteExportsAsync_WithPendingAttributeChanges_IncludesInExecutionAsync()
    {
        // Arrange
        var cso = CreateTestCso();
        var pendingExport = CreateTestPendingExport(cso, PendingExportStatus.Pending);
        pendingExport.AttributeValueChanges.Add(new PendingExportAttributeValueChange
        {
            Id = Guid.NewGuid(),
            AttributeId = DisplayNameAttr.Id,
            Attribute = DisplayNameAttr,
            ChangeType = PendingExportAttributeChangeType.Update,
            StringValue = "John Doe",
            Status = PendingExportAttributeChangeStatus.Pending
        });

        var mockConnector = new Mock<IConnector>();
        mockConnector.Setup(c => c.Name).Returns("Test Connector");

        // Act
        var result = await Jim.ExportExecution.ExecuteExportsAsync(
            TargetSystem,
            mockConnector.Object,
            SyncRunMode.PreviewOnly);

        // Assert
        Assert.That(result.TotalPendingExports, Is.EqualTo(1),
            "Export with pending attribute changes should be included");
        Assert.That(result.ProcessedPendingExportIds, Does.Contain(pendingExport.Id));
    }

    /// <summary>
    /// Tests that exports with ExportedNotConfirmed attribute changes are included for retry.
    /// </summary>
    [Test]
    public async Task ExecuteExportsAsync_WithExportedNotConfirmedChanges_IncludesForRetryAsync()
    {
        // Arrange
        var cso = CreateTestCso();
        var pendingExport = CreateTestPendingExport(cso, PendingExportStatus.Exported);
        pendingExport.AttributeValueChanges.Add(new PendingExportAttributeValueChange
        {
            Id = Guid.NewGuid(),
            AttributeId = DisplayNameAttr.Id,
            Attribute = DisplayNameAttr,
            ChangeType = PendingExportAttributeChangeType.Update,
            StringValue = "John Doe",
            Status = PendingExportAttributeChangeStatus.ExportedNotConfirmed,
            ExportAttemptCount = 2
        });

        var mockConnector = new Mock<IConnector>();
        mockConnector.Setup(c => c.Name).Returns("Test Connector");

        // Act
        var result = await Jim.ExportExecution.ExecuteExportsAsync(
            TargetSystem,
            mockConnector.Object,
            SyncRunMode.PreviewOnly);

        // Assert
        Assert.That(result.TotalPendingExports, Is.EqualTo(1),
            "Export with ExportedNotConfirmed changes should be included for retry");
    }

    /// <summary>
    /// Tests that exports with only ExportedPendingConfirmation changes are NOT included.
    /// These are awaiting import confirmation, not ready for re-export.
    /// </summary>
    [Test]
    public async Task ExecuteExportsAsync_WithOnlyExportedPendingConfirmationChanges_ExcludedAsync()
    {
        // Arrange
        var cso = CreateTestCso();
        var pendingExport = CreateTestPendingExport(cso, PendingExportStatus.Exported);
        pendingExport.AttributeValueChanges.Add(new PendingExportAttributeValueChange
        {
            Id = Guid.NewGuid(),
            AttributeId = DisplayNameAttr.Id,
            Attribute = DisplayNameAttr,
            ChangeType = PendingExportAttributeChangeType.Update,
            StringValue = "John Doe",
            Status = PendingExportAttributeChangeStatus.ExportedPendingConfirmation
        });

        var mockConnector = new Mock<IConnector>();
        mockConnector.Setup(c => c.Name).Returns("Test Connector");

        // Act
        var result = await Jim.ExportExecution.ExecuteExportsAsync(
            TargetSystem,
            mockConnector.Object,
            SyncRunMode.PreviewOnly);

        // Assert
        Assert.That(result.TotalPendingExports, Is.EqualTo(0),
            "Export with only ExportedPendingConfirmation changes should NOT be included");
    }

    /// <summary>
    /// Tests that exports with only Failed attribute changes are NOT included.
    /// These require manual intervention.
    /// </summary>
    [Test]
    public async Task ExecuteExportsAsync_WithOnlyFailedChanges_ExcludedAsync()
    {
        // Arrange
        var cso = CreateTestCso();
        var pendingExport = CreateTestPendingExport(cso, PendingExportStatus.Failed);
        pendingExport.AttributeValueChanges.Add(new PendingExportAttributeValueChange
        {
            Id = Guid.NewGuid(),
            AttributeId = DisplayNameAttr.Id,
            Attribute = DisplayNameAttr,
            ChangeType = PendingExportAttributeChangeType.Update,
            StringValue = "John Doe",
            Status = PendingExportAttributeChangeStatus.Failed
        });

        var mockConnector = new Mock<IConnector>();
        mockConnector.Setup(c => c.Name).Returns("Test Connector");

        // Act
        var result = await Jim.ExportExecution.ExecuteExportsAsync(
            TargetSystem,
            mockConnector.Object,
            SyncRunMode.PreviewOnly);

        // Assert
        Assert.That(result.TotalPendingExports, Is.EqualTo(0),
            "Export with only Failed changes should NOT be included");
    }

    /// <summary>
    /// Tests that exports with mixed status attribute changes (some Pending, some Failed) are included
    /// because there are Pending changes to export.
    /// </summary>
    [Test]
    public async Task ExecuteExportsAsync_WithMixedStatusChanges_IncludesForPendingAsync()
    {
        // Arrange
        var cso = CreateTestCso();
        var pendingExport = CreateTestPendingExport(cso, PendingExportStatus.Pending);

        // Add one pending change
        pendingExport.AttributeValueChanges.Add(new PendingExportAttributeValueChange
        {
            Id = Guid.NewGuid(),
            AttributeId = DisplayNameAttr.Id,
            Attribute = DisplayNameAttr,
            ChangeType = PendingExportAttributeChangeType.Update,
            StringValue = "John Doe",
            Status = PendingExportAttributeChangeStatus.Pending
        });

        // Add one failed change
        var mailAttr = TargetUserType.Attributes.Single(a => a.Name == MockTargetSystemAttributeNames.Mail.ToString());
        pendingExport.AttributeValueChanges.Add(new PendingExportAttributeValueChange
        {
            Id = Guid.NewGuid(),
            AttributeId = mailAttr.Id,
            Attribute = mailAttr,
            ChangeType = PendingExportAttributeChangeType.Update,
            StringValue = "john@example.com",
            Status = PendingExportAttributeChangeStatus.Failed
        });

        var mockConnector = new Mock<IConnector>();
        mockConnector.Setup(c => c.Name).Returns("Test Connector");

        // Act
        var result = await Jim.ExportExecution.ExecuteExportsAsync(
            TargetSystem,
            mockConnector.Object,
            SyncRunMode.PreviewOnly);

        // Assert
        Assert.That(result.TotalPendingExports, Is.EqualTo(1),
            "Export should be included because it has Pending changes");
    }

    /// <summary>
    /// Tests that exports with no attribute changes at all are NOT included.
    /// </summary>
    [Test]
    public async Task ExecuteExportsAsync_WithNoAttributeChanges_ExcludedAsync()
    {
        // Arrange
        var cso = CreateTestCso();
        var pendingExport = CreateTestPendingExport(cso, PendingExportStatus.Pending);
        // Don't add any attribute changes

        var mockConnector = new Mock<IConnector>();
        mockConnector.Setup(c => c.Name).Returns("Test Connector");

        // Act
        var result = await Jim.ExportExecution.ExecuteExportsAsync(
            TargetSystem,
            mockConnector.Object,
            SyncRunMode.PreviewOnly);

        // Assert
        Assert.That(result.TotalPendingExports, Is.EqualTo(0),
            "Export with no attribute changes should NOT be included");
    }

    #endregion

    #region Status Transition Tests After Export

    /// <summary>
    /// Tests that after successful export, attribute changes transition from Pending to ExportedPendingConfirmation.
    /// </summary>
    [Test]
    public async Task ExecuteExportsAsync_SuccessfulExport_TransitionsToExportedPendingConfirmationAsync()
    {
        // Arrange
        var cso = CreateTestCso();
        var pendingExport = CreateTestPendingExport(cso, PendingExportStatus.Pending);
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

        // Mock connector that succeeds
        var mockConnector = new Mock<IConnector>();
        var mockExportConnector = mockConnector.As<IConnectorExportUsingCalls>();
        mockConnector.Setup(c => c.Name).Returns("Test Connector");
        mockExportConnector.Setup(c => c.ExportAsync(It.IsAny<IList<PendingExport>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ExportResult> { ExportResult.Succeeded() });

        // Mock update
        MockDbSetPendingExports.Setup(set => set.Update(It.IsAny<PendingExport>()));

        // Act
        var result = await Jim.ExportExecution.ExecuteExportsAsync(
            TargetSystem,
            mockConnector.Object,
            SyncRunMode.PreviewAndSync);

        // Assert
        Assert.That(result.SuccessCount, Is.EqualTo(1));
        Assert.That(attrChange.Status, Is.EqualTo(PendingExportAttributeChangeStatus.ExportedPendingConfirmation),
            "Attribute change should transition to ExportedPendingConfirmation");
        Assert.That(attrChange.ExportAttemptCount, Is.EqualTo(1),
            "Export attempt count should be incremented");
        Assert.That(attrChange.LastExportedAt, Is.Not.Null,
            "LastExportedAt should be set");
    }

    /// <summary>
    /// Tests that after successful export, retry attribute changes also transition correctly.
    /// </summary>
    [Test]
    public async Task ExecuteExportsAsync_RetryExport_TransitionsToExportedPendingConfirmationAsync()
    {
        // Arrange
        var cso = CreateTestCso();
        var pendingExport = CreateTestPendingExport(cso, PendingExportStatus.ExportNotConfirmed);
        var attrChange = new PendingExportAttributeValueChange
        {
            Id = Guid.NewGuid(),
            AttributeId = DisplayNameAttr.Id,
            Attribute = DisplayNameAttr,
            ChangeType = PendingExportAttributeChangeType.Update,
            StringValue = "John Doe",
            Status = PendingExportAttributeChangeStatus.ExportedNotConfirmed,
            ExportAttemptCount = 2
        };
        pendingExport.AttributeValueChanges.Add(attrChange);

        // Mock connector that succeeds
        var mockConnector = new Mock<IConnector>();
        var mockExportConnector = mockConnector.As<IConnectorExportUsingCalls>();
        mockConnector.Setup(c => c.Name).Returns("Test Connector");
        mockExportConnector.Setup(c => c.ExportAsync(It.IsAny<IList<PendingExport>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ExportResult> { ExportResult.Succeeded() });

        MockDbSetPendingExports.Setup(set => set.Update(It.IsAny<PendingExport>()));

        // Act
        var result = await Jim.ExportExecution.ExecuteExportsAsync(
            TargetSystem,
            mockConnector.Object,
            SyncRunMode.PreviewAndSync);

        // Assert
        Assert.That(result.SuccessCount, Is.EqualTo(1));
        Assert.That(attrChange.Status, Is.EqualTo(PendingExportAttributeChangeStatus.ExportedPendingConfirmation),
            "Retry attribute change should transition to ExportedPendingConfirmation");
        Assert.That(attrChange.ExportAttemptCount, Is.EqualTo(3),
            "Export attempt count should be incremented from 2 to 3");
    }

    /// <summary>
    /// Tests that already-confirmed attribute changes are not updated during export.
    /// </summary>
    [Test]
    public async Task ExecuteExportsAsync_ExportedPendingConfirmationChange_NotUpdatedAsync()
    {
        // Arrange
        var cso = CreateTestCso();
        var pendingExport = CreateTestPendingExport(cso, PendingExportStatus.Pending);

        // One pending change
        var pendingChange = new PendingExportAttributeValueChange
        {
            Id = Guid.NewGuid(),
            AttributeId = DisplayNameAttr.Id,
            Attribute = DisplayNameAttr,
            ChangeType = PendingExportAttributeChangeType.Update,
            StringValue = "John Doe",
            Status = PendingExportAttributeChangeStatus.Pending,
            ExportAttemptCount = 0
        };
        pendingExport.AttributeValueChanges.Add(pendingChange);

        // One already awaiting confirmation (shouldn't be touched)
        var mailAttr = TargetUserType.Attributes.Single(a => a.Name == MockTargetSystemAttributeNames.Mail.ToString());
        var existingChange = new PendingExportAttributeValueChange
        {
            Id = Guid.NewGuid(),
            AttributeId = mailAttr.Id,
            Attribute = mailAttr,
            ChangeType = PendingExportAttributeChangeType.Update,
            StringValue = "john@example.com",
            Status = PendingExportAttributeChangeStatus.ExportedPendingConfirmation,
            ExportAttemptCount = 1,
            LastExportedAt = DateTime.UtcNow.AddHours(-1)
        };
        pendingExport.AttributeValueChanges.Add(existingChange);

        var originalAttemptCount = existingChange.ExportAttemptCount;
        var originalLastExportedAt = existingChange.LastExportedAt;

        // Mock connector that succeeds
        var mockConnector = new Mock<IConnector>();
        var mockExportConnector = mockConnector.As<IConnectorExportUsingCalls>();
        mockConnector.Setup(c => c.Name).Returns("Test Connector");
        mockExportConnector.Setup(c => c.ExportAsync(It.IsAny<IList<PendingExport>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ExportResult> { ExportResult.Succeeded() });

        MockDbSetPendingExports.Setup(set => set.Update(It.IsAny<PendingExport>()));

        // Act
        var result = await Jim.ExportExecution.ExecuteExportsAsync(
            TargetSystem,
            mockConnector.Object,
            SyncRunMode.PreviewAndSync);

        // Assert
        Assert.That(result.SuccessCount, Is.EqualTo(1));
        Assert.That(pendingChange.Status, Is.EqualTo(PendingExportAttributeChangeStatus.ExportedPendingConfirmation),
            "Pending change should be updated");
        Assert.That(existingChange.ExportAttemptCount, Is.EqualTo(originalAttemptCount),
            "ExportedPendingConfirmation change should NOT have attempt count incremented");
        Assert.That(existingChange.LastExportedAt, Is.EqualTo(originalLastExportedAt),
            "ExportedPendingConfirmation change should NOT have LastExportedAt updated");
    }

    /// <summary>
    /// Tests that Delete exports with Exported status are NOT re-executed.
    /// Delete is an all-or-nothing operation: once exported, the delete was sent to the target
    /// and should only be cleaned up during import confirmation, not re-executed (which would
    /// fail if the object was already deleted from the target system).
    /// </summary>
    [Test]
    public async Task ExecuteExportsAsync_DeleteWithExportedStatus_ExcludedFromReExecutionAsync()
    {
        // Arrange - create a Delete export that was already exported (awaiting confirmation)
        var cso = CreateTestCso();
        var pendingExport = new PendingExport
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = TargetSystem.Id,
            ConnectedSystem = TargetSystem,
            ConnectedSystemObject = cso,
            Status = PendingExportStatus.Exported,
            ChangeType = PendingExportChangeType.Delete,
            CreatedAt = DateTime.UtcNow.AddMinutes(-5),
            LastAttemptedAt = DateTime.UtcNow.AddMinutes(-1),
            AttributeValueChanges = new List<PendingExportAttributeValueChange>()
        };
        PendingExportsData.Add(pendingExport);

        var mockConnector = new Mock<IConnector>();
        mockConnector.Setup(c => c.Name).Returns("Test Connector");

        // Act
        var result = await Jim.ExportExecution.ExecuteExportsAsync(
            TargetSystem,
            mockConnector.Object,
            SyncRunMode.PreviewOnly);

        // Assert
        Assert.That(result.TotalPendingExports, Is.EqualTo(0),
            "Delete export with Exported status should NOT be re-executed (awaiting import confirmation)");
    }

    /// <summary>
    /// Tests that Delete exports with Pending status ARE included for execution.
    /// A new delete export that hasn't been executed yet should be processed.
    /// </summary>
    [Test]
    public async Task ExecuteExportsAsync_DeleteWithPendingStatus_IncludedForExecutionAsync()
    {
        // Arrange - create a new Delete export that hasn't been executed yet
        var cso = CreateTestCso();
        var pendingExport = new PendingExport
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = TargetSystem.Id,
            ConnectedSystem = TargetSystem,
            ConnectedSystemObject = cso,
            Status = PendingExportStatus.Pending,
            ChangeType = PendingExportChangeType.Delete,
            CreatedAt = DateTime.UtcNow,
            AttributeValueChanges = new List<PendingExportAttributeValueChange>()
        };
        PendingExportsData.Add(pendingExport);

        var mockConnector = new Mock<IConnector>();
        mockConnector.Setup(c => c.Name).Returns("Test Connector");

        // Act
        var result = await Jim.ExportExecution.ExecuteExportsAsync(
            TargetSystem,
            mockConnector.Object,
            SyncRunMode.PreviewOnly);

        // Assert
        Assert.That(result.TotalPendingExports, Is.EqualTo(1),
            "Delete export with Pending status should be included for execution");
        Assert.That(result.ProcessedPendingExportIds, Does.Contain(pendingExport.Id));
    }

    #endregion
}
