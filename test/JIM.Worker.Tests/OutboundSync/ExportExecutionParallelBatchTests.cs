// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
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
using SyncRepository = JIM.InMemoryData.SyncRepository;

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
        // Collision Remediation reads whether Metaverse Object changes are tracked; an empty set falls back to the default.
        MockJimDbContext.Setup(m => m.ServiceSettingItems).Returns(new List<ServiceSetting>().BuildMockDbSet().Object);

        SyncRepo = TestUtilities.CreateSyncRepository(activity: ActivitiesData.First());
        Jim = new JimApplication(new PostgresDataRepository(MockJimDbContext.Object), syncRepository: SyncRepo);
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

        // Create 3 Pending Exports (fits in 1 batch with default size 100)
        for (var i = 0; i < 3; i++)
        {
            var cso = CreateCso(targetSystem, targetUserType);
            ConnectedSystemObjectsData.Add(cso);
            var pe = CreatePendingExport(targetSystem, cso, displayNameAttr, $"Value {i}");
            PendingExportsData.Add(pe);
            SyncRepo.SeedPendingExport(pe);
        }

        var mockConnector = CreateMockConnector(ConnectedSystemExportResult.Succeeded());

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
            var pe = CreatePendingExport(targetSystem, cso, displayNameAttr, $"Value {i}");
            PendingExportsData.Add(pe);
            SyncRepo.SeedPendingExport(pe);
        }

        var mockConnector = CreateMockConnector(ConnectedSystemExportResult.Succeeded());

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
    /// Tests that with MaxParallelism > 1 and factories provided, immediate exports are still
    /// processed sequentially via batch-loading. Factories are only used for deferred exports,
    /// so they should not be called when all exports are immediate (no unresolved references).
    /// </summary>
    [Test]
    public async Task ExecuteExportsAsync_MaxParallelism2_ProcessesImmediateExportsSequentiallyAsync()
    {
        // Arrange
        var targetSystem = ConnectedSystemsData.Single(s => s.Name == "Dummy Target System");
        var targetUserType = ConnectedSystemObjectTypesData.Single(t => t.Name == "TARGET_USER");
        var displayNameAttr = targetUserType.Attributes.Single(a => a.Name == MockTargetSystemAttributeNames.DisplayName.ToString());

        // Create 4 Pending Exports - with BatchSize=2, processed in 2 sequential batches
        var pendingExportIds = new List<Guid>();
        for (var i = 0; i < 4; i++)
        {
            var cso = CreateCso(targetSystem, targetUserType);
            ConnectedSystemObjectsData.Add(cso);
            var pe = CreatePendingExport(targetSystem, cso, displayNameAttr, $"Value {i}");
            PendingExportsData.Add(pe);
            SyncRepo.SeedPendingExport(pe);
            pendingExportIds.Add(pe.Id);
        }

        var primaryConnector = CreateMockConnector(ConnectedSystemExportResult.Succeeded());

        // Track whether factories are called - they should NOT be for immediate exports
        var connectorFactoryCalled = false;
        Func<IConnector> connectorFactory = () =>
        {
            connectorFactoryCalled = true;
            return CreateMockConnector(ConnectedSystemExportResult.Succeeded()).Object;
        };

        var repoFactoryCalled = false;
        Func<ISyncRepositoryScope> repositoryFactory = () =>
        {
            repoFactoryCalled = true;
            return new SyncRepositoryScope(TestUtilities.CreateSyncRepository(pendingExports: PendingExportsData));
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

        // Assert - all 4 exports should succeed via sequential batch-loading
        Assert.That(result.SuccessCount, Is.EqualTo(4), "All 4 exports should succeed");
        Assert.That(result.FailedCount, Is.EqualTo(0));
        Assert.That(result.ProcessedExportItems, Has.Count.EqualTo(4));

        // Assert - factories not called because immediate exports use the primary connector
        Assert.That(connectorFactoryCalled, Is.False, "Connector factory should not be called for immediate exports");
        Assert.That(repoFactoryCalled, Is.False, "Repository factory should not be called for immediate exports");
    }

    /// <summary>
    /// Tests that when a connector throws during sequential batch-loading, exports processed
    /// before the failure are still counted. The failure stops further batch processing but
    /// does not roll back already-completed batches.
    /// </summary>
    [Test]
    public async Task ExecuteExportsAsync_ConnectorFailsOnSecondBatch_FirstBatchSucceedsAsync()
    {
        // Arrange
        var targetSystem = ConnectedSystemsData.Single(s => s.Name == "Dummy Target System");
        var targetUserType = ConnectedSystemObjectTypesData.Single(t => t.Name == "TARGET_USER");
        var displayNameAttr = targetUserType.Attributes.Single(a => a.Name == MockTargetSystemAttributeNames.DisplayName.ToString());

        // Create 4 Pending Exports - 2 batches of 2
        for (var i = 0; i < 4; i++)
        {
            var cso = CreateCso(targetSystem, targetUserType);
            ConnectedSystemObjectsData.Add(cso);
            var pe = CreatePendingExport(targetSystem, cso, displayNameAttr, $"Value {i}");
            PendingExportsData.Add(pe);
            SyncRepo.SeedPendingExport(pe);
        }

        // Connector succeeds on first call, throws on second call
        var callCount = 0;
        var mockConnector = new Mock<IConnector>();
        var mockExportConnector = mockConnector.As<IConnectorExportUsingCalls>();
        mockConnector.Setup(c => c.Name).Returns("Test Connector");
        mockExportConnector.Setup(c => c.ExportAsync(It.IsAny<IList<PendingExport>>(), It.IsAny<CancellationToken>(), It.IsAny<IConnectorProgress>()))
            .ReturnsAsync((IList<PendingExport> exports, CancellationToken _, IConnectorProgress _) =>
            {
                var currentCall = Interlocked.Increment(ref callCount);
                if (currentCall >= 2)
                    throw new Exception("Simulated batch failure on second call");
                return exports.Select(_ => ConnectedSystemExportResult.Succeeded()).ToList();
            });

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
            CancellationToken.None);

        // Assert - first batch (2 exports) succeeded before the connector threw on second batch
        Assert.That(result.SuccessCount, Is.EqualTo(2), "First batch should succeed before connector failure");
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

        // Create 6 Pending Exports - 3 batches of 2
        for (var i = 0; i < 6; i++)
        {
            var cso = CreateCso(targetSystem, targetUserType);
            ConnectedSystemObjectsData.Add(cso);
            var pe = CreatePendingExport(targetSystem, cso, displayNameAttr, $"Value {i}");
            PendingExportsData.Add(pe);
            SyncRepo.SeedPendingExport(pe);
        }

        var primaryConnector = CreateMockConnector(ConnectedSystemExportResult.Succeeded());

        Func<IConnector> connectorFactory = () => CreateMockConnector(ConnectedSystemExportResult.Succeeded()).Object;
        Func<ISyncRepositoryScope> repositoryFactory = () => new SyncRepositoryScope(TestUtilities.CreateSyncRepository(pendingExports: PendingExportsData));

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
            var pe = CreatePendingExport(targetSystem, cso, displayNameAttr, $"Value {i}");
            PendingExportsData.Add(pe);
            SyncRepo.SeedPendingExport(pe);
        }

        var primaryConnector = CreateMockConnector(ConnectedSystemExportResult.Succeeded());

        Func<IConnector> connectorFactory = () => CreateMockConnector(ConnectedSystemExportResult.Succeeded()).Object;
        Func<ISyncRepositoryScope> repositoryFactory = () => new SyncRepositoryScope(TestUtilities.CreateSyncRepository(pendingExports: PendingExportsData));

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
            var pe = CreatePendingExport(targetSystem, cso, displayNameAttr, $"Value {i}");
            PendingExportsData.Add(pe);
            SyncRepo.SeedPendingExport(pe);
        }

        var primaryConnector = CreateMockConnector(ConnectedSystemExportResult.Succeeded());

        var connectorFactoryCalled = false;
        Func<IConnector> connectorFactory = () =>
        {
            connectorFactoryCalled = true;
            return CreateMockConnector(ConnectedSystemExportResult.Succeeded()).Object;
        };

        var repoFactoryCalled = false;
        Func<ISyncRepositoryScope> repositoryFactory = () =>
        {
            repoFactoryCalled = true;
            return new SyncRepositoryScope(TestUtilities.CreateSyncRepository(pendingExports: PendingExportsData));
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

        // Create 6 Pending Exports - 3 batches of 2
        var expectedIds = new List<Guid>();
        for (var i = 0; i < 6; i++)
        {
            var cso = CreateCso(targetSystem, targetUserType);
            ConnectedSystemObjectsData.Add(cso);
            var pe = CreatePendingExport(targetSystem, cso, displayNameAttr, $"Value {i}");
            PendingExportsData.Add(pe);
            SyncRepo.SeedPendingExport(pe);
            expectedIds.Add(pe.Id);
        }

        var primaryConnector = CreateMockConnector(ConnectedSystemExportResult.Succeeded());
        Func<IConnector> connectorFactory = () => CreateMockConnector(ConnectedSystemExportResult.Succeeded()).Object;
        Func<ISyncRepositoryScope> repositoryFactory = () => new SyncRepositoryScope(TestUtilities.CreateSyncRepository(pendingExports: PendingExportsData));

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
    /// Run Profile Safeguards (#1618): a withheld change type must never reach the parallel deferred
    /// batch dispatcher at all, however many batches an allowed type spans. The ledger's filtering in
    /// <c>ProcessDeferredExportsAsync</c> runs before <c>exportsToWrite</c> is split into batches, so
    /// a withheld type is excluded before parallel dispatch, not raced against it.
    /// </summary>
    [Test]
    public async Task ExecuteExportsAsync_ParallelDeferredBatches_WithheldTypeNeverReachesDispatchAsync()
    {
        // Arrange
        var targetSystem = ConnectedSystemsData.Single(s => s.Name == "Dummy Target System");
        var targetUserType = ConnectedSystemObjectTypesData.Single(t => t.Name == "TARGET_USER");
        var baseTime = DateTime.UtcNow.AddMinutes(-10);

        // A resolvable reference target shared by every deferred export below.
        var referencedMvoId = SeedResolvableReferenceTarget(targetSystem, targetUserType);
        PendingExport CreateDeferredExport(PendingExportChangeType changeType, DateTime createdAt) =>
            SeedResolvableDeferredExport(targetSystem, targetUserType, changeType, createdAt, referencedMvoId);

        // 4 allowed-type (Update, no limit) deferred exports: with BatchSize=2 these span 2 batches,
        // enough for the parallel dispatcher (useParallelBatches requires deferredBatches.Count > 1).
        var deferredUpdates = Enumerable.Range(0, 4)
            .Select(i => CreateDeferredExport(PendingExportChangeType.Update, baseTime.AddSeconds(i)))
            .ToList();

        // 2 withheld-type (Create, limit 1) deferred exports, later in queue order so the primary
        // loop's fast-path collection (unfiltered by change type) is what gathers them.
        var deferredCreates = Enumerable.Range(0, 2)
            .Select(i => CreateDeferredExport(PendingExportChangeType.Create, baseTime.AddSeconds(10 + i)))
            .ToList();

        var primaryConnector = CreateMockConnector(ConnectedSystemExportResult.Succeeded());

        var connectorFactoryCalled = false;
        Func<IConnector> connectorFactory = () =>
        {
            connectorFactoryCalled = true;
            return CreateMockConnector(ConnectedSystemExportResult.Succeeded()).Object;
        };
        Func<ISyncRepositoryScope> repositoryFactory = () => new SyncRepositoryScope(TestUtilities.CreateSyncRepository(pendingExports: PendingExportsData));

        var options = new ExportExecutionOptions
        {
            BatchSize = 2,
            MaxParallelism = 2,
            MaxCreates = 1
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

        // Assert - the allowed type completed via genuine parallel dispatch
        Assert.That(connectorFactoryCalled, Is.True, "the 4 allowed-type exports must have gone through the parallel path to prove the guard, not merely the sequential fallback");
        Assert.That(result.SuccessCount, Is.EqualTo(4));
        Assert.That(deferredUpdates, Has.All.Matches<PendingExport>(u => u.Status == PendingExportStatus.Exported));

        // Assert - the withheld type never reached any batch, parallel or otherwise
        Assert.That(result.CreatesWithheld, Is.EqualTo(2));
        Assert.That(deferredCreates, Has.All.Matches<PendingExport>(c => c.Status == PendingExportStatus.Pending));
        Assert.That(result.ProcessedExportItems.Any(item => item.ChangeType == PendingExportChangeType.Create), Is.False);
    }

    /// <summary>
    /// #1875: a parallel batch's own connector was opened outside the block that closes and disposes it, so one that
    /// failed to connect was neither closed nor disposed, and anything it returned at close (a pin the failure
    /// invalidated) was lost. The batch still fails; its connector must still be closed.
    /// </summary>
    [Test]
    public async Task ExecuteExportsAsync_ParallelDeferredBatches_BatchConnectorThatFailsToOpenIsStillClosedAsync()
    {
        // Arrange
        var targetSystem = ConnectedSystemsData.Single(s => s.Name == "Dummy Target System");
        var targetUserType = ConnectedSystemObjectTypesData.Single(t => t.Name == "TARGET_USER");
        var baseTime = DateTime.UtcNow.AddMinutes(-10);

        // 4 deferred exports: with BatchSize=2 these span 2 batches, enough for the parallel dispatcher, and the
        // second batch runs on a connector of its own from the factory.
        var referencedMvoId = SeedResolvableReferenceTarget(targetSystem, targetUserType);
        for (var i = 0; i < 4; i++)
            SeedResolvableDeferredExport(targetSystem, targetUserType, PendingExportChangeType.Update, baseTime.AddSeconds(i), referencedMvoId);

        var primaryConnector = CreateMockConnector(ConnectedSystemExportResult.Succeeded());

        var batchConnectors = new System.Collections.Concurrent.ConcurrentBag<Mock<IConnectorExportUsingCalls>>();
        Func<IConnector> connectorFactory = () =>
        {
            var batchConnector = CreateMockConnector(ConnectedSystemExportResult.Succeeded());
            var exportCalls = batchConnector.As<IConnectorExportUsingCalls>();
            exportCalls.Setup(c => c.OpenExportConnection(It.IsAny<IList<ConnectedSystemSettingValue>>(), It.IsAny<string?>()))
                .Throws(new InvalidOperationException("simulated connection failure"));
            batchConnectors.Add(exportCalls);
            return batchConnector.Object;
        };
        Func<ISyncRepositoryScope> repositoryFactory = () => new SyncRepositoryScope(TestUtilities.CreateSyncRepository(pendingExports: PendingExportsData));

        var options = new ExportExecutionOptions
        {
            BatchSize = 2,
            MaxParallelism = 2
        };

        // Act
        await Jim.ExportExecution.ExecuteExportsAsync(
            targetSystem,
            primaryConnector.Object,
            SyncRunMode.PreviewAndSync,
            options,
            CancellationToken.None,
            connectorFactory: connectorFactory,
            repositoryFactory: repositoryFactory);

        // Assert
        Assert.That(batchConnectors, Is.Not.Empty, "precondition: the parallel path created a batch connector of its own");
        foreach (var batchConnector in batchConnectors)
            batchConnector.Verify(c => c.CloseExportConnection(), Times.Once,
                "a batch connector that failed to open must still be closed, so what it returns at close can be persisted");
    }

    /// <summary>
    /// Every connector used for an export must be told the Connected System's managed scope (#1764), not only
    /// the primary one. Each parallel batch gets a fresh connector from the factory, and one that never receives
    /// the scope writes wherever the Attribute Flow points it, including into containers the next import cannot
    /// read back: the very churn the managed scope exists to prevent (#1250, #1255).
    /// </summary>
    [Test]
    public async Task ExecuteExportsAsync_ParallelDeferredBatches_EveryConnectorReceivesManagedScopeAsync()
    {
        // Arrange
        var targetSystem = ConnectedSystemsData.Single(s => s.Name == "Dummy Target System");
        var targetUserType = ConnectedSystemObjectTypesData.Single(t => t.Name == "TARGET_USER");
        var baseTime = DateTime.UtcNow.AddMinutes(-10);

        // A selection with an exclusion beneath it, so the scope stated must carry both (#1255).
        var serviceAccounts = new ConnectedSystemContainer { Name = "Service Accounts", ExternalId = "OU=Service Accounts,OU=Corp,DC=test,DC=local", Excluded = true };
        var corp = new ConnectedSystemContainer { Name = "Corp", ExternalId = "OU=Corp,DC=test,DC=local", Selected = true };
        serviceAccounts.ParentContainer = corp;
        corp.ChildContainers.Add(serviceAccounts);
        targetSystem.Partitions = [new ConnectedSystemPartition { Name = "test.local", Selected = true, Containers = [corp] }];

        // 4 deferred exports: with BatchSize=2 these span 2 batches, enough for the parallel dispatcher.
        var referencedMvoId = SeedResolvableReferenceTarget(targetSystem, targetUserType);
        for (var i = 0; i < 4; i++)
            SeedResolvableDeferredExport(targetSystem, targetUserType, PendingExportChangeType.Update, baseTime.AddSeconds(i), referencedMvoId);

        var primaryConnector = CreateMockConnector(ConnectedSystemExportResult.Succeeded());
        var primaryScope = primaryConnector.As<IConnectorManagedScope>();

        var batchScopes = new System.Collections.Concurrent.ConcurrentBag<Mock<IConnectorManagedScope>>();
        Func<IConnector> connectorFactory = () =>
        {
            var batchConnector = CreateMockConnector(ConnectedSystemExportResult.Succeeded());
            batchScopes.Add(batchConnector.As<IConnectorManagedScope>());
            return batchConnector.Object;
        };
        Func<ISyncRepositoryScope> repositoryFactory = () => new SyncRepositoryScope(TestUtilities.CreateSyncRepository(pendingExports: PendingExportsData));

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

        // Assert - the exports went through genuine parallel dispatch, so a batch connector was in play
        Assert.That(result.SuccessCount, Is.EqualTo(4));
        Assert.That(batchScopes, Is.Not.Empty, "the exports must have gone through the parallel path, not the sequential fallback");

        // Assert - every batch connector and the primary connector were told the same scope
        var expectedScope = new[] { corp, serviceAccounts };
        foreach (var batchScope in batchScopes)
            batchScope.Verify(s => s.SetManagedScope(It.Is<IReadOnlyList<ConnectedSystemContainer>>(scope => scope.OrderBy(c => c.ExternalId).SequenceEqual(expectedScope.OrderBy(c => c.ExternalId)))), Times.Once,
                "a parallel batch's connector must receive the managed scope, or it can write outside the selected containers");
        primaryScope.Verify(s => s.SetManagedScope(It.Is<IReadOnlyList<ConnectedSystemContainer>>(scope => scope.OrderBy(c => c.ExternalId).SequenceEqual(expectedScope.OrderBy(c => c.ExternalId)))), Times.Once);
    }

    /// <summary>
    /// A Connected System with no container selections states no scope at all: the connector is never asked,
    /// so an unset scope keeps permitting everything, exactly as it did before managed scope existed.
    /// </summary>
    [Test]
    public async Task ExecuteExportsAsync_NoContainerSelections_DoesNotStateManagedScopeAsync()
    {
        // Arrange
        var targetSystem = ConnectedSystemsData.Single(s => s.Name == "Dummy Target System");
        var targetUserType = ConnectedSystemObjectTypesData.Single(t => t.Name == "TARGET_USER");
        var displayNameAttr = targetUserType.Attributes.Single(a => a.Name == MockTargetSystemAttributeNames.DisplayName.ToString());
        targetSystem.Partitions = null;

        var cso = CreateCso(targetSystem, targetUserType);
        ConnectedSystemObjectsData.Add(cso);
        var pe = CreatePendingExport(targetSystem, cso, displayNameAttr, "Value");
        PendingExportsData.Add(pe);
        SyncRepo.SeedPendingExport(pe);

        var connector = CreateMockConnector(ConnectedSystemExportResult.Succeeded());
        var scope = connector.As<IConnectorManagedScope>();

        // Act
        var result = await Jim.ExportExecution.ExecuteExportsAsync(
            targetSystem,
            connector.Object,
            SyncRunMode.PreviewAndSync,
            new ExportExecutionOptions(),
            CancellationToken.None);

        // Assert
        Assert.That(result.SuccessCount, Is.EqualTo(1));
        scope.Verify(s => s.SetManagedScope(It.IsAny<IReadOnlyList<ConnectedSystemContainer>>()), Times.Never);
    }

    /// <summary>
    /// Tests that the ExportExecutionOptions.MaxParallelism defaults to 1 (sequential).
    /// </summary>
    /// <summary>
    /// The deferred pass sends Creates too, once their references resolve, and decides for each of its batches whether
    /// an Initial Password follows exactly as the first pass does (#2039): a Create sent without the flag would be
    /// created enabled on Active Directory and refused there.
    /// </summary>
    [Test]
    public async Task ExecuteExportsAsync_DeferredCreateFromARuleThatAsksForAPassword_IsHandedToTheConnectorFlaggedAsync()
    {
        var targetSystem = ConnectedSystemsData.Single(s => s.Name == "Dummy Target System");
        var targetUserType = ConnectedSystemObjectTypesData.Single(t => t.Name == "TARGET_USER");
        var referencedMvoId = SeedResolvableReferenceTarget(targetSystem, targetUserType);
        SyncRepo.SeedSyncRule(ProvisioningRuleAskingForAPassword(targetSystem));

        var create = SeedResolvableDeferredExport(targetSystem, targetUserType, PendingExportChangeType.Create, DateTime.UtcNow.AddMinutes(-1), referencedMvoId);
        create.ProvisioningSyncRuleId = InitialPasswordRuleId;

        var handedToConnector = new System.Collections.Concurrent.ConcurrentBag<(Guid Id, bool InitialPasswordFollows)>();
        var connector = CreateCapturingConnector(handedToConnector);

        await Jim.ExportExecution.ExecuteExportsAsync(targetSystem, connector.Object, SyncRunMode.PreviewAndSync,
            new ExportExecutionOptions { BatchSize = 10, MaxParallelism = 1 }, CancellationToken.None);

        Assert.That(handedToConnector.Where(h => h.Id == create.Id).Select(h => h.InitialPasswordFollows), Is.EqualTo(new[] { true }));
    }

    /// <summary>
    /// Parallel deferred batches re-load their Pending Exports on a repository of their own, so each batch must make
    /// the decision against that repository; one deciding only on the primary would hand a fresh, unflagged copy to
    /// every connector but the first (#2039).
    /// </summary>
    [Test]
    public async Task ExecuteExportsAsync_ParallelDeferredBatches_CreateFromARuleThatAsksForAPasswordIsFlaggedOnEveryConnectorAsync()
    {
        var targetSystem = ConnectedSystemsData.Single(s => s.Name == "Dummy Target System");
        var targetUserType = ConnectedSystemObjectTypesData.Single(t => t.Name == "TARGET_USER");
        var referencedMvoId = SeedResolvableReferenceTarget(targetSystem, targetUserType);
        var rule = ProvisioningRuleAskingForAPassword(targetSystem);
        SyncRepo.SeedSyncRule(rule);

        var baseTime = DateTime.UtcNow.AddMinutes(-10);
        var creates = Enumerable.Range(0, 2)
            .Select(i => SeedResolvableDeferredExport(targetSystem, targetUserType, PendingExportChangeType.Create, baseTime.AddSeconds(i), referencedMvoId))
            .ToList();
        foreach (var create in creates)
            create.ProvisioningSyncRuleId = InitialPasswordRuleId;

        var handedToConnector = new System.Collections.Concurrent.ConcurrentBag<(Guid Id, bool InitialPasswordFollows)>();
        var connectorFactoryCalled = false;
        Func<IConnector> connectorFactory = () =>
        {
            connectorFactoryCalled = true;
            return CreateCapturingConnector(handedToConnector).Object;
        };
        Func<ISyncRepositoryScope> repositoryFactory = () =>
            new SyncRepositoryScope(TestUtilities.CreateSyncRepository(pendingExports: PendingExportsData, syncRules: [rule]));

        await Jim.ExportExecution.ExecuteExportsAsync(targetSystem, CreateCapturingConnector(handedToConnector).Object, SyncRunMode.PreviewAndSync,
            new ExportExecutionOptions { BatchSize = 1, MaxParallelism = 2 }, CancellationToken.None,
            connectorFactory: connectorFactory, repositoryFactory: repositoryFactory);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(connectorFactoryCalled, Is.True, "precondition: the second batch ran on a connector of its own");
            Assert.That(handedToConnector.Select(h => h.Id), Is.EquivalentTo(creates.Select(c => c.Id)));
            Assert.That(handedToConnector.Select(h => h.InitialPasswordFollows), Is.All.True);
        }
    }

    /// <summary>
    /// An Update to an account still owed a password is flagged on the instances each parallel batch re-loaded, through
    /// the batch's own repository (#2158): the owed passwords exist only there, so a decision made on the primary's
    /// repository, or on the primary's copies, would hand every Connector an unflagged Update that Active Directory then
    /// refuses.
    /// </summary>
    [Test]
    public async Task ExecuteExportsAsync_ParallelBatch_UpdateToAnAccountOwedAPassword_IsFlaggedOnTheBatchsOwnInstancesAsync()
    {
        var targetSystem = ConnectedSystemsData.Single(s => s.Name == "Dummy Target System");
        var targetUserType = ConnectedSystemObjectTypesData.Single(t => t.Name == "TARGET_USER");
        var referencedMvoId = SeedResolvableReferenceTarget(targetSystem, targetUserType);

        var baseTime = DateTime.UtcNow.AddMinutes(-10);
        var updates = Enumerable.Range(0, 2)
            .Select(i => SeedResolvableDeferredExport(targetSystem, targetUserType, PendingExportChangeType.Update, baseTime.AddSeconds(i), referencedMvoId))
            .ToList();
        foreach (var update in updates)
            update.ConnectedSystemObject!.MetaverseObjectId = Guid.NewGuid();

        var handedToConnector = new System.Collections.Concurrent.ConcurrentBag<(Guid Id, bool InitialPasswordFollows)>();
        var connectorFactoryCalled = false;
        Func<IConnector> connectorFactory = () =>
        {
            connectorFactoryCalled = true;
            return CreateCapturingConnector(handedToConnector, setsPasswords: true).Object;
        };
        Func<ISyncRepositoryScope> repositoryFactory = () =>
        {
            var batchRepository = TestUtilities.CreateSyncRepository(pendingExports: PendingExportsData);
            batchRepository.QueuePasswordChangesAsync(updates.Select(update => OwedPassword(targetSystem.Id, update.ConnectedSystemObject!)).ToList())
                .GetAwaiter().GetResult();
            return new SyncRepositoryScope(batchRepository);
        };

        await Jim.ExportExecution.ExecuteExportsAsync(targetSystem, CreateCapturingConnector(handedToConnector, setsPasswords: true).Object, SyncRunMode.PreviewAndSync,
            new ExportExecutionOptions { BatchSize = 1, MaxParallelism = 2 }, CancellationToken.None,
            connectorFactory: connectorFactory, repositoryFactory: repositoryFactory);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(connectorFactoryCalled, Is.True, "precondition: the second batch ran on a connector of its own");
            Assert.That(handedToConnector.Select(h => h.Id), Is.EquivalentTo(updates.Select(u => u.Id)));
            Assert.That(handedToConnector.Select(h => h.InitialPasswordFollows), Is.All.True);
            Assert.That(SyncRepo.GetMetaverseObjectIdsOwedPasswordCallCount, Is.Zero, "decided through each batch's own repository, never the primary's");
        }
    }

    private static PendingPasswordChange OwedPassword(int connectedSystemId, ConnectedSystemObject account) => new()
    {
        Id = Guid.NewGuid(),
        MetaverseObjectId = account.MetaverseObjectId!.Value,
        ConnectedSystemId = connectedSystemId,
        ConnectedSystemObjectId = account.Id,
        Origin = PendingPasswordChangeOrigin.Provisioned,
        Status = PendingPasswordChangeStatus.Parked,
        CreatedAt = DateTime.UtcNow,
        ExpiresAt = DateTime.UtcNow.AddDays(7),
        ActivityId = Guid.NewGuid()
    };

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

    #region Batch interruption (#2083)

    /// <summary>
    /// #2083: a run cancelled while the Connector held a batch left every Pending Export in it Executing, which neither
    /// the export queue nor import reconciliation reads, until the Worker restarted. Nothing was recorded for the batch,
    /// so it must go back exactly as it was before it was sent, with no attempt counted.
    /// </summary>
    [Test]
    public void ExecuteExportsAsync_CancelledWhileTheConnectorHoldsABatch_PutsTheBatchBackAsItWas()
    {
        var targetSystem = ConnectedSystemsData.Single(s => s.Name == "Dummy Target System");
        var exports = SeedExportsRetriedOnce(targetSystem, 3);
        var before = exports.Select(PendingExportRetryState.Of).ToList();

        using var cts = new CancellationTokenSource();
        var connector = CreateScriptedConnector((_, _) =>
        {
            cts.Cancel();
            throw new OperationCanceledException(cts.Token);
        });

        Assert.ThrowsAsync<OperationCanceledException>(async () => await Jim.ExportExecution.ExecuteExportsAsync(
            targetSystem, connector.Object, SyncRunMode.PreviewAndSync, new ExportExecutionOptions { BatchSize = 100 }, cts.Token));

        Assert.That(exports.Select(PendingExportRetryState.Of), Is.EqualTo(before));
    }

    /// <summary>
    /// #2083: a Connector that stops at a safe point on cancellation reports what it wrote and marks the rest not
    /// attempted. What it wrote is recorded (a Create's External ID, its execution item), and what it never started goes
    /// back as it was, uncounted and with no execution item, so the next run carries on without resending a write that
    /// already happened. The run then stops before the next batch.
    /// </summary>
    [Test]
    public async Task ExecuteExportsAsync_ConnectorReportsTheRestNotAttemptedOnCancellation_RecordsWhatWasWrittenAndPutsTheRestBackAsync()
    {
        var targetSystem = ConnectedSystemsData.Single(s => s.Name == "Dummy Target System");
        var exports = SeedFreshExports(targetSystem, 5);
        var before = exports.ToDictionary(pe => pe.Id, PendingExportRetryState.Of);

        using var cts = new CancellationTokenSource();
        var calls = 0;
        var connector = CreateScriptedConnector((sent, _) =>
        {
            calls++;
            cts.Cancel();
            return sent.Select((_, i) => i == 0 ? ConnectedSystemExportResult.Succeeded() : ConnectedSystemExportResult.NotAttempted()).ToList();
        });
        var recorded = new List<ProcessedExportItem>();

        Assert.ThrowsAsync<OperationCanceledException>(async () => await Jim.ExportExecution.ExecuteExportsAsync(
            targetSystem, connector.Object, SyncRunMode.PreviewAndSync, new ExportExecutionOptions { BatchSize = 3 }, cts.Token,
            batchCompletedCallback: items => { recorded.AddRange(items); return Task.CompletedTask; }));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(calls, Is.EqualTo(1), "the run stops before sending another batch");
            Assert.That(exports[0].Status, Is.EqualTo(PendingExportStatus.Exported));
            Assert.That(exports.Skip(1).Select(PendingExportRetryState.Of), Is.EqualTo(exports.Skip(1).Select(pe => before[pe.Id])),
                "the changes never started, and the batch never sent, are as they were");
            Assert.That(recorded.Select(item => (item.PendingExportId, item.Succeeded)), Is.EqualTo(new[] { ((Guid?)exports[0].Id, true) }),
                "one execution item, for the change that was written");
        }
    }

    /// <summary>
    /// #2083: the deferred pass had no handling at all. A run cancelled part-way through it left the interrupted batch
    /// Executing, and the execution items of the deferred batches already written, which are only handed on once the pass
    /// ends, were never recorded.
    /// </summary>
    [Test]
    public void ExecuteExportsAsync_CancelledDuringTheDeferredPass_RecordsTheBatchesWrittenAndPutsTheInterruptedOneBack()
    {
        var targetSystem = ConnectedSystemsData.Single(s => s.Name == "Dummy Target System");
        var targetUserType = ConnectedSystemObjectTypesData.Single(t => t.Name == "TARGET_USER");
        var exports = SeedResolvableDeferredExports(targetSystem, targetUserType, 4);

        using var cts = new CancellationTokenSource();
        var connector = CreateScriptedConnector((sent, call) =>
        {
            if (call == 1)
                return sent.Select(_ => ConnectedSystemExportResult.Succeeded()).ToList();
            cts.Cancel();
            throw new OperationCanceledException(cts.Token);
        });
        var recorded = new List<ProcessedExportItem>();

        Assert.ThrowsAsync<OperationCanceledException>(async () => await Jim.ExportExecution.ExecuteExportsAsync(
            targetSystem, connector.Object, SyncRunMode.PreviewAndSync, new ExportExecutionOptions { BatchSize = 2, MaxParallelism = 1 }, cts.Token,
            batchCompletedCallback: items => { recorded.AddRange(items); return Task.CompletedTask; }));

        var written = exports.Where(pe => pe.Status == PendingExportStatus.Exported).ToList();
        var interrupted = exports.Except(written).ToList();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(written, Has.Count.EqualTo(2));
            Assert.That(recorded.Select(item => item.PendingExportId), Is.EquivalentTo(written.Select(pe => (Guid?)pe.Id)),
                "the first deferred batch's execution items are recorded");
            Assert.That(interrupted, Has.All.Matches<PendingExport>(pe => pe.Status == PendingExportStatus.Pending && pe.ErrorCount == 0),
                "the interrupted batch goes back to Pending, uncounted");
        }
    }

    /// <summary>
    /// #2083: a deferred batch whose Connector threw escaped the deferred pass with its Pending Exports still Executing.
    /// It is a failed attempt like any other batch failure: counted, scheduled for a retry, and reported per change.
    /// </summary>
    [Test]
    public async Task ExecuteExportsAsync_DeferredBatchConnectorThrows_FailsTheBatchAndLeavesNothingExecutingAsync()
    {
        var targetSystem = ConnectedSystemsData.Single(s => s.Name == "Dummy Target System");
        var targetUserType = ConnectedSystemObjectTypesData.Single(t => t.Name == "TARGET_USER");
        var exports = SeedResolvableDeferredExports(targetSystem, targetUserType, 4);

        var connector = CreateScriptedConnector((sent, call) => call == 1
            ? sent.Select(_ => ConnectedSystemExportResult.Succeeded()).ToList()
            : throw new InvalidOperationException("The directory went away"));
        var recorded = new List<ProcessedExportItem>();

        var result = await Jim.ExportExecution.ExecuteExportsAsync(
            targetSystem, connector.Object, SyncRunMode.PreviewAndSync, new ExportExecutionOptions { BatchSize = 2, MaxParallelism = 1 }, CancellationToken.None,
            batchCompletedCallback: items => { recorded.AddRange(items); return Task.CompletedTask; });

        var failed = exports.Where(pe => pe.Status != PendingExportStatus.Exported).ToList();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(failed, Has.Count.EqualTo(2));
            Assert.That(failed, Has.All.Matches<PendingExport>(pe =>
                pe.Status == PendingExportStatus.Pending && pe.ErrorCount == 1 && pe.LastErrorMessage!.Contains("The directory went away")));
            Assert.That(result.FailedCount, Is.EqualTo(2));
            Assert.That(recorded.Where(item => !item.Succeeded).Select(item => (item.PendingExportId, item.ConnectedSystemObject)),
                Is.EquivalentTo(failed.Select(pe => ((Guid?)pe.Id, pe.ConnectedSystemObject))),
                "each failure names its Pending Export and its Connected System Object");
        }
    }

    /// <summary>
    /// #2083: a parallel batch that threw was counted as failed, with execution items naming no object, but its Pending
    /// Exports were never written: they stayed Executing until the Worker restarted.
    /// </summary>
    [Test]
    public async Task ExecuteExportsAsync_ParallelDeferredBatchThrows_RecordsAFailedAttemptOnEachOfItsPendingExportsAsync()
    {
        var targetSystem = ConnectedSystemsData.Single(s => s.Name == "Dummy Target System");
        var targetUserType = ConnectedSystemObjectTypesData.Single(t => t.Name == "TARGET_USER");
        var exports = SeedResolvableDeferredExports(targetSystem, targetUserType, 4);

        var primaryConnector = CreateMockConnector(ConnectedSystemExportResult.Succeeded());
        Func<IConnector> connectorFactory = () => CreateScriptedConnector((_, _) => throw new InvalidOperationException("The directory went away")).Object;
        Func<ISyncRepositoryScope> repositoryFactory = () => new SyncRepositoryScope(TestUtilities.CreateSyncRepository(pendingExports: PendingExportsData));

        var result = await Jim.ExportExecution.ExecuteExportsAsync(
            targetSystem, primaryConnector.Object, SyncRunMode.PreviewAndSync, new ExportExecutionOptions { BatchSize = 2, MaxParallelism = 2 },
            CancellationToken.None, connectorFactory: connectorFactory, repositoryFactory: repositoryFactory);

        var failed = exports.Where(pe => pe.Status != PendingExportStatus.Exported).ToList();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(failed, Has.Count.EqualTo(2));
            Assert.That(failed, Has.All.Matches<PendingExport>(pe =>
                pe.Status == PendingExportStatus.Pending && pe.ErrorCount == 1 && pe.LastErrorMessage!.Contains("The directory went away")));
            Assert.That(result.FailedCount, Is.EqualTo(2));
            Assert.That(result.ProcessedExportItems.Where(item => !item.Succeeded).Select(item => item.PendingExportId),
                Is.EquivalentTo(failed.Select(pe => (Guid?)pe.Id)));
        }
    }

    /// <summary>
    /// #2197: a parallel batch whose Connector created a container and then failed still hands the container on to be
    /// selected. Captured only from a batch that succeeded, the container of a failed one was lost when its Connector
    /// was closed and disposed, and the accounts created in it stayed unconfirmed.
    /// </summary>
    [Test]
    public async Task ExecuteExportsAsync_ParallelDeferredBatchCreatesAContainerThenThrows_StillReportsTheContainerAsync()
    {
        var targetSystem = ConnectedSystemsData.Single(s => s.Name == "Dummy Target System");
        var targetUserType = ConnectedSystemObjectTypesData.Single(t => t.Name == "TARGET_USER");
        SeedResolvableDeferredExports(targetSystem, targetUserType, 4);

        var primaryConnector = CreateMockConnector(ConnectedSystemExportResult.Succeeded());
        Func<IConnector> connectorFactory = () =>
        {
            var created = new List<string>();
            var connector = CreateScriptedConnector((_, _) =>
            {
                created.Add("OU=New,DC=panoply,DC=local");
                throw new InvalidOperationException("The directory went away");
            });
            connector.As<IConnectorContainerCreation>().Setup(c => c.CreatedContainerExternalIds).Returns(() => created.ToList());
            return connector.Object;
        };
        Func<ISyncRepositoryScope> repositoryFactory = () => new SyncRepositoryScope(TestUtilities.CreateSyncRepository(pendingExports: PendingExportsData));
        var reported = new List<string>();

        var result = await Jim.ExportExecution.ExecuteExportsAsync(
            targetSystem, primaryConnector.Object, SyncRunMode.PreviewAndSync, new ExportExecutionOptions { BatchSize = 2, MaxParallelism = 2 },
            CancellationToken.None, connectorFactory: connectorFactory, repositoryFactory: repositoryFactory,
            containersCreated: reported.AddRange);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.FailedCount, Is.EqualTo(2), "the batch failed");
            Assert.That(result.CreatedContainerExternalIds, Is.EqualTo(new[] { "OU=New,DC=panoply,DC=local" }));
            Assert.That(reported, Is.EqualTo(new[] { "OU=New,DC=panoply,DC=local" }));
        }
    }

    /// <summary>
    /// #2083: a parallel batch cancelled while its Connector held it was left Executing.
    /// </summary>
    [Test]
    public void ExecuteExportsAsync_ParallelDeferredBatchCancelled_LeavesNothingExecuting()
    {
        var targetSystem = ConnectedSystemsData.Single(s => s.Name == "Dummy Target System");
        var targetUserType = ConnectedSystemObjectTypesData.Single(t => t.Name == "TARGET_USER");
        var exports = SeedResolvableDeferredExports(targetSystem, targetUserType, 4);

        using var cts = new CancellationTokenSource();
        var primaryConnector = CreateMockConnector(ConnectedSystemExportResult.Succeeded());
        Func<IConnector> connectorFactory = () => CreateScriptedConnector((_, _) =>
        {
            cts.Cancel();
            throw new OperationCanceledException(cts.Token);
        }).Object;
        Func<ISyncRepositoryScope> repositoryFactory = () => new SyncRepositoryScope(TestUtilities.CreateSyncRepository(pendingExports: PendingExportsData));

        var before = exports.ToDictionary(pe => pe.Id, PendingExportRetryState.Of);

        Assert.CatchAsync<OperationCanceledException>(async () => await Jim.ExportExecution.ExecuteExportsAsync(
            targetSystem, primaryConnector.Object, SyncRunMode.PreviewAndSync, new ExportExecutionOptions { BatchSize = 2, MaxParallelism = 2 },
            cts.Token, connectorFactory: connectorFactory, repositoryFactory: repositoryFactory));

        var notWritten = exports.Where(pe => pe.Status != PendingExportStatus.Exported).ToList();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(notWritten, Is.Not.Empty, "the cancelled batch is among them");
            Assert.That(notWritten.Select(PendingExportRetryState.Of), Is.EqualTo(notWritten.Select(pe => before[pe.Id])),
                "every change not written is exactly as it was before the run");
        }
    }

    /// <summary>
    /// #2083: a Connector reporting a change not attempted while the run carries on is not exporting it. Putting it back
    /// silently would repeat that every run with nothing to show for it; it is a failed attempt with an execution item.
    /// </summary>
    [Test]
    public async Task ExecuteExportsAsync_ConnectorReportsNotAttemptedWithoutTheRunBeingCancelled_CountsAFailedAttemptAsync()
    {
        var targetSystem = ConnectedSystemsData.Single(s => s.Name == "Dummy Target System");
        var exports = SeedFreshExports(targetSystem, 3);
        var connector = CreateScriptedConnector((sent, _) =>
            sent.Select((_, i) => i == 0 ? ConnectedSystemExportResult.Succeeded() : ConnectedSystemExportResult.NotAttempted()).ToList());

        var result = await Jim.ExportExecution.ExecuteExportsAsync(
            targetSystem, connector.Object, SyncRunMode.PreviewAndSync, new ExportExecutionOptions { BatchSize = 100 }, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(exports[0].Status, Is.EqualTo(PendingExportStatus.Exported));
            Assert.That(exports.Skip(1), Has.All.Matches<PendingExport>(pe =>
                pe.Status == PendingExportStatus.Pending && pe.ErrorCount == 1 && pe.LastErrorMessage!.Contains("not attempted")));
            Assert.That(result.FailedCount, Is.EqualTo(2));
            Assert.That(result.ProcessedExportItems.Where(item => !item.Succeeded).Select(item => item.PendingExportId),
                Is.EquivalentTo(exports.Skip(1).Select(pe => (Guid?)pe.Id)));
        }
    }

    /// <summary>
    /// #2197: a batch's results are recorded before anything that processes them further can fail. Collision Remediation
    /// failing for one rejected change used to fail the whole batch before any result was recorded, so a change the
    /// Connector had written was counted as a failed attempt and sent again (a Create, a second time).
    /// </summary>
    [Test]
    public async Task ExecuteExportsAsync_CollisionRemediationFailsForOneChange_RecordsTheOthersResultsFirstAsync()
    {
        var targetSystem = ConnectedSystemsData.Single(s => s.Name == "Dummy Target System");
        targetSystem.ConnectorDefinition.SupportsUniquenessRejectionClassification = true;
        var exports = SeedFreshExports(targetSystem, 2);
        var written = new HashSet<Guid>();
        SyncRepo.OnPendingExportsUpdated = ids => written.UnionWith(ids);
        SyncRepo.FailSyncRuleLoadWith = new InvalidOperationException("The Synchronisation Rules could not be read.");
        var rejection = ConnectedSystemExportResult.Failed("That value is already in use.");
        rejection.ErrorType = ConnectedSystemExportErrorType.UniqueValueAlreadyInUse;
        var connector = CreateScriptedConnector((_, _) => [ConnectedSystemExportResult.Succeeded(), rejection]);

        var result = await Jim.ExportExecution.ExecuteExportsAsync(
            targetSystem, connector.Object, SyncRunMode.PreviewAndSync, new ExportExecutionOptions { BatchSize = 100 }, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(written, Does.Contain(exports[0].Id), "the written change's result is recorded");
            Assert.That(result.ProcessedExportItems.Where(item => item.Succeeded).Select(item => item.PendingExportId),
                Is.EqualTo(new[] { (Guid?)exports[0].Id }));
            Assert.That(result.ProcessedExportItems.Where(item => !item.Succeeded).Select(item => item.PendingExportId),
                Is.EqualTo(new[] { (Guid?)exports[1].Id }), "only the change whose remediation failed is reported failed");
        }
    }

    /// <summary>
    /// #2197: a Connector that returns fewer results than it was handed changes has not said what became of the rest, and
    /// a change JIM cannot account for is never read as a success: that would confirm a write nobody made. Each missing
    /// result is a failed attempt, with an execution item.
    /// </summary>
    [Test]
    public async Task ExecuteExportsAsync_ConnectorReturnsTooFewResults_CountsEachMissingOneAsAFailedAttemptAsync()
    {
        var targetSystem = ConnectedSystemsData.Single(s => s.Name == "Dummy Target System");
        var exports = SeedFreshExports(targetSystem, 3);
        var connector = CreateScriptedConnector((_, _) => [ConnectedSystemExportResult.Succeeded()]);

        var result = await Jim.ExportExecution.ExecuteExportsAsync(
            targetSystem, connector.Object, SyncRunMode.PreviewAndSync, new ExportExecutionOptions { BatchSize = 100 }, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(exports[0].Status, Is.EqualTo(PendingExportStatus.Exported));
            Assert.That(exports.Skip(1), Has.All.Matches<PendingExport>(pe =>
                pe.Status == PendingExportStatus.Pending && pe.ErrorCount == 1 && pe.LastErrorMessage!.Contains("no result")));
            Assert.That(result.SuccessCount, Is.EqualTo(1));
            Assert.That(result.FailedCount, Is.EqualTo(2));
        }
    }

    /// <summary>
    /// #2197: when the run has been cancelled, a change the Connector returned no result for is taken to be one it never
    /// started: it goes back as it was, uncounted, as a change the Connector reports not attempted does (#2083).
    /// </summary>
    [Test]
    public void ExecuteExportsAsync_ConnectorReturnsTooFewResultsOnCancellation_PutsTheMissingOnesBackAsItWas()
    {
        var targetSystem = ConnectedSystemsData.Single(s => s.Name == "Dummy Target System");
        var exports = SeedFreshExports(targetSystem, 3);
        var before = exports.ToDictionary(pe => pe.Id, PendingExportRetryState.Of);

        using var cts = new CancellationTokenSource();
        var connector = CreateScriptedConnector((_, _) =>
        {
            cts.Cancel();
            return [ConnectedSystemExportResult.Succeeded()];
        });

        Assert.ThrowsAsync<OperationCanceledException>(async () => await Jim.ExportExecution.ExecuteExportsAsync(
            targetSystem, connector.Object, SyncRunMode.PreviewAndSync, new ExportExecutionOptions { BatchSize = 100 }, cts.Token));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(exports[0].Status, Is.EqualTo(PendingExportStatus.Exported));
            Assert.That(exports.Skip(1).Select(PendingExportRetryState.Of), Is.EqualTo(exports.Skip(1).Select(pe => before[pe.Id])));
        }
    }

    /// <summary>
    /// #2083: a parallel batch whose Connector fails, and then fails again as its connection is closed, is one failed
    /// batch: its failure is recorded once, and the close failure is reported beside it rather than as the batch failing
    /// a second time with execution items naming no object.
    /// </summary>
    [Test]
    public async Task ExecuteExportsAsync_ParallelBatchFailsAndItsConnectionFailsToClose_RecordsTheFailureOnceAsync()
    {
        var targetSystem = ConnectedSystemsData.Single(s => s.Name == "Dummy Target System");
        var targetUserType = ConnectedSystemObjectTypesData.Single(t => t.Name == "TARGET_USER");
        var exports = SeedResolvableDeferredExports(targetSystem, targetUserType, 4);

        var primaryConnector = CreateMockConnector(ConnectedSystemExportResult.Succeeded());
        Func<IConnector> connectorFactory = () => CreateScriptedConnector(
            (_, _) => throw new InvalidOperationException("The directory went away"),
            closeFailure: new InvalidOperationException("The connection was already broken")).Object;
        Func<ISyncRepositoryScope> repositoryFactory = () => new SyncRepositoryScope(TestUtilities.CreateSyncRepository(pendingExports: PendingExportsData));

        var result = await Jim.ExportExecution.ExecuteExportsAsync(
            targetSystem, primaryConnector.Object, SyncRunMode.PreviewAndSync, new ExportExecutionOptions { BatchSize = 2, MaxParallelism = 2 },
            CancellationToken.None, connectorFactory: connectorFactory, repositoryFactory: repositoryFactory);

        var failed = exports.Where(pe => pe.Status != PendingExportStatus.Exported).ToList();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(failed, Has.Count.EqualTo(2));
            Assert.That(result.FailedCount, Is.EqualTo(2), "the batch's failure is counted once");
            Assert.That(result.ProcessedExportItems.Count(item => !item.Succeeded && item.PendingExportId == null), Is.EqualTo(1),
                "the close failure is reported once, beside the batch's own items");
        }
    }

    /// <summary>
    /// #2083: an <see cref="OperationCanceledException"/> nobody asked for, such as an HTTP client's timeout, was taken
    /// for the run being cancelled: the batch was left Executing and the run ended as "Export cancelled". It is a failed
    /// attempt at the batch.
    /// </summary>
    [Test]
    public async Task ExecuteExportsAsync_ConnectorTimesOutWithoutTheRunBeingCancelled_FailsTheBatchRatherThanCancellingTheRunAsync()
    {
        var targetSystem = ConnectedSystemsData.Single(s => s.Name == "Dummy Target System");
        var exports = SeedFreshExports(targetSystem, 3);
        var connector = CreateScriptedConnector((_, _) =>
            throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout of 100 seconds elapsing."));

        var result = await Jim.ExportExecution.ExecuteExportsAsync(
            targetSystem, connector.Object, SyncRunMode.PreviewAndSync, new ExportExecutionOptions { BatchSize = 100 }, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(exports, Has.All.Matches<PendingExport>(pe =>
                pe.Status == PendingExportStatus.Pending && pe.ErrorCount == 1 && pe.LastErrorMessage!.Contains("HttpClient.Timeout")));
            Assert.That(result.FailedCount, Is.EqualTo(3));
            Assert.That(result.ProcessedExportItems.Select(item => item.PendingExportId), Is.EquivalentTo(exports.Select(pe => (Guid?)pe.Id)));
        }
    }

    #endregion

    #region Helper Methods

    /// <summary>
    /// Seeds a Metaverse Object with a joined CSO carrying an external identifier, so a deferred export
    /// referencing it resolves on the second pass.
    /// </summary>
    private Guid SeedResolvableReferenceTarget(ConnectedSystem targetSystem, ConnectedSystemObjectType targetUserType)
    {
        var objectGuidAttr = targetUserType.Attributes.Single(a => a.Name == MockTargetSystemAttributeNames.ObjectGuid.ToString());
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
        return referencedMvoId;
    }

    /// <summary>
    /// Seeds a Pending Export whose reference is unresolved on the first pass but resolvable against
    /// <paramref name="referencedMvoId"/>, so it is written by the deferred pass.
    /// </summary>
    private PendingExport SeedResolvableDeferredExport(
        ConnectedSystem targetSystem,
        ConnectedSystemObjectType targetUserType,
        PendingExportChangeType changeType,
        DateTime createdAt,
        Guid referencedMvoId)
    {
        var displayNameAttr = targetUserType.Attributes.Single(a => a.Name == MockTargetSystemAttributeNames.DisplayName.ToString());
        var managerAttr = targetUserType.Attributes.Single(a => a.Name == MockTargetSystemAttributeNames.Manager.ToString());

        var cso = CreateCso(targetSystem, targetUserType);
        ConnectedSystemObjectsData.Add(cso);
        SyncRepo.SeedConnectedSystemObject(cso);

        var export = new PendingExport
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = targetSystem.Id,
            ConnectedSystem = targetSystem,
            ConnectedSystemObject = cso,
            ConnectedSystemObjectId = cso.Id,
            Status = PendingExportStatus.Pending,
            ChangeType = changeType,
            CreatedAt = createdAt,
            HasUnresolvedReferences = true,
            AttributeValueChanges = new List<PendingExportAttributeValueChange>
            {
                new()
                {
                    Id = Guid.NewGuid(),
                    ChangeType = PendingExportAttributeChangeType.Update,
                    AttributeId = displayNameAttr.Id,
                    Attribute = displayNameAttr,
                    StringValue = "Resolvable",
                    Status = PendingExportAttributeChangeStatus.Pending
                },
                new()
                {
                    Id = Guid.NewGuid(),
                    ChangeType = PendingExportAttributeChangeType.Update,
                    AttributeId = managerAttr.Id,
                    Attribute = managerAttr,
                    UnresolvedReferenceValue = referencedMvoId.ToString(),
                    Status = PendingExportAttributeChangeStatus.Pending
                }
            }
        };
        PendingExportsData.Add(export);
        SyncRepo.SeedPendingExport(export);
        return export;
    }

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

    private const int InitialPasswordRuleId = 92039;

    private static SyncRule ProvisioningRuleAskingForAPassword(ConnectedSystem targetSystem) => new()
    {
        Id = InitialPasswordRuleId,
        Name = "Provision Users with an Initial Password",
        Direction = SyncRuleDirection.Export,
        ConnectedSystemId = targetSystem.Id,
        ProvisionToConnectedSystem = true,
        InitialPassword = new SyncRuleInitialPassword { SyncRuleId = InitialPasswordRuleId, Enabled = true }
    };

    /// <summary>
    /// A connector that succeeds for everything and records, as it is handed each Pending Export, whether an Initial
    /// Password was said to follow it.
    /// </summary>
    private static Mock<IConnector> CreateCapturingConnector(System.Collections.Concurrent.ConcurrentBag<(Guid Id, bool InitialPasswordFollows)> handedToConnector,
        bool setsPasswords = false)
    {
        var mockConnector = new Mock<IConnector>();
        var mockExportConnector = mockConnector.As<IConnectorExportUsingCalls>();
        if (setsPasswords)
            mockConnector.As<IConnectorPasswordManagement>();
        mockConnector.Setup(c => c.Name).Returns("Test Connector");
        mockExportConnector.Setup(c => c.ExportAsync(It.IsAny<IList<PendingExport>>(), It.IsAny<CancellationToken>(), It.IsAny<IConnectorProgress>()))
            .ReturnsAsync((IList<PendingExport> exports, CancellationToken _, IConnectorProgress _) =>
            {
                foreach (var handed in exports.Select(export => (export.Id, export.InitialPasswordFollows)))
                    handedToConnector.Add(handed);
                return exports.Select(_ => ConnectedSystemExportResult.Succeeded()).ToList();
            });
        return mockConnector;
    }

    /// <summary>
    /// A Connector whose every export call is answered by <paramref name="script"/>, given what was sent and which call
    /// this is, counting from one. The script may throw.
    /// </summary>
    private static Mock<IConnector> CreateScriptedConnector(
        Func<IList<PendingExport>, int, List<ConnectedSystemExportResult>> script,
        Exception? closeFailure = null)
    {
        var call = 0;
        var mockConnector = new Mock<IConnector>();
        var mockExportConnector = mockConnector.As<IConnectorExportUsingCalls>();
        mockConnector.Setup(c => c.Name).Returns("Test Connector");
        if (closeFailure != null)
            mockExportConnector.Setup(c => c.CloseExportConnection()).Throws(closeFailure);
        mockExportConnector.Setup(c => c.ExportAsync(It.IsAny<IList<PendingExport>>(), It.IsAny<CancellationToken>(), It.IsAny<IConnectorProgress>()))
            .Returns((IList<PendingExport> exports, CancellationToken _, IConnectorProgress _) =>
                Task.FromResult(script(exports, Interlocked.Increment(ref call))));
        return mockConnector;
    }

    /// <summary>
    /// Seeds <paramref name="count"/> Update exports never yet attempted, queued a second apart so they are sent in order.
    /// </summary>
    private List<PendingExport> SeedFreshExports(ConnectedSystem targetSystem, int count)
    {
        var targetUserType = ConnectedSystemObjectTypesData.Single(t => t.Name == "TARGET_USER");
        var displayNameAttr = targetUserType.Attributes.Single(a => a.Name == MockTargetSystemAttributeNames.DisplayName.ToString());
        var queuedAt = DateTime.UtcNow.AddMinutes(-30);
        var exports = new List<PendingExport>();
        for (var i = 0; i < count; i++)
        {
            var cso = CreateCso(targetSystem, targetUserType);
            ConnectedSystemObjectsData.Add(cso);
            var pe = CreatePendingExport(targetSystem, cso, displayNameAttr, $"Value {i}");
            pe.CreatedAt = queuedAt.AddSeconds(i);
            PendingExportsData.Add(pe);
            SyncRepo.SeedPendingExport(pe);
            exports.Add(pe);
        }
        return exports;
    }

    /// <summary>
    /// Seeds <paramref name="count"/> Update exports that have failed once before, so putting one back has a previous
    /// attempt, the start of its run of failures and an error message to preserve.
    /// </summary>
    private List<PendingExport> SeedExportsRetriedOnce(ConnectedSystem targetSystem, int count)
    {
        var exports = SeedFreshExports(targetSystem, count);
        foreach (var pe in exports)
        {
            pe.ErrorCount = 1;
            pe.LastAttemptedAt = DateTime.UtcNow.AddMinutes(-10);
            pe.FailingSince = DateTime.UtcNow.AddMinutes(-10);
            pe.LastErrorMessage = "An earlier attempt failed";
        }
        return exports;
    }

    /// <summary>
    /// Seeds <paramref name="count"/> deferred Update exports that resolve on the deferred pass, a second apart.
    /// </summary>
    private List<PendingExport> SeedResolvableDeferredExports(ConnectedSystem targetSystem, ConnectedSystemObjectType targetUserType, int count)
    {
        var referencedMvoId = SeedResolvableReferenceTarget(targetSystem, targetUserType);
        var queuedAt = DateTime.UtcNow.AddMinutes(-30);
        return Enumerable.Range(0, count)
            .Select(i => SeedResolvableDeferredExport(targetSystem, targetUserType, PendingExportChangeType.Update, queuedAt.AddSeconds(i), referencedMvoId))
            .ToList();
    }

    private static Mock<IConnector> CreateMockConnector(ConnectedSystemExportResult defaultResult)
    {
        var mockConnector = new Mock<IConnector>();
        var mockExportConnector = mockConnector.As<IConnectorExportUsingCalls>();
        mockConnector.Setup(c => c.Name).Returns("Test Connector");
        mockExportConnector.Setup(c => c.ExportAsync(It.IsAny<IList<PendingExport>>(), It.IsAny<CancellationToken>(), It.IsAny<IConnectorProgress>()))
            .ReturnsAsync((IList<PendingExport> exports, CancellationToken _, IConnectorProgress _) =>
                exports.Select(_ => defaultResult).ToList());
        return mockConnector;
    }

    #endregion
}
