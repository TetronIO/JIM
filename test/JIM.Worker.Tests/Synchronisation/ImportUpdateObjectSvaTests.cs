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

namespace JIM.Worker.Tests.Synchronisation;

public class ImportUpdateObjectSvaTests
{
    #region accessors
    private MetaverseObject InitiatedBy { get; set; }
    private List<ConnectedSystem> ConnectedSystemsData { get; set; } 
    private Mock<DbSet<ConnectedSystem>> MockDbSetConnectedSystems { get; set; }
    private List<ConnectedSystemRunProfile> ConnectedSystemRunProfilesData { get; set; }
    private Mock<DbSet<ConnectedSystemRunProfile>> MockDbSetConnectedSystemRunProfiles { get; set; }
    private List<ConnectedSystemObject> ConnectedSystemObjectsData { get; } = new();
    private List<ConnectedSystemObjectType> ConnectedSystemObjectTypesData { get; set; } 
    private Mock<DbSet<ConnectedSystemObjectType>> MockDbSetConnectedSystemObjectTypes { get; set; }
    private List<ConnectedSystemPartition> ConnectedSystemPartitionsData { get; set; }
    private Mock<DbSet<ConnectedSystemPartition>> MockDbSetConnectedSystemPartitions { get; set; }
    private List<Activity> ActivitiesData { get; set; }
    private Mock<DbSet<Activity>> MockDbSetActivities { get; set; }
    private Mock<JimDbContext> MockJimDbContext { get; set; }
    private JimApplication Jim { get; set; }
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
        
        // mock entity framework calls to use our data sources above
        MockJimDbContext = new Mock<JimDbContext>();
        MockJimDbContext.Setup(m => m.Activities).Returns(MockDbSetActivities.Object);
        MockJimDbContext.Setup(m => m.ConnectedSystems).Returns(MockDbSetConnectedSystems.Object);
        MockJimDbContext.Setup(m => m.ConnectedSystemObjectTypes).Returns(MockDbSetConnectedSystemObjectTypes.Object);
        MockJimDbContext.Setup(m => m.ConnectedSystemRunProfiles).Returns(MockDbSetConnectedSystemRunProfiles.Object);
        MockJimDbContext.Setup(m => m.ConnectedSystemPartitions).Returns(MockDbSetConnectedSystemPartitions.Object);
        
        // instantiate Jim using the mocked db context
        Jim = new JimApplication(new PostgresDataRepository(MockJimDbContext.Object));
        
