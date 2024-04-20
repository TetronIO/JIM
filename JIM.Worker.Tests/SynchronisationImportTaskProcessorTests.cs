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

namespace JIM.Worker.Tests;

public class SynchronisationImportTaskProcessorTests
{
    // accessors for data that will be used across tests
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
                    }
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
    public async Task FullImportBasicTestAsync()
    {
        // set up the Connected System Objects mock. this is specific to this test
        var connectedSystemObjectData = new List<ConnectedSystemObject>();
        var mockDbSetConnectedSystemObject = connectedSystemObjectData.AsQueryable().BuildMockDbSet();
        mockDbSetConnectedSystemObject.Setup(set => set.AddRange(It.IsAny<IEnumerable<ConnectedSystemObject>>())).Callback(
            (IEnumerable<ConnectedSystemObject> entities) => {
                var connectedSystemObjects = entities as ConnectedSystemObject[] ?? entities.ToArray();
                foreach (var entity in connectedSystemObjects)
                    entity.Id = Guid.NewGuid(); // assign the ids here, mocking what the db would do in SaveChanges()
                connectedSystemObjectData.AddRange(connectedSystemObjects);
            });
        MockJimDbContext.Setup(m => m.ConnectedSystemObjects).Returns(mockDbSetConnectedSystemObject.Object);
        
        // mock up a connector that will return testable data
        var mockFileConnector = new MockFileConnector();
        mockFileConnector.TestImportObjects.Add(new ConnectedSystemImportObject
        {
            ChangeType = ObjectChangeType.Create,
            ObjectType = "user",
            Attributes = new List<ConnectedSystemImportObjectAttribute>()
            {
                new ()
                {
                    Name = MockAttributeName.ID.ToString(),
                    IntValues = new List<int> { 1 }
                },
                new ()
                {
                    Name = MockAttributeName.DISPLAY_NAME.ToString(),
                    StringValues = new List<string> { "Jane Smith" }
                },
                new ()
                {
                    Name = MockAttributeName.EMAIL_ADDRESS.ToString(),
                    StringValues = new List<string> { "jane.smith@phlebas.tetron.io" }
                },
                new ()
                {
                    Name = MockAttributeName.ROLE.ToString(),
                    StringValues = new List<string> { "Manager" }
                }
            }
        });
        mockFileConnector.TestImportObjects.Add(new ConnectedSystemImportObject
        {
            ChangeType = ObjectChangeType.Create,
            ObjectType = "user",
            Attributes = new List<ConnectedSystemImportObjectAttribute>()
            {
                new ()
                {
                    Name = MockAttributeName.ID.ToString(),
                    IntValues = new List<int> { 2 }
                },
                new ()
                {
                    Name = MockAttributeName.DISPLAY_NAME.ToString(),
                    StringValues = new List<string> { "Joe Bloggs" }
                },
                new ()
                {
                    Name = MockAttributeName.EMAIL_ADDRESS.ToString(),
                    StringValues = new List<string> { "joe.bloggs@phlebas.tetron.io" }
                },
                new ()
                {
                    Name = MockAttributeName.ROLE.ToString(),
                    StringValues = new List<string> { "Developer" }
                },
                new ()
                {
                    Name = MockAttributeName.MANAGER.ToString(),
                    ReferenceValues = new List<string> { "1" }
                }
            }
        });
        
        // now execute Jim functionality we want to test...
        var connectedSystem = await Jim.ConnectedSystems.GetConnectedSystemAsync(1);
        Assert.That(connectedSystem, Is.Not.Null, "Expected to retrieve a Connected System.");

        var activity = ActivitiesData.First();
        var runProfile = ConnectedSystemRunProfilesData.Single(q => q.RunType == ConnectedSystemRunType.FullImport);
        var synchronisationImportTaskProcessor = new SynchronisationImportTaskProcessor(Jim, mockFileConnector, connectedSystem, runProfile, InitiatedBy, activity, new CancellationTokenSource());
        await synchronisationImportTaskProcessor.PerformFullImportAsync();
        
        // confirm the results persisted to the mocked db context
        Assert.That(connectedSystemObjectData, Has.Count.EqualTo(2), "Expected two Connected System Objects to have been persisted.");

        // validate the first user (who is a manager)
        var firstPersistedConnectedSystemObject = connectedSystemObjectData[0];
        var firstSourceConnectedSystemImportObject = mockFileConnector.TestImportObjects[0];
        ValidateAttributesForEquality(firstPersistedConnectedSystemObject, firstSourceConnectedSystemImportObject, MockAttributeName.ID, AttributeDataType.Number);
        ValidateAttributesForEquality(firstPersistedConnectedSystemObject, firstSourceConnectedSystemImportObject, MockAttributeName.DISPLAY_NAME, AttributeDataType.Text);
        ValidateAttributesForEquality(firstPersistedConnectedSystemObject, firstSourceConnectedSystemImportObject, MockAttributeName.EMAIL_ADDRESS, AttributeDataType.Text);
        ValidateAttributesForEquality(firstPersistedConnectedSystemObject, firstSourceConnectedSystemImportObject, MockAttributeName.ROLE, AttributeDataType.Text);

        // validate the second user (who is a direct-report)
        var secondPersistedConnectedSystemObject = connectedSystemObjectData[1];
        var secondSourceConnectedSystemImportObject = mockFileConnector.TestImportObjects[1];
        ValidateAttributesForEquality(secondPersistedConnectedSystemObject, secondSourceConnectedSystemImportObject, MockAttributeName.ID, AttributeDataType.Number);
        ValidateAttributesForEquality(secondPersistedConnectedSystemObject, secondSourceConnectedSystemImportObject, MockAttributeName.DISPLAY_NAME, AttributeDataType.Text);
        ValidateAttributesForEquality(secondPersistedConnectedSystemObject, secondSourceConnectedSystemImportObject, MockAttributeName.EMAIL_ADDRESS, AttributeDataType.Text);
        ValidateAttributesForEquality(secondPersistedConnectedSystemObject, secondSourceConnectedSystemImportObject, MockAttributeName.ROLE, AttributeDataType.Text);
        
        // validate manager reference
        var managerAttribute = secondPersistedConnectedSystemObject.AttributeValues.SingleOrDefault(q=>q.Attribute.Name == MockAttributeName.MANAGER.ToString());
        Assert.That(managerAttribute, Is.Not.Null, "Expected the MANAGER attribute to not be null.");
        Assert.That(managerAttribute.ReferenceValue, Is.Not.Null, "Expected the MANAGER reference value not to be null.");
        // can't test this, EF code that sets these values is being overriden as part of testing
        //Assert.That(managerAttribute.ReferenceValueId.HasValue, "Expected the MANAGER reference value id not to be null"); 
        //Assert.That(managerAttribute.ReferenceValueId.Value, Is.EqualTo(firstPersistedConnectedSystemObject.Id), "Expected the MANAGER reference valid id to be the same as the first object id.");
        Assert.That(managerAttribute.ReferenceValue.Id, Is.EqualTo(firstPersistedConnectedSystemObject.Id), "Expected the MANAGER reference object id to match the id of the first object.");
        
        Assert.Pass();
    }

    [Test]
    public async Task FullImportUpdateTestAsync()
    {
        // set up the Connected System Objects mock. this is specific to this test
        // these objects represent our initiate state, what the imported objects will be compared to, and if successful, be updated
        var connectedSystemObjectType = ConnectedSystemObjectTypesData.First();
        var connectedSystemObjectData = new List<ConnectedSystemObject>
        {
            new ()
            {
                Id = Guid.NewGuid(),
                ConnectedSystemId = 1,
                ConnectedSystem = ConnectedSystemsData.First(),
                Type = connectedSystemObjectType,
                AttributeValues = new List<ConnectedSystemObjectAttributeValue>
                {
                    new()
                    {
                        Id = Guid.NewGuid(),
                        IntValue = 1,
                        Attribute = connectedSystemObjectType.Attributes.Single(q=>q.Name == MockAttributeName.ID.ToString())
                    },
                    new()
                    {
                        Id = Guid.NewGuid(),
                        StringValue = "Jane Smith",
                        Attribute = connectedSystemObjectType.Attributes.Single(q=>q.Name == MockAttributeName.DISPLAY_NAME.ToString())
                    },
                    new()
                    {
                        Id = Guid.NewGuid(),
                        StringValue = "Manager",
                        Attribute = connectedSystemObjectType.Attributes.Single(q=>q.Name == MockAttributeName.ROLE.ToString())
                    },
                    new()
                    {
                        Id = Guid.NewGuid(),
                        StringValue = "jane.smith@phlebas.tetron.io",
                        Attribute = connectedSystemObjectType.Attributes.Single(q=>q.Name == MockAttributeName.EMAIL_ADDRESS.ToString())
                    }
                }
            },
        };
        connectedSystemObjectData.Add(new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = 1,
            ConnectedSystem = ConnectedSystemsData.First(),
            Type = connectedSystemObjectType,
            AttributeValues = new List<ConnectedSystemObjectAttributeValue>
            {
                new()
                {
                    Id = Guid.NewGuid(),
                    IntValue = 2,
                    Attribute = connectedSystemObjectType.Attributes.Single(q=>q.Name == MockAttributeName.ID.ToString())
                },
                new()
                {
                    Id = Guid.NewGuid(),
                    StringValue = "Joe Bloggs",
                    Attribute = connectedSystemObjectType.Attributes.Single(q=>q.Name == MockAttributeName.DISPLAY_NAME.ToString())
                },
                new()
                {
                    Id = Guid.NewGuid(),
                    StringValue = "Developer",
                    Attribute = connectedSystemObjectType.Attributes.Single(q=>q.Name == MockAttributeName.ROLE.ToString())
                },
                new()
                {
                    Id = Guid.NewGuid(),
                    StringValue = "joe.bloggs@phlebas.tetron.io",
                    Attribute = connectedSystemObjectType.Attributes.Single(q=>q.Name == MockAttributeName.EMAIL_ADDRESS.ToString())
                },
                new()
                {
                    Id = Guid.NewGuid(),
                    ReferenceValueId = connectedSystemObjectData.First().Id,
                    ReferenceValue = connectedSystemObjectData.First(),
                    Attribute = connectedSystemObjectType.Attributes.Single(q=>q.Name == MockAttributeName.MANAGER.ToString())
                }
            }
        });

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
        // changes: Jane Smith is now Jane Smith-Watson
        var mockFileConnector = new MockFileConnector();
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
                    StringValues = new List<string> { "Jane Smith-Watson" },
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
        var synchronisationImportTaskProcessor = new SynchronisationImportTaskProcessor(Jim, mockFileConnector, connectedSystem, runProfile, InitiatedBy, activity, new CancellationTokenSource());
        await synchronisationImportTaskProcessor.PerformFullImportAsync();
        
        // confirm the results persisted to the mocked db context
        Assert.That(connectedSystemObjectData, Has.Count.EqualTo(2), $"Expected two Connected System Objects to remain persisted. Found {connectedSystemObjectData.Count}.");
        Assert.Pass();
    }

    private static void ValidateAttributesForEquality(ConnectedSystemObject connectedSystemObject, ConnectedSystemImportObject connectedSystemImportObject, MockAttributeName attributeName, AttributeDataType expectedAttributeDataType)
    {
        Assert.That(connectedSystemObject, Is.Not.Null);
        Assert.That(connectedSystemObject.AttributeValues, Is.Not.Null);
        Assert.That(connectedSystemImportObject, Is.Not.Null);
        Assert.That(connectedSystemImportObject.Attributes, Is.Not.Null);

        var csoAttributeValues = connectedSystemObject.AttributeValues.Where(q => q.Attribute.Name == attributeName.ToString()).ToList();
        Assert.That(csoAttributeValues, Is.Not.Null);

        var csioAttribute = connectedSystemImportObject.Attributes.SingleOrDefault(q => q.Name == attributeName.ToString());
        Assert.That(csioAttribute, Is.Not.Null);

        switch (expectedAttributeDataType)
        {
            case AttributeDataType.Boolean:
                Assert.That(csoAttributeValues, Has.Count.EqualTo(1)); // booleans are single-valued by nature. you can't have multiple bool attribute values: you'd have no way to differentiate them
                Assert.That(csoAttributeValues[0].BoolValue, Is.EqualTo(csioAttribute.BoolValue));
                break;
            case AttributeDataType.Guid:
                // checking that the counts are the same, and that the cso values exist in the csio value, and visa verse (i.e. are the two collections the same)
                Assert.That(csoAttributeValues, Has.Count.EqualTo(csioAttribute.GuidValues.Count));
                foreach (var csoGuidValue in csoAttributeValues)
                    Assert.That(csioAttribute.GuidValues.Any(q => q == csoGuidValue.GuidValue));
                foreach (var csioGuidValue in csioAttribute.GuidValues)
                    Assert.That(csoAttributeValues.Any(q => q.GuidValue == csioGuidValue));
                break;
            case AttributeDataType.Number:
                // checking that the counts are the same, and that the cso values exist in the csio value, and visa verse (i.e. are the two collections the same)
                Assert.That(csoAttributeValues, Has.Count.EqualTo(csioAttribute.IntValues.Count));
                foreach (var csoIntValue in csoAttributeValues)
                    Assert.That(csioAttribute.IntValues.Any(q => q == csoIntValue.IntValue));
                foreach (var csioIntValue in csioAttribute.IntValues)
                    Assert.That(csoAttributeValues.Any(q => q.IntValue == csioIntValue));
                break;
            case AttributeDataType.Text:
                // checking that the counts are the same, and that the cso values exist in the csio value, and visa verse (i.e. are the two collections the same)
                Assert.That(csoAttributeValues, Has.Count.EqualTo(csioAttribute.StringValues.Count));
                foreach (var csoStringValue in csoAttributeValues)
                    Assert.That(csioAttribute.StringValues.Any(q => q == csoStringValue.StringValue));
                foreach (var csioStringValue in csioAttribute.StringValues)
                    Assert.That(csoAttributeValues.Any(q => q.StringValue == csioStringValue));
                break;
            case AttributeDataType.DateTime:
                // checking that the counts are the same, and that the cso values exist in the csio value, and visa verse (i.e. are the two collections the same)
                Assert.That(csoAttributeValues, Has.Count.EqualTo(csioAttribute.DateTimeValues.Count));
                foreach (var csoDateTimeValue in csoAttributeValues)
                    Assert.That(csioAttribute.DateTimeValues.Any(q => q == csoDateTimeValue.DateTimeValue));
                foreach (var csioDateTimeValue in csioAttribute.DateTimeValues)
                    Assert.That(csoAttributeValues.Any(q => q.DateTimeValue == csioDateTimeValue));
                break;
            case AttributeDataType.Binary:
                // this is quite crude, and could be improved
                // checking that the counts are the same, and that the cso values exist in the csio value, and visa verse (i.e. are the two collections the same)
                Assert.That(csoAttributeValues, Has.Count.EqualTo(csioAttribute.ByteValues.Count));
                foreach (var csoByteValue in csoAttributeValues)
                    Assert.That(csioAttribute.ByteValues.Any(q => q == csoByteValue.ByteValue));
                foreach (var csioByteValue in csioAttribute.ByteValues)
                    Assert.That(csoAttributeValues.Any(q => q.ByteValue?.Length == csioByteValue.Length));
                break;
            case AttributeDataType.Reference:
            case AttributeDataType.NotSet:
            default:
                throw new NotSupportedException($"AttributeDataType of {expectedAttributeDataType} is supported by this method.");
        }
    }
}
