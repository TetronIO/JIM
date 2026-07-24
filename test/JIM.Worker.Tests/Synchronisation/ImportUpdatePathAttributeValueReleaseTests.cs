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
/// Tests for #1079 Regression B: a full-scale validation run (Scale500k25kGroups, 2026-07-21)
/// showed the confirming import's live heap tripled (78.4GB vs a 22.4GB baseline at the same
/// waypoint) after optimistic export apply started keeping update-path CSOs fully hydrated for
/// the whole import run, culminating in an OOM kill. These tests cover the fix: releasing each
/// update-path CSO's AttributeValues once its batch has been persisted, so the ~9.8M attribute
/// value objects a 500K-object run touches don't all stay resident simultaneously.
/// </summary>
[TestFixture]
public class ImportUpdatePathAttributeValueReleaseTests
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

    #region The release seam itself

    /// <summary>
    /// Unit-level proof of the release mechanism in isolation, independent of the rest of the
    /// import pipeline: given a batch of CSOs, every one has its AttributeValues replaced with a
    /// fresh empty list (not mutated in place - other holders of the same instances, such as
    /// PendingAttributeValueRemovals' change-history references, must be unaffected).
    /// </summary>
    [Test]
    public void ReleaseHydratedAttributeValues_ClearsAttributeValuesOnEveryCsoInBatch()
    {
        var cso1 = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            AttributeValues = new List<ConnectedSystemObjectAttributeValue> { new() { Id = Guid.NewGuid() } }
        };
        var cso2 = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            AttributeValues = new List<ConnectedSystemObjectAttributeValue>
            {
                new() { Id = Guid.NewGuid() },
                new() { Id = Guid.NewGuid() }
            }
        };

        JIM.Worker.Processors.SyncImportTaskProcessor.ReleaseHydratedAttributeValues(new List<ConnectedSystemObject> { cso1, cso2 });

        Assert.That(cso1.AttributeValues, Is.Empty);
        Assert.That(cso2.AttributeValues, Is.Empty);
    }

    #endregion

    #region End-to-end: wired into PerformImportAsync

    /// <summary>
    /// After a confirming import updates existing CSOs, the working copies the update-save path
    /// held onto (captured here via a spy repository at the exact point they were persisted) must
    /// no longer retain their AttributeValues once the whole import completes - otherwise a
    /// 500K-object run keeps ~9.8M attribute value objects alive for the rest of the run (#1079
    /// Regression B). This must hold without sacrificing correctness: the store's own canonical
    /// copy must still show the updated values, proving the release only affects the transient
    /// working copy, never the persisted data.
    /// </summary>
    [Test]
    public async Task PerformImportAsync_UpdatesExistingCsos_ReleasesWorkingCopyAttributeValuesAfterPersistingAsync()
    {
        const int objectCount = 5;
        var (existingCsos, hrIds) = CreateExistingCsos(objectCount);
        ConnectedSystemObjectsData = existingCsos;
        SetupDbContextWithCsoData();

        var capturingRepo = new CapturingUpdateSyncRepository();
        SyncRepo = TestUtilities.CreateSyncRepository(csos: existingCsos, activity: ActivitiesData.First(), repository: capturingRepo);
        Jim = new JimApplication(new PostgresDataRepository(MockJimDbContext.Object), syncRepository: SyncRepo);

        var mockFileConnector = new MockFileConnector();
        for (var i = 0; i < objectCount; i++)
            mockFileConnector.TestImportObjects.Add(CreateImportObject(hrIds[i], $"Updated Name {i}"));

        var processor = await CreateProcessorAsync(mockFileConnector);
        await processor.PerformImportAsync();

        // The update-save path must have captured all 5 CSOs, and every one of those exact
        // working-copy instances must have had its AttributeValues released by the time the
        // whole import run completes.
        Assert.That(capturingRepo.CapturedConnectedSystemObjects, Has.Count.EqualTo(objectCount));
        foreach (var captured in capturingRepo.CapturedConnectedSystemObjects)
        {
            Assert.That(captured.AttributeValues, Is.Empty,
                $"CSO {captured.Id}'s working-copy AttributeValues should have been released after its batch was persisted.");
        }

        // Despite the working copy being released, the store's own canonical data must be intact
        // and correctly updated - the release must never reach the persisted copy.
        for (var i = 0; i < objectCount; i++)
        {
            var cso = SyncRepo.ConnectedSystemObjects[existingCsos[i].Id];
            var displayName = cso.AttributeValues
                .SingleOrDefault(av => av.Attribute?.Name == MockSourceSystemAttributeNames.DISPLAY_NAME.ToString());
            Assert.That(displayName?.StringValue, Is.EqualTo($"Updated Name {i}"),
                $"CSO {i}'s persisted copy must retain the updated value despite the working copy being released.");
        }
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
    /// Spy repository that captures every CSO instance handed to
    /// <see cref="SyncRepository.UpdateConnectedSystemObjectsAsync(List{ConnectedSystemObject}, List{(Guid, ConnectedSystemObjectAttributeValue)}?, List{Guid}?)"/>,
    /// so a test can inspect those exact working-copy instances after the import run completes.
    /// </summary>
    private sealed class CapturingUpdateSyncRepository : SyncRepository
    {
        public readonly List<ConnectedSystemObject> CapturedConnectedSystemObjects = new();

        public override Task UpdateConnectedSystemObjectsAsync(
            List<ConnectedSystemObject> connectedSystemObjects,
            List<(Guid CsoId, ConnectedSystemObjectAttributeValue Value)>? pendingAdditions = null,
            List<Guid>? pendingRemovalIds = null)
        {
            CapturedConnectedSystemObjects.AddRange(connectedSystemObjects);
            return base.UpdateConnectedSystemObjectsAsync(connectedSystemObjects, pendingAdditions, pendingRemovalIds);
        }
    }

    #endregion
}