        var mockDbSetConnectedSystemObject = ConnectedSystemObjectsData.BuildMockDbSet();
        mockDbSetConnectedSystemObject.Setup(set => set.AddRange(It.IsAny<IEnumerable<ConnectedSystemObject>>())).Callback((IEnumerable<ConnectedSystemObject> entities) => {
            var connectedSystemObjects = entities as ConnectedSystemObject[] ?? entities.ToArray();
            foreach (var entity in connectedSystemObjects)
                entity.Id = Guid.NewGuid(); // assign the ids here, mocking what the db would do in SaveChanges()
            ConnectedSystemObjectsData.AddRange(connectedSystemObjects);
        });
        MockJimDbContext.Setup(m => m.ConnectedSystemObjects).Returns(mockDbSetConnectedSystemObject.Object); 
    }
    
    [Test]
    public async Task FullImportUpdateIntSvaTestAsync()
    {
        InitialiseConnectedSystemObjectsData();
        
        // mock up a connector that will return updates for our existing connected system objects above.
        // changes: CONTRACTED_WEEKLY_HOURS has a populated, but different value
        var mockFileConnector = new MockFileConnector();
        mockFileConnector.TestImportObjects.Add(new ConnectedSystemImportObject
        {
            ChangeType = ObjectChangeType.Create,
            ObjectType = "SOURCE_USER",
            Attributes = new List<ConnectedSystemImportObjectAttribute>()
            {
                new ()
                {
                    Name = MockSourceSystemAttributeNames.EMPLOYEE_ID.ToString(),
                    IntValues = new List<int> { 1 },
                    Type = AttributeDataType.Number
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.HR_ID.ToString(),
                    GuidValues = new List<Guid> { TestConstants.CS_OBJECT_1_HR_ID },
                    Type = AttributeDataType.Guid
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.START_DATE.ToString(),
                    DateTimeValue = TestConstants.CS_OBJECT_1_START_DATE,
                    Type = AttributeDataType.DateTime
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.DISPLAY_NAME.ToString(),
                    StringValues = new List<string> { TestConstants.CS_OBJECT_1_DISPLAY_NAME },
                    Type = AttributeDataType.Text
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.EMAIL_ADDRESS.ToString(),
                    StringValues = new List<string> { "jane.smith@phlebas.tetron.io" },
                    Type = AttributeDataType.Text
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.ROLE.ToString(),
                    StringValues = new List<string> { "Manager" },
                    Type = AttributeDataType.Text
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.PROFILE_PICTURE_BYTES.ToString(),
                    ByteValues = new List<byte[]> { Convert.FromHexString(TestConstants.IMAGE_1_HEX) },
                    Type = AttributeDataType.Binary
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.CONTRACTED_WEEKLY_HOURS.ToString(),
                    IntValues = new List<int> { 32 },
                    Type = AttributeDataType.Number
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.LOCATION_ID.ToString(),
                    GuidValues = new List<Guid> { TestConstants.LOCATION_1_ID },
                    Type = AttributeDataType.Guid
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.LEAVER.ToString(),
                    BoolValue = false,
                    Type = AttributeDataType.Boolean
                }
            }
        });
        mockFileConnector.TestImportObjects.Add(new ConnectedSystemImportObject
        {
            ChangeType = ObjectChangeType.Create,
            ObjectType = "SOURCE_USER",
            Attributes = new List<ConnectedSystemImportObjectAttribute>()
            {
                new ()
                {
                    Name = MockSourceSystemAttributeNames.EMPLOYEE_ID.ToString(),
                    IntValues = new List<int> { 2 },
                    Type = AttributeDataType.Number
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.HR_ID.ToString(),
                    GuidValues = new List<Guid> { TestConstants.CS_OBJECT_2_HR_ID },
                    Type = AttributeDataType.Guid
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.START_DATE.ToString(),
                    DateTimeValue = TestConstants.CS_OBJECT_2_START_DATE,
                    Type = AttributeDataType.DateTime
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.DISPLAY_NAME.ToString(),
                    StringValues = new List<string> { TestConstants.CS_OBJECT_2_DISPLAY_NAME },
                    Type = AttributeDataType.Text
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.EMAIL_ADDRESS.ToString(),
                    StringValues = new List<string> { "joe.bloggs@phlebas.tetron.io" },
                    Type = AttributeDataType.Text
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.ROLE.ToString(),
                    StringValues = new List<string> { "Developer" },
                    Type = AttributeDataType.Text
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.MANAGER.ToString(),
                    ReferenceValues = new List<string> { TestConstants.CS_OBJECT_1_HR_ID.ToString() },
                    Type = AttributeDataType.Reference
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.CONTRACTED_WEEKLY_HOURS.ToString(),
                    IntValues = new List<int> { 40 },
                    Type = AttributeDataType.Number
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.LOCATION_ID.ToString(),
                    GuidValues = new List<Guid> { TestConstants.LOCATION_1_ID },
                    Type = AttributeDataType.Guid
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.LEAVER.ToString(),
                    BoolValue = false,
                    Type = AttributeDataType.Boolean
                }
            }
        });
        
        // now execute Jim functionality we want to test...
        var connectedSystem = await Jim.ConnectedSystems.GetConnectedSystemAsync(1);
        Assert.That(connectedSystem, Is.Not.Null, "Expected to retrieve a Connected System.");

        var activity = ActivitiesData.First();
        var runProfile = ConnectedSystemRunProfilesData.Single(q => q.ConnectedSystemId == connectedSystem.Id && q.RunType == ConnectedSystemRunType.FullImport);
        var synchronisationImportTaskProcessor = new SyncImportTaskProcessor(Jim, mockFileConnector, connectedSystem, runProfile, TestUtilities.CreateTestWorkerTask(activity, InitiatedBy), new CancellationTokenSource());
        await synchronisationImportTaskProcessor.PerformFullImportAsync();
        
        // confirm the results persisted to the mocked db context
        Assert.That(ConnectedSystemObjectsData, Has.Count.EqualTo(2), $"Expected two Connected System Objects to remain persisted. Found {ConnectedSystemObjectsData.Count}.");
        
        // get the Connected System Object for the user we changed some attribute values for in the mocked connector
        var cso1ToValidate = await Jim.ConnectedSystems.GetConnectedSystemObjectAsync(1, TestConstants.CS_OBJECT_1_ID);
        Assert.That(cso1ToValidate, Is.Not.EqualTo(null), "Expected to be able to retrieve the first CSO to validate.");

        var hoursAttribute = cso1ToValidate.GetAttributeValue(MockSourceSystemAttributeNames.CONTRACTED_WEEKLY_HOURS.ToString());
        Assert.That(hoursAttribute, Is.Not.Null);
        Assert.That(hoursAttribute.IntValue, Is.EqualTo(32));
        
        Assert.Pass();
    }
    
    [Test]
    public async Task FullImportUpdateTextSvaTestAsync()
    {
        InitialiseConnectedSystemObjectsData();
        
        // mock up a connector that will return updates for our existing connected system objects above.
        // changes: DISPLAY_NAME has a populated, but different value
        const string newDisplayNameValue = "Jane Smith-Watson";
        var mockFileConnector = new MockFileConnector();
        mockFileConnector.TestImportObjects.Add(new ConnectedSystemImportObject
        {
            ChangeType = ObjectChangeType.Create,
            ObjectType = "SOURCE_USER",
            Attributes = new List<ConnectedSystemImportObjectAttribute>()
            {
                new ()
                {
                    Name = MockSourceSystemAttributeNames.EMPLOYEE_ID.ToString(),
                    IntValues = new List<int> { 1 },
                    Type = AttributeDataType.Number
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.HR_ID.ToString(),
                    GuidValues = new List<Guid> { TestConstants.CS_OBJECT_1_HR_ID },
                    Type = AttributeDataType.Guid
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.START_DATE.ToString(),
                    DateTimeValue = TestConstants.CS_OBJECT_1_START_DATE,
                    Type = AttributeDataType.DateTime
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.DISPLAY_NAME.ToString(),
                    StringValues = new List<string> { newDisplayNameValue },
                    Type = AttributeDataType.Text
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.EMAIL_ADDRESS.ToString(),
                    StringValues = new List<string> { "jane.smith@phlebas.tetron.io" },
                    Type = AttributeDataType.Text
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.ROLE.ToString(),
                    StringValues = new List<string> { "Manager" },
                    Type = AttributeDataType.Text
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.PROFILE_PICTURE_BYTES.ToString(),
                    ByteValues = new List<byte[]> { Convert.FromHexString(TestConstants.IMAGE_1_HEX) },
                    Type = AttributeDataType.Binary
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.CONTRACTED_WEEKLY_HOURS.ToString(),
                    IntValues = new List<int> { 40 },
                    Type = AttributeDataType.Number
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.LOCATION_ID.ToString(),
                    GuidValues = new List<Guid> { TestConstants.LOCATION_1_ID },
                    Type = AttributeDataType.Guid
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.LEAVER.ToString(),
                    BoolValue = false,
                    Type = AttributeDataType.Boolean
                }
            }
        });
        mockFileConnector.TestImportObjects.Add(new ConnectedSystemImportObject
        {
            ChangeType = ObjectChangeType.Create,
            ObjectType = "SOURCE_USER",
            Attributes = new List<ConnectedSystemImportObjectAttribute>()
            {
                new ()
                {
                    Name = MockSourceSystemAttributeNames.EMPLOYEE_ID.ToString(),
                    IntValues = new List<int> { 2 },
                    Type = AttributeDataType.Number
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.HR_ID.ToString(),
                    GuidValues = new List<Guid> { TestConstants.CS_OBJECT_2_HR_ID },
                    Type = AttributeDataType.Guid
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.START_DATE.ToString(),
                    DateTimeValue = TestConstants.CS_OBJECT_2_START_DATE,
                    Type = AttributeDataType.DateTime
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.DISPLAY_NAME.ToString(),
                    StringValues = new List<string> { TestConstants.CS_OBJECT_2_DISPLAY_NAME },
                    Type = AttributeDataType.Text
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.EMAIL_ADDRESS.ToString(),
                    StringValues = new List<string> { "joe.bloggs@phlebas.tetron.io" },
                    Type = AttributeDataType.Text
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.ROLE.ToString(),
                    StringValues = new List<string> { "Developer" },
                    Type = AttributeDataType.Text
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.MANAGER.ToString(),
                    ReferenceValues = new List<string> { TestConstants.CS_OBJECT_1_HR_ID.ToString() },
                    Type = AttributeDataType.Reference
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.CONTRACTED_WEEKLY_HOURS.ToString(),
                    IntValues = new List<int> { 40 },
                    Type = AttributeDataType.Number
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.LOCATION_ID.ToString(),
                    GuidValues = new List<Guid> { TestConstants.LOCATION_1_ID },
                    Type = AttributeDataType.Guid
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.LEAVER.ToString(),
                    BoolValue = false,
                    Type = AttributeDataType.Boolean
                }
            }
        });
        
        // now execute Jim functionality we want to test...
        var connectedSystem = await Jim.ConnectedSystems.GetConnectedSystemAsync(1);
        Assert.That(connectedSystem, Is.Not.Null, "Expected to retrieve a Connected System.");

        var activity = ActivitiesData.First();
        var runProfile = ConnectedSystemRunProfilesData.Single(q => q.ConnectedSystemId == connectedSystem.Id && q.RunType == ConnectedSystemRunType.FullImport);
        var synchronisationImportTaskProcessor = new SyncImportTaskProcessor(Jim, mockFileConnector, connectedSystem, runProfile, TestUtilities.CreateTestWorkerTask(activity, InitiatedBy), new CancellationTokenSource());
        await synchronisationImportTaskProcessor.PerformFullImportAsync();
        
        // confirm the results persisted to the mocked db context
        Assert.That(ConnectedSystemObjectsData, Has.Count.EqualTo(2), $"Expected two Connected System Objects to remain persisted. Found {ConnectedSystemObjectsData.Count}.");
        
        // get the Connected System Object for the user we changed some attribute values for in the mocked connector
        var cso1ToValidate = await Jim.ConnectedSystems.GetConnectedSystemObjectAsync(1, TestConstants.CS_OBJECT_1_ID);
        Assert.That(cso1ToValidate, Is.Not.EqualTo(null), "Expected to be able to retrieve the first CSO to validate.");

        var displayNameAttribute = cso1ToValidate.GetAttributeValue(MockSourceSystemAttributeNames.DISPLAY_NAME.ToString());
        Assert.That(displayNameAttribute, Is.Not.Null);
        Assert.That(displayNameAttribute.StringValue, Is.EqualTo(newDisplayNameValue));
        
        Assert.Pass();
    }
    
    [Test]
    public async Task FullImportUpdateGuidSvaTestAsync()
    {
        InitialiseConnectedSystemObjectsData();
        
        // mock up a connector that will return updates for our existing connected system objects above.
        // changes: LOCATION_ID has a populated, but different value
        var mockFileConnector = new MockFileConnector();
        mockFileConnector.TestImportObjects.Add(new ConnectedSystemImportObject
        {
            ChangeType = ObjectChangeType.Create,
            ObjectType = "SOURCE_USER",
            Attributes = new List<ConnectedSystemImportObjectAttribute>()
            {
                new ()
                {
                    Name = MockSourceSystemAttributeNames.EMPLOYEE_ID.ToString(),
                    IntValues = new List<int> { 1 },
                    Type = AttributeDataType.Number
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.HR_ID.ToString(),
                    GuidValues = new List<Guid> { TestConstants.CS_OBJECT_1_HR_ID },
                    Type = AttributeDataType.Guid
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.START_DATE.ToString(),
                    DateTimeValue = TestConstants.CS_OBJECT_1_START_DATE,
                    Type = AttributeDataType.DateTime
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.DISPLAY_NAME.ToString(),
                    StringValues = new List<string> { TestConstants.CS_OBJECT_1_DISPLAY_NAME },
                    Type = AttributeDataType.Text
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.EMAIL_ADDRESS.ToString(),
                    StringValues = new List<string> { "jane.smith@phlebas.tetron.io" },
                    Type = AttributeDataType.Text
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.ROLE.ToString(),
                    StringValues = new List<string> { "Manager" },
                    Type = AttributeDataType.Text
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.PROFILE_PICTURE_BYTES.ToString(),
                    ByteValues = new List<byte[]> { Convert.FromHexString(TestConstants.IMAGE_1_HEX) },
                    Type = AttributeDataType.Binary
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.CONTRACTED_WEEKLY_HOURS.ToString(),
                    IntValues = new List<int> { 40 },
                    Type = AttributeDataType.Number
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.LOCATION_ID.ToString(),
                    GuidValues = new List<Guid> { TestConstants.LOCATION_2_ID },
                    Type = AttributeDataType.Guid
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.LEAVER.ToString(),
                    BoolValue = false,
                    Type = AttributeDataType.Boolean
                }
            }
        });
        mockFileConnector.TestImportObjects.Add(new ConnectedSystemImportObject
        {
            ChangeType = ObjectChangeType.Create,
            ObjectType = "SOURCE_USER",
            Attributes = new List<ConnectedSystemImportObjectAttribute>()
            {
                new ()
                {
                    Name = MockSourceSystemAttributeNames.EMPLOYEE_ID.ToString(),
                    IntValues = new List<int> { 2 },
                    Type = AttributeDataType.Number
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.HR_ID.ToString(),
                    GuidValues = new List<Guid> { TestConstants.CS_OBJECT_2_HR_ID },
                    Type = AttributeDataType.Guid
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.START_DATE.ToString(),
                    DateTimeValue = TestConstants.CS_OBJECT_2_START_DATE,
                    Type = AttributeDataType.DateTime
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.DISPLAY_NAME.ToString(),
                    StringValues = new List<string> { TestConstants.CS_OBJECT_2_DISPLAY_NAME },
                    Type = AttributeDataType.Text
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.EMAIL_ADDRESS.ToString(),
                    StringValues = new List<string> { "joe.bloggs@phlebas.tetron.io" },
                    Type = AttributeDataType.Text
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.ROLE.ToString(),
                    StringValues = new List<string> { "Developer" },
                    Type = AttributeDataType.Text
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.MANAGER.ToString(),
                    ReferenceValues = new List<string> { TestConstants.CS_OBJECT_1_HR_ID.ToString() },
                    Type = AttributeDataType.Reference
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.CONTRACTED_WEEKLY_HOURS.ToString(),
                    IntValues = new List<int> { 40 },
                    Type = AttributeDataType.Number
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.LOCATION_ID.ToString(),
                    GuidValues = new List<Guid> { TestConstants.LOCATION_1_ID },
                    Type = AttributeDataType.Guid
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.LEAVER.ToString(),
                    BoolValue = false,
                    Type = AttributeDataType.Boolean
                }
            }
        });
        
        // now execute Jim functionality we want to test...
        var connectedSystem = await Jim.ConnectedSystems.GetConnectedSystemAsync(1);
        Assert.That(connectedSystem, Is.Not.Null, "Expected to retrieve a Connected System.");

        var activity = ActivitiesData.First();
        var runProfile = ConnectedSystemRunProfilesData.Single(q => q.ConnectedSystemId == connectedSystem.Id && q.RunType == ConnectedSystemRunType.FullImport);
        var synchronisationImportTaskProcessor = new SyncImportTaskProcessor(Jim, mockFileConnector, connectedSystem, runProfile, TestUtilities.CreateTestWorkerTask(activity, InitiatedBy), new CancellationTokenSource());
        await synchronisationImportTaskProcessor.PerformFullImportAsync();
        
        // confirm the results persisted to the mocked db context
        Assert.That(ConnectedSystemObjectsData, Has.Count.EqualTo(2), $"Expected two Connected System Objects to remain persisted. Found {ConnectedSystemObjectsData.Count}.");
        
        // get the Connected System Object for the user we changed some attribute values for in the mocked connector
        var cso1ToValidate = await Jim.ConnectedSystems.GetConnectedSystemObjectAsync(1, TestConstants.CS_OBJECT_1_ID);
        Assert.That(cso1ToValidate, Is.Not.EqualTo(null), "Expected to be able to retrieve the first CSO to validate.");

        var locationIdAttribute = cso1ToValidate.GetAttributeValue(MockSourceSystemAttributeNames.LOCATION_ID.ToString());
        Assert.That(locationIdAttribute, Is.Not.Null);
        Assert.That(locationIdAttribute.GuidValue.HasValue);
        Assert.That(locationIdAttribute.GuidValue.Value, Is.EqualTo(TestConstants.LOCATION_2_ID));
        
        Assert.Pass();
    }
    
    [Test]
    public async Task FullImportUpdateByteSvaTestAsync()
    {
        InitialiseConnectedSystemObjectsData();
        
        // mock up a connector that will return updates for our existing connected system objects above.
        // changes: PROFILE_PICTURE_BYTES has a populated but different value.
        var newProfilePictureValue = Convert.FromHexString(TestConstants.IMAGE_2_HEX);
        var mockFileConnector = new MockFileConnector();
        mockFileConnector.TestImportObjects.Add(new ConnectedSystemImportObject
        {
            ChangeType = ObjectChangeType.Create,
            ObjectType = "SOURCE_USER",
            Attributes = new List<ConnectedSystemImportObjectAttribute>()
            {
                new ()
                {
                    Name = MockSourceSystemAttributeNames.EMPLOYEE_ID.ToString(),
                    IntValues = new List<int> { 1 },
                    Type = AttributeDataType.Number
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.HR_ID.ToString(),
                    GuidValues = new List<Guid> { TestConstants.CS_OBJECT_1_HR_ID },
                    Type = AttributeDataType.Guid
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.START_DATE.ToString(),
                    DateTimeValue = TestConstants.CS_OBJECT_1_START_DATE,
                    Type = AttributeDataType.DateTime
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.DISPLAY_NAME.ToString(),
                    StringValues = new List<string> { TestConstants.CS_OBJECT_1_DISPLAY_NAME },
                    Type = AttributeDataType.Text
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.EMAIL_ADDRESS.ToString(),
                    StringValues = new List<string> { "jane.smith@phlebas.tetron.io" },
                    Type = AttributeDataType.Text
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.ROLE.ToString(),
                    StringValues = new List<string> { "Manager" },
                    Type = AttributeDataType.Text
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.PROFILE_PICTURE_BYTES.ToString(),
                    ByteValues = new List<byte[]> { newProfilePictureValue },
                    Type = AttributeDataType.Binary
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.CONTRACTED_WEEKLY_HOURS.ToString(),
                    IntValues = new List<int> { 40 },
                    Type = AttributeDataType.Number
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.LOCATION_ID.ToString(),
                    GuidValues = new List<Guid> { TestConstants.LOCATION_1_ID },
                    Type = AttributeDataType.Guid
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.LEAVER.ToString(),
                    BoolValue = false,
                    Type = AttributeDataType.Boolean
                }
            }
        });
        mockFileConnector.TestImportObjects.Add(new ConnectedSystemImportObject
        {
            ChangeType = ObjectChangeType.Create,
            ObjectType = "SOURCE_USER",
            Attributes = new List<ConnectedSystemImportObjectAttribute>()
            {
                new ()
                {
                    Name = MockSourceSystemAttributeNames.EMPLOYEE_ID.ToString(),
                    IntValues = new List<int> { 2 },
                    Type = AttributeDataType.Number
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.HR_ID.ToString(),
                    GuidValues = new List<Guid> { TestConstants.CS_OBJECT_2_HR_ID },
                    Type = AttributeDataType.Guid
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.START_DATE.ToString(),
                    DateTimeValue = TestConstants.CS_OBJECT_2_START_DATE,
                    Type = AttributeDataType.DateTime
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.DISPLAY_NAME.ToString(),
                    StringValues = new List<string> { TestConstants.CS_OBJECT_2_DISPLAY_NAME },
                    Type = AttributeDataType.Text
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.EMAIL_ADDRESS.ToString(),
                    StringValues = new List<string> { "joe.bloggs@phlebas.tetron.io" },
                    Type = AttributeDataType.Text
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.ROLE.ToString(),
                    StringValues = new List<string> { "Developer" },
                    Type = AttributeDataType.Text
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.MANAGER.ToString(),
                    ReferenceValues = new List<string> { TestConstants.CS_OBJECT_1_HR_ID.ToString() },
                    Type = AttributeDataType.Reference
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.CONTRACTED_WEEKLY_HOURS.ToString(),
                    IntValues = new List<int> { 40 },
                    Type = AttributeDataType.Number
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.LOCATION_ID.ToString(),
                    GuidValues = new List<Guid> { TestConstants.LOCATION_1_ID },
                    Type = AttributeDataType.Guid
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.LEAVER.ToString(),
                    BoolValue = false,
                    Type = AttributeDataType.Boolean
                }
            }
        });
        
        // now execute Jim functionality we want to test...
        var connectedSystem = await Jim.ConnectedSystems.GetConnectedSystemAsync(1);
        Assert.That(connectedSystem, Is.Not.Null, "Expected to retrieve a Connected System.");

        var activity = ActivitiesData.First();
        var runProfile = ConnectedSystemRunProfilesData.Single(q => q.ConnectedSystemId == connectedSystem.Id && q.RunType == ConnectedSystemRunType.FullImport);
        var synchronisationImportTaskProcessor = new SyncImportTaskProcessor(Jim, mockFileConnector, connectedSystem, runProfile, TestUtilities.CreateTestWorkerTask(activity, InitiatedBy), new CancellationTokenSource());
        await synchronisationImportTaskProcessor.PerformFullImportAsync();
        
        // confirm the results persisted to the mocked db context
        Assert.That(ConnectedSystemObjectsData, Has.Count.EqualTo(2), $"Expected two Connected System Objects to remain persisted. Found {ConnectedSystemObjectsData.Count}.");
        
        // get the Connected System Object for the user we changed some attribute values for in the mocked connector
        var cso1ToValidate = await Jim.ConnectedSystems.GetConnectedSystemObjectAsync(1, TestConstants.CS_OBJECT_1_ID);
        Assert.That(cso1ToValidate, Is.Not.EqualTo(null), "Expected to be able to retrieve the first CSO to validate.");

        var profilePictureAttribute = cso1ToValidate.GetAttributeValue(MockSourceSystemAttributeNames.PROFILE_PICTURE_BYTES.ToString());
        Assert.That(profilePictureAttribute, Is.Not.Null);
        Assert.That(profilePictureAttribute.ByteValue, Is.EqualTo(newProfilePictureValue));
        
        Assert.Pass();
    }

    [Test]
    public async Task FullImportUpdateDateTimeSvaTestAsync()
    {
        InitialiseConnectedSystemObjectsData();
        
        // mock up a connector that will return updates for our existing connected system objects above.
        // changes: END_DATE has a populated but different value.
        var mockFileConnector = new MockFileConnector();
        mockFileConnector.TestImportObjects.Add(new ConnectedSystemImportObject
        {
            ChangeType = ObjectChangeType.Create,
            ObjectType = "SOURCE_USER",
            Attributes = new List<ConnectedSystemImportObjectAttribute>()
            {
                new ()
                {
                    Name = MockSourceSystemAttributeNames.EMPLOYEE_ID.ToString(),
                    IntValues = new List<int> { 1 },
                    Type = AttributeDataType.Number
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.HR_ID.ToString(),
                    GuidValues = new List<Guid> { TestConstants.CS_OBJECT_1_HR_ID },
                    Type = AttributeDataType.Guid
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.START_DATE.ToString(),
                    DateTimeValue = TestConstants.CS_OBJECT_1_START_DATE,
                    Type = AttributeDataType.DateTime
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.DISPLAY_NAME.ToString(),
                    StringValues = new List<string> { TestConstants.CS_OBJECT_1_DISPLAY_NAME },
                    Type = AttributeDataType.Text
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.EMAIL_ADDRESS.ToString(),
                    StringValues = new List<string> { "jane.smith@phlebas.tetron.io" },
                    Type = AttributeDataType.Text
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.ROLE.ToString(),
                    StringValues = new List<string> { "Manager" },
                    Type = AttributeDataType.Text
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.PROFILE_PICTURE_BYTES.ToString(),
                    ByteValues = new List<byte[]> { Convert.FromHexString(TestConstants.IMAGE_1_HEX) },
                    Type = AttributeDataType.Binary
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.CONTRACTED_WEEKLY_HOURS.ToString(),
                    IntValues = new List<int> { 40 },
                    Type = AttributeDataType.Number
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.LOCATION_ID.ToString(),
                    GuidValues = new List<Guid> { TestConstants.LOCATION_1_ID },
                    Type = AttributeDataType.Guid
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.LEAVER.ToString(),
                    BoolValue = false,
                    Type = AttributeDataType.Boolean
                }
            }
        });
        mockFileConnector.TestImportObjects.Add(new ConnectedSystemImportObject
        {
            ChangeType = ObjectChangeType.Create,
            ObjectType = "SOURCE_USER",
            Attributes = new List<ConnectedSystemImportObjectAttribute>()
            {
                new ()
                {
                    Name = MockSourceSystemAttributeNames.EMPLOYEE_ID.ToString(),
                    IntValues = new List<int> { 2 },
                    Type = AttributeDataType.Number
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.HR_ID.ToString(),
                    GuidValues = new List<Guid> { TestConstants.CS_OBJECT_2_HR_ID },
                    Type = AttributeDataType.Guid
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.START_DATE.ToString(),
                    DateTimeValue = TestConstants.CS_OBJECT_2_START_DATE,
                    Type = AttributeDataType.DateTime
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.DISPLAY_NAME.ToString(),
                    StringValues = new List<string> { TestConstants.CS_OBJECT_2_DISPLAY_NAME },
                    Type = AttributeDataType.Text
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.EMAIL_ADDRESS.ToString(),
                    StringValues = new List<string> { "joe.bloggs@phlebas.tetron.io" },
                    Type = AttributeDataType.Text
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.ROLE.ToString(),
                    StringValues = new List<string> { "Developer" },
                    Type = AttributeDataType.Text
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.MANAGER.ToString(),
                    ReferenceValues = new List<string> { TestConstants.CS_OBJECT_1_HR_ID.ToString() },
                    Type = AttributeDataType.Reference
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.CONTRACTED_WEEKLY_HOURS.ToString(),
                    IntValues = new List<int> { 40 },
                    Type = AttributeDataType.Number
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.LOCATION_ID.ToString(),
                    GuidValues = new List<Guid> { TestConstants.LOCATION_1_ID },
                    Type = AttributeDataType.Guid
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.END_DATE.ToString(),
                    DateTimeValue = TestConstants.CS_OBJECT_2_END_DATE_2,
                    Type = AttributeDataType.DateTime
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.LEAVER.ToString(),
                    BoolValue = false,
                    Type = AttributeDataType.Boolean
                }
            }
        });
        
        // now execute Jim functionality we want to test...
        var connectedSystem = await Jim.ConnectedSystems.GetConnectedSystemAsync(1);
        Assert.That(connectedSystem, Is.Not.Null, "Expected to retrieve a Connected System.");

        var activity = ActivitiesData.First();
        var runProfile = ConnectedSystemRunProfilesData.Single(q => q.ConnectedSystemId == connectedSystem.Id && q.RunType == ConnectedSystemRunType.FullImport);
        var synchronisationImportTaskProcessor = new SyncImportTaskProcessor(Jim, mockFileConnector, connectedSystem, runProfile, TestUtilities.CreateTestWorkerTask(activity, InitiatedBy), new CancellationTokenSource());
        await synchronisationImportTaskProcessor.PerformFullImportAsync();
        
        // confirm the results persisted to the mocked db context
        Assert.That(ConnectedSystemObjectsData, Has.Count.EqualTo(2), $"Expected two Connected System Objects to remain persisted. Found {ConnectedSystemObjectsData.Count}.");
        
        // get the Connected System Object for the user we changed some attribute values for in the mocked connector
        var cso2ToValidate = await Jim.ConnectedSystems.GetConnectedSystemObjectAsync(1, TestConstants.CS_OBJECT_2_ID);
        Assert.That(cso2ToValidate, Is.Not.EqualTo(null), "Expected to be able to retrieve the second CSO to validate.");

        var endDateAttribute = cso2ToValidate.GetAttributeValue(MockSourceSystemAttributeNames.END_DATE.ToString());
        Assert.That(endDateAttribute, Is.Not.Null, "Expected END_DATE to not be null.");
        Assert.That(endDateAttribute.DateTimeValue.HasValue, "Expected END_DATE to have a datetime value.");
        Assert.That(endDateAttribute.DateTimeValue.Value, Is.EqualTo(TestConstants.CS_OBJECT_2_END_DATE_2), $"Expected END_DATE to be a different value. Expected: {TestConstants.CS_OBJECT_2_END_DATE_2}, Received: {endDateAttribute.DateTimeValue.Value}.");
        
        Assert.Pass();
    }
    
    [Test]
    public async Task FullImportUpdateBooleanSvaTestAsync()
    {
        InitialiseConnectedSystemObjectsData();
        
        // mock up a connector that will return updates for our existing connected system objects above.
        // changes: LEAVER has a populated but different value.
        var mockFileConnector = new MockFileConnector();
        mockFileConnector.TestImportObjects.Add(new ConnectedSystemImportObject
        {
            ChangeType = ObjectChangeType.Create,
            ObjectType = "SOURCE_USER",
            Attributes = new List<ConnectedSystemImportObjectAttribute>()
            {
                new ()
                {
                    Name = MockSourceSystemAttributeNames.EMPLOYEE_ID.ToString(),
                    IntValues = new List<int> { 1 },
                    Type = AttributeDataType.Number
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.HR_ID.ToString(),
                    GuidValues = new List<Guid> { TestConstants.CS_OBJECT_1_HR_ID },
                    Type = AttributeDataType.Guid
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.START_DATE.ToString(),
                    DateTimeValue = TestConstants.CS_OBJECT_1_START_DATE,
                    Type = AttributeDataType.DateTime
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.DISPLAY_NAME.ToString(),
                    StringValues = new List<string> { TestConstants.CS_OBJECT_1_DISPLAY_NAME },
                    Type = AttributeDataType.Text
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.EMAIL_ADDRESS.ToString(),
                    StringValues = new List<string> { "jane.smith@phlebas.tetron.io" },
                    Type = AttributeDataType.Text
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.ROLE.ToString(),
                    StringValues = new List<string> { "Manager" },
                    Type = AttributeDataType.Text
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.PROFILE_PICTURE_BYTES.ToString(),
                    ByteValues = new List<byte[]> { Convert.FromHexString(TestConstants.IMAGE_1_HEX) },
                    Type = AttributeDataType.Binary
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.CONTRACTED_WEEKLY_HOURS.ToString(),
                    IntValues = new List<int> { 40 },
                    Type = AttributeDataType.Number
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.LOCATION_ID.ToString(),
                    GuidValues = new List<Guid> { TestConstants.LOCATION_1_ID },
                    Type = AttributeDataType.Guid
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.LEAVER.ToString(),
                    BoolValue = false,
                    Type = AttributeDataType.Boolean
                }
            }
        });
        mockFileConnector.TestImportObjects.Add(new ConnectedSystemImportObject
        {
            ChangeType = ObjectChangeType.Create,
            ObjectType = "SOURCE_USER",
            Attributes = new List<ConnectedSystemImportObjectAttribute>()
            {
                new ()
                {
                    Name = MockSourceSystemAttributeNames.EMPLOYEE_ID.ToString(),
                    IntValues = new List<int> { 2 },
                    Type = AttributeDataType.Number
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.HR_ID.ToString(),
                    GuidValues = new List<Guid> { TestConstants.CS_OBJECT_2_HR_ID },
                    Type = AttributeDataType.Guid
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.START_DATE.ToString(),
                    DateTimeValue = TestConstants.CS_OBJECT_2_START_DATE,
                    Type = AttributeDataType.DateTime
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.DISPLAY_NAME.ToString(),
                    StringValues = new List<string> { TestConstants.CS_OBJECT_2_DISPLAY_NAME },
                    Type = AttributeDataType.Text
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.EMAIL_ADDRESS.ToString(),
                    StringValues = new List<string> { "joe.bloggs@phlebas.tetron.io" },
                    Type = AttributeDataType.Text
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.ROLE.ToString(),
                    StringValues = new List<string> { "Developer" },
                    Type = AttributeDataType.Text
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.MANAGER.ToString(),
                    ReferenceValues = new List<string> { TestConstants.CS_OBJECT_1_HR_ID.ToString() },
                    Type = AttributeDataType.Reference
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.CONTRACTED_WEEKLY_HOURS.ToString(),
                    IntValues = new List<int> { 40 },
                    Type = AttributeDataType.Number
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.LOCATION_ID.ToString(),
                    GuidValues = new List<Guid> { TestConstants.LOCATION_1_ID },
                    Type = AttributeDataType.Guid
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.END_DATE.ToString(),
                    DateTimeValue = TestConstants.CS_OBJECT_2_END_DATE_1,
                    Type = AttributeDataType.DateTime
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.LEAVER.ToString(),
                    BoolValue = true,
                    Type = AttributeDataType.Boolean
                }
            }
        });
        
        // now execute Jim functionality we want to test...
        var connectedSystem = await Jim.ConnectedSystems.GetConnectedSystemAsync(1);
        Assert.That(connectedSystem, Is.Not.Null, "Expected to retrieve a Connected System.");

        var activity = ActivitiesData.First();
        var runProfile = ConnectedSystemRunProfilesData.Single(q => q.ConnectedSystemId == connectedSystem.Id && q.RunType == ConnectedSystemRunType.FullImport);
        var synchronisationImportTaskProcessor = new SyncImportTaskProcessor(Jim, mockFileConnector, connectedSystem, runProfile, TestUtilities.CreateTestWorkerTask(activity, InitiatedBy), new CancellationTokenSource());
        await synchronisationImportTaskProcessor.PerformFullImportAsync();
        
        // confirm the results persisted to the mocked db context
        Assert.That(ConnectedSystemObjectsData, Has.Count.EqualTo(2), $"Expected two Connected System Objects to remain persisted. Found {ConnectedSystemObjectsData.Count}.");
        
        // get the Connected System Object for the user we changed some attribute values for in the mocked connector
        var cso2ToValidate = await Jim.ConnectedSystems.GetConnectedSystemObjectAsync(1, TestConstants.CS_OBJECT_2_ID);
        Assert.That(cso2ToValidate, Is.Not.EqualTo(null), "Expected to be able to retrieve the second CSO to validate.");

        var leaverAttribute = cso2ToValidate.GetAttributeValue(MockSourceSystemAttributeNames.LEAVER.ToString());
        Assert.That(leaverAttribute, Is.Not.Null);
        Assert.That(leaverAttribute.BoolValue.HasValue);
        Assert.That(leaverAttribute.BoolValue.Value, Is.EqualTo(true));
        
        Assert.Pass();
    }

    [Test]
    public async Task FullImportUpdateReferenceSvaTestAsync()
    {
        InitialiseConnectedSystemObjectsData();

        // mock up a connector that will return updates for our existing connected system objects above.
        // changes: MANAGER has a populated but different value.
        var mockFileConnector = new MockFileConnector();
        mockFileConnector.TestImportObjects.Add(new ConnectedSystemImportObject
        {
            ChangeType = ObjectChangeType.Create,
            ObjectType = "SOURCE_USER",
            Attributes = new List<ConnectedSystemImportObjectAttribute>()
            {
                new()
                {
                    Name = MockSourceSystemAttributeNames.EMPLOYEE_ID.ToString(),
                    StringValues = new List<string> { "1" },
                    Type = AttributeDataType.Number
                },
                new()
                {
                    Name = MockSourceSystemAttributeNames.HR_ID.ToString(),
                    GuidValues = new List<Guid> { TestConstants.CS_OBJECT_1_HR_ID },
                    Type = AttributeDataType.Guid
                },
                new()
                {
                    Name = MockSourceSystemAttributeNames.START_DATE.ToString(),
                    DateTimeValue = TestConstants.CS_OBJECT_1_START_DATE,
                    Type = AttributeDataType.DateTime
                },
                new()
                {
                    Name = MockSourceSystemAttributeNames.DISPLAY_NAME.ToString(),
                    StringValues = new List<string> { TestConstants.CS_OBJECT_1_DISPLAY_NAME },
                    Type = AttributeDataType.Text
                },
                new()
                {
                    Name = MockSourceSystemAttributeNames.EMAIL_ADDRESS.ToString(),
                    StringValues = new List<string> { "jane.smith@phlebas.tetron.io" },
                    Type = AttributeDataType.Text
                },
                new()
                {
                    Name = MockSourceSystemAttributeNames.ROLE.ToString(),
                    StringValues = new List<string> { "Manager" },
                    Type = AttributeDataType.Text
                },
                new()
                {
                    Name = MockSourceSystemAttributeNames.PROFILE_PICTURE_BYTES.ToString(),
                    ByteValues = new List<byte[]> { Convert.FromHexString(TestConstants.IMAGE_1_HEX) },
                    Type = AttributeDataType.Binary
                },
                new()
                {
                    Name = MockSourceSystemAttributeNames.CONTRACTED_WEEKLY_HOURS.ToString(),
                    IntValues = new List<int> { 40 },
                    Type = AttributeDataType.Number
                },
                new()
                {
                    Name = MockSourceSystemAttributeNames.LOCATION_ID.ToString(),
                    GuidValues = new List<Guid> { TestConstants.LOCATION_1_ID },
                    Type = AttributeDataType.Guid
                },
                new()
                {
                    Name = MockSourceSystemAttributeNames.LEAVER.ToString(),
                    BoolValue = false,
                    Type = AttributeDataType.Boolean
                }
            }
        });
        
        // update our second user so their new manager is our third object below
        mockFileConnector.TestImportObjects.Add(new ConnectedSystemImportObject
        {
            ChangeType = ObjectChangeType.Create,
            ObjectType = "SOURCE_USER",
            Attributes = new List<ConnectedSystemImportObjectAttribute>()
            {
                new()
                {
                    Name = MockSourceSystemAttributeNames.EMPLOYEE_ID.ToString(),
                    StringValues = new List<string> { "2" },
                    Type = AttributeDataType.Number
                },
                new()
                {
                    Name = MockSourceSystemAttributeNames.HR_ID.ToString(),
                    GuidValues = new List<Guid> { TestConstants.CS_OBJECT_2_HR_ID },
                    Type = AttributeDataType.Guid
                },
                new()
                {
                    Name = MockSourceSystemAttributeNames.START_DATE.ToString(),
                    DateTimeValue = TestConstants.CS_OBJECT_2_START_DATE,
                    Type = AttributeDataType.DateTime
                },
                new()
                {
                    Name = MockSourceSystemAttributeNames.DISPLAY_NAME.ToString(),
                    StringValues = new List<string> { TestConstants.CS_OBJECT_2_DISPLAY_NAME },
                    Type = AttributeDataType.Text
                },
                new()
                {
                    Name = MockSourceSystemAttributeNames.EMAIL_ADDRESS.ToString(),
                    StringValues = new List<string> { "joe.bloggs@phlebas.tetron.io" },
                    Type = AttributeDataType.Text
                },
                new()
                {
                    Name = MockSourceSystemAttributeNames.ROLE.ToString(),
                    StringValues = new List<string> { "Developer" },
                    Type = AttributeDataType.Text
                },
                new()
                {
                    Name = MockSourceSystemAttributeNames.MANAGER.ToString(),
                    ReferenceValues = new List<string> { TestConstants.CS_OBJECT_3_HR_ID.ToString() }, // HR_ID (external id) of cso 3
                    Type = AttributeDataType.Reference
                },
                new()
                {
                    Name = MockSourceSystemAttributeNames.CONTRACTED_WEEKLY_HOURS.ToString(),
                    IntValues = new List<int> { 40 },
                    Type = AttributeDataType.Number
                },
                new()
                {
                    Name = MockSourceSystemAttributeNames.LOCATION_ID.ToString(),
                    GuidValues = new List<Guid> { TestConstants.LOCATION_1_ID },
                    Type = AttributeDataType.Guid
                },
                new()
                {
                    Name = MockSourceSystemAttributeNames.END_DATE.ToString(),
                    DateTimeValue = TestConstants.CS_OBJECT_2_END_DATE_1,
                    Type = AttributeDataType.DateTime
                },
                new()
                {
                    Name = MockSourceSystemAttributeNames.LEAVER.ToString(),
                    BoolValue = false,
                    Type = AttributeDataType.Boolean
                }
            }
        });
        
        // add a new user that will be the new manager for cso2
        // it will also have a MANAGER reference to cso1, so the new hierarchy will be cso2 => cso3 => cso1
        mockFileConnector.TestImportObjects.Add(new ConnectedSystemImportObject
        {
            ChangeType = ObjectChangeType.Create,
            ObjectType = "SOURCE_USER",
            Attributes = new List<ConnectedSystemImportObjectAttribute>()
            {
                new()
                {
                    Name = MockSourceSystemAttributeNames.EMPLOYEE_ID.ToString(),
                    StringValues = new List<string> { "3" },
                    Type = AttributeDataType.Number
                },
                new()
                {
                    Name = MockSourceSystemAttributeNames.HR_ID.ToString(),
                    GuidValues = new List<Guid> { TestConstants.CS_OBJECT_3_HR_ID },
                    Type = AttributeDataType.Guid
                },
                new()
                {
                    Name = MockSourceSystemAttributeNames.START_DATE.ToString(),
                    DateTimeValue = TestConstants.CS_OBJECT_3_START_DATE,
                    Type = AttributeDataType.DateTime
                },
                new()
                {
                    Name = MockSourceSystemAttributeNames.DISPLAY_NAME.ToString(),
                    StringValues = new List<string> { TestConstants.CS_OBJECT_3_DISPLAY_NAME },
                    Type = AttributeDataType.Text
                },
                new()
                {
                    Name = MockSourceSystemAttributeNames.EMAIL_ADDRESS.ToString(),
                    StringValues = new List<string> { TestConstants.CS_OBJECT_3_EMAIL },
                    Type = AttributeDataType.Text
                },
                new()
                {
                    Name = MockSourceSystemAttributeNames.ROLE.ToString(),
                    StringValues = new List<string> { "Manager" },
                    Type = AttributeDataType.Text
                },
                new()
                {
                    Name = MockSourceSystemAttributeNames.MANAGER.ToString(),
                    ReferenceValues = new List<string> { TestConstants.CS_OBJECT_1_HR_ID.ToString() }, // HR_ID (external id) of cso 1
                    Type = AttributeDataType.Reference
                },
                new()
                {
                    Name = MockSourceSystemAttributeNames.CONTRACTED_WEEKLY_HOURS.ToString(),
                    IntValues = new List<int> { 40 },
                    Type = AttributeDataType.Number
                },
                new()
                {
                    Name = MockSourceSystemAttributeNames.LOCATION_ID.ToString(),
                    GuidValues = new List<Guid> { TestConstants.LOCATION_1_ID },
                    Type = AttributeDataType.Guid
                },
                new()
                {
                    Name = MockSourceSystemAttributeNames.LEAVER.ToString(),
                    BoolValue = false,
                    Type = AttributeDataType.Boolean
                }
            }
        });
        
        // now execute Jim functionality we want to test...
        var connectedSystem = await Jim.ConnectedSystems.GetConnectedSystemAsync(1);
        Assert.That(connectedSystem, Is.Not.Null, "Expected to retrieve a Connected System.");

        var activity = ActivitiesData.First();
        var runProfile = ConnectedSystemRunProfilesData.Single(q => q.ConnectedSystemId == connectedSystem.Id && q.RunType == ConnectedSystemRunType.FullImport);
        var synchronisationImportTaskProcessor = new SyncImportTaskProcessor(Jim, mockFileConnector, connectedSystem, runProfile, TestUtilities.CreateTestWorkerTask(activity, InitiatedBy), new CancellationTokenSource());
        await synchronisationImportTaskProcessor.PerformFullImportAsync();
        
        // confirm the results persisted to the mocked db context
        Assert.That(ConnectedSystemObjectsData, Has.Count.EqualTo(3), $"Expected three Connected System Objects to remain persisted. Found {ConnectedSystemObjectsData.Count}.");
        
        // get the new manager
        var cso3 = await Jim.ConnectedSystems.GetConnectedSystemObjectByAttributeAsync(1, (int)MockSourceSystemAttributeNames.EMPLOYEE_ID, "3");
        Assert.That(cso3, Is.Not.EqualTo(null), "Expected to be able to retrieve the new manager (cso3).");
        var cso3ManagerAttribute = cso3.GetAttributeValue(MockSourceSystemAttributeNames.MANAGER.ToString());
        Assert.That(cso3ManagerAttribute, Is.Not.Null);
        Assert.That(cso3ManagerAttribute.ReferenceValue, Is.Not.Null);
        Assert.That(!string.IsNullOrEmpty(cso3ManagerAttribute.UnresolvedReferenceValue), "Expected the MANAGER UnresolvedReferenceValue to also be populated.");
        Assert.That(cso3ManagerAttribute.ReferenceValue.Id, Is.EqualTo(TestConstants.CS_OBJECT_1_ID));
        
        // get the Connected System Object for the user we changed some attribute values for in the mocked connector
        var cso2 = await Jim.ConnectedSystems.GetConnectedSystemObjectAsync(1, TestConstants.CS_OBJECT_2_ID);
        Assert.That(cso2, Is.Not.EqualTo(null), "Expected to be able to retrieve the first CSO to validate.");

        var cso2ManagerAttribute = cso2.GetAttributeValue(MockSourceSystemAttributeNames.MANAGER.ToString());
        Assert.That(cso2ManagerAttribute, Is.Not.Null);
        Assert.That(cso2ManagerAttribute.ReferenceValue, Is.Not.Null);
        Assert.That(cso2ManagerAttribute.ReferenceValue.Id, Is.EqualTo(cso3.Id));
        Assert.That(!string.IsNullOrEmpty(cso2ManagerAttribute.UnresolvedReferenceValue), "Expected the MANAGER UnresolvedReferenceValue to also be populated.");
        
        Assert.Pass();
    }
    
    [Test]
    public async Task FullImportUpdateRemoveTextSvaTestAsync()
    {
        InitialiseConnectedSystemObjectsData();
        
        // mock up a connector that will return updates for our existing connected system objects above.
        // changes: ROLE has been removed.
        var mockFileConnector = new MockFileConnector();
        mockFileConnector.TestImportObjects.Add(new ConnectedSystemImportObject
        {
            ChangeType = ObjectChangeType.Create,
            ObjectType = "SOURCE_USER",
            Attributes = new List<ConnectedSystemImportObjectAttribute>()
            {
                new ()
                {
                    Name = MockSourceSystemAttributeNames.EMPLOYEE_ID.ToString(),
                    IntValues = new List<int> { 1 },
                    Type = AttributeDataType.Number
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.HR_ID.ToString(),
                    GuidValues = new List<Guid> { TestConstants.CS_OBJECT_1_HR_ID },
                    Type = AttributeDataType.Guid
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.START_DATE.ToString(),
                    DateTimeValue = TestConstants.CS_OBJECT_1_START_DATE,
                    Type = AttributeDataType.DateTime
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.DISPLAY_NAME.ToString(),
                    StringValues = new List<string> { TestConstants.CS_OBJECT_1_DISPLAY_NAME },
                    Type = AttributeDataType.Text
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.EMAIL_ADDRESS.ToString(),
                    StringValues = new List<string> { "jane.smith@phlebas.tetron.io" },
                    Type = AttributeDataType.Text
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.PROFILE_PICTURE_BYTES.ToString(),
                    ByteValues = new List<byte[]> { Convert.FromHexString(TestConstants.IMAGE_1_HEX) },
                    Type = AttributeDataType.Binary
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.CONTRACTED_WEEKLY_HOURS.ToString(),
                    IntValues = new List<int> { 40 },
                    Type = AttributeDataType.Number
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.LOCATION_ID.ToString(),
                    GuidValues = new List<Guid> { TestConstants.LOCATION_1_ID },
                    Type = AttributeDataType.Guid
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.LEAVER.ToString(),
                    BoolValue = false,
                    Type = AttributeDataType.Boolean
                }
            }
        });
        
        // now execute Jim functionality we want to test...
        var connectedSystem = await Jim.ConnectedSystems.GetConnectedSystemAsync(1);
        Assert.That(connectedSystem, Is.Not.Null, "Expected to retrieve a Connected System.");

        var activity = ActivitiesData.First();
        var runProfile = ConnectedSystemRunProfilesData.Single(q => q.ConnectedSystemId == connectedSystem.Id && q.RunType == ConnectedSystemRunType.FullImport);
        var synchronisationImportTaskProcessor = new SyncImportTaskProcessor(Jim, mockFileConnector, connectedSystem, runProfile, TestUtilities.CreateTestWorkerTask(activity, InitiatedBy), new CancellationTokenSource());
        await synchronisationImportTaskProcessor.PerformFullImportAsync();
        
        // confirm the results persisted to the mocked db context
        Assert.That(ConnectedSystemObjectsData, Has.Count.EqualTo(2), $"Expected two Connected System Objects to remain persisted. Found {ConnectedSystemObjectsData.Count}.");
        
        // get the Connected System Object for the user we changed some attribute values for in the mocked connector
        var cso1ToValidate = await Jim.ConnectedSystems.GetConnectedSystemObjectAsync(1, TestConstants.CS_OBJECT_1_ID);
        Assert.That(cso1ToValidate, Is.Not.EqualTo(null), "Expected to be able to retrieve the first CSO to validate.");

        var roleAttribute = cso1ToValidate.GetAttributeValue(MockSourceSystemAttributeNames.ROLE.ToString());
        Assert.That(roleAttribute, Is.Null);
        
        Assert.Pass();
    }
    
    [Test]
    public async Task FullImportUpdateBooleanRemoveSvaTestAsync()
    {
        InitialiseConnectedSystemObjectsData();
        
        // mock up a connector that will return updates for our existing connected system objects above.
        // changes: LEAVER has been removed.
        var mockFileConnector = new MockFileConnector();
        mockFileConnector.TestImportObjects.Add(new ConnectedSystemImportObject
        {
            ChangeType = ObjectChangeType.Create,
            ObjectType = "SOURCE_USER",
            Attributes = new List<ConnectedSystemImportObjectAttribute>()
            {
                new ()
                {
                    Name = MockSourceSystemAttributeNames.EMPLOYEE_ID.ToString(),
                    IntValues = new List<int> { 1 },
                    Type = AttributeDataType.Number
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.HR_ID.ToString(),
                    GuidValues = new List<Guid> { TestConstants.CS_OBJECT_1_HR_ID },
                    Type = AttributeDataType.Guid
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.START_DATE.ToString(),
                    DateTimeValue = TestConstants.CS_OBJECT_1_START_DATE,
                    Type = AttributeDataType.DateTime
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.DISPLAY_NAME.ToString(),
                    StringValues = new List<string> { TestConstants.CS_OBJECT_1_DISPLAY_NAME },
                    Type = AttributeDataType.Text
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.EMAIL_ADDRESS.ToString(),
                    StringValues = new List<string> { "jane.smith@phlebas.tetron.io" },
                    Type = AttributeDataType.Text
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.ROLE.ToString(),
                    StringValues = new List<string> { "Manager" },
                    Type = AttributeDataType.Text
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.PROFILE_PICTURE_BYTES.ToString(),
                    ByteValues = new List<byte[]> { Convert.FromHexString(TestConstants.IMAGE_1_HEX) },
                    Type = AttributeDataType.Binary
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.CONTRACTED_WEEKLY_HOURS.ToString(),
                    IntValues = new List<int> { 40 },
                    Type = AttributeDataType.Number
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.LOCATION_ID.ToString(),
                    GuidValues = new List<Guid> { TestConstants.LOCATION_1_ID },
                    Type = AttributeDataType.Guid
                }
            }
        });
        
        // now execute Jim functionality we want to test...
        var connectedSystem = await Jim.ConnectedSystems.GetConnectedSystemAsync(1);
        Assert.That(connectedSystem, Is.Not.Null, "Expected to retrieve a Connected System.");

        var activity = ActivitiesData.First();
        var runProfile = ConnectedSystemRunProfilesData.Single(q => q.ConnectedSystemId == connectedSystem.Id && q.RunType == ConnectedSystemRunType.FullImport);
        var synchronisationImportTaskProcessor = new SyncImportTaskProcessor(Jim, mockFileConnector, connectedSystem, runProfile, TestUtilities.CreateTestWorkerTask(activity, InitiatedBy), new CancellationTokenSource());
        await synchronisationImportTaskProcessor.PerformFullImportAsync();
        
        // confirm the results persisted to the mocked db context
        Assert.That(ConnectedSystemObjectsData, Has.Count.EqualTo(2), $"Expected two Connected System Objects to remain persisted. Found {ConnectedSystemObjectsData.Count}.");
        
        // get the Connected System Object for the user we changed some attribute values for in the mocked connector
        var cso1 = await Jim.ConnectedSystems.GetConnectedSystemObjectAsync(1, TestConstants.CS_OBJECT_1_ID);
        Assert.That(cso1, Is.Not.EqualTo(null), "Expected to be able to retrieve the first CSO to validate.");

        var leaverAttribute = cso1.GetAttributeValue(MockSourceSystemAttributeNames.LEAVER.ToString());
        Assert.That(leaverAttribute, Is.Null);
        
        Assert.Pass();
    }
    
    [Test]
    public async Task FullImportUpdateDateTimeRemoveSvaTestAsync()
    {
        InitialiseConnectedSystemObjectsData();
        
        // mock up a connector that will return updates for our existing connected system objects above.
        // changes: START_DATE has been removed.
        var mockFileConnector = new MockFileConnector();
        mockFileConnector.TestImportObjects.Add(new ConnectedSystemImportObject
        {
            ChangeType = ObjectChangeType.Create,
            ObjectType = "SOURCE_USER",
            Attributes = new List<ConnectedSystemImportObjectAttribute>()
            {
                new ()
                {
                    Name = MockSourceSystemAttributeNames.EMPLOYEE_ID.ToString(),
                    IntValues = new List<int> { 1 },
                    Type = AttributeDataType.Number
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.HR_ID.ToString(),
                    GuidValues = new List<Guid> { TestConstants.CS_OBJECT_1_HR_ID },
                    Type = AttributeDataType.Guid
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.DISPLAY_NAME.ToString(),
                    StringValues = new List<string> { TestConstants.CS_OBJECT_1_DISPLAY_NAME },
                    Type = AttributeDataType.Text
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.EMAIL_ADDRESS.ToString(),
                    StringValues = new List<string> { "jane.smith@phlebas.tetron.io" },
                    Type = AttributeDataType.Text
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.ROLE.ToString(),
                    StringValues = new List<string> { "Manager" },
                    Type = AttributeDataType.Text
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.PROFILE_PICTURE_BYTES.ToString(),
                    ByteValues = new List<byte[]> { Convert.FromHexString(TestConstants.IMAGE_1_HEX) },
                    Type = AttributeDataType.Binary
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.CONTRACTED_WEEKLY_HOURS.ToString(),
                    IntValues = new List<int> { 40 },
                    Type = AttributeDataType.Number
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.LOCATION_ID.ToString(),
                    GuidValues = new List<Guid> { TestConstants.LOCATION_1_ID },
                    Type = AttributeDataType.Guid
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.LEAVER.ToString(),
                    BoolValue = false,
                    Type = AttributeDataType.Boolean
                }
            }
        });
        
        // now execute Jim functionality we want to test...
        var connectedSystem = await Jim.ConnectedSystems.GetConnectedSystemAsync(1);
        Assert.That(connectedSystem, Is.Not.Null, "Expected to retrieve a Connected System.");

        var activity = ActivitiesData.First();
        var runProfile = ConnectedSystemRunProfilesData.Single(q => q.ConnectedSystemId == connectedSystem.Id && q.RunType == ConnectedSystemRunType.FullImport);
        var synchronisationImportTaskProcessor = new SyncImportTaskProcessor(Jim, mockFileConnector, connectedSystem, runProfile, TestUtilities.CreateTestWorkerTask(activity, InitiatedBy), new CancellationTokenSource());
        await synchronisationImportTaskProcessor.PerformFullImportAsync();
        
        // confirm the results persisted to the mocked db context
        Assert.That(ConnectedSystemObjectsData, Has.Count.EqualTo(2), $"Expected two Connected System Objects to remain persisted. Found {ConnectedSystemObjectsData.Count}.");
        
        // get the Connected System Object for the user we changed some attribute values for in the mocked connector
        var cso1 = await Jim.ConnectedSystems.GetConnectedSystemObjectAsync(1, TestConstants.CS_OBJECT_1_ID);
        Assert.That(cso1, Is.Not.EqualTo(null), "Expected to be able to retrieve the first CSO to validate.");

        var startDateAttribute = cso1.GetAttributeValue(MockSourceSystemAttributeNames.START_DATE.ToString());
        Assert.That(startDateAttribute, Is.Null);
        
        Assert.Pass();
    }
    
    [Test]
    public async Task FullImportUpdateGuidRemoveSvaTestAsync()
    {
        InitialiseConnectedSystemObjectsData();
        
        // mock up a connector that will return updates for our existing connected system objects above.
        // changes: LOCATION_ID has been removed.
        var mockFileConnector = new MockFileConnector();
        mockFileConnector.TestImportObjects.Add(new ConnectedSystemImportObject
        {
            ChangeType = ObjectChangeType.Create,
            ObjectType = "SOURCE_USER",
            Attributes = new List<ConnectedSystemImportObjectAttribute>()
            {
                new ()
                {
                    Name = MockSourceSystemAttributeNames.EMPLOYEE_ID.ToString(),
                    IntValues = new List<int> { 1 },
                    Type = AttributeDataType.Number
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.HR_ID.ToString(),
                    GuidValues = new List<Guid> { TestConstants.CS_OBJECT_1_HR_ID },
                    Type = AttributeDataType.Guid
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.START_DATE.ToString(),
                    DateTimeValue = TestConstants.CS_OBJECT_1_START_DATE,
                    Type = AttributeDataType.DateTime
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.DISPLAY_NAME.ToString(),
                    StringValues = new List<string> { TestConstants.CS_OBJECT_1_DISPLAY_NAME },
                    Type = AttributeDataType.Text
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.EMAIL_ADDRESS.ToString(),
                    StringValues = new List<string> { "jane.smith@phlebas.tetron.io" },
                    Type = AttributeDataType.Text
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.ROLE.ToString(),
                    StringValues = new List<string> { "Manager" },
                    Type = AttributeDataType.Text
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.PROFILE_PICTURE_BYTES.ToString(),
                    ByteValues = new List<byte[]> { Convert.FromHexString(TestConstants.IMAGE_1_HEX) },
                    Type = AttributeDataType.Binary
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.CONTRACTED_WEEKLY_HOURS.ToString(),
                    IntValues = new List<int> { 40 },
                    Type = AttributeDataType.Number
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.LEAVER.ToString(),
                    BoolValue = false,
                    Type = AttributeDataType.Boolean
                }
            }
        });
        
        // now execute Jim functionality we want to test...
        var connectedSystem = await Jim.ConnectedSystems.GetConnectedSystemAsync(1);
        Assert.That(connectedSystem, Is.Not.Null, "Expected to retrieve a Connected System.");

        var activity = ActivitiesData.First();
        var runProfile = ConnectedSystemRunProfilesData.Single(q => q.ConnectedSystemId == connectedSystem.Id && q.RunType == ConnectedSystemRunType.FullImport);
        var synchronisationImportTaskProcessor = new SyncImportTaskProcessor(Jim, mockFileConnector, connectedSystem, runProfile, TestUtilities.CreateTestWorkerTask(activity, InitiatedBy), new CancellationTokenSource());
        await synchronisationImportTaskProcessor.PerformFullImportAsync();
        
        // confirm the results persisted to the mocked db context
        Assert.That(ConnectedSystemObjectsData, Has.Count.EqualTo(2), $"Expected two Connected System Objects to remain persisted. Found {ConnectedSystemObjectsData.Count}.");
        
        // get the Connected System Object for the user we changed some attribute values for in the mocked connector
        var cso1 = await Jim.ConnectedSystems.GetConnectedSystemObjectAsync(1, TestConstants.CS_OBJECT_1_ID);
        Assert.That(cso1, Is.Not.EqualTo(null), "Expected to be able to retrieve the first CSO to validate.");

        var locationIdAttribute = cso1.GetAttributeValue(MockSourceSystemAttributeNames.LOCATION_ID.ToString());
        Assert.That(locationIdAttribute, Is.Null);
        
        Assert.Pass();
    }
    
    [Test]
    public async Task FullImportUpdateIntRemoveSvaTestAsync()
    {
        InitialiseConnectedSystemObjectsData();
        
        // mock up a connector that will return updates for our existing connected system objects above.
        // changes: CONTRACTED_WEEKLY_HOURS has been removed.
        var mockFileConnector = new MockFileConnector();
        mockFileConnector.TestImportObjects.Add(new ConnectedSystemImportObject
        {
            ChangeType = ObjectChangeType.Create,
            ObjectType = "SOURCE_USER",
            Attributes = new List<ConnectedSystemImportObjectAttribute>()
            {
                new ()
                {
                    Name = MockSourceSystemAttributeNames.EMPLOYEE_ID.ToString(),
                    IntValues = new List<int> { 1 },
                    Type = AttributeDataType.Number
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.HR_ID.ToString(),
                    GuidValues = new List<Guid> { TestConstants.CS_OBJECT_1_HR_ID },
                    Type = AttributeDataType.Guid
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.START_DATE.ToString(),
                    DateTimeValue = TestConstants.CS_OBJECT_1_START_DATE,
                    Type = AttributeDataType.DateTime
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.DISPLAY_NAME.ToString(),
                    StringValues = new List<string> { TestConstants.CS_OBJECT_1_DISPLAY_NAME },
                    Type = AttributeDataType.Text
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.EMAIL_ADDRESS.ToString(),
                    StringValues = new List<string> { "jane.smith@phlebas.tetron.io" },
                    Type = AttributeDataType.Text
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.ROLE.ToString(),
                    StringValues = new List<string> { "Manager" },
                    Type = AttributeDataType.Text
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.PROFILE_PICTURE_BYTES.ToString(),
                    ByteValues = new List<byte[]> { Convert.FromHexString(TestConstants.IMAGE_1_HEX) },
                    Type = AttributeDataType.Binary
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.LOCATION_ID.ToString(),
                    GuidValues = new List<Guid> { TestConstants.LOCATION_1_ID },
                    Type = AttributeDataType.Guid
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.LEAVER.ToString(),
                    BoolValue = false,
                    Type = AttributeDataType.Boolean
                }
            }
        });
        
        // now execute Jim functionality we want to test...
        var connectedSystem = await Jim.ConnectedSystems.GetConnectedSystemAsync(1);
        Assert.That(connectedSystem, Is.Not.Null, "Expected to retrieve a Connected System.");

        var activity = ActivitiesData.First();
        var runProfile = ConnectedSystemRunProfilesData.Single(q => q.ConnectedSystemId == connectedSystem.Id && q.RunType == ConnectedSystemRunType.FullImport);
        var synchronisationImportTaskProcessor = new SyncImportTaskProcessor(Jim, mockFileConnector, connectedSystem, runProfile, TestUtilities.CreateTestWorkerTask(activity, InitiatedBy), new CancellationTokenSource());
        await synchronisationImportTaskProcessor.PerformFullImportAsync();
        
        // confirm the results persisted to the mocked db context
        Assert.That(ConnectedSystemObjectsData, Has.Count.EqualTo(2), $"Expected two Connected System Objects to remain persisted. Found {ConnectedSystemObjectsData.Count}.");
        
        // get the Connected System Object for the user we changed some attribute values for in the mocked connector
        var cso1 = await Jim.ConnectedSystems.GetConnectedSystemObjectAsync(1, TestConstants.CS_OBJECT_1_ID);
        Assert.That(cso1, Is.Not.EqualTo(null), "Expected to be able to retrieve the first CSO to validate.");

        var hoursAttribute = cso1.GetAttributeValue(MockSourceSystemAttributeNames.CONTRACTED_WEEKLY_HOURS.ToString());
        Assert.That(hoursAttribute, Is.Null);
        
        Assert.Pass();
    }
    
    [Test]
    public async Task FullImportUpdateReferenceRemoveSvaTestAsync()
    {
        InitialiseConnectedSystemObjectsData();

        // mock up a connector that will return updates for our existing connected system objects above.
        // changes: MANAGER has been removed.
        var mockFileConnector = new MockFileConnector();
        
        // update our second user by removing their manager reference.
        mockFileConnector.TestImportObjects.Add(new ConnectedSystemImportObject
        {
            ChangeType = ObjectChangeType.Create,
            ObjectType = "SOURCE_USER",
            Attributes = new List<ConnectedSystemImportObjectAttribute>()
            {
                new()
                {
                    Name = MockSourceSystemAttributeNames.EMPLOYEE_ID.ToString(),
                    IntValues = new List<int> { 2 },
                    Type = AttributeDataType.Number
                },
                new()
                {
                    Name = MockSourceSystemAttributeNames.HR_ID.ToString(),
                    GuidValues = new List<Guid> { TestConstants.CS_OBJECT_2_HR_ID },
                    Type = AttributeDataType.Guid
                },
                new()
                {
                    Name = MockSourceSystemAttributeNames.START_DATE.ToString(),
                    DateTimeValue = TestConstants.CS_OBJECT_2_START_DATE,
                    Type = AttributeDataType.DateTime
                },
                new()
                {
                    Name = MockSourceSystemAttributeNames.DISPLAY_NAME.ToString(),
                    StringValues = new List<string> { TestConstants.CS_OBJECT_2_DISPLAY_NAME },
                    Type = AttributeDataType.Text
                },
                new()
                {
                    Name = MockSourceSystemAttributeNames.EMAIL_ADDRESS.ToString(),
                    StringValues = new List<string> { "joe.bloggs@phlebas.tetron.io" },
                    Type = AttributeDataType.Text
                },
                new()
                {
                    Name = MockSourceSystemAttributeNames.ROLE.ToString(),
                    StringValues = new List<string> { "Developer" },
                    Type = AttributeDataType.Text
                },
                new()
                {
                    Name = MockSourceSystemAttributeNames.CONTRACTED_WEEKLY_HOURS.ToString(),
                    IntValues = new List<int> { 40 },
                    Type = AttributeDataType.Number
                },
                new()
                {
                    Name = MockSourceSystemAttributeNames.LOCATION_ID.ToString(),
                    GuidValues = new List<Guid> { TestConstants.LOCATION_1_ID },
                    Type = AttributeDataType.Guid
                },
                new()
                {
                    Name = MockSourceSystemAttributeNames.END_DATE.ToString(),
                    DateTimeValue = TestConstants.CS_OBJECT_2_END_DATE_1,
                    Type = AttributeDataType.DateTime
                },
                new()
                {
                    Name = MockSourceSystemAttributeNames.LEAVER.ToString(),
                    BoolValue = false,
                    Type = AttributeDataType.Boolean
                }
            }
        });
        
        // now execute Jim functionality we want to test...
        var connectedSystem = await Jim.ConnectedSystems.GetConnectedSystemAsync(1);
        Assert.That(connectedSystem, Is.Not.Null, "Expected to retrieve a Connected System.");

        var activity = ActivitiesData.First();
        var runProfile = ConnectedSystemRunProfilesData.Single(q => q.ConnectedSystemId == connectedSystem.Id && q.RunType == ConnectedSystemRunType.FullImport);
        var synchronisationImportTaskProcessor = new SyncImportTaskProcessor(Jim, mockFileConnector, connectedSystem, runProfile, TestUtilities.CreateTestWorkerTask(activity, InitiatedBy), new CancellationTokenSource());
        await synchronisationImportTaskProcessor.PerformFullImportAsync();
        
        // confirm the results persisted to the mocked db context
        Assert.That(ConnectedSystemObjectsData, Has.Count.EqualTo(2), $"Expected two Connected System Objects to remain persisted. Found {ConnectedSystemObjectsData.Count}.");
        
        // get the Connected System Object for the user we changed some attribute values for in the mocked connector
        var cso2 = await Jim.ConnectedSystems.GetConnectedSystemObjectAsync(1, TestConstants.CS_OBJECT_2_ID);
        Assert.That(cso2, Is.Not.EqualTo(null), "Expected to be able to retrieve the first CSO to validate.");

        var cso2ManagerAttribute = cso2.GetAttributeValue(MockSourceSystemAttributeNames.MANAGER.ToString());
        Assert.That(cso2ManagerAttribute, Is.Null);
        
        Assert.Pass();
    }
    
    [Test]
    public async Task FullImportUpdateByteRemoveSvaTestAsync()
    {
        InitialiseConnectedSystemObjectsData();
        
        // mock up a connector that will return updates for our existing connected system objects above.
        // changes: PROFILE_PICTURE_BYTES has been removed.
        var mockFileConnector = new MockFileConnector();
        mockFileConnector.TestImportObjects.Add(new ConnectedSystemImportObject
        {
            ChangeType = ObjectChangeType.Create,
            ObjectType = "SOURCE_USER",
            Attributes = new List<ConnectedSystemImportObjectAttribute>()
            {
                new ()
                {
                    Name = MockSourceSystemAttributeNames.EMPLOYEE_ID.ToString(),
                    IntValues = new List<int> { 1 },
                    Type = AttributeDataType.Number
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.HR_ID.ToString(),
                    GuidValues = new List<Guid> { TestConstants.CS_OBJECT_1_HR_ID },
                    Type = AttributeDataType.Guid
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.START_DATE.ToString(),
                    DateTimeValue = TestConstants.CS_OBJECT_1_START_DATE,
                    Type = AttributeDataType.DateTime
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.DISPLAY_NAME.ToString(),
                    StringValues = new List<string> { TestConstants.CS_OBJECT_1_DISPLAY_NAME },
                    Type = AttributeDataType.Text
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.EMAIL_ADDRESS.ToString(),
                    StringValues = new List<string> { "jane.smith@phlebas.tetron.io" },
                    Type = AttributeDataType.Text
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.ROLE.ToString(),
                    StringValues = new List<string> { "Manager" },
                    Type = AttributeDataType.Text
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.CONTRACTED_WEEKLY_HOURS.ToString(),
                    IntValues = new List<int> { 40 },
                    Type = AttributeDataType.Number
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.LOCATION_ID.ToString(),
                    GuidValues = new List<Guid> { TestConstants.LOCATION_1_ID },
                    Type = AttributeDataType.Guid
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.LEAVER.ToString(),
                    BoolValue = false,
                    Type = AttributeDataType.Boolean
                }
            }
        });
        
        // now execute Jim functionality we want to test...
        var connectedSystem = await Jim.ConnectedSystems.GetConnectedSystemAsync(1);
        Assert.That(connectedSystem, Is.Not.Null, "Expected to retrieve a Connected System.");

        var activity = ActivitiesData.First();
        var runProfile = ConnectedSystemRunProfilesData.Single(q => q.ConnectedSystemId == connectedSystem.Id && q.RunType == ConnectedSystemRunType.FullImport);
        var synchronisationImportTaskProcessor = new SyncImportTaskProcessor(Jim, mockFileConnector, connectedSystem, runProfile, TestUtilities.CreateTestWorkerTask(activity, InitiatedBy), new CancellationTokenSource());
        await synchronisationImportTaskProcessor.PerformFullImportAsync();
        
        // confirm the results persisted to the mocked db context
        Assert.That(ConnectedSystemObjectsData, Has.Count.EqualTo(2), $"Expected two Connected System Objects to remain persisted. Found {ConnectedSystemObjectsData.Count}.");
        
        // get the Connected System Object for the user we changed some attribute values for in the mocked connector
        var cso1 = await Jim.ConnectedSystems.GetConnectedSystemObjectAsync(1, TestConstants.CS_OBJECT_1_ID);
        Assert.That(cso1, Is.Not.EqualTo(null), "Expected to be able to retrieve the first CSO to validate.");

        var profilePictureAttribute = cso1.GetAttributeValue(MockSourceSystemAttributeNames.PROFILE_PICTURE_BYTES.ToString());
        Assert.That(profilePictureAttribute, Is.Null);
        
        Assert.Pass();
    }
    
    [Test]
    public async Task FullImportUpdateIntAddSvaTestAsync()
    {
        InitialiseConnectedSystemObjectsData();
        
        // mock up a connector that will return updates for our existing connected system objects above.
        // changes: COURSE_COUNT has a value added for the first time.
        var mockFileConnector = new MockFileConnector();
        mockFileConnector.TestImportObjects.Add(new ConnectedSystemImportObject
        {
            ChangeType = ObjectChangeType.Create,
            ObjectType = "SOURCE_USER",
            Attributes = new List<ConnectedSystemImportObjectAttribute>()
            {
                new ()
                {
                    Name = MockSourceSystemAttributeNames.EMPLOYEE_ID.ToString(),
                    IntValues = new List<int> { 1 },
                    Type = AttributeDataType.Number
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.HR_ID.ToString(),
                    GuidValues = new List<Guid> { TestConstants.CS_OBJECT_1_HR_ID },
                    Type = AttributeDataType.Guid
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.START_DATE.ToString(),
                    DateTimeValue = TestConstants.CS_OBJECT_1_START_DATE,
                    Type = AttributeDataType.DateTime
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.DISPLAY_NAME.ToString(),
                    StringValues = new List<string> { TestConstants.CS_OBJECT_1_DISPLAY_NAME },
                    Type = AttributeDataType.Text
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.EMAIL_ADDRESS.ToString(),
                    StringValues = new List<string> { "jane.smith@phlebas.tetron.io" },
                    Type = AttributeDataType.Text
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.ROLE.ToString(),
                    StringValues = new List<string> { "Manager" },
                    Type = AttributeDataType.Text
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.PROFILE_PICTURE_BYTES.ToString(),
                    ByteValues = new List<byte[]> { Convert.FromHexString(TestConstants.IMAGE_1_HEX) },
                    Type = AttributeDataType.Binary
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.CONTRACTED_WEEKLY_HOURS.ToString(),
                    IntValues = new List<int> { 32 },
                    Type = AttributeDataType.Number
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.LOCATION_ID.ToString(),
                    GuidValues = new List<Guid> { TestConstants.LOCATION_1_ID },
                    Type = AttributeDataType.Guid
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.LEAVER.ToString(),
                    BoolValue = false,
                    Type = AttributeDataType.Boolean
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.COURSE_COUNT.ToString(),
                    IntValues = new List<int> { 3 },
                    Type = AttributeDataType.Number
                }
            }
        });
        
        // now execute Jim functionality we want to test...
        var connectedSystem = await Jim.ConnectedSystems.GetConnectedSystemAsync(1);
        Assert.That(connectedSystem, Is.Not.Null, "Expected to retrieve a Connected System.");

        var activity = ActivitiesData.First();
        var runProfile = ConnectedSystemRunProfilesData.Single(q => q.ConnectedSystemId == connectedSystem.Id && q.RunType == ConnectedSystemRunType.FullImport);
        var synchronisationImportTaskProcessor = new SyncImportTaskProcessor(Jim, mockFileConnector, connectedSystem, runProfile, TestUtilities.CreateTestWorkerTask(activity, InitiatedBy), new CancellationTokenSource());
        await synchronisationImportTaskProcessor.PerformFullImportAsync();
        
        // confirm the results persisted to the mocked db context
        Assert.That(ConnectedSystemObjectsData, Has.Count.EqualTo(2), $"Expected two Connected System Objects to remain persisted. Found {ConnectedSystemObjectsData.Count}.");
        
        // get the Connected System Object for the user we changed some attribute values for in the mocked connector
        var cso1ToValidate = await Jim.ConnectedSystems.GetConnectedSystemObjectAsync(1, TestConstants.CS_OBJECT_1_ID);
        Assert.That(cso1ToValidate, Is.Not.EqualTo(null), "Expected to be able to retrieve the first CSO to validate.");

        var courseCountAttribute = cso1ToValidate.GetAttributeValue(MockSourceSystemAttributeNames.COURSE_COUNT.ToString());
        Assert.That(courseCountAttribute, Is.Not.Null);
        Assert.That(courseCountAttribute.IntValue, Is.EqualTo(3));
        
        Assert.Pass();
    }
    
    [Test]
    public async Task FullImportUpdateDateTimeAddSvaTestAsync()
    {
        InitialiseConnectedSystemObjectsData();
        
        // mock up a connector that will return updates for our existing connected system objects above.
        // changes: COURSE_END_DATE is being populated for the first time.
        var mockFileConnector = new MockFileConnector();
        var courseEndDate = DateTime.UtcNow;
        mockFileConnector.TestImportObjects.Add(new ConnectedSystemImportObject
        {
            ChangeType = ObjectChangeType.Create,
            ObjectType = "SOURCE_USER",
            Attributes = new List<ConnectedSystemImportObjectAttribute>()
            {
                new ()
                {
                    Name = MockSourceSystemAttributeNames.EMPLOYEE_ID.ToString(),
                    IntValues = new List<int> { 1 },
                    Type = AttributeDataType.Number
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.HR_ID.ToString(),
                    GuidValues = new List<Guid> { TestConstants.CS_OBJECT_1_HR_ID },
                    Type = AttributeDataType.Guid
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.START_DATE.ToString(),
                    DateTimeValue = TestConstants.CS_OBJECT_1_START_DATE,
                    Type = AttributeDataType.DateTime
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.DISPLAY_NAME.ToString(),
                    StringValues = new List<string> { TestConstants.CS_OBJECT_1_DISPLAY_NAME },
                    Type = AttributeDataType.Text
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.EMAIL_ADDRESS.ToString(),
                    StringValues = new List<string> { "jane.smith@phlebas.tetron.io" },
                    Type = AttributeDataType.Text
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.ROLE.ToString(),
                    StringValues = new List<string> { "Manager" },
                    Type = AttributeDataType.Text
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.PROFILE_PICTURE_BYTES.ToString(),
                    ByteValues = new List<byte[]> { Convert.FromHexString(TestConstants.IMAGE_1_HEX) },
                    Type = AttributeDataType.Binary
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.CONTRACTED_WEEKLY_HOURS.ToString(),
                    IntValues = new List<int> { 40 },
                    Type = AttributeDataType.Number
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.LOCATION_ID.ToString(),
                    GuidValues = new List<Guid> { TestConstants.LOCATION_1_ID },
                    Type = AttributeDataType.Guid
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.LEAVER.ToString(),
                    BoolValue = false,
                    Type = AttributeDataType.Boolean
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.COURSE_END_DATE.ToString(),
                    DateTimeValue = courseEndDate,
                    Type = AttributeDataType.DateTime
                }
            }
        });
        
        // now execute Jim functionality we want to test...
        var connectedSystem = await Jim.ConnectedSystems.GetConnectedSystemAsync(1);
        Assert.That(connectedSystem, Is.Not.Null, "Expected to retrieve a Connected System.");

        var activity = ActivitiesData.First();
        var runProfile = ConnectedSystemRunProfilesData.Single(q => q.ConnectedSystemId == connectedSystem.Id && q.RunType == ConnectedSystemRunType.FullImport);
        var synchronisationImportTaskProcessor = new SyncImportTaskProcessor(Jim, mockFileConnector, connectedSystem, runProfile, TestUtilities.CreateTestWorkerTask(activity, InitiatedBy), new CancellationTokenSource());
        await synchronisationImportTaskProcessor.PerformFullImportAsync();
        
        // confirm the results persisted to the mocked db context
        Assert.That(ConnectedSystemObjectsData, Has.Count.EqualTo(2), $"Expected two Connected System Objects to remain persisted. Found {ConnectedSystemObjectsData.Count}.");
        
        // get the Connected System Object for the user we changed some attribute values for in the mocked connector
        var cso1 = await Jim.ConnectedSystems.GetConnectedSystemObjectAsync(1, TestConstants.CS_OBJECT_1_ID);
        Assert.That(cso1, Is.Not.EqualTo(null), "Expected to be able to retrieve the first CSO to validate.");

        var courseEndDateAttribute = cso1.GetAttributeValue(MockSourceSystemAttributeNames.COURSE_END_DATE.ToString());
        Assert.That(courseEndDateAttribute, Is.Not.Null);
        Assert.That(courseEndDateAttribute.DateTimeValue.HasValue);
        Assert.That(courseEndDateAttribute.DateTimeValue.Value, Is.EqualTo(courseEndDate));
        
        Assert.Pass();
    }
    
    [Test]
    public async Task FullImportUpdateTextAddSvaTestAsync()
    {
        InitialiseConnectedSystemObjectsData();
        
        // mock up a connector that will return updates for our existing connected system objects above.
        // changes: CURRENT_COURSE_NAME gets a value for the first time.
        const string currentCourseName = "HGV Training L1";
        var mockFileConnector = new MockFileConnector();
        mockFileConnector.TestImportObjects.Add(new ConnectedSystemImportObject
        {
            ChangeType = ObjectChangeType.Create,
            ObjectType = "SOURCE_USER",
            Attributes = new List<ConnectedSystemImportObjectAttribute>()
            {
                new ()
                {
                    Name = MockSourceSystemAttributeNames.EMPLOYEE_ID.ToString(),
                    IntValues = new List<int> { 1 },
                    Type = AttributeDataType.Number
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.HR_ID.ToString(),
                    GuidValues = new List<Guid> { TestConstants.CS_OBJECT_1_HR_ID },
                    Type = AttributeDataType.Guid
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.START_DATE.ToString(),
                    DateTimeValue = TestConstants.CS_OBJECT_1_START_DATE,
                    Type = AttributeDataType.DateTime
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.DISPLAY_NAME.ToString(),
                    StringValues = new List<string> { TestConstants.CS_OBJECT_1_DISPLAY_NAME },
                    Type = AttributeDataType.Text
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.EMAIL_ADDRESS.ToString(),
                    StringValues = new List<string> { "jane.smith@phlebas.tetron.io" },
                    Type = AttributeDataType.Text
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.ROLE.ToString(),
                    StringValues = new List<string> { "Manager" },
                    Type = AttributeDataType.Text
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.PROFILE_PICTURE_BYTES.ToString(),
                    ByteValues = new List<byte[]> { Convert.FromHexString(TestConstants.IMAGE_1_HEX) },
                    Type = AttributeDataType.Binary
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.CONTRACTED_WEEKLY_HOURS.ToString(),
                    IntValues = new List<int> { 40 },
                    Type = AttributeDataType.Number
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.LOCATION_ID.ToString(),
                    GuidValues = new List<Guid> { TestConstants.LOCATION_1_ID },
                    Type = AttributeDataType.Guid
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.LEAVER.ToString(),
                    BoolValue = false,
                    Type = AttributeDataType.Boolean
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.CURRENT_COURSE_NAME.ToString(),
                    StringValues = new List<string> { currentCourseName },
                    Type = AttributeDataType.Text
                }
            }
        });
        
        // now execute Jim functionality we want to test...
        var connectedSystem = await Jim.ConnectedSystems.GetConnectedSystemAsync(1);
        Assert.That(connectedSystem, Is.Not.Null, "Expected to retrieve a Connected System.");

        var activity = ActivitiesData.First();
        var runProfile = ConnectedSystemRunProfilesData.Single(q => q.ConnectedSystemId == connectedSystem.Id && q.RunType == ConnectedSystemRunType.FullImport);
        var synchronisationImportTaskProcessor = new SyncImportTaskProcessor(Jim, mockFileConnector, connectedSystem, runProfile, TestUtilities.CreateTestWorkerTask(activity, InitiatedBy), new CancellationTokenSource());
        await synchronisationImportTaskProcessor.PerformFullImportAsync();
        
        // confirm the results persisted to the mocked db context
        Assert.That(ConnectedSystemObjectsData, Has.Count.EqualTo(2), $"Expected two Connected System Objects to remain persisted. Found {ConnectedSystemObjectsData.Count}.");
        
        // get the Connected System Object for the user we changed some attribute values for in the mocked connector
        var cso1 = await Jim.ConnectedSystems.GetConnectedSystemObjectAsync(1, TestConstants.CS_OBJECT_1_ID);
        Assert.That(cso1, Is.Not.EqualTo(null), "Expected to be able to retrieve the first CSO to validate.");

        var currentCourseNameAttribute = cso1.GetAttributeValue(MockSourceSystemAttributeNames.CURRENT_COURSE_NAME.ToString());
        Assert.That(currentCourseNameAttribute, Is.Not.Null);
        Assert.That(currentCourseNameAttribute.StringValue, Is.EqualTo(currentCourseName));
        
        Assert.Pass();
    }
    
    [Test]
    public async Task FullImportUpdateGuidAddSvaTestAsync()
    {
        InitialiseConnectedSystemObjectsData();
        
        // mock up a connector that will return updates for our existing connected system objects above.
        // changes: CURRENT_COURSE_ID gets a value for the first time.
        var currentCourseId = Guid.NewGuid();
        var mockFileConnector = new MockFileConnector();
        mockFileConnector.TestImportObjects.Add(new ConnectedSystemImportObject
        {
            ChangeType = ObjectChangeType.Create,
            ObjectType = "SOURCE_USER",
            Attributes = new List<ConnectedSystemImportObjectAttribute>()
            {
                new ()
                {
                    Name = MockSourceSystemAttributeNames.EMPLOYEE_ID.ToString(),
                    IntValues = new List<int> { 1 },
                    Type = AttributeDataType.Number
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.HR_ID.ToString(),
                    GuidValues = new List<Guid> { TestConstants.CS_OBJECT_1_HR_ID },
                    Type = AttributeDataType.Guid
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.START_DATE.ToString(),
                    DateTimeValue = TestConstants.CS_OBJECT_1_START_DATE,
                    Type = AttributeDataType.DateTime
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.DISPLAY_NAME.ToString(),
                    StringValues = new List<string> { TestConstants.CS_OBJECT_1_DISPLAY_NAME },
                    Type = AttributeDataType.Text
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.EMAIL_ADDRESS.ToString(),
                    StringValues = new List<string> { "jane.smith@phlebas.tetron.io" },
                    Type = AttributeDataType.Text
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.ROLE.ToString(),
                    StringValues = new List<string> { "Manager" },
                    Type = AttributeDataType.Text
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.PROFILE_PICTURE_BYTES.ToString(),
                    ByteValues = new List<byte[]> { Convert.FromHexString(TestConstants.IMAGE_1_HEX) },
                    Type = AttributeDataType.Binary
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.CONTRACTED_WEEKLY_HOURS.ToString(),
                    IntValues = new List<int> { 40 },
                    Type = AttributeDataType.Number
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.LOCATION_ID.ToString(),
                    GuidValues = new List<Guid> { TestConstants.LOCATION_1_ID },
                    Type = AttributeDataType.Guid
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.LEAVER.ToString(),
                    BoolValue = false,
                    Type = AttributeDataType.Boolean
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.CURRENT_COURSE_ID.ToString(),
                    GuidValues = new List<Guid> { currentCourseId },
                    Type = AttributeDataType.Guid
                },
            }
        });
        
        // now execute Jim functionality we want to test...
        var connectedSystem = await Jim.ConnectedSystems.GetConnectedSystemAsync(1);
        Assert.That(connectedSystem, Is.Not.Null, "Expected to retrieve a Connected System.");

        var activity = ActivitiesData.First();
        var runProfile = ConnectedSystemRunProfilesData.Single(q => q.ConnectedSystemId == connectedSystem.Id && q.RunType == ConnectedSystemRunType.FullImport);
        var synchronisationImportTaskProcessor = new SyncImportTaskProcessor(Jim, mockFileConnector, connectedSystem, runProfile, TestUtilities.CreateTestWorkerTask(activity, InitiatedBy), new CancellationTokenSource());
        await synchronisationImportTaskProcessor.PerformFullImportAsync();
        
        // confirm the results persisted to the mocked db context
        Assert.That(ConnectedSystemObjectsData, Has.Count.EqualTo(2), $"Expected two Connected System Objects to remain persisted. Found {ConnectedSystemObjectsData.Count}.");
        
        // get the Connected System Object for the user we changed some attribute values for in the mocked connector
        var cso1 = await Jim.ConnectedSystems.GetConnectedSystemObjectAsync(1, TestConstants.CS_OBJECT_1_ID);
        Assert.That(cso1, Is.Not.EqualTo(null), "Expected to be able to retrieve the first CSO to validate.");

        var currentCourseIdAttribute = cso1.GetAttributeValue(MockSourceSystemAttributeNames.CURRENT_COURSE_ID.ToString());
        Assert.That(currentCourseIdAttribute, Is.Not.Null);
        Assert.That(currentCourseIdAttribute.GuidValue.HasValue);
        Assert.That(currentCourseIdAttribute.GuidValue.Value, Is.EqualTo(currentCourseId));
        
        Assert.Pass();
    }
    
    [Test]
    public async Task FullImportUpdateBooleanAddSvaTestAsync()
    {
        InitialiseConnectedSystemObjectsData();
        
        // mock up a connector that will return updates for our existing connected system objects above.
        // changes: CURRENT_COURSE_ACTIVE has a value set for the first time.
        var mockFileConnector = new MockFileConnector();
        mockFileConnector.TestImportObjects.Add(new ConnectedSystemImportObject
        {
            ChangeType = ObjectChangeType.Create,
            ObjectType = "SOURCE_USER",
            Attributes = new List<ConnectedSystemImportObjectAttribute>()
            {
                new ()
                {
                    Name = MockSourceSystemAttributeNames.EMPLOYEE_ID.ToString(),
                    IntValues = new List<int> { 1 },
                    Type = AttributeDataType.Number
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.HR_ID.ToString(),
                    GuidValues = new List<Guid> { TestConstants.CS_OBJECT_1_HR_ID },
                    Type = AttributeDataType.Guid
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.START_DATE.ToString(),
                    DateTimeValue = TestConstants.CS_OBJECT_1_START_DATE,
                    Type = AttributeDataType.DateTime
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.DISPLAY_NAME.ToString(),
                    StringValues = new List<string> { TestConstants.CS_OBJECT_1_DISPLAY_NAME },
                    Type = AttributeDataType.Text
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.EMAIL_ADDRESS.ToString(),
                    StringValues = new List<string> { "jane.smith@phlebas.tetron.io" },
                    Type = AttributeDataType.Text
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.ROLE.ToString(),
                    StringValues = new List<string> { "Manager" },
                    Type = AttributeDataType.Text
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.PROFILE_PICTURE_BYTES.ToString(),
                    ByteValues = new List<byte[]> { Convert.FromHexString(TestConstants.IMAGE_1_HEX) },
                    Type = AttributeDataType.Binary
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.CONTRACTED_WEEKLY_HOURS.ToString(),
                    IntValues = new List<int> { 40 },
                    Type = AttributeDataType.Number
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.LOCATION_ID.ToString(),
                    GuidValues = new List<Guid> { TestConstants.LOCATION_1_ID },
                    Type = AttributeDataType.Guid
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.LEAVER.ToString(),
                    BoolValue = false,
                    Type = AttributeDataType.Boolean
                },
                new ()
                {
                    Name = MockSourceSystemAttributeNames.CURRENT_COURSE_ACTIVE.ToString(),
                    BoolValue = true,
                    Type = AttributeDataType.Boolean
                }
            }
        });
        
        // now execute Jim functionality we want to test...
        var connectedSystem = await Jim.ConnectedSystems.GetConnectedSystemAsync(1);
        Assert.That(connectedSystem, Is.Not.Null, "Expected to retrieve a Connected System.");

        var activity = ActivitiesData.First();
        var runProfile = ConnectedSystemRunProfilesData.Single(q => q.ConnectedSystemId == connectedSystem.Id && q.RunType == ConnectedSystemRunType.FullImport);
        var synchronisationImportTaskProcessor = new SyncImportTaskProcessor(Jim, mockFileConnector, connectedSystem, runProfile, TestUtilities.CreateTestWorkerTask(activity, InitiatedBy), new CancellationTokenSource());
        await synchronisationImportTaskProcessor.PerformFullImportAsync();
        
        // confirm the results persisted to the mocked db context
        Assert.That(ConnectedSystemObjectsData, Has.Count.EqualTo(2), $"Expected two Connected System Objects to remain persisted. Found {ConnectedSystemObjectsData.Count}.");
        
        // get the Connected System Object for the user we changed some attribute values for in the mocked connector
        var cso1 = await Jim.ConnectedSystems.GetConnectedSystemObjectAsync(1, TestConstants.CS_OBJECT_1_ID);
        Assert.That(cso1, Is.Not.EqualTo(null), "Expected to be able to retrieve the second CSO to validate.");

        var currentCourseActiveAttribute = cso1.GetAttributeValue(MockSourceSystemAttributeNames.CURRENT_COURSE_ACTIVE.ToString());
        Assert.That(currentCourseActiveAttribute, Is.Not.Null);
        Assert.That(currentCourseActiveAttribute.BoolValue.HasValue);
        Assert.That(currentCourseActiveAttribute.BoolValue.Value, Is.EqualTo(true));
        
        Assert.Pass();
    }
     
    [Test]
    public async Task FullImportUpdateReferenceAddSvaTestAsync()
    {
        InitialiseConnectedSystemObjectsData();

        // mock up a connector that will return updates for our existing connected system objects above.
        // changes: CURRENT_COURSE_TUTOR has a value set for the first time.
        var mockFileConnector = new MockFileConnector();
        mockFileConnector.TestImportObjects.Add(new ConnectedSystemImportObject
        {
            ChangeType = ObjectChangeType.Create,
            ObjectType = "SOURCE_USER",
            Attributes = new List<ConnectedSystemImportObjectAttribute>()
            {
                new()
                {
                    Name = MockSourceSystemAttributeNames.EMPLOYEE_ID.ToString(),
                    IntValues = new List<int> { 1 },
                    Type = AttributeDataType.Number
                },
                new()
                {
                    Name = MockSourceSystemAttributeNames.HR_ID.ToString(),
                    GuidValues = new List<Guid> { TestConstants.CS_OBJECT_1_HR_ID },
                    Type = AttributeDataType.Guid
                },
                new()
                {
                    Name = MockSourceSystemAttributeNames.START_DATE.ToString(),
                    DateTimeValue = TestConstants.CS_OBJECT_1_START_DATE,
                    Type = AttributeDataType.DateTime
                },
                new()
                {
                    Name = MockSourceSystemAttributeNames.DISPLAY_NAME.ToString(),
                    StringValues = new List<string> { TestConstants.CS_OBJECT_1_DISPLAY_NAME },
                    Type = AttributeDataType.Text
                },
                new()
                {
                    Name = MockSourceSystemAttributeNames.EMAIL_ADDRESS.ToString(),
                    StringValues = new List<string> { "jane.smith@phlebas.tetron.io" },
                    Type = AttributeDataType.Text
                },
                new()
                {
                    Name = MockSourceSystemAttributeNames.ROLE.ToString(),
                    StringValues = new List<string> { "Manager" },
                    Type = AttributeDataType.Text
                },
                new()
                {
                    Name = MockSourceSystemAttributeNames.PROFILE_PICTURE_BYTES.ToString(),
                    ByteValues = new List<byte[]> { Convert.FromHexString(TestConstants.IMAGE_1_HEX) },
                    Type = AttributeDataType.Binary
                },
                new()
                {
                    Name = MockSourceSystemAttributeNames.CONTRACTED_WEEKLY_HOURS.ToString(),
                    IntValues = new List<int> { 40 },
                    Type = AttributeDataType.Number
                },
                new()
                {
                    Name = MockSourceSystemAttributeNames.LOCATION_ID.ToString(),
                    GuidValues = new List<Guid> { TestConstants.LOCATION_1_ID },
                    Type = AttributeDataType.Guid
                },
                new()
                {
                    Name = MockSourceSystemAttributeNames.LEAVER.ToString(),
                    BoolValue = false,
                    Type = AttributeDataType.Boolean
                }
            }
        });
        
        // update our second user so their current course tutor references our first user.
        mockFileConnector.TestImportObjects.Add(new ConnectedSystemImportObject
        {
            ChangeType = ObjectChangeType.Create,
            ObjectType = "SOURCE_USER",
            Attributes = new List<ConnectedSystemImportObjectAttribute>()
            {
                new()
                {
                    Name = MockSourceSystemAttributeNames.EMPLOYEE_ID.ToString(),
                    IntValues = new List<int> { 2 },
                    Type = AttributeDataType.Number
                },
                new()
                {
                    Name = MockSourceSystemAttributeNames.HR_ID.ToString(),
                    GuidValues = new List<Guid> { TestConstants.CS_OBJECT_2_HR_ID },
                    Type = AttributeDataType.Guid
                },
                new()
                {
                    Name = MockSourceSystemAttributeNames.START_DATE.ToString(),
                    DateTimeValue = TestConstants.CS_OBJECT_2_START_DATE,
                    Type = AttributeDataType.DateTime
                },
                new()
                {
                    Name = MockSourceSystemAttributeNames.DISPLAY_NAME.ToString(),
                    StringValues = new List<string> { TestConstants.CS_OBJECT_2_DISPLAY_NAME },
                    Type = AttributeDataType.Text
                },
                new()
                {
                    Name = MockSourceSystemAttributeNames.EMAIL_ADDRESS.ToString(),
                    StringValues = new List<string> { "joe.bloggs@phlebas.tetron.io" },
                    Type = AttributeDataType.Text
                },
                new()
                {
                    Name = MockSourceSystemAttributeNames.ROLE.ToString(),
                    StringValues = new List<string> { "Developer" },
                    Type = AttributeDataType.Text
                },
                new()
                {
                    Name = MockSourceSystemAttributeNames.MANAGER.ToString(),
                    ReferenceValues = new List<string> { TestConstants.CS_OBJECT_1_HR_ID.ToString() },
                    Type = AttributeDataType.Reference
                },
                new()
                {
                    Name = MockSourceSystemAttributeNames.CONTRACTED_WEEKLY_HOURS.ToString(),
                    IntValues = new List<int> { 40 },
                    Type = AttributeDataType.Number
                },
                new()
                {
                    Name = MockSourceSystemAttributeNames.LOCATION_ID.ToString(),
                    GuidValues = new List<Guid> { TestConstants.LOCATION_1_ID },
                    Type = AttributeDataType.Guid
                },
                new()
                {
                    Name = MockSourceSystemAttributeNames.END_DATE.ToString(),
                    DateTimeValue = TestConstants.CS_OBJECT_2_END_DATE_1,
                    Type = AttributeDataType.DateTime
                },
                new()
                {
                    Name = MockSourceSystemAttributeNames.LEAVER.ToString(),
                    BoolValue = false,
                    Type = AttributeDataType.Boolean
                },
                new()
                {
                    Name = MockSourceSystemAttributeNames.CURRENT_COURSE_TUTOR.ToString(),
                    ReferenceValues = new List<string> { TestConstants.CS_OBJECT_1_HR_ID.ToString() }, // HR_ID (external id) of cso 1
                    Type = AttributeDataType.Reference
                },
            }
        });
        
        // now execute Jim functionality we want to test...
        var connectedSystem = await Jim.ConnectedSystems.GetConnectedSystemAsync(1);
        Assert.That(connectedSystem, Is.Not.Null, "Expected to retrieve a Connected System.");

        var activity = ActivitiesData.First();
        var runProfile = ConnectedSystemRunProfilesData.Single(q => q.ConnectedSystemId == connectedSystem.Id && q.RunType == ConnectedSystemRunType.FullImport);
        var synchronisationImportTaskProcessor = new SyncImportTaskProcessor(Jim, mockFileConnector, connectedSystem, runProfile, TestUtilities.CreateTestWorkerTask(activity, InitiatedBy), new CancellationTokenSource());
        await synchronisationImportTaskProcessor.PerformFullImportAsync();
        
        // confirm the results persisted to the mocked db context
        Assert.That(ConnectedSystemObjectsData, Has.Count.EqualTo(2), $"Expected two Connected System Objects to remain persisted. Found {ConnectedSystemObjectsData.Count}.");
        
        // get the Connected System Object for the user we changed some attribute values for in the mocked connector
        var cso2 = await Jim.ConnectedSystems.GetConnectedSystemObjectAsync(1, TestConstants.CS_OBJECT_2_ID);
        Assert.That(cso2, Is.Not.EqualTo(null), "Expected to be able to retrieve the second CSO to validate.");
        
        // not core to this test, but we've had failings for this before, so increasing coverage.
        var cso2ManagerAttribute = cso2.GetAttributeValue(MockSourceSystemAttributeNames.MANAGER.ToString());
        Assert.That(cso2ManagerAttribute, Is.Not.Null, "Expected to be able to get the MANAGER attribute on CSO2.");
        Assert.That(cso2ManagerAttribute.ReferenceValue, Is.Not.Null);
        Assert.That(cso2ManagerAttribute.ReferenceValue.Id, Is.EqualTo(TestConstants.CS_OBJECT_1_ID));
        Assert.That(!string.IsNullOrEmpty(cso2ManagerAttribute.UnresolvedReferenceValue), "Expected the MANAGER UnresolvedReferenceValue to also be populated.");
        Assert.That(cso2ManagerAttribute.UnresolvedReferenceValue, Is.EqualTo(TestConstants.CS_OBJECT_1_HR_ID.ToString()), "Expected the MANAGER UnresolvedReference to be the HR_ID of the first CSO.");

        // assert that our new reference is present and correct.
        var cso2CurrentCourseTutorAttribute = cso2.GetAttributeValue(MockSourceSystemAttributeNames.CURRENT_COURSE_TUTOR.ToString());
        Assert.That(cso2CurrentCourseTutorAttribute, Is.Not.Null, "Expected to be able to get the CURRENT_COURSE_TUTOR attribute on CSO2.");
        Assert.That(cso2CurrentCourseTutorAttribute.ReferenceValue, Is.Not.Null);
        Assert.That(cso2CurrentCourseTutorAttribute.ReferenceValue.Id, Is.EqualTo(TestConstants.CS_OBJECT_1_ID));
        Assert.That(!string.IsNullOrEmpty(cso2CurrentCourseTutorAttribute.UnresolvedReferenceValue), "Expected the CURRENT_COURSE_TUTOR UnresolvedReferenceValue to also be populated.");
        Assert.That(cso2CurrentCourseTutorAttribute.UnresolvedReferenceValue, Is.EqualTo(TestConstants.CS_OBJECT_1_HR_ID.ToString()), "Expected the CURRENT_COURSE_TUTOR UnresolvedReference to be the HR_ID of the first CSO.");
        
        Assert.Pass();
    }

    [Test]
    public async Task FullImportUpdateByteAddSvaTestAsync()
    {
        InitialiseConnectedSystemObjectsData();

        // mock up a connector that will return updates for our existing connected system objects above.
        // changes: PROFILE_PICTURE_BYTES is being added to cso2 for the first time.
        var newProfilePicture = Convert.FromHexString(TestConstants.IMAGE_2_HEX);
        var mockFileConnector = new MockFileConnector();

        // cso1 - no changes, just maintaining existing state
        mockFileConnector.TestImportObjects.Add(new ConnectedSystemImportObject
        {
            ChangeType = ObjectChangeType.Create,
            ObjectType = "SOURCE_USER",
            Attributes = new List<ConnectedSystemImportObjectAttribute>()
            {
                new()
                {
                    Name = MockSourceSystemAttributeNames.EMPLOYEE_ID.ToString(),
                    IntValues = new List<int> { 1 },
                    Type = AttributeDataType.Number
                },
                new()
                {
                    Name = MockSourceSystemAttributeNames.HR_ID.ToString(),
                    GuidValues = new List<Guid> { TestConstants.CS_OBJECT_1_HR_ID },
                    Type = AttributeDataType.Guid
                },
                new()
                {
                    Name = MockSourceSystemAttributeNames.START_DATE.ToString(),
                    DateTimeValue = TestConstants.CS_OBJECT_1_START_DATE,
                    Type = AttributeDataType.DateTime
                },
                new()
                {
                    Name = MockSourceSystemAttributeNames.DISPLAY_NAME.ToString(),
                    StringValues = new List<string> { TestConstants.CS_OBJECT_1_DISPLAY_NAME },
                    Type = AttributeDataType.Text
                },
                new()
                {
                    Name = MockSourceSystemAttributeNames.EMAIL_ADDRESS.ToString(),
                    StringValues = new List<string> { "jane.smith@phlebas.tetron.io" },
                    Type = AttributeDataType.Text
                },
                new()
                {
                    Name = MockSourceSystemAttributeNames.ROLE.ToString(),
                    StringValues = new List<string> { "Manager" },
                    Type = AttributeDataType.Text
                },
                new()
                {
                    Name = MockSourceSystemAttributeNames.PROFILE_PICTURE_BYTES.ToString(),
                    ByteValues = new List<byte[]> { Convert.FromHexString(TestConstants.IMAGE_1_HEX) },
                    Type = AttributeDataType.Binary
                },
                new()
                {
                    Name = MockSourceSystemAttributeNames.CONTRACTED_WEEKLY_HOURS.ToString(),
                    IntValues = new List<int> { 40 },
                    Type = AttributeDataType.Number
                },
                new()
                {
                    Name = MockSourceSystemAttributeNames.LOCATION_ID.ToString(),
                    GuidValues = new List<Guid> { TestConstants.LOCATION_1_ID },
                    Type = AttributeDataType.Guid
                },
                new()
                {
                    Name = MockSourceSystemAttributeNames.LEAVER.ToString(),
                    BoolValue = false,
                    Type = AttributeDataType.Boolean
                }
            }
        });

        // cso2 - adding PROFILE_PICTURE_BYTES for the first time
        mockFileConnector.TestImportObjects.Add(new ConnectedSystemImportObject
        {
            ChangeType = ObjectChangeType.Create,
            ObjectType = "SOURCE_USER",
            Attributes = new List<ConnectedSystemImportObjectAttribute>()
            {
                new()
                {
                    Name = MockSourceSystemAttributeNames.EMPLOYEE_ID.ToString(),
                    IntValues = new List<int> { 2 },
                    Type = AttributeDataType.Number
                },
                new()
                {
                    Name = MockSourceSystemAttributeNames.HR_ID.ToString(),
                    GuidValues = new List<Guid> { TestConstants.CS_OBJECT_2_HR_ID },
                    Type = AttributeDataType.Guid
                },
                new()
                {
                    Name = MockSourceSystemAttributeNames.START_DATE.ToString(),
                    DateTimeValue = TestConstants.CS_OBJECT_2_START_DATE,
                    Type = AttributeDataType.DateTime
                },
                new()
                {
                    Name = MockSourceSystemAttributeNames.DISPLAY_NAME.ToString(),
                    StringValues = new List<string> { TestConstants.CS_OBJECT_2_DISPLAY_NAME },
                    Type = AttributeDataType.Text
                },
                new()
                {
                    Name = MockSourceSystemAttributeNames.EMAIL_ADDRESS.ToString(),
                    StringValues = new List<string> { "joe.bloggs@phlebas.tetron.io" },
                    Type = AttributeDataType.Text
                },
                new()
                {
                    Name = MockSourceSystemAttributeNames.ROLE.ToString(),
                    StringValues = new List<string> { "Developer" },
                    Type = AttributeDataType.Text
                },
                new()
                {
                    Name = MockSourceSystemAttributeNames.MANAGER.ToString(),
                    ReferenceValues = new List<string> { TestConstants.CS_OBJECT_1_HR_ID.ToString() },
                    Type = AttributeDataType.Reference
                },
                new()
                {
                    Name = MockSourceSystemAttributeNames.CONTRACTED_WEEKLY_HOURS.ToString(),
                    IntValues = new List<int> { 40 },
                    Type = AttributeDataType.Number
                },
                new()
                {
                    Name = MockSourceSystemAttributeNames.LOCATION_ID.ToString(),
                    GuidValues = new List<Guid> { TestConstants.LOCATION_1_ID },
                    Type = AttributeDataType.Guid
                },
                new()
                {
                    Name = MockSourceSystemAttributeNames.END_DATE.ToString(),
                    DateTimeValue = TestConstants.CS_OBJECT_2_END_DATE_1,
                    Type = AttributeDataType.DateTime
                },
                new()
                {
                    Name = MockSourceSystemAttributeNames.LEAVER.ToString(),
                    BoolValue = false,
                    Type = AttributeDataType.Boolean
                },
                new()
                {
                    Name = MockSourceSystemAttributeNames.PROFILE_PICTURE_BYTES.ToString(),
                    ByteValues = new List<byte[]> { newProfilePicture },
                    Type = AttributeDataType.Binary
                }
            }
        });

        // now execute Jim functionality we want to test...
        var connectedSystem = await Jim.ConnectedSystems.GetConnectedSystemAsync(1);
        Assert.That(connectedSystem, Is.Not.Null, "Expected to retrieve a Connected System.");

        var activity = ActivitiesData.First();
        var runProfile = ConnectedSystemRunProfilesData.Single(q => q.ConnectedSystemId == connectedSystem.Id && q.RunType == ConnectedSystemRunType.FullImport);
        var synchronisationImportTaskProcessor = new SyncImportTaskProcessor(Jim, mockFileConnector, connectedSystem, runProfile, TestUtilities.CreateTestWorkerTask(activity, InitiatedBy), new CancellationTokenSource());
        await synchronisationImportTaskProcessor.PerformFullImportAsync();

        // confirm the results persisted to the mocked db context
        Assert.That(ConnectedSystemObjectsData, Has.Count.EqualTo(2), $"Expected two Connected System Objects to remain persisted. Found {ConnectedSystemObjectsData.Count}.");

        // get the Connected System Object for the user we added the profile picture to
        var cso2 = await Jim.ConnectedSystems.GetConnectedSystemObjectAsync(1, TestConstants.CS_OBJECT_2_ID);
        Assert.That(cso2, Is.Not.EqualTo(null), "Expected to be able to retrieve the second CSO to validate.");

        // verify the new PROFILE_PICTURE_BYTES attribute was added
        var profilePictureAttribute = cso2.GetAttributeValue(MockSourceSystemAttributeNames.PROFILE_PICTURE_BYTES.ToString());
        Assert.That(profilePictureAttribute, Is.Not.Null, "Expected PROFILE_PICTURE_BYTES attribute to be present.");
        Assert.That(profilePictureAttribute.ByteValue, Is.Not.Null, "Expected ByteValue to be populated.");
        Assert.That(profilePictureAttribute.ByteValue, Is.EqualTo(newProfilePicture), "Expected the profile picture bytes to match the imported value.");

        Assert.Pass();
    }

    // todo: test activity/run profile execution item/change object creation

    #region private methods
    private void InitialiseConnectedSystemObjectsData()
    {
        ConnectedSystemObjectsData.Clear();
        
        // set the start-state for the tests; create the Connected System Objects we'll alter in the tests
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
                StringValue = "1",
                Attribute = connectedSystemObjectType.Attributes.Single(q => q.Name == MockSourceSystemAttributeNames.EMPLOYEE_ID.ToString()),
                ConnectedSystemObject = cso1
            },
            new()
            {
                Id = Guid.NewGuid(),
                DateTimeValue = TestConstants.CS_OBJECT_1_START_DATE,
                Attribute = connectedSystemObjectType.Attributes.Single(q => q.Name == MockSourceSystemAttributeNames.START_DATE.ToString()),
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
                StringValue = "Manager",
                Attribute = connectedSystemObjectType.Attributes.Single(q => q.Name == MockSourceSystemAttributeNames.ROLE.ToString()),
                ConnectedSystemObject = cso1
            },
            new()
            {
                Id = Guid.NewGuid(),
                StringValue = "jane.smith@phlebas.tetron.io",
                Attribute = connectedSystemObjectType.Attributes.Single(q => q.Name == MockSourceSystemAttributeNames.EMAIL_ADDRESS.ToString()),
                ConnectedSystemObject = cso1
            },
            new()
            {
                Id = Guid.NewGuid(),
                ByteValue = Convert.FromHexString(TestConstants.IMAGE_1_HEX),
                Attribute = connectedSystemObjectType.Attributes.Single(q => q.Name == MockSourceSystemAttributeNames.PROFILE_PICTURE_BYTES.ToString()),
                ConnectedSystemObject = cso1
            },
            new()
            {
                Id = Guid.NewGuid(),
                IntValue = 40,
                Attribute = connectedSystemObjectType.Attributes.Single(q => q.Name == MockSourceSystemAttributeNames.CONTRACTED_WEEKLY_HOURS.ToString()),
                ConnectedSystemObject = cso1
            },
            new()
            {
                Id = Guid.NewGuid(),
                GuidValue = TestConstants.LOCATION_1_ID,
                Attribute = connectedSystemObjectType.Attributes.Single(q => q.Name == MockSourceSystemAttributeNames.LOCATION_ID.ToString()),
                ConnectedSystemObject = cso1
            },
            new()
            {
                Id = Guid.NewGuid(),
                BoolValue = false,
                Attribute = connectedSystemObjectType.Attributes.Single(q => q.Name == MockSourceSystemAttributeNames.LEAVER.ToString()),
                ConnectedSystemObject = cso1
            }
        };
        ConnectedSystemObjectsData.Add(cso1);
        
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
                StringValue = "2",
                Attribute = connectedSystemObjectType.Attributes.Single(q => q.Name == MockSourceSystemAttributeNames.EMPLOYEE_ID.ToString()),
                ConnectedSystemObject = cso2
            },
            new()
            {
                Id = Guid.NewGuid(),
                DateTimeValue = TestConstants.CS_OBJECT_2_START_DATE,
                Attribute = connectedSystemObjectType.Attributes.Single(q => q.Name == MockSourceSystemAttributeNames.START_DATE.ToString()),
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
                StringValue = "Developer",
                Attribute = connectedSystemObjectType.Attributes.Single(q => q.Name == MockSourceSystemAttributeNames.ROLE.ToString()),
                ConnectedSystemObject = cso2
            },
            new()
            {
                Id = Guid.NewGuid(),
                StringValue = "joe.bloggs@phlebas.tetron.io",
                Attribute = connectedSystemObjectType.Attributes.Single(q => q.Name == MockSourceSystemAttributeNames.EMAIL_ADDRESS.ToString()),
                ConnectedSystemObject = cso2
            },
            new()
            {
                Id = Guid.NewGuid(),
                ReferenceValueId = ConnectedSystemObjectsData.First().Id,
                ReferenceValue = ConnectedSystemObjectsData.First(),
                UnresolvedReferenceValue = TestConstants.CS_OBJECT_1_HR_ID.ToString(),
                Attribute = connectedSystemObjectType.Attributes.Single(q => q.Name == MockSourceSystemAttributeNames.MANAGER.ToString()),
                ConnectedSystemObject = cso2
            },
            new()
            {
                Id = Guid.NewGuid(),
                IntValue = 40,
                Attribute = connectedSystemObjectType.Attributes.Single(q => q.Name == MockSourceSystemAttributeNames.CONTRACTED_WEEKLY_HOURS.ToString()),
                ConnectedSystemObject = cso2
            },
            new()
            {
                Id = Guid.NewGuid(),
                GuidValue = TestConstants.LOCATION_1_ID,
                Attribute = connectedSystemObjectType.Attributes.Single(q => q.Name == MockSourceSystemAttributeNames.LOCATION_ID.ToString()),
                ConnectedSystemObject = cso2
            },
            new()
            {
                Id = Guid.NewGuid(),
                DateTimeValue = TestConstants.CS_OBJECT_2_END_DATE_1,
                Attribute = connectedSystemObjectType.Attributes.Single(q => q.Name == MockSourceSystemAttributeNames.END_DATE.ToString()),
                ConnectedSystemObject = cso2
            },
            new()
            {
                Id = Guid.NewGuid(),
                BoolValue = false,
                Attribute = connectedSystemObjectType.Attributes.Single(q => q.Name == MockSourceSystemAttributeNames.LEAVER.ToString()),
                ConnectedSystemObject = cso2
            }
        };
        ConnectedSystemObjectsData.Add(cso2);
    }
    #endregion
}
