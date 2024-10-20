using JIM.Application;
using JIM.Connectors.Mock;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.PostgresData;
using JIM.Worker.Processors;
using JIM.Worker.Tests.Models;
using Microsoft.EntityFrameworkCore;
using MockQueryable.Moq;
using Moq;

namespace JIM.Worker.Tests.Synchronisation;

public class FullSyncTests
{
    #region accessors
    private MetaverseObject InitiatedBy { get; set; }
    private Mock<JimDbContext> MockJimDbContext { get; set; }
    private List<Activity> ActivitiesData { get; set; }
    private Mock<DbSet<Activity>> MockDbSetActivities { get; set; }
    private List<ConnectedSystem> ConnectedSystemsData { get; set; } 
    public List<ConnectedSystemObject> ConnectedSystemObjectsData { get; set; }
    private Mock<DbSet<ConnectedSystemObject>> MockDbSetConnectedSystemObjects { get; set; } 
    private List<ConnectedSystemRunProfile> ConnectedSystemRunProfilesData { get; set; }
    private Mock<DbSet<ConnectedSystemRunProfile>> MockDbSetConnectedSystemRunProfiles { get; set; }
    private Mock<DbSet<ConnectedSystem>> MockDbSetConnectedSystems { get; set; }
    private List<ConnectedSystemObjectType> ConnectedSystemObjectTypesData { get; set; } 
    private Mock<DbSet<ConnectedSystemObjectType>> MockDbSetConnectedSystemObjectTypes { get; set; }
    private List<MetaverseObjectType> MetaverseObjectTypesData { get; set; }
    private Mock<DbSet<MetaverseObjectType>> MockDbSetMetaverseObjectTypes { get; set; }
    private List<MetaverseObject> MetaverseObjectsData { get; set; }
    private Mock<DbSet<MetaverseObject>> MockDbSetMetaverseObjects { get; set; }
    private List<SyncRule> SyncRulesData { get; set; }
    private Mock<DbSet<SyncRule>> MockDbSetSyncRules { get; set; }
    private JimApplication Jim { get; set; }
    #endregion
    
    [SetUp]
    public void Setup()
    {
        // common dependencies
        TestUtilities.SetEnvironmentVariables();
        InitiatedBy = TestUtilities.GetInitiatedBy();
        
        // setup up the connected system run profiles mock
        ConnectedSystemRunProfilesData = TestUtilities.GetConnectedSystemRunProfileData();
        MockDbSetConnectedSystemRunProfiles = ConnectedSystemRunProfilesData.AsQueryable().BuildMockDbSet();
        
        // set up the activity mock
        var fullSyncRunProfile = ConnectedSystemRunProfilesData.Single(rp => rp.Name == "Dummy Source System Full Sync");
        ActivitiesData = TestUtilities.GetActivityData(fullSyncRunProfile.RunType, fullSyncRunProfile.Id);
        MockDbSetActivities = ActivitiesData.AsQueryable().BuildMockDbSet();
        
        // set up the connected systems mock
        ConnectedSystemsData = TestUtilities.GetConnectedSystemData();
        MockDbSetConnectedSystems = ConnectedSystemsData.AsQueryable().BuildMockDbSet();
        
        // todo: not sure if we need this. remove if not
        // set up the connected system object types mock. this acts as the persisted schema in JIM
        ConnectedSystemObjectTypesData = TestUtilities.GetConnectedSystemObjectTypeData();
        MockDbSetConnectedSystemObjectTypes = ConnectedSystemObjectTypesData.AsQueryable().BuildMockDbSet();
        
        // set up the connected system objects mock
        ConnectedSystemObjectsData = TestUtilities.GetConnectedSystemObjectData();
        MockDbSetConnectedSystemObjects = ConnectedSystemObjectsData.AsQueryable().BuildMockDbSet();
        
        // set up the metaverse object types mock
        MetaverseObjectTypesData = TestUtilities.GetMetaverseObjectTypeData();
        MockDbSetMetaverseObjectTypes = MetaverseObjectTypesData.AsQueryable().BuildMockDbSet();

        // set up the metaverse objects mock
        MetaverseObjectsData = TestUtilities.GetMetaverseObjectData();
        MockDbSetMetaverseObjects = MetaverseObjectsData.AsQueryable().BuildMockDbSet();
        
        // set up the sync rule stub mocks. they will be customised to specific use-cases in individual tests.
        SyncRulesData = TestUtilities.GetSyncRuleData();
        MockDbSetSyncRules = SyncRulesData.AsQueryable().BuildMockDbSet();
        
        // mock entity framework calls to use our data sources above
        MockJimDbContext = new Mock<JimDbContext>();
        MockJimDbContext.Setup(m => m.Activities).Returns(MockDbSetActivities.Object);
        MockJimDbContext.Setup(m => m.ConnectedSystems).Returns(MockDbSetConnectedSystems.Object);
        MockJimDbContext.Setup(m => m.ConnectedSystemObjects).Returns(MockDbSetConnectedSystemObjects.Object);
        MockJimDbContext.Setup(m => m.ConnectedSystemObjectTypes).Returns(MockDbSetConnectedSystemObjectTypes.Object);
        MockJimDbContext.Setup(m => m.ConnectedSystemRunProfiles).Returns(MockDbSetConnectedSystemRunProfiles.Object);
        MockJimDbContext.Setup(m => m.MetaverseObjectTypes).Returns(MockDbSetMetaverseObjectTypes.Object);
        MockJimDbContext.Setup(m => m.MetaverseObjects).Returns(MockDbSetMetaverseObjects.Object);
        MockJimDbContext.Setup(m => m.SyncRules).Returns(MockDbSetSyncRules.Object);
        
        // instantiate Jim using the mocked db context
        Jim = new JimApplication(new PostgresDataRepository(MockJimDbContext.Object));
    }
    
