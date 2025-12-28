using JIM.Application;
using JIM.Connectors.Mock;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Staging;
using JIM.PostgresData;
using JIM.Worker.Processors;
using JIM.Worker.Tests.Models;
using Microsoft.EntityFrameworkCore;
using MockQueryable.Moq;
using Moq;

namespace JIM.Worker.Tests.Activities;

/// <summary>
/// Tests for verifying that ActivityRunProfileExecutionItem records are correctly created
/// during various sync operations (Import, Full Sync, Delta Sync, Export).
/// These tests ensure that sync activity history is properly recorded for audit and troubleshooting.
/// </summary>
[TestFixture]
public class ActivityRunProfileExecutionItemTests
{
    #region Accessors
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
    private Mock<JimDbContext> MockJimDbContext { get; set; } = null!;
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
        InitiatedBy = TestUtilities.GetInitiatedBy();

        // set up the connected systems mock
        ConnectedSystemsData = TestUtilities.GetConnectedSystemData();
        MockDbSetConnectedSystems = ConnectedSystemsData.BuildMockDbSet();

        // setup up the connected system run profiles mock
        ConnectedSystemRunProfilesData = TestUtilities.GetConnectedSystemRunProfileData();
        MockDbSetConnectedSystemRunProfiles = ConnectedSystemRunProfilesData.BuildMockDbSet();

        // set up the connected system object types mock. this acts as the persisted schema in JIM
        ConnectedSystemObjectTypesData = TestUtilities.GetConnectedSystemObjectTypeData();
        MockDbSetConnectedSystemObjectTypes = ConnectedSystemObjectTypesData.BuildMockDbSet();

        // setup up the Connected System Partitions mock
        ConnectedSystemPartitionsData = TestUtilities.GetConnectedSystemPartitionData();
        MockDbSetConnectedSystemPartitions = ConnectedSystemPartitionsData.BuildMockDbSet();

