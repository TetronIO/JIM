using JIM.Application;
using JIM.Connectors.File;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Staging;
using JIM.PostgresData;
using JIM.Worker.Processors;
using Microsoft.EntityFrameworkCore;
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
        }.AsQueryable();
        var mockSetActivity = new Mock<DbSet<Activity>>();
        mockSetActivity.As<IQueryable<Activity>>().Setup(m => m.Provider).Returns(activityData.Provider);
        mockSetActivity.As<IQueryable<Activity>>().Setup(m => m.Expression).Returns(activityData.Expression);
        mockSetActivity.As<IQueryable<Activity>>().Setup(m => m.ElementType).Returns(activityData.ElementType);
        mockSetActivity.As<IQueryable<Activity>>().Setup(m => m.GetEnumerator()).Returns(() => activityData.GetEnumerator());
        mockDbContext.Setup(m => m.Activities).Returns(mockSetActivity.Object);
        
        // set up the connected system mock
        var connectedSystemData = new List<ConnectedSystem> 
        {
            new()
            {
                Id = 1
            }
        }.AsQueryable();
        var mockSetConnectedSystem = new Mock<DbSet<ConnectedSystem>>();
        mockSetConnectedSystem.As<IQueryable<ConnectedSystem>>().Setup(m => m.Provider).Returns(connectedSystemData.Provider);
        mockSetConnectedSystem.As<IQueryable<ConnectedSystem>>().Setup(m => m.Expression).Returns(connectedSystemData.Expression);
        mockSetConnectedSystem.As<IQueryable<ConnectedSystem>>().Setup(m => m.ElementType).Returns(connectedSystemData.ElementType);
        mockSetConnectedSystem.As<IQueryable<ConnectedSystem>>().Setup(m => m.GetEnumerator()).Returns(() => connectedSystemData.GetEnumerator());
        mockDbContext.Setup(m => m.ConnectedSystems).Returns(mockSetConnectedSystem.Object);
        
        // actual app code for the most part
        var jim = new JimApplication(new PostgresDataRepository(mockDbContext.Object));
        var fileConnector = new FileConnector(); // change this to a mocked FileConnector that returns test data
        var connectedSystem = await jim.ConnectedSystems.GetConnectedSystemAsync(1);
        Assert.That(connectedSystem, Is.Not.Null);
        
        var synchronisationImportTaskProcessor = new SynchronisationImportTaskProcessor(jim, fileConnector, connectedSystem, runProfile, initiatedBy, activityData.First(), new CancellationTokenSource());
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
