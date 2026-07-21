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

        // Set up the Connected System Attributes mock (all attributes from all object types)
        ConnectedSystemAttributesData = ConnectedSystemObjectTypesData.SelectMany(t => t.Attributes).ToList();
        MockDbSetConnectedSystemAttributes = ConnectedSystemAttributesData.BuildMockDbSet();

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

        // Set up the Synchronisation Rule stub mocks
        SyncRulesData = TestUtilities.GetSyncRuleData();
        MockDbSetSyncRules = SyncRulesData.BuildMockDbSet();

        // Mock entity framework calls
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

        // Instantiate Jim using the mocked db context
        SyncRepo = TestUtilities.CreateSyncRepository(activity: ActivitiesData.First());
        Jim = new JimApplication(new PostgresDataRepository(MockJimDbContext.Object), syncRepository: SyncRepo);
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

        // Create a Pending Export
        var pendingExport = new PendingExport
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = targetSystem.Id,
            ConnectedSystem = targetSystem,
            ConnectedSystemObject = cso,
            ConnectedSystemObjectId = cso.Id,
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
        SyncRepo.SeedPendingExport(pendingExport);

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
        Assert.That(result.ProcessedPendingExportIds.Count, Is.GreaterThan(0), "Expected processed exports");
        Assert.That(result.ProcessedPendingExportIds, Does.Contain(pendingExport.Id));

        // Verify export details via the original Pending Export
        Assert.That(pendingExport.ChangeType, Is.EqualTo(PendingExportChangeType.Update));
        Assert.That(pendingExport.AttributeValueChanges.Count, Is.EqualTo(1));
        Assert.That(pendingExport.AttributeValueChanges[0].StringValue, Is.EqualTo("New Display Name"));
    }

    /// <summary>
    /// Tests that when there are no Pending Exports, an empty result is returned.
    /// </summary>
    [Test]
    public async Task ExecuteExportsAsync_NoPendingExports_ReturnsEmptyResultAsync()
    {
        // Arrange
        var targetSystem = ConnectedSystemsData.Single(s => s.Name == "Dummy Target System");
        var mockConnector = new Mock<IConnector>();
        mockConnector.Setup(c => c.Name).Returns("Test Connector");

        // Ensure no Pending Exports
        PendingExportsData.Clear();

        // Act
        var result = await Jim.ExportExecution.ExecuteExportsAsync(
            targetSystem,
            mockConnector.Object,
            SyncRunMode.PreviewAndSync);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.TotalPendingExports, Is.EqualTo(0));
        Assert.That(result.ProcessedPendingExportIds.Count, Is.EqualTo(0));
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

        // Create a Pending Export that's not due for retry yet
        var pendingExport = new PendingExport
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = targetSystem.Id,
            ConnectedSystem = targetSystem,
            ConnectedSystemObject = cso,
            ConnectedSystemObjectId = cso.Id,
            Status = PendingExportStatus.ExportNotConfirmed, // Failed status
            ChangeType = PendingExportChangeType.Update,
            CreatedAt = DateTime.UtcNow.AddMinutes(-30),
            NextRetryAt = DateTime.UtcNow.AddMinutes(30), // Not due yet
            ErrorCount = 1,
            MaxRetries = 3,
            AttributeValueChanges = new List<PendingExportAttributeValueChange>()
        };
        PendingExportsData.Add(pendingExport);
        SyncRepo.SeedPendingExport(pendingExport);

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

        // Create a Pending Export that has exceeded max retries
        var pendingExport = new PendingExport
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = targetSystem.Id,
            ConnectedSystem = targetSystem,
            ConnectedSystemObject = cso,
            ConnectedSystemObjectId = cso.Id,
            Status = PendingExportStatus.ExportNotConfirmed,
            ChangeType = PendingExportChangeType.Update,
            CreatedAt = DateTime.UtcNow.AddHours(-1),
            ErrorCount = 3,
            MaxRetries = 3, // Max reached
            AttributeValueChanges = new List<PendingExportAttributeValueChange>()
        };
        PendingExportsData.Add(pendingExport);
        SyncRepo.SeedPendingExport(pendingExport);

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

        var displayNameAttr = targetUserType.Attributes.Single(a => a.Name == MockTargetSystemAttributeNames.DisplayName.ToString());

        var pendingExport = new PendingExport
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = targetSystem.Id,
            ConnectedSystem = targetSystem,
            ConnectedSystemObject = cso,
            ConnectedSystemObjectId = cso.Id,
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
                    StringValue = "Test Value",
                    Status = PendingExportAttributeChangeStatus.Pending
                }
            }
        };
        PendingExportsData.Add(pendingExport);
        SyncRepo.SeedPendingExport(pendingExport);

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

        var displayNameAttr = targetUserType.Attributes.Single(a => a.Name == MockTargetSystemAttributeNames.DisplayName.ToString());

        // Create multiple Pending Exports
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

            var pe = new PendingExport
            {
                Id = Guid.NewGuid(),
                ConnectedSystemId = targetSystem.Id,
                ConnectedSystem = targetSystem,
                ConnectedSystemObject = cso,
                ConnectedSystemObjectId = cso.Id,
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
                        StringValue = $"Test Value {i}",
                        Status = PendingExportAttributeChangeStatus.Pending
                    }
                }
            };
            PendingExportsData.Add(pe);
            SyncRepo.SeedPendingExport(pe);
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
            ConnectedSystemObjectId = cso.Id,
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
                    StringValue = "john.doe@panoply.org"
                }
            }
        };
        PendingExportsData.Add(pendingExport);
        SyncRepo.SeedPendingExport(pendingExport);

        var mockConnector = new Mock<IConnector>();
        mockConnector.Setup(c => c.Name).Returns("Test Connector");

        // Act
        var result = await Jim.ExportExecution.ExecuteExportsAsync(
            targetSystem,
            mockConnector.Object,
            SyncRunMode.PreviewOnly);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.ProcessedPendingExportIds.Count, Is.EqualTo(1));
        Assert.That(result.ProcessedPendingExportIds[0], Is.EqualTo(pendingExport.Id));

        // Verify export details via the original Pending Export
        Assert.That(pendingExport.ChangeType, Is.EqualTo(PendingExportChangeType.Update));
        Assert.That(pendingExport.ConnectedSystemObject?.Id, Is.EqualTo(cso.Id));
        Assert.That(pendingExport.AttributeValueChanges.Count, Is.EqualTo(2));

        var displayNameChange = pendingExport.AttributeValueChanges.Single(ac => ac.AttributeId == displayNameAttr.Id);
        Assert.That(displayNameChange.Attribute?.Name, Is.EqualTo(MockTargetSystemAttributeNames.DisplayName.ToString()));
        Assert.That(displayNameChange.ChangeType, Is.EqualTo(PendingExportAttributeChangeType.Update));
        Assert.That(displayNameChange.StringValue, Is.EqualTo("John Doe"));

        var mailChange = pendingExport.AttributeValueChanges.Single(ac => ac.AttributeId == mailAttr.Id);
        Assert.That(mailChange.Attribute?.Name, Is.EqualTo(MockTargetSystemAttributeNames.Mail.ToString()));
        Assert.That(mailChange.ChangeType, Is.EqualTo(PendingExportAttributeChangeType.Add));
        Assert.That(mailChange.StringValue, Is.EqualTo("john.doe@panoply.org"));
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
            ConnectedSystemObjectId = cso.Id,
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
        SyncRepo.SeedPendingExport(pendingExport);

        var mockConnector = new Mock<IConnector>();
        mockConnector.Setup(c => c.Name).Returns("Test Connector");

        // Act
        var result = await Jim.ExportExecution.ExecuteExportsAsync(
            targetSystem,
            mockConnector.Object,
            SyncRunMode.PreviewOnly);

        // Assert
        Assert.That(result.ProcessedPendingExportIds.Count, Is.EqualTo(1));
        Assert.That(result.ProcessedPendingExportIds, Does.Contain(pendingExport.Id));

        // Verify the integer value is stored correctly in the Pending Export
        Assert.That(pendingExport.AttributeValueChanges.Count, Is.EqualTo(1));
        Assert.That(pendingExport.AttributeValueChanges[0].IntValue, Is.EqualTo(512));
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
        SyncRepo.SeedPendingExport(pendingExport);

        var mockConnector = new Mock<IConnector>();
        mockConnector.Setup(c => c.Name).Returns("Test Connector");

        // Act
        var result = await Jim.ExportExecution.ExecuteExportsAsync(
            targetSystem,
            mockConnector.Object,
            SyncRunMode.PreviewOnly);

        // Assert
        Assert.That(result.ProcessedPendingExportIds.Count, Is.EqualTo(1));
        Assert.That(result.ProcessedPendingExportIds, Does.Contain(pendingExport.Id));

        // Verify Create change type via the original Pending Export
        Assert.That(pendingExport.ChangeType, Is.EqualTo(PendingExportChangeType.Create));
        Assert.That(pendingExport.ConnectedSystemObject, Is.Null);
        Assert.That(pendingExport.SourceMetaverseObjectId, Is.EqualTo(mvo.Id));
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
            ConnectedSystemObjectId = cso.Id,
            Status = PendingExportStatus.Pending,
            ChangeType = PendingExportChangeType.Delete,
            CreatedAt = DateTime.UtcNow,
            AttributeValueChanges = new List<PendingExportAttributeValueChange>()
        };
        PendingExportsData.Add(pendingExport);
        SyncRepo.SeedPendingExport(pendingExport);

        var mockConnector = new Mock<IConnector>();
        mockConnector.Setup(c => c.Name).Returns("Test Connector");

        // Act
        var result = await Jim.ExportExecution.ExecuteExportsAsync(
            targetSystem,
            mockConnector.Object,
            SyncRunMode.PreviewOnly);

        // Assert
        Assert.That(result.ProcessedPendingExportIds.Count, Is.EqualTo(1));
        Assert.That(result.ProcessedPendingExportIds, Does.Contain(pendingExport.Id));

        // Verify Delete change type via the original Pending Export
        Assert.That(pendingExport.ChangeType, Is.EqualTo(PendingExportChangeType.Delete));
        Assert.That(pendingExport.ConnectedSystemObject?.Id, Is.EqualTo(cso.Id));
    }

    #region Created Container Tracking Tests

    /// <summary>
    /// Tests that created container DNs are captured from connectors that implement IConnectorContainerCreation.
    /// </summary>
    [Test]
    public async Task ExecuteExportsAsync_WithContainerCreation_CapturesCreatedContainerExternalIdsAsync()
    {
        // Arrange
        var targetSystem = ConnectedSystemsData.Single(s => s.Name == "Dummy Target System");
        var targetUserType = ConnectedSystemObjectTypesData.Single(t => t.Name == "TARGET_USER");
        var displayNameAttr = targetUserType.Attributes.Single(a => a.Name == MockTargetSystemAttributeNames.DisplayName.ToString());

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
            ConnectedSystemObjectId = cso.Id,
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
                    StringValue = "Test User",
                    Status = PendingExportAttributeChangeStatus.Pending
                }
            }
        };
        PendingExportsData.Add(pendingExport);
        SyncRepo.SeedPendingExport(pendingExport);

        // Mock connector that implements IConnector, IConnectorExportUsingCalls, and IConnectorContainerCreation
        var mockConnector = new Mock<IConnector>();
        var mockExportConnector = mockConnector.As<IConnectorExportUsingCalls>();
        var mockContainerCreation = mockConnector.As<IConnectorContainerCreation>();

        mockConnector.Setup(c => c.Name).Returns("Test Container Creation Connector");
        mockExportConnector.Setup(c => c.ExportAsync(It.IsAny<IList<PendingExport>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ConnectedSystemExportResult> { ConnectedSystemExportResult.Succeeded() });

        // Simulate that the connector created two OUs during export
        var createdContainers = new List<string>
        {
            "OU=Sales,OU=Borton Corp,DC=testdomain,DC=local",
            "OU=Marketing,OU=Borton Corp,DC=testdomain,DC=local"
        };
        mockContainerCreation.Setup(c => c.CreatedContainerExternalIds)
            .Returns(createdContainers.AsReadOnly());

        // Act
        var result = await Jim.ExportExecution.ExecuteExportsAsync(
            targetSystem,
            mockConnector.Object,
            SyncRunMode.PreviewAndSync);

        // Assert
        Assert.That(result.SuccessCount, Is.EqualTo(1), "Export should have succeeded");
        Assert.That(result.CreatedContainerExternalIds.Count, Is.EqualTo(2), "Should have captured 2 created containers");
        Assert.That(result.CreatedContainerExternalIds, Does.Contain("OU=Sales,OU=Borton Corp,DC=testdomain,DC=local"));
        Assert.That(result.CreatedContainerExternalIds, Does.Contain("OU=Marketing,OU=Borton Corp,DC=testdomain,DC=local"));
    }

    /// <summary>
    /// Tests that when a connector doesn't implement IConnectorContainerCreation, CreatedContainerExternalIds remains empty.
    /// </summary>
    [Test]
    public async Task ExecuteExportsAsync_WithoutContainerCreation_CreatedContainerExternalIdsIsEmptyAsync()
    {
        // Arrange
        var targetSystem = ConnectedSystemsData.Single(s => s.Name == "Dummy Target System");
        var targetUserType = ConnectedSystemObjectTypesData.Single(t => t.Name == "TARGET_USER");
        var displayNameAttr = targetUserType.Attributes.Single(a => a.Name == MockTargetSystemAttributeNames.DisplayName.ToString());

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
            ConnectedSystemObjectId = cso.Id,
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
                    StringValue = "Updated Name",
                    Status = PendingExportAttributeChangeStatus.Pending
                }
            }
        };
        PendingExportsData.Add(pendingExport);
        SyncRepo.SeedPendingExport(pendingExport);

        // Mock connector that only implements IConnector and IConnectorExportUsingCalls (not IConnectorContainerCreation)
        var mockConnector = new Mock<IConnector>();
        var mockExportConnector = mockConnector.As<IConnectorExportUsingCalls>();

        mockConnector.Setup(c => c.Name).Returns("Test Regular Connector");
        mockExportConnector.Setup(c => c.ExportAsync(It.IsAny<IList<PendingExport>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ConnectedSystemExportResult> { ConnectedSystemExportResult.Succeeded() });

        // Act
        var result = await Jim.ExportExecution.ExecuteExportsAsync(
            targetSystem,
            mockConnector.Object,
            SyncRunMode.PreviewAndSync);

        // Assert
        Assert.That(result.SuccessCount, Is.EqualTo(1), "Export should have succeeded");
        Assert.That(result.CreatedContainerExternalIds, Is.Empty, "CreatedContainerExternalIds should be empty when connector doesn't support container creation");
    }

    #endregion

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
        SyncRepo.SeedConnectedSystemObject(pendingProvisioningCso);

        // Create a pending Create export that references the CSO
        var pendingExport = new PendingExport
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = targetSystem.Id,
            ConnectedSystem = targetSystem,
            ConnectedSystemObject = pendingProvisioningCso,
            ConnectedSystemObjectId = pendingProvisioningCso.Id,
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
        SyncRepo.SeedPendingExport(pendingExport);

        // Mock connector that implements both IConnector and IConnectorExportUsingCalls
        var mockConnector = new Mock<IConnector>();
        var mockExportConnector = mockConnector.As<IConnectorExportUsingCalls>();
        mockConnector.Setup(c => c.Name).Returns("Test Failing Connector");
        mockExportConnector.Setup(c => c.ExportAsync(It.IsAny<IList<PendingExport>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ConnectedSystemExportResult>
            {
                ConnectedSystemExportResult.Failed("Connection to target system failed")
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
    /// End-to-end test: When export succeeds, the CSO should remain in PendingProvisioning status.
    /// The transition to Normal only occurs during confirming import when the object is verified
    /// to exist in the target system. This allows the confirming import to match the CSO by
    /// secondary external ID (e.g., distinguishedName) since the primary external ID (e.g., objectGUID)
    /// is typically system-assigned and not known until the confirming import.
    /// </summary>
    [Test]
    public async Task ExecuteExportsAsync_WhenExportSucceeds_CsoRemainsPendingProvisioningAsync()
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
        ConnectedSystemAttributesData.Add(objectGuidAttr);
        // Re-seed the object type so SyncRepo sees the new attribute
        SyncRepo.SeedObjectType(targetUserType);

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
        SyncRepo.SeedConnectedSystemObject(pendingProvisioningCso);

        // Create a pending Create export
        var pendingExport = new PendingExport
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = targetSystem.Id,
            ConnectedSystem = targetSystem,
            ConnectedSystemObject = pendingProvisioningCso,
            ConnectedSystemObjectId = pendingProvisioningCso.Id,
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
                    StringValue = "John Doe",
                    Status = PendingExportAttributeChangeStatus.Pending
                }
            }
        };
        PendingExportsData.Add(pendingExport);
        SyncRepo.SeedPendingExport(pendingExport);

        // Mock connector that implements both IConnector and IConnectorExportUsingCalls
        var generatedObjectGuid = Guid.NewGuid();
        var mockConnector = new Mock<IConnector>();
        var mockExportConnector = mockConnector.As<IConnectorExportUsingCalls>();
        mockConnector.Setup(c => c.Name).Returns("Test Successful Connector");
        mockExportConnector.Setup(c => c.ExportAsync(It.IsAny<IList<PendingExport>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ConnectedSystemExportResult>
            {
                ConnectedSystemExportResult.Succeeded(generatedObjectGuid.ToString())
            });

        // Act
        var result = await Jim.ExportExecution.ExecuteExportsAsync(
            targetSystem,
            mockConnector.Object,
            SyncRunMode.PreviewAndSync);

        // Assert - Export should have succeeded
        Assert.That(result.SuccessCount, Is.EqualTo(1), "Export should have succeeded");
        Assert.That(result.FailedCount, Is.EqualTo(0), "No exports should have failed");

        // Assert - CSO should remain in PendingProvisioning status
        // The transition to Normal happens during confirming import, not during export execution.
        // This allows the confirming import to match the CSO by secondary external ID.
        Assert.That(pendingProvisioningCso.Status, Is.EqualTo(ConnectedSystemObjectStatus.PendingProvisioning),
            "CSO should remain in PendingProvisioning after export - transition to Normal happens during confirming import");

        // Assert - CSO should still be linked to MVO
        Assert.That(pendingProvisioningCso.MetaverseObjectId, Is.EqualTo(mvo.Id),
            "CSO should remain linked to MVO after successful export");
        Assert.That(pendingProvisioningCso.JoinType, Is.EqualTo(ConnectedSystemObjectJoinType.Provisioned),
            "CSO JoinType should remain Provisioned");

        // Assert - External ID attribute should be populated with the connector-returned value
        var externalIdAttrValue = pendingProvisioningCso.AttributeValues
            .FirstOrDefault(av => av.AttributeId == objectGuidAttr.Id);
        Assert.That(externalIdAttrValue, Is.Not.Null, "External ID attribute should be created");
        Assert.That(externalIdAttrValue!.GuidValue, Is.EqualTo(generatedObjectGuid),
            "External ID should be set to the objectGUID returned by the connector");

        // Assert - PendingExport should have been updated (not deleted) with ExportedPendingConfirmation status
        // Deletion happens during import confirmation, not immediately after export
        Assert.That(pendingExport.Status, Is.EqualTo(PendingExportStatus.Exported),
            "PendingExport status should be Exported after successful export");
        Assert.That(pendingExport.AttributeValueChanges[0].Status, Is.EqualTo(PendingExportAttributeChangeStatus.ExportedPendingConfirmation),
            "Attribute change status should be ExportedPendingConfirmation");
        Assert.That(pendingExport.AttributeValueChanges[0].ExportAttemptCount, Is.EqualTo(1),
            "Export attempt count should be 1");
    }

    /// <summary>
    /// Tests that ErrorCount is incremented exactly once when an export fails.
    /// This verifies the fix for a bug where ErrorCount was being incremented twice:
    /// once in the connector's catch block and once in ExportExecutionServer.MarkExportFailed.
    /// The connector should only return ConnectedSystemExportResult.Failed() without modifying ErrorCount.
    /// </summary>
    [Test]
    public async Task ExecuteExportsAsync_WhenExportFails_ErrorCountIsIncrementedExactlyOnceAsync()
    {
        // Arrange
        var targetSystem = ConnectedSystemsData.Single(s => s.Name == "Dummy Target System");
        var targetUserType = ConnectedSystemObjectTypesData.Single(t => t.Name == "TARGET_USER");
        var mvo = MetaverseObjectsData[0];
        var displayNameAttr = targetUserType.Attributes.Single(a => a.Name == MockTargetSystemAttributeNames.DisplayName.ToString());

        // Create a CSO
        var cso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = targetSystem.Id,
            ConnectedSystem = targetSystem,
            Type = targetUserType,
            TypeId = targetUserType.Id,
            MetaverseObject = mvo,
            MetaverseObjectId = mvo.Id,
            JoinType = ConnectedSystemObjectJoinType.Provisioned,
            Status = ConnectedSystemObjectStatus.Normal,
            DateJoined = DateTime.UtcNow,
            AttributeValues = new List<ConnectedSystemObjectAttributeValue>()
        };
        ConnectedSystemObjectsData.Add(cso);

        // Create a Pending Export with ErrorCount = 0 (first attempt)
        var pendingExport = new PendingExport
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = targetSystem.Id,
            ConnectedSystem = targetSystem,
            ConnectedSystemObject = cso,
            ConnectedSystemObjectId = cso.Id,
            SourceMetaverseObjectId = mvo.Id,
            Status = PendingExportStatus.Pending,
            ChangeType = PendingExportChangeType.Update,
            CreatedAt = DateTime.UtcNow,
            ErrorCount = 0, // Starting at 0 - this is the first attempt
            MaxRetries = 3,
            AttributeValueChanges = new List<PendingExportAttributeValueChange>
            {
                new()
                {
                    Id = Guid.NewGuid(),
                    ChangeType = PendingExportAttributeChangeType.Update,
                    AttributeId = displayNameAttr.Id,
                    Attribute = displayNameAttr,
                    StringValue = "Updated Name"
                }
            }
        };
        PendingExportsData.Add(pendingExport);
        SyncRepo.SeedPendingExport(pendingExport);

        // Mock connector that returns a failure
        var mockConnector = new Mock<IConnector>();
        var mockExportConnector = mockConnector.As<IConnectorExportUsingCalls>();
        mockConnector.Setup(c => c.Name).Returns("Test Failing Connector");
        mockExportConnector.Setup(c => c.ExportAsync(It.IsAny<IList<PendingExport>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ConnectedSystemExportResult>
            {
                ConnectedSystemExportResult.Failed("LDAP error: The object exists. Attribute member already exists")
            });

        // Act
        var result = await Jim.ExportExecution.ExecuteExportsAsync(
            targetSystem,
            mockConnector.Object,
            SyncRunMode.PreviewAndSync);

        // Assert - Export should have failed
        Assert.That(result.FailedCount, Is.EqualTo(1), "One export should have failed");
        Assert.That(result.SuccessCount, Is.EqualTo(0), "No exports should have succeeded");

        // Assert - ErrorCount should be incremented exactly once (from 0 to 1)
        Assert.That(pendingExport.ErrorCount, Is.EqualTo(1),
            "ErrorCount should be incremented exactly once (from 0 to 1). " +
            "If this is 2, it indicates a double-increment bug where both the connector and " +
            "ExportExecutionServer are incrementing ErrorCount.");

        // Assert - Status should be Pending (still retrying, not Failed)
        Assert.That(pendingExport.Status, Is.EqualTo(PendingExportStatus.Pending),
            "Status should be Pending while retries remain");

        // Assert - Error message should be captured
        Assert.That(pendingExport.LastErrorMessage, Is.Not.Null.And.Contains("LDAP error"),
            "Error message should be captured from ConnectedSystemExportResult");

        // Assert - ProcessedExportItems should report ErrorCount = 1
        Assert.That(result.ProcessedExportItems.Count, Is.EqualTo(1), "Should have one processed export item");
        Assert.That(result.ProcessedExportItems[0].ErrorCount, Is.EqualTo(1),
            "ProcessedExportItem should report ErrorCount = 1 for activity tracking");
    }

    /// <summary>
    /// Tests that ErrorCount continues to increment correctly on repeated failures.
    /// Each failure should increment ErrorCount by exactly 1.
    /// </summary>
    [Test]
    public async Task ExecuteExportsAsync_RepeatedFailures_ErrorCountIncrementsCorrectlyAsync()
    {
        // Arrange
        var targetSystem = ConnectedSystemsData.Single(s => s.Name == "Dummy Target System");
        var targetUserType = ConnectedSystemObjectTypesData.Single(t => t.Name == "TARGET_USER");
        var mvo = MetaverseObjectsData[0];
        var displayNameAttr = targetUserType.Attributes.Single(a => a.Name == MockTargetSystemAttributeNames.DisplayName.ToString());

        // Create a CSO
        var cso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = targetSystem.Id,
            ConnectedSystem = targetSystem,
            Type = targetUserType,
            TypeId = targetUserType.Id,
            MetaverseObject = mvo,
            MetaverseObjectId = mvo.Id,
            JoinType = ConnectedSystemObjectJoinType.Provisioned,
            Status = ConnectedSystemObjectStatus.Normal,
            DateJoined = DateTime.UtcNow,
            AttributeValues = new List<ConnectedSystemObjectAttributeValue>()
        };
        ConnectedSystemObjectsData.Add(cso);

        // Create a Pending Export that has already failed once (ErrorCount = 1)
        var pendingExport = new PendingExport
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = targetSystem.Id,
            ConnectedSystem = targetSystem,
            ConnectedSystemObject = cso,
            ConnectedSystemObjectId = cso.Id,
            SourceMetaverseObjectId = mvo.Id,
            Status = PendingExportStatus.Pending,
            ChangeType = PendingExportChangeType.Update,
            CreatedAt = DateTime.UtcNow.AddHours(-1),
            ErrorCount = 1, // Already failed once
            MaxRetries = 5,
            NextRetryAt = null, // Due for retry
            AttributeValueChanges = new List<PendingExportAttributeValueChange>
            {
                new()
                {
                    Id = Guid.NewGuid(),
                    ChangeType = PendingExportAttributeChangeType.Update,
                    AttributeId = displayNameAttr.Id,
                    Attribute = displayNameAttr,
                    StringValue = "Updated Name"
                }
            }
        };
        PendingExportsData.Add(pendingExport);
        SyncRepo.SeedPendingExport(pendingExport);

        // Mock connector that returns a failure
        var mockConnector = new Mock<IConnector>();
        var mockExportConnector = mockConnector.As<IConnectorExportUsingCalls>();
        mockConnector.Setup(c => c.Name).Returns("Test Failing Connector");
        mockExportConnector.Setup(c => c.ExportAsync(It.IsAny<IList<PendingExport>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ConnectedSystemExportResult>
            {
                ConnectedSystemExportResult.Failed("Connection timeout")
            });

        // Act
        var result = await Jim.ExportExecution.ExecuteExportsAsync(
            targetSystem,
            mockConnector.Object,
            SyncRunMode.PreviewAndSync);

        // Assert - ErrorCount should be incremented from 1 to 2
        Assert.That(pendingExport.ErrorCount, Is.EqualTo(2),
            "ErrorCount should be incremented from 1 to 2 (exactly once per failure)");

        // Assert - Status should still be Pending (retries remain)
        Assert.That(pendingExport.Status, Is.EqualTo(PendingExportStatus.Pending),
            "Status should be Pending while retries remain");

        // Assert - ProcessedExportItems should report ErrorCount = 2
        Assert.That(result.ProcessedExportItems[0].ErrorCount, Is.EqualTo(2),
            "ProcessedExportItem should report the current ErrorCount for activity tracking");
    }

    #endregion

    #region Phase 1 - IsReadyForExecution in-memory filtering

    /// <summary>
    /// Tests that Update exports with no exportable attribute changes are skipped.
    /// This check can't be done at the database level as it requires evaluating
    /// the navigation property status values.
    /// </summary>
    [Test]
    public async Task ExecuteExportsAsync_UpdateWithNoExportableAttributeChanges_SkipsExportAsync()
    {
        // Arrange
        var targetSystem = ConnectedSystemsData.Single(s => s.Name == "Dummy Target System");
        var targetUserType = ConnectedSystemObjectTypesData.Single(t => t.Name == "TARGET_USER");
        var displayNameAttr = targetUserType.Attributes.Single(a => a.Name == MockTargetSystemAttributeNames.DisplayName.ToString());

        var cso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = targetSystem.Id,
            Type = targetUserType,
            TypeId = targetUserType.Id
        };
        ConnectedSystemObjectsData.Add(cso);

        // Create a Pending Export with only ExportedPendingConfirmation attribute changes (not exportable)
        var pendingExport = new PendingExport
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = targetSystem.Id,
            ConnectedSystem = targetSystem,
            ConnectedSystemObject = cso,
            ConnectedSystemObjectId = cso.Id,
            Status = PendingExportStatus.Exported,
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
                    StringValue = "Test",
                    Status = PendingExportAttributeChangeStatus.ExportedPendingConfirmation
                }
            }
        };
        PendingExportsData.Add(pendingExport);
        SyncRepo.SeedPendingExport(pendingExport);

        var mockConnector = new Mock<IConnector>();
        mockConnector.Setup(c => c.Name).Returns("Test Connector");

        // Act
        var result = await Jim.ExportExecution.ExecuteExportsAsync(
            targetSystem,
            mockConnector.Object,
            SyncRunMode.PreviewOnly);

        // Assert
        Assert.That(result.TotalPendingExports, Is.EqualTo(0),
            "Update export with no exportable attribute changes should be skipped");
    }

    /// <summary>
    /// Tests that Delete exports with Exported status are not re-executed.
    /// </summary>
    [Test]
    public async Task ExecuteExportsAsync_DeleteWithExportedStatus_SkipsExportAsync()
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

        // Create a Delete export that was already exported
        var pendingExport = new PendingExport
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = targetSystem.Id,
            ConnectedSystem = targetSystem,
            ConnectedSystemObject = cso,
            ConnectedSystemObjectId = cso.Id,
            Status = PendingExportStatus.Exported,
            ChangeType = PendingExportChangeType.Delete,
            CreatedAt = DateTime.UtcNow,
            AttributeValueChanges = new List<PendingExportAttributeValueChange>()
        };
        PendingExportsData.Add(pendingExport);
        SyncRepo.SeedPendingExport(pendingExport);

        var mockConnector = new Mock<IConnector>();
        mockConnector.Setup(c => c.Name).Returns("Test Connector");

        // Act
        var result = await Jim.ExportExecution.ExecuteExportsAsync(
            targetSystem,
            mockConnector.Object,
            SyncRunMode.PreviewOnly);

        // Assert
        Assert.That(result.TotalPendingExports, Is.EqualTo(0),
            "Delete export with Exported status should not be re-executed");
    }

    /// <summary>
    /// Tests that Update exports with Pending attribute changes are eligible.
    /// </summary>
    [Test]
    public async Task ExecuteExportsAsync_UpdateWithPendingAttributeChanges_IncludesExportAsync()
    {
        // Arrange
        var targetSystem = ConnectedSystemsData.Single(s => s.Name == "Dummy Target System");
        var targetUserType = ConnectedSystemObjectTypesData.Single(t => t.Name == "TARGET_USER");
        var displayNameAttr = targetUserType.Attributes.Single(a => a.Name == MockTargetSystemAttributeNames.DisplayName.ToString());

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
            ConnectedSystemObjectId = cso.Id,
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
                    StringValue = "Test",
                    Status = PendingExportAttributeChangeStatus.Pending
                }
            }
        };
        PendingExportsData.Add(pendingExport);
        SyncRepo.SeedPendingExport(pendingExport);

        var mockConnector = new Mock<IConnector>();
        mockConnector.Setup(c => c.Name).Returns("Test Connector");

        // Act
        var result = await Jim.ExportExecution.ExecuteExportsAsync(
            targetSystem,
            mockConnector.Object,
            SyncRunMode.PreviewOnly);

        // Assert
        Assert.That(result.TotalPendingExports, Is.EqualTo(1),
            "Update export with Pending attribute changes should be included");
    }

    /// <summary>
    /// Tests that Create exports are eligible even with no attribute changes.
    /// </summary>
    [Test]
    public async Task ExecuteExportsAsync_CreateWithNoAttributeChanges_IncludesExportAsync()
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
            ConnectedSystemObjectId = cso.Id,
            Status = PendingExportStatus.Pending,
            ChangeType = PendingExportChangeType.Create,
            CreatedAt = DateTime.UtcNow,
            AttributeValueChanges = new List<PendingExportAttributeValueChange>()
        };
        PendingExportsData.Add(pendingExport);
        SyncRepo.SeedPendingExport(pendingExport);

        var mockConnector = new Mock<IConnector>();
        mockConnector.Setup(c => c.Name).Returns("Test Connector");

        // Act
        var result = await Jim.ExportExecution.ExecuteExportsAsync(
            targetSystem,
            mockConnector.Object,
            SyncRunMode.PreviewOnly);

        // Assert
        Assert.That(result.TotalPendingExports, Is.EqualTo(1),
            "Create export should be eligible even without attribute changes");
    }

    /// <summary>
    /// Tests that progress reports during deferred export processing do not double-count
    /// processed exports. Previously, ProcessDeferredBatchesSequentiallyAsync reported
    /// ProcessedExports = result.SuccessCount + result.FailedCount + processedCount,
    /// but after each batch both result.SuccessCount and processedCount were incremented
    /// for the same items, causing the numerator to exceed the denominator.
    /// </summary>
    [Test]
    public async Task ExecuteExportsAsync_WithDeferredExports_ProgressNeverExceedsTotalAsync()
    {
        // Arrange
        var targetSystem = ConnectedSystemsData.Single(s => s.Name == "Dummy Target System");
        var targetUserType = ConnectedSystemObjectTypesData.Single(t => t.Name == "TARGET_USER");
        var displayNameAttr = targetUserType.Attributes.Single(a => a.Name == MockTargetSystemAttributeNames.DisplayName.ToString());
        var managerAttr = targetUserType.Attributes.Single(a => a.Name == MockTargetSystemAttributeNames.Manager.ToString());
        var objectGuidAttr = targetUserType.Attributes.Single(a => a.Name == MockTargetSystemAttributeNames.ObjectGuid.ToString());

        // Create 3 immediate exports (no unresolved references)
        for (var i = 0; i < 3; i++)
        {
            var cso = new ConnectedSystemObject
            {
                Id = Guid.NewGuid(),
                ConnectedSystemId = targetSystem.Id,
                Type = targetUserType,
                TypeId = targetUserType.Id,
                AttributeValues = new List<ConnectedSystemObjectAttributeValue>()
            };
            ConnectedSystemObjectsData.Add(cso);
            SyncRepo.SeedConnectedSystemObject(cso);

            var pe = new PendingExport
            {
                Id = Guid.NewGuid(),
                ConnectedSystemId = targetSystem.Id,
                ConnectedSystem = targetSystem,
                ConnectedSystemObject = cso,
                ConnectedSystemObjectId = cso.Id,
                Status = PendingExportStatus.Pending,
                ChangeType = PendingExportChangeType.Update,
                CreatedAt = DateTime.UtcNow.AddMinutes(-10 + i),
                AttributeValueChanges = new List<PendingExportAttributeValueChange>
                {
                    new()
                    {
                        Id = Guid.NewGuid(),
                        ChangeType = PendingExportAttributeChangeType.Update,
                        AttributeId = displayNameAttr.Id,
                        Attribute = displayNameAttr,
                        StringValue = $"Immediate User {i}",
                        Status = PendingExportAttributeChangeStatus.Pending
                    }
                }
            };
            PendingExportsData.Add(pe);
            SyncRepo.SeedPendingExport(pe);
        }

        // Create MVOs and target CSOs that the deferred references will resolve to
        var referencedMvoIds = new List<Guid>();
        for (var i = 0; i < 6; i++)
        {
            var mvoId = Guid.NewGuid();
            referencedMvoIds.Add(mvoId);
            var mvo = new MetaverseObject { Id = mvoId };
            SyncRepo.SeedMetaverseObject(mvo);

            // Create a CSO in the target system linked to this MVO (for reference resolution)
            var targetCso = new ConnectedSystemObject
            {
                Id = Guid.NewGuid(),
                ConnectedSystemId = targetSystem.Id,
                Type = targetUserType,
                TypeId = targetUserType.Id,
                MetaverseObjectId = mvoId,
                AttributeValues = new List<ConnectedSystemObjectAttributeValue>
                {
                    new()
                    {
                        Id = Guid.NewGuid(),
                        Attribute = objectGuidAttr,
                        AttributeId = objectGuidAttr.Id,
                        GuidValue = Guid.NewGuid()
                    }
                }
            };
            SyncRepo.SeedConnectedSystemObject(targetCso);
        }

        // Create 6 deferred exports (with unresolved references) — use batch size 2
        // so they span 3 batches, exposing the double-counting bug from the second iteration onward
        for (var i = 0; i < 6; i++)
        {
            var cso = new ConnectedSystemObject
            {
                Id = Guid.NewGuid(),
                ConnectedSystemId = targetSystem.Id,
                Type = targetUserType,
                TypeId = targetUserType.Id,
                AttributeValues = new List<ConnectedSystemObjectAttributeValue>()
            };
            ConnectedSystemObjectsData.Add(cso);
            SyncRepo.SeedConnectedSystemObject(cso);

            var pe = new PendingExport
            {
                Id = Guid.NewGuid(),
                ConnectedSystemId = targetSystem.Id,
                ConnectedSystem = targetSystem,
                ConnectedSystemObject = cso,
                ConnectedSystemObjectId = cso.Id,
                Status = PendingExportStatus.Pending,
                ChangeType = PendingExportChangeType.Update,
                HasUnresolvedReferences = true,
                CreatedAt = DateTime.UtcNow.AddMinutes(i),
                AttributeValueChanges = new List<PendingExportAttributeValueChange>
                {
                    new()
                    {
                        Id = Guid.NewGuid(),
                        ChangeType = PendingExportAttributeChangeType.Update,
                        AttributeId = displayNameAttr.Id,
                        Attribute = displayNameAttr,
                        StringValue = $"Deferred User {i}",
                        Status = PendingExportAttributeChangeStatus.Pending
                    },
                    new()
                    {
                        Id = Guid.NewGuid(),
                        ChangeType = PendingExportAttributeChangeType.Update,
                        AttributeId = managerAttr.Id,
                        Attribute = managerAttr,
                        UnresolvedReferenceValue = referencedMvoIds[i].ToString(),
                        Status = PendingExportAttributeChangeStatus.Pending
                    }
                }
            };
            PendingExportsData.Add(pe);
            SyncRepo.SeedPendingExport(pe);
        }

        var mockConnector = new Mock<IConnector>();
        var mockExportConnector = mockConnector.As<IConnectorExportUsingCalls>();
        mockConnector.Setup(c => c.Name).Returns("Test Connector");
        mockExportConnector.Setup(c => c.ExportAsync(It.IsAny<IList<PendingExport>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IList<PendingExport> exports, CancellationToken _) =>
                exports.Select(_ => ConnectedSystemExportResult.Succeeded()).ToList());

        var progressReports = new List<ExportProgressInfo>();
        Func<ExportProgressInfo, Task> progressCallback = info =>
        {
            progressReports.Add(new ExportProgressInfo
            {
                Phase = info.Phase,
                TotalExports = info.TotalExports,
                ProcessedExports = info.ProcessedExports,
                Message = info.Message
            });
            return Task.CompletedTask;
        };

        var options = new ExportExecutionOptions
        {
            BatchSize = 2,
            MaxParallelism = 1
        };

        // Act
        var result = await Jim.ExportExecution.ExecuteExportsAsync(
            targetSystem,
            mockConnector.Object,
            SyncRunMode.PreviewAndSync,
            options,
            CancellationToken.None,
            progressCallback);

        // Assert — ProcessedExports must never exceed TotalExports in any progress report
        var overcountedReports = progressReports
            .Where(p => p.ProcessedExports > p.TotalExports)
            .ToList();

        Assert.That(overcountedReports, Is.Empty,
            $"Progress reports should never have ProcessedExports > TotalExports. " +
            $"Overcounted reports: {string.Join("; ", overcountedReports.Select(p => $"Phase={p.Phase}, Processed={p.ProcessedExports}, Total={p.TotalExports}, Message={p.Message}"))}");

        // Verify all exports were processed
        Assert.That(result.SuccessCount + result.FailedCount + result.DeferredCount, Is.EqualTo(9),
            "All 9 exports (3 immediate + 6 deferred) should be accounted for");
    }

    /// <summary>
    /// Parallel twin of the test above: the deferred PARALLEL batch phase must report cumulative
    /// progress (immediate-phase count + deferred progress), like the sequential deferred path
    /// already does. Reporting the phase-local count against the run-global total made the UI show
    /// "2,884 of 209,984" with a nonsense rate and ETA during the Scale200k10kGroups export
    /// (2026-07-13).
    /// </summary>
    [Test]
    public async Task ExecuteExportsAsync_ParallelDeferredExports_ReportsCumulativeProgressAsync()
    {
        // Arrange - identical data shape to the sequential test: 3 immediate + 6 deferred exports
        var targetSystem = ConnectedSystemsData.Single(s => s.Name == "Dummy Target System");
        var targetUserType = ConnectedSystemObjectTypesData.Single(t => t.Name == "TARGET_USER");
        var displayNameAttr = targetUserType.Attributes.Single(a => a.Name == MockTargetSystemAttributeNames.DisplayName.ToString());
        var managerAttr = targetUserType.Attributes.Single(a => a.Name == MockTargetSystemAttributeNames.Manager.ToString());
        var objectGuidAttr = targetUserType.Attributes.Single(a => a.Name == MockTargetSystemAttributeNames.ObjectGuid.ToString());

        for (var i = 0; i < 3; i++)
        {
            var cso = new ConnectedSystemObject
            {
                Id = Guid.NewGuid(),
                ConnectedSystemId = targetSystem.Id,
                Type = targetUserType,
                TypeId = targetUserType.Id,
                AttributeValues = new List<ConnectedSystemObjectAttributeValue>()
            };
            ConnectedSystemObjectsData.Add(cso);
            SyncRepo.SeedConnectedSystemObject(cso);

            var pe = new PendingExport
            {
                Id = Guid.NewGuid(),
                ConnectedSystemId = targetSystem.Id,
                ConnectedSystem = targetSystem,
                ConnectedSystemObject = cso,
                ConnectedSystemObjectId = cso.Id,
                Status = PendingExportStatus.Pending,
                ChangeType = PendingExportChangeType.Update,
                CreatedAt = DateTime.UtcNow.AddMinutes(-10 + i),
                AttributeValueChanges = new List<PendingExportAttributeValueChange>
                {
                    new()
                    {
                        Id = Guid.NewGuid(),
                        ChangeType = PendingExportAttributeChangeType.Update,
                        AttributeId = displayNameAttr.Id,
                        Attribute = displayNameAttr,
                        StringValue = $"Immediate User {i}",
                        Status = PendingExportAttributeChangeStatus.Pending
                    }
                }
            };
            PendingExportsData.Add(pe);
            SyncRepo.SeedPendingExport(pe);
        }

        var referencedMvoIds = new List<Guid>();
        for (var i = 0; i < 6; i++)
        {
            var mvoId = Guid.NewGuid();
            referencedMvoIds.Add(mvoId);
            SyncRepo.SeedMetaverseObject(new MetaverseObject { Id = mvoId });

            var targetCso = new ConnectedSystemObject
            {
                Id = Guid.NewGuid(),
                ConnectedSystemId = targetSystem.Id,
                Type = targetUserType,
                TypeId = targetUserType.Id,
                MetaverseObjectId = mvoId,
                AttributeValues = new List<ConnectedSystemObjectAttributeValue>
                {
                    new()
                    {
                        Id = Guid.NewGuid(),
                        Attribute = objectGuidAttr,
                        AttributeId = objectGuidAttr.Id,
                        GuidValue = Guid.NewGuid()
                    }
                }
            };
            SyncRepo.SeedConnectedSystemObject(targetCso);
        }

        for (var i = 0; i < 6; i++)
        {
            var cso = new ConnectedSystemObject
            {
                Id = Guid.NewGuid(),
                ConnectedSystemId = targetSystem.Id,
                Type = targetUserType,
                TypeId = targetUserType.Id,
                AttributeValues = new List<ConnectedSystemObjectAttributeValue>()
            };
            ConnectedSystemObjectsData.Add(cso);
            SyncRepo.SeedConnectedSystemObject(cso);

            var pe = new PendingExport
            {
                Id = Guid.NewGuid(),
                ConnectedSystemId = targetSystem.Id,
                ConnectedSystem = targetSystem,
                ConnectedSystemObject = cso,
                ConnectedSystemObjectId = cso.Id,
                Status = PendingExportStatus.Pending,
                ChangeType = PendingExportChangeType.Update,
                HasUnresolvedReferences = true,
                CreatedAt = DateTime.UtcNow.AddMinutes(i),
                AttributeValueChanges = new List<PendingExportAttributeValueChange>
                {
                    new()
                    {
                        Id = Guid.NewGuid(),
                        ChangeType = PendingExportAttributeChangeType.Update,
                        AttributeId = displayNameAttr.Id,
                        Attribute = displayNameAttr,
                        StringValue = $"Deferred User {i}",
                        Status = PendingExportAttributeChangeStatus.Pending
                    },
                    new()
                    {
                        Id = Guid.NewGuid(),
                        ChangeType = PendingExportAttributeChangeType.Update,
                        AttributeId = managerAttr.Id,
                        Attribute = managerAttr,
                        UnresolvedReferenceValue = referencedMvoIds[i].ToString(),
                        Status = PendingExportAttributeChangeStatus.Pending
                    }
                }
            };
            PendingExportsData.Add(pe);
            SyncRepo.SeedPendingExport(pe);
        }

        var mockConnector = new Mock<IConnector>();
        var mockExportConnector = mockConnector.As<IConnectorExportUsingCalls>();
        mockConnector.Setup(c => c.Name).Returns("Test Connector");
        mockExportConnector.Setup(c => c.ExportAsync(It.IsAny<IList<PendingExport>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IList<PendingExport> exports, CancellationToken _) =>
                exports.Select(_ => ConnectedSystemExportResult.Succeeded()).ToList());

        Func<IConnector> connectorFactory = () =>
        {
            var factoryConnector = new Mock<IConnector>();
            var factoryExportConnector = factoryConnector.As<IConnectorExportUsingCalls>();
            factoryConnector.Setup(c => c.Name).Returns("Test Connector");
            factoryExportConnector.Setup(c => c.ExportAsync(It.IsAny<IList<PendingExport>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((IList<PendingExport> exports, CancellationToken _) =>
                    exports.Select(_ => ConnectedSystemExportResult.Succeeded()).ToList());
            return factoryConnector.Object;
        };
        Func<JIM.Data.Repositories.ISyncRepositoryScope> repositoryFactory = () => new JIM.Data.Repositories.SyncRepositoryScope(TestUtilities.CreateSyncRepository(pendingExports: PendingExportsData));

        var progressReports = new List<ExportProgressInfo>();
        Func<ExportProgressInfo, Task> progressCallback = info =>
        {
            lock (progressReports)
            {
                progressReports.Add(new ExportProgressInfo
                {
                    Phase = info.Phase,
                    TotalExports = info.TotalExports,
                    ProcessedExports = info.ProcessedExports,
                    Message = info.Message
                });
            }
            return Task.CompletedTask;
        };

        // BatchSize 2 spreads the 6 deferred exports over 3 batches so the parallel path engages
        var options = new ExportExecutionOptions
        {
            BatchSize = 2,
            MaxParallelism = 2
        };

        // Act
        var result = await Jim.ExportExecution.ExecuteExportsAsync(
            targetSystem,
            mockConnector.Object,
            SyncRunMode.PreviewAndSync,
            options,
            CancellationToken.None,
            progressCallback,
            connectorFactory: connectorFactory,
            repositoryFactory: repositoryFactory);

        // Assert - all exports accounted for, and the deferred parallel phase reported CUMULATIVE
        // progress: the highest Executing-phase report must cover immediate + deferred, not reset
        // to a phase-local count.
        Assert.That(result.SuccessCount, Is.EqualTo(9), "All 9 exports (3 immediate + 6 deferred) should succeed");

        List<ExportProgressInfo> executingReports;
        lock (progressReports)
        {
            executingReports = progressReports.Where(p => p.Phase == ExportPhase.Executing).ToList();
        }
        Assert.That(executingReports, Is.Not.Empty, "Expected Executing-phase progress reports");
        Assert.That(executingReports.Max(p => p.ProcessedExports), Is.EqualTo(9),
            "The deferred parallel phase must report cumulative processed exports (immediate + deferred), not a phase-local count");
        Assert.That(executingReports.Where(p => p.ProcessedExports > p.TotalExports), Is.Empty,
            "Progress must never exceed the total");
    }

    /// <summary>
    /// Every per-batch repository scope created by the deferred parallel path must be disposed by
    /// the time the export completes. Before scopes were disposed, each batch's JimApplication and
    /// DbContext lived until process exit and pinned one pooled connection apiece, exhausting the
    /// Npgsql pool (Max Pool Size 30) at scale: the Scale200k10kGroups export failed from batch 29
    /// onwards with "The connection pool has been exhausted" (2026-07-13).
    /// </summary>
    [Test]
    public async Task ExecuteExportsAsync_ParallelDeferredExports_DisposesPerBatchRepositoryScopesAsync()
    {
        // Arrange - same shape as the cumulative progress test above: 6 deferred exports over
        // 3 batches so the parallel path engages and creates per-batch scopes
        var targetSystem = ConnectedSystemsData.Single(s => s.Name == "Dummy Target System");
        var targetUserType = ConnectedSystemObjectTypesData.Single(t => t.Name == "TARGET_USER");
        var displayNameAttr = targetUserType.Attributes.Single(a => a.Name == MockTargetSystemAttributeNames.DisplayName.ToString());
        var managerAttr = targetUserType.Attributes.Single(a => a.Name == MockTargetSystemAttributeNames.Manager.ToString());
        var objectGuidAttr = targetUserType.Attributes.Single(a => a.Name == MockTargetSystemAttributeNames.ObjectGuid.ToString());

        var referencedMvoIds = new List<Guid>();
        for (var i = 0; i < 6; i++)
        {
            var mvoId = Guid.NewGuid();
            referencedMvoIds.Add(mvoId);
            SyncRepo.SeedMetaverseObject(new MetaverseObject { Id = mvoId });

            var targetCso = new ConnectedSystemObject
            {
                Id = Guid.NewGuid(),
                ConnectedSystemId = targetSystem.Id,
                Type = targetUserType,
                TypeId = targetUserType.Id,
                MetaverseObjectId = mvoId,
                AttributeValues = new List<ConnectedSystemObjectAttributeValue>
                {
                    new()
                    {
                        Id = Guid.NewGuid(),
                        Attribute = objectGuidAttr,
                        AttributeId = objectGuidAttr.Id,
                        GuidValue = Guid.NewGuid()
                    }
                }
            };
            SyncRepo.SeedConnectedSystemObject(targetCso);
        }

        for (var i = 0; i < 6; i++)
        {
            var cso = new ConnectedSystemObject
            {
                Id = Guid.NewGuid(),
                ConnectedSystemId = targetSystem.Id,
                Type = targetUserType,
                TypeId = targetUserType.Id,
                AttributeValues = new List<ConnectedSystemObjectAttributeValue>()
            };
            ConnectedSystemObjectsData.Add(cso);
            SyncRepo.SeedConnectedSystemObject(cso);

            var pe = new PendingExport
            {
                Id = Guid.NewGuid(),
                ConnectedSystemId = targetSystem.Id,
                ConnectedSystem = targetSystem,
                ConnectedSystemObject = cso,
                ConnectedSystemObjectId = cso.Id,
                Status = PendingExportStatus.Pending,
                ChangeType = PendingExportChangeType.Update,
                HasUnresolvedReferences = true,
                CreatedAt = DateTime.UtcNow.AddMinutes(i),
                AttributeValueChanges = new List<PendingExportAttributeValueChange>
                {
                    new()
                    {
                        Id = Guid.NewGuid(),
                        ChangeType = PendingExportAttributeChangeType.Update,
                        AttributeId = displayNameAttr.Id,
                        Attribute = displayNameAttr,
                        StringValue = $"Deferred User {i}",
                        Status = PendingExportAttributeChangeStatus.Pending
                    },
                    new()
                    {
                        Id = Guid.NewGuid(),
                        ChangeType = PendingExportAttributeChangeType.Update,
                        AttributeId = managerAttr.Id,
                        Attribute = managerAttr,
                        UnresolvedReferenceValue = referencedMvoIds[i].ToString(),
                        Status = PendingExportAttributeChangeStatus.Pending
                    }
                }
            };
            PendingExportsData.Add(pe);
            SyncRepo.SeedPendingExport(pe);
        }

        var mockConnector = new Mock<IConnector>();
        var mockExportConnector = mockConnector.As<IConnectorExportUsingCalls>();
        mockConnector.Setup(c => c.Name).Returns("Test Connector");
        mockExportConnector.Setup(c => c.ExportAsync(It.IsAny<IList<PendingExport>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IList<PendingExport> exports, CancellationToken _) =>
                exports.Select(_ => ConnectedSystemExportResult.Succeeded()).ToList());

        Func<IConnector> connectorFactory = () =>
        {
            var factoryConnector = new Mock<IConnector>();
            var factoryExportConnector = factoryConnector.As<IConnectorExportUsingCalls>();
            factoryConnector.Setup(c => c.Name).Returns("Test Connector");
            factoryExportConnector.Setup(c => c.ExportAsync(It.IsAny<IList<PendingExport>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((IList<PendingExport> exports, CancellationToken _) =>
                    exports.Select(_ => ConnectedSystemExportResult.Succeeded()).ToList());
            return factoryConnector.Object;
        };

        var scopesCreated = 0;
        var scopesDisposed = 0;
        Func<JIM.Data.Repositories.ISyncRepositoryScope> repositoryFactory = () =>
        {
            Interlocked.Increment(ref scopesCreated);
            return new JIM.Data.Repositories.SyncRepositoryScope(
                TestUtilities.CreateSyncRepository(pendingExports: PendingExportsData),
                new DisposalCounter(() => Interlocked.Increment(ref scopesDisposed)));
        };

        var options = new ExportExecutionOptions
        {
            BatchSize = 2,
            MaxParallelism = 2
        };

        // Act
        var result = await Jim.ExportExecution.ExecuteExportsAsync(
            targetSystem,
            mockConnector.Object,
            SyncRunMode.PreviewAndSync,
            options,
            CancellationToken.None,
            connectorFactory: connectorFactory,
            repositoryFactory: repositoryFactory);

        // Assert - every created scope must be disposed
        Assert.That(result.SuccessCount, Is.EqualTo(6), "All 6 deferred exports should succeed");
        Assert.That(scopesCreated, Is.GreaterThan(0), "The deferred parallel path should create per-batch repository scopes");
        Assert.That(scopesDisposed, Is.EqualTo(scopesCreated),
            "Every per-batch repository scope must be disposed when its batch completes; an undisposed scope pins a pooled database connection for the process lifetime");
    }

    private sealed class DisposalCounter(Action onDispose) : IDisposable
    {
        public void Dispose() => onDispose();
    }

    #endregion

    #region Exception Handling (RPEI Blind Spot Fixes)

    /// <summary>
    /// Tests that when a file-based connector throws an exception during ExportAsync,
    /// ProcessedExportItems are created so RPEIs can be generated. Previously, the catch block
    /// in ExecuteUsingFilesWithBatchingAsync set result.FailedCount but did not create
    /// ProcessedExportItems, causing the activity to show "100 failed" with Status: Complete
    /// and no error RPEIs.
    /// </summary>
    [Test]
    public async Task ExecuteExportsAsync_FileConnectorThrows_CreatesProcessedExportItemsAsync()
    {
        // Arrange
        var targetSystem = ConnectedSystemsData.Single(s => s.Name == "Dummy Target System");
        var targetUserType = ConnectedSystemObjectTypesData.Single(t => t.Name == "TARGET_USER");
        var mvo = MetaverseObjectsData[0];
        var displayNameAttr = targetUserType.Attributes.Single(a => a.Name == MockTargetSystemAttributeNames.DisplayName.ToString());

        // Create a CSO
        var cso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = targetSystem.Id,
            ConnectedSystem = targetSystem,
            Type = targetUserType,
            TypeId = targetUserType.Id,
            MetaverseObject = mvo,
            MetaverseObjectId = mvo.Id,
            JoinType = ConnectedSystemObjectJoinType.Provisioned,
            Status = ConnectedSystemObjectStatus.Normal,
            DateJoined = DateTime.UtcNow,
            AttributeValues = new List<ConnectedSystemObjectAttributeValue>()
        };
        ConnectedSystemObjectsData.Add(cso);

        // Create Pending Exports
        var pendingExport1 = new PendingExport
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = targetSystem.Id,
            ConnectedSystem = targetSystem,
            ConnectedSystemObject = cso,
            ConnectedSystemObjectId = cso.Id,
            SourceMetaverseObjectId = mvo.Id,
            Status = PendingExportStatus.Pending,
            ChangeType = PendingExportChangeType.Create,
            CreatedAt = DateTime.UtcNow,
            ErrorCount = 0,
            MaxRetries = 3,
            AttributeValueChanges = new List<PendingExportAttributeValueChange>
            {
                new()
                {
                    Id = Guid.NewGuid(),
                    ChangeType = PendingExportAttributeChangeType.Add,
                    AttributeId = displayNameAttr.Id,
                    Attribute = displayNameAttr,
                    StringValue = "Test User"
                }
            }
        };

        var pendingExport2 = new PendingExport
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = targetSystem.Id,
            ConnectedSystem = targetSystem,
            ConnectedSystemObject = cso,
            ConnectedSystemObjectId = cso.Id,
            SourceMetaverseObjectId = mvo.Id,
            Status = PendingExportStatus.Pending,
            ChangeType = PendingExportChangeType.Update,
            CreatedAt = DateTime.UtcNow,
            ErrorCount = 0,
            MaxRetries = 3,
            AttributeValueChanges = new List<PendingExportAttributeValueChange>
            {
                new()
                {
                    Id = Guid.NewGuid(),
                    ChangeType = PendingExportAttributeChangeType.Update,
                    AttributeId = displayNameAttr.Id,
                    Attribute = displayNameAttr,
                    StringValue = "Updated Name"
                }
            }
        };
        PendingExportsData.Add(pendingExport1);
        PendingExportsData.Add(pendingExport2);
        SyncRepo.SeedPendingExport(pendingExport1);
        SyncRepo.SeedPendingExport(pendingExport2);

        // Mock a file-based connector that throws during ExportAsync
        var mockConnector = new Mock<IConnector>();
        var mockFileConnector = mockConnector.As<IConnectorExportUsingFiles>();
        mockConnector.Setup(c => c.Name).Returns("Test File Connector");
        mockFileConnector.Setup(c => c.ExportAsync(
                It.IsAny<IList<ConnectedSystemSettingValue>>(),
                It.IsAny<IList<PendingExport>>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("Permission denied: /connector-files/export.csv"));

        // Act
        var result = await Jim.ExportExecution.ExecuteExportsAsync(
            targetSystem,
            mockConnector.Object,
            SyncRunMode.PreviewAndSync);

        // Assert - FailedCount should reflect all Pending Exports
        Assert.That(result.FailedCount, Is.EqualTo(2), "Both exports should be marked as failed");
        Assert.That(result.SuccessCount, Is.EqualTo(0), "No exports should have succeeded");

        // Assert - ProcessedExportItems must be created for RPEI generation
        Assert.That(result.ProcessedExportItems, Has.Count.EqualTo(2),
            "ProcessedExportItems must be created even when connector throws, " +
            "otherwise no RPEIs are generated and the activity silently reports Status: Complete");

        // Assert - Each ProcessedExportItem must have failure information for RPEI error tracking
        foreach (var item in result.ProcessedExportItems)
        {
            Assert.That(item.Succeeded, Is.False, "Each item should be marked as not succeeded");
            Assert.That(item.ErrorMessage, Is.Not.Null.And.Not.Empty,
                "Each item must have an error message so the RPEI gets an ErrorType set");
        }
    }

    /// <summary>
    /// Tests that when a call-based connector throws a connection-level exception
    /// (e.g., LDAP connection dropped), the exception propagates to the caller so
    /// PerformExportAsync can fail the activity. Previously, the outer catch block
    /// silently swallowed the exception.
    /// </summary>
    [Test]
    public void ExecuteExportsAsync_CallConnectorThrows_RethrowsToCallerAsync()
    {
        // Arrange
        var targetSystem = ConnectedSystemsData.Single(s => s.Name == "Dummy Target System");
        var targetUserType = ConnectedSystemObjectTypesData.Single(t => t.Name == "TARGET_USER");
        var mvo = MetaverseObjectsData[0];
        var displayNameAttr = targetUserType.Attributes.Single(a => a.Name == MockTargetSystemAttributeNames.DisplayName.ToString());

        // Create a CSO
        var cso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = targetSystem.Id,
            ConnectedSystem = targetSystem,
            Type = targetUserType,
            TypeId = targetUserType.Id,
            MetaverseObject = mvo,
            MetaverseObjectId = mvo.Id,
            JoinType = ConnectedSystemObjectJoinType.Provisioned,
            Status = ConnectedSystemObjectStatus.Normal,
            DateJoined = DateTime.UtcNow,
            AttributeValues = new List<ConnectedSystemObjectAttributeValue>()
        };
        ConnectedSystemObjectsData.Add(cso);

        // Create a Pending Export
        var pendingExport = new PendingExport
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = targetSystem.Id,
            ConnectedSystem = targetSystem,
            ConnectedSystemObject = cso,
            ConnectedSystemObjectId = cso.Id,
            SourceMetaverseObjectId = mvo.Id,
            Status = PendingExportStatus.Pending,
            ChangeType = PendingExportChangeType.Update,
            CreatedAt = DateTime.UtcNow,
            ErrorCount = 0,
            MaxRetries = 3,
            AttributeValueChanges = new List<PendingExportAttributeValueChange>
            {
                new()
                {
                    Id = Guid.NewGuid(),
                    ChangeType = PendingExportAttributeChangeType.Update,
                    AttributeId = displayNameAttr.Id,
                    Attribute = displayNameAttr,
                    StringValue = "Updated Name"
                }
            }
        };
        PendingExportsData.Add(pendingExport);
        SyncRepo.SeedPendingExport(pendingExport);

        // Mock a call-based connector that throws during OpenExportConnection
        var mockConnector = new Mock<IConnector>();
        var mockExportConnector = mockConnector.As<IConnectorExportUsingCalls>();
        mockConnector.Setup(c => c.Name).Returns("Test Failing Connector");
        mockExportConnector.Setup(c => c.OpenExportConnection(It.IsAny<IList<ConnectedSystemSettingValue>>()))
            .Throws(new InvalidOperationException("Connection refused"));

        // Act & Assert - Exception must propagate so PerformExportAsync can fail the activity
        Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await Jim.ExportExecution.ExecuteExportsAsync(
                targetSystem,
                mockConnector.Object,
                SyncRunMode.PreviewAndSync);
        });
    }

    #endregion

    #region Reference Resolution Transient Stamp (issue #1079)

    /// <summary>
    /// Issue #1079 (optimistic export apply): when a deferred export's Reference attribute change
    /// is resolved via TryResolveReferencesFromLookup, the referenced CSO is in hand at that
    /// moment. The transient ResolvedReferenceCsoId hint must be stamped onto the attribute change
    /// so optimistic apply can populate ConnectedSystemObjectAttributeValue.ReferenceValueId
    /// without a further database round-trip.
    /// </summary>
    [Test]
    public async Task ExecuteExportsAsync_DeferredReferenceResolves_StampsResolvedReferenceCsoIdAsync()
    {
        // Arrange
        var targetSystem = ConnectedSystemsData.Single(s => s.Name == "Dummy Target System");
        var targetUserType = ConnectedSystemObjectTypesData.Single(t => t.Name == "TARGET_USER");
        var managerAttr = targetUserType.Attributes.Single(a => a.Name == MockTargetSystemAttributeNames.Manager.ToString());
        var objectGuidAttr = targetUserType.Attributes.Single(a => a.Name == MockTargetSystemAttributeNames.ObjectGuid.ToString());

        // The referenced CSO (the "manager") that the deferred reference resolves to.
        var managerMvoId = Guid.NewGuid();
        SyncRepo.SeedMetaverseObject(new MetaverseObject { Id = managerMvoId });
        var managerCso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = targetSystem.Id,
            Type = targetUserType,
            TypeId = targetUserType.Id,
            MetaverseObjectId = managerMvoId,
            AttributeValues = new List<ConnectedSystemObjectAttributeValue>
            {
                new()
                {
                    Id = Guid.NewGuid(),
                    Attribute = objectGuidAttr,
                    AttributeId = objectGuidAttr.Id,
                    GuidValue = Guid.NewGuid()
                }
            }
        };
        SyncRepo.SeedConnectedSystemObject(managerCso);

        // The CSO with the deferred (unresolved) reference to the manager.
        var cso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = targetSystem.Id,
            Type = targetUserType,
            TypeId = targetUserType.Id,
            AttributeValues = new List<ConnectedSystemObjectAttributeValue>()
        };
        ConnectedSystemObjectsData.Add(cso);
        SyncRepo.SeedConnectedSystemObject(cso);

        var managerChange = new PendingExportAttributeValueChange
        {
            Id = Guid.NewGuid(),
            ChangeType = PendingExportAttributeChangeType.Update,
            AttributeId = managerAttr.Id,
            Attribute = managerAttr,
            UnresolvedReferenceValue = managerMvoId.ToString(),
            Status = PendingExportAttributeChangeStatus.Pending
        };
        var pendingExport = new PendingExport
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = targetSystem.Id,
            ConnectedSystem = targetSystem,
            ConnectedSystemObject = cso,
            ConnectedSystemObjectId = cso.Id,
            Status = PendingExportStatus.Pending,
            ChangeType = PendingExportChangeType.Update,
            HasUnresolvedReferences = true,
            CreatedAt = DateTime.UtcNow,
            AttributeValueChanges = new List<PendingExportAttributeValueChange> { managerChange }
        };
        PendingExportsData.Add(pendingExport);
        SyncRepo.SeedPendingExport(pendingExport);

        var mockConnector = new Mock<IConnector>();
        var mockExportConnector = mockConnector.As<IConnectorExportUsingCalls>();
        mockConnector.Setup(c => c.Name).Returns("Test Connector");
        mockExportConnector.Setup(c => c.ExportAsync(It.IsAny<IList<PendingExport>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IList<PendingExport> exports, CancellationToken _) =>
                exports.Select(_ => ConnectedSystemExportResult.Succeeded()).ToList());

        // Act
        await Jim.ExportExecution.ExecuteExportsAsync(
            targetSystem,
            mockConnector.Object,
            SyncRunMode.PreviewAndSync);

        // Assert
        Assert.That(managerChange.ResolvedReferenceCsoId, Is.EqualTo(managerCso.Id),
            "Resolving a deferred reference must stamp the transient ResolvedReferenceCsoId hint " +
            "with the referenced CSO's Id so optimistic apply can use it without a further lookup.");
    }

    #endregion

    #region Optimistic Export Apply (issue #1079)

    /// <summary>
    /// Issue #1079: on export success, the exported attribute values are applied to the CSO's
    /// in-memory AttributeValues (D10), so the confirming import's diff finds them already present.
    /// </summary>
    [Test]
    public async Task ExecuteExportsAsync_SuccessfulUpdateExport_AppliesValuesToInMemoryCsoAsync()
    {
        // Arrange
        var targetSystem = ConnectedSystemsData.Single(s => s.Name == "Dummy Target System");
        var targetUserType = ConnectedSystemObjectTypesData.Single(t => t.Name == "TARGET_USER");
        var displayNameAttr = targetUserType.Attributes.Single(a => a.Name == MockTargetSystemAttributeNames.DisplayName.ToString());

        var cso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = targetSystem.Id,
            Type = targetUserType,
            TypeId = targetUserType.Id,
            Status = ConnectedSystemObjectStatus.Normal,
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
                    StringValue = "Applied Name",
                    Status = PendingExportAttributeChangeStatus.Pending
                }
            }
        };
        PendingExportsData.Add(pendingExport);
        SyncRepo.SeedPendingExport(pendingExport);

        var mockConnector = CreateSucceedingCallsConnector();

        // Act
        var result = await Jim.ExportExecution.ExecuteExportsAsync(
            targetSystem,
            mockConnector.Object,
            SyncRunMode.PreviewAndSync);

        // Assert
        Assert.That(cso.AttributeValues.Any(av => av.AttributeId == displayNameAttr.Id && av.StringValue == "Applied Name"), Is.True,
            "the exported value must be applied to the CSO's in-memory AttributeValues");
        Assert.That(result.OptimisticApplyAppliedCount, Is.EqualTo(1));
        Assert.That(cso.LastUpdated, Is.Null, "optimistic apply must never stamp LastUpdated (D2)");
        Assert.That(cso.Status, Is.EqualTo(ConnectedSystemObjectStatus.Normal), "optimistic apply must never touch parent CSO fields (D2)");
    }

    /// <summary>
    /// Issue #1079 (D7): a failure during optimistic apply must be swallowed, logged, and must
    /// never fail the batch, the Pending Export updates, or the Activity - the export itself
    /// already succeeded against the Connected System.
    /// </summary>
    [Test]
    public async Task ExecuteExportsAsync_OptimisticApplyThrows_SwallowsFailureAndDoesNotFailBatchAsync()
    {
        // Arrange
        var throwingRepo = new ThrowingOnApplySyncRepository();
        var syncRepo = TestUtilities.CreateSyncRepository(activity: ActivitiesData.First(), repository: throwingRepo);
        using var jim = new JimApplication(new PostgresDataRepository(MockJimDbContext.Object), syncRepository: syncRepo);

        var targetSystem = ConnectedSystemsData.Single(s => s.Name == "Dummy Target System");
        var targetUserType = ConnectedSystemObjectTypesData.Single(t => t.Name == "TARGET_USER");
        var displayNameAttr = targetUserType.Attributes.Single(a => a.Name == MockTargetSystemAttributeNames.DisplayName.ToString());

        var cso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = targetSystem.Id,
            Type = targetUserType,
            TypeId = targetUserType.Id,
            AttributeValues = new List<ConnectedSystemObjectAttributeValue>()
        };
        syncRepo.SeedConnectedSystemObject(cso);

        var pendingExport = new PendingExport
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = targetSystem.Id,
            ConnectedSystem = targetSystem,
            ConnectedSystemObject = cso,
            ConnectedSystemObjectId = cso.Id,
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
                    StringValue = "Doomed Name",
                    Status = PendingExportAttributeChangeStatus.Pending
                }
            }
        };
        syncRepo.SeedPendingExport(pendingExport);

        var mockConnector = CreateSucceedingCallsConnector();

        // Act
        var result = await jim.ExportExecution.ExecuteExportsAsync(
            targetSystem,
            mockConnector.Object,
            SyncRunMode.PreviewAndSync);

        // Assert: the export itself still succeeded despite optimistic apply throwing.
        Assert.That(result.SuccessCount, Is.EqualTo(1));
        Assert.That(result.FailedCount, Is.EqualTo(0));
        Assert.That(pendingExport.Status, Is.EqualTo(PendingExportStatus.Exported));
        Assert.That(result.OptimisticApplyFailedCount, Is.EqualTo(1));
        Assert.That(cso.AttributeValues, Is.Empty, "a failed apply must leave the CSO untouched, not partially applied");
    }

    /// <summary>
    /// Issue #1079 (D6): Delete-ChangeType Pending Exports are skipped entirely by optimistic
    /// apply; the CSO obsolete/delete lifecycle owns that path.
    /// </summary>
    [Test]
    public async Task ExecuteExportsAsync_DeleteChangeTypeExport_DoesNotApplyAsync()
    {
        // Arrange
        var targetSystem = ConnectedSystemsData.Single(s => s.Name == "Dummy Target System");
        var targetUserType = ConnectedSystemObjectTypesData.Single(t => t.Name == "TARGET_USER");

        var cso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = targetSystem.Id,
            Type = targetUserType,
            TypeId = targetUserType.Id,
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
            ChangeType = PendingExportChangeType.Delete,
            CreatedAt = DateTime.UtcNow,
            AttributeValueChanges = new List<PendingExportAttributeValueChange>()
        };
        PendingExportsData.Add(pendingExport);
        SyncRepo.SeedPendingExport(pendingExport);

        var mockConnector = CreateSucceedingCallsConnector();

        // Act
        var result = await Jim.ExportExecution.ExecuteExportsAsync(
            targetSystem,
            mockConnector.Object,
            SyncRunMode.PreviewAndSync);

        // Assert
        Assert.That(result.SuccessCount, Is.EqualTo(1));
        Assert.That(result.OptimisticApplyAppliedCount, Is.EqualTo(0));
        Assert.That(result.OptimisticApplySkippedCount, Is.EqualTo(1));
        Assert.That(cso.AttributeValues, Is.Empty);
    }

    /// <summary>
    /// Issue #1079: Preview mode short circuits before execution, so optimistic apply must never run.
    /// </summary>
    [Test]
    public async Task ExecuteExportsAsync_PreviewOnlyMode_DoesNotApplyAsync()
    {
        // Arrange
        var targetSystem = ConnectedSystemsData.Single(s => s.Name == "Dummy Target System");
        var targetUserType = ConnectedSystemObjectTypesData.Single(t => t.Name == "TARGET_USER");
        var displayNameAttr = targetUserType.Attributes.Single(a => a.Name == MockTargetSystemAttributeNames.DisplayName.ToString());

        var cso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = targetSystem.Id,
            Type = targetUserType,
            TypeId = targetUserType.Id,
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
                    StringValue = "Should Not Apply",
                    Status = PendingExportAttributeChangeStatus.Pending
                }
            }
        };
        PendingExportsData.Add(pendingExport);
        SyncRepo.SeedPendingExport(pendingExport);

        var mockConnector = new Mock<IConnector>();
        mockConnector.Setup(c => c.Name).Returns("Test Connector");

        // Act
        var result = await Jim.ExportExecution.ExecuteExportsAsync(
            targetSystem,
            mockConnector.Object,
            SyncRunMode.PreviewOnly);

        // Assert
        Assert.That(cso.AttributeValues, Is.Empty);
        Assert.That(result.OptimisticApplyAppliedCount, Is.EqualTo(0));
    }

    /// <summary>
    /// Repository that throws when optimistic apply tries to persist the delta, to prove D7's
    /// failure-containment guarantee (the batch, Pending Export updates, and Activity must not fail).
    /// </summary>
    private sealed class ThrowingOnApplySyncRepository : SyncRepository
    {
        public override Task ApplyExportedAttributeValuesAsync(
            List<ConnectedSystemObjectAttributeValue> additions, List<Guid> removalValueIds)
        {
            throw new InvalidOperationException("Simulated optimistic export apply failure");
        }
    }

    #endregion

    #region batch scan efficiency (issue #985)

    /// <summary>
    /// Spy repository that counts how many times the export batch-collection loop hits the
    /// database. Deferred (reference-bearing) exports stay Pending in the database for the
    /// whole collection loop, so a scan that restarts from the beginning for every batch
    /// degrades to O(n²) page loads at scale (issue #985).
    /// </summary>
    private sealed class BatchLoadCountingSyncRepository : SyncRepository
    {
        public int BatchLoadCalls;
        public int RemainingDeferredCalls;
        public int ExecutableProbeCalls;

        public override Task<List<PendingExport>> GetExecutableExportBatchAsync(int connectedSystemId, int take, DateTime? afterCreatedAt, Guid? afterId)
        {
            Interlocked.Increment(ref BatchLoadCalls);
            return base.GetExecutableExportBatchAsync(connectedSystemId, take, afterCreatedAt, afterId);
        }

        public override Task<List<PendingExport>> GetRemainingDeferredExportsAsync(int connectedSystemId, DateTime? afterCreatedAt, Guid? afterId)
        {
            Interlocked.Increment(ref RemainingDeferredCalls);
            return base.GetRemainingDeferredExportsAsync(connectedSystemId, afterCreatedAt, afterId);
        }

        public override Task<bool> AnyExecutableNonDeferredExportsAfterAsync(int connectedSystemId, DateTime? afterCreatedAt, Guid? afterId)
        {
            Interlocked.Increment(ref ExecutableProbeCalls);
            return base.AnyExecutableNonDeferredExportsAfterAsync(connectedSystemId, afterCreatedAt, afterId);
        }
    }

    /// <summary>
    /// Simulates database persistence isolation for the parallel export batch path: the real
    /// ProcessBatchesInParallelAsync re-loads each batch's Pending Exports by ID on a FRESH
    /// per-batch DbContext, which only sees state that has been persisted. The plain in-memory
    /// repository returns live object references, so in-memory mutations (such as reference
    /// resolution) are visible to "re-loads" even when nothing was persisted; that masks exactly
    /// the class of bug this fake exists to expose. Here, GetPendingExportsByIdsAsync returns
    /// deep clones of the last-PERSISTED state, and only UpdatePendingExportsAsync (the persist
    /// event) refreshes that state.
    /// </summary>
    private sealed class PersistenceIsolatingSyncRepository : SyncRepository
    {
        private readonly Dictionary<Guid, PendingExport> _persistedState = new();
        private readonly object _persistLock = new();

        /// <summary>
        /// Seeds a Pending Export into the store AND snapshots it as the persisted (committed)
        /// state, as a real database row would be after the sync run that created it.
        /// </summary>
        public void SeedPendingExportAsPersisted(PendingExport pendingExport)
        {
            SeedPendingExport(pendingExport);
            lock (_persistLock)
                _persistedState[pendingExport.Id] = ClonePendingExport(pendingExport);
        }

        public override Task UpdatePendingExportsAsync(IEnumerable<PendingExport> pendingExports)
        {
            var list = pendingExports.ToList();
            lock (_persistLock)
            {
                foreach (var pe in list)
                    _persistedState[pe.Id] = ClonePendingExport(pe);
            }
            return base.UpdatePendingExportsAsync(list);
        }

        public override Task<List<PendingExport>> GetPendingExportsByIdsAsync(IList<Guid> pendingExportIds)
        {
            lock (_persistLock)
            {
                var result = pendingExportIds
                    .Where(id => _persistedState.ContainsKey(id))
                    .Select(id => ClonePendingExport(_persistedState[id]))
                    .ToList();
                return Task.FromResult(result);
            }
        }

        private static PendingExport ClonePendingExport(PendingExport source)
        {
            return new PendingExport
            {
                Id = source.Id,
                ConnectedSystemId = source.ConnectedSystemId,
                ConnectedSystem = source.ConnectedSystem,
                ConnectedSystemObject = source.ConnectedSystemObject,
                ConnectedSystemObjectId = source.ConnectedSystemObjectId,
                SourceMetaverseObjectId = source.SourceMetaverseObjectId,
                Status = source.Status,
                ChangeType = source.ChangeType,
                CreatedAt = source.CreatedAt,
                HasUnresolvedReferences = source.HasUnresolvedReferences,
                MaxRetries = source.MaxRetries,
                ErrorCount = source.ErrorCount,
                NextRetryAt = source.NextRetryAt,
                LastAttemptedAt = source.LastAttemptedAt,
                AttributeValueChanges = source.AttributeValueChanges.Select(avc => new PendingExportAttributeValueChange
                {
                    Id = avc.Id,
                    PendingExportId = avc.PendingExportId,
                    AttributeId = avc.AttributeId,
                    Attribute = avc.Attribute,
                    ChangeType = avc.ChangeType,
                    Status = avc.Status,
                    StringValue = avc.StringValue,
                    UnresolvedReferenceValue = avc.UnresolvedReferenceValue,
                    GuidValue = avc.GuidValue,
                    IntValue = avc.IntValue,
                    ExportAttemptCount = avc.ExportAttemptCount
                }).ToList()
            };
        }
    }

    private static Mock<IConnector> CreateSucceedingCallsConnector()
    {
        var mockConnector = new Mock<IConnector>();
        var mockExportConnector = mockConnector.As<IConnectorExportUsingCalls>();
        mockConnector.Setup(c => c.Name).Returns("Test Connector");
        mockExportConnector.Setup(c => c.ExportAsync(It.IsAny<IList<PendingExport>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IList<PendingExport> exports, CancellationToken _) =>
                exports.Select(_ => ConnectedSystemExportResult.Succeeded()).ToList());
        return mockConnector;
    }

    private PendingExport CreateSeededCreateExport(ConnectedSystem targetSystem, ConnectedSystemObjectType type,
        DateTime createdAt, bool hasUnresolvedReferences)
    {
        var cso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = targetSystem.Id,
            Type = type,
            TypeId = type.Id
        };
        ConnectedSystemObjectsData.Add(cso);

        var pendingExport = new PendingExport
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = targetSystem.Id,
            ConnectedSystem = targetSystem,
            ConnectedSystemObject = cso,
            ConnectedSystemObjectId = cso.Id,
            Status = PendingExportStatus.Pending,
            ChangeType = PendingExportChangeType.Create,
            CreatedAt = createdAt,
            HasUnresolvedReferences = hasUnresolvedReferences,
            MaxRetries = 3,
            AttributeValueChanges = new List<PendingExportAttributeValueChange>()
        };
        PendingExportsData.Add(pendingExport);
        return pendingExport;
    }

    /// <summary>
    /// Issue #985: with N deferred (reference-bearing) exports and batch size B, batch
    /// collection must be a single forward sweep over the query (ceil(N/B) pages plus one
    /// exhaustion probe), not a restart-from-zero rescan per batch, which costs O((N/B)²)
    /// page loads and starved the connector for hours at 200K scale.
    /// </summary>
    [Test]
    public async Task ExecuteExportsAsync_DeferredExports_BatchCollectionIsSinglePassAsync()
    {
        // Arrange: a fresh application wired to a counting repository.
        var countingRepo = new BatchLoadCountingSyncRepository();
        var syncRepo = TestUtilities.CreateSyncRepository(activity: ActivitiesData.First(), repository: countingRepo);
        using var jim = new JimApplication(new PostgresDataRepository(MockJimDbContext.Object), syncRepository: syncRepo);

        var targetSystem = ConnectedSystemsData.Single(s => s.Name == "Dummy Target System");
        var targetUserType = ConnectedSystemObjectTypesData.Single(t => t.Name == "TARGET_USER");

        const int exportCount = 250;
        const int batchSize = 100;
        var baseTime = DateTime.UtcNow.AddMinutes(-10);
        for (var i = 0; i < exportCount; i++)
        {
            var pe = CreateSeededCreateExport(targetSystem, targetUserType,
                baseTime.AddMilliseconds(i), hasUnresolvedReferences: true);
            countingRepo.SeedPendingExport(pe);
        }

        var mockConnector = CreateSucceedingCallsConnector();
        var options = new ExportExecutionOptions { BatchSize = batchSize };

        // Act
        var result = await jim.ExportExecution.ExecuteExportsAsync(
            targetSystem,
            mockConnector.Object,
            SyncRunMode.PreviewAndSync,
            options,
            CancellationToken.None);

        // Assert: every export was collected...
        Assert.That(result.ProcessedPendingExportIds, Has.Count.EqualTo(exportCount));

        // ...in a single forward sweep: 3 pages of 100 + 1 exhaustion probe.
        const int maxExpectedBatchLoads = exportCount / batchSize + 2;
        Assert.That(countingRepo.BatchLoadCalls, Is.LessThanOrEqualTo(maxExpectedBatchLoads),
            $"Batch collection re-scanned the Pending Export query: {countingRepo.BatchLoadCalls} page loads " +
            $"for {exportCount} deferred exports at batch size {batchSize} (expected <= {maxExpectedBatchLoads}). " +
            "See issue #985.");
    }

    /// <summary>
    /// Issue #985 (c): once a loaded batch is discovered to contain only deferred
    /// (reference-bearing) exports and nothing executable, the collection loop must stop
    /// page-by-page scanning and collect all remaining deferred exports with a single bulk
    /// repository call, rather than continuing to page 100 at a time purely to build the
    /// deferred list. With N=1000 deferred exports and batch size 100, the first page already
    /// reveals the batch is entirely deferred, so at most one further page load plus exactly
    /// one bulk collect call are needed (previously this cost ceil(N/B)+1 = 11 page loads).
    /// </summary>
    [Test]
    public async Task ExecuteExportsAsync_AllDeferredExports_FastPathsRemainingCollectionInSingleBulkCallAsync()
    {
        // Arrange: a fresh application wired to a counting repository.
        var countingRepo = new BatchLoadCountingSyncRepository();
        var syncRepo = TestUtilities.CreateSyncRepository(activity: ActivitiesData.First(), repository: countingRepo);
        using var jim = new JimApplication(new PostgresDataRepository(MockJimDbContext.Object), syncRepository: syncRepo);

        var targetSystem = ConnectedSystemsData.Single(s => s.Name == "Dummy Target System");
        var targetUserType = ConnectedSystemObjectTypesData.Single(t => t.Name == "TARGET_USER");

        const int exportCount = 1000;
        const int batchSize = 100;
        var baseTime = DateTime.UtcNow.AddMinutes(-10);
        for (var i = 0; i < exportCount; i++)
        {
            var pe = CreateSeededCreateExport(targetSystem, targetUserType,
                baseTime.AddMilliseconds(i), hasUnresolvedReferences: true);
            countingRepo.SeedPendingExport(pe);
        }

        var mockConnector = CreateSucceedingCallsConnector();
        var options = new ExportExecutionOptions { BatchSize = batchSize };

        // Act
        var result = await jim.ExportExecution.ExecuteExportsAsync(
            targetSystem,
            mockConnector.Object,
            SyncRunMode.PreviewAndSync,
            options,
            CancellationToken.None);

        // Assert: every export was still collected...
        Assert.That(result.ProcessedPendingExportIds, Has.Count.EqualTo(exportCount));

        // ...via at most 2 page loads plus exactly one bulk "collect the rest" call, not
        // ceil(N/B) = 10 page loads (plus an exhaustion probe).
        Assert.That(countingRepo.BatchLoadCalls, Is.LessThanOrEqualTo(2),
            $"Batch collection paged through deferred exports instead of fast-pathing: " +
            $"{countingRepo.BatchLoadCalls} page loads for {exportCount} deferred exports at batch size {batchSize}. " +
            "See issue #985 (c).");
        Assert.That(countingRepo.RemainingDeferredCalls, Is.EqualTo(1),
            "Expected exactly one bulk GetRemainingDeferredExportsAsync call to collect the deferred tail.");
        Assert.That(countingRepo.ExecutableProbeCalls, Is.EqualTo(1),
            "Expected exactly one executable-exports existence probe before the fast path fired.");
    }

    /// <summary>
    /// Issue #985 (c): the fast path must only trigger for a batch that is entirely deferred.
    /// A batch mixing executable and deferred exports must execute the executable ones exactly
    /// as before (no fast path), while a later batch that is entirely deferred should still
    /// trigger the fast path for the remainder.
    /// </summary>
    [Test]
    public async Task ExecuteExportsAsync_MixedThenAllDeferredBatch_ExecutesImmediateNormallyAndFastPathsRestAsync()
    {
        // Arrange
        var countingRepo = new BatchLoadCountingSyncRepository();
        var syncRepo = TestUtilities.CreateSyncRepository(activity: ActivitiesData.First(), repository: countingRepo);
        using var jim = new JimApplication(new PostgresDataRepository(MockJimDbContext.Object), syncRepository: syncRepo);

        var targetSystem = ConnectedSystemsData.Single(s => s.Name == "Dummy Target System");
        var targetUserType = ConnectedSystemObjectTypesData.Single(t => t.Name == "TARGET_USER");

        const int batchSize = 4;
        var baseTime = DateTime.UtcNow.AddMinutes(-10);
        var offset = 0;

        // Page 1 (4 rows): interleaved immediate/deferred; must NOT fast-path.
        var page1Types = new[] { false, true, false, true };
        foreach (var deferred in page1Types)
        {
            var pe = CreateSeededCreateExport(targetSystem, targetUserType,
                baseTime.AddMilliseconds(offset++), hasUnresolvedReferences: deferred);
            countingRepo.SeedPendingExport(pe);
        }

        // Page 2 (4 rows): entirely deferred; must fast-path and bulk-collect the rest.
        for (var i = 0; i < 4; i++)
        {
            var pe = CreateSeededCreateExport(targetSystem, targetUserType,
                baseTime.AddMilliseconds(offset++), hasUnresolvedReferences: true);
            countingRepo.SeedPendingExport(pe);
        }

        // Remaining tail (4 rows): all deferred, collected via the bulk call, not further pages.
        for (var i = 0; i < 4; i++)
        {
            var pe = CreateSeededCreateExport(targetSystem, targetUserType,
                baseTime.AddMilliseconds(offset++), hasUnresolvedReferences: true);
            countingRepo.SeedPendingExport(pe);
        }

        const int exportCount = 12;
        var mockConnector = CreateSucceedingCallsConnector();
        var options = new ExportExecutionOptions { BatchSize = batchSize };

        // Act
        var result = await jim.ExportExecution.ExecuteExportsAsync(
            targetSystem,
            mockConnector.Object,
            SyncRunMode.PreviewAndSync,
            options,
            CancellationToken.None);

        // Assert: every export accounted for and (trivially, since none reference real MVOs)
        // successfully exported, exactly as the pre-#985(c) page-by-page loop would produce.
        Assert.That(result.ProcessedPendingExportIds, Has.Count.EqualTo(exportCount));
        Assert.That(result.SuccessCount, Is.EqualTo(exportCount));

        // Only the 2 pages that were actually loaded; no further page loads once the
        // wholly-deferred second page triggered the fast path.
        Assert.That(countingRepo.BatchLoadCalls, Is.EqualTo(2),
            $"Expected exactly 2 page loads (mixed page 1, all-deferred page 2); got {countingRepo.BatchLoadCalls}.");
        Assert.That(countingRepo.RemainingDeferredCalls, Is.EqualTo(1),
            "Expected exactly one bulk GetRemainingDeferredExportsAsync call once page 2 was found to be entirely deferred.");
        Assert.That(countingRepo.ExecutableProbeCalls, Is.EqualTo(1),
            "Expected exactly one executable-exports existence probe (for the all-deferred page 2; " +
            "the mixed page 1 must not probe).");
    }

    /// <summary>
    /// Issue #985 (c) correctness guard: deferred and executable Pending Exports interleave in
    /// (CreatedAt, Id) order, so a contiguous run of a full batch of deferred exports can be
    /// followed by later executable ones. The fast path must NOT trigger in that case; breaking
    /// out of the scan after bulk-collecting only the deferred remainder would silently skip the
    /// executable exports for the whole run, a behaviour regression versus normal paging.
    /// </summary>
    [Test]
    public async Task ExecuteExportsAsync_FullDeferredBatchFollowedByExecutableExports_ExecutesExecutableExportsAsync()
    {
        // Arrange
        var countingRepo = new BatchLoadCountingSyncRepository();
        var syncRepo = TestUtilities.CreateSyncRepository(activity: ActivitiesData.First(), repository: countingRepo);
        using var jim = new JimApplication(new PostgresDataRepository(MockJimDbContext.Object), syncRepository: syncRepo);

        var targetSystem = ConnectedSystemsData.Single(s => s.Name == "Dummy Target System");
        var targetUserType = ConnectedSystemObjectTypesData.Single(t => t.Name == "TARGET_USER");

        const int batchSize = 4;
        var baseTime = DateTime.UtcNow.AddMinutes(-10);
        var offset = 0;

        // Page 1 (exactly one full batch): entirely deferred.
        for (var i = 0; i < batchSize; i++)
        {
            var pe = CreateSeededCreateExport(targetSystem, targetUserType,
                baseTime.AddMilliseconds(offset++), hasUnresolvedReferences: true);
            countingRepo.SeedPendingExport(pe);
        }

        // Later rows: executable (non-deferred) exports created after the deferred run.
        var executableIds = new List<Guid>();
        for (var i = 0; i < 3; i++)
        {
            var pe = CreateSeededCreateExport(targetSystem, targetUserType,
                baseTime.AddMilliseconds(offset++), hasUnresolvedReferences: false);
            countingRepo.SeedPendingExport(pe);
            executableIds.Add(pe.Id);
        }

        const int exportCount = batchSize + 3;
        var mockConnector = CreateSucceedingCallsConnector();
        var options = new ExportExecutionOptions { BatchSize = batchSize };

        // Act
        var result = await jim.ExportExecution.ExecuteExportsAsync(
            targetSystem,
            mockConnector.Object,
            SyncRunMode.PreviewAndSync,
            options,
            CancellationToken.None);

        // Assert: the executable exports created after the all-deferred batch must have been
        // collected and executed in this run, exactly as page-by-page scanning would have done.
        Assert.That(result.ProcessedPendingExportIds, Is.SupersetOf(executableIds),
            "Executable exports beyond an all-deferred batch were never collected; the fast path " +
            "must not break out of the scan while executable exports remain. See issue #985 (c).");
        Assert.That(result.SuccessCount, Is.EqualTo(exportCount),
            $"All {exportCount} exports (deferred + executable) should have exported successfully in this run.");

        // The probe found executable exports beyond the cursor, so the fast path must not have
        // fired; the loop kept paging normally instead.
        Assert.That(countingRepo.RemainingDeferredCalls, Is.EqualTo(0),
            "The deferred bulk-collect must not fire while executable exports remain beyond the cursor.");
        Assert.That(countingRepo.ExecutableProbeCalls, Is.GreaterThanOrEqualTo(1),
            "Expected the all-deferred page 1 to trigger the executable-exports existence probe.");
    }

    /// <summary>
    /// Regression guard for the parallel deferred-export path: ProcessBatchesInParallelAsync
    /// re-loads each batch's Pending Exports by ID on a fresh per-batch repository/DbContext,
    /// which only sees PERSISTED state. Reference resolution happens in memory before dispatch,
    /// so the resolutions must be persisted BEFORE the parallel batches execute; otherwise each
    /// batch re-loads the stale unresolved rows and sends raw Metaverse Object identifiers to
    /// the target directory ("member: value #0 invalid per syntax" against OpenLDAP; observed
    /// 2026-07-13 when the Max Export Parallelism default first exceeded 1). The sequential path
    /// masks this by passing the in-memory objects straight to the connector.
    /// </summary>
    [Test]
    public async Task ExecuteExportsAsync_ParallelDeferredExports_ConnectorReceivesResolvedReferencesAsync()
    {
        // Arrange: an application wired to a persistence-isolating repository, so per-batch
        // re-loads behave like a real fresh DbContext (persisted state only).
        var isolatingRepo = new PersistenceIsolatingSyncRepository();
        var syncRepo = TestUtilities.CreateSyncRepository(activity: ActivitiesData.First(), repository: isolatingRepo);
        using var jim = new JimApplication(new PostgresDataRepository(MockJimDbContext.Object), syncRepository: syncRepo);

        var targetSystem = ConnectedSystemsData.Single(s => s.Name == "Dummy Target System");
        var targetUserType = ConnectedSystemObjectTypesData.Single(t => t.Name == "TARGET_USER");
        var managerAttr = targetUserType.Attributes.Single(a => a.Name == MockTargetSystemAttributeNames.Manager.ToString());
        var objectGuidAttr = targetUserType.Attributes.Single(a => a.Name == MockTargetSystemAttributeNames.ObjectGuid.ToString());

        // Referenced MVOs with target CSOs so every deferred reference resolves in this run.
        var referencedMvoIds = new List<Guid>();
        for (var i = 0; i < 6; i++)
        {
            var mvoId = Guid.NewGuid();
            referencedMvoIds.Add(mvoId);
            isolatingRepo.SeedMetaverseObject(new MetaverseObject { Id = mvoId });

            var referencedCso = new ConnectedSystemObject
            {
                Id = Guid.NewGuid(),
                ConnectedSystemId = targetSystem.Id,
                Type = targetUserType,
                TypeId = targetUserType.Id,
                MetaverseObjectId = mvoId,
                AttributeValues = new List<ConnectedSystemObjectAttributeValue>
                {
                    new()
                    {
                        Id = Guid.NewGuid(),
                        Attribute = objectGuidAttr,
                        AttributeId = objectGuidAttr.Id,
                        GuidValue = Guid.NewGuid()
                    }
                }
            };
            isolatingRepo.SeedConnectedSystemObject(referencedCso);
        }

        // Six deferred reference-bearing exports at batch size 2 = three deferred batches,
        // which is what routes execution through ProcessBatchesInParallelAsync (it requires
        // more than one batch) when MaxParallelism > 1 and both factories are supplied.
        var baseTime = DateTime.UtcNow.AddMinutes(-10);
        for (var i = 0; i < 6; i++)
        {
            var pe = CreateSeededCreateExport(targetSystem, targetUserType,
                baseTime.AddMilliseconds(i), hasUnresolvedReferences: true);
            pe.AttributeValueChanges.Add(new PendingExportAttributeValueChange
            {
                Id = Guid.NewGuid(),
                PendingExportId = pe.Id,
                ChangeType = PendingExportAttributeChangeType.Add,
                AttributeId = managerAttr.Id,
                Attribute = managerAttr,
                UnresolvedReferenceValue = referencedMvoIds[i].ToString(),
                Status = PendingExportAttributeChangeStatus.Pending
            });
            isolatingRepo.SeedPendingExportAsPersisted(pe);
        }

        // Recording connectors: capture the reference values each batch's connector actually
        // receives, cloned AT CALL TIME so later in-memory mutations cannot mask staleness.
        var receivedReferenceValues = new System.Collections.Concurrent.ConcurrentBag<(Guid PeId, string? StringValue, string? UnresolvedReferenceValue)>();
        Mock<IConnector> CreateRecordingConnector()
        {
            var mock = new Mock<IConnector>();
            var export = mock.As<IConnectorExportUsingCalls>();
            mock.Setup(c => c.Name).Returns("Recording Test Connector");
            export.Setup(c => c.ExportAsync(It.IsAny<IList<PendingExport>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((IList<PendingExport> exports, CancellationToken _) =>
                {
                    foreach (var pe in exports)
                        foreach (var avc in pe.AttributeValueChanges.Where(a => a.AttributeId == managerAttr.Id))
                            receivedReferenceValues.Add((pe.Id, avc.StringValue, avc.UnresolvedReferenceValue));
                    return exports.Select(_ => ConnectedSystemExportResult.Succeeded()).ToList();
                });
            return mock;
        }

        var primaryConnector = CreateRecordingConnector();
        var options = new ExportExecutionOptions { BatchSize = 2, MaxParallelism = 2 };

        // Act
        var result = await jim.ExportExecution.ExecuteExportsAsync(
            targetSystem,
            primaryConnector.Object,
            SyncRunMode.PreviewAndSync,
            options,
            CancellationToken.None,
            progressCallback: null,
            connectorFactory: () => CreateRecordingConnector().Object,
            repositoryFactory: () => new JIM.Data.Repositories.SyncRepositoryScope(isolatingRepo));

        // Assert: every deferred export reached a connector, and every reference the connectors
        // received was RESOLVED (StringValue populated, unresolved marker cleared). Before the
        // fix, the parallel batches re-loaded pre-resolution rows and received raw MVO GUIDs.
        Assert.That(receivedReferenceValues.Count, Is.EqualTo(6),
            "All six deferred exports should have been executed via connector batches.");
        var unresolvedReceived = receivedReferenceValues
            .Where(v => string.IsNullOrEmpty(v.StringValue) || !string.IsNullOrEmpty(v.UnresolvedReferenceValue))
            .ToList();
        Assert.That(unresolvedReceived, Is.Empty,
            "Connector received unresolved reference values; in-memory resolutions must be persisted " +
            "before parallel deferred batches re-load their exports from fresh contexts. Received: " +
            string.Join("; ", unresolvedReceived.Select(v => $"PE {v.PeId}: StringValue='{v.StringValue}', Unresolved='{v.UnresolvedReferenceValue}'")));
        Assert.That(result.FailedCount, Is.EqualTo(0), "No deferred export should fail in this scenario.");
    }

    /// <summary>
    /// Guard for keyset tie-handling (issue #985): exports sharing a single CreatedAt instant
    /// must all be collected exactly once when paging splits the tie across batches.
    /// </summary>
    [Test]
    public async Task ExecuteExportsAsync_IdenticalCreatedAt_AllExportedExactlyOnceAsync()
    {
        // Arrange
        var targetSystem = ConnectedSystemsData.Single(s => s.Name == "Dummy Target System");
        var targetUserType = ConnectedSystemObjectTypesData.Single(t => t.Name == "TARGET_USER");

        var sharedCreatedAt = DateTime.UtcNow.AddMinutes(-5);
        var seededIds = new List<Guid>();
        for (var i = 0; i < 5; i++)
        {
            var pe = CreateSeededCreateExport(targetSystem, targetUserType, sharedCreatedAt,
                hasUnresolvedReferences: false);
            SyncRepo.SeedPendingExport(pe);
            seededIds.Add(pe.Id);
        }

        var exportedIds = new List<Guid>();
        var mockConnector = new Mock<IConnector>();
        var mockExportConnector = mockConnector.As<IConnectorExportUsingCalls>();
        mockConnector.Setup(c => c.Name).Returns("Test Connector");
        mockExportConnector.Setup(c => c.ExportAsync(It.IsAny<IList<PendingExport>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IList<PendingExport> exports, CancellationToken _) =>
            {
                lock (exportedIds)
                {
                    exportedIds.AddRange(exports.Select(pe => pe.Id));
                }
                return exports.Select(_ => ConnectedSystemExportResult.Succeeded()).ToList();
            });

        var options = new ExportExecutionOptions { BatchSize = 2 };

        // Act
        var result = await Jim.ExportExecution.ExecuteExportsAsync(
            targetSystem,
            mockConnector.Object,
            SyncRunMode.PreviewAndSync,
            options,
            CancellationToken.None);

        // Assert: all five exported, no duplicates, no skips.
        Assert.That(result.SuccessCount, Is.EqualTo(5));
        Assert.That(exportedIds, Is.EquivalentTo(seededIds));
    }

    #endregion
}
