using JIM.Application;
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
using NUnit.Framework;

// ReSharper disable PossibleNullReferenceException

namespace JIM.Worker.Tests.Synchronisation;

/// <summary>
/// Tests defensive deduplication of multi-valued attributes during import.
/// Addresses GitHub issue #284: Implement defensive deduplication for multi-valued reference attributes.
/// </summary>
[TestFixture]
public class ImportDeduplicationTests
{
    #region accessors
    private MetaverseObject InitiatedBy { get; set; } = null!;
    private List<ConnectedSystem> ConnectedSystemsData { get; set; } = null!;
    private Mock<DbSet<ConnectedSystem>> MockDbSetConnectedSystems { get; set; } = null!;
    private List<ConnectedSystemRunProfile> ConnectedSystemRunProfilesData { get; set; } = null!;
    private Mock<DbSet<ConnectedSystemRunProfile>> MockDbSetConnectedSystemRunProfiles { get; set; } = null!;
    private List<ConnectedSystemObject> ConnectedSystemObjectsData { get; set; } = null!;
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

        // set up the activity mock
        var fullImportRunProfile = ConnectedSystemRunProfilesData[0];
        ActivitiesData = TestUtilities.GetActivityData(fullImportRunProfile.RunType, fullImportRunProfile.Id);
        MockDbSetActivities = ActivitiesData.BuildMockDbSet();

        // set up the service settings mock
        ServiceSettingsData = TestUtilities.GetServiceSettingsData();
        MockDbSetServiceSettings = ServiceSettingsData.BuildMockDbSet();

        // set up the pending exports mock (empty - import tests don't have pending exports to reconcile)
        PendingExportsData = new List<PendingExport>();
        MockDbSetPendingExports = PendingExportsData.BuildMockDbSet();

        // mock entity framework calls to use our data sources above
        MockJimDbContext = new Mock<JimDbContext>();
        MockJimDbContext.Setup(m => m.Activities).Returns(MockDbSetActivities.Object);
        MockJimDbContext.Setup(m => m.ConnectedSystems).Returns(MockDbSetConnectedSystems.Object);
        MockJimDbContext.Setup(m => m.ConnectedSystemObjectTypes).Returns(MockDbSetConnectedSystemObjectTypes.Object);
        MockJimDbContext.Setup(m => m.ConnectedSystemRunProfiles).Returns(MockDbSetConnectedSystemRunProfiles.Object);
        MockJimDbContext.Setup(m => m.ConnectedSystemPartitions).Returns(MockDbSetConnectedSystemPartitions.Object);
        MockJimDbContext.Setup(m => m.ServiceSettingItems).Returns(MockDbSetServiceSettings.Object);
        MockJimDbContext.Setup(m => m.PendingExports).Returns(MockDbSetPendingExports.Object);

        // instantiate Jim using the mocked db context
        Jim = new JimApplication(new PostgresDataRepository(MockJimDbContext.Object));

