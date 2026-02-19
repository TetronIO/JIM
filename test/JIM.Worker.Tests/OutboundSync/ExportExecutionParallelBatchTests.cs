using JIM.Application;
using JIM.Data;
using JIM.Data.Repositories;
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
/// Tests for ExportExecutionServer parallel batch processing (Phase 3).
/// Validates that MaxParallelism > 1 processes batches concurrently using separate
/// DbContext and connector instances, and that results are correctly aggregated.
/// </summary>
[TestFixture]
public class ExportExecutionParallelBatchTests
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

        Jim = new JimApplication(new PostgresDataRepository(MockJimDbContext.Object));
    }

    /// <summary>
    /// Tests that with MaxParallelism=1 (default), the sequential code path is used.
    /// The connector is called with batches sequentially and results are correct.
    /// </summary>
    [Test]
    public async Task ExecuteExportsAsync_MaxParallelism1_UsesSequentialPathAsync()
    {
        // Arrange
        var targetSystem = ConnectedSystemsData.Single(s => s.Name == "Dummy Target System");
        var targetUserType = ConnectedSystemObjectTypesData.Single(t => t.Name == "TARGET_USER");
        var displayNameAttr = targetUserType.Attributes.Single(a => a.Name == MockTargetSystemAttributeNames.DisplayName.ToString());

        // Create 3 pending exports (fits in 1 batch with default size 100)
        for (var i = 0; i < 3; i++)
        {
            var cso = CreateCso(targetSystem, targetUserType);
            ConnectedSystemObjectsData.Add(cso);
            PendingExportsData.Add(CreatePendingExport(targetSystem, cso, displayNameAttr, $"Value {i}"));
        }

        var mockConnector = CreateMockConnector(ExportResult.Succeeded());

        var options = new ExportExecutionOptions
        {
            BatchSize = 100,
            MaxParallelism = 1
        };

        // Act
        var result = await Jim.ExportExecution.ExecuteExportsAsync(
            targetSystem,
            mockConnector.Object,
            SyncRunMode.PreviewAndSync,
            options,
            CancellationToken.None);

        // Assert
        Assert.That(result.SuccessCount, Is.EqualTo(3));
        Assert.That(result.FailedCount, Is.EqualTo(0));
        Assert.That(result.ProcessedExportItems, Has.Count.EqualTo(3));
    }

    /// <summary>
    /// Tests that MaxParallelism > 1 without connector/repository factories falls back to sequential.
    /// This is the safety path - parallel execution requires both factories.
    /// </summary>
    [Test]
    public async Task ExecuteExportsAsync_MaxParallelismWithoutFactories_UsesSequentialPathAsync()
    {
        // Arrange
        var targetSystem = ConnectedSystemsData.Single(s => s.Name == "Dummy Target System");
        var targetUserType = ConnectedSystemObjectTypesData.Single(t => t.Name == "TARGET_USER");
        var displayNameAttr = targetUserType.Attributes.Single(a => a.Name == MockTargetSystemAttributeNames.DisplayName.ToString());

        for (var i = 0; i < 3; i++)
        {
            var cso = CreateCso(targetSystem, targetUserType);
            ConnectedSystemObjectsData.Add(cso);
            PendingExportsData.Add(CreatePendingExport(targetSystem, cso, displayNameAttr, $"Value {i}"));
        }

        var mockConnector = CreateMockConnector(ExportResult.Succeeded());

        var options = new ExportExecutionOptions
        {
            BatchSize = 2,
            MaxParallelism = 4  // High parallelism but no factories
        };

        // Act - no connectorFactory or repositoryFactory passed
        var result = await Jim.ExportExecution.ExecuteExportsAsync(
            targetSystem,
            mockConnector.Object,
            SyncRunMode.PreviewAndSync,
            options,
            CancellationToken.None);

        // Assert - should still work via sequential path
        Assert.That(result.SuccessCount, Is.EqualTo(3));
        Assert.That(result.FailedCount, Is.EqualTo(0));
    }

    /// <summary>
    /// Tests that MaxParallelism > 1 with factories creates separate connector and repository
    /// instances for parallel batches. Uses batch size of 2 with 4 exports to get 2 batches.
    /// </summary>
    [Test]
    public async Task ExecuteExportsAsync_MaxParallelism2_ProcessesBatchesInParallelAsync()
    {
        // Arrange
        var targetSystem = ConnectedSystemsData.Single(s => s.Name == "Dummy Target System");
        var targetUserType = ConnectedSystemObjectTypesData.Single(t => t.Name == "TARGET_USER");
        var displayNameAttr = targetUserType.Attributes.Single(a => a.Name == MockTargetSystemAttributeNames.DisplayName.ToString());

        // Create 4 pending exports - with BatchSize=2, this creates 2 batches
        var pendingExportIds = new List<Guid>();
        for (var i = 0; i < 4; i++)
        {
            var cso = CreateCso(targetSystem, targetUserType);
            ConnectedSystemObjectsData.Add(cso);
            var pe = CreatePendingExport(targetSystem, cso, displayNameAttr, $"Value {i}");
            PendingExportsData.Add(pe);
            pendingExportIds.Add(pe.Id);
        }

        var primaryConnector = CreateMockConnector(ExportResult.Succeeded());

        // Track how many connector instances were created
        var connectorInstanceCount = 0;
        Func<IConnector> connectorFactory = () =>
        {
            Interlocked.Increment(ref connectorInstanceCount);
            return CreateMockConnector(ExportResult.Succeeded()).Object;
        };

        // Track how many repository instances were created
        var repoInstanceCount = 0;
        Func<IRepository> repositoryFactory = () =>
        {
            Interlocked.Increment(ref repoInstanceCount);
            return CreateMockRepository(PendingExportsData);
        };

        var options = new ExportExecutionOptions
        {
            BatchSize = 2,
            MaxParallelism = 2
        };

        // Act
        var result = await Jim.ExportExecution.ExecuteExportsAsync(
            targetSystem,
            primaryConnector.Object,
            SyncRunMode.PreviewAndSync,
            options,
            CancellationToken.None,
            connectorFactory: connectorFactory,
            repositoryFactory: repositoryFactory);

        // Assert - all 4 exports should succeed
        Assert.That(result.SuccessCount, Is.EqualTo(4), "All 4 exports should succeed");
        Assert.That(result.FailedCount, Is.EqualTo(0));
        Assert.That(result.ProcessedExportItems, Has.Count.EqualTo(4));

        // Assert - parallel path was used: batch 0 reuses primary connector,
        // batch 1 creates a new one. So 1 factory connector + 2 factory repos.
        Assert.That(connectorInstanceCount, Is.EqualTo(1), "One additional connector should be created (batch 1)");
        Assert.That(repoInstanceCount, Is.EqualTo(2), "Two repository instances should be created (one per batch)");
    }

    /// <summary>
    /// Tests that when one parallel batch fails, other batches still complete successfully.
    /// Error isolation is critical for parallel processing.
    /// </summary>
    [Test]
    public async Task ExecuteExportsAsync_OneBatchFails_OtherBatchesCompleteAsync()
    {
        // Arrange
        var targetSystem = ConnectedSystemsData.Single(s => s.Name == "Dummy Target System");
        var targetUserType = ConnectedSystemObjectTypesData.Single(t => t.Name == "TARGET_USER");
        var displayNameAttr = targetUserType.Attributes.Single(a => a.Name == MockTargetSystemAttributeNames.DisplayName.ToString());

        // Create 4 pending exports - 2 batches of 2
        for (var i = 0; i < 4; i++)
        {
            var cso = CreateCso(targetSystem, targetUserType);
            ConnectedSystemObjectsData.Add(cso);
            PendingExportsData.Add(CreatePendingExport(targetSystem, cso, displayNameAttr, $"Value {i}"));
        }

        // Primary connector succeeds (used by batch 0)
        var primaryConnector = CreateMockConnector(ExportResult.Succeeded());

        // Factory connector fails (used by batch 1)
        Func<IConnector> connectorFactory = () =>
        {
            var failMock = new Mock<IConnector>();
            var failExportMock = failMock.As<IConnectorExportUsingCalls>();
            failMock.Setup(c => c.Name).Returns("Failing Batch Connector");
            failExportMock.Setup(c => c.ExportAsync(It.IsAny<IList<PendingExport>>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new Exception("Simulated batch failure"));
            return failMock.Object;
        };

        Func<IRepository> repositoryFactory = () => CreateMockRepository(PendingExportsData);

        var options = new ExportExecutionOptions
        {
            BatchSize = 2,
            MaxParallelism = 2
        };

        // Act
        var result = await Jim.ExportExecution.ExecuteExportsAsync(
            targetSystem,
            primaryConnector.Object,
            SyncRunMode.PreviewAndSync,
            options,
            CancellationToken.None,
            connectorFactory: connectorFactory,
            repositoryFactory: repositoryFactory);

        // Assert - batch 0 succeeds (2 exports), batch 1 fails (2 exports)
        Assert.That(result.SuccessCount, Is.EqualTo(2), "Batch 0 should succeed with 2 exports");
        Assert.That(result.FailedCount, Is.EqualTo(2), "Batch 1 should fail with 2 exports");
    }

    /// <summary>
    /// Tests that cancellation stops all parallel batches.
    /// </summary>
    [Test]
    public void ExecuteExportsAsync_WhenCancelled_StopsAllBatches()
    {
        // Arrange
        var targetSystem = ConnectedSystemsData.Single(s => s.Name == "Dummy Target System");
        var targetUserType = ConnectedSystemObjectTypesData.Single(t => t.Name == "TARGET_USER");
        var displayNameAttr = targetUserType.Attributes.Single(a => a.Name == MockTargetSystemAttributeNames.DisplayName.ToString());

        // Create 6 pending exports - 3 batches of 2
        for (var i = 0; i < 6; i++)
        {
            var cso = CreateCso(targetSystem, targetUserType);
            ConnectedSystemObjectsData.Add(cso);
            PendingExportsData.Add(CreatePendingExport(targetSystem, cso, displayNameAttr, $"Value {i}"));
        }

        var primaryConnector = CreateMockConnector(ExportResult.Succeeded());

        Func<IConnector> connectorFactory = () => CreateMockConnector(ExportResult.Succeeded()).Object;
        Func<IRepository> repositoryFactory = () => CreateMockRepository(PendingExportsData);

        using var cts = new CancellationTokenSource();
        cts.Cancel(); // Cancel immediately

        var options = new ExportExecutionOptions
        {
            BatchSize = 2,
            MaxParallelism = 3
        };

        // Act & Assert - should throw OperationCanceledException
        Assert.ThrowsAsync<OperationCanceledException>(async () =>
        {
            await Jim.ExportExecution.ExecuteExportsAsync(
                targetSystem,
                primaryConnector.Object,
                SyncRunMode.PreviewAndSync,
                options,
                cts.Token,
                connectorFactory: connectorFactory,
                repositoryFactory: repositoryFactory);
        });
    }

    /// <summary>
    /// Tests that progress callback is invoked during parallel batch processing
    /// and that progress counts are correct.
    /// </summary>
    [Test]
    public async Task ExecuteExportsAsync_ParallelWithProgressCallback_ReportsProgressAsync()
    {
        // Arrange
        var targetSystem = ConnectedSystemsData.Single(s => s.Name == "Dummy Target System");
        var targetUserType = ConnectedSystemObjectTypesData.Single(t => t.Name == "TARGET_USER");
        var displayNameAttr = targetUserType.Attributes.Single(a => a.Name == MockTargetSystemAttributeNames.DisplayName.ToString());

        for (var i = 0; i < 4; i++)
        {
            var cso = CreateCso(targetSystem, targetUserType);
            ConnectedSystemObjectsData.Add(cso);
            PendingExportsData.Add(CreatePendingExport(targetSystem, cso, displayNameAttr, $"Value {i}"));
        }

        var primaryConnector = CreateMockConnector(ExportResult.Succeeded());

        Func<IConnector> connectorFactory = () => CreateMockConnector(ExportResult.Succeeded()).Object;
        Func<IRepository> repositoryFactory = () => CreateMockRepository(PendingExportsData);

        var progressReports = new List<ExportProgressInfo>();
        Func<ExportProgressInfo, Task> progressCallback = info =>
        {
            lock (progressReports)
            {
                progressReports.Add(info);
            }
            return Task.CompletedTask;
        };

        var options = new ExportExecutionOptions
        {
            BatchSize = 2,
            MaxParallelism = 2
        };

        // Act
        var result = await Jim.ExportExecution.ExecuteExportsAsync(
            targetSystem,
            primaryConnector.Object,
            SyncRunMode.PreviewAndSync,
            options,
            CancellationToken.None,
            progressCallback,
            connectorFactory: connectorFactory,
            repositoryFactory: repositoryFactory);

        // Assert - progress was reported
        Assert.That(progressReports.Count, Is.GreaterThan(0), "Expected progress to be reported");
        Assert.That(progressReports.Any(p => p.Phase == ExportPhase.Preparing), Is.True, "Expected Preparing phase");
        Assert.That(progressReports.Any(p => p.Phase == ExportPhase.Executing), Is.True, "Expected Executing phase");
        Assert.That(progressReports.Any(p => p.Phase == ExportPhase.Completed), Is.True, "Expected Completed phase");
    }

    /// <summary>
    /// Tests that with only 1 batch (even when MaxParallelism > 1), the sequential path is used.
    /// Parallel overhead is unnecessary for a single batch.
    /// </summary>
    [Test]
    public async Task ExecuteExportsAsync_SingleBatchWithHighParallelism_UsesSequentialPathAsync()
    {
        // Arrange
        var targetSystem = ConnectedSystemsData.Single(s => s.Name == "Dummy Target System");
        var targetUserType = ConnectedSystemObjectTypesData.Single(t => t.Name == "TARGET_USER");
        var displayNameAttr = targetUserType.Attributes.Single(a => a.Name == MockTargetSystemAttributeNames.DisplayName.ToString());

        // Create 2 exports with BatchSize=100, so only 1 batch
        for (var i = 0; i < 2; i++)
        {
            var cso = CreateCso(targetSystem, targetUserType);
            ConnectedSystemObjectsData.Add(cso);
            PendingExportsData.Add(CreatePendingExport(targetSystem, cso, displayNameAttr, $"Value {i}"));
        }

        var primaryConnector = CreateMockConnector(ExportResult.Succeeded());

        var connectorFactoryCalled = false;
        Func<IConnector> connectorFactory = () =>
        {
            connectorFactoryCalled = true;
            return CreateMockConnector(ExportResult.Succeeded()).Object;
        };

        var repoFactoryCalled = false;
        Func<IRepository> repositoryFactory = () =>
        {
            repoFactoryCalled = true;
            return CreateMockRepository(PendingExportsData);
        };

        var options = new ExportExecutionOptions
        {
            BatchSize = 100,  // Large batch size -> 1 batch
            MaxParallelism = 4
        };

        // Act
        var result = await Jim.ExportExecution.ExecuteExportsAsync(
            targetSystem,
            primaryConnector.Object,
            SyncRunMode.PreviewAndSync,
            options,
            CancellationToken.None,
            connectorFactory: connectorFactory,
            repositoryFactory: repositoryFactory);

        // Assert - sequential path used (factories never called since only 1 batch)
        Assert.That(result.SuccessCount, Is.EqualTo(2));
        Assert.That(connectorFactoryCalled, Is.False, "Connector factory should not be called for single batch");
        Assert.That(repoFactoryCalled, Is.False, "Repository factory should not be called for single batch");
    }

    /// <summary>
    /// Tests that ProcessedExportItems are correctly aggregated from parallel batches.
    /// </summary>
    [Test]
    public async Task ExecuteExportsAsync_ParallelBatches_AggregatesProcessedExportItemsAsync()
    {
        // Arrange
        var targetSystem = ConnectedSystemsData.Single(s => s.Name == "Dummy Target System");
        var targetUserType = ConnectedSystemObjectTypesData.Single(t => t.Name == "TARGET_USER");
        var displayNameAttr = targetUserType.Attributes.Single(a => a.Name == MockTargetSystemAttributeNames.DisplayName.ToString());

        // Create 6 pending exports - 3 batches of 2
        var expectedIds = new List<Guid>();
        for (var i = 0; i < 6; i++)
        {
            var cso = CreateCso(targetSystem, targetUserType);
            ConnectedSystemObjectsData.Add(cso);
            var pe = CreatePendingExport(targetSystem, cso, displayNameAttr, $"Value {i}");
            PendingExportsData.Add(pe);
            expectedIds.Add(pe.Id);
        }

        var primaryConnector = CreateMockConnector(ExportResult.Succeeded());
        Func<IConnector> connectorFactory = () => CreateMockConnector(ExportResult.Succeeded()).Object;
        Func<IRepository> repositoryFactory = () => CreateMockRepository(PendingExportsData);

        var options = new ExportExecutionOptions
        {
            BatchSize = 2,
            MaxParallelism = 3
        };

        // Act
        var result = await Jim.ExportExecution.ExecuteExportsAsync(
            targetSystem,
            primaryConnector.Object,
            SyncRunMode.PreviewAndSync,
            options,
            CancellationToken.None,
            connectorFactory: connectorFactory,
            repositoryFactory: repositoryFactory);

        // Assert - all 6 export items aggregated
        Assert.That(result.SuccessCount, Is.EqualTo(6));
        Assert.That(result.ProcessedExportItems, Has.Count.EqualTo(6));
        Assert.That(result.ProcessedExportItems.All(item => item.Succeeded), Is.True);
    }

    /// <summary>
    /// Tests that the ExportExecutionOptions.MaxParallelism defaults to 1 (sequential).
    /// </summary>
    [Test]
    public void ExportExecutionOptions_MaxParallelism_DefaultsTo1()
    {
        var options = new ExportExecutionOptions();
        Assert.That(options.MaxParallelism, Is.EqualTo(1), "MaxParallelism should default to 1 for safe sequential behaviour");
    }

    /// <summary>
    /// Tests that the ExportExecutionOptions.BatchSize defaults to 100.
    /// </summary>
    [Test]
    public void ExportExecutionOptions_BatchSize_DefaultsTo100()
    {
        var options = new ExportExecutionOptions();
        Assert.That(options.BatchSize, Is.EqualTo(100));
    }

    #region Helper Methods

    private static ConnectedSystemObject CreateCso(ConnectedSystem system, ConnectedSystemObjectType type)
    {
        return new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = system.Id,
            Type = type,
            TypeId = type.Id,
            AttributeValues = new List<ConnectedSystemObjectAttributeValue>()
        };
    }

    private static PendingExport CreatePendingExport(
        ConnectedSystem system,
        ConnectedSystemObject cso,
        ConnectedSystemObjectTypeAttribute attr,
        string value)
    {
        return new PendingExport
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = system.Id,
            ConnectedSystem = system,
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
                    AttributeId = attr.Id,
                    Attribute = attr,
                    StringValue = value,
                    Status = PendingExportAttributeChangeStatus.Pending
                }
            }
        };
    }

    private static Mock<IConnector> CreateMockConnector(ExportResult defaultResult)
    {
        var mockConnector = new Mock<IConnector>();
        var mockExportConnector = mockConnector.As<IConnectorExportUsingCalls>();
        mockConnector.Setup(c => c.Name).Returns("Test Connector");
        mockExportConnector.Setup(c => c.ExportAsync(It.IsAny<IList<PendingExport>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IList<PendingExport> exports, CancellationToken _) =>
                exports.Select(_ => defaultResult).ToList());
        return mockConnector;
    }

    /// <summary>
    /// Creates a mock IRepository with its own mock DbContext that can serve
    /// GetPendingExportsByIdsAsync from the shared pending exports data.
    /// This simulates what each parallel batch's per-batch repository would do.
    /// </summary>
    private static IRepository CreateMockRepository(List<PendingExport> pendingExportsData)
    {
        var mockPendingExports = pendingExportsData.BuildMockDbSet();

        var mockDbContext = new Mock<JimDbContext>();
        mockDbContext.Setup(m => m.PendingExports).Returns(mockPendingExports.Object);

        // Also need to set up the other DbSets that might be accessed during batch processing
        mockDbContext.Setup(m => m.ConnectedSystemAttributes)
            .Returns(new List<ConnectedSystemObjectTypeAttribute>().BuildMockDbSet().Object);
        mockDbContext.Setup(m => m.ConnectedSystemObjects)
            .Returns(new List<ConnectedSystemObject>().BuildMockDbSet().Object);

        return new PostgresDataRepository(mockDbContext.Object);
    }

    #endregion
}
