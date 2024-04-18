using System.Data.Entity.Infrastructure;
using JIM.Application;
using JIM.Connectors.Mock;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Staging;
using JIM.PostgresData;
using JIM.Worker.Processors;
using Microsoft.EntityFrameworkCore;
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
    public async Task Test1Async()
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
        
        // set up the connected system mock
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
        
        // setup the connected system object type mock
        var connectedSystemObjectData = new List<ConnectedSystemObjectType>
        {
            new ()
            {
                Id = 1,
                Name = "User",
                ConnectedSystemId = 1 // todo: ef migration needed for this new attribute
            }
        };
        
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
                    Name = "ID",
                    IntValues = new List<int> { 1 }
                },
                new ()
                {
                    Name = "DISPLAY_NAME",
                    StringValues = new List<string> { "Joe Bloggs" }
                },
                new ()
                {
                    Name = "EMAIL_ADDRESS",
                    StringValues = new List<string> { "joe.bloggs@phlebas.tetron.io" }
                },
                new ()
                {
                    Name = "ROLE",
                    StringValues = new List<string> { "Developer" }
                }
            }
        });
        
        // todo: schema
        
        // environment variables needed by JIM, even though they won't be used
        Environment.SetEnvironmentVariable(Constants.Config.DatabaseHostname, "dummy");
        Environment.SetEnvironmentVariable(Constants.Config.DatabaseName, "dummy");
        Environment.SetEnvironmentVariable(Constants.Config.DatabaseUsername, "dummy");
        Environment.SetEnvironmentVariable(Constants.Config.DatabasePassword, "dummy");
        
        // now execute Jim functionality we want to test...
        var jim = new JimApplication(new PostgresDataRepository(mockDbContext.Object));
        var connectedSystem = await jim.ConnectedSystems.GetConnectedSystemAsync(1);
        Assert.That(connectedSystem, Is.Not.Null);
        
        var synchronisationImportTaskProcessor = new SynchronisationImportTaskProcessor(jim, mockFileConnector, connectedSystem, runProfile, initiatedBy, activityData.First(), new CancellationTokenSource());
        await synchronisationImportTaskProcessor.PerformFullImportAsync();
        
        // confirm the results in the mocked db context
        
        Assert.Pass();
    }
    
    /*#region private methods
    private void SetupInitialImportJim()
    {
        InitialImportJim = new JimApplication();
    }
    #endregion*/
}
