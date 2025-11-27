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
    
    /// <summary>
    /// Tests that a CSO successfully projects to create a new Metaverse object when projection is enabled on the sync rule.
    /// </summary>
    [Test]
    public async Task CsoProjectToMvoTestAsync()
    {
        // get a stub import sync rule
        var importSyncRule = SyncRulesData.Single(q => q.Id == 1);

        // enable projection on the sync rule
        importSyncRule.ProjectToMetaverse = true;

        // start the test
        var connectedSystem = await Jim.ConnectedSystems.GetConnectedSystemAsync(1);
        Assert.That(connectedSystem, Is.Not.Null, "Expected to retrieve a Connected System.");
        var activity = ActivitiesData.First();
        var runProfile = ConnectedSystemRunProfilesData.Single(q => q.ConnectedSystemId == connectedSystem.Id && q.RunType == ConnectedSystemRunType.FullSynchronisation);
        var syncFullSyncTaskProcessor = new SyncFullSyncTaskProcessor(Jim, connectedSystem, runProfile, activity, new CancellationTokenSource());
        await syncFullSyncTaskProcessor.PerformFullSyncAsync();

        // test that a CSO successfully projected to create a new MVO
        Assert.That(ConnectedSystemObjectsData[0].MetaverseObject, Is.Not.Null, "Expected CSO to have projected to create an MVO.");
        Assert.That(ConnectedSystemObjectsData[0].JoinType, Is.EqualTo(ConnectedSystemObjectJoinType.Projected), "Expected CSO to have a join type of Projected.");
        Assert.That(ConnectedSystemObjectsData[0].DateJoined, Is.Not.Null, "Expected CSO to have a DateJoined value.");

        // verify the MVO was created with the correct type
        var projectedMvo = ConnectedSystemObjectsData[0].MetaverseObject;
        Assert.That(projectedMvo.Type, Is.Not.Null, "Expected projected MVO to have a type.");
        Assert.That(projectedMvo.Type.Id, Is.EqualTo(importSyncRule.MetaverseObjectType.Id), "Expected projected MVO type to match the sync rule's MVO type.");

        // verify the MVO has a reference back to the CSO
        Assert.That(projectedMvo.ConnectedSystemObjects, Is.Not.Null, "Expected projected MVO to have a non-null CSO list.");
        Assert.That(projectedMvo.ConnectedSystemObjects, Is.Not.Empty, "Expected projected MVO to have at least one CSO reference.");
        Assert.That(projectedMvo.ConnectedSystemObjects.Contains(ConnectedSystemObjectsData[0]), "Expected projected MVO to reference the CSO that created it.");

        // verify that a second CSO also projects to create its own MVO (each CSO should create its own MVO when projection is enabled)
        Assert.That(ConnectedSystemObjectsData[1].MetaverseObject, Is.Not.Null, "Expected second CSO to have projected to create an MVO.");
        Assert.That(ConnectedSystemObjectsData[1].MetaverseObject.Id, Is.Not.EqualTo(projectedMvo.Id), "Expected second CSO to create a different MVO than the first CSO.");
    }

    /// <summary>
    /// Tests that pending attribute value adds are created for all data types when a CSO joins/projects to an MVO.
    /// </summary>
    [Test]
    public async Task MvoPendingAttributeValueAddsAllDataTypesTestAsync()
    {
        // get a stub import sync rule
        var importSyncRule = SyncRulesData.Single(q => q.Id == 1);

        // enable projection on the sync rule
        importSyncRule.ProjectToMetaverse = true;

        // add attribute flow rules for all data types
        var mvUserType = MetaverseObjectTypesData.Single(q => q.Name == "User");
        var csUserType = ConnectedSystemObjectTypesData.Single(q => q.Name == "SOURCE_USER");

        // Text attribute mapping (Display Name)
        var displayNameMapping = new SyncRuleMapping
        {
            Id = 100,
            Type = SyncRuleMappingType.AttributeFlow,
            AttributeFlowSynchronisationRule = importSyncRule,
            TargetMetaverseAttribute = mvUserType.Attributes.Single(a => a.Id == (int)MockMetaverseAttributeName.DisplayName),
            TargetMetaverseAttributeId = (int)MockMetaverseAttributeName.DisplayName
        };
        displayNameMapping.Sources.Add(new SyncRuleMappingSource
        {
            Id = 1000,
            ConnectedSystemAttributeId = (int)MockSourceSystemAttributeNames.DISPLAY_NAME,
            ConnectedSystemAttribute = csUserType.Attributes.Single(a => a.Id == (int)MockSourceSystemAttributeNames.DISPLAY_NAME),
            Order = 1
        });
        importSyncRule.AttributeFlowRules.Add(displayNameMapping);

        // DateTime attribute mapping (Start Date)
        var startDateMapping = new SyncRuleMapping
        {
            Id = 101,
            Type = SyncRuleMappingType.AttributeFlow,
            AttributeFlowSynchronisationRule = importSyncRule,
            TargetMetaverseAttribute = mvUserType.Attributes.Single(a => a.Id == (int)MockMetaverseAttributeName.EmployeeStartDate),
            TargetMetaverseAttributeId = (int)MockMetaverseAttributeName.EmployeeStartDate
        };
        startDateMapping.Sources.Add(new SyncRuleMappingSource
        {
            Id = 1001,
            ConnectedSystemAttributeId = (int)MockSourceSystemAttributeNames.START_DATE,
            ConnectedSystemAttribute = csUserType.Attributes.Single(a => a.Id == (int)MockSourceSystemAttributeNames.START_DATE),
            Order = 1
        });
        importSyncRule.AttributeFlowRules.Add(startDateMapping);

        // Number attribute mapping (Employee Number)
        var employeeNumberMapping = new SyncRuleMapping
        {
            Id = 102,
            Type = SyncRuleMappingType.AttributeFlow,
            AttributeFlowSynchronisationRule = importSyncRule,
            TargetMetaverseAttribute = mvUserType.Attributes.Single(a => a.Id == (int)MockMetaverseAttributeName.EmployeeNumber),
            TargetMetaverseAttributeId = (int)MockMetaverseAttributeName.EmployeeNumber
        };
        employeeNumberMapping.Sources.Add(new SyncRuleMappingSource
        {
            Id = 1002,
            ConnectedSystemAttributeId = (int)MockSourceSystemAttributeNames.EMPLOYEE_NUMBER,
            ConnectedSystemAttribute = csUserType.Attributes.Single(a => a.Id == (int)MockSourceSystemAttributeNames.EMPLOYEE_NUMBER),
            Order = 1
        });
        importSyncRule.AttributeFlowRules.Add(employeeNumberMapping);

        // Guid attribute mapping (HR ID)
        var hrIdMapping = new SyncRuleMapping
        {
            Id = 103,
            Type = SyncRuleMappingType.AttributeFlow,
            AttributeFlowSynchronisationRule = importSyncRule,
            TargetMetaverseAttribute = mvUserType.Attributes.Single(a => a.Id == (int)MockMetaverseAttributeName.HrId),
            TargetMetaverseAttributeId = (int)MockMetaverseAttributeName.HrId
        };
        hrIdMapping.Sources.Add(new SyncRuleMappingSource
        {
            Id = 1003,
            ConnectedSystemAttributeId = (int)MockSourceSystemAttributeNames.HR_ID,
            ConnectedSystemAttribute = csUserType.Attributes.Single(a => a.Id == (int)MockSourceSystemAttributeNames.HR_ID),
            Order = 1
        });
        importSyncRule.AttributeFlowRules.Add(hrIdMapping);

        // Reference attribute mapping (Manager)
        var managerMapping = new SyncRuleMapping
        {
            Id = 104,
            Type = SyncRuleMappingType.AttributeFlow,
            AttributeFlowSynchronisationRule = importSyncRule,
            TargetMetaverseAttribute = mvUserType.Attributes.Single(a => a.Id == (int)MockMetaverseAttributeName.Manager),
            TargetMetaverseAttributeId = (int)MockMetaverseAttributeName.Manager
        };
        managerMapping.Sources.Add(new SyncRuleMappingSource
        {
            Id = 1004,
            ConnectedSystemAttributeId = (int)MockSourceSystemAttributeNames.MANAGER,
            ConnectedSystemAttribute = csUserType.Attributes.Single(a => a.Id == (int)MockSourceSystemAttributeNames.MANAGER),
            Order = 1
        });
        importSyncRule.AttributeFlowRules.Add(managerMapping);

        // start the test
        var connectedSystem = await Jim.ConnectedSystems.GetConnectedSystemAsync(1);
        Assert.That(connectedSystem, Is.Not.Null, "Expected to retrieve a Connected System.");
        var activity = ActivitiesData.First();
        var runProfile = ConnectedSystemRunProfilesData.Single(q => q.ConnectedSystemId == connectedSystem.Id && q.RunType == ConnectedSystemRunType.FullSynchronisation);
        var syncFullSyncTaskProcessor = new SyncFullSyncTaskProcessor(Jim, connectedSystem, runProfile, activity, new CancellationTokenSource());
        await syncFullSyncTaskProcessor.PerformFullSyncAsync();

        // get the first projected MVO
        var projectedMvo = ConnectedSystemObjectsData[0].MetaverseObject;
        Assert.That(projectedMvo, Is.Not.Null, "Expected CSO to have projected to an MVO.");

        // verify pending attribute values were created for Text (DisplayName)
        var pendingDisplayName = projectedMvo.PendingAttributeValueAdditions.Where(av =>
            av.AttributeId == (int)MockMetaverseAttributeName.DisplayName).ToList();
        Assert.That(pendingDisplayName, Is.Not.Empty, "Expected pending DisplayName attribute value to be created.");
        Assert.That(pendingDisplayName.First().StringValue, Is.EqualTo(ConnectedSystemObjectsData[0].AttributeValues.Single(a => a.AttributeId == (int)MockSourceSystemAttributeNames.DISPLAY_NAME).StringValue),
            "Expected DisplayName to match CSO value.");

        // verify pending attribute values were created for DateTime (StartDate)
        var pendingStartDate = projectedMvo.PendingAttributeValueAdditions.Where(av =>
            av.AttributeId == (int)MockMetaverseAttributeName.EmployeeStartDate).ToList();
        Assert.That(pendingStartDate, Is.Not.Empty, "Expected pending EmployeeStartDate attribute value to be created.");
        Assert.That(pendingStartDate.First().DateTimeValue, Is.EqualTo(ConnectedSystemObjectsData[0].AttributeValues.Single(a => a.AttributeId == (int)MockSourceSystemAttributeNames.START_DATE).DateTimeValue),
            "Expected StartDate to match CSO value.");

        // verify pending attribute values were created for Number (EmployeeNumber)
        var pendingEmployeeNumber = projectedMvo.PendingAttributeValueAdditions.Where(av =>
            av.AttributeId == (int)MockMetaverseAttributeName.EmployeeNumber).ToList();
        Assert.That(pendingEmployeeNumber, Is.Not.Empty, "Expected pending EmployeeNumber attribute value to be created.");
        Assert.That(pendingEmployeeNumber.First().IntValue, Is.EqualTo(ConnectedSystemObjectsData[0].AttributeValues.Single(a => a.AttributeId == (int)MockSourceSystemAttributeNames.EMPLOYEE_NUMBER).IntValue),
            "Expected EmployeeNumber to match CSO value.");

        // verify pending attribute values were created for Guid (HrId)
        var pendingHrId = projectedMvo.PendingAttributeValueAdditions.Where(av =>
            av.AttributeId == (int)MockMetaverseAttributeName.HrId).ToList();
        Assert.That(pendingHrId, Is.Not.Empty, "Expected pending HrId attribute value to be created.");
        Assert.That(pendingHrId.First().GuidValue, Is.EqualTo(ConnectedSystemObjectsData[0].AttributeValues.Single(a => a.AttributeId == (int)MockSourceSystemAttributeNames.HR_ID).GuidValue),
            "Expected HrId to match CSO value.");

        // verify pending attribute values were created for Reference (Manager)
        var pendingManager = projectedMvo.PendingAttributeValueAdditions.Where(av =>
            av.AttributeId == (int)MockMetaverseAttributeName.Manager).ToList();
        Assert.That(pendingManager, Is.Not.Empty, "Expected pending Manager attribute value to be created.");
    }

    /// <summary>
    /// Tests that pending attribute value removes are created when CSO attribute values are deleted.
    /// </summary>
    [Test]
    public async Task MvoPendingAttributeValueRemovesTestAsync()
    {
        // get a stub import sync rule
        var importSyncRule = SyncRulesData.Single(q => q.Id == 1);

        // add object matching rule to join CSO to existing MVO
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

        // add attribute flow rule for DisplayName
        var mvUserType = MetaverseObjectTypesData.Single(q => q.Name == "User");
        var csUserType = ConnectedSystemObjectTypesData.Single(q => q.Name == "SOURCE_USER");

        var displayNameMapping = new SyncRuleMapping
        {
            Id = 100,
            Type = SyncRuleMappingType.AttributeFlow,
            AttributeFlowSynchronisationRule = importSyncRule,
            TargetMetaverseAttribute = mvUserType.Attributes.Single(a => a.Id == (int)MockMetaverseAttributeName.DisplayName),
            TargetMetaverseAttributeId = (int)MockMetaverseAttributeName.DisplayName
        };
        displayNameMapping.Sources.Add(new SyncRuleMappingSource
        {
            Id = 1000,
            ConnectedSystemAttributeId = (int)MockSourceSystemAttributeNames.DISPLAY_NAME,
            ConnectedSystemAttribute = csUserType.Attributes.Single(a => a.Id == (int)MockSourceSystemAttributeNames.DISPLAY_NAME),
            Order = 1
        });
        importSyncRule.AttributeFlowRules.Add(displayNameMapping);

        // remove the DisplayName attribute from the CSO (simulating a delete)
        var csoDisplayNameAttr = ConnectedSystemObjectsData[0].AttributeValues.FirstOrDefault(a => a.AttributeId == (int)MockSourceSystemAttributeNames.DISPLAY_NAME);
        if (csoDisplayNameAttr != null)
            ConnectedSystemObjectsData[0].AttributeValues.Remove(csoDisplayNameAttr);

        // start the test
        var connectedSystem = await Jim.ConnectedSystems.GetConnectedSystemAsync(1);
        Assert.That(connectedSystem, Is.Not.Null, "Expected to retrieve a Connected System.");
        var activity = ActivitiesData.First();
        var runProfile = ConnectedSystemRunProfilesData.Single(q => q.ConnectedSystemId == connectedSystem.Id && q.RunType == ConnectedSystemRunType.FullSynchronisation);
        var syncFullSyncTaskProcessor = new SyncFullSyncTaskProcessor(Jim, connectedSystem, runProfile, activity, new CancellationTokenSource());
        await syncFullSyncTaskProcessor.PerformFullSyncAsync();

        // get the joined MVO
        var joinedMvo = MetaverseObjectsData[0];
        Assert.That(joinedMvo, Is.Not.Null, "Expected to find the MVO.");

        // verify a pending removal was created for DisplayName that exists on MVO but not on CSO
        var pendingRemoval = joinedMvo.PendingAttributeValueRemovals.Where(av =>
            av.AttributeId == (int)MockMetaverseAttributeName.DisplayName).ToList();
        Assert.That(pendingRemoval, Is.Not.Empty, "Expected pending removal for DisplayName attribute value.");
    }

    /// <summary>
    /// Tests that non-joined CSOs can be successfully deleted when marked as obsolete.
    /// </summary>
    [Test]
    public async Task CsoObsoleteDeletionForNonJoinedCsoTestAsync()
    {
        // get a stub import sync rule
        var importSyncRule = SyncRulesData.Single(q => q.Id == 1);

        // mark first CSO as obsolete (simulating it was not present in the import)
        ConnectedSystemObjectsData[0].Status = ConnectedSystemObjectStatus.Obsolete;

        // ensure CSO is not joined to any MVO
        ConnectedSystemObjectsData[0].MetaverseObject = null;
        ConnectedSystemObjectsData[0].MetaverseObjectId = null;
        ConnectedSystemObjectsData[0].JoinType = ConnectedSystemObjectJoinType.NotJoined;

        var initialCsoCount = ConnectedSystemObjectsData.Count;

        // start the test
        var connectedSystem = await Jim.ConnectedSystems.GetConnectedSystemAsync(1);
        Assert.That(connectedSystem, Is.Not.Null, "Expected to retrieve a Connected System.");
        var activity = ActivitiesData.First();
        var runProfile = ConnectedSystemRunProfilesData.Single(q => q.ConnectedSystemId == connectedSystem.Id && q.RunType == ConnectedSystemRunType.FullSynchronisation);

        // setup mock to handle CSO deletion
        MockDbSetConnectedSystemObjects.Setup(set => set.Remove(It.IsAny<ConnectedSystemObject>())).Callback(
            (ConnectedSystemObject entity) => {
                ConnectedSystemObjectsData.Remove(entity);
            });

        var syncFullSyncTaskProcessor = new SyncFullSyncTaskProcessor(Jim, connectedSystem, runProfile, activity, new CancellationTokenSource());
        await syncFullSyncTaskProcessor.PerformFullSyncAsync();

        // verify that the obsolete, non-joined CSO was deleted
        Assert.That(ConnectedSystemObjectsData.Count, Is.EqualTo(initialCsoCount - 1), "Expected one CSO to be deleted.");
        Assert.That(ConnectedSystemObjectsData.Any(cso => cso.Status == ConnectedSystemObjectStatus.Obsolete), Is.False,
            "Expected no obsolete CSOs to remain after processing.");
    }

    /// <summary>
    /// Tests that attempting to delete a joined CSO throws NotImplementedException as this functionality is pending.
    /// This test documents the current limitation and should be updated when MVO deletion logic is implemented.
    /// </summary>
    [Test]
    public async Task CsoObsoleteDeletionForJoinedCsoThrowsNotImplementedTestAsync()
    {
        // get a stub import sync rule with object matching
        var importSyncRule = SyncRulesData.Single(q => q.Id == 1);

        // add object matching rule to ensure CSO is joined to MVO
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

        // manually join the first CSO to the first MVO to set up the test scenario
        ConnectedSystemObjectsData[0].MetaverseObject = MetaverseObjectsData[0];
        ConnectedSystemObjectsData[0].MetaverseObjectId = MetaverseObjectsData[0].Id;
        ConnectedSystemObjectsData[0].JoinType = ConnectedSystemObjectJoinType.Joined;
        ConnectedSystemObjectsData[0].DateJoined = DateTime.UtcNow;
        MetaverseObjectsData[0].ConnectedSystemObjects.Add(ConnectedSystemObjectsData[0]);

        // verify the CSO is now joined to an MVO
        Assert.That(ConnectedSystemObjectsData[0].MetaverseObject, Is.Not.Null, "Expected CSO to be joined to an MVO.");

        // mark the joined CSO as obsolete (simulating it was not present in the import)
        ConnectedSystemObjectsData[0].Status = ConnectedSystemObjectStatus.Obsolete;

        // verify that attempting to process this throws NotImplementedException
        var ex = Assert.ThrowsAsync<NotImplementedException>(async () =>
        {
            var connectedSystem = await Jim.ConnectedSystems.GetConnectedSystemAsync(1);
            var activity = ActivitiesData.First();
            var runProfile = ConnectedSystemRunProfilesData.Single(q => q.ConnectedSystemId == connectedSystem.Id && q.RunType == ConnectedSystemRunType.FullSynchronisation);
            var syncFullSyncTaskProcessor = new SyncFullSyncTaskProcessor(Jim, connectedSystem, runProfile, activity, new CancellationTokenSource());
            await syncFullSyncTaskProcessor.PerformFullSyncAsync();
        });
        Assert.That(ex, Is.Not.Null);
    }

    /// <summary>
    /// Tests that CSOs correctly transition from non-joined to joined status during sync.
    /// </summary>
    [Test]
    public async Task CsoTransitionFromNonJoinedToJoinedTestAsync()
    {
        // get a stub import sync rule
        var importSyncRule = SyncRulesData.Single(q => q.Id == 1);

        // add object matching rule
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

        // ensure CSO starts as non-joined
        ConnectedSystemObjectsData[0].MetaverseObject = null;
        ConnectedSystemObjectsData[0].MetaverseObjectId = null;
        ConnectedSystemObjectsData[0].JoinType = ConnectedSystemObjectJoinType.NotJoined;
        ConnectedSystemObjectsData[0].DateJoined = null;

        // start the test
        var connectedSystem = await Jim.ConnectedSystems.GetConnectedSystemAsync(1);
        Assert.That(connectedSystem, Is.Not.Null, "Expected to retrieve a Connected System.");
        var activity = ActivitiesData.First();
        var runProfile = ConnectedSystemRunProfilesData.Single(q => q.ConnectedSystemId == connectedSystem.Id && q.RunType == ConnectedSystemRunType.FullSynchronisation);
        var syncFullSyncTaskProcessor = new SyncFullSyncTaskProcessor(Jim, connectedSystem, runProfile, activity, new CancellationTokenSource());
        await syncFullSyncTaskProcessor.PerformFullSyncAsync();

        // verify CSO successfully joined to MVO
        Assert.That(ConnectedSystemObjectsData[0].MetaverseObject, Is.Not.Null, "Expected CSO to have joined to an MVO.");
        Assert.That(ConnectedSystemObjectsData[0].JoinType, Is.EqualTo(ConnectedSystemObjectJoinType.Joined), "Expected CSO join type to be Joined.");
        Assert.That(ConnectedSystemObjectsData[0].DateJoined, Is.Not.Null, "Expected CSO to have a DateJoined value.");
        Assert.That(ConnectedSystemObjectsData[0].MetaverseObject.ConnectedSystemObjects.Contains(ConnectedSystemObjectsData[0]),
            "Expected MVO to reference the CSO.");
    }

    /// <summary>
    /// Tests that multiple CSOs from the same connected system cannot join to the same MVO (enforces 1:1 relationship per system).
    /// </summary>
    [Test]
    public async Task MultipleCsosCannotJoinToSameMvoTestAsync()
    {
        // get a stub import sync rule
        var importSyncRule = SyncRulesData.Single(q => q.Id == 1);

        // add object matching rule that will match both CSOs to the same MVO
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

        // make both CSOs have the same EMPLOYEE_ID so they match to the same MVO
        var employeeIdAttr1 = ConnectedSystemObjectsData[0].AttributeValues.First(a => a.AttributeId == (int)MockSourceSystemAttributeNames.EMPLOYEE_ID);
        var employeeIdAttr2 = ConnectedSystemObjectsData[1].AttributeValues.First(a => a.AttributeId == (int)MockSourceSystemAttributeNames.EMPLOYEE_ID);
        employeeIdAttr2.StringValue = employeeIdAttr1.StringValue; // set to same value

        // ensure both CSOs start as non-joined
        ConnectedSystemObjectsData[0].MetaverseObject = null;
        ConnectedSystemObjectsData[0].MetaverseObjectId = null;
        ConnectedSystemObjectsData[0].JoinType = ConnectedSystemObjectJoinType.NotJoined;
        ConnectedSystemObjectsData[1].MetaverseObject = null;
        ConnectedSystemObjectsData[1].MetaverseObjectId = null;
        ConnectedSystemObjectsData[1].JoinType = ConnectedSystemObjectJoinType.NotJoined;

        // start the test
        var connectedSystem = await Jim.ConnectedSystems.GetConnectedSystemAsync(1);
        Assert.That(connectedSystem, Is.Not.Null, "Expected to retrieve a Connected System.");
        var activity = ActivitiesData.First();
        var runProfile = ConnectedSystemRunProfilesData.Single(q => q.ConnectedSystemId == connectedSystem.Id && q.RunType == ConnectedSystemRunType.FullSynchronisation);
        var syncFullSyncTaskProcessor = new SyncFullSyncTaskProcessor(Jim, connectedSystem, runProfile, activity, new CancellationTokenSource());
        await syncFullSyncTaskProcessor.PerformFullSyncAsync();

        // verify that only one CSO successfully joined
        var joinedCsos = ConnectedSystemObjectsData.Where(cso => cso.MetaverseObject != null).ToList();
        Assert.That(joinedCsos.Count, Is.EqualTo(1), "Expected only one CSO to successfully join when multiple CSOs match the same MVO.");

        // verify that the activity recorded an error for the duplicate join attempt
        Assert.That(activity.RunProfileExecutionItems, Is.Not.Empty, "Expected run profile execution items to be created.");
        var errorItems = activity.RunProfileExecutionItems.Where(item => item.ErrorType == ActivityRunProfileExecutionItemErrorType.CouldNotJoinDueToExistingJoin).ToList();
        Assert.That(errorItems, Is.Not.Empty, "Expected at least one error for duplicate join attempt.");
    }

    // todo: MVO changes are persisted as expected (requires understanding persistence layer)
    // todo: Onward updates/exports as a result of MVO changes (export scenario)
}
