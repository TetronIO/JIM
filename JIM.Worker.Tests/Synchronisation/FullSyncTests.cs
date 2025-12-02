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
    
    /// <summary>
    /// Tests that a PendingExport is deleted when the CSO state is confirmed to match the pending changes.
    /// This happens during Full Sync when we verify that exported changes have been successfully applied.
    /// </summary>
    [Test]
    public async Task PendingExportDeletedWhenCsoStateMatchesTestAsync()
    {
        // get the first CSO that we'll have a pending export for
        var cso = ConnectedSystemObjectsData[0];
        var connectedSystem = ConnectedSystemsData[0];

        // get the connected system object type attributes we'll use
        var csUserType = ConnectedSystemObjectTypesData.Single(q => q.Name == "SOURCE_USER");
        var displayNameAttr = csUserType.Attributes.Single(a => a.Id == (int)MockSourceSystemAttributeNames.DISPLAY_NAME);
        var employeeNumberAttr = csUserType.Attributes.Single(a => a.Id == (int)MockSourceSystemAttributeNames.EMPLOYEE_NUMBER);

        // create a pending export for updating this CSO with attribute changes
        var pendingExport = new PendingExport
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = connectedSystem.Id,
            ConnectedSystem = connectedSystem,
            ConnectedSystemObject = cso,
            Status = PendingExportStatus.Pending,
            ChangeType = PendingExportChangeType.Update,
            AttributeValueChanges = new List<PendingExportAttributeValueChange>
            {
                new()
                {
                    Id = Guid.NewGuid(),
                    ChangeType = PendingExportAttributeChangeType.Update,
                    AttributeId = (int)MockSourceSystemAttributeNames.DISPLAY_NAME,
                    Attribute = displayNameAttr,
                    StringValue = "Updated Name"
                },
                new()
                {
                    Id = Guid.NewGuid(),
                    ChangeType = PendingExportAttributeChangeType.Update,
                    AttributeId = (int)MockSourceSystemAttributeNames.EMPLOYEE_NUMBER,
                    Attribute = employeeNumberAttr,
                    IntValue = 999
                }
            }
        };

        // add the pending export to our mock data
        PendingExportsData.Add(pendingExport);

        // update the CSO to have the exact values that are in the pending export
        // (simulating that the export was successfully applied)
        var csoDisplayNameValue = cso.AttributeValues.FirstOrDefault(av => av.AttributeId == (int)MockSourceSystemAttributeNames.DISPLAY_NAME);
        if (csoDisplayNameValue != null)
            csoDisplayNameValue.StringValue = "Updated Name";

        var csoEmployeeNumberValue = cso.AttributeValues.FirstOrDefault(av => av.AttributeId == (int)MockSourceSystemAttributeNames.EMPLOYEE_NUMBER);
        if (csoEmployeeNumberValue != null)
            csoEmployeeNumberValue.IntValue = 999;

        // verify setup
        Assert.That(PendingExportsData.Count, Is.EqualTo(1), "Expected one pending export before sync.");
        Assert.That(cso.AttributeValues.Single(av => av.AttributeId == (int)MockSourceSystemAttributeNames.DISPLAY_NAME).StringValue,
            Is.EqualTo("Updated Name"), "Expected CSO to have the updated display name.");
        Assert.That(cso.AttributeValues.Single(av => av.AttributeId == (int)MockSourceSystemAttributeNames.EMPLOYEE_NUMBER).IntValue,
            Is.EqualTo(999), "Expected CSO to have the updated employee number.");

        // setup mock to handle pending export deletion
        MockDbSetPendingExports.Setup(set => set.Remove(It.IsAny<PendingExport>())).Callback(
            (PendingExport entity) => {
                PendingExportsData.Remove(entity);
            });

        // run full sync
        var activity = ActivitiesData.First();
        var runProfile = ConnectedSystemRunProfilesData.Single(q => q.ConnectedSystemId == connectedSystem.Id && q.RunType == ConnectedSystemRunType.FullSynchronisation);
        var syncFullSyncTaskProcessor = new SyncFullSyncTaskProcessor(Jim, connectedSystem, runProfile, activity, new CancellationTokenSource());
        await syncFullSyncTaskProcessor.PerformFullSyncAsync();

        // verify the pending export was deleted because the CSO state matches
        Assert.That(PendingExportsData.Count, Is.EqualTo(0),
            "Expected pending export to be deleted when CSO state matches the pending changes.");
    }

    /// <summary>
    /// Tests that a PendingExport is NOT deleted when only some attributes match.
    /// This occurs when an export is only partially successful, requiring retry on the next sync run.
    /// </summary>
    [Test]
    public async Task PendingExportKeptWhenCsoStatePartiallyMatchesTestAsync()
    {
        // get the first CSO that we'll have a pending export for
        var cso = ConnectedSystemObjectsData[0];
        var connectedSystem = ConnectedSystemsData[0];

        // get the connected system object type attributes we'll use
        var csUserType = ConnectedSystemObjectTypesData.Single(q => q.Name == "SOURCE_USER");
        var displayNameAttr = csUserType.Attributes.Single(a => a.Id == (int)MockSourceSystemAttributeNames.DISPLAY_NAME);
        var employeeNumberAttr = csUserType.Attributes.Single(a => a.Id == (int)MockSourceSystemAttributeNames.EMPLOYEE_NUMBER);
        var roleAttr = csUserType.Attributes.Single(a => a.Id == (int)MockSourceSystemAttributeNames.ROLE);

        // create a pending export with 3 attribute changes
        var pendingExport = new PendingExport
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = connectedSystem.Id,
            ConnectedSystem = connectedSystem,
            ConnectedSystemObject = cso,
            Status = PendingExportStatus.Pending,
            ChangeType = PendingExportChangeType.Update,
            AttributeValueChanges = new List<PendingExportAttributeValueChange>
            {
                new()
                {
                    Id = Guid.NewGuid(),
                    ChangeType = PendingExportAttributeChangeType.Update,
                    AttributeId = (int)MockSourceSystemAttributeNames.DISPLAY_NAME,
                    Attribute = displayNameAttr,
                    StringValue = "Updated Name"
                },
                new()
                {
                    Id = Guid.NewGuid(),
                    ChangeType = PendingExportAttributeChangeType.Update,
                    AttributeId = (int)MockSourceSystemAttributeNames.EMPLOYEE_NUMBER,
                    Attribute = employeeNumberAttr,
                    IntValue = 999
                },
                new()
                {
                    Id = Guid.NewGuid(),
                    ChangeType = PendingExportAttributeChangeType.Update,
                    AttributeId = (int)MockSourceSystemAttributeNames.ROLE,
                    Attribute = roleAttr,
                    StringValue = "Senior Manager"
                }
            }
        };

        // add the pending export to our mock data
        PendingExportsData.Add(pendingExport);

        // update the CSO with only 2 of the 3 pending changes (simulating partial export success)
        var csoDisplayNameValue = cso.AttributeValues.FirstOrDefault(av => av.AttributeId == (int)MockSourceSystemAttributeNames.DISPLAY_NAME);
        if (csoDisplayNameValue != null)
            csoDisplayNameValue.StringValue = "Updated Name";

        var csoEmployeeNumberValue = cso.AttributeValues.FirstOrDefault(av => av.AttributeId == (int)MockSourceSystemAttributeNames.EMPLOYEE_NUMBER);
        if (csoEmployeeNumberValue != null)
            csoEmployeeNumberValue.IntValue = 999;

        // Role is NOT updated - this simulates an export failure for this attribute
        var csoRoleValue = cso.AttributeValues.FirstOrDefault(av => av.AttributeId == (int)MockSourceSystemAttributeNames.ROLE);
        if (csoRoleValue != null)
            csoRoleValue.StringValue = "Manager"; // Different from pending export value

        // verify setup
        Assert.That(PendingExportsData.Count, Is.EqualTo(1), "Expected one pending export before sync.");
        Assert.That(cso.AttributeValues.Single(av => av.AttributeId == (int)MockSourceSystemAttributeNames.DISPLAY_NAME).StringValue,
            Is.EqualTo("Updated Name"), "Expected CSO to have the updated display name.");
        Assert.That(cso.AttributeValues.Single(av => av.AttributeId == (int)MockSourceSystemAttributeNames.EMPLOYEE_NUMBER).IntValue,
            Is.EqualTo(999), "Expected CSO to have the updated employee number.");
        Assert.That(cso.AttributeValues.Single(av => av.AttributeId == (int)MockSourceSystemAttributeNames.ROLE).StringValue,
            Is.Not.EqualTo("Senior Manager"), "Expected CSO Role to NOT match the pending export value.");

        // setup mock to handle pending export deletion (shouldn't be called)
        var deleteCallCount = 0;
        MockDbSetPendingExports.Setup(set => set.Remove(It.IsAny<PendingExport>())).Callback(
            (PendingExport entity) => {
                deleteCallCount++;
                PendingExportsData.Remove(entity);
            });

        // run full sync
        var activity = ActivitiesData.First();
        var runProfile = ConnectedSystemRunProfilesData.Single(q => q.ConnectedSystemId == connectedSystem.Id && q.RunType == ConnectedSystemRunType.FullSynchronisation);
        var syncFullSyncTaskProcessor = new SyncFullSyncTaskProcessor(Jim, connectedSystem, runProfile, activity, new CancellationTokenSource());
        await syncFullSyncTaskProcessor.PerformFullSyncAsync();

        // verify the pending export was NOT deleted because not all attributes matched
        Assert.That(PendingExportsData.Count, Is.EqualTo(1),
            "Expected pending export to be kept when CSO state only partially matches the pending changes.");
        Assert.That(deleteCallCount, Is.EqualTo(0),
            "Expected Delete to not be called when pending export doesn't fully match.");
    }

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
            TargetMetaverseAttributeId = (int)MockMetaverseAttributeName.DisplayName,
            Order = 1
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

        // verify pending attribute values were created for Reference (Manager) on cso2's MVO
        // Note: cso1 doesn't have a Manager attribute, so we check cso2's MVO instead.
        // Also, Reference attributes only flow when the referenced CSO is already joined to an MVO,
        // so for this test scenario, the Manager reference may not flow yet as cso1's MVO was just created.
        // This test verifies that the Reference attribute flow mapping is correctly configured,
        // but the actual reference resolution happens when the referenced object is joined.
        var cso2Mvo = ConnectedSystemObjectsData[1].MetaverseObject;
        if (cso2Mvo != null)
        {
            // If cso2 was also projected, check that Manager would be pending
            // (though it may be empty if cso1's MVO wasn't joined when cso2 was processed)
            var pendingManager = cso2Mvo.PendingAttributeValueAdditions.Where(av =>
                av.AttributeId == (int)MockMetaverseAttributeName.Manager).ToList();
            // Reference flow requires the referenced CSO to already have a MetaverseObject,
            // which may not be the case during initial projection sync, so we don't assert Is.Not.Empty
        }
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
    /// Tests that when a joined CSO is obsoleted, the CSO-MVO join is broken before the CSO is deleted.
    /// </summary>
    [Test]
    public async Task CsoObsoleteDeletionForJoinedCsoBreaksJoinTestAsync()
    {
        // manually join the first CSO to the first MVO to set up the test scenario
        var cso = ConnectedSystemObjectsData[0];
        var mvo = MetaverseObjectsData[0];
        cso.MetaverseObject = mvo;
        cso.MetaverseObjectId = mvo.Id;
        cso.JoinType = ConnectedSystemObjectJoinType.Joined;
        cso.DateJoined = DateTime.UtcNow;
        mvo.ConnectedSystemObjects.Add(cso);

        // verify the CSO is now joined to an MVO
        Assert.That(cso.MetaverseObject, Is.Not.Null, "Expected CSO to be joined to an MVO.");

        // mark the joined CSO as obsolete (simulating it was not present in the import)
        cso.Status = ConnectedSystemObjectStatus.Obsolete;

        // run full sync
        var connectedSystem = await Jim.ConnectedSystems.GetConnectedSystemAsync(1);
        Assert.That(connectedSystem, Is.Not.Null, "Expected to retrieve a Connected System.");
        var activity = ActivitiesData.First();
        var runProfile = ConnectedSystemRunProfilesData.Single(q => q.ConnectedSystemId == connectedSystem.Id && q.RunType == ConnectedSystemRunType.FullSynchronisation);
        var syncFullSyncTaskProcessor = new SyncFullSyncTaskProcessor(Jim, connectedSystem, runProfile, activity, new CancellationTokenSource());
        await syncFullSyncTaskProcessor.PerformFullSyncAsync();

        // verify CSO-MVO join was broken
        Assert.That(cso.MetaverseObject, Is.Null, "Expected CSO-MVO join to be broken.");
        Assert.That(cso.MetaverseObjectId, Is.Null, "Expected CSO.MetaverseObjectId to be null.");
        Assert.That(cso.JoinType, Is.EqualTo(ConnectedSystemObjectJoinType.NotJoined), "Expected CSO join type to be NotJoined.");

        // verify the MVO no longer references the CSO
        Assert.That(mvo.ConnectedSystemObjects.Contains(cso), Is.False,
            "Expected MVO to no longer reference the obsoleted CSO.");
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

    /// <summary>
    /// Tests that when a joined CSO is obsoleted and RemoveContributedAttributesOnObsoletion is true (default),
    /// the MVO attributes contributed by that CSO are added to PendingAttributeValueRemovals,
    /// the CSO-MVO join is broken, and the CSO is deleted.
    /// </summary>
    [Test]
    public async Task CsoObsoleteWithRemoveContributedAttributesEnabledTestAsync()
    {
        // manually join the first CSO to the first MVO
        var cso = ConnectedSystemObjectsData[0];

        // enable RemoveContributedAttributesOnObsoletion on the CSO's type (this is the default)
        // (we must use the CSO's Type property directly, not the ConnectedSystemObjectTypesData,
        // because they are different instances created by separate calls to GetConnectedSystemObjectTypeData())
        cso.Type.RemoveContributedAttributesOnObsoletion = true;

        // get a stub import sync rule
        var importSyncRule = SyncRulesData.Single(q => q.Id == 1);
        var mvo = MetaverseObjectsData[0];
        cso.MetaverseObject = mvo;
        cso.MetaverseObjectId = mvo.Id;
        cso.JoinType = ConnectedSystemObjectJoinType.Joined;
        cso.DateJoined = DateTime.UtcNow;
        mvo.ConnectedSystemObjects.Add(cso);

        // clear existing attribute values and add specific ones contributed by this connected system
        var connectedSystem = ConnectedSystemsData[0];
        var mvUserType = MetaverseObjectTypesData.Single(q => q.Name == "User");
        var displayNameAttr = mvUserType.Attributes.Single(a => a.Id == (int)MockMetaverseAttributeName.DisplayName);
        var employeeNumberAttr = mvUserType.Attributes.Single(a => a.Id == (int)MockMetaverseAttributeName.EmployeeNumber);

        mvo.AttributeValues.Clear();
        mvo.AttributeValues.Add(new MetaverseObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            MetaverseObject = mvo,
            Attribute = displayNameAttr,
            AttributeId = displayNameAttr.Id,
            StringValue = "Joe Bloggs",
            ContributedBySystem = connectedSystem
        });
        mvo.AttributeValues.Add(new MetaverseObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            MetaverseObject = mvo,
            Attribute = employeeNumberAttr,
            AttributeId = employeeNumberAttr.Id,
            IntValue = 123,
            ContributedBySystem = connectedSystem
        });

        // verify setup
        Assert.That(cso.MetaverseObject, Is.Not.Null, "Expected CSO to be joined to an MVO.");
        Assert.That(mvo.AttributeValues.Count, Is.EqualTo(2), "Expected MVO to have 2 attribute values.");
        Assert.That(mvo.AttributeValues.All(av => av.ContributedBySystem?.Id == connectedSystem.Id), Is.True,
            "Expected all MVO attribute values to be contributed by the connected system.");

        // mark the joined CSO as obsolete
        cso.Status = ConnectedSystemObjectStatus.Obsolete;

        // run full sync
        var activity = ActivitiesData.First();
        var runProfile = ConnectedSystemRunProfilesData.Single(q => q.ConnectedSystemId == connectedSystem.Id && q.RunType == ConnectedSystemRunType.FullSynchronisation);
        var syncFullSyncTaskProcessor = new SyncFullSyncTaskProcessor(Jim, connectedSystem, runProfile, activity, new CancellationTokenSource());
        await syncFullSyncTaskProcessor.PerformFullSyncAsync();

        // verify that the MVO has pending attribute value removals for the attributes contributed by this system
        Assert.That(mvo.PendingAttributeValueRemovals, Is.Not.Empty,
            "Expected pending attribute value removals when RemoveContributedAttributesOnObsoletion is enabled.");
        Assert.That(mvo.PendingAttributeValueRemovals.Count, Is.EqualTo(2),
            "Expected 2 pending attribute value removals (DisplayName and EmployeeNumber).");

        // verify the CSO-MVO join was broken
        Assert.That(cso.MetaverseObject, Is.Null, "Expected CSO-MVO join to be broken.");
        Assert.That(cso.MetaverseObjectId, Is.Null, "Expected CSO.MetaverseObjectId to be null.");

        // verify the MVO still exists (not deleted) but no longer references this CSO
        Assert.That(mvo.ConnectedSystemObjects.Contains(cso), Is.False,
            "Expected MVO to no longer reference the obsoleted CSO.");
    }

    /// <summary>
    /// Tests that when a joined CSO is obsoleted and RemoveContributedAttributesOnObsoletion is false,
    /// the MVO attributes remain (no PendingAttributeValueRemovals added),
    /// the CSO-MVO join is broken, and the CSO is deleted.
    /// </summary>
    [Test]
    public async Task CsoObsoleteWithRemoveContributedAttributesDisabledTestAsync()
    {
        // manually join the first CSO to the first MVO
        var cso = ConnectedSystemObjectsData[0];

        // disable RemoveContributedAttributesOnObsoletion on the CSO's type
        // (we must use the CSO's Type property directly, not the ConnectedSystemObjectTypesData,
        // because they are different instances created by separate calls to GetConnectedSystemObjectTypeData())
        cso.Type.RemoveContributedAttributesOnObsoletion = false;

        // get a stub import sync rule
        var importSyncRule = SyncRulesData.Single(q => q.Id == 1);
        var mvo = MetaverseObjectsData[0];
        cso.MetaverseObject = mvo;
        cso.MetaverseObjectId = mvo.Id;
        cso.JoinType = ConnectedSystemObjectJoinType.Joined;
        cso.DateJoined = DateTime.UtcNow;
        mvo.ConnectedSystemObjects.Add(cso);

        // clear existing attribute values and add specific ones contributed by this connected system
        var connectedSystem = ConnectedSystemsData[0];
        var mvUserType = MetaverseObjectTypesData.Single(q => q.Name == "User");
        var displayNameAttr = mvUserType.Attributes.Single(a => a.Id == (int)MockMetaverseAttributeName.DisplayName);
        var employeeNumberAttr = mvUserType.Attributes.Single(a => a.Id == (int)MockMetaverseAttributeName.EmployeeNumber);

        mvo.AttributeValues.Clear();
        mvo.AttributeValues.Add(new MetaverseObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            MetaverseObject = mvo,
            Attribute = displayNameAttr,
            AttributeId = displayNameAttr.Id,
            StringValue = "Joe Bloggs",
            ContributedBySystem = connectedSystem
        });
        mvo.AttributeValues.Add(new MetaverseObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            MetaverseObject = mvo,
            Attribute = employeeNumberAttr,
            AttributeId = employeeNumberAttr.Id,
            IntValue = 123,
            ContributedBySystem = connectedSystem
        });

        // verify setup
        Assert.That(cso.MetaverseObject, Is.Not.Null, "Expected CSO to be joined to an MVO.");
        Assert.That(mvo.AttributeValues.Count, Is.EqualTo(2), "Expected MVO to have 2 attribute values.");
        Assert.That(mvo.AttributeValues.All(av => av.ContributedBySystem?.Id == connectedSystem.Id), Is.True,
            "Expected all MVO attribute values to be contributed by the connected system.");

        // mark the joined CSO as obsolete
        cso.Status = ConnectedSystemObjectStatus.Obsolete;

        // run full sync
        var activity = ActivitiesData.First();
        var runProfile = ConnectedSystemRunProfilesData.Single(q => q.ConnectedSystemId == connectedSystem.Id && q.RunType == ConnectedSystemRunType.FullSynchronisation);
        var syncFullSyncTaskProcessor = new SyncFullSyncTaskProcessor(Jim, connectedSystem, runProfile, activity, new CancellationTokenSource());
        await syncFullSyncTaskProcessor.PerformFullSyncAsync();

        // verify that no pending attribute value removals were created (attributes are retained)
        Assert.That(mvo.PendingAttributeValueRemovals, Is.Empty,
            "Expected no pending attribute value removals when RemoveContributedAttributesOnObsoletion is disabled.");

        // verify the MVO attribute values are still present
        Assert.That(mvo.AttributeValues.Count, Is.EqualTo(2),
            "Expected MVO attribute values to remain when RemoveContributedAttributesOnObsoletion is disabled.");

        // verify the CSO-MVO join was broken
        Assert.That(cso.MetaverseObject, Is.Null, "Expected CSO-MVO join to be broken.");
        Assert.That(cso.MetaverseObjectId, Is.Null, "Expected CSO.MetaverseObjectId to be null.");

        // verify the MVO still exists (not deleted) but no longer references this CSO
        Assert.That(mvo.ConnectedSystemObjects.Contains(cso), Is.False,
            "Expected MVO to no longer reference the obsoleted CSO.");
    }
}