        // Set up the connected system objects mock
        ConnectedSystemObjectsData = new List<ConnectedSystemObject>();
        var mockDbSetConnectedSystemObject = ConnectedSystemObjectsData.BuildMockDbSet();
        mockDbSetConnectedSystemObject.Setup(set => set.AddRange(It.IsAny<IEnumerable<ConnectedSystemObject>>())).Callback((IEnumerable<ConnectedSystemObject> entities) =>
        {
            var connectedSystemObjects = entities as ConnectedSystemObject[] ?? entities.ToArray();
            foreach (var entity in connectedSystemObjects)
                entity.Id = Guid.NewGuid();
            ConnectedSystemObjectsData.AddRange(connectedSystemObjects);
        });
        MockJimDbContext.Setup(m => m.ConnectedSystemObjects).Returns(mockDbSetConnectedSystemObject.Object);
    }

    /// <summary>
    /// Creates a standard import object with all required attributes.
    /// The QUALIFICATIONS attribute can be customised with duplicate values for testing.
    /// </summary>
    private static ConnectedSystemImportObject CreateStandardImportObject(Guid hrId, int employeeId, string displayName, List<string> qualifications)
    {
        return new ConnectedSystemImportObject
        {
            ChangeType = ObjectChangeType.NotSet,
            ObjectType = "SOURCE_USER",
            Attributes = new List<ConnectedSystemImportObjectAttribute>
            {
                new()
                {
                    // External ID - GUID
                    Name = MockSourceSystemAttributeNames.HR_ID.ToString(),
                    GuidValues = new List<Guid> { hrId }
                },
                new()
                {
                    // Integer
                    Name = MockSourceSystemAttributeNames.EMPLOYEE_ID.ToString(),
                    IntValues = new List<int> { employeeId }
                },
                new()
                {
                    // DateTime
                    Name = MockSourceSystemAttributeNames.START_DATE.ToString(),
                    DateTimeValue = DateTime.UtcNow
                },
                new()
                {
                    // String
                    Name = MockSourceSystemAttributeNames.DISPLAY_NAME.ToString(),
                    StringValues = new List<string> { displayName }
                },
                new()
                {
                    // String
                    Name = MockSourceSystemAttributeNames.EMAIL_ADDRESS.ToString(),
                    StringValues = new List<string> { $"{displayName.ToLower().Replace(" ", ".")}@test.com" }
                },
                new()
                {
                    // String
                    Name = MockSourceSystemAttributeNames.ROLE.ToString(),
                    StringValues = new List<string> { "Employee" }
                },
                new()
                {
                    // MVA String - this is what we test for deduplication
                    Name = MockSourceSystemAttributeNames.QUALIFICATIONS.ToString(),
                    StringValues = qualifications
                },
                new()
                {
                    // Boolean
                    Name = MockSourceSystemAttributeNames.LEAVER.ToString(),
                    BoolValue = false
                }
            }
        };
    }

    #region String (Text) attribute deduplication tests

    [Test]
    public async Task FullImport_WithDuplicateStringValues_DeduplicatesValuesAsync()
    {
        // Arrange: Create an import object with duplicate string values in the QUALIFICATIONS attribute
        var mockFileConnector = new MockFileConnector();
        mockFileConnector.TestImportObjects.Add(CreateStandardImportObject(
            Guid.NewGuid(),
            100,
            "Test User With Duplicates",
            // Multi-valued string attribute with DUPLICATES
            new List<string> { "CERT-A", "CERT-B", "CERT-A", "CERT-C", "CERT-B" }
        ));

        // Use the JIM API to get the connected system with all includes
        var connectedSystem = await Jim.ConnectedSystems.GetConnectedSystemAsync(1);
        Assert.That(connectedSystem, Is.Not.Null);
        var runProfile = ConnectedSystemRunProfilesData.Single(q => q.ConnectedSystemId == connectedSystem!.Id && q.RunType == ConnectedSystemRunType.FullImport);
        var activity = ActivitiesData[0];

        // Act
        var syncImportTaskProcessor = new SyncImportTaskProcessor(
            Jim, mockFileConnector, connectedSystem, runProfile,
            TestUtilities.CreateTestWorkerTask(activity, InitiatedBy), new CancellationTokenSource());
        await syncImportTaskProcessor.PerformFullImportAsync();

        // Assert: Verify the CSO was created with deduplicated values
        Assert.That(ConnectedSystemObjectsData, Has.Count.EqualTo(1));
        var createdCso = ConnectedSystemObjectsData[0];

        var qualificationsAttribute = ConnectedSystemObjectTypesData[0].Attributes
            .Single(a => a.Name == MockSourceSystemAttributeNames.QUALIFICATIONS.ToString());

        var qualificationValues = createdCso.AttributeValues
            .Where(av => av.AttributeId == qualificationsAttribute.Id)
            .Select(av => av.StringValue)
            .ToList();

        // Should have 3 unique values, not 5
        Assert.That(qualificationValues, Has.Count.EqualTo(3));
        Assert.That(qualificationValues, Does.Contain("CERT-A"));
        Assert.That(qualificationValues, Does.Contain("CERT-B"));
        Assert.That(qualificationValues, Does.Contain("CERT-C"));
    }

    [Test]
    public async Task FullImport_WithUniqueStringValues_RetainsAllValuesAsync()
    {
        // Arrange: Create an import object with unique string values (no duplicates)
        var mockFileConnector = new MockFileConnector();
        mockFileConnector.TestImportObjects.Add(CreateStandardImportObject(
            Guid.NewGuid(),
            101,
            "Test User Unique Values",
            // Multi-valued string attribute with all UNIQUE values
            new List<string> { "CERT-X", "CERT-Y", "CERT-Z" }
        ));

        var connectedSystem = await Jim.ConnectedSystems.GetConnectedSystemAsync(1);
        Assert.That(connectedSystem, Is.Not.Null);
        var runProfile = ConnectedSystemRunProfilesData.Single(q => q.ConnectedSystemId == connectedSystem!.Id && q.RunType == ConnectedSystemRunType.FullImport);
        var activity = ActivitiesData[0];

        // Act
        var syncImportTaskProcessor = new SyncImportTaskProcessor(
            Jim, mockFileConnector, connectedSystem, runProfile,
            TestUtilities.CreateTestWorkerTask(activity, InitiatedBy), new CancellationTokenSource());
        await syncImportTaskProcessor.PerformFullImportAsync();

        // Assert: All 3 values should be retained
        Assert.That(ConnectedSystemObjectsData, Has.Count.EqualTo(1));
        var createdCso = ConnectedSystemObjectsData[0];

        var qualificationsAttribute = ConnectedSystemObjectTypesData[0].Attributes
            .Single(a => a.Name == MockSourceSystemAttributeNames.QUALIFICATIONS.ToString());

        var qualificationValues = createdCso.AttributeValues
            .Where(av => av.AttributeId == qualificationsAttribute.Id)
            .Select(av => av.StringValue)
            .ToList();

        Assert.That(qualificationValues, Has.Count.EqualTo(3));
    }

    #endregion

    #region Single value scenarios (no deduplication needed)

    [Test]
    public async Task FullImport_WithSingleValue_RetainsValueAsync()
    {
        // Arrange: Single value should not be affected by deduplication
        var mockFileConnector = new MockFileConnector();
        mockFileConnector.TestImportObjects.Add(CreateStandardImportObject(
            Guid.NewGuid(),
            102,
            "Single Value User",
            new List<string> { "ONLY-ONE-CERT" }
        ));

        var connectedSystem = await Jim.ConnectedSystems.GetConnectedSystemAsync(1);
        Assert.That(connectedSystem, Is.Not.Null);
        var runProfile = ConnectedSystemRunProfilesData.Single(q => q.ConnectedSystemId == connectedSystem!.Id && q.RunType == ConnectedSystemRunType.FullImport);
        var activity = ActivitiesData[0];

        // Act
        var syncImportTaskProcessor = new SyncImportTaskProcessor(
            Jim, mockFileConnector, connectedSystem, runProfile,
            TestUtilities.CreateTestWorkerTask(activity, InitiatedBy), new CancellationTokenSource());
        await syncImportTaskProcessor.PerformFullImportAsync();

        // Assert: Single value retained
        Assert.That(ConnectedSystemObjectsData, Has.Count.EqualTo(1));
        var createdCso = ConnectedSystemObjectsData[0];

        var qualificationsAttribute = ConnectedSystemObjectTypesData[0].Attributes
            .Single(a => a.Name == MockSourceSystemAttributeNames.QUALIFICATIONS.ToString());

        var qualificationValues = createdCso.AttributeValues
            .Where(av => av.AttributeId == qualificationsAttribute.Id)
            .Select(av => av.StringValue)
            .ToList();

        Assert.That(qualificationValues, Has.Count.EqualTo(1));
        Assert.That(qualificationValues[0], Is.EqualTo("ONLY-ONE-CERT"));
    }

    #endregion

    #region Empty value scenarios

    [Test]
    public async Task FullImport_WithEmptyValues_HandlesGracefullyAsync()
    {
        // Arrange: Empty multi-valued attribute should not cause issues
        var mockFileConnector = new MockFileConnector();
        mockFileConnector.TestImportObjects.Add(CreateStandardImportObject(
            Guid.NewGuid(),
            103,
            "Empty Qualifications User",
            new List<string>() // Empty list
        ));

        var connectedSystem = await Jim.ConnectedSystems.GetConnectedSystemAsync(1);
        Assert.That(connectedSystem, Is.Not.Null);
        var runProfile = ConnectedSystemRunProfilesData.Single(q => q.ConnectedSystemId == connectedSystem!.Id && q.RunType == ConnectedSystemRunType.FullImport);
        var activity = ActivitiesData[0];

        // Act
        var syncImportTaskProcessor = new SyncImportTaskProcessor(
            Jim, mockFileConnector, connectedSystem, runProfile,
            TestUtilities.CreateTestWorkerTask(activity, InitiatedBy), new CancellationTokenSource());
        await syncImportTaskProcessor.PerformFullImportAsync();

        // Assert: Object created, no attribute values for the empty attribute
        Assert.That(ConnectedSystemObjectsData, Has.Count.EqualTo(1));
        var createdCso = ConnectedSystemObjectsData[0];

        var qualificationsAttribute = ConnectedSystemObjectTypesData[0].Attributes
            .Single(a => a.Name == MockSourceSystemAttributeNames.QUALIFICATIONS.ToString());

        var qualificationValues = createdCso.AttributeValues
            .Where(av => av.AttributeId == qualificationsAttribute.Id)
            .ToList();

        Assert.That(qualificationValues, Has.Count.EqualTo(0));
    }

    #endregion

    #region Multiple duplicate occurrences in same attribute

    [Test]
    public async Task FullImport_WithManyDuplicates_DeduplicatesToUniqueSetAsync()
    {
        // Arrange: Many duplicates of the same values
        var mockFileConnector = new MockFileConnector();
        mockFileConnector.TestImportObjects.Add(CreateStandardImportObject(
            Guid.NewGuid(),
            104,
            "Many Duplicates User",
            // 6 values but only 3 unique
            new List<string> { "A", "B", "A", "C", "B", "A" }
        ));

        var connectedSystem = await Jim.ConnectedSystems.GetConnectedSystemAsync(1);
        Assert.That(connectedSystem, Is.Not.Null);
        var runProfile = ConnectedSystemRunProfilesData.Single(q => q.ConnectedSystemId == connectedSystem!.Id && q.RunType == ConnectedSystemRunType.FullImport);
        var activity = ActivitiesData[0];

        // Act
        var syncImportTaskProcessor = new SyncImportTaskProcessor(
            Jim, mockFileConnector, connectedSystem, runProfile,
            TestUtilities.CreateTestWorkerTask(activity, InitiatedBy), new CancellationTokenSource());
        await syncImportTaskProcessor.PerformFullImportAsync();

        // Assert
        Assert.That(ConnectedSystemObjectsData, Has.Count.EqualTo(1));
        var createdCso = ConnectedSystemObjectsData[0];

        var qualificationsAttribute = ConnectedSystemObjectTypesData[0].Attributes
            .Single(a => a.Name == MockSourceSystemAttributeNames.QUALIFICATIONS.ToString());

        var stringValues = createdCso.AttributeValues
            .Where(av => av.AttributeId == qualificationsAttribute.Id)
            .Select(av => av.StringValue)
            .ToList();

        // Should have 3 unique values (A, B, C)
        Assert.That(stringValues, Has.Count.EqualTo(3));
        Assert.That(stringValues, Does.Contain("A"));
        Assert.That(stringValues, Does.Contain("B"));
        Assert.That(stringValues, Does.Contain("C"));
    }

    #endregion

    #region Case sensitivity tests

    [Test]
    public async Task FullImport_WithCaseDifferentStringValues_RetainsAllAsync()
    {
        // Arrange: Different case = different values for string attributes (case-sensitive)
        var mockFileConnector = new MockFileConnector();
        mockFileConnector.TestImportObjects.Add(CreateStandardImportObject(
            Guid.NewGuid(),
            105,
            "Case Sensitive User",
            // These are all different due to case
            new List<string> { "cert-a", "CERT-A", "Cert-A" }
        ));

        var connectedSystem = await Jim.ConnectedSystems.GetConnectedSystemAsync(1);
        Assert.That(connectedSystem, Is.Not.Null);
        var runProfile = ConnectedSystemRunProfilesData.Single(q => q.ConnectedSystemId == connectedSystem!.Id && q.RunType == ConnectedSystemRunType.FullImport);
        var activity = ActivitiesData[0];

        // Act
        var syncImportTaskProcessor = new SyncImportTaskProcessor(
            Jim, mockFileConnector, connectedSystem, runProfile,
            TestUtilities.CreateTestWorkerTask(activity, InitiatedBy), new CancellationTokenSource());
        await syncImportTaskProcessor.PerformFullImportAsync();

        // Assert: All 3 case-different values should be retained (case-sensitive comparison)
        Assert.That(ConnectedSystemObjectsData, Has.Count.EqualTo(1));
        var createdCso = ConnectedSystemObjectsData[0];

        var qualificationsAttribute = ConnectedSystemObjectTypesData[0].Attributes
            .Single(a => a.Name == MockSourceSystemAttributeNames.QUALIFICATIONS.ToString());

        var stringValues = createdCso.AttributeValues
            .Where(av => av.AttributeId == qualificationsAttribute.Id)
            .Select(av => av.StringValue)
            .ToList();

        Assert.That(stringValues, Has.Count.EqualTo(3));
    }

    #endregion
}
