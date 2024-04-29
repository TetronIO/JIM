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

public class ImportUpdateObjectTests
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
    
    [SetUp]
    public void Setup()
    {
        TestUtilities.SetEnvironmentVariables();
        InitiatedBy = TestUtilities.GetInitiatedBy();
        
        // set up the connected systems mock
        ConnectedSystemsData = TestUtilities.GetConnectedSystemData();
        MockDbSetConnectedSystems = ConnectedSystemsData.AsQueryable().BuildMockDbSet();
        
        // setup up the connected system run profiles mock
        ConnectedSystemRunProfilesData = TestUtilities.GetConnectedSystemRunProfileData();
        MockDbSetConnectedSystemRunProfiles = ConnectedSystemRunProfilesData.AsQueryable().BuildMockDbSet();
        
        // set up the connected system object types mock. this acts as the persisted schema in JIM
        ConnectedSystemObjectTypesData = TestUtilities.GetConnectedSystemObjectTypeData();
        MockDbSetConnectedSystemObjectTypes = ConnectedSystemObjectTypesData.AsQueryable().BuildMockDbSet();
        
        // setup up the Connected System Partitions mock
        ConnectedSystemPartitionsData = TestUtilities.GetConnectedSystemPartitionData();
        MockDbSetConnectedSystemPartitions = ConnectedSystemPartitionsData.AsQueryable().BuildMockDbSet();
        
        // set up the activity mock
        ActivitiesData = TestUtilities.GetActivityData(ConnectedSystemRunType.FullImport);
        MockDbSetActivities = ActivitiesData.AsQueryable().BuildMockDbSet();
        
        // mock entity framework calls to use our data sources above
        MockJimDbContext = new Mock<JimDbContext>();
        MockJimDbContext.Setup(m => m.Activities).Returns(MockDbSetActivities.Object);
        MockJimDbContext.Setup(m => m.ConnectedSystems).Returns(MockDbSetConnectedSystems.Object);
        MockJimDbContext.Setup(m => m.ConnectedSystemObjectTypes).Returns(MockDbSetConnectedSystemObjectTypes.Object);
        MockJimDbContext.Setup(m => m.ConnectedSystemRunProfiles).Returns(MockDbSetConnectedSystemRunProfiles.Object);
        MockJimDbContext.Setup(m => m.ConnectedSystemPartitions).Returns(MockDbSetConnectedSystemPartitions.Object);
        
        // instantiate Jim using the mocked db context
        Jim = new JimApplication(new PostgresDataRepository(MockJimDbContext.Object));
        
        var mockDbSetConnectedSystemObject = ConnectedSystemObjectsData.AsQueryable().BuildMockDbSet();
        mockDbSetConnectedSystemObject.Setup(set => set.AddRange(It.IsAny<IEnumerable<ConnectedSystemObject>>())).Callback((IEnumerable<ConnectedSystemObject> entities) => {
            var connectedSystemObjects = entities as ConnectedSystemObject[] ?? entities.ToArray();
            foreach (var entity in connectedSystemObjects)
                entity.Id = Guid.NewGuid(); // assign the ids here, mocking what the db would do in SaveChanges()
            ConnectedSystemObjectsData.AddRange(connectedSystemObjects);
        });
        MockJimDbContext.Setup(m => m.ConnectedSystemObjects).Returns(mockDbSetConnectedSystemObject.Object); 
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
            ObjectType = "User",
            Attributes = new List<ConnectedSystemImportObjectAttribute>()
            {
                new ()
                {
                    Name = MockAttributeName.EMPLOYEE_ID.ToString(),
                    IntValues = new List<int> { 1 },
                    Type = AttributeDataType.Number
                },
                new ()
                {
                    Name = MockAttributeName.HR_ID.ToString(),
                    GuidValues = new List<Guid> { TestConstants.CS_OBJECT_1_HR_ID },
                    Type = AttributeDataType.Guid
                },
                new ()
                {
                    Name = MockAttributeName.START_DATE.ToString(),
                    DateTimeValues = new List<DateTime> { TestConstants.CS_OBJECT_1_START_DATE },
                    Type = AttributeDataType.DateTime
                },
                new ()
                {
                    Name = MockAttributeName.DISPLAY_NAME.ToString(),
                    StringValues = new List<string> { newDisplayNameValue },
                    Type = AttributeDataType.Text
                },
                new ()
                {
                    Name = MockAttributeName.EMAIL_ADDRESS.ToString(),
                    StringValues = new List<string> { "jane.smith@phlebas.tetron.io" },
                    Type = AttributeDataType.Text
                },
                new ()
                {
                    Name = MockAttributeName.ROLE.ToString(),
                    StringValues = new List<string> { "Manager" },
                    Type = AttributeDataType.Text
                },
                new ()
                {
                    Name = MockAttributeName.PROFILE_PICTURE_BYTES.ToString(),
                    ByteValues = new List<byte[]> { Convert.FromHexString(TestConstants.IMAGE_1_HEX) },
                    Type = AttributeDataType.Binary
                }
            }
        });
        mockFileConnector.TestImportObjects.Add(new ConnectedSystemImportObject
        {
            ChangeType = ObjectChangeType.Create,
            ObjectType = "User",
            Attributes = new List<ConnectedSystemImportObjectAttribute>()
            {
                new ()
                {
                    Name = MockAttributeName.EMPLOYEE_ID.ToString(),
                    IntValues = new List<int> { 2 },
                    Type = AttributeDataType.Number
                },
                new ()
                {
                    Name = MockAttributeName.HR_ID.ToString(),
                    GuidValues = new List<Guid> { TestConstants.CS_OBJECT_2_HR_ID },
                    Type = AttributeDataType.Guid
                },
                new ()
                {
                    Name = MockAttributeName.START_DATE.ToString(),
                    DateTimeValues = new List<DateTime> { TestConstants.CS_OBJECT_2_START_DATE },
                    Type = AttributeDataType.DateTime
                },
                new ()
                {
                    Name = MockAttributeName.DISPLAY_NAME.ToString(),
                    StringValues = new List<string> { TestConstants.CS_OBJECT_2_DISPLAY_NAME },
                    Type = AttributeDataType.Text
                },
                new ()
                {
                    Name = MockAttributeName.EMAIL_ADDRESS.ToString(),
                    StringValues = new List<string> { "joe.bloggs@phlebas.tetron.io" },
                    Type = AttributeDataType.Text
                },
                new ()
                {
                    Name = MockAttributeName.ROLE.ToString(),
                    StringValues = new List<string> { "Developer" },
                    Type = AttributeDataType.Text
                },
                new ()
                {
                    Name = MockAttributeName.MANAGER.ToString(),
                    ReferenceValues = new List<string> { "1" },
                    Type = AttributeDataType.Reference
                }
            }
        });
        
        // now execute Jim functionality we want to test...
        var connectedSystem = await Jim.ConnectedSystems.GetConnectedSystemAsync(1);
        Assert.That(connectedSystem, Is.Not.Null, "Expected to retrieve a Connected System.");

        var activity = ActivitiesData.First();
        var runProfile = ConnectedSystemRunProfilesData.Single(q => q.RunType == ConnectedSystemRunType.FullImport);
        var synchronisationImportTaskProcessor = new SyncImportTaskProcessor(Jim, mockFileConnector, connectedSystem, runProfile, InitiatedBy, activity, new CancellationTokenSource());
        await synchronisationImportTaskProcessor.PerformFullImportAsync();
        
        // confirm the results persisted to the mocked db context
        Assert.That(ConnectedSystemObjectsData, Has.Count.EqualTo(2), $"Expected two Connected System Objects to remain persisted. Found {ConnectedSystemObjectsData.Count}.");
        
        // get the Connected System Object for the user we changed some attribute values for in the mocked connector
        var cso1ToValidate = await Jim.ConnectedSystems.GetConnectedSystemObjectAsync(1, TestConstants.CS_OBJECT_1_ID);
        Assert.That(cso1ToValidate, Is.Not.EqualTo(null), "Expected to be able to retrieve the first CSO to validate.");

        var displayNameAttribute = cso1ToValidate.GetAttributeValue(MockAttributeName.DISPLAY_NAME.ToString());
        Assert.That(displayNameAttribute, Is.Not.Null);
        Assert.That(displayNameAttribute.StringValue, Is.EqualTo(newDisplayNameValue));
        
        Assert.Pass();
    }
    
    [Test]
    public async Task FullImportUpdateGuidSvaTestAsync()
    {
        InitialiseConnectedSystemObjectsData();
        
        // mock up a connector that will return updates for our existing connected system objects above.
        // changes: HR_ID has a populated, but different value
        var newHrIdValue = new Guid("ED70F4CF-9C6D-4D6C-8AEB-96E7C440CA11");
        var mockFileConnector = new MockFileConnector();
        mockFileConnector.TestImportObjects.Add(new ConnectedSystemImportObject
        {
            ChangeType = ObjectChangeType.Create,
            ObjectType = "User",
            Attributes = new List<ConnectedSystemImportObjectAttribute>()
            {
                new ()
                {
                    Name = MockAttributeName.EMPLOYEE_ID.ToString(),
                    IntValues = new List<int> { 1 },
                    Type = AttributeDataType.Number
                },
                new ()
                {
                    Name = MockAttributeName.HR_ID.ToString(),
                    GuidValues = new List<Guid> { newHrIdValue },
                    Type = AttributeDataType.Guid
                },
                new ()
                {
                    Name = MockAttributeName.START_DATE.ToString(),
                    DateTimeValues = new List<DateTime> { TestConstants.CS_OBJECT_1_START_DATE },
                    Type = AttributeDataType.DateTime
                },
                new ()
                {
                    Name = MockAttributeName.DISPLAY_NAME.ToString(),
                    StringValues = new List<string> { TestConstants.CS_OBJECT_1_DISPLAY_NAME },
                    Type = AttributeDataType.Text
                },
                new ()
                {
                    Name = MockAttributeName.EMAIL_ADDRESS.ToString(),
                    StringValues = new List<string> { "jane.smith@phlebas.tetron.io" },
                    Type = AttributeDataType.Text
                },
                new ()
                {
                    Name = MockAttributeName.ROLE.ToString(),
                    StringValues = new List<string> { "Manager" },
                    Type = AttributeDataType.Text
                },
                new ()
                {
                    Name = MockAttributeName.PROFILE_PICTURE_BYTES.ToString(),
                    ByteValues = new List<byte[]> { Convert.FromHexString(TestConstants.IMAGE_1_HEX) },
                    Type = AttributeDataType.Binary
                }
            }
        });
        mockFileConnector.TestImportObjects.Add(new ConnectedSystemImportObject
        {
            ChangeType = ObjectChangeType.Create,
            ObjectType = "User",
            Attributes = new List<ConnectedSystemImportObjectAttribute>()
            {
                new ()
                {
                    Name = MockAttributeName.EMPLOYEE_ID.ToString(),
                    IntValues = new List<int> { 2 },
                    Type = AttributeDataType.Number
                },
                new ()
                {
                    Name = MockAttributeName.HR_ID.ToString(),
                    GuidValues = new List<Guid> { TestConstants.CS_OBJECT_2_HR_ID },
                    Type = AttributeDataType.Guid
                },
                new ()
                {
                    Name = MockAttributeName.START_DATE.ToString(),
                    DateTimeValues = new List<DateTime> { TestConstants.CS_OBJECT_2_START_DATE },
                    Type = AttributeDataType.DateTime
                },
                new ()
                {
                    Name = MockAttributeName.DISPLAY_NAME.ToString(),
                    StringValues = new List<string> { TestConstants.CS_OBJECT_2_DISPLAY_NAME },
                    Type = AttributeDataType.Text
                },
                new ()
                {
                    Name = MockAttributeName.EMAIL_ADDRESS.ToString(),
                    StringValues = new List<string> { "joe.bloggs@phlebas.tetron.io" },
                    Type = AttributeDataType.Text
                },
                new ()
                {
                    Name = MockAttributeName.ROLE.ToString(),
                    StringValues = new List<string> { "Developer" },
                    Type = AttributeDataType.Text
                },
                new ()
                {
                    Name = MockAttributeName.MANAGER.ToString(),
                    ReferenceValues = new List<string> { "1" },
                    Type = AttributeDataType.Reference
                }
            }
        });
        
        // now execute Jim functionality we want to test...
        var connectedSystem = await Jim.ConnectedSystems.GetConnectedSystemAsync(1);
        Assert.That(connectedSystem, Is.Not.Null, "Expected to retrieve a Connected System.");

        var activity = ActivitiesData.First();
        var runProfile = ConnectedSystemRunProfilesData.Single(q => q.RunType == ConnectedSystemRunType.FullImport);
        var synchronisationImportTaskProcessor = new SyncImportTaskProcessor(Jim, mockFileConnector, connectedSystem, runProfile, InitiatedBy, activity, new CancellationTokenSource());
        await synchronisationImportTaskProcessor.PerformFullImportAsync();
        
        // confirm the results persisted to the mocked db context
        Assert.That(ConnectedSystemObjectsData, Has.Count.EqualTo(2), $"Expected two Connected System Objects to remain persisted. Found {ConnectedSystemObjectsData.Count}.");
        
        // get the Connected System Object for the user we changed some attribute values for in the mocked connector
        var cso1ToValidate = await Jim.ConnectedSystems.GetConnectedSystemObjectAsync(1, TestConstants.CS_OBJECT_1_ID);
        Assert.That(cso1ToValidate, Is.Not.EqualTo(null), "Expected to be able to retrieve the first CSO to validate.");

        var hrIdAttribute = cso1ToValidate.GetAttributeValue(MockAttributeName.HR_ID.ToString());
        Assert.That(hrIdAttribute, Is.Not.Null);
        Assert.That(hrIdAttribute.GuidValue.HasValue);
        Assert.That(hrIdAttribute.GuidValue.Value, Is.EqualTo(newHrIdValue));
        
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
            ObjectType = "User",
            Attributes = new List<ConnectedSystemImportObjectAttribute>()
            {
                new ()
                {
                    Name = MockAttributeName.EMPLOYEE_ID.ToString(),
                    IntValues = new List<int> { 1 },
                    Type = AttributeDataType.Number
                },
                new ()
                {
                    Name = MockAttributeName.HR_ID.ToString(),
                    GuidValues = new List<Guid> { TestConstants.CS_OBJECT_1_HR_ID },
                    Type = AttributeDataType.Guid
                },
                new ()
                {
                    Name = MockAttributeName.START_DATE.ToString(),
                    DateTimeValues = new List<DateTime> { TestConstants.CS_OBJECT_1_START_DATE },
                    Type = AttributeDataType.DateTime
                },
                new ()
                {
                    Name = MockAttributeName.DISPLAY_NAME.ToString(),
                    StringValues = new List<string> { TestConstants.CS_OBJECT_1_DISPLAY_NAME },
                    Type = AttributeDataType.Text
                },
                new ()
                {
                    Name = MockAttributeName.EMAIL_ADDRESS.ToString(),
                    StringValues = new List<string> { "jane.smith@phlebas.tetron.io" },
                    Type = AttributeDataType.Text
                },
                new ()
                {
                    Name = MockAttributeName.ROLE.ToString(),
                    StringValues = new List<string> { "Manager" },
                    Type = AttributeDataType.Text
                },
                new ()
                {
                    Name = MockAttributeName.PROFILE_PICTURE_BYTES.ToString(),
                    ByteValues = new List<byte[]> { newProfilePictureValue },
                    Type = AttributeDataType.Binary
                }
            }
        });
        mockFileConnector.TestImportObjects.Add(new ConnectedSystemImportObject
        {
            ChangeType = ObjectChangeType.Create,
            ObjectType = "User",
            Attributes = new List<ConnectedSystemImportObjectAttribute>()
            {
                new ()
                {
                    Name = MockAttributeName.EMPLOYEE_ID.ToString(),
                    IntValues = new List<int> { 2 },
                    Type = AttributeDataType.Number
                },
                new ()
                {
                    Name = MockAttributeName.HR_ID.ToString(),
                    GuidValues = new List<Guid> { TestConstants.CS_OBJECT_2_HR_ID },
                    Type = AttributeDataType.Guid
                },
                new ()
                {
                    Name = MockAttributeName.START_DATE.ToString(),
                    DateTimeValues = new List<DateTime> { TestConstants.CS_OBJECT_2_START_DATE },
                    Type = AttributeDataType.DateTime
                },
                new ()
                {
                    Name = MockAttributeName.DISPLAY_NAME.ToString(),
                    StringValues = new List<string> { TestConstants.CS_OBJECT_2_DISPLAY_NAME },
                    Type = AttributeDataType.Text
                },
                new ()
                {
                    Name = MockAttributeName.EMAIL_ADDRESS.ToString(),
                    StringValues = new List<string> { "joe.bloggs@phlebas.tetron.io" },
                    Type = AttributeDataType.Text
                },
                new ()
                {
                    Name = MockAttributeName.ROLE.ToString(),
                    StringValues = new List<string> { "Developer" },
                    Type = AttributeDataType.Text
                },
                new ()
                {
                    Name = MockAttributeName.MANAGER.ToString(),
                    ReferenceValues = new List<string> { "1" },
                    Type = AttributeDataType.Reference
                }
            }
        });
        
        // now execute Jim functionality we want to test...
        var connectedSystem = await Jim.ConnectedSystems.GetConnectedSystemAsync(1);
        Assert.That(connectedSystem, Is.Not.Null, "Expected to retrieve a Connected System.");

        var activity = ActivitiesData.First();
        var runProfile = ConnectedSystemRunProfilesData.Single(q => q.RunType == ConnectedSystemRunType.FullImport);
        var synchronisationImportTaskProcessor = new SyncImportTaskProcessor(Jim, mockFileConnector, connectedSystem, runProfile, InitiatedBy, activity, new CancellationTokenSource());
        await synchronisationImportTaskProcessor.PerformFullImportAsync();
        
        // confirm the results persisted to the mocked db context
        Assert.That(ConnectedSystemObjectsData, Has.Count.EqualTo(2), $"Expected two Connected System Objects to remain persisted. Found {ConnectedSystemObjectsData.Count}.");
        
        // get the Connected System Object for the user we changed some attribute values for in the mocked connector
        var cso1ToValidate = await Jim.ConnectedSystems.GetConnectedSystemObjectAsync(1, TestConstants.CS_OBJECT_1_ID);
        Assert.That(cso1ToValidate, Is.Not.EqualTo(null), "Expected to be able to retrieve the first CSO to validate.");

        var profilePictureAttribute = cso1ToValidate.GetAttributeValue(MockAttributeName.PROFILE_PICTURE_BYTES.ToString());
        Assert.That(profilePictureAttribute, Is.Not.Null);
        Assert.That(profilePictureAttribute.ByteValue, Is.EqualTo(newProfilePictureValue));
        
        Assert.Pass();
    }
    
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
            Type = connectedSystemObjectType
        };
        cso1.AttributeValues = new List<ConnectedSystemObjectAttributeValue>
        {
            new()
            {
                Id = Guid.NewGuid(),
                GuidValue = TestConstants.CS_OBJECT_1_HR_ID,
                Attribute = connectedSystemObjectType.Attributes.Single(q => q.Name == MockAttributeName.HR_ID.ToString()),
                ConnectedSystemObject = cso1
            },
            new()
            {
                Id = Guid.NewGuid(),
                IntValue = 1,
                Attribute = connectedSystemObjectType.Attributes.Single(q => q.Name == MockAttributeName.EMPLOYEE_ID.ToString()),
                ConnectedSystemObject = cso1
            },
            new()
            {
                Id = Guid.NewGuid(),
                DateTimeValue = TestConstants.CS_OBJECT_1_START_DATE,
                Attribute = connectedSystemObjectType.Attributes.Single(q => q.Name == MockAttributeName.START_DATE.ToString()),
                ConnectedSystemObject = cso1
            },
            new()
            {
                Id = Guid.NewGuid(),
                StringValue = TestConstants.CS_OBJECT_1_DISPLAY_NAME,
                Attribute = connectedSystemObjectType.Attributes.Single(q => q.Name == MockAttributeName.DISPLAY_NAME.ToString()),
                ConnectedSystemObject = cso1
            },
            new()
            {
                Id = Guid.NewGuid(),
                StringValue = "Manager",
                Attribute = connectedSystemObjectType.Attributes.Single(q => q.Name == MockAttributeName.ROLE.ToString()),
                ConnectedSystemObject = cso1
            },
            new()
            {
                Id = Guid.NewGuid(),
                StringValue = "jane.smith@phlebas.tetron.io",
                Attribute = connectedSystemObjectType.Attributes.Single(q => q.Name == MockAttributeName.EMAIL_ADDRESS.ToString()),
                ConnectedSystemObject = cso1
            },
            new()
            {
                Id = Guid.NewGuid(),
                ByteValue = Convert.FromHexString(TestConstants.IMAGE_1_HEX),
                Attribute = connectedSystemObjectType.Attributes.Single(q => q.Name == MockAttributeName.PROFILE_PICTURE_BYTES.ToString()),
                ConnectedSystemObject = cso1
            }
        };
        ConnectedSystemObjectsData.Add(cso1);
        
        var cso2 = new ConnectedSystemObject
        {
            Id = TestConstants.CS_OBJECT_2_ID,
            ConnectedSystemId = 1,
            ConnectedSystem = ConnectedSystemsData.First(),
            Type = connectedSystemObjectType
        };
        cso2.AttributeValues = new List<ConnectedSystemObjectAttributeValue>
        {
            new()
            {
                Id = Guid.NewGuid(),
                GuidValue = TestConstants.CS_OBJECT_2_HR_ID,
                Attribute = connectedSystemObjectType.Attributes.Single(q => q.Name == MockAttributeName.HR_ID.ToString()),
                ConnectedSystemObject = cso2
            },
            new()
            {
                Id = Guid.NewGuid(),
                IntValue = 2,
                Attribute = connectedSystemObjectType.Attributes.Single(q => q.Name == MockAttributeName.EMPLOYEE_ID.ToString()),
                ConnectedSystemObject = cso2
            },
            new()
            {
                Id = Guid.NewGuid(),
                DateTimeValue = TestConstants.CS_OBJECT_2_START_DATE,
                Attribute = connectedSystemObjectType.Attributes.Single(q => q.Name == MockAttributeName.START_DATE.ToString()),
                ConnectedSystemObject = cso2
            },
            new()
            {
                Id = Guid.NewGuid(),
                StringValue = TestConstants.CS_OBJECT_2_DISPLAY_NAME,
                Attribute = connectedSystemObjectType.Attributes.Single(q => q.Name == MockAttributeName.DISPLAY_NAME.ToString()),
                ConnectedSystemObject = cso2
            },
            new()
            {
                Id = Guid.NewGuid(),
                StringValue = "Developer",
                Attribute = connectedSystemObjectType.Attributes.Single(q => q.Name == MockAttributeName.ROLE.ToString()),
                ConnectedSystemObject = cso2
            },
            new()
            {
                Id = Guid.NewGuid(),
                StringValue = "joe.bloggs@phlebas.tetron.io",
                Attribute = connectedSystemObjectType.Attributes.Single(q => q.Name == MockAttributeName.EMAIL_ADDRESS.ToString()),
                ConnectedSystemObject = cso2
            },
            new()
            {
                Id = Guid.NewGuid(),
                ReferenceValueId = ConnectedSystemObjectsData.First().Id,
                ReferenceValue = ConnectedSystemObjectsData.First(),
                Attribute = connectedSystemObjectType.Attributes.Single(q => q.Name == MockAttributeName.MANAGER.ToString()),
                ConnectedSystemObject = cso2
            }
        };
        ConnectedSystemObjectsData.Add(cso2);
    }
    #endregion
}