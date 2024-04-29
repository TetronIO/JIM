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
    private List<ConnectedSystemObjectType> ConnectedSystemObjectTypesData { get; set; } 
    private Mock<DbSet<ConnectedSystemObjectType>> MockDbSetConnectedSystemObjectTypes { get; set; }
    // ReSharper disable once CollectionNeverUpdated.Local
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
        // environment variables needed by JIM, even though they won't be used
        Environment.SetEnvironmentVariable(Constants.Config.DatabaseHostname, "dummy");
        Environment.SetEnvironmentVariable(Constants.Config.DatabaseName, "dummy");
        Environment.SetEnvironmentVariable(Constants.Config.DatabaseUsername, "dummy");
        Environment.SetEnvironmentVariable(Constants.Config.DatabasePassword, "dummy");
        
        // set up the initiated-by user Metaverse object
        InitiatedBy = new MetaverseObject {
            Id = Guid.NewGuid()
        };
        
        // set up the connected systems mock
        ConnectedSystemsData = new List<ConnectedSystem>
        {
            new()
            {
                Id = 1,
                Name = "Dummy System"
            }
        };
        MockDbSetConnectedSystems = ConnectedSystemsData.AsQueryable().BuildMockDbSet();
        
        // setup up the connected system run profiles mock
        ConnectedSystemRunProfilesData = new List<ConnectedSystemRunProfile>
        {
            new()
            {
                Id = 1,
                Name = "Dummy Full Import",
                RunType = ConnectedSystemRunType.FullImport,
                ConnectedSystemId = 1
            }
        };
        MockDbSetConnectedSystemRunProfiles = ConnectedSystemRunProfilesData.AsQueryable().BuildMockDbSet();
        
        // set up the connected system object types mock. this acts as the persisted schema in JIM
        ConnectedSystemObjectTypesData = new List<ConnectedSystemObjectType>
        {
            new ()
            {
                Id = 1,
                Name = "User",
                ConnectedSystemId = 1,
                Selected = true,
                Attributes = new List<ConnectedSystemObjectTypeAttribute>
                {
                    new()
                    {
                        Id = (int)MockAttributeName.ID,
                        IsExternalId = true,
                        Name = MockAttributeName.ID.ToString(),
                        Type = AttributeDataType.Number
                    },
                    new()
                    {
                        Id = (int)MockAttributeName.DISPLAY_NAME,
                        Name = MockAttributeName.DISPLAY_NAME.ToString(),
                        Type = AttributeDataType.Text
                    },
                    new()
                    {
                        Id = (int)MockAttributeName.EMAIL_ADDRESS,
                        Name = MockAttributeName.EMAIL_ADDRESS.ToString(),
                        Type = AttributeDataType.Text
                    },
                    new()
                    {
                        Id = (int)MockAttributeName.ROLE,
                        Name = MockAttributeName.ROLE.ToString(),
                        Type = AttributeDataType.Text
                    },
                    new()
                    {
                        Id = (int)MockAttributeName.MANAGER,
                        Name = MockAttributeName.MANAGER.ToString(),
                        Type = AttributeDataType.Reference
                    },
                    new()
                    {
                        Id = (int)MockAttributeName.QUALIFICATIONS,
                        Name = MockAttributeName.QUALIFICATIONS.ToString(),
                        Type = AttributeDataType.Text,
                        AttributePlurality = AttributePlurality.MultiValued
                    },
                    new()
                    {
                        Id = (int)MockAttributeName.HR_ID,
                        Name = MockAttributeName.HR_ID.ToString(),
                        Type = AttributeDataType.Guid
                    },
                    new()
                    {
                        Id = (int)MockAttributeName.START_DATE,
                        Name = MockAttributeName.START_DATE.ToString(),
                        Type = AttributeDataType.DateTime
                    },
                    new()
                    {
                        Id = (int)MockAttributeName.PROFILE_PICTURE_BYTES,
                        Name = MockAttributeName.PROFILE_PICTURE_BYTES.ToString(),
                        Type = AttributeDataType.Binary
                    },
                }
            }
        };
        MockDbSetConnectedSystemObjectTypes = ConnectedSystemObjectTypesData.AsQueryable().BuildMockDbSet();
        
        // setup up the Connected System Partitions mock
        // ReSharper disable once CollectionNeverUpdated.Local
        ConnectedSystemPartitionsData = new List<ConnectedSystemPartition>();
        MockDbSetConnectedSystemPartitions = ConnectedSystemPartitionsData.AsQueryable().BuildMockDbSet();
        
        // set up the activity mock
        ActivitiesData = new List<Activity>
        {
            new()
            {
                Id = Guid.NewGuid(),
                TargetName = "Mock Full Import Execution",
                Status = ActivityStatus.InProgress,
                ConnectedSystemRunType = ConnectedSystemRunType.FullImport,
                InitiatedBy = InitiatedBy,
                InitiatedByName = "Joe Bloggs"
            }
        };
        MockDbSetActivities = ActivitiesData.AsQueryable().BuildMockDbSet();
        
        // mock entity framework calls to use our data sources above
        MockJimDbContext = new Mock<JimDbContext>();
        MockJimDbContext.Setup(m => m.Activities).Returns(MockDbSetActivities.Object);
        MockJimDbContext.Setup(m => m.ConnectedSystems).Returns(MockDbSetConnectedSystems.Object);
        MockJimDbContext.Setup(m => m.ConnectedSystemObjectTypes).Returns(MockDbSetConnectedSystemObjectTypes.Object);
        MockJimDbContext.Setup(m => m.ConnectedSystemRunProfiles).Returns(MockDbSetConnectedSystemRunProfiles.Object);
        MockJimDbContext.Setup(m => m.ConnectedSystemPartitions).Returns(MockDbSetConnectedSystemPartitions.Object);
        Jim = new JimApplication(new PostgresDataRepository(MockJimDbContext.Object));
    }
    
    [Test]
    public async Task FullImportUpdateTestAsync()
    {
        // set up the Connected System Objects mock. this is specific to this test
        // these objects represent our initiate state, what the imported objects will be compared to, and if successful, be updated
        var connectedSystemObjectType = ConnectedSystemObjectTypesData.First();
        var connectedSystemObjectData = new List<ConnectedSystemObject>();
        var cso1Id = Guid.Parse("86E8DDA1-217B-46B2-93F8-55BAEFD0F12A");
        var cso1 = new ConnectedSystemObject
        {
            Id = cso1Id,
            ConnectedSystemId = 1,
            ConnectedSystem = ConnectedSystemsData.First(),
            Type = connectedSystemObjectType
        };
        cso1.AttributeValues = new List<ConnectedSystemObjectAttributeValue>
        {
            new()
            {
                Id = Guid.NewGuid(),
                IntValue = 1,
                Attribute = connectedSystemObjectType.Attributes.Single(q => q.Name == MockAttributeName.ID.ToString()),
                ConnectedSystemObject = cso1
            },
            new()
            {
                Id = Guid.NewGuid(),
                StringValue = "Jane Smith",
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
                DateTimeValue = DateTime.Parse("1-jan-2000"),
                Attribute = connectedSystemObjectType.Attributes.Single(q => q.Name == MockAttributeName.START_DATE.ToString()),
                ConnectedSystemObject = cso1
            },
            new()
            {
                Id = Guid.NewGuid(),
                GuidValue = Guid.Parse("04CF471F-269C-42E3-9DAE-B558DC54A12E"),
                Attribute = connectedSystemObjectType.Attributes.Single(q => q.Name == MockAttributeName.HR_ID.ToString()),
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
        connectedSystemObjectData.Add(cso1);
        
        var cso2 = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = 1,
            ConnectedSystem = ConnectedSystemsData.First(),
            Type = connectedSystemObjectType
        };
        cso2.AttributeValues = new List<ConnectedSystemObjectAttributeValue>
        {
            new()
            {
                Id = Guid.NewGuid(),
                IntValue = 2,
                Attribute = connectedSystemObjectType.Attributes.Single(q => q.Name == MockAttributeName.ID.ToString()),
                ConnectedSystemObject = cso2
            },
            new()
            {
                Id = Guid.NewGuid(),
                StringValue = "Joe Bloggs",
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
                ReferenceValueId = connectedSystemObjectData.First().Id,
                ReferenceValue = connectedSystemObjectData.First(),
                Attribute = connectedSystemObjectType.Attributes.Single(q => q.Name == MockAttributeName.MANAGER.ToString()),
                ConnectedSystemObject = cso2
            }
        };
        connectedSystemObjectData.Add(cso2);

        var mockDbSetConnectedSystemObject = connectedSystemObjectData.AsQueryable().BuildMockDbSet();
        mockDbSetConnectedSystemObject.Setup(set => set.AddRange(It.IsAny<IEnumerable<ConnectedSystemObject>>())).Callback(
            (IEnumerable<ConnectedSystemObject> entities) => {
                var connectedSystemObjects = entities as ConnectedSystemObject[] ?? entities.ToArray();
                foreach (var entity in connectedSystemObjects)
                    entity.Id = Guid.NewGuid(); // assign the ids here, mocking what the db would do in SaveChanges()
                connectedSystemObjectData.AddRange(connectedSystemObjects);
            });
        MockJimDbContext.Setup(m => m.ConnectedSystemObjects).Returns(mockDbSetConnectedSystemObject.Object); 
        
        // mock up a connector that will return updates for our existing connected system objects above.
        // changes:
        // DISPLAY_NAME is different
        // HR_ID is different
        // PROFILE_PICTURE_BYTES is different
        var mockFileConnector = new MockFileConnector();
        const string newDisplayNameValue = "Jane Smith-Watson";
        var newHrIdValue = new Guid("ED70F4CF-9C6D-4D6C-8AEB-96E7C440CA11");
        var newProfilePictureValue = Convert.FromHexString(TestConstants.IMAGE_2_HEX);
        mockFileConnector.TestImportObjects.Add(new ConnectedSystemImportObject
        {
            ChangeType = ObjectChangeType.Create,
            ObjectType = "User",
            Attributes = new List<ConnectedSystemImportObjectAttribute>()
            {
                new ()
                {
                    Name = MockAttributeName.ID.ToString(),
                    IntValues = new List<int> { 1 },
                    Type = AttributeDataType.Number
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
                    Name = MockAttributeName.HR_ID.ToString(),
                    GuidValues = new List<Guid> { newHrIdValue },
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
                    Name = MockAttributeName.ID.ToString(),
                    IntValues = new List<int> { 2 },
                    Type = AttributeDataType.Number
                },
                new ()
                {
                    Name = MockAttributeName.DISPLAY_NAME.ToString(),
                    StringValues = new List<string> { "Joe Bloggs" },
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
                    Type = AttributeDataType.Text
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
        Assert.That(connectedSystemObjectData, Has.Count.EqualTo(2), $"Expected two Connected System Objects to remain persisted. Found {connectedSystemObjectData.Count}.");
        
        // get the Connected System Object for the user we changed some attribute values for in the mocked connector
        var cso1ToValidate = await Jim.ConnectedSystems.GetConnectedSystemObjectAsync(1, cso1Id);
        Assert.That(cso1ToValidate, Is.Not.EqualTo(null), "Expected to be able to retrieve the first CSO to validate.");

        var displayNameAttribute = cso1ToValidate.GetAttributeValue(MockAttributeName.DISPLAY_NAME.ToString());
        Assert.That(displayNameAttribute, Is.Not.Null);
        Assert.That(displayNameAttribute.StringValue, Is.EqualTo(newDisplayNameValue));
        
        var hrIdAttribute = cso1ToValidate.GetAttributeValue(MockAttributeName.HR_ID.ToString());
        Assert.That(hrIdAttribute, Is.Not.Null);
        Assert.That(hrIdAttribute.GuidValue.HasValue);
        Assert.That(hrIdAttribute.GuidValue.Value, Is.EqualTo(newHrIdValue));
        
        var profilePictureAttribute = cso1ToValidate.GetAttributeValue(MockAttributeName.PROFILE_PICTURE_BYTES.ToString());
        Assert.That(profilePictureAttribute, Is.Not.Null);
        Assert.That(profilePictureAttribute.ByteValue, Is.EqualTo(newProfilePictureValue));
        
        Assert.Pass();
    }
}