    // [Test]
    // public async Task PendingExportTestSuccessAsync()
    // {
    //      // set up the Pending Export objects mock. this is specific to this test
    //      var pendingExportObjects = new List<PendingExport>
    //      {
    //          new()
    //          {
    //              Id = Guid.NewGuid(),
    //              ConnectedSystemId = 1,
    //              Status = PendingExportStatus.Pending,
    //              ChangeType = PendingExportChangeType.Create,
    //              AttributeValueChanges = new()
    //              {
    //                  new()
    //                  {
    //                      Id = Guid.NewGuid(),
    //                      ChangeType = PendingExportAttributeChangeType.Add,
    //                      AttributeId = (int)MockSourceSystemAttributeNames.DISPLAY_NAME,
    //                      StringValue = "James McGill"
    //                  },
    //                  
    //              }
    //          }
    //     };
    //     
    //     // mock up a connector that will return testable data
    //     
    //     // now execute Jim functionality we want to test...
    //     
    //     // confirm the results persisted to the mocked db context
    //
    //     // validate the first user (who is a manager)
    //
    //     // validate the second user (who is a direct-report)
    //     
    //     // validate second user manager reference.
    //
    //     Assert.Fail("Not implemented yet. Doesn't need to be done until export scenario worked on.");
    // }
    
    /// <summary>
    /// Tests that a CSO can successfully join to a Metaverse object using matching rules on a sync rule. 
    /// </summary>
    [Test]
    public async Task CsoJoinToMvoViaTextAttributeTestAsync()
    {
        // get a stub import sync rule
        var importSyncRule = SyncRulesData.Single(q => q.Id == 1);
        
        // add test-specific matching rules to it
        var objectMatchingRule = new SyncRuleMapping
        {
            Id = 1,
            Type = SyncRuleMappingType.ObjectMatching,
            ObjectMatchingSynchronisationRule = importSyncRule,
            TargetMetaverseAttributeId = (int)MockMetaverseAttributeName.EmployeeId
        };
        objectMatchingRule.Sources.Add(new SyncRuleMappingSource
        {
            Id = 1,
            ConnectedSystemAttributeId = (int)MockSourceSystemAttributeNames.EMPLOYEE_ID
        });
        importSyncRule.ObjectMatchingRules.Add(objectMatchingRule);
        
        // mock up a connector that will return testable data
        var mockFileConnector = new MockFileConnector();
        
        // test that a CSO is successfully match to an MVO using the sync rule
        var connectedSystem = await Jim.ConnectedSystems.GetConnectedSystemAsync(1);
        Assert.That(connectedSystem, Is.Not.Null, "Expected to retrieve a Connected System.");

        var activity = ActivitiesData.First();
        var runProfile = ConnectedSystemRunProfilesData.Single(q => q.ConnectedSystemId == connectedSystem.Id && q.RunType == ConnectedSystemRunType.FullSynchronisation);
        var synchronisationImportTaskProcessor = new SyncImportTaskProcessor(Jim, mockFileConnector, connectedSystem, runProfile, InitiatedBy, activity, new CancellationTokenSource());
        await synchronisationImportTaskProcessor.PerformFullImportAsync();
        
        Assert.Fail("Not implemented.");
    }
    
    // todo: CSO joins to MVO
    // todo: CSO projects to MV
    // todo: MVO has pending attribute value adds for all data types as expected
    // todo: MVO has pending attribute value removes for all data types as expected
    // todo: MVO changes are persisted as expected
    // todo: CSO obsolete/deletion process
    // todo: Onward updates as a result of all above scenarios
}