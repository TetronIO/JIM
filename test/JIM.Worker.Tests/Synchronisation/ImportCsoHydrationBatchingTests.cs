// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Connectors.Mock;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.PostgresData;
using JIM.Worker.Tests.Models;
using Microsoft.EntityFrameworkCore;
using MockQueryable.Moq;
using Moq;
using SyncRepository = JIM.InMemoryData.SyncRepository;

namespace JIM.Worker.Tests.Synchronisation;

/// <summary>
/// Tests for issue #988 finding 1: batch CSO hydration during full/delta import.
/// Before the fix, <c>HydrateCsoAsync</c> called <c>GetConnectedSystemObjectsByIdsAsync</c>
/// once per matched import object (a single-element array each time), an N+1 pattern that
/// measured ~893s at 209,984 objects. The fix hydrates a whole page's worth of matched CSO
/// IDs with one (or a handful of chunked) batch call(s) instead.
/// </summary>
[TestFixture]
public class ImportCsoHydrationBatchingTests
{
    #region accessors
    private MetaverseObject InitiatedBy { get; set; } = null!;
    private List<ConnectedSystem> ConnectedSystemsData { get; set; } = null!;
    private Mock<DbSet<ConnectedSystem>> MockDbSetConnectedSystems { get; set; } = null!;
    private List<ConnectedSystemRunProfile> ConnectedSystemRunProfilesData { get; set; } = null!;
    private Mock<DbSet<ConnectedSystemRunProfile>> MockDbSetConnectedSystemRunProfiles { get; set; } = null!;
    private List<ConnectedSystemObjectType> ConnectedSystemObjectTypesData { get; set; } = null!;
    private Mock<DbSet<ConnectedSystemObjectType>> MockDbSetConnectedSystemObjectTypes { get; set; } = null!;
    private List<ConnectedSystemPartition> ConnectedSystemPartitionsData { get; set; } = null!;
    private Mock<DbSet<ConnectedSystemPartition>> MockDbSetConnectedSystemPartitions { get; set; } = null!;
    private List<Activity> ActivitiesData { get; set; } = null!;
    private Mock<DbSet<Activity>> MockDbSetActivities { get; set; } = null!;
    private List<ServiceSetting> ServiceSettingsData { get; set; } = null!;
    private Mock<DbSet<ServiceSetting>> MockDbSetServiceSettings { get; set; } = null!;
    private List<PendingExport> PendingExportsData { get; set; } = null!;
    private Mock<DbSet<PendingExport>> MockDbSetPendingExports { get; set; } = null!;
    private List<ConnectedSystemObject> ConnectedSystemObjectsData { get; set; } = new();
    private Mock<JimDbContext> MockJimDbContext { get; set; } = null!;
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
        InitiatedBy = TestUtilities.GetInitiatedBy();

        ConnectedSystemsData = TestUtilities.GetConnectedSystemData();
        MockDbSetConnectedSystems = ConnectedSystemsData.BuildMockDbSet();

        ConnectedSystemRunProfilesData = TestUtilities.GetConnectedSystemRunProfileData();
        MockDbSetConnectedSystemRunProfiles = ConnectedSystemRunProfilesData.BuildMockDbSet();

        ConnectedSystemObjectTypesData = TestUtilities.GetConnectedSystemObjectTypeData();
        MockDbSetConnectedSystemObjectTypes = ConnectedSystemObjectTypesData.BuildMockDbSet();

        ConnectedSystemPartitionsData = TestUtilities.GetConnectedSystemPartitionData();
        MockDbSetConnectedSystemPartitions = ConnectedSystemPartitionsData.BuildMockDbSet();

        var fullImportRunProfile = ConnectedSystemRunProfilesData[0];
        ActivitiesData = TestUtilities.GetActivityData(fullImportRunProfile.RunType, fullImportRunProfile.Id);
        MockDbSetActivities = ActivitiesData.BuildMockDbSet();

        ServiceSettingsData = TestUtilities.GetServiceSettingsData();
        MockDbSetServiceSettings = ServiceSettingsData.BuildMockDbSet();

        PendingExportsData = new List<PendingExport>();
        MockDbSetPendingExports = PendingExportsData.BuildMockDbSet();

