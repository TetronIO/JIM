using JIM.Application;
using JIM.Connectors.Mock;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Staging;
using JIM.PostgresData;
using JIM.Worker.Processors;
using JIM.Worker.Tests.Models;
using MockQueryable.Moq;
using Moq;

namespace JIM.Worker.Tests;

public class SynchronisationImportTaskProcessorTests
{
     //private JimApplication InitialImportJim { get; set; }
     // todo: add other Jim instances for scenarios such as ImportWithUpdatesJim, ImportWithDeletesJim, etc.
    
    [SetUp]
    public void Setup()
    {
    }

    [Test]
    public async Task FullImportBasicTestAsync()
    {
        // set up the initiated-by user Metaverse object
        var initiatedBy = new MetaverseObject {
            Id = Guid.NewGuid()
        };
        
        // set up the run profile
        var runProfile = new ConnectedSystemRunProfile {
            Id = 1,
            RunType = ConnectedSystemRunType.FullImport
        };
        
        // set up the activity mock
        var activityData = new List<Activity>
        {
            new()
            {
                Id = new Guid(),
                TargetName = "Mock Full Import Execution",
                Status = ActivityStatus.InProgress,
                ConnectedSystemRunType = ConnectedSystemRunType.FullImport,
                InitiatedBy = initiatedBy,
                InitiatedByName = "Joe Bloggs"
            }
        };
        var mockDbSetActivity = activityData.AsQueryable().BuildMockDbSet();
        
        // set up the connected systems mock
        var connectedSystemData = new List<ConnectedSystem>
        {
            new()
            {
                Id = 1,
                Name = "Dummy System"
            }
        };
        var mockDbSetConnectedSystem = connectedSystemData.AsQueryable().BuildMockDbSet();
        
        // setup up the connected system run profiles mock
        var connectedSystemRunProfileData = new List<ConnectedSystemRunProfile>
        {
            new()
            {
                Id = 1,
                Name = "Dummy Full Import",
                RunType = ConnectedSystemRunType.FullImport,
                ConnectedSystemId = 1
            },
            new()
            {
                Id = 2,
                Name = "Dummy Delta Import",
                RunType = ConnectedSystemRunType.DeltaImport,
                ConnectedSystemId = 1
            },
            new()
            {
                Id = 3,
                Name = "Dummy Full Synchronisation",
                RunType = ConnectedSystemRunType.FullSynchronisation,
                ConnectedSystemId = 1
            },
            new()
            {
                Id = 4,
                Name = "Dummy Delta Synchronisation",
                RunType = ConnectedSystemRunType.DeltaImport,
                ConnectedSystemId = 1
            },
            new()
            {
                Id = 5,
                Name = "Dummy Export",
                RunType = ConnectedSystemRunType.Export,
                ConnectedSystemId = 1
            }
        };
        var mockDbSetConnectedSystemRunProfile = connectedSystemRunProfileData.AsQueryable().BuildMockDbSet();
        
        // set up the connected system object types mock
        var connectedSystemObjectTypeData = new List<ConnectedSystemObjectType>
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
                    }
                }
            }
        };
        var mockDbSetConnectedSystemObjectType = connectedSystemObjectTypeData.AsQueryable().BuildMockDbSet();
        
        // set up the connected system objects mock
        var connectedSystemObjectData = new List<ConnectedSystemObject>();
        var mockDbSetConnectedSystemObject = connectedSystemObjectData.AsQueryable().BuildMockDbSet();
        
        // setup up the Connected System Partitions mock
        // ReSharper disable once CollectionNeverUpdated.Local
        var connectedSystemPartitionsData = new List<ConnectedSystemPartition>();
        var mockDbSetConnectedSystemPartition = connectedSystemPartitionsData.AsQueryable().BuildMockDbSet();
        
        // mock entity framework calls to use our data sources above
        var mockDbContext = new Mock<JimDbContext>();
        mockDbContext.Setup(m => m.Activities).Returns(mockDbSetActivity.Object);
        mockDbContext.Setup(m => m.ConnectedSystems).Returns(mockDbSetConnectedSystem.Object);
        mockDbContext.Setup(m => m.ConnectedSystemObjectTypes).Returns(mockDbSetConnectedSystemObjectType.Object);
        mockDbContext.Setup(m => m.ConnectedSystemRunProfiles).Returns(mockDbSetConnectedSystemRunProfile.Object);
        mockDbSetConnectedSystemObject.Setup(set => set.AddRange(It.IsAny<IEnumerable<ConnectedSystemObject>>())).Callback((IEnumerable<ConnectedSystemObject> entities) => connectedSystemObjectData.AddRange(entities));
        mockDbContext.Setup(m => m.ConnectedSystemObjects).Returns(mockDbSetConnectedSystemObject.Object);
        mockDbContext.Setup(m => m.ConnectedSystemPartitions).Returns(mockDbSetConnectedSystemPartition.Object);
        
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
                }
            }
        });
        
        // environment variables needed by JIM, even though they won't be used
        Environment.SetEnvironmentVariable(Constants.Config.DatabaseHostname, "dummy");
        Environment.SetEnvironmentVariable(Constants.Config.DatabaseName, "dummy");
        Environment.SetEnvironmentVariable(Constants.Config.DatabaseUsername, "dummy");
        Environment.SetEnvironmentVariable(Constants.Config.DatabasePassword, "dummy");
        
        // now execute Jim functionality we want to test...
        var jim = new JimApplication(new PostgresDataRepository(mockDbContext.Object));
        var connectedSystem = await jim.ConnectedSystems.GetConnectedSystemAsync(1);
        Assert.That(connectedSystem, Is.Not.Null, "Expected to retrieve a Connected System.");

        var activity = activityData.First();
        var synchronisationImportTaskProcessor = new SynchronisationImportTaskProcessor(jim, mockFileConnector, connectedSystem, runProfile, initiatedBy, activity, new CancellationTokenSource());
        await synchronisationImportTaskProcessor.PerformFullImportAsync();
        
        // confirm the results in the mocked db context
        Assert.That(connectedSystemObjectData, Has.Count.EqualTo(1), "Expected a single Connected System Object to have been persisted.");
        var persistedConnectedSystemObject = connectedSystemObjectData[0];
        var sourceConnectedSystemImportObject = mockFileConnector.TestImportObjects[0];

        ValidateAttributesForEquality(persistedConnectedSystemObject, sourceConnectedSystemImportObject, MockAttributeName.ID, AttributeDataType.Number);
        ValidateAttributesForEquality(persistedConnectedSystemObject, sourceConnectedSystemImportObject, MockAttributeName.DISPLAY_NAME, AttributeDataType.Text);
        ValidateAttributesForEquality(persistedConnectedSystemObject, sourceConnectedSystemImportObject, MockAttributeName.EMAIL_ADDRESS, AttributeDataType.Text);
        ValidateAttributesForEquality(persistedConnectedSystemObject, sourceConnectedSystemImportObject, MockAttributeName.ROLE, AttributeDataType.Text);
        
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
            case AttributeDataType.NotSet:
            case AttributeDataType.Binary:
            case AttributeDataType.Reference:
            default:
                throw new NotSupportedException($"AttributeDataType of {expectedAttributeDataType} is not currently supported by this test.");
        }
    }
}
