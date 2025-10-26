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
    private List<ConnectedSystemObject> ConnectedSystemObjectsData { get; set; }
    private Mock<DbSet<ConnectedSystemObject>> MockDbSetConnectedSystemObjects { get; set; } 
    private List<ConnectedSystemRunProfile> ConnectedSystemRunProfilesData { get; set; }
    private Mock<DbSet<ConnectedSystemRunProfile>> MockDbSetConnectedSystemRunProfiles { get; set; }
    private Mock<DbSet<ConnectedSystem>> MockDbSetConnectedSystems { get; set; }
    private List<ConnectedSystemObjectType> ConnectedSystemObjectTypesData { get; set; } 
    private Mock<DbSet<ConnectedSystemObjectType>> MockDbSetConnectedSystemObjectTypes { get; set; }
    private List<ConnectedSystemPartition> ConnectedSystemPartitionsData { get; set; }
    private Mock<DbSet<ConnectedSystemPartition>> MockDbSetConnectedSystemPartitions { get; set; }
    private List<PendingExport> PendingExportsData { get; set; }
    private Mock<DbSet<PendingExport>> MockDbSetPendingExports { get; set; }
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
        
        // setup up the Connected System Run Profiles mock
        ConnectedSystemRunProfilesData = TestUtilities.GetConnectedSystemRunProfileData();
        MockDbSetConnectedSystemRunProfiles = ConnectedSystemRunProfilesData.BuildMockDbSet();
        
        // set up the Activity mock
        var fullSyncRunProfile = ConnectedSystemRunProfilesData.Single(rp => rp.Name == "Dummy Source System Full Sync");
        ActivitiesData = TestUtilities.GetActivityData(fullSyncRunProfile.RunType, fullSyncRunProfile.Id);
        MockDbSetActivities = ActivitiesData.BuildMockDbSet();
        
        // set up the Connected Systems mock
        ConnectedSystemsData = TestUtilities.GetConnectedSystemData();
        MockDbSetConnectedSystems = ConnectedSystemsData.BuildMockDbSet();
        
        // set up the Connected System Object Types mock. this acts as the persisted schema in JIM
        ConnectedSystemObjectTypesData = TestUtilities.GetConnectedSystemObjectTypeData();
        MockDbSetConnectedSystemObjectTypes = ConnectedSystemObjectTypesData.BuildMockDbSet();
        
        // set up the Connected System Objects mock
        ConnectedSystemObjectsData = TestUtilities.GetConnectedSystemObjectData();
        MockDbSetConnectedSystemObjects = ConnectedSystemObjectsData.BuildMockDbSet();
        
        // setup up the Connected System Partitions mock
        ConnectedSystemPartitionsData = TestUtilities.GetConnectedSystemPartitionData();
        MockDbSetConnectedSystemPartitions = ConnectedSystemPartitionsData.BuildMockDbSet();
        
        // set up the Pending Export objects mock
        PendingExportsData = new List<PendingExport>();
        MockDbSetPendingExports = PendingExportsData.BuildMockDbSet();
        
        // set up the Metaverse Object Types mock
        MetaverseObjectTypesData = TestUtilities.GetMetaverseObjectTypeData();
        MockDbSetMetaverseObjectTypes = MetaverseObjectTypesData.BuildMockDbSet();

        // set up the Metaverse Objects mock
        MetaverseObjectsData = TestUtilities.GetMetaverseObjectData();
        MockDbSetMetaverseObjects = MetaverseObjectsData.BuildMockDbSet();
        
        // set up the Sync Rule stub mocks. they will be customised to specific use-cases in individual tests.
        SyncRulesData = TestUtilities.GetSyncRuleData();
        MockDbSetSyncRules = SyncRulesData.BuildMockDbSet();
        
        // mock entity framework calls to use our data sources above
        MockJimDbContext = new Mock<JimDbContext>();
        MockJimDbContext.Setup(m => m.Activities).Returns(MockDbSetActivities.Object);
        MockJimDbContext.Setup(m => m.ConnectedSystemObjectTypes).Returns(MockDbSetConnectedSystemObjectTypes.Object);
        MockJimDbContext.Setup(m => m.ConnectedSystemObjects).Returns(MockDbSetConnectedSystemObjects.Object);
        MockJimDbContext.Setup(m => m.ConnectedSystemPartitions).Returns(MockDbSetConnectedSystemPartitions.Object);
        MockJimDbContext.Setup(m => m.ConnectedSystemRunProfiles).Returns(MockDbSetConnectedSystemRunProfiles.Object);
        MockJimDbContext.Setup(m => m.ConnectedSystems).Returns(MockDbSetConnectedSystems.Object);
        MockJimDbContext.Setup(m => m.MetaverseObjectTypes).Returns(MockDbSetMetaverseObjectTypes.Object);
        MockJimDbContext.Setup(m => m.MetaverseObjects).Returns(MockDbSetMetaverseObjects.Object);
        MockJimDbContext.Setup(m => m.PendingExports).Returns(MockDbSetPendingExports.Object);
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
    /// Tests that a CSO can successfully join to a Metaverse object using matching rules on a sync rule using a text data type attribute. 
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
            TargetMetaverseAttribute = MetaverseObjectTypesData.Single(q => q.Name == "User")
                .Attributes.Single(q=>q.Id == (int)MockMetaverseAttributeName.EmployeeId)
        };
        objectMatchingRule.TargetMetaverseAttributeId = objectMatchingRule.TargetMetaverseAttribute.Id;
        objectMatchingRule.Sources.Add(new SyncRuleMappingSource
        {
            Id = 1,
            ConnectedSystemAttributeId = (int)MockSourceSystemAttributeNames.EMPLOYEE_ID,
            ConnectedSystemAttribute = ConnectedSystemObjectTypesData.Single(q => q.Name == "SOURCE_USER").
                Attributes.Single(q=>q.Id == (int)MockSourceSystemAttributeNames.EMPLOYEE_ID)
        });
        importSyncRule.ObjectMatchingRules.Add(objectMatchingRule);
     
        // start the test
        var connectedSystem = await Jim.ConnectedSystems.GetConnectedSystemAsync(1);
        Assert.That(connectedSystem, Is.Not.Null, "Expected to retrieve a Connected System.");
        var activity = ActivitiesData.First();
        var runProfile = ConnectedSystemRunProfilesData.Single(q => q.ConnectedSystemId == connectedSystem.Id && q.RunType == ConnectedSystemRunType.FullSynchronisation);
        var syncFullSyncTaskProcessor = new SyncFullSyncTaskProcessor(Jim, connectedSystem, runProfile, activity, new CancellationTokenSource());
        await syncFullSyncTaskProcessor.PerformFullSyncAsync();
        
        // test that a CSO is successfully match to an MVO using the sync rule.
        // we expect the cso with employee id 123 to have joined to the mvo with employee id 123.
        Assert.That(ConnectedSystemObjectsData[0].MetaverseObject, Is.Not.Null, "Expected CSO to have joined to an MVO by Employee ID.");
        Assert.That(ConnectedSystemObjectsData[0].MetaverseObject.Id, Is.EqualTo(MetaverseObjectsData[0].Id), "Expected first CSO to have joined to the first MVO.");
        Assert.That(ConnectedSystemObjectsData[0].JoinType, Is.EqualTo(ConnectedSystemObjectJoinType.Joined), "Expected first CSO to have a join type of Joined.");
        Assert.That(ConnectedSystemObjectsData[0].DateJoined, Is.Not.Null, "Expected CSO to have joined to a DATE value.");
        
        Assert.That(MetaverseObjectsData[0].ConnectedSystemObjects, Is.Not.Null, "Expected MVO to have a non-null CSO list.");
        Assert.That(MetaverseObjectsData[0].ConnectedSystemObjects, Is.Not.Empty, "Expected MVO to have at least one CSO reference.");
        Assert.That(MetaverseObjectsData[0].ConnectedSystemObjects[0].Id, Is.EqualTo(ConnectedSystemObjectsData[0].Id), "Expected first MVO to have a reference to the first CSO.");
    }
    
    /// <summary>
    /// Tests that a CSO can successfully join to a Metaverse object using matching rules on a sync rule using a number data type attribute. 
    /// </summary>
    [Test]
    public async Task CsoJoinToMvoViaNumberAttributeTestAsync()
    {
        // get a stub import sync rule
        var importSyncRule = SyncRulesData.Single(q => q.Id == 1);
        
        // add test-specific matching rules to it
        var objectMatchingRule = new SyncRuleMapping
        {
            Id = 1,
            Type = SyncRuleMappingType.ObjectMatching,
            ObjectMatchingSynchronisationRule = importSyncRule,
            TargetMetaverseAttribute = MetaverseObjectTypesData.Single(q => q.Name == "User")
                .Attributes.Single(q=>q.Id == (int)MockMetaverseAttributeName.EmployeeNumber)
        };
        objectMatchingRule.TargetMetaverseAttributeId = objectMatchingRule.TargetMetaverseAttribute.Id;
        objectMatchingRule.Sources.Add(new SyncRuleMappingSource
        {
            Id = 1,
            ConnectedSystemAttributeId = (int)MockSourceSystemAttributeNames.EMPLOYEE_NUMBER,
            ConnectedSystemAttribute = ConnectedSystemObjectTypesData.Single(q => q.Name == "SOURCE_USER").
                Attributes.Single(q=>q.Id == (int)MockSourceSystemAttributeNames.EMPLOYEE_NUMBER)
        });
        importSyncRule.ObjectMatchingRules.Add(objectMatchingRule);
     
        // start the test
        var connectedSystem = await Jim.ConnectedSystems.GetConnectedSystemAsync(1);
        Assert.That(connectedSystem, Is.Not.Null, "Expected to retrieve a Connected System.");
        var activity = ActivitiesData.First();
        var runProfile = ConnectedSystemRunProfilesData.Single(q => q.ConnectedSystemId == connectedSystem.Id && q.RunType == ConnectedSystemRunType.FullSynchronisation);
        var syncFullSyncTaskProcessor = new SyncFullSyncTaskProcessor(Jim, connectedSystem, runProfile, activity, new CancellationTokenSource());
        await syncFullSyncTaskProcessor.PerformFullSyncAsync();
        
        // test that a CSO is successfully match to an MVO using the sync rule.
        // we expect the cso with employee id 123 to have joined to the mvo with employee id 123.
        Assert.That(ConnectedSystemObjectsData[0].MetaverseObject, Is.Not.Null, "Expected CSO to have joined to an MVO by Employee ID.");
        Assert.That(ConnectedSystemObjectsData[0].MetaverseObject.Id, Is.EqualTo(MetaverseObjectsData[0].Id), "Expected first CSO to have joined to the first MVO.");
        Assert.That(ConnectedSystemObjectsData[0].JoinType, Is.EqualTo(ConnectedSystemObjectJoinType.Joined), "Expected first CSO to have a join type of Joined.");
        Assert.That(ConnectedSystemObjectsData[0].DateJoined, Is.Not.Null, "Expected CSO to have joined to a DATE value.");
        
        Assert.That(MetaverseObjectsData[0].ConnectedSystemObjects, Is.Not.Null, "Expected MVO to have a non-null CSO list.");
        Assert.That(MetaverseObjectsData[0].ConnectedSystemObjects, Is.Not.Empty, "Expected MVO to have at least one CSO reference.");
        Assert.That(MetaverseObjectsData[0].ConnectedSystemObjects[0].Id, Is.EqualTo(ConnectedSystemObjectsData[0].Id), "Expected first MVO to have a reference to the first CSO.");
    }
    
    /// <summary>
    /// Tests that a CSO can successfully join to a Metaverse object using matching rules on a sync rule using a guid data type attribute. 
    /// </summary>
    [Test]
    public async Task CsoJoinToMvoViaGuidAttributeTestAsync()
    {
        // get a stub import sync rule
        var importSyncRule = SyncRulesData.Single(q => q.Id == 1);
        
        // add test-specific matching rules to it
        var objectMatchingRule = new SyncRuleMapping
        {
            Id = 1,
            Type = SyncRuleMappingType.ObjectMatching,
            ObjectMatchingSynchronisationRule = importSyncRule,
            TargetMetaverseAttribute = MetaverseObjectTypesData.Single(q => q.Name == "User")
                .Attributes.Single(q=>q.Id == (int)MockMetaverseAttributeName.HrId)
        };
        objectMatchingRule.TargetMetaverseAttributeId = objectMatchingRule.TargetMetaverseAttribute.Id;
        objectMatchingRule.Sources.Add(new SyncRuleMappingSource
        {
            Id = 1,
            ConnectedSystemAttributeId = (int)MockSourceSystemAttributeNames.HR_ID,
            ConnectedSystemAttribute = ConnectedSystemObjectTypesData.Single(q => q.Name == "SOURCE_USER").
                Attributes.Single(q=>q.Id == (int)MockSourceSystemAttributeNames.HR_ID)
        });
        importSyncRule.ObjectMatchingRules.Add(objectMatchingRule);
     
        // start the test
        var connectedSystem = await Jim.ConnectedSystems.GetConnectedSystemAsync(1);
        Assert.That(connectedSystem, Is.Not.Null, "Expected to retrieve a Connected System.");
        var activity = ActivitiesData.First();
        var runProfile = ConnectedSystemRunProfilesData.Single(q => q.ConnectedSystemId == connectedSystem.Id && q.RunType == ConnectedSystemRunType.FullSynchronisation);
        var syncFullSyncTaskProcessor = new SyncFullSyncTaskProcessor(Jim, connectedSystem, runProfile, activity, new CancellationTokenSource());
        await syncFullSyncTaskProcessor.PerformFullSyncAsync();
        
        // test that a CSO is successfully match to an MVO using the sync rule.
        // we expect the cso with HR_ID A98D00CB-FB7F-48BE-A093-DF79E193836E to have joined to the mvo with HrId A98D00CB-FB7F-48BE-A093-DF79E193836E.
        Assert.That(ConnectedSystemObjectsData[0].MetaverseObject, Is.Not.Null, "Expected CSO to have joined to an MVO by HR ID.");
        Assert.That(ConnectedSystemObjectsData[0].MetaverseObject.Id, Is.EqualTo(MetaverseObjectsData[0].Id), "Expected first CSO to have joined to the first MVO.");
        Assert.That(ConnectedSystemObjectsData[0].JoinType, Is.EqualTo(ConnectedSystemObjectJoinType.Joined), "Expected first CSO to have a join type of Joined.");
        Assert.That(ConnectedSystemObjectsData[0].DateJoined, Is.Not.Null, "Expected CSO to have joined to a DateJoined value.");
        
        Assert.That(MetaverseObjectsData[0].ConnectedSystemObjects, Is.Not.Null, "Expected MVO to have a non-null CSO list.");
        Assert.That(MetaverseObjectsData[0].ConnectedSystemObjects, Is.Not.Empty, "Expected MVO to have at least one CSO reference.");
        Assert.That(MetaverseObjectsData[0].ConnectedSystemObjects[0].Id, Is.EqualTo(ConnectedSystemObjectsData[0].Id), "Expected first MVO to have a reference to the first CSO.");
    }
    
    // todo: CSO projects to MV
    // todo: MVO has pending attribute value adds for all data types as expected
    // todo: MVO has pending attribute value removes for all data types as expected
    // todo: MVO changes are persisted as expected
    // todo: CSO obsolete/deletion process
    // todo: Onward updates as a result of all above scenarios
}