        MockJimDbContext = new Mock<JimDbContext>();
        TestUtilities.SetUpEmptyConnectedSystemGraphMocks(MockJimDbContext);
        MockJimDbContext.Setup(m => m.Activities).Returns(MockDbSetActivities.Object);
        MockJimDbContext.Setup(m => m.ConnectedSystems).Returns(MockDbSetConnectedSystems.Object);
        MockJimDbContext.Setup(m => m.ConnectedSystemObjectTypes).Returns(MockDbSetConnectedSystemObjectTypes.Object);
        MockJimDbContext.Setup(m => m.ConnectedSystemRunProfiles).Returns(MockDbSetConnectedSystemRunProfiles.Object);
        MockJimDbContext.Setup(m => m.ConnectedSystemPartitions).Returns(MockDbSetConnectedSystemPartitions.Object);
        MockJimDbContext.Setup(m => m.ServiceSettingItems).Returns(MockDbSetServiceSettings.Object);
        MockJimDbContext.Setup(m => m.PendingExports).Returns(MockDbSetPendingExports.Object);

        ConnectedSystemObjectsData = new List<ConnectedSystemObject>();
    }

    #region N+1 elimination: batched hydration, not per-object

    /// <summary>
    /// Issue #988 finding 1: a re-import of N already-matched objects must hydrate their CSOs
    /// with a small, bounded number of batch calls (one per page-sized chunk of matched IDs),
    /// not N individual single-row round trips.
    /// </summary>
    [Test]
    public async Task ProcessImportObjectsAsync_ReImportOfManyExistingCsos_HydratesCsosInBatchesNotPerObjectAsync()
    {
        // Arrange — seed 2,500 existing CSOs (spans more than one 1,000-id hydration chunk)
        // and re-import all of them with a changed DISPLAY_NAME, so every object matches an
        // existing CSO and requires hydration.
        const int objectCount = 2500;
        var (existingCsos, hrIds) = CreateExistingCsos(objectCount);
        ConnectedSystemObjectsData = existingCsos;
        SetupDbContextWithCsoData();

        var countingRepo = new HydrationCallCountingSyncRepository();
        SyncRepo = TestUtilities.CreateSyncRepository(csos: existingCsos, activity: ActivitiesData.First(), repository: countingRepo);
        Jim = new JimApplication(new PostgresDataRepository(MockJimDbContext.Object), syncRepository: SyncRepo);

        var mockFileConnector = new MockFileConnector();
        for (var i = 0; i < objectCount; i++)
            mockFileConnector.TestImportObjects.Add(CreateImportObject(hrIds[i], $"Updated Name {i}"));

        // Act
        var processor = await CreateProcessorAsync(mockFileConnector);
        await processor.PerformImportAsync();

        // Assert — batched hydration means ceil(2500/1000) = 3 chunked calls, plus a small
        // constant of slack for any incidental calls elsewhere. Nowhere near the 2,500 calls
        // the old per-object hydration would have made.
        const int maxExpectedHydrationCalls = 3 + 2;
        Assert.That(countingRepo.HydrationCalls, Is.LessThanOrEqualTo(maxExpectedHydrationCalls),
            $"Expected batched hydration (<= {maxExpectedHydrationCalls} calls for {objectCount} objects), " +
            $"but GetConnectedSystemObjectsByIdsAsync was called {countingRepo.HydrationCalls} times. See issue #988.");

        // Every object should have been matched and updated in place, not recreated.
        Assert.That(SyncRepo.ConnectedSystemObjects.Count, Is.EqualTo(objectCount),
            "Re-import of existing objects should not create duplicate CSOs.");

        for (var i = 0; i < objectCount; i++)
        {
            var cso = SyncRepo.ConnectedSystemObjects[existingCsos[i].Id];
            var displayName = cso.AttributeValues
                .SingleOrDefault(av => av.Attribute?.Name == MockSourceSystemAttributeNames.DISPLAY_NAME.ToString());
            Assert.That(displayName?.StringValue, Is.EqualTo($"Updated Name {i}"),
                $"CSO {i} should have been updated via the batched hydration path.");
        }
    }

    #endregion

    #region Concurrent deletion fallback: pre-fetch dictionary has a stale ID

    /// <summary>
    /// Issue #988 finding 1: if the pre-fetch dictionary knows about a CSO ID that no longer
    /// exists in the store by the time the batch hydration query runs (e.g. concurrently
    /// deleted), the importer must fall through gracefully - exactly as the previous
    /// per-object hydration's "cache had ID but CSO gone" path did - not throw or corrupt state.
    /// </summary>
    [Test]
    public async Task ProcessImportObjectsAsync_CsoVanishesBeforeBatchHydration_FallsThroughGracefullyAsync()
    {
        // Arrange — one existing CSO, whose ID is known to the pre-fetch external ID lookup
        // dictionary, but the batch hydration call always returns nothing (simulating the CSO
        // having been deleted between the pre-fetch and the hydration query).
        var (existingCsos, hrIds) = CreateExistingCsos(1);
        ConnectedSystemObjectsData = existingCsos;
        SetupDbContextWithCsoData();

        var vanishingRepo = new CsoVanishesAtHydrationSyncRepository();
        SyncRepo = TestUtilities.CreateSyncRepository(csos: existingCsos, activity: ActivitiesData.First(), repository: vanishingRepo);
        Jim = new JimApplication(new PostgresDataRepository(MockJimDbContext.Object), syncRepository: SyncRepo);

        var mockFileConnector = new MockFileConnector();
        mockFileConnector.TestImportObjects.Add(CreateImportObject(hrIds[0], "Re-added After Concurrent Delete"));

        // Act
        var processor = await CreateProcessorAsync(mockFileConnector);
        await processor.PerformImportAsync();

        // Assert — the importer should not throw, and should fall through to treating the
        // object as new (the secondary external ID lookup also finds nothing), exactly as the
        // pre-batching code did for this scenario. The original (now "vanished" from hydration's
        // point of view, but never actually removed from the store) CSO is untouched - imported
        // external IDs are still recorded as "seen", so deletion detection leaves it alone - and
        // a second, brand new CSO is created for the same external ID.
        Assert.That(SyncRepo.ConnectedSystemObjects.Count, Is.EqualTo(2),
            "The original CSO should remain untouched and a new CSO created for the fallen-through object.");
        Assert.That(SyncRepo.ConnectedSystemObjects.ContainsKey(existingCsos[0].Id), Is.True,
            "The original CSO should not have been removed just because hydration could not find it.");
        var newCso = SyncRepo.ConnectedSystemObjects.Values.Single(c => c.Id != existingCsos[0].Id);
        var newDisplayName = newCso.AttributeValues
            .SingleOrDefault(av => av.Attribute?.Name == MockSourceSystemAttributeNames.DISPLAY_NAME.ToString());
        Assert.That(newDisplayName?.StringValue, Is.EqualTo("Re-added After Concurrent Delete"),
            "The newly created CSO should carry the imported object's attribute values.");
    }

    #endregion

    #region Private helpers

    private async Task<JIM.Worker.Processors.SyncImportTaskProcessor> CreateProcessorAsync(MockFileConnector connector)
    {
        var connectedSystem = await Jim.ConnectedSystems.GetConnectedSystemAsync(1);
        Assert.That(connectedSystem, Is.Not.Null, "Expected to retrieve a Connected System.");

        var runProfile = ConnectedSystemRunProfilesData.Single(
            q => q.ConnectedSystemId == connectedSystem!.Id && q.RunType == ConnectedSystemRunType.FullImport);
        var activity = ActivitiesData.First();

        return new JIM.Worker.Processors.SyncImportTaskProcessor(
            Jim, SyncRepo, new JIM.Application.Servers.SyncServer(Jim), new JIM.Application.Servers.SyncEngine(),
            connector, connectedSystem!, runProfile,
            TestUtilities.CreateTestWorkerTask(activity, InitiatedBy),
            new CancellationTokenSource());
    }

    private void SetupDbContextWithCsoData()
    {
        var mockDbSetConnectedSystemObject = ConnectedSystemObjectsData.BuildMockDbSet();
        mockDbSetConnectedSystemObject.Setup(set => set.AddRange(It.IsAny<IEnumerable<ConnectedSystemObject>>()))
            .Callback((IEnumerable<ConnectedSystemObject> entities) =>
            {
                var connectedSystemObjects = entities as ConnectedSystemObject[] ?? entities.ToArray();
                foreach (var entity in connectedSystemObjects)
                    entity.Id = Guid.NewGuid();
                ConnectedSystemObjectsData.AddRange(connectedSystemObjects);
            });
        MockJimDbContext.Setup(m => m.ConnectedSystemObjects).Returns(mockDbSetConnectedSystemObject.Object);
    }

    private static ConnectedSystemImportObject CreateImportObject(Guid hrId, string displayName)
    {
        return new ConnectedSystemImportObject
        {
            ChangeType = ObjectChangeType.NotSet,
            ObjectType = "SOURCE_USER",
            Attributes = new List<ConnectedSystemImportObjectAttribute>
            {
                new()
                {
                    Name = MockSourceSystemAttributeNames.HR_ID.ToString(),
                    GuidValues = new List<Guid> { hrId }
                },
                new()
                {
                    Name = MockSourceSystemAttributeNames.DISPLAY_NAME.ToString(),
                    StringValues = new List<string> { displayName }
                },
                new()
                {
                    Name = MockSourceSystemAttributeNames.EMPLOYEE_ID.ToString(),
                    IntValues = new List<int> { 1 }
                }
            }
        };
    }

    /// <summary>
    /// Creates <paramref name="count"/> existing CSOs with unique HR_ID (external ID) values,
    /// returning the CSOs and their HR_IDs (index-aligned) so a test can re-import them.
    /// </summary>
    private (List<ConnectedSystemObject> csos, List<Guid> hrIds) CreateExistingCsos(int count)
    {
        var connectedSystemObjectType = ConnectedSystemObjectTypesData.First();
        var csos = new List<ConnectedSystemObject>(count);
        var hrIds = new List<Guid>(count);

        for (var i = 0; i < count; i++)
        {
            var hrId = Guid.NewGuid();
            hrIds.Add(hrId);

            var cso = new ConnectedSystemObject
            {
                Id = Guid.NewGuid(),
                ConnectedSystemId = 1,
                ConnectedSystem = ConnectedSystemsData.First(),
                Type = connectedSystemObjectType,
                ExternalIdAttributeId = (int)MockSourceSystemAttributeNames.HR_ID
            };
            cso.AttributeValues = new List<ConnectedSystemObjectAttributeValue>
            {
                new()
                {
                    Id = Guid.NewGuid(),
                    GuidValue = hrId,
                    Attribute = connectedSystemObjectType.Attributes.Single(q => q.Name == MockSourceSystemAttributeNames.HR_ID.ToString()),
                    ConnectedSystemObject = cso
                },
                new()
                {
                    Id = Guid.NewGuid(),
                    StringValue = $"Original Name {i}",
                    Attribute = connectedSystemObjectType.Attributes.Single(q => q.Name == MockSourceSystemAttributeNames.DISPLAY_NAME.ToString()),
                    ConnectedSystemObject = cso
                },
                new()
                {
                    Id = Guid.NewGuid(),
                    IntValue = i,
                    Attribute = connectedSystemObjectType.Attributes.Single(q => q.Name == MockSourceSystemAttributeNames.EMPLOYEE_ID.ToString()),
                    ConnectedSystemObject = cso
                }
            };
            csos.Add(cso);
        }

        return (csos, hrIds);
    }

    /// <summary>
    /// Spy repository that counts calls to <see cref="SyncRepository.GetConnectedSystemObjectsByIdsAsync"/>,
    /// so tests can assert the import pipeline batches CSO hydration instead of calling it once per object.
    /// </summary>
    private sealed class HydrationCallCountingSyncRepository : SyncRepository
    {
        public int HydrationCalls;

        public override Task<List<ConnectedSystemObject>> GetConnectedSystemObjectsByIdsAsync(int connectedSystemId, IEnumerable<Guid> csoIds)
        {
            Interlocked.Increment(ref HydrationCalls);
            return base.GetConnectedSystemObjectsByIdsAsync(connectedSystemId, csoIds);
        }
    }

    /// <summary>
    /// Repository double that always returns no results from the batch hydration call, simulating
    /// a CSO that was concurrently deleted after the pre-fetch external ID dictionary was built but
    /// before the batch hydration query ran.
    /// </summary>
    private sealed class CsoVanishesAtHydrationSyncRepository : SyncRepository
    {
        public override Task<List<ConnectedSystemObject>> GetConnectedSystemObjectsByIdsAsync(int connectedSystemId, IEnumerable<Guid> csoIds)
            => Task.FromResult(new List<ConnectedSystemObject>());
    }

    #endregion
}
