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

public class ImportCreateObjectTests
{
    #region accessors
    private MetaverseObject InitiatedBy { get; set; }
    private List<ConnectedSystem> ConnectedSystemsData { get; set; } 
    private Mock<DbSet<ConnectedSystem>> MockDbSetConnectedSystems { get; set; }
    private List<ConnectedSystemRunProfile> ConnectedSystemRunProfilesData { get; set; }
    private Mock<DbSet<ConnectedSystemRunProfile>> MockDbSetConnectedSystemRunProfiles { get; set; }
    private List<ConnectedSystemObjectType> ConnectedSystemObjectTypesData { get; set; } 
    private Mock<DbSet<ConnectedSystemObjectType>> MockDbSetConnectedSystemObjectTypes { get; set; }
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
        TestUtilities.SetEnvironmentVariables();
        InitiatedBy = TestUtilities.GetInitiatedBy();
        
        // set up the connected systems mock
        ConnectedSystemsData = TestUtilities.GetConnectedSystemData();
        MockDbSetConnectedSystems = ConnectedSystemsData.AsQueryable().BuildMockDbSet();
        
        // setup up the connected system run profiles mock
        ConnectedSystemRunProfilesData = TestUtilities.GetConnectedSystemRunProfileData();
        MockDbSetConnectedSystemRunProfiles = ConnectedSystemRunProfilesData.AsQueryable().BuildMockDbSet();
        
        // set up the connected system object types mock. this acts as the persisted schema in JIM
        ConnectedSystemObjectTypesData = TestUtilities.GetConnectedSystemObjectTypeData();
        MockDbSetConnectedSystemObjectTypes = ConnectedSystemObjectTypesData.AsQueryable().BuildMockDbSet();
        
        // setup up the Connected System Partitions mock
        ConnectedSystemPartitionsData = TestUtilities.GetConnectedSystemPartitionData();
        MockDbSetConnectedSystemPartitions = ConnectedSystemPartitionsData.AsQueryable().BuildMockDbSet();
        
        // set up the activity mock
        var fullImportRunProfile = ConnectedSystemRunProfilesData[0];
        ActivitiesData = TestUtilities.GetActivityData(fullImportRunProfile.RunType, fullImportRunProfile.Id);
        MockDbSetActivities = ActivitiesData.AsQueryable().BuildMockDbSet();
        
        // mock entity framework calls to use our data sources above
        MockJimDbContext = new Mock<JimDbContext>();
        MockJimDbContext.Setup(m => m.Activities).Returns(MockDbSetActivities.Object);
        MockJimDbContext.Setup(m => m.ConnectedSystems).Returns(MockDbSetConnectedSystems.Object);
        MockJimDbContext.Setup(m => m.ConnectedSystemObjectTypes).Returns(MockDbSetConnectedSystemObjectTypes.Object);
        MockJimDbContext.Setup(m => m.ConnectedSystemRunProfiles).Returns(MockDbSetConnectedSystemRunProfiles.Object);
        MockJimDbContext.Setup(m => m.ConnectedSystemPartitions).Returns(MockDbSetConnectedSystemPartitions.Object);
        
