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

namespace JIM.Worker.Tests.OutboundSync;

/// <summary>
/// Tests for ExportExecutionServer - the Q5 (preview mode) and Q6 (retry with backoff) decisions.
/// </summary>
public class ExportExecutionTests
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
    #endregion

    [SetUp]
    public void Setup()
    {
        TestUtilities.SetEnvironmentVariables();

        // Set up the Connected System Run Profiles mock
        ConnectedSystemRunProfilesData = TestUtilities.GetConnectedSystemRunProfileData();
        MockDbSetConnectedSystemRunProfiles = ConnectedSystemRunProfilesData.BuildMockDbSet();

        // Set up the Activity mock
        var exportRunProfile = ConnectedSystemRunProfilesData.Single(rp => rp.Name == "Dummy Target System Export");
        ActivitiesData = TestUtilities.GetActivityData(exportRunProfile.RunType, exportRunProfile.Id);
        MockDbSetActivities = ActivitiesData.BuildMockDbSet();

        // Set up the Connected Systems mock
        ConnectedSystemsData = TestUtilities.GetConnectedSystemData();
        MockDbSetConnectedSystems = ConnectedSystemsData.BuildMockDbSet();

        // Set up the Connected System Object Types mock
        ConnectedSystemObjectTypesData = TestUtilities.GetConnectedSystemObjectTypeData();
        MockDbSetConnectedSystemObjectTypes = ConnectedSystemObjectTypesData.BuildMockDbSet();

        // Set up the Connected System Objects mock
        ConnectedSystemObjectsData = TestUtilities.GetConnectedSystemObjectData();
        MockDbSetConnectedSystemObjects = ConnectedSystemObjectsData.BuildMockDbSet();

        // Set up the Connected System Partitions mock
        ConnectedSystemPartitionsData = TestUtilities.GetConnectedSystemPartitionData();
        MockDbSetConnectedSystemPartitions = ConnectedSystemPartitionsData.BuildMockDbSet();

        // Set up the Pending Export objects mock
        PendingExportsData = new List<PendingExport>();
        MockDbSetPendingExports = PendingExportsData.BuildMockDbSet();

        // Set up the Metaverse Object Types mock
        MetaverseObjectTypesData = TestUtilities.GetMetaverseObjectTypeData();
        MockDbSetMetaverseObjectTypes = MetaverseObjectTypesData.BuildMockDbSet();

        // Set up the Metaverse Objects mock
        MetaverseObjectsData = TestUtilities.GetMetaverseObjectData();
        MockDbSetMetaverseObjects = MetaverseObjectsData.BuildMockDbSet();

        // Set up the Sync Rule stub mocks
        SyncRulesData = TestUtilities.GetSyncRuleData();
        MockDbSetSyncRules = SyncRulesData.BuildMockDbSet();

        // Mock entity framework calls
        MockJimDbContext = new Mock<JimDbContext>();
        MockJimDbContext.Setup(m => m.Activities).Returns(MockDbSetActivities.Object);
        MockJimDbContext.Setup(m => m.ConnectedSystemObjectTypes).Returns(MockDbSetConnectedSystemObjectTypes.Object);
        MockJimDbContext.Setup(m => m.ConnectedSystemObjects).Returns(MockDbSetConnectedSystemObjects.Object);
        MockJimDbContext.Setup(m => m.ConnectedSystemPartitions).Returns(MockDbSetConnectedSystemPartitions.Object);
        MockJimDbContext.Setup(m => m.ConnectedSystemRunProfiles).Returns(MockDbSetConnectedSystemRunProfiles.Object);
        MockJimDbContext.Setup(m => m.ConnectedSystems).Returns(MockDbSetConnectedSystems.Object);
        MockJimDbContext.Setup(m => m.MetaverseObjectTypes).Returns(MockDbSetMetaverseObjectTypes.Object);
        MockJimDbContext.Setup(m => m.MetaverseObjects).Returns(MockDbSetMetaverseObjects.Object);
        MockJimDbContext.Setup(m => m.PendingExports).Returns(MockDbSetPendingExports.Object);
        MockJimDbContext.Setup(m => m.SyncRules).Returns(MockDbSetSyncRules.Object);

        // Instantiate Jim using the mocked db context
        Jim = new JimApplication(new PostgresDataRepository(MockJimDbContext.Object));
    }

    /// <summary>
    /// Tests the Q5 decision: Preview Only mode returns previews without executing exports.
    /// </summary>
    [Test]
    public async Task ExecuteExportsAsync_PreviewOnlyMode_ReturnsPreviewsWithoutExecutingAsync()
    {
        // Arrange
        var targetSystem = ConnectedSystemsData.Single(s => s.Name == "Dummy Target System");
        var targetUserType = ConnectedSystemObjectTypesData.Single(t => t.Name == "TARGET_USER");
        var displayNameAttr = targetUserType.Attributes.Single(a => a.Name == MockTargetSystemAttributeNames.DisplayName.ToString());

        // Create a CSO to be updated
        var cso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = targetSystem.Id,
            Type = targetUserType,
            TypeId = targetUserType.Id
        };
        ConnectedSystemObjectsData.Add(cso);

        // Create a pending export
        var pendingExport = new PendingExport
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = targetSystem.Id,
            ConnectedSystem = targetSystem,
            ConnectedSystemObject = cso,
            Status = PendingExportStatus.Pending,
            ChangeType = PendingExportChangeType.Update,
            CreatedAt = DateTime.UtcNow,
            AttributeValueChanges = new List<PendingExportAttributeValueChange>
            {
                new()
                {
                    Id = Guid.NewGuid(),
                    ChangeType = PendingExportAttributeChangeType.Update,
                    AttributeId = displayNameAttr.Id,
                    Attribute = displayNameAttr,
                    StringValue = "New Display Name"
                }
            }
        };
        PendingExportsData.Add(pendingExport);

        // Mock connector - use IConnector as the base type
        var mockConnector = new Mock<IConnector>();
        mockConnector.Setup(c => c.Name).Returns("Test Connector");

        // Act
        var result = await Jim.ExportExecution.ExecuteExportsAsync(
            targetSystem,
            mockConnector.Object,
            SyncRunMode.PreviewOnly);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.RunMode, Is.EqualTo(SyncRunMode.PreviewOnly));
        Assert.That(result.Previews.Count, Is.GreaterThan(0), "Expected preview information");
        Assert.That(result.Previews[0].ChangeType, Is.EqualTo(PendingExportChangeType.Update));
        Assert.That(result.Previews[0].AttributeChanges.Count, Is.EqualTo(1));
        Assert.That(result.Previews[0].AttributeChanges[0].NewValue, Is.EqualTo("New Display Name"));
    }

    /// <summary>
    /// Tests that when there are no pending exports, an empty result is returned.
    /// </summary>
    [Test]
    public async Task ExecuteExportsAsync_NoPendingExports_ReturnsEmptyResultAsync()
    {
        // Arrange
        var targetSystem = ConnectedSystemsData.Single(s => s.Name == "Dummy Target System");
        var mockConnector = new Mock<IConnector>();
        mockConnector.Setup(c => c.Name).Returns("Test Connector");

        // Ensure no pending exports
        PendingExportsData.Clear();

        // Act
        var result = await Jim.ExportExecution.ExecuteExportsAsync(
            targetSystem,
            mockConnector.Object,
            SyncRunMode.PreviewAndSync);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.TotalPendingExports, Is.EqualTo(0));
        Assert.That(result.Previews.Count, Is.EqualTo(0));
        Assert.That(result.CompletedAt, Is.Not.Null);
    }

    /// <summary>
    /// Tests the Q6 decision: exports with NextRetryAt in the future are not executed.
    /// </summary>
    [Test]
    public async Task ExecuteExportsAsync_ExportNotDueForRetry_SkipsExportAsync()
    {
        // Arrange
        var targetSystem = ConnectedSystemsData.Single(s => s.Name == "Dummy Target System");
        var targetUserType = ConnectedSystemObjectTypesData.Single(t => t.Name == "TARGET_USER");

        var cso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = targetSystem.Id,
            Type = targetUserType,
            TypeId = targetUserType.Id
        };
        ConnectedSystemObjectsData.Add(cso);

        // Create a pending export that's not due for retry yet
        var pendingExport = new PendingExport
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = targetSystem.Id,
            ConnectedSystem = targetSystem,
            ConnectedSystemObject = cso,
            Status = PendingExportStatus.ExportNotImported, // Failed status
            ChangeType = PendingExportChangeType.Update,
            CreatedAt = DateTime.UtcNow.AddMinutes(-30),
            NextRetryAt = DateTime.UtcNow.AddMinutes(30), // Not due yet
            ErrorCount = 1,
            MaxRetries = 3,
            AttributeValueChanges = new List<PendingExportAttributeValueChange>()
        };
        PendingExportsData.Add(pendingExport);

        var mockConnector = new Mock<IConnector>();
        mockConnector.Setup(c => c.Name).Returns("Test Connector");

        // Act
        var result = await Jim.ExportExecution.ExecuteExportsAsync(
            targetSystem,
            mockConnector.Object,
            SyncRunMode.PreviewOnly);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.TotalPendingExports, Is.EqualTo(0), "Export not due for retry should be skipped");
    }

    /// <summary>
    /// Tests the Q6 decision: exports that have exceeded max retries are not executed.
    /// </summary>
    [Test]
    public async Task ExecuteExportsAsync_MaxRetriesExceeded_SkipsExportAsync()
    {
        // Arrange
        var targetSystem = ConnectedSystemsData.Single(s => s.Name == "Dummy Target System");
        var targetUserType = ConnectedSystemObjectTypesData.Single(t => t.Name == "TARGET_USER");

        var cso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = targetSystem.Id,
            Type = targetUserType,
            TypeId = targetUserType.Id
        };
        ConnectedSystemObjectsData.Add(cso);

        // Create a pending export that has exceeded max retries
        var pendingExport = new PendingExport
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = targetSystem.Id,
            ConnectedSystem = targetSystem,
            ConnectedSystemObject = cso,
            Status = PendingExportStatus.ExportNotImported,
            ChangeType = PendingExportChangeType.Update,
            CreatedAt = DateTime.UtcNow.AddHours(-1),
            ErrorCount = 3,
            MaxRetries = 3, // Max reached
            AttributeValueChanges = new List<PendingExportAttributeValueChange>()
        };
        PendingExportsData.Add(pendingExport);

        var mockConnector = new Mock<IConnector>();
        mockConnector.Setup(c => c.Name).Returns("Test Connector");

        // Act
        var result = await Jim.ExportExecution.ExecuteExportsAsync(
            targetSystem,
            mockConnector.Object,
            SyncRunMode.PreviewOnly);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.TotalPendingExports, Is.EqualTo(0), "Export that exceeded max retries should be skipped");
    }

    /// <summary>
    /// Tests that progress callback is invoked during export execution.
    /// </summary>
    [Test]
    public async Task ExecuteExportsAsync_WithProgressCallback_ReportsProgressAsync()
    {
        // Arrange
        var targetSystem = ConnectedSystemsData.Single(s => s.Name == "Dummy Target System");
        var targetUserType = ConnectedSystemObjectTypesData.Single(t => t.Name == "TARGET_USER");

        var cso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = targetSystem.Id,
            Type = targetUserType,
            TypeId = targetUserType.Id
        };
        ConnectedSystemObjectsData.Add(cso);

        var pendingExport = new PendingExport
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = targetSystem.Id,
            ConnectedSystem = targetSystem,
            ConnectedSystemObject = cso,
            Status = PendingExportStatus.Pending,
            ChangeType = PendingExportChangeType.Update,
            CreatedAt = DateTime.UtcNow,
            AttributeValueChanges = new List<PendingExportAttributeValueChange>()
        };
        PendingExportsData.Add(pendingExport);

        var mockConnector = new Mock<IConnector>();
        mockConnector.Setup(c => c.Name).Returns("Test Connector");

        var progressReports = new List<ExportProgressInfo>();
        Func<ExportProgressInfo, Task> progressCallback = (info) =>
        {
            progressReports.Add(info);
            return Task.CompletedTask;
        };

        // Act
        var result = await Jim.ExportExecution.ExecuteExportsAsync(
            targetSystem,
            mockConnector.Object,
            SyncRunMode.PreviewOnly,
            null,
            CancellationToken.None,
            progressCallback);

        // Assert
        Assert.That(progressReports.Count, Is.GreaterThan(0), "Expected progress to be reported");
        Assert.That(progressReports.Any(p => p.Phase == ExportPhase.Preparing), Is.True, "Expected Preparing phase");
    }

    /// <summary>
    /// Tests that cancellation is respected during export execution.
    /// </summary>
    [Test]
    public void ExecuteExportsAsync_WhenCancelled_ThrowsOperationCancelledException()
    {
        // Arrange
        var targetSystem = ConnectedSystemsData.Single(s => s.Name == "Dummy Target System");
        var targetUserType = ConnectedSystemObjectTypesData.Single(t => t.Name == "TARGET_USER");

        // Create multiple pending exports
        for (int i = 0; i < 10; i++)
        {
            var cso = new ConnectedSystemObject
            {
                Id = Guid.NewGuid(),
                ConnectedSystemId = targetSystem.Id,
                Type = targetUserType,
                TypeId = targetUserType.Id
            };
            ConnectedSystemObjectsData.Add(cso);

            PendingExportsData.Add(new PendingExport
            {
                Id = Guid.NewGuid(),
                ConnectedSystemId = targetSystem.Id,
                ConnectedSystem = targetSystem,
                ConnectedSystemObject = cso,
                Status = PendingExportStatus.Pending,
                ChangeType = PendingExportChangeType.Update,
                CreatedAt = DateTime.UtcNow,
                AttributeValueChanges = new List<PendingExportAttributeValueChange>()
            });
        }

        var mockConnector = new Mock<IConnector>();
        mockConnector.Setup(c => c.Name).Returns("Test Connector");

        using var cts = new CancellationTokenSource();
        cts.Cancel(); // Cancel immediately

        // Act & Assert
        Assert.ThrowsAsync<OperationCanceledException>(async () =>
        {
            await Jim.ExportExecution.ExecuteExportsAsync(
                targetSystem,
                mockConnector.Object,
                SyncRunMode.PreviewAndSync,
                null,
                cts.Token);
        });
    }

    /// <summary>
    /// Tests that export preview contains correct attribute change information.
    /// </summary>
    [Test]
    public async Task ExecuteExportsAsync_Preview_ContainsAttributeChangesAsync()
    {
        // Arrange
        var targetSystem = ConnectedSystemsData.Single(s => s.Name == "Dummy Target System");
        var targetUserType = ConnectedSystemObjectTypesData.Single(t => t.Name == "TARGET_USER");
        var displayNameAttr = targetUserType.Attributes.Single(a => a.Name == MockTargetSystemAttributeNames.DisplayName.ToString());
        var mailAttr = targetUserType.Attributes.Single(a => a.Name == MockTargetSystemAttributeNames.Mail.ToString());

        var cso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = targetSystem.Id,
            Type = targetUserType,
            TypeId = targetUserType.Id
        };
        ConnectedSystemObjectsData.Add(cso);

        var pendingExport = new PendingExport
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = targetSystem.Id,
            ConnectedSystem = targetSystem,
            ConnectedSystemObject = cso,
            SourceMetaverseObjectId = Guid.NewGuid(),
            Status = PendingExportStatus.Pending,
            ChangeType = PendingExportChangeType.Update,
            CreatedAt = DateTime.UtcNow,
            AttributeValueChanges = new List<PendingExportAttributeValueChange>
            {
                new()
                {
                    Id = Guid.NewGuid(),
                    ChangeType = PendingExportAttributeChangeType.Update,
                    AttributeId = displayNameAttr.Id,
                    Attribute = displayNameAttr,
                    StringValue = "John Doe"
                },
                new()
                {
                    Id = Guid.NewGuid(),
                    ChangeType = PendingExportAttributeChangeType.Add,
                    AttributeId = mailAttr.Id,
                    Attribute = mailAttr,
                    StringValue = "john.doe@example.com"
                }
            }
        };
        PendingExportsData.Add(pendingExport);

        var mockConnector = new Mock<IConnector>();
        mockConnector.Setup(c => c.Name).Returns("Test Connector");

        // Act
        var result = await Jim.ExportExecution.ExecuteExportsAsync(
            targetSystem,
            mockConnector.Object,
            SyncRunMode.PreviewOnly);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Previews.Count, Is.EqualTo(1));

        var preview = result.Previews[0];
        Assert.That(preview.PendingExportId, Is.EqualTo(pendingExport.Id));
        Assert.That(preview.ChangeType, Is.EqualTo(PendingExportChangeType.Update));
        Assert.That(preview.ConnectedSystemObjectId, Is.EqualTo(cso.Id));
        Assert.That(preview.SourceMetaverseObjectId, Is.EqualTo(pendingExport.SourceMetaverseObjectId));
        Assert.That(preview.AttributeChanges.Count, Is.EqualTo(2));

        var displayNameChange = preview.AttributeChanges.Single(ac => ac.AttributeId == displayNameAttr.Id);
        Assert.That(displayNameChange.AttributeName, Is.EqualTo(MockTargetSystemAttributeNames.DisplayName.ToString()));
        Assert.That(displayNameChange.ChangeType, Is.EqualTo(PendingExportAttributeChangeType.Update));
        Assert.That(displayNameChange.NewValue, Is.EqualTo("John Doe"));

        var mailChange = preview.AttributeChanges.Single(ac => ac.AttributeId == mailAttr.Id);
        Assert.That(mailChange.AttributeName, Is.EqualTo(MockTargetSystemAttributeNames.Mail.ToString()));
        Assert.That(mailChange.ChangeType, Is.EqualTo(PendingExportAttributeChangeType.Add));
        Assert.That(mailChange.NewValue, Is.EqualTo("john.doe@example.com"));
    }

    /// <summary>
    /// Tests that different value types are correctly formatted in preview.
    /// </summary>
    [Test]
    public async Task ExecuteExportsAsync_Preview_FormatsValueTypesCorrectlyAsync()
    {
        // Arrange
        var targetSystem = ConnectedSystemsData.Single(s => s.Name == "Dummy Target System");
        var targetUserType = ConnectedSystemObjectTypesData.Single(t => t.Name == "TARGET_USER");
        var uacAttr = targetUserType.Attributes.Single(a => a.Name == MockTargetSystemAttributeNames.UserAccountControl.ToString());

        var cso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = targetSystem.Id,
            Type = targetUserType,
            TypeId = targetUserType.Id
        };
        ConnectedSystemObjectsData.Add(cso);

        var pendingExport = new PendingExport
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = targetSystem.Id,
            ConnectedSystem = targetSystem,
            ConnectedSystemObject = cso,
            Status = PendingExportStatus.Pending,
            ChangeType = PendingExportChangeType.Update,
            CreatedAt = DateTime.UtcNow,
            AttributeValueChanges = new List<PendingExportAttributeValueChange>
            {
                new()
                {
                    Id = Guid.NewGuid(),
                    ChangeType = PendingExportAttributeChangeType.Update,
                    AttributeId = uacAttr.Id,
                    Attribute = uacAttr,
                    IntValue = 512 // Normal account
                }
            }
        };
        PendingExportsData.Add(pendingExport);

        var mockConnector = new Mock<IConnector>();
        mockConnector.Setup(c => c.Name).Returns("Test Connector");

        // Act
        var result = await Jim.ExportExecution.ExecuteExportsAsync(
            targetSystem,
            mockConnector.Object,
            SyncRunMode.PreviewOnly);

        // Assert
        Assert.That(result.Previews.Count, Is.EqualTo(1));
        var preview = result.Previews[0];
        Assert.That(preview.AttributeChanges.Count, Is.EqualTo(1));
        Assert.That(preview.AttributeChanges[0].NewValue, Is.EqualTo("512"));
    }

    /// <summary>
    /// Tests that Create change type is correctly represented in preview.
    /// </summary>
    [Test]
    public async Task ExecuteExportsAsync_Preview_CreateChangeType_CorrectlyRepresentedAsync()
    {
        // Arrange
        var targetSystem = ConnectedSystemsData.Single(s => s.Name == "Dummy Target System");
        var mvo = MetaverseObjectsData[0];

        var pendingExport = new PendingExport
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = targetSystem.Id,
            ConnectedSystem = targetSystem,
            ConnectedSystemObject = null, // No CSO yet - it's a create
            SourceMetaverseObjectId = mvo.Id,
            Status = PendingExportStatus.Pending,
            ChangeType = PendingExportChangeType.Create,
            CreatedAt = DateTime.UtcNow,
            AttributeValueChanges = new List<PendingExportAttributeValueChange>()
        };
        PendingExportsData.Add(pendingExport);

        var mockConnector = new Mock<IConnector>();
        mockConnector.Setup(c => c.Name).Returns("Test Connector");

        // Act
        var result = await Jim.ExportExecution.ExecuteExportsAsync(
            targetSystem,
            mockConnector.Object,
            SyncRunMode.PreviewOnly);

        // Assert
        Assert.That(result.Previews.Count, Is.EqualTo(1));
        Assert.That(result.Previews[0].ChangeType, Is.EqualTo(PendingExportChangeType.Create));
        Assert.That(result.Previews[0].ConnectedSystemObjectId, Is.Null);
        Assert.That(result.Previews[0].SourceMetaverseObjectId, Is.EqualTo(mvo.Id));
    }

    /// <summary>
    /// Tests that Delete change type is correctly represented in preview.
    /// </summary>
    [Test]
    public async Task ExecuteExportsAsync_Preview_DeleteChangeType_CorrectlyRepresentedAsync()
    {
        // Arrange
        var targetSystem = ConnectedSystemsData.Single(s => s.Name == "Dummy Target System");
        var targetUserType = ConnectedSystemObjectTypesData.Single(t => t.Name == "TARGET_USER");

        var cso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = targetSystem.Id,
            Type = targetUserType,
            TypeId = targetUserType.Id
        };
        ConnectedSystemObjectsData.Add(cso);

        var pendingExport = new PendingExport
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = targetSystem.Id,
            ConnectedSystem = targetSystem,
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
            targetSystem,
            mockConnector.Object,
            SyncRunMode.PreviewOnly);

        // Assert
        Assert.That(result.Previews.Count, Is.EqualTo(1));
        Assert.That(result.Previews[0].ChangeType, Is.EqualTo(PendingExportChangeType.Delete));
        Assert.That(result.Previews[0].ConnectedSystemObjectId, Is.EqualTo(cso.Id));
    }

    #region Provisioning Flow End-to-End Tests

    /// <summary>
    /// End-to-end test: When export fails, the CSO should remain in PendingProvisioning status.
    /// This ensures the CSO↔MVO relationship is preserved for retry, and the CSO doesn't become
    /// orphaned in an inconsistent state.
    /// </summary>
    [Test]
    public async Task ExecuteExportsAsync_WhenExportFails_CsoRemainsInPendingProvisioningAsync()
    {
        // Arrange
        var targetSystem = ConnectedSystemsData.Single(s => s.Name == "Dummy Target System");
        var targetUserType = ConnectedSystemObjectTypesData.Single(t => t.Name == "TARGET_USER");
        var mvo = MetaverseObjectsData[0];
        var displayNameAttr = targetUserType.Attributes.Single(a => a.Name == MockTargetSystemAttributeNames.DisplayName.ToString());

        // Create a CSO in PendingProvisioning state (simulating the state after export evaluation)
        var pendingProvisioningCso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = targetSystem.Id,
            ConnectedSystem = targetSystem,
            Type = targetUserType,
            TypeId = targetUserType.Id,
            MetaverseObject = mvo,
            MetaverseObjectId = mvo.Id,
            JoinType = ConnectedSystemObjectJoinType.Provisioned,
            Status = ConnectedSystemObjectStatus.PendingProvisioning,
            DateJoined = DateTime.UtcNow,
            AttributeValues = new List<ConnectedSystemObjectAttributeValue>()
        };
        ConnectedSystemObjectsData.Add(pendingProvisioningCso);

        // Create a pending Create export that references the CSO
        var pendingExport = new PendingExport
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = targetSystem.Id,
            ConnectedSystem = targetSystem,
            ConnectedSystemObject = pendingProvisioningCso,
            SourceMetaverseObjectId = mvo.Id,
            Status = PendingExportStatus.Pending,
            ChangeType = PendingExportChangeType.Create,
            CreatedAt = DateTime.UtcNow,
            AttributeValueChanges = new List<PendingExportAttributeValueChange>
            {
                new()
                {
                    Id = Guid.NewGuid(),
                    ChangeType = PendingExportAttributeChangeType.Add,
                    AttributeId = displayNameAttr.Id,
                    Attribute = displayNameAttr,
                    StringValue = "John Doe"
                }
            }
        };
        PendingExportsData.Add(pendingExport);

        // Mock connector that implements both IConnector and IConnectorExportUsingCalls
        var mockConnector = new Mock<IConnector>();
        var mockExportConnector = mockConnector.As<IConnectorExportUsingCalls>();
        mockConnector.Setup(c => c.Name).Returns("Test Failing Connector");
        mockExportConnector.Setup(c => c.Export(It.IsAny<IList<PendingExport>>()))
            .Returns(new List<ExportResult>
            {
                ExportResult.Failed("Connection to target system failed")
            });

        // Mock update methods
        MockDbSetPendingExports.Setup(set => set.Update(It.IsAny<PendingExport>()))
            .Callback((PendingExport pe) =>
            {
                var existingPe = PendingExportsData.Find(p => p.Id == pe.Id);
                if (existingPe != null)
                {
                    existingPe.Status = pe.Status;
                    existingPe.ErrorCount = pe.ErrorCount;
                    existingPe.LastErrorMessage = pe.LastErrorMessage;
                }
            });

        // Act
        var result = await Jim.ExportExecution.ExecuteExportsAsync(
            targetSystem,
            mockConnector.Object,
            SyncRunMode.PreviewAndSync);

        // Assert - Export should have failed
        Assert.That(result.FailedCount, Is.EqualTo(1), "Export should have failed");
        Assert.That(result.SuccessCount, Is.EqualTo(0), "No exports should have succeeded");

        // Assert - CSO should still be in PendingProvisioning status (not Normal, not deleted)
        Assert.That(pendingProvisioningCso.Status, Is.EqualTo(ConnectedSystemObjectStatus.PendingProvisioning),
            "CSO should remain in PendingProvisioning status after export failure");

        // Assert - CSO should still be linked to MVO
        Assert.That(pendingProvisioningCso.MetaverseObjectId, Is.EqualTo(mvo.Id),
            "CSO should still be linked to MVO after export failure");
        Assert.That(pendingProvisioningCso.JoinType, Is.EqualTo(ConnectedSystemObjectJoinType.Provisioned),
            "CSO JoinType should remain Provisioned");
    }

    /// <summary>
    /// End-to-end test: When export succeeds, the CSO should transition from PendingProvisioning to Normal.
    /// This validates the complete provisioning flow where a new object is successfully created in the target system.
    /// </summary>
    [Test]
    public async Task ExecuteExportsAsync_WhenExportSucceeds_CsoTransitionsToNormalAsync()
    {
        // Arrange
        var targetSystem = ConnectedSystemsData.Single(s => s.Name == "Dummy Target System");
        var targetUserType = ConnectedSystemObjectTypesData.Single(t => t.Name == "TARGET_USER");
        var mvo = MetaverseObjectsData[0];
        var displayNameAttr = targetUserType.Attributes.Single(a => a.Name == MockTargetSystemAttributeNames.DisplayName.ToString());

        // Create objectGUID attribute for external ID
        var objectGuidAttr = new ConnectedSystemObjectTypeAttribute
        {
            Id = 999,
            Name = "objectGUID",
            Type = AttributeDataType.Guid,
            ConnectedSystemObjectType = targetUserType
        };
        targetUserType.Attributes.Add(objectGuidAttr);

        // Create a CSO in PendingProvisioning state with ExternalIdAttributeId set
        var pendingProvisioningCso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = targetSystem.Id,
            ConnectedSystem = targetSystem,
            Type = targetUserType,
            TypeId = targetUserType.Id,
            MetaverseObject = mvo,
            MetaverseObjectId = mvo.Id,
            JoinType = ConnectedSystemObjectJoinType.Provisioned,
            Status = ConnectedSystemObjectStatus.PendingProvisioning,
            DateJoined = DateTime.UtcNow,
            ExternalIdAttributeId = objectGuidAttr.Id,
            AttributeValues = new List<ConnectedSystemObjectAttributeValue>()
        };
        ConnectedSystemObjectsData.Add(pendingProvisioningCso);

        // Create a pending Create export
        var pendingExport = new PendingExport
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = targetSystem.Id,
            ConnectedSystem = targetSystem,
            ConnectedSystemObject = pendingProvisioningCso,
            SourceMetaverseObjectId = mvo.Id,
            Status = PendingExportStatus.Pending,
            ChangeType = PendingExportChangeType.Create,
            CreatedAt = DateTime.UtcNow,
            AttributeValueChanges = new List<PendingExportAttributeValueChange>
            {
                new()
                {
                    Id = Guid.NewGuid(),
                    ChangeType = PendingExportAttributeChangeType.Add,
                    AttributeId = displayNameAttr.Id,
                    Attribute = displayNameAttr,
                    StringValue = "John Doe"
                }
            }
        };
        PendingExportsData.Add(pendingExport);

        // Mock connector that implements both IConnector and IConnectorExportUsingCalls
        var generatedObjectGuid = Guid.NewGuid();
        var mockConnector = new Mock<IConnector>();
        var mockExportConnector = mockConnector.As<IConnectorExportUsingCalls>();
        mockConnector.Setup(c => c.Name).Returns("Test Successful Connector");
        mockExportConnector.Setup(c => c.Export(It.IsAny<IList<PendingExport>>()))
            .Returns(new List<ExportResult>
            {
                ExportResult.Succeeded(generatedObjectGuid.ToString())
            });

        // Track deleted pending exports
        var deletedExportIds = new List<Guid>();
        MockDbSetPendingExports.Setup(set => set.Remove(It.IsAny<PendingExport>()))
            .Callback((PendingExport pe) =>
            {
                deletedExportIds.Add(pe.Id);
                PendingExportsData.RemoveAll(p => p.Id == pe.Id);
            });

        // Track CSO updates
        var updatedCsos = new List<ConnectedSystemObject>();
        MockDbSetConnectedSystemObjects.Setup(set => set.Update(It.IsAny<ConnectedSystemObject>()))
            .Callback((ConnectedSystemObject cso) =>
            {
                updatedCsos.Add(cso);
            });

        // Also need to mock PendingExport updates
        MockDbSetPendingExports.Setup(set => set.Update(It.IsAny<PendingExport>()));

        // Act
        var result = await Jim.ExportExecution.ExecuteExportsAsync(
            targetSystem,
            mockConnector.Object,
            SyncRunMode.PreviewAndSync);

        // Assert - Export should have succeeded
        Assert.That(result.SuccessCount, Is.EqualTo(1), "Export should have succeeded");
        Assert.That(result.FailedCount, Is.EqualTo(0), "No exports should have failed");

        // Assert - CSO should have transitioned to Normal status
        Assert.That(pendingProvisioningCso.Status, Is.EqualTo(ConnectedSystemObjectStatus.Normal),
            "CSO should transition from PendingProvisioning to Normal after successful export");

        // Assert - CSO should still be linked to MVO
        Assert.That(pendingProvisioningCso.MetaverseObjectId, Is.EqualTo(mvo.Id),
            "CSO should remain linked to MVO after successful export");
        Assert.That(pendingProvisioningCso.JoinType, Is.EqualTo(ConnectedSystemObjectJoinType.Provisioned),
            "CSO JoinType should remain Provisioned");

        // Assert - External ID attribute should be populated
        var externalIdAttrValue = pendingProvisioningCso.AttributeValues
            .FirstOrDefault(av => av.AttributeId == objectGuidAttr.Id);
        Assert.That(externalIdAttrValue, Is.Not.Null, "External ID attribute should be created");
        Assert.That(externalIdAttrValue!.GuidValue, Is.EqualTo(generatedObjectGuid),
            "External ID should be set to the objectGUID returned by the connector");

        // Assert - PendingExport should have been deleted (cleaned up)
        Assert.That(deletedExportIds, Does.Contain(pendingExport.Id),
            "PendingExport should be deleted after successful export");
    }

    #endregion
}