        // mock entity framework calls to use our data sources above
        MockJimDbContext = new Mock<JimDbContext>();
        MockJimDbContext.Setup(m => m.ConnectedSystems).Returns(MockDbSetConnectedSystems.Object);
        MockJimDbContext.Setup(m => m.ConnectedSystemObjectTypes).Returns(MockDbSetConnectedSystemObjectTypes.Object);
        MockJimDbContext.Setup(m => m.ConnectedSystemRunProfiles).Returns(MockDbSetConnectedSystemRunProfiles.Object);
        MockJimDbContext.Setup(m => m.ConnectedSystemPartitions).Returns(MockDbSetConnectedSystemPartitions.Object);
    }

    #region Import Execution Item Tests

    /// <summary>
    /// Tests that when a Full Import creates new CSOs, ActivityRunProfileExecutionItems are created
    /// for each imported object with the correct ObjectChangeType of Create.
    /// </summary>
    [Test]
    public async Task FullImport_CreatesNewObjects_CreatesExecutionItemsWithCreateChangeTypeAsync()
    {
        // Arrange
        var fullImportRunProfile = ConnectedSystemRunProfilesData[0];
        ActivitiesData = TestUtilities.GetActivityData(fullImportRunProfile.RunType, fullImportRunProfile.Id);
        var mockDbSetActivities = ActivitiesData.BuildMockDbSet();
        MockJimDbContext.Setup(m => m.Activities).Returns(mockDbSetActivities.Object);

        var connectedSystemObjectData = new List<ConnectedSystemObject>();
        var mockDbSetConnectedSystemObject = connectedSystemObjectData.BuildMockDbSet();
        mockDbSetConnectedSystemObject.Setup(set => set.AddRange(It.IsAny<IEnumerable<ConnectedSystemObject>>()))
            .Callback((IEnumerable<ConnectedSystemObject> entities) =>
            {
                var connectedSystemObjects = entities as ConnectedSystemObject[] ?? entities.ToArray();
                foreach (var entity in connectedSystemObjects)
                    entity.Id = Guid.NewGuid();
                connectedSystemObjectData.AddRange(connectedSystemObjects);
            });
        MockJimDbContext.Setup(m => m.ConnectedSystemObjects).Returns(mockDbSetConnectedSystemObject.Object);

        Jim = new JimApplication(new PostgresDataRepository(MockJimDbContext.Object));

        // Create mock connector with test import data
        var mockFileConnector = new MockFileConnector();
        mockFileConnector.TestImportObjects.Add(new ConnectedSystemImportObject
        {
            ChangeType = ObjectChangeType.Create,
            ObjectType = "SOURCE_USER",
            Attributes = new List<ConnectedSystemImportObjectAttribute>
            {
                new() { Name = MockSourceSystemAttributeNames.HR_ID.ToString(), GuidValues = new List<Guid> { Guid.NewGuid() } },
                new() { Name = MockSourceSystemAttributeNames.DISPLAY_NAME.ToString(), StringValues = new List<string> { "Test User 1" } }
            }
        });
        mockFileConnector.TestImportObjects.Add(new ConnectedSystemImportObject
        {
            ChangeType = ObjectChangeType.Create,
            ObjectType = "SOURCE_USER",
            Attributes = new List<ConnectedSystemImportObjectAttribute>
            {
                new() { Name = MockSourceSystemAttributeNames.HR_ID.ToString(), GuidValues = new List<Guid> { Guid.NewGuid() } },
                new() { Name = MockSourceSystemAttributeNames.DISPLAY_NAME.ToString(), StringValues = new List<string> { "Test User 2" } }
            }
        });

        // Act
        var connectedSystem = await Jim.ConnectedSystems.GetConnectedSystemAsync(1);
        var activity = ActivitiesData.First();
        var runProfile = ConnectedSystemRunProfilesData.Single(q =>
            q.ConnectedSystemId == connectedSystem!.Id && q.RunType == ConnectedSystemRunType.FullImport);
        var syncImportTaskProcessor = new SyncImportTaskProcessor(
            Jim, mockFileConnector, connectedSystem!, runProfile, TestUtilities.CreateTestWorkerTask(activity, InitiatedBy), new CancellationTokenSource());

        await syncImportTaskProcessor.PerformFullImportAsync();

        // Assert
        Assert.That(activity.RunProfileExecutionItems, Has.Count.EqualTo(2),
            "Expected 2 ActivityRunProfileExecutionItems to be created for 2 imported objects.");

        Assert.That(activity.RunProfileExecutionItems.All(item => item.ObjectChangeType == ObjectChangeType.Create),
            Is.True, "All execution items should have ObjectChangeType.Create for new imports.");

        Assert.That(activity.RunProfileExecutionItems.All(item => item.ConnectedSystemObject != null),
            Is.True, "All execution items should be linked to a ConnectedSystemObject.");

        Assert.That(activity.RunProfileExecutionItems.All(item => item.ErrorType == null || item.ErrorType == ActivityRunProfileExecutionItemErrorType.NotSet),
            Is.True, "No errors should be recorded for successful imports.");
    }

    // NOTE: The following tests for Update and Delete scenarios require integration tests
    // because the mock setup needed to properly simulate existing CSOs involves complex
    // navigation property resolution that unit test mocks don't handle well.
    //
    // When an import finds an existing CSO:
    // - SyncImportTaskProcessor.ProcessImportObjectsAsync sets ObjectChangeType.Update
    // - For Delete requests, it sets ObjectChangeType.Obsolete and marks CSO.Status = Obsolete
    //
    // These behaviours are tested indirectly through the integration test suite.
    // See: test/JIM.Worker.Tests/Synchronisation/ImportUpdateObjectSvaTests.cs
    // See: test/JIM.Worker.Tests/Synchronisation/ImportDeleteObjectTests.cs

    /// <summary>
    /// Tests that when an import error occurs (e.g., duplicate attributes),
    /// the ActivityRunProfileExecutionItem correctly records the error type and message.
    /// </summary>
    [Test]
    public async Task FullImport_WithError_RecordsErrorInExecutionItemAsync()
    {
        // Arrange
        var fullImportRunProfile = ConnectedSystemRunProfilesData[0];
        ActivitiesData = TestUtilities.GetActivityData(fullImportRunProfile.RunType, fullImportRunProfile.Id);
        var mockDbSetActivities = ActivitiesData.BuildMockDbSet();
        MockJimDbContext.Setup(m => m.Activities).Returns(mockDbSetActivities.Object);

        var connectedSystemObjectData = new List<ConnectedSystemObject>();
        var mockDbSetConnectedSystemObject = connectedSystemObjectData.BuildMockDbSet();
        MockJimDbContext.Setup(m => m.ConnectedSystemObjects).Returns(mockDbSetConnectedSystemObject.Object);

        Jim = new JimApplication(new PostgresDataRepository(MockJimDbContext.Object));

        // Create import object with duplicate attributes (error condition)
        var mockFileConnector = new MockFileConnector();
        mockFileConnector.TestImportObjects.Add(new ConnectedSystemImportObject
        {
            ChangeType = ObjectChangeType.Create,
            ObjectType = "SOURCE_USER",
            Attributes = new List<ConnectedSystemImportObjectAttribute>
            {
                new() { Name = MockSourceSystemAttributeNames.HR_ID.ToString(), GuidValues = new List<Guid> { Guid.NewGuid() } },
                new() { Name = MockSourceSystemAttributeNames.DISPLAY_NAME.ToString(), StringValues = new List<string> { "Name 1" } },
                // Duplicate attribute - this should cause an error
                new() { Name = MockSourceSystemAttributeNames.DISPLAY_NAME.ToString().ToLower(), StringValues = new List<string> { "Name 2" } }
            }
        });

        // Act
        var connectedSystem = await Jim.ConnectedSystems.GetConnectedSystemAsync(1);
        var activity = ActivitiesData.First();
        var runProfile = ConnectedSystemRunProfilesData.Single(q =>
            q.ConnectedSystemId == connectedSystem!.Id && q.RunType == ConnectedSystemRunType.FullImport);
        var syncImportTaskProcessor = new SyncImportTaskProcessor(
            Jim, mockFileConnector, connectedSystem!, runProfile, TestUtilities.CreateTestWorkerTask(activity, InitiatedBy), new CancellationTokenSource());

        await syncImportTaskProcessor.PerformFullImportAsync();

        // Assert
        Assert.That(activity.RunProfileExecutionItems, Has.Count.EqualTo(1),
            "Expected 1 ActivityRunProfileExecutionItem for the error object.");

        var errorItem = activity.RunProfileExecutionItems.First();
        Assert.That(errorItem.ErrorType, Is.EqualTo(ActivityRunProfileExecutionItemErrorType.DuplicateImportedAttributes),
            "Error type should be DuplicateImportedAttributes.");

        Assert.That(errorItem.ErrorMessage, Is.Not.Null.And.Not.Empty,
            "Error message should be recorded.");

        Assert.That(errorItem.ErrorMessage, Does.Contain("DISPLAY_NAME"),
            "Error message should mention the duplicate attribute.");
    }

    /// <summary>
    /// Tests that ActivityRunProfileExecutionItems are correctly linked to their parent Activity.
    /// </summary>
    [Test]
    public async Task FullImport_ExecutionItems_AreLinkedToActivityAsync()
    {
        // Arrange
        var fullImportRunProfile = ConnectedSystemRunProfilesData[0];
        ActivitiesData = TestUtilities.GetActivityData(fullImportRunProfile.RunType, fullImportRunProfile.Id);
        var mockDbSetActivities = ActivitiesData.BuildMockDbSet();
        MockJimDbContext.Setup(m => m.Activities).Returns(mockDbSetActivities.Object);

        var connectedSystemObjectData = new List<ConnectedSystemObject>();
        var mockDbSetConnectedSystemObject = connectedSystemObjectData.BuildMockDbSet();
        mockDbSetConnectedSystemObject.Setup(set => set.AddRange(It.IsAny<IEnumerable<ConnectedSystemObject>>()))
            .Callback((IEnumerable<ConnectedSystemObject> entities) =>
            {
                var connectedSystemObjects = entities as ConnectedSystemObject[] ?? entities.ToArray();
                foreach (var entity in connectedSystemObjects)
                    entity.Id = Guid.NewGuid();
                connectedSystemObjectData.AddRange(connectedSystemObjects);
            });
        MockJimDbContext.Setup(m => m.ConnectedSystemObjects).Returns(mockDbSetConnectedSystemObject.Object);

        Jim = new JimApplication(new PostgresDataRepository(MockJimDbContext.Object));

        var mockFileConnector = new MockFileConnector();
        mockFileConnector.TestImportObjects.Add(new ConnectedSystemImportObject
        {
            ChangeType = ObjectChangeType.Create,
            ObjectType = "SOURCE_USER",
            Attributes = new List<ConnectedSystemImportObjectAttribute>
            {
                new() { Name = MockSourceSystemAttributeNames.HR_ID.ToString(), GuidValues = new List<Guid> { Guid.NewGuid() } },
                new() { Name = MockSourceSystemAttributeNames.DISPLAY_NAME.ToString(), StringValues = new List<string> { "Test User" } }
            }
        });

        // Act
        var connectedSystem = await Jim.ConnectedSystems.GetConnectedSystemAsync(1);
        var activity = ActivitiesData.First();
        var runProfile = ConnectedSystemRunProfilesData.Single(q =>
            q.ConnectedSystemId == connectedSystem!.Id && q.RunType == ConnectedSystemRunType.FullImport);
        var syncImportTaskProcessor = new SyncImportTaskProcessor(
            Jim, mockFileConnector, connectedSystem!, runProfile, TestUtilities.CreateTestWorkerTask(activity, InitiatedBy), new CancellationTokenSource());

        await syncImportTaskProcessor.PerformFullImportAsync();

        // Assert
        Assert.That(activity.RunProfileExecutionItems, Has.Count.GreaterThan(0),
            "At least one execution item should be created.");

        foreach (var item in activity.RunProfileExecutionItems)
        {
            Assert.That(item.Activity, Is.EqualTo(activity),
                "Execution item should reference the parent Activity.");

            Assert.That(item.ActivityId, Is.EqualTo(activity.Id),
                "Execution item ActivityId should match parent Activity.Id.");
        }
    }

    /// <summary>
    /// Tests that importing multiple objects creates the correct count of execution items
    /// and that each is properly populated.
    /// </summary>
    [Test]
    public async Task FullImport_MultipleObjects_CreatesCorrectCountOfExecutionItemsAsync()
    {
        // Arrange
        var fullImportRunProfile = ConnectedSystemRunProfilesData[0];
        ActivitiesData = TestUtilities.GetActivityData(fullImportRunProfile.RunType, fullImportRunProfile.Id);
        var mockDbSetActivities = ActivitiesData.BuildMockDbSet();
        MockJimDbContext.Setup(m => m.Activities).Returns(mockDbSetActivities.Object);

        var connectedSystemObjectData = new List<ConnectedSystemObject>();
        var mockDbSetConnectedSystemObject = connectedSystemObjectData.BuildMockDbSet();
        mockDbSetConnectedSystemObject.Setup(set => set.AddRange(It.IsAny<IEnumerable<ConnectedSystemObject>>()))
            .Callback((IEnumerable<ConnectedSystemObject> entities) =>
            {
                var connectedSystemObjects = entities as ConnectedSystemObject[] ?? entities.ToArray();
                foreach (var entity in connectedSystemObjects)
                    entity.Id = Guid.NewGuid();
                connectedSystemObjectData.AddRange(connectedSystemObjects);
            });
        MockJimDbContext.Setup(m => m.ConnectedSystemObjects).Returns(mockDbSetConnectedSystemObject.Object);

        Jim = new JimApplication(new PostgresDataRepository(MockJimDbContext.Object));

        var mockFileConnector = new MockFileConnector();
        const int objectCount = 5;
        for (var i = 0; i < objectCount; i++)
        {
            mockFileConnector.TestImportObjects.Add(new ConnectedSystemImportObject
            {
                ChangeType = ObjectChangeType.Create,
                ObjectType = "SOURCE_USER",
                Attributes = new List<ConnectedSystemImportObjectAttribute>
                {
                    new() { Name = MockSourceSystemAttributeNames.HR_ID.ToString(), GuidValues = new List<Guid> { Guid.NewGuid() } },
                    new() { Name = MockSourceSystemAttributeNames.DISPLAY_NAME.ToString(), StringValues = new List<string> { $"Test User {i + 1}" } }
                }
            });
        }

        // Act
        var connectedSystem = await Jim.ConnectedSystems.GetConnectedSystemAsync(1);
        var activity = ActivitiesData.First();
        var runProfile = ConnectedSystemRunProfilesData.Single(q =>
            q.ConnectedSystemId == connectedSystem!.Id && q.RunType == ConnectedSystemRunType.FullImport);
        var syncImportTaskProcessor = new SyncImportTaskProcessor(
            Jim, mockFileConnector, connectedSystem!, runProfile, TestUtilities.CreateTestWorkerTask(activity, InitiatedBy), new CancellationTokenSource());

        await syncImportTaskProcessor.PerformFullImportAsync();

        // Assert
        Assert.That(activity.RunProfileExecutionItems, Has.Count.EqualTo(objectCount),
            $"Expected {objectCount} ActivityRunProfileExecutionItems for {objectCount} imported objects.");

        // Verify each execution item has unique CSO
        var csoIds = activity.RunProfileExecutionItems
            .Where(i => i.ConnectedSystemObject != null)
            .Select(i => i.ConnectedSystemObject!.Id)
            .ToList();
        Assert.That(csoIds.Distinct().Count(), Is.EqualTo(objectCount),
            "Each execution item should reference a unique CSO.");
    }

    #endregion

    #region Execution Item Statistics Tests

    /// <summary>
    /// Tests that ActivityRunProfileExecutionStats are correctly calculated from execution items.
    /// Note: When errors occur during CSO creation (e.g., duplicate attributes), the ObjectChangeType
    /// may remain at Create (set before the error) or NotSet depending on when the error occurs.
    /// </summary>
    [Test]
    public async Task ExecutionStats_CalculatedCorrectlyFromExecutionItemsAsync()
    {
        // Arrange
        var fullImportRunProfile = ConnectedSystemRunProfilesData[0];
        ActivitiesData = TestUtilities.GetActivityData(fullImportRunProfile.RunType, fullImportRunProfile.Id);
        var mockDbSetActivities = ActivitiesData.BuildMockDbSet();
        MockJimDbContext.Setup(m => m.Activities).Returns(mockDbSetActivities.Object);

        var connectedSystemObjectData = new List<ConnectedSystemObject>();
        var mockDbSetConnectedSystemObject = connectedSystemObjectData.BuildMockDbSet();
        mockDbSetConnectedSystemObject.Setup(set => set.AddRange(It.IsAny<IEnumerable<ConnectedSystemObject>>()))
            .Callback((IEnumerable<ConnectedSystemObject> entities) =>
            {
                var connectedSystemObjects = entities as ConnectedSystemObject[] ?? entities.ToArray();
                foreach (var entity in connectedSystemObjects)
                    entity.Id = Guid.NewGuid();
                connectedSystemObjectData.AddRange(connectedSystemObjects);
            });
        MockJimDbContext.Setup(m => m.ConnectedSystemObjects).Returns(mockDbSetConnectedSystemObject.Object);

        Jim = new JimApplication(new PostgresDataRepository(MockJimDbContext.Object));

        var mockFileConnector = new MockFileConnector();

        // Add 3 successful creates
        for (var i = 0; i < 3; i++)
        {
            mockFileConnector.TestImportObjects.Add(new ConnectedSystemImportObject
            {
                ChangeType = ObjectChangeType.Create,
                ObjectType = "SOURCE_USER",
                Attributes = new List<ConnectedSystemImportObjectAttribute>
                {
                    new() { Name = MockSourceSystemAttributeNames.HR_ID.ToString(), GuidValues = new List<Guid> { Guid.NewGuid() } },
                    new() { Name = MockSourceSystemAttributeNames.DISPLAY_NAME.ToString(), StringValues = new List<string> { $"User {i}" } }
                }
            });
        }

        // Add 1 object with error (duplicate attributes)
        mockFileConnector.TestImportObjects.Add(new ConnectedSystemImportObject
        {
            ChangeType = ObjectChangeType.Create,
            ObjectType = "SOURCE_USER",
            Attributes = new List<ConnectedSystemImportObjectAttribute>
            {
                new() { Name = MockSourceSystemAttributeNames.HR_ID.ToString(), GuidValues = new List<Guid> { Guid.NewGuid() } },
                new() { Name = MockSourceSystemAttributeNames.DISPLAY_NAME.ToString(), StringValues = new List<string> { "Duplicate 1" } },
                new() { Name = MockSourceSystemAttributeNames.DISPLAY_NAME.ToString().ToLower(), StringValues = new List<string> { "Duplicate 2" } }
            }
        });

        // Act
        var connectedSystem = await Jim.ConnectedSystems.GetConnectedSystemAsync(1);
        var activity = ActivitiesData.First();
        var runProfile = ConnectedSystemRunProfilesData.Single(q =>
            q.ConnectedSystemId == connectedSystem!.Id && q.RunType == ConnectedSystemRunType.FullImport);
        var syncImportTaskProcessor = new SyncImportTaskProcessor(
            Jim, mockFileConnector, connectedSystem!, runProfile, TestUtilities.CreateTestWorkerTask(activity, InitiatedBy), new CancellationTokenSource());

        await syncImportTaskProcessor.PerformFullImportAsync();

        // Assert
        Assert.That(activity.RunProfileExecutionItems, Has.Count.EqualTo(4),
            "Expected 4 execution items total (3 creates + 1 error).");

        // 3 successful items should have Create change type
        var successfulCreateCount = activity.RunProfileExecutionItems.Count(i =>
            i.ObjectChangeType == ObjectChangeType.Create &&
            (i.ErrorType == null || i.ErrorType == ActivityRunProfileExecutionItemErrorType.NotSet));
        Assert.That(successfulCreateCount, Is.EqualTo(3), "Expected 3 successful items with Create change type.");

        var errorCount = activity.RunProfileExecutionItems.Count(i =>
            i.ErrorType != null && i.ErrorType != ActivityRunProfileExecutionItemErrorType.NotSet);
        Assert.That(errorCount, Is.EqualTo(1), "Expected 1 item with an error.");

        var successCount = activity.RunProfileExecutionItems.Count(i =>
            i.ErrorType == null || i.ErrorType == ActivityRunProfileExecutionItemErrorType.NotSet);
        Assert.That(successCount, Is.EqualTo(3), "Expected 3 successful items without errors.");
    }

    #endregion
}
