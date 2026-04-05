using JIM.Application;
using JIM.Application.Servers;
using JIM.Connectors.Mock;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.PostgresData;
using JIM.Worker.Processors;
using JIM.Worker.Tests.Models;
using Microsoft.EntityFrameworkCore;
using MockQueryable.Moq;
using Moq;
using SyncRepository = JIM.InMemoryData.SyncRepository;

namespace JIM.Worker.Tests.Synchronisation;

/// <summary>
/// Tests for the batch pre-fetch import pipeline (#440).
/// Verifies that the lookup (dictionary) + hydration (batch load) approach produces
/// identical results to the previous per-object query approach.
/// </summary>
[TestFixture]
public class ImportBatchPrefetchTests
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
        MockJimDbContext.Setup(m => m.Activities).Returns(MockDbSetActivities.Object);
        MockJimDbContext.Setup(m => m.ConnectedSystems).Returns(MockDbSetConnectedSystems.Object);
        MockJimDbContext.Setup(m => m.ConnectedSystemObjectTypes).Returns(MockDbSetConnectedSystemObjectTypes.Object);
        MockJimDbContext.Setup(m => m.ConnectedSystemRunProfiles).Returns(MockDbSetConnectedSystemRunProfiles.Object);
        MockJimDbContext.Setup(m => m.ConnectedSystemPartitions).Returns(MockDbSetConnectedSystemPartitions.Object);
        MockJimDbContext.Setup(m => m.ServiceSettingItems).Returns(MockDbSetServiceSettings.Object);
        MockJimDbContext.Setup(m => m.PendingExports).Returns(MockDbSetPendingExports.Object);

        ConnectedSystemObjectsData = new List<ConnectedSystemObject>();
    }

    #region First-ever import (empty CS) — dictionary not loaded, all objects created

    [Test]
    public async Task FullImport_EmptyConnectedSystem_CreatesAllObjectsAsync()
    {
        // Arrange — no pre-existing CSOs, so _csIsEmpty=true and dictionary is not loaded
        SetupDbContextWithCsoData();
        SyncRepo = TestUtilities.CreateSyncRepository(activity: ActivitiesData.First());
        Jim = new JimApplication(new PostgresDataRepository(MockJimDbContext.Object), syncRepository: SyncRepo);

        var mockFileConnector = new MockFileConnector();
        mockFileConnector.TestImportObjects.Add(CreateImportObject(TestConstants.CS_OBJECT_1_HR_ID, TestConstants.CS_OBJECT_1_DISPLAY_NAME));
        mockFileConnector.TestImportObjects.Add(CreateImportObject(TestConstants.CS_OBJECT_2_HR_ID, TestConstants.CS_OBJECT_2_DISPLAY_NAME));

        // Act
        var processor = await CreateProcessorAsync(mockFileConnector);
        await processor.PerformImportAsync();

        // Assert — both objects should be created
        Assert.That(SyncRepo.ConnectedSystemObjects.Count, Is.EqualTo(2),
            "First-ever import should create all imported objects.");

        var cso1 = SyncRepo.ConnectedSystemObjects.Values
            .SingleOrDefault(c => c.AttributeValues.Any(av => av.GuidValue == TestConstants.CS_OBJECT_1_HR_ID));
        Assert.That(cso1, Is.Not.Null, "CSO for object 1 should have been created.");

        var cso2 = SyncRepo.ConnectedSystemObjects.Values
            .SingleOrDefault(c => c.AttributeValues.Any(av => av.GuidValue == TestConstants.CS_OBJECT_2_HR_ID));
        Assert.That(cso2, Is.Not.Null, "CSO for object 2 should have been created.");
    }

    #endregion

    #region Re-import with existing CSOs — dictionary finds matches, CSOs updated

    [Test]
    public async Task FullImport_WithExistingCsos_MatchesAndUpdatesViaPreFetchAsync()
    {
        // Arrange — seed two existing CSOs, then re-import with attribute changes
        InitialiseExistingCsos();
        SetupDbContextWithCsoData();
        SyncRepo = TestUtilities.CreateSyncRepository(csos: ConnectedSystemObjectsData, activity: ActivitiesData.First());
        Jim = new JimApplication(new PostgresDataRepository(MockJimDbContext.Object), syncRepository: SyncRepo);

        // Import the same objects with a changed DISPLAY_NAME for object 1
        var mockFileConnector = new MockFileConnector();
        mockFileConnector.TestImportObjects.Add(CreateImportObject(TestConstants.CS_OBJECT_1_HR_ID, "Jane Smith-Updated"));
        mockFileConnector.TestImportObjects.Add(CreateImportObject(TestConstants.CS_OBJECT_2_HR_ID, TestConstants.CS_OBJECT_2_DISPLAY_NAME));

        // Act
        var processor = await CreateProcessorAsync(mockFileConnector);
        await processor.PerformImportAsync();

        // Assert — same two CSOs should exist (no new ones created)
        Assert.That(SyncRepo.ConnectedSystemObjects.Count, Is.EqualTo(2),
            "Re-import should not create new CSOs for existing objects.");

        // Verify the pre-fetch dictionary matched correctly — object 1 was updated, not recreated
        var cso1 = SyncRepo.ConnectedSystemObjects[TestConstants.CS_OBJECT_1_ID];
        Assert.That(cso1, Is.Not.Null, "Original CSO 1 should still exist.");

        var displayName = cso1.AttributeValues
            .SingleOrDefault(av => av.Attribute?.Name == MockSourceSystemAttributeNames.DISPLAY_NAME.ToString());
        Assert.That(displayName?.StringValue, Is.EqualTo("Jane Smith-Updated"),
            "CSO 1 DISPLAY_NAME should have been updated to the new value.");

        // Verify object 2 is unchanged
        var cso2 = SyncRepo.ConnectedSystemObjects[TestConstants.CS_OBJECT_2_ID];
        var displayName2 = cso2.AttributeValues
            .SingleOrDefault(av => av.Attribute?.Name == MockSourceSystemAttributeNames.DISPLAY_NAME.ToString());
        Assert.That(displayName2?.StringValue, Is.EqualTo(TestConstants.CS_OBJECT_2_DISPLAY_NAME),
            "CSO 2 DISPLAY_NAME should be unchanged.");
    }

    #endregion

    #region Mixed import — some new, some existing

    [Test]
    public async Task FullImport_MixedNewAndExisting_CreatesNewAndUpdatesExistingAsync()
    {
        // Arrange — seed one existing CSO, import two objects (one existing, one new)
        InitialiseExistingCsos(singleCsoOnly: true);
        SetupDbContextWithCsoData();
        SyncRepo = TestUtilities.CreateSyncRepository(csos: ConnectedSystemObjectsData, activity: ActivitiesData.First());
        Jim = new JimApplication(new PostgresDataRepository(MockJimDbContext.Object), syncRepository: SyncRepo);

        var mockFileConnector = new MockFileConnector();
        // Object 1 already exists — import with changed display name
        mockFileConnector.TestImportObjects.Add(CreateImportObject(TestConstants.CS_OBJECT_1_HR_ID, "Jane Smith-Updated"));
        // Object 2 is new — should be created
        mockFileConnector.TestImportObjects.Add(CreateImportObject(TestConstants.CS_OBJECT_2_HR_ID, TestConstants.CS_OBJECT_2_DISPLAY_NAME));

        // Act
        var processor = await CreateProcessorAsync(mockFileConnector);
        await processor.PerformImportAsync();

        // Assert — should have 2 CSOs total: 1 updated + 1 newly created
        Assert.That(SyncRepo.ConnectedSystemObjects.Count, Is.EqualTo(2),
            "Mixed import should result in one updated and one new CSO.");

        // Existing CSO was matched and updated (not recreated)
        var cso1 = SyncRepo.ConnectedSystemObjects[TestConstants.CS_OBJECT_1_ID];
        Assert.That(cso1, Is.Not.Null, "Original CSO 1 should still exist with the same ID.");
        var displayName1 = cso1.AttributeValues
            .SingleOrDefault(av => av.Attribute?.Name == MockSourceSystemAttributeNames.DISPLAY_NAME.ToString());
        Assert.That(displayName1?.StringValue, Is.EqualTo("Jane Smith-Updated"),
            "Existing CSO should have been updated.");

        // New CSO was created
        var cso2 = SyncRepo.ConnectedSystemObjects.Values
            .SingleOrDefault(c => c.Id != TestConstants.CS_OBJECT_1_ID);
        Assert.That(cso2, Is.Not.Null, "A new CSO should have been created for object 2.");
        var displayName2 = cso2!.AttributeValues
            .SingleOrDefault(av => av.Attribute?.Name == MockSourceSystemAttributeNames.DISPLAY_NAME.ToString());
        Assert.That(displayName2?.StringValue, Is.EqualTo(TestConstants.CS_OBJECT_2_DISPLAY_NAME),
            "New CSO should have the correct display name.");
    }

    #endregion

    #region Dictionary is updated mid-import when new CSOs are created

    [Test]
    public async Task FullImport_NewCsoCreatedMidBatch_DoesNotDuplicateOnSubsequentPageAsync()
    {
        // Arrange — empty CS, import 3 objects. Verifies that all 3 are correctly created
        // even though the dictionary starts empty. The dictionary should be updated as new
        // CSOs are created so that cross-page deduplication still works.
        SetupDbContextWithCsoData();
        SyncRepo = TestUtilities.CreateSyncRepository(activity: ActivitiesData.First());
        Jim = new JimApplication(new PostgresDataRepository(MockJimDbContext.Object), syncRepository: SyncRepo);

        var mockFileConnector = new MockFileConnector();
        mockFileConnector.TestImportObjects.Add(CreateImportObject(TestConstants.CS_OBJECT_1_HR_ID, TestConstants.CS_OBJECT_1_DISPLAY_NAME));
        mockFileConnector.TestImportObjects.Add(CreateImportObject(TestConstants.CS_OBJECT_2_HR_ID, TestConstants.CS_OBJECT_2_DISPLAY_NAME));
        mockFileConnector.TestImportObjects.Add(CreateImportObject(TestConstants.CS_OBJECT_3_HR_ID, TestConstants.CS_OBJECT_3_DISPLAY_NAME));

        // Act
        var processor = await CreateProcessorAsync(mockFileConnector);
        await processor.PerformImportAsync();

        // Assert — all 3 should be created without duplicates
        Assert.That(SyncRepo.ConnectedSystemObjects.Count, Is.EqualTo(3),
            "All three objects should be created, no duplicates.");
    }

    #endregion

    #region Pre-fetch dictionary lookup tests (InMemory SyncRepository)

    [Test]
    public async Task GetAllCsoExternalIdMappingsAsync_WithExistingCsos_ReturnsDictionaryAsync()
    {
        // Arrange
        InitialiseExistingCsos();
        SyncRepo = TestUtilities.CreateSyncRepository(csos: ConnectedSystemObjectsData, activity: ActivitiesData.First());

        // Act
        var mappings = await SyncRepo.GetAllCsoExternalIdMappingsAsync(1);

        // Assert
        Assert.That(mappings.Count, Is.EqualTo(2), "Should have 2 mappings for 2 seeded CSOs.");

        // Verify the cache keys follow the expected format
        var objectType = ConnectedSystemObjectTypesData.First();
        var externalIdAttrId = (int)MockSourceSystemAttributeNames.HR_ID;
        var expectedKey1 = $"cso:1:{externalIdAttrId}:{TestConstants.CS_OBJECT_1_HR_ID.ToString().ToLowerInvariant()}";
        Assert.That(mappings.ContainsKey(expectedKey1), Is.True,
            $"Dictionary should contain key for CSO 1. Keys: {string.Join(", ", mappings.Keys)}");
        Assert.That(mappings[expectedKey1], Is.EqualTo(TestConstants.CS_OBJECT_1_ID));
    }

    [Test]
    public async Task GetAllCsoExternalIdMappingsAsync_EmptyConnectedSystem_ReturnsEmptyDictionaryAsync()
    {
        // Arrange
        SyncRepo = TestUtilities.CreateSyncRepository(activity: ActivitiesData.First());

        // Act
        var mappings = await SyncRepo.GetAllCsoExternalIdMappingsAsync(1);

        // Assert
        Assert.That(mappings.Count, Is.EqualTo(0), "Empty CS should return empty dictionary.");
    }

    [Test]
    public async Task GetConnectedSystemObjectsByIdsAsync_WithValidIds_ReturnsMatchingCsosAsync()
    {
        // Arrange
        InitialiseExistingCsos();
        SyncRepo = TestUtilities.CreateSyncRepository(csos: ConnectedSystemObjectsData, activity: ActivitiesData.First());

        // Act
        var csos = await SyncRepo.GetConnectedSystemObjectsByIdsAsync(1,
            new[] { TestConstants.CS_OBJECT_1_ID, TestConstants.CS_OBJECT_2_ID });

        // Assert
        Assert.That(csos.Count, Is.EqualTo(2), "Should return both CSOs.");
        Assert.That(csos.Any(c => c.Id == TestConstants.CS_OBJECT_1_ID), Is.True);
        Assert.That(csos.Any(c => c.Id == TestConstants.CS_OBJECT_2_ID), Is.True);
    }

    [Test]
    public async Task GetConnectedSystemObjectsByIdsAsync_WithEmptyIds_ReturnsEmptyListAsync()
    {
        // Arrange
        InitialiseExistingCsos();
        SyncRepo = TestUtilities.CreateSyncRepository(csos: ConnectedSystemObjectsData, activity: ActivitiesData.First());

        // Act
        var csos = await SyncRepo.GetConnectedSystemObjectsByIdsAsync(1, Array.Empty<Guid>());

        // Assert
        Assert.That(csos.Count, Is.EqualTo(0));
    }

    [Test]
    public async Task GetConnectedSystemObjectsByIdsAsync_WithNonExistentIds_ReturnsEmptyListAsync()
    {
        // Arrange
        InitialiseExistingCsos();
        SyncRepo = TestUtilities.CreateSyncRepository(csos: ConnectedSystemObjectsData, activity: ActivitiesData.First());

        // Act
        var csos = await SyncRepo.GetConnectedSystemObjectsByIdsAsync(1,
            new[] { Guid.NewGuid(), Guid.NewGuid() });

        // Assert
        Assert.That(csos.Count, Is.EqualTo(0));
    }

    [Test]
    public async Task GetConnectedSystemObjectsByIdsAsync_WrongConnectedSystem_ReturnsEmptyListAsync()
    {
        // Arrange
        InitialiseExistingCsos();
        SyncRepo = TestUtilities.CreateSyncRepository(csos: ConnectedSystemObjectsData, activity: ActivitiesData.First());

        // Act — CSOs belong to CS 1, query for CS 999
        var csos = await SyncRepo.GetConnectedSystemObjectsByIdsAsync(999,
            new[] { TestConstants.CS_OBJECT_1_ID, TestConstants.CS_OBJECT_2_ID });

        // Assert
        Assert.That(csos.Count, Is.EqualTo(0));
    }

    #endregion

    #region Private helpers

    private async Task<SyncImportTaskProcessor> CreateProcessorAsync(MockFileConnector connector)
    {
        var connectedSystem = await Jim.ConnectedSystems.GetConnectedSystemAsync(1);
        Assert.That(connectedSystem, Is.Not.Null, "Expected to retrieve a Connected System.");

        var runProfile = ConnectedSystemRunProfilesData.Single(
            q => q.ConnectedSystemId == connectedSystem!.Id && q.RunType == ConnectedSystemRunType.FullImport);
        var activity = ActivitiesData.First();

        return new SyncImportTaskProcessor(
            Jim, SyncRepo, new SyncServer(Jim), new JIM.Application.Servers.SyncEngine(),
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

    private void InitialiseExistingCsos(bool singleCsoOnly = false)
    {
        ConnectedSystemObjectsData.Clear();

        var connectedSystemObjectType = ConnectedSystemObjectTypesData.First();
        var cso1 = new ConnectedSystemObject
        {
            Id = TestConstants.CS_OBJECT_1_ID,
            ConnectedSystemId = 1,
            ConnectedSystem = ConnectedSystemsData.First(),
            Type = connectedSystemObjectType,
            ExternalIdAttributeId = (int)MockSourceSystemAttributeNames.HR_ID
        };
        cso1.AttributeValues = new List<ConnectedSystemObjectAttributeValue>
        {
            new()
            {
                Id = Guid.NewGuid(),
                GuidValue = TestConstants.CS_OBJECT_1_HR_ID,
                Attribute = connectedSystemObjectType.Attributes.Single(q => q.Name == MockSourceSystemAttributeNames.HR_ID.ToString()),
                ConnectedSystemObject = cso1
            },
            new()
            {
                Id = Guid.NewGuid(),
                StringValue = TestConstants.CS_OBJECT_1_DISPLAY_NAME,
                Attribute = connectedSystemObjectType.Attributes.Single(q => q.Name == MockSourceSystemAttributeNames.DISPLAY_NAME.ToString()),
                ConnectedSystemObject = cso1
            },
            new()
            {
                Id = Guid.NewGuid(),
                IntValue = 1,
                Attribute = connectedSystemObjectType.Attributes.Single(q => q.Name == MockSourceSystemAttributeNames.EMPLOYEE_ID.ToString()),
                ConnectedSystemObject = cso1
            }
        };
        ConnectedSystemObjectsData.Add(cso1);

        if (singleCsoOnly)
            return;

        var cso2 = new ConnectedSystemObject
        {
            Id = TestConstants.CS_OBJECT_2_ID,
            ConnectedSystemId = 1,
            ConnectedSystem = ConnectedSystemsData.First(),
            Type = connectedSystemObjectType,
            ExternalIdAttributeId = (int)MockSourceSystemAttributeNames.HR_ID
        };
        cso2.AttributeValues = new List<ConnectedSystemObjectAttributeValue>
        {
            new()
            {
                Id = Guid.NewGuid(),
                GuidValue = TestConstants.CS_OBJECT_2_HR_ID,
                Attribute = connectedSystemObjectType.Attributes.Single(q => q.Name == MockSourceSystemAttributeNames.HR_ID.ToString()),
                ConnectedSystemObject = cso2
            },
            new()
            {
                Id = Guid.NewGuid(),
                StringValue = TestConstants.CS_OBJECT_2_DISPLAY_NAME,
                Attribute = connectedSystemObjectType.Attributes.Single(q => q.Name == MockSourceSystemAttributeNames.DISPLAY_NAME.ToString()),
                ConnectedSystemObject = cso2
            },
            new()
            {
                Id = Guid.NewGuid(),
                IntValue = 2,
                Attribute = connectedSystemObjectType.Attributes.Single(q => q.Name == MockSourceSystemAttributeNames.EMPLOYEE_ID.ToString()),
                ConnectedSystemObject = cso2
            }
        };
        ConnectedSystemObjectsData.Add(cso2);
    }

    #endregion
}