        // instantiate Jim using the mocked db context
        Jim = new JimApplication(new PostgresDataRepository(MockJimDbContext.Object));
    }
    
    [Test]
    public async Task FullImportCreateTestAsync()
    {
        // set up the Connected System Objects mock. this is specific to this test
        var connectedSystemObjectData = new List<ConnectedSystemObject>();
        var mockDbSetConnectedSystemObject = connectedSystemObjectData.AsQueryable().BuildMockDbSet();
        mockDbSetConnectedSystemObject.Setup(set => set.AddRange(It.IsAny<IEnumerable<ConnectedSystemObject>>())).Callback((IEnumerable<ConnectedSystemObject> entities) => {
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
            ObjectType = "SOURCE_USER",
            Attributes = new List<ConnectedSystemImportObjectAttribute>()
            {
                new ()
                {
                    // guid
                    Name = MockSourceSystemAttributeNames.HR_ID.ToString(),
                    GuidValues = new List<Guid> { TestConstants.CS_OBJECT_1_HR_ID }
                },
                new ()
                {
                    // int
                    Name = MockSourceSystemAttributeNames.EMPLOYEE_ID.ToString(),
                    IntValues = new List<int> { 1 }
                },
                new ()
                {
                    // datetime
                    Name = MockSourceSystemAttributeNames.START_DATE.ToString(),
                    DateTimeValue = TestConstants.CS_OBJECT_1_START_DATE
                },
                new ()
                {
                    // string
                    Name = MockSourceSystemAttributeNames.DISPLAY_NAME.ToString(),
                    StringValues = new List<string> { TestConstants.CS_OBJECT_1_DISPLAY_NAME }
                },
                new ()
                {
                    // string
                    Name = MockSourceSystemAttributeNames.EMAIL_ADDRESS.ToString(),
                    StringValues = new List<string> { "jane.smith@phlebas.tetron.io" }
                },
                new ()
                {
                    // string
                    Name = MockSourceSystemAttributeNames.ROLE.ToString(),
                    StringValues = new List<string> { "Manager" }
                },
                new ()
                {
                    // mva string
                    Name = MockSourceSystemAttributeNames.QUALIFICATIONS.ToString(),
                    StringValues = new List<string> { "C-MNGT-101", "C-MNGT-102", "C-MNGT-103" }
                },
                new ()
                {
                    // boolean string
                    Name = MockSourceSystemAttributeNames.LEAVER.ToString(),
                    BoolValue = false
                }
            }
        });
        mockFileConnector.TestImportObjects.Add(new ConnectedSystemImportObject
        {
            ChangeType = ObjectChangeType.Create,
            ObjectType = "SOURCE_USER",
            Attributes = new List<ConnectedSystemImportObjectAttribute>()
            {
                new ()
                {
                    // guid
                    Name = MockSourceSystemAttributeNames.HR_ID.ToString(),
                    GuidValues = new List<Guid> { TestConstants.CS_OBJECT_2_HR_ID }
                },
                new ()
                {
                    // int
                    Name = MockSourceSystemAttributeNames.EMPLOYEE_ID.ToString(),
                    IntValues = new List<int> { 2 }
                },
                new ()
                {
                    // datetime
                    Name = MockSourceSystemAttributeNames.START_DATE.ToString(),
                    DateTimeValue = TestConstants.CS_OBJECT_2_START_DATE
                },
                new ()
                {
                    // string
                    Name = MockSourceSystemAttributeNames.DISPLAY_NAME.ToString(),
                    StringValues = new List<string> { TestConstants.CS_OBJECT_2_DISPLAY_NAME }
                },
                new ()
                {
                    // string
                    Name = MockSourceSystemAttributeNames.EMAIL_ADDRESS.ToString(),
                    StringValues = new List<string> { "joe.bloggs@phlebas.tetron.io" }
                },
                new ()
                {
                    // string
                    Name = MockSourceSystemAttributeNames.ROLE.ToString(),
                    StringValues = new List<string> { "Developer" }
                },
                new ()
                {
                    // reference
                    Name = MockSourceSystemAttributeNames.MANAGER.ToString(),
                    ReferenceValues = new List<string> { TestConstants.CS_OBJECT_1_HR_ID.ToString() }
                },
                new ()
                {
                    // mva string
                    Name = MockSourceSystemAttributeNames.QUALIFICATIONS.ToString(),
                    StringValues = new List<string> { "C-CDEV-101" }
                },
                new ()
                {
                    // boolean string
                    Name = MockSourceSystemAttributeNames.LEAVER.ToString(),
                    BoolValue = false
                }
            }
        });
        
        // now execute Jim functionality we want to test...
        var connectedSystem = await Jim.ConnectedSystems.GetConnectedSystemAsync(1);
        Assert.That(connectedSystem, Is.Not.Null, "Expected to retrieve a Connected System.");

        var activity = ActivitiesData.First();
        var runProfile = ConnectedSystemRunProfilesData.Single(q => q.ConnectedSystemId == connectedSystem.Id && q.RunType == ConnectedSystemRunType.FullImport);
        var synchronisationImportTaskProcessor = new SyncImportTaskProcessor(Jim, mockFileConnector, connectedSystem, runProfile, InitiatedBy, activity, new CancellationTokenSource());
        await synchronisationImportTaskProcessor.PerformFullImportAsync();
        
        // confirm the results persisted to the mocked db context
        Assert.That(connectedSystemObjectData, Has.Count.EqualTo(2), "Expected two Connected System Objects to have been persisted.");

        // validate the first user (who is a manager)
        var firstPersistedConnectedSystemObject = connectedSystemObjectData[0];
        var firstSourceConnectedSystemImportObject = mockFileConnector.TestImportObjects[0];
        TestUtilities.ValidateImportAttributesForEquality(firstPersistedConnectedSystemObject, firstSourceConnectedSystemImportObject, MockSourceSystemAttributeNames.HR_ID, ConnectedSystemObjectTypesData);
        TestUtilities.ValidateImportAttributesForEquality(firstPersistedConnectedSystemObject, firstSourceConnectedSystemImportObject, MockSourceSystemAttributeNames.EMPLOYEE_ID, ConnectedSystemObjectTypesData);
        TestUtilities.ValidateImportAttributesForEquality(firstPersistedConnectedSystemObject, firstSourceConnectedSystemImportObject, MockSourceSystemAttributeNames.START_DATE, ConnectedSystemObjectTypesData);
        TestUtilities.ValidateImportAttributesForEquality(firstPersistedConnectedSystemObject, firstSourceConnectedSystemImportObject, MockSourceSystemAttributeNames.DISPLAY_NAME, ConnectedSystemObjectTypesData);
        TestUtilities.ValidateImportAttributesForEquality(firstPersistedConnectedSystemObject, firstSourceConnectedSystemImportObject, MockSourceSystemAttributeNames.EMAIL_ADDRESS, ConnectedSystemObjectTypesData);
        TestUtilities.ValidateImportAttributesForEquality(firstPersistedConnectedSystemObject, firstSourceConnectedSystemImportObject, MockSourceSystemAttributeNames.ROLE, ConnectedSystemObjectTypesData);
        TestUtilities.ValidateImportAttributesForEquality(firstPersistedConnectedSystemObject, firstSourceConnectedSystemImportObject, MockSourceSystemAttributeNames.QUALIFICATIONS, ConnectedSystemObjectTypesData);
        TestUtilities.ValidateImportAttributesForEquality(firstPersistedConnectedSystemObject, firstSourceConnectedSystemImportObject, MockSourceSystemAttributeNames.LEAVER, ConnectedSystemObjectTypesData);
        
        // validate the second user (who is a direct-report)
        var secondPersistedConnectedSystemObject = connectedSystemObjectData[1];
        var secondSourceConnectedSystemImportObject = mockFileConnector.TestImportObjects[1];
        TestUtilities.ValidateImportAttributesForEquality(secondPersistedConnectedSystemObject, secondSourceConnectedSystemImportObject, MockSourceSystemAttributeNames.HR_ID, ConnectedSystemObjectTypesData);
        TestUtilities.ValidateImportAttributesForEquality(secondPersistedConnectedSystemObject, secondSourceConnectedSystemImportObject, MockSourceSystemAttributeNames.EMPLOYEE_ID, ConnectedSystemObjectTypesData);
        TestUtilities.ValidateImportAttributesForEquality(secondPersistedConnectedSystemObject, secondSourceConnectedSystemImportObject, MockSourceSystemAttributeNames.START_DATE, ConnectedSystemObjectTypesData);
        TestUtilities.ValidateImportAttributesForEquality(secondPersistedConnectedSystemObject, secondSourceConnectedSystemImportObject, MockSourceSystemAttributeNames.DISPLAY_NAME, ConnectedSystemObjectTypesData);
        TestUtilities.ValidateImportAttributesForEquality(secondPersistedConnectedSystemObject, secondSourceConnectedSystemImportObject, MockSourceSystemAttributeNames.EMAIL_ADDRESS, ConnectedSystemObjectTypesData);
        TestUtilities.ValidateImportAttributesForEquality(secondPersistedConnectedSystemObject, secondSourceConnectedSystemImportObject, MockSourceSystemAttributeNames.ROLE, ConnectedSystemObjectTypesData);
        TestUtilities.ValidateImportAttributesForEquality(secondPersistedConnectedSystemObject, secondSourceConnectedSystemImportObject, MockSourceSystemAttributeNames.QUALIFICATIONS, ConnectedSystemObjectTypesData);
        TestUtilities.ValidateImportAttributesForEquality(secondPersistedConnectedSystemObject, secondSourceConnectedSystemImportObject, MockSourceSystemAttributeNames.LEAVER, ConnectedSystemObjectTypesData);
        
        // validate second user manager reference
        var managerAttribute = secondPersistedConnectedSystemObject.AttributeValues.SingleOrDefault(q=>q.Attribute.Name == MockSourceSystemAttributeNames.MANAGER.ToString());
        Assert.That(managerAttribute, Is.Not.Null, "Expected the MANAGER attribute to not be null.");
        Assert.That(managerAttribute.ReferenceValue, Is.Not.Null, "Expected the MANAGER reference value not to be null.");
        Assert.That(!string.IsNullOrEmpty(managerAttribute.UnresolvedReferenceValue), "Expected the MANAGER UnresolvedReferenceValue to also be populated.");
        Assert.That(managerAttribute.ReferenceValue.Id, Is.EqualTo(firstPersistedConnectedSystemObject.Id), "Expected the MANAGER reference object id to match the id of the first object.");
        
        Assert.Pass();
    }
    
    /// <summary>
    /// Test whether null import object attribute values are removed by JIM on import.
    /// This is where JIM has a degree of opinion and doesn't allow empty strings/references to be imported and instead removes them,
    /// so they are imported as attribute deletes.
    /// </summary>
    [Test]
    public async Task FullImportCreateWithNullAttributesTestAsync()
    {
        // set up the Connected System Objects mock. this is specific to this test
        var connectedSystemObjectData = new List<ConnectedSystemObject>();
        var mockDbSetConnectedSystemObject = connectedSystemObjectData.AsQueryable().BuildMockDbSet();
        mockDbSetConnectedSystemObject.Setup(set => set.AddRange(It.IsAny<IEnumerable<ConnectedSystemObject>>())).Callback((IEnumerable<ConnectedSystemObject> entities) => {
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
            ObjectType = "SOURCE_USER",
            Attributes = new List<ConnectedSystemImportObjectAttribute>()
            {
                new ()
                {
                    // guid
                    Name = MockSourceSystemAttributeNames.HR_ID.ToString(),
                    GuidValues = new List<Guid> { TestConstants.CS_OBJECT_1_HR_ID }
                },
                new ()
                {
                    // int
                    Name = MockSourceSystemAttributeNames.EMPLOYEE_ID.ToString(),
                    IntValues = new List<int> { 1 }
                },
                new ()
                {
                    // datetime
                    Name = MockSourceSystemAttributeNames.START_DATE.ToString(),
                    DateTimeValue = null
                },
                new ()
                {
                    // string
                    Name = MockSourceSystemAttributeNames.DISPLAY_NAME.ToString(),
                    StringValues = new List<string> { TestConstants.CS_OBJECT_1_DISPLAY_NAME }
                },
                new ()
                {
                    // string
                    Name = MockSourceSystemAttributeNames.EMAIL_ADDRESS.ToString(),
                    StringValues = new List<string> { string.Empty }
                },
                new ()
                {
                    // string
                    Name = MockSourceSystemAttributeNames.ROLE.ToString(),
                    StringValues = new List<string> { null! }
                },
                new ()
                {
                    // mva string
                    Name = MockSourceSystemAttributeNames.QUALIFICATIONS.ToString(),
                    StringValues = new List<string> { "C-MNGT-101", "C-MNGT-102", null!, string.Empty }
                },
                new ()
                {
                    // boolean
                    Name = MockSourceSystemAttributeNames.LEAVER.ToString(),
                    BoolValue = null
                }
            }
        });
        
        // now execute Jim functionality we want to test...
        var connectedSystem = await Jim.ConnectedSystems.GetConnectedSystemAsync(1);
        Assert.That(connectedSystem, Is.Not.Null, "Expected to retrieve a Connected System.");

        var activity = ActivitiesData.First();
        var runProfile = ConnectedSystemRunProfilesData.Single(q => q.ConnectedSystemId == connectedSystem.Id && q.RunType == ConnectedSystemRunType.FullImport);
        var synchronisationImportTaskProcessor = new SyncImportTaskProcessor(Jim, mockFileConnector, connectedSystem, runProfile, InitiatedBy, activity, new CancellationTokenSource());
        await synchronisationImportTaskProcessor.PerformFullImportAsync();
        
        // confirm the results persisted to the mocked db context
        Assert.That(connectedSystemObjectData, Has.Count.EqualTo(1), "Expected one Connected System Object to have been persisted.");

        // validate the user
        var firstPersistedConnectedSystemObject = connectedSystemObjectData[0];
        var firstSourceConnectedSystemImportObject = mockFileConnector.TestImportObjects[0];
        
        TestUtilities.ValidateImportAttributesForEquality(firstPersistedConnectedSystemObject, firstSourceConnectedSystemImportObject, MockSourceSystemAttributeNames.HR_ID, ConnectedSystemObjectTypesData);
        TestUtilities.ValidateImportAttributesForEquality(firstPersistedConnectedSystemObject, firstSourceConnectedSystemImportObject, MockSourceSystemAttributeNames.EMPLOYEE_ID, ConnectedSystemObjectTypesData);
   
        // there should be no EMAIL_ADDRESS attribute on the persisted object as it had an empty string on the imported object.
        Assert.That(firstPersistedConnectedSystemObject.AttributeValues.Count(q => q.Attribute.Name == MockSourceSystemAttributeNames.EMAIL_ADDRESS.ToString()), Is.EqualTo(0), $"Didn't expect a {MockSourceSystemAttributeNames.EMAIL_ADDRESS} attribute.");
        
        // there should be no ROLE attribute on the persisted object as it had a null string on the imported object.
        Assert.That(firstPersistedConnectedSystemObject.AttributeValues.Count(q => q.Attribute.Name == MockSourceSystemAttributeNames.ROLE.ToString()), Is.EqualTo(0), $"Didn't expect a {MockSourceSystemAttributeNames.ROLE} attribute.");
        
        // there should only be three QUALIFICATIONS attributes as two were null or empty on the imported object.
        Assert.That(firstPersistedConnectedSystemObject.AttributeValues.Count(q => q.Attribute.Name == MockSourceSystemAttributeNames.QUALIFICATIONS.ToString()), Is.EqualTo(2), $"Expected only two {MockSourceSystemAttributeNames.QUALIFICATIONS} attributes.");
        
        // there should be no LEAVER attribute on the persisted object as it had a null value on the imported object.
        Assert.That(firstPersistedConnectedSystemObject.AttributeValues.Count(q => q.Attribute.Name == MockSourceSystemAttributeNames.LEAVER.ToString()), Is.EqualTo(0), $"Didn't expect a {MockSourceSystemAttributeNames.LEAVER} attribute.");
        
        Assert.Pass();
    }
    
    // todo: test activity/run profile execution item/change object creation
    // todo: test object error handling and logging scenario(s)
    // todo: test connectivity error handling and logging scenario(s)
}