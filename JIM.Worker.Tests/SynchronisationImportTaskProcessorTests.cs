using System.Data.Entity;
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
    public async Task FullImportTestAsync()
    {
        // test-specific:
        var mockDbContext = new Mock<JimDbContext>();
        
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
        mockDbContext.Setup(m => m.Activities).Returns(mockDbSetActivity.Object);
        
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
        mockDbContext.Setup(m => m.ConnectedSystems).Returns(mockDbSetConnectedSystem.Object);

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
        mockDbContext.Setup(m => m.ConnectedSystemRunProfiles).Returns(mockDbSetConnectedSystemRunProfile.Object);
        
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
                        Id = (int)MockAttributeNames.ID,
                        IsExternalId = true,
                        Name = MockAttributeNames.ID.ToString(),
                        Type = AttributeDataType.Number
                    },
                    new()
                    {
                        Id = (int)MockAttributeNames.DISPLAY_NAME,
                        Name = MockAttributeNames.DISPLAY_NAME.ToString(),
                        Type = AttributeDataType.Text
                    },
                    new()
                    {
                        Id = (int)MockAttributeNames.EMAIL_ADDRESS,
                        Name = MockAttributeNames.EMAIL_ADDRESS.ToString(),
                        Type = AttributeDataType.Text
                    },
                    new()
                    {
                        Id = (int)MockAttributeNames.ROLE,
                        Name = MockAttributeNames.ROLE.ToString(),
                        Type = AttributeDataType.Text
                    }
                }
            }
        };
        var mockDbSetConnectedSystemObjectType = connectedSystemObjectTypeData.AsQueryable().BuildMockDbSet();
        mockDbContext.Setup(m => m.ConnectedSystemObjectTypes).Returns(mockDbSetConnectedSystemObjectType.Object);

        // set up the connected system objects mock
        var connectedSystemObjectData = new List<ConnectedSystemObject>();
        var mockDbSetConnectedSystemObject = connectedSystemObjectData.AsQueryable().BuildMockDbSet();
        mockDbSetConnectedSystemObject
             .Setup(set => set.AddRange(It.IsAny<IEnumerable<ConnectedSystemObject>>()))
             .Callback((IEnumerable<ConnectedSystemObject> entities) => connectedSystemObjectData.AddRange(entities));
        mockDbContext.Setup(m => m.ConnectedSystemObjects).Returns(mockDbSetConnectedSystemObject.Object);
        
        // setup up the Connected System Partitions mock
        // ReSharper disable once CollectionNeverUpdated.Local
        var connectedSystemPartitionsData = new List<ConnectedSystemPartition>();
        var mockDbSetConnectedSystemPartition = connectedSystemPartitionsData.AsQueryable().BuildMockDbSet();
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
                    Name = MockAttributeNames.ID.ToString(),
                    IntValues = new List<int> { 1 }
                },
                new ()
                {
                    Name = MockAttributeNames.DISPLAY_NAME.ToString(),
                    StringValues = new List<string> { "Joe Bloggs" }
                },
                new ()
                {
                    Name = MockAttributeNames.EMAIL_ADDRESS.ToString(),
                    StringValues = new List<string> { "joe.bloggs@phlebas.tetron.io" }
                },
                new ()
                {
                    Name = MockAttributeNames.ROLE.ToString(),
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
        
        Assert.Pass();
    }
    
    /*#region private methods
    private void SetupInitialImportJim()
    {
        InitialImportJim = new JimApplication();
    }
    #endregion*/
}
