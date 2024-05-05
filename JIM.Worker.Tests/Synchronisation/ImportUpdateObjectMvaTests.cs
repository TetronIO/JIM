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

public class ImportUpdateObjectMvaTests
{
    #region accessors
    private MetaverseObject InitiatedBy { get; set; }
    private List<ConnectedSystem> ConnectedSystemsData { get; set; } 
    private Mock<DbSet<ConnectedSystem>> MockDbSetConnectedSystems { get; set; }
    private List<ConnectedSystemRunProfile> ConnectedSystemRunProfilesData { get; set; }
    private Mock<DbSet<ConnectedSystemRunProfile>> MockDbSetConnectedSystemRunProfiles { get; set; }
    private List<ConnectedSystemObject> ConnectedSystemObjectsData { get; } = new();
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
        ActivitiesData = TestUtilities.GetActivityData(ConnectedSystemRunType.FullImport);
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
        
        var mockDbSetConnectedSystemObject = ConnectedSystemObjectsData.AsQueryable().BuildMockDbSet();
        mockDbSetConnectedSystemObject.Setup(set => set.AddRange(It.IsAny<IEnumerable<ConnectedSystemObject>>())).Callback((IEnumerable<ConnectedSystemObject> entities) => {
            var connectedSystemObjects = entities as ConnectedSystemObject[] ?? entities.ToArray();
            foreach (var entity in connectedSystemObjects)
                entity.Id = Guid.NewGuid(); // assign the ids here, mocking what the db would do in SaveChanges()
            ConnectedSystemObjectsData.AddRange(connectedSystemObjects);
        });
        MockJimDbContext.Setup(m => m.ConnectedSystemObjects).Returns(mockDbSetConnectedSystemObject.Object); 
    }
    
    [Test]
    public async Task FullImportUpdateAddIntMvaTestAsync()
    {
        InitialiseConnectedSystemObjectsData();
        
        // mock up a connector that will return updates for our existing connected system objects above.
        // changes: COMPLETED_COURSE_IDS has multiple values added for the first time.
        var mockFileConnector = new MockFileConnector();
        mockFileConnector.TestImportObjects.Add(new ConnectedSystemImportObject
        {
            ChangeType = ObjectChangeType.Create,
            ObjectType = "User",
            Attributes = new List<ConnectedSystemImportObjectAttribute>()
            {
                new ()
                {
                    Name = MockAttributeName.HR_ID.ToString(),
                    GuidValues = new List<Guid> { TestConstants.CS_OBJECT_1_HR_ID },
                    Type = AttributeDataType.Guid
                },
                new ()
                {
                    Name = MockAttributeName.EMPLOYEE_ID.ToString(),
                    IntValues = new List<int> { 1 },
                    Type = AttributeDataType.Number
                },
                new ()
                {
                    Name = MockAttributeName.DISPLAY_NAME.ToString(),
                    StringValues = new List<string> { TestConstants.CS_OBJECT_1_DISPLAY_NAME },
                    Type = AttributeDataType.Text
                },
                new ()
                {
                    Name = MockAttributeName.COMPLETED_COURSE_IDS.ToString(),
                    IntValues = new List<int> { 1,2,3,4,5 },
                    Type = AttributeDataType.Number
                },
            }
        });
        
        // now execute Jim functionality we want to test...
        var connectedSystem = await Jim.ConnectedSystems.GetConnectedSystemAsync(1);
        Assert.That(connectedSystem, Is.Not.Null, "Expected to retrieve a Connected System.");

        var activity = ActivitiesData.First();
        var runProfile = ConnectedSystemRunProfilesData.Single(q => q.RunType == ConnectedSystemRunType.FullImport);
        var synchronisationImportTaskProcessor = new SyncImportTaskProcessor(Jim, mockFileConnector, connectedSystem, runProfile, InitiatedBy, activity, new CancellationTokenSource());
        await synchronisationImportTaskProcessor.PerformFullImportAsync();
        
        // confirm the results persisted to the mocked db context
        Assert.That(ConnectedSystemObjectsData, Has.Count.EqualTo(2), $"Expected two Connected System Objects to remain persisted. Found {ConnectedSystemObjectsData.Count}.");
        
        // get the Connected System Object for the user we changed some attribute values for in the mocked connector
        var cso1 = await Jim.ConnectedSystems.GetConnectedSystemObjectAsync(1, TestConstants.CS_OBJECT_1_ID);
        Assert.That(cso1, Is.Not.EqualTo(null), "Expected to be able to retrieve the first CSO to validate.");

        var completedCourseIdsAttributes = cso1.GetAttributeValues(MockAttributeName.COMPLETED_COURSE_IDS.ToString());
        Assert.That(completedCourseIdsAttributes, Is.Not.Null);
        Assert.That(completedCourseIdsAttributes.Count == 5);
        
        Assert.That(completedCourseIdsAttributes[0].IntValue.HasValue);
        Assert.That(completedCourseIdsAttributes[0].IntValue.Value, Is.EqualTo(1));
        
        Assert.That(completedCourseIdsAttributes[1].IntValue.HasValue);
        Assert.That(completedCourseIdsAttributes[1].IntValue.Value, Is.EqualTo(2));
        
        Assert.That(completedCourseIdsAttributes[2].IntValue.HasValue);
        Assert.That(completedCourseIdsAttributes[2].IntValue.Value, Is.EqualTo(3));
        
        Assert.That(completedCourseIdsAttributes[3].IntValue.HasValue);
        Assert.That(completedCourseIdsAttributes[3].IntValue.Value, Is.EqualTo(4));
        
        Assert.That(completedCourseIdsAttributes[4].IntValue.HasValue);
        Assert.That(completedCourseIdsAttributes[4].IntValue.Value, Is.EqualTo(5));
        
        Assert.Pass();
    }
    
    [Test]
    public async Task FullImportUpdateAddTextMvaTestAsync()
    {
        InitialiseConnectedSystemObjectsData();
        
        // mock up a connector that will return updates for our existing connected system objects above.
        // changes: PROXY_ADDRESSES has multiple values added for the first time.
        const string proxyAddress1 = "SMTP:jane.smith@phlebas.tetron.io";
        const string proxyAddress2 = "smtp:jane.wright@phlebas.tetron.io";
        const string proxyAddress3 = "smtp:cto@phlebas.tetron.io";
        var mockFileConnector = new MockFileConnector();
        mockFileConnector.TestImportObjects.Add(new ConnectedSystemImportObject
        {
            ChangeType = ObjectChangeType.Create,
            ObjectType = "User",
            Attributes = new List<ConnectedSystemImportObjectAttribute>()
            {
                new ()
                {
                    Name = MockAttributeName.HR_ID.ToString(),
                    GuidValues = new List<Guid> { TestConstants.CS_OBJECT_1_HR_ID },
                    Type = AttributeDataType.Guid
                },
                new ()
                {
                    Name = MockAttributeName.EMPLOYEE_ID.ToString(),
                    IntValues = new List<int> { 1 },
                    Type = AttributeDataType.Number
                },
                new ()
                {
                    Name = MockAttributeName.DISPLAY_NAME.ToString(),
                    StringValues = new List<string> { TestConstants.CS_OBJECT_1_DISPLAY_NAME },
                    Type = AttributeDataType.Text
                },
                new ()
                {
                    Name = MockAttributeName.PROXY_ADDRESSES.ToString(),
                    StringValues = new List<string>
                    {
                        proxyAddress1,
                        proxyAddress2,
                        proxyAddress3
                    },
                    Type = AttributeDataType.Text
                },
            }
        });
        
        // now execute Jim functionality we want to test...
        var connectedSystem = await Jim.ConnectedSystems.GetConnectedSystemAsync(1);
        Assert.That(connectedSystem, Is.Not.Null, "Expected to retrieve a Connected System.");

        var activity = ActivitiesData.First();
        var runProfile = ConnectedSystemRunProfilesData.Single(q => q.RunType == ConnectedSystemRunType.FullImport);
        var synchronisationImportTaskProcessor = new SyncImportTaskProcessor(Jim, mockFileConnector, connectedSystem, runProfile, InitiatedBy, activity, new CancellationTokenSource());
        await synchronisationImportTaskProcessor.PerformFullImportAsync();
        
        // confirm the results persisted to the mocked db context
        Assert.That(ConnectedSystemObjectsData, Has.Count.EqualTo(2), $"Expected two Connected System Objects to remain persisted. Found {ConnectedSystemObjectsData.Count}.");
        
        // get the Connected System Object for the user we changed some attribute values for in the mocked connector
        var cso1 = await Jim.ConnectedSystems.GetConnectedSystemObjectAsync(1, TestConstants.CS_OBJECT_1_ID);
        Assert.That(cso1, Is.Not.EqualTo(null), "Expected to be able to retrieve the first CSO to validate.");

        var proxyAddressesAttribute = cso1.GetAttributeValues(MockAttributeName.PROXY_ADDRESSES.ToString());
        Assert.That(proxyAddressesAttribute, Is.Not.Null);
        Assert.That(proxyAddressesAttribute.Count == 3);
        
        Assert.That(proxyAddressesAttribute[0].StringValue, Is.Not.Null);
        Assert.That(proxyAddressesAttribute[0].StringValue, Is.EqualTo(proxyAddress1));
        
        Assert.That(proxyAddressesAttribute[1].StringValue, Is.Not.Null);
        Assert.That(proxyAddressesAttribute[1].StringValue, Is.EqualTo(proxyAddress2));
        
        Assert.That(proxyAddressesAttribute[2].StringValue, Is.Not.Null);
        Assert.That(proxyAddressesAttribute[2].StringValue, Is.EqualTo(proxyAddress3));
        
        Assert.Pass();
    }
    
    [Test]
    public async Task FullImportUpdateAddGuidMvaTestAsync()
    {
        InitialiseConnectedSystemObjectsData();
        
        // mock up a connector that will return updates for our existing connected system objects above.
        // changes: PREVIOUS_LOCATION_IDS has multiple values added for the first time.
        var previousLocation1 = Guid.NewGuid();
        var previousLocation2 = Guid.NewGuid();
        var previousLocation3 = Guid.NewGuid();
        var mockFileConnector = new MockFileConnector();
        mockFileConnector.TestImportObjects.Add(new ConnectedSystemImportObject
        {
            ChangeType = ObjectChangeType.Create,
            ObjectType = "User",
            Attributes = new List<ConnectedSystemImportObjectAttribute>()
            {
                new ()
                {
                    Name = MockAttributeName.HR_ID.ToString(),
                    GuidValues = new List<Guid> { TestConstants.CS_OBJECT_1_HR_ID },
                    Type = AttributeDataType.Guid
                },
                new ()
                {
                    Name = MockAttributeName.EMPLOYEE_ID.ToString(),
                    IntValues = new List<int> { 1 },
                    Type = AttributeDataType.Number
                },
                new ()
                {
                    Name = MockAttributeName.DISPLAY_NAME.ToString(),
                    StringValues = new List<string> { TestConstants.CS_OBJECT_1_DISPLAY_NAME },
                    Type = AttributeDataType.Text
                },
                new ()
                {
                    Name = MockAttributeName.PREVIOUS_LOCATION_IDS.ToString(),
                    GuidValues = new List<Guid>
                    {
                        previousLocation1,
                        previousLocation2,
                        previousLocation3
                    },
                    Type = AttributeDataType.Guid
                },
            }
        });
        
        // now execute Jim functionality we want to test...
        var connectedSystem = await Jim.ConnectedSystems.GetConnectedSystemAsync(1);
        Assert.That(connectedSystem, Is.Not.Null, "Expected to retrieve a Connected System.");

        var activity = ActivitiesData.First();
        var runProfile = ConnectedSystemRunProfilesData.Single(q => q.RunType == ConnectedSystemRunType.FullImport);
        var synchronisationImportTaskProcessor = new SyncImportTaskProcessor(Jim, mockFileConnector, connectedSystem, runProfile, InitiatedBy, activity, new CancellationTokenSource());
        await synchronisationImportTaskProcessor.PerformFullImportAsync();
        
        // confirm the results persisted to the mocked db context
        Assert.That(ConnectedSystemObjectsData, Has.Count.EqualTo(2), $"Expected two Connected System Objects to remain persisted. Found {ConnectedSystemObjectsData.Count}.");
        
        // get the Connected System Object for the user we changed some attribute values for in the mocked connector
        var cso1 = await Jim.ConnectedSystems.GetConnectedSystemObjectAsync(1, TestConstants.CS_OBJECT_1_ID);
        Assert.That(cso1, Is.Not.EqualTo(null), "Expected to be able to retrieve the first CSO to validate.");

        var previousLocationIdsAttributes = cso1.GetAttributeValues(MockAttributeName.PREVIOUS_LOCATION_IDS.ToString());
        Assert.That(previousLocationIdsAttributes, Is.Not.Null);
        Assert.That(previousLocationIdsAttributes.Count == 3);
        
        Assert.That(previousLocationIdsAttributes[0].GuidValue, Is.Not.Null);
        Assert.That(previousLocationIdsAttributes[0].GuidValue, Is.EqualTo(previousLocation1));
        
        Assert.That(previousLocationIdsAttributes[1].GuidValue, Is.Not.Null);
        Assert.That(previousLocationIdsAttributes[1].GuidValue, Is.EqualTo(previousLocation2));
        
        Assert.That(previousLocationIdsAttributes[2].GuidValue, Is.Not.Null);
        Assert.That(previousLocationIdsAttributes[2].GuidValue, Is.EqualTo(previousLocation3));
        
        Assert.Pass();
    }
    
    [Test]
    public async Task FullImportUpdateAddByteMvaTestAsync()
    {
        InitialiseConnectedSystemObjectsData();
        
        // mock up a connector that will return updates for our existing connected system objects above.
        // changes: CERTIFICATES has multiple values added for the first time.
        var certificate1 = Convert.FromHexString(TestConstants.IMAGE_1_HEX);
        var certificate2 = Convert.FromHexString(TestConstants.IMAGE_2_HEX);
        var mockFileConnector = new MockFileConnector();
        mockFileConnector.TestImportObjects.Add(new ConnectedSystemImportObject
        {
            ChangeType = ObjectChangeType.Create,
            ObjectType = "User",
            Attributes = new List<ConnectedSystemImportObjectAttribute>()
            {
                new ()
                {
                    Name = MockAttributeName.HR_ID.ToString(),
                    GuidValues = new List<Guid> { TestConstants.CS_OBJECT_1_HR_ID },
                    Type = AttributeDataType.Guid
                },
                new ()
                {
                    Name = MockAttributeName.EMPLOYEE_ID.ToString(),
                    IntValues = new List<int> { 1 },
                    Type = AttributeDataType.Number
                },
                new ()
                {
                    Name = MockAttributeName.DISPLAY_NAME.ToString(),
                    StringValues = new List<string> { TestConstants.CS_OBJECT_1_DISPLAY_NAME },
                    Type = AttributeDataType.Text
                },
                new ()
                {
                    Name = MockAttributeName.CERTIFICATES.ToString(),
                    ByteValues = new List<byte[]>
                    {
                        certificate1,
                        certificate2,
                    },
                    Type = AttributeDataType.Binary
                },
            }
        });
        
        // now execute Jim functionality we want to test...
        var connectedSystem = await Jim.ConnectedSystems.GetConnectedSystemAsync(1);
        Assert.That(connectedSystem, Is.Not.Null, "Expected to retrieve a Connected System.");

        var activity = ActivitiesData.First();
        var runProfile = ConnectedSystemRunProfilesData.Single(q => q.RunType == ConnectedSystemRunType.FullImport);
        var synchronisationImportTaskProcessor = new SyncImportTaskProcessor(Jim, mockFileConnector, connectedSystem, runProfile, InitiatedBy, activity, new CancellationTokenSource());
        await synchronisationImportTaskProcessor.PerformFullImportAsync();
        
        // confirm the results persisted to the mocked db context
        Assert.That(ConnectedSystemObjectsData, Has.Count.EqualTo(2), $"Expected two Connected System Objects to remain persisted. Found {ConnectedSystemObjectsData.Count}.");
        
        // get the Connected System Object for the user we changed some attribute values for in the mocked connector
        var cso1 = await Jim.ConnectedSystems.GetConnectedSystemObjectAsync(1, TestConstants.CS_OBJECT_1_ID);
        Assert.That(cso1, Is.Not.EqualTo(null), "Expected to be able to retrieve the first CSO to validate.");

        var certificatesAttributes = cso1.GetAttributeValues(MockAttributeName.CERTIFICATES.ToString());
        Assert.That(certificatesAttributes, Is.Not.Null);
        Assert.That(certificatesAttributes.Count == 2);
        
        Assert.That(certificatesAttributes[0].ByteValue, Is.Not.Null);
        Assert.That(certificatesAttributes[0].ByteValue, Is.EqualTo(certificate1));
        
        Assert.That(certificatesAttributes[1].ByteValue, Is.Not.Null);
        Assert.That(certificatesAttributes[1].ByteValue, Is.EqualTo(certificate2));
        
        Assert.Pass();
    }

    [Test]
    public async Task FullImportAddReferenceMvaTestAsync()
    {
        InitialiseConnectedSystemObjectsData();

        // mock up a connector that will return updates for our existing connected system objects above.
        // changes: MEMBER has values added for the first time.
        var mockFileConnector = new MockFileConnector();
        mockFileConnector.TestImportObjects.Add(new ConnectedSystemImportObject
        {
            ChangeType = ObjectChangeType.Create,
            ObjectType = "Group",
            Attributes = new List<ConnectedSystemImportObjectAttribute>()
            {
                new()
                {
                    Name = MockAttributeName.GROUP_UID.ToString(),
                    GuidValues = new List<Guid> { TestConstants.CS_OBJECT_4_GROUP_UID },
                    Type = AttributeDataType.Guid
                },
                new()
                {
                    Name = MockAttributeName.DISPLAY_NAME.ToString(),
                    StringValues = new List<string> { TestConstants.CS_OBJECT_4_DISPLAY_NAME },
                    Type = AttributeDataType.Text
                },
                new()
                {
                    Name = MockAttributeName.MEMBER.ToString(),
                    ReferenceValues = new List<string>
                    {
                        TestConstants.CS_OBJECT_1_HR_ID.ToString(),
                        TestConstants.CS_OBJECT_2_HR_ID.ToString(),
                        TestConstants.CS_OBJECT_3_HR_ID.ToString()
                    },
                    Type = AttributeDataType.Reference
                }
            }
        });
        
        // now execute Jim functionality we want to test...
        var connectedSystem = await Jim.ConnectedSystems.GetConnectedSystemAsync(1);
        Assert.That(connectedSystem, Is.Not.Null, "Expected to retrieve a Connected System.");

        var activity = ActivitiesData.First();
        var runProfile = ConnectedSystemRunProfilesData.Single(q => q.RunType == ConnectedSystemRunType.FullImport);
        var synchronisationImportTaskProcessor = new SyncImportTaskProcessor(Jim, mockFileConnector, connectedSystem, runProfile, InitiatedBy, activity, new CancellationTokenSource());
        await synchronisationImportTaskProcessor.PerformFullImportAsync();
        
        // confirm the results persisted to the mocked db context
        Assert.That(ConnectedSystemObjectsData, Has.Count.EqualTo(4), $"Expected four Connected System Objects to remain persisted. Found {ConnectedSystemObjectsData.Count}.");
        
        // get the group cso
        var cso4 = await Jim.ConnectedSystems.GetConnectedSystemObjectByAttributeAsync(1, (int)MockAttributeName.GROUP_UID, TestConstants.CS_OBJECT_4_GROUP_UID);
        Assert.That(cso4, Is.Not.EqualTo(null), "Expected to be able to retrieve the group (cso4).");
        var cso4MemberAttributes = cso4.GetAttributeValues(MockAttributeName.MEMBER.ToString());
        Assert.That(cso4MemberAttributes, Is.Not.Null);
        Assert.That(cso4MemberAttributes.Count, Is.EqualTo(3), $"Expected the group to have 3 members. It had {cso4MemberAttributes.Count}");
        
        Assert.That(cso4MemberAttributes[0].ReferenceValue, Is.Not.EqualTo(null));
        Assert.That(cso4MemberAttributes[0].ReferenceValue.Id, Is.EqualTo(TestConstants.CS_OBJECT_1_ID));
        Assert.That(!string.IsNullOrEmpty(cso4MemberAttributes[0].UnresolvedReferenceValue));
        Assert.That(cso4MemberAttributes[0].UnresolvedReferenceValue, Is.EqualTo(TestConstants.CS_OBJECT_1_HR_ID.ToString()));
        
        Assert.That(cso4MemberAttributes[1].ReferenceValue, Is.Not.EqualTo(null));
        Assert.That(cso4MemberAttributes[1].ReferenceValue.Id, Is.EqualTo(TestConstants.CS_OBJECT_2_ID));
        Assert.That(!string.IsNullOrEmpty(cso4MemberAttributes[1].UnresolvedReferenceValue));
        Assert.That(cso4MemberAttributes[1].UnresolvedReferenceValue, Is.EqualTo(TestConstants.CS_OBJECT_2_HR_ID.ToString()));
        
        Assert.That(cso4MemberAttributes[2].ReferenceValue, Is.Not.EqualTo(null));
        Assert.That(cso4MemberAttributes[2].ReferenceValue.Id, Is.EqualTo(TestConstants.CS_OBJECT_3_ID));
        Assert.That(!string.IsNullOrEmpty(cso4MemberAttributes[2].UnresolvedReferenceValue));
        Assert.That(cso4MemberAttributes[2].UnresolvedReferenceValue, Is.EqualTo(TestConstants.CS_OBJECT_3_HR_ID.ToString()));
        
        Assert.Pass();
    }
    
    [Test]
    public async Task FullImportRemoveIntMvaTestAsync()
    {
        InitialiseConnectedSystemObjectsData();
        
        // mock up a connector that will return updates for our existing connected system objects above.
        // changes: CONTRACTED_WEEKLY_HOURS has been removed.
        var mockFileConnector = new MockFileConnector();
        mockFileConnector.TestImportObjects.Add(new ConnectedSystemImportObject
        {
            ChangeType = ObjectChangeType.Create,
            ObjectType = "User",
            Attributes = new List<ConnectedSystemImportObjectAttribute>()
            {
                new ()
                {
                    Name = MockAttributeName.EMPLOYEE_ID.ToString(),
                    IntValues = new List<int> { 1 },
                    Type = AttributeDataType.Number
                },
                new ()
                {
                    Name = MockAttributeName.HR_ID.ToString(),
                    GuidValues = new List<Guid> { TestConstants.CS_OBJECT_1_HR_ID },
                    Type = AttributeDataType.Guid
                },
                new ()
                {
                    Name = MockAttributeName.START_DATE.ToString(),
                    DateTimeValue = TestConstants.CS_OBJECT_1_START_DATE,
                    Type = AttributeDataType.DateTime
                },
                new ()
                {
                    Name = MockAttributeName.DISPLAY_NAME.ToString(),
                    StringValues = new List<string> { TestConstants.CS_OBJECT_1_DISPLAY_NAME },
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
                },
                new ()
                {
                    Name = MockAttributeName.PROFILE_PICTURE_BYTES.ToString(),
                    ByteValues = new List<byte[]> { Convert.FromHexString(TestConstants.IMAGE_1_HEX) },
                    Type = AttributeDataType.Binary
                },
                new ()
                {
                    Name = MockAttributeName.LOCATION_ID.ToString(),
                    GuidValues = new List<Guid> { TestConstants.LOCATION_1_ID },
                    Type = AttributeDataType.Guid
                },
                new ()
                {
                    Name = MockAttributeName.LEAVER.ToString(),
                    BoolValue = false,
                    Type = AttributeDataType.Boolean
                }
            }
        });
        
        // now execute Jim functionality we want to test...
        var connectedSystem = await Jim.ConnectedSystems.GetConnectedSystemAsync(1);
        Assert.That(connectedSystem, Is.Not.Null, "Expected to retrieve a Connected System.");

        var activity = ActivitiesData.First();
        var runProfile = ConnectedSystemRunProfilesData.Single(q => q.RunType == ConnectedSystemRunType.FullImport);
        var synchronisationImportTaskProcessor = new SyncImportTaskProcessor(Jim, mockFileConnector, connectedSystem, runProfile, InitiatedBy, activity, new CancellationTokenSource());
        await synchronisationImportTaskProcessor.PerformFullImportAsync();
        
        // confirm the results persisted to the mocked db context
        Assert.That(ConnectedSystemObjectsData, Has.Count.EqualTo(2), $"Expected two Connected System Objects to remain persisted. Found {ConnectedSystemObjectsData.Count}.");
        
        // get the Connected System Object for the user we changed some attribute values for in the mocked connector
        var cso1 = await Jim.ConnectedSystems.GetConnectedSystemObjectAsync(1, TestConstants.CS_OBJECT_1_ID);
        Assert.That(cso1, Is.Not.EqualTo(null), "Expected to be able to retrieve the first CSO to validate.");

        var hoursAttribute = cso1.GetAttributeValue(MockAttributeName.CONTRACTED_WEEKLY_HOURS.ToString());
        Assert.That(hoursAttribute, Is.Null);
        
        Assert.Pass();
    }
    
    [Test]
    public async Task FullImportRemoveTextMvaTestAsync()
    {
        InitialiseConnectedSystemObjectsData();
        
        // mock up a connector that will return updates for our existing connected system objects above.
        // changes: ROLE has been removed.
        var mockFileConnector = new MockFileConnector();
        mockFileConnector.TestImportObjects.Add(new ConnectedSystemImportObject
        {
            ChangeType = ObjectChangeType.Create,
            ObjectType = "User",
            Attributes = new List<ConnectedSystemImportObjectAttribute>()
            {
                new ()
                {
                    Name = MockAttributeName.EMPLOYEE_ID.ToString(),
                    IntValues = new List<int> { 1 },
                    Type = AttributeDataType.Number
                },
                new ()
                {
                    Name = MockAttributeName.HR_ID.ToString(),
                    GuidValues = new List<Guid> { TestConstants.CS_OBJECT_1_HR_ID },
                    Type = AttributeDataType.Guid
                },
                new ()
                {
                    Name = MockAttributeName.START_DATE.ToString(),
                    DateTimeValue = TestConstants.CS_OBJECT_1_START_DATE,
                    Type = AttributeDataType.DateTime
                },
                new ()
                {
                    Name = MockAttributeName.DISPLAY_NAME.ToString(),
                    StringValues = new List<string> { TestConstants.CS_OBJECT_1_DISPLAY_NAME },
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
                    Name = MockAttributeName.PROFILE_PICTURE_BYTES.ToString(),
                    ByteValues = new List<byte[]> { Convert.FromHexString(TestConstants.IMAGE_1_HEX) },
                    Type = AttributeDataType.Binary
                },
                new ()
                {
                    Name = MockAttributeName.CONTRACTED_WEEKLY_HOURS.ToString(),
                    IntValues = new List<int> { 40 },
                    Type = AttributeDataType.Number
                },
                new ()
                {
                    Name = MockAttributeName.LOCATION_ID.ToString(),
                    GuidValues = new List<Guid> { TestConstants.LOCATION_1_ID },
                    Type = AttributeDataType.Guid
                },
                new ()
                {
                    Name = MockAttributeName.LEAVER.ToString(),
                    BoolValue = false,
                    Type = AttributeDataType.Boolean
                }
            }
        });
        
        // now execute Jim functionality we want to test...
        var connectedSystem = await Jim.ConnectedSystems.GetConnectedSystemAsync(1);
        Assert.That(connectedSystem, Is.Not.Null, "Expected to retrieve a Connected System.");

        var activity = ActivitiesData.First();
        var runProfile = ConnectedSystemRunProfilesData.Single(q => q.RunType == ConnectedSystemRunType.FullImport);
        var synchronisationImportTaskProcessor = new SyncImportTaskProcessor(Jim, mockFileConnector, connectedSystem, runProfile, InitiatedBy, activity, new CancellationTokenSource());
        await synchronisationImportTaskProcessor.PerformFullImportAsync();
        
        // confirm the results persisted to the mocked db context
        Assert.That(ConnectedSystemObjectsData, Has.Count.EqualTo(2), $"Expected two Connected System Objects to remain persisted. Found {ConnectedSystemObjectsData.Count}.");
        
        // get the Connected System Object for the user we changed some attribute values for in the mocked connector
        var cso1ToValidate = await Jim.ConnectedSystems.GetConnectedSystemObjectAsync(1, TestConstants.CS_OBJECT_1_ID);
        Assert.That(cso1ToValidate, Is.Not.EqualTo(null), "Expected to be able to retrieve the first CSO to validate.");

        var roleAttribute = cso1ToValidate.GetAttributeValue(MockAttributeName.ROLE.ToString());
        Assert.That(roleAttribute, Is.Null);
        
        Assert.Pass();
    }
    
    [Test]
    public async Task FullImportRemoveGuidMvaTestAsync()
    {
        InitialiseConnectedSystemObjectsData();
        
        // mock up a connector that will return updates for our existing connected system objects above.
        // changes: LOCATION_ID has been removed.
        var mockFileConnector = new MockFileConnector();
        mockFileConnector.TestImportObjects.Add(new ConnectedSystemImportObject
        {
            ChangeType = ObjectChangeType.Create,
            ObjectType = "User",
            Attributes = new List<ConnectedSystemImportObjectAttribute>()
            {
                new ()
                {
                    Name = MockAttributeName.EMPLOYEE_ID.ToString(),
                    IntValues = new List<int> { 1 },
                    Type = AttributeDataType.Number
                },
                new ()
                {
                    Name = MockAttributeName.HR_ID.ToString(),
                    GuidValues = new List<Guid> { TestConstants.CS_OBJECT_1_HR_ID },
                    Type = AttributeDataType.Guid
                },
                new ()
                {
                    Name = MockAttributeName.START_DATE.ToString(),
                    DateTimeValue = TestConstants.CS_OBJECT_1_START_DATE,
                    Type = AttributeDataType.DateTime
                },
                new ()
                {
                    Name = MockAttributeName.DISPLAY_NAME.ToString(),
                    StringValues = new List<string> { TestConstants.CS_OBJECT_1_DISPLAY_NAME },
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
                },
                new ()
                {
                    Name = MockAttributeName.PROFILE_PICTURE_BYTES.ToString(),
                    ByteValues = new List<byte[]> { Convert.FromHexString(TestConstants.IMAGE_1_HEX) },
                    Type = AttributeDataType.Binary
                },
                new ()
                {
                    Name = MockAttributeName.CONTRACTED_WEEKLY_HOURS.ToString(),
                    IntValues = new List<int> { 40 },
                    Type = AttributeDataType.Number
                },
                new ()
                {
                    Name = MockAttributeName.LEAVER.ToString(),
                    BoolValue = false,
                    Type = AttributeDataType.Boolean
                }
            }
        });
        
        // now execute Jim functionality we want to test...
        var connectedSystem = await Jim.ConnectedSystems.GetConnectedSystemAsync(1);
        Assert.That(connectedSystem, Is.Not.Null, "Expected to retrieve a Connected System.");

        var activity = ActivitiesData.First();
        var runProfile = ConnectedSystemRunProfilesData.Single(q => q.RunType == ConnectedSystemRunType.FullImport);
        var synchronisationImportTaskProcessor = new SyncImportTaskProcessor(Jim, mockFileConnector, connectedSystem, runProfile, InitiatedBy, activity, new CancellationTokenSource());
        await synchronisationImportTaskProcessor.PerformFullImportAsync();
        
        // confirm the results persisted to the mocked db context
        Assert.That(ConnectedSystemObjectsData, Has.Count.EqualTo(2), $"Expected two Connected System Objects to remain persisted. Found {ConnectedSystemObjectsData.Count}.");
        
        // get the Connected System Object for the user we changed some attribute values for in the mocked connector
        var cso1 = await Jim.ConnectedSystems.GetConnectedSystemObjectAsync(1, TestConstants.CS_OBJECT_1_ID);
        Assert.That(cso1, Is.Not.EqualTo(null), "Expected to be able to retrieve the first CSO to validate.");

        var locationIdAttribute = cso1.GetAttributeValue(MockAttributeName.LOCATION_ID.ToString());
        Assert.That(locationIdAttribute, Is.Null);
        
        Assert.Pass();
    }
    
    [Test]
    public async Task FullImportRemoveByteMvaTestAsync()
    {
        InitialiseConnectedSystemObjectsData();
        
        // mock up a connector that will return updates for our existing connected system objects above.
        // changes: PROFILE_PICTURE_BYTES has been removed.
        var mockFileConnector = new MockFileConnector();
        mockFileConnector.TestImportObjects.Add(new ConnectedSystemImportObject
        {
            ChangeType = ObjectChangeType.Create,
            ObjectType = "User",
            Attributes = new List<ConnectedSystemImportObjectAttribute>()
            {
                new ()
                {
                    Name = MockAttributeName.EMPLOYEE_ID.ToString(),
                    IntValues = new List<int> { 1 },
                    Type = AttributeDataType.Number
                },
                new ()
                {
                    Name = MockAttributeName.HR_ID.ToString(),
                    GuidValues = new List<Guid> { TestConstants.CS_OBJECT_1_HR_ID },
                    Type = AttributeDataType.Guid
                },
                new ()
                {
                    Name = MockAttributeName.START_DATE.ToString(),
                    DateTimeValue = TestConstants.CS_OBJECT_1_START_DATE,
                    Type = AttributeDataType.DateTime
                },
                new ()
                {
                    Name = MockAttributeName.DISPLAY_NAME.ToString(),
                    StringValues = new List<string> { TestConstants.CS_OBJECT_1_DISPLAY_NAME },
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
                },
                new ()
                {
                    Name = MockAttributeName.CONTRACTED_WEEKLY_HOURS.ToString(),
                    IntValues = new List<int> { 40 },
                    Type = AttributeDataType.Number
                },
                new ()
                {
                    Name = MockAttributeName.LOCATION_ID.ToString(),
                    GuidValues = new List<Guid> { TestConstants.LOCATION_1_ID },
                    Type = AttributeDataType.Guid
                },
                new ()
                {
                    Name = MockAttributeName.LEAVER.ToString(),
                    BoolValue = false,
                    Type = AttributeDataType.Boolean
                }
            }
        });
        
        // now execute Jim functionality we want to test...
        var connectedSystem = await Jim.ConnectedSystems.GetConnectedSystemAsync(1);
        Assert.That(connectedSystem, Is.Not.Null, "Expected to retrieve a Connected System.");

        var activity = ActivitiesData.First();
        var runProfile = ConnectedSystemRunProfilesData.Single(q => q.RunType == ConnectedSystemRunType.FullImport);
        var synchronisationImportTaskProcessor = new SyncImportTaskProcessor(Jim, mockFileConnector, connectedSystem, runProfile, InitiatedBy, activity, new CancellationTokenSource());
        await synchronisationImportTaskProcessor.PerformFullImportAsync();
        
        // confirm the results persisted to the mocked db context
        Assert.That(ConnectedSystemObjectsData, Has.Count.EqualTo(2), $"Expected two Connected System Objects to remain persisted. Found {ConnectedSystemObjectsData.Count}.");
        
        // get the Connected System Object for the user we changed some attribute values for in the mocked connector
        var cso1 = await Jim.ConnectedSystems.GetConnectedSystemObjectAsync(1, TestConstants.CS_OBJECT_1_ID);
        Assert.That(cso1, Is.Not.EqualTo(null), "Expected to be able to retrieve the first CSO to validate.");

        var profilePictureAttribute = cso1.GetAttributeValue(MockAttributeName.PROFILE_PICTURE_BYTES.ToString());
        Assert.That(profilePictureAttribute, Is.Null);
        
        Assert.Pass();
    }
    
    [Test]
    public async Task FullImportRemoveDateTimeMvaTestAsync()
    {
        InitialiseConnectedSystemObjectsData();
        
        // mock up a connector that will return updates for our existing connected system objects above.
        // changes: START_DATE has been removed.
        var mockFileConnector = new MockFileConnector();
        mockFileConnector.TestImportObjects.Add(new ConnectedSystemImportObject
        {
            ChangeType = ObjectChangeType.Create,
            ObjectType = "User",
            Attributes = new List<ConnectedSystemImportObjectAttribute>()
            {
                new ()
                {
                    Name = MockAttributeName.EMPLOYEE_ID.ToString(),
                    IntValues = new List<int> { 1 },
                    Type = AttributeDataType.Number
                },
                new ()
                {
                    Name = MockAttributeName.HR_ID.ToString(),
                    GuidValues = new List<Guid> { TestConstants.CS_OBJECT_1_HR_ID },
                    Type = AttributeDataType.Guid
                },
                new ()
                {
                    Name = MockAttributeName.DISPLAY_NAME.ToString(),
                    StringValues = new List<string> { TestConstants.CS_OBJECT_1_DISPLAY_NAME },
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
                },
                new ()
                {
                    Name = MockAttributeName.PROFILE_PICTURE_BYTES.ToString(),
                    ByteValues = new List<byte[]> { Convert.FromHexString(TestConstants.IMAGE_1_HEX) },
                    Type = AttributeDataType.Binary
                },
                new ()
                {
                    Name = MockAttributeName.CONTRACTED_WEEKLY_HOURS.ToString(),
                    IntValues = new List<int> { 40 },
                    Type = AttributeDataType.Number
                },
                new ()
                {
                    Name = MockAttributeName.LOCATION_ID.ToString(),
                    GuidValues = new List<Guid> { TestConstants.LOCATION_1_ID },
                    Type = AttributeDataType.Guid
                },
                new ()
                {
                    Name = MockAttributeName.LEAVER.ToString(),
                    BoolValue = false,
                    Type = AttributeDataType.Boolean
                }
            }
        });
        
        // now execute Jim functionality we want to test...
        var connectedSystem = await Jim.ConnectedSystems.GetConnectedSystemAsync(1);
        Assert.That(connectedSystem, Is.Not.Null, "Expected to retrieve a Connected System.");

        var activity = ActivitiesData.First();
        var runProfile = ConnectedSystemRunProfilesData.Single(q => q.RunType == ConnectedSystemRunType.FullImport);
        var synchronisationImportTaskProcessor = new SyncImportTaskProcessor(Jim, mockFileConnector, connectedSystem, runProfile, InitiatedBy, activity, new CancellationTokenSource());
        await synchronisationImportTaskProcessor.PerformFullImportAsync();
        
        // confirm the results persisted to the mocked db context
        Assert.That(ConnectedSystemObjectsData, Has.Count.EqualTo(2), $"Expected two Connected System Objects to remain persisted. Found {ConnectedSystemObjectsData.Count}.");
        
        // get the Connected System Object for the user we changed some attribute values for in the mocked connector
        var cso1 = await Jim.ConnectedSystems.GetConnectedSystemObjectAsync(1, TestConstants.CS_OBJECT_1_ID);
        Assert.That(cso1, Is.Not.EqualTo(null), "Expected to be able to retrieve the first CSO to validate.");

        var startDateAttribute = cso1.GetAttributeValue(MockAttributeName.START_DATE.ToString());
        Assert.That(startDateAttribute, Is.Null);
        
        Assert.Pass();
    }
    
    [Test]
    public async Task FullImportRemoveReferenceMvaTestAsync()
    {
        InitialiseConnectedSystemObjectsData();

        // mock up a connector that will return updates for our existing connected system objects above.
        // changes: MANAGER has been removed.
        var mockFileConnector = new MockFileConnector();
        
        // update our second user by removing their manager reference.
        mockFileConnector.TestImportObjects.Add(new ConnectedSystemImportObject
        {
            ChangeType = ObjectChangeType.Create,
            ObjectType = "User",
            Attributes = new List<ConnectedSystemImportObjectAttribute>()
            {
                new()
                {
                    Name = MockAttributeName.EMPLOYEE_ID.ToString(),
                    IntValues = new List<int> { 2 },
                    Type = AttributeDataType.Number
                },
                new()
                {
                    Name = MockAttributeName.HR_ID.ToString(),
                    GuidValues = new List<Guid> { TestConstants.CS_OBJECT_2_HR_ID },
                    Type = AttributeDataType.Guid
                },
                new()
                {
                    Name = MockAttributeName.START_DATE.ToString(),
                    DateTimeValue = TestConstants.CS_OBJECT_2_START_DATE,
                    Type = AttributeDataType.DateTime
                },
                new()
                {
                    Name = MockAttributeName.DISPLAY_NAME.ToString(),
                    StringValues = new List<string> { TestConstants.CS_OBJECT_2_DISPLAY_NAME },
                    Type = AttributeDataType.Text
                },
                new()
                {
                    Name = MockAttributeName.EMAIL_ADDRESS.ToString(),
                    StringValues = new List<string> { "joe.bloggs@phlebas.tetron.io" },
                    Type = AttributeDataType.Text
                },
                new()
                {
                    Name = MockAttributeName.ROLE.ToString(),
                    StringValues = new List<string> { "Developer" },
                    Type = AttributeDataType.Text
                },
                new()
                {
                    Name = MockAttributeName.CONTRACTED_WEEKLY_HOURS.ToString(),
                    IntValues = new List<int> { 40 },
                    Type = AttributeDataType.Number
                },
                new()
                {
                    Name = MockAttributeName.LOCATION_ID.ToString(),
                    GuidValues = new List<Guid> { TestConstants.LOCATION_1_ID },
                    Type = AttributeDataType.Guid
                },
                new()
                {
                    Name = MockAttributeName.END_DATE.ToString(),
                    DateTimeValue = TestConstants.CS_OBJECT_2_END_DATE_1,
                    Type = AttributeDataType.DateTime
                },
                new()
                {
                    Name = MockAttributeName.LEAVER.ToString(),
                    BoolValue = false,
                    Type = AttributeDataType.Boolean
                }
            }
        });
        
        // now execute Jim functionality we want to test...
        var connectedSystem = await Jim.ConnectedSystems.GetConnectedSystemAsync(1);
        Assert.That(connectedSystem, Is.Not.Null, "Expected to retrieve a Connected System.");

        var activity = ActivitiesData.First();
        var runProfile = ConnectedSystemRunProfilesData.Single(q => q.RunType == ConnectedSystemRunType.FullImport);
        var synchronisationImportTaskProcessor = new SyncImportTaskProcessor(Jim, mockFileConnector, connectedSystem, runProfile, InitiatedBy, activity, new CancellationTokenSource());
        await synchronisationImportTaskProcessor.PerformFullImportAsync();
        
        // confirm the results persisted to the mocked db context
        Assert.That(ConnectedSystemObjectsData, Has.Count.EqualTo(2), $"Expected two Connected System Objects to remain persisted. Found {ConnectedSystemObjectsData.Count}.");
        
        // get the Connected System Object for the user we changed some attribute values for in the mocked connector
        var cso2 = await Jim.ConnectedSystems.GetConnectedSystemObjectAsync(1, TestConstants.CS_OBJECT_2_ID);
        Assert.That(cso2, Is.Not.EqualTo(null), "Expected to be able to retrieve the first CSO to validate.");

        var cso2ManagerAttribute = cso2.GetAttributeValue(MockAttributeName.MANAGER.ToString());
        Assert.That(cso2ManagerAttribute, Is.Null);
        
        Assert.Pass();
    }
    
    // todo: test activity/run profile execution item/change object creation
    
    // mva:
    // todo: add/remove int
    // todo: add/remove datetime
    // todo: add/remove text
    // todo: add/remove guid
    // todo: add/remove reference
    
    #region private methods
    private void InitialiseConnectedSystemObjectsData()
    {
        ConnectedSystemObjectsData.Clear();
        
        // set the start-state for the tests; create the Connected System Objects we'll alter in the tests
        var userObjectType = ConnectedSystemObjectTypesData.Single(q => q.Name.Equals("User", StringComparison.InvariantCultureIgnoreCase));
        var groupObjectType = ConnectedSystemObjectTypesData.Single(q => q.Name.Equals("Group", StringComparison.InvariantCultureIgnoreCase));
        
        // user 1
        var cso1 = new ConnectedSystemObject
        {
            Id = TestConstants.CS_OBJECT_1_ID,
            ConnectedSystemId = 1,
            ConnectedSystem = ConnectedSystemsData.First(),
            Type = userObjectType,
            ExternalIdAttributeId = (int)MockAttributeName.HR_ID
        };
        cso1.AttributeValues = new List<ConnectedSystemObjectAttributeValue>
        {
            new()
            {
                Id = Guid.NewGuid(),
                GuidValue = TestConstants.CS_OBJECT_1_HR_ID,
                Attribute = userObjectType.Attributes.Single(q => q.Name == MockAttributeName.HR_ID.ToString()),
                ConnectedSystemObject = cso1
            },
            new()
            {
                Id = Guid.NewGuid(),
                IntValue = 1,
                Attribute = userObjectType.Attributes.Single(q => q.Name == MockAttributeName.EMPLOYEE_ID.ToString()),
                ConnectedSystemObject = cso1
            },
            new()
            {
                Id = Guid.NewGuid(),
                StringValue = TestConstants.CS_OBJECT_1_DISPLAY_NAME,
                Attribute = userObjectType.Attributes.Single(q => q.Name == MockAttributeName.DISPLAY_NAME.ToString()),
                ConnectedSystemObject = cso1
            },
            new()
            {
                Id = Guid.NewGuid(),
                StringValue = "jane.smith@phlebas.tetron.io",
                Attribute = userObjectType.Attributes.Single(q => q.Name == MockAttributeName.EMAIL_ADDRESS.ToString()),
                ConnectedSystemObject = cso1
            }
        };
        ConnectedSystemObjectsData.Add(cso1);
        
        // user 2
        var cso2 = new ConnectedSystemObject
        {
            Id = TestConstants.CS_OBJECT_2_ID,
            ConnectedSystemId = 1,
            ConnectedSystem = ConnectedSystemsData.First(),
            Type = userObjectType,
            ExternalIdAttributeId = (int)MockAttributeName.HR_ID
        };
        cso2.AttributeValues = new List<ConnectedSystemObjectAttributeValue>
        {
            new()
            {
                Id = Guid.NewGuid(),
                GuidValue = TestConstants.CS_OBJECT_2_HR_ID,
                Attribute = userObjectType.Attributes.Single(q => q.Name == MockAttributeName.HR_ID.ToString()),
                ConnectedSystemObject = cso2
            },
            new()
            {
                Id = Guid.NewGuid(),
                IntValue = 2,
                Attribute = userObjectType.Attributes.Single(q => q.Name == MockAttributeName.EMPLOYEE_ID.ToString()),
                ConnectedSystemObject = cso2
            },
            new()
            {
                Id = Guid.NewGuid(),
                StringValue = TestConstants.CS_OBJECT_2_DISPLAY_NAME,
                Attribute = userObjectType.Attributes.Single(q => q.Name == MockAttributeName.DISPLAY_NAME.ToString()),
                ConnectedSystemObject = cso2
            },
            new()
            {
                Id = Guid.NewGuid(),
                StringValue = "joe.bloggs@phlebas.tetron.io",
                Attribute = userObjectType.Attributes.Single(q => q.Name == MockAttributeName.EMAIL_ADDRESS.ToString()),
                ConnectedSystemObject = cso2
            }
        };
        ConnectedSystemObjectsData.Add(cso2);
        
        // user 3
        var cso3 = new ConnectedSystemObject
        {
            Id = TestConstants.CS_OBJECT_3_ID,
            ConnectedSystemId = 1,
            ConnectedSystem = ConnectedSystemsData.First(),
            Type = userObjectType,
            ExternalIdAttributeId = (int)MockAttributeName.HR_ID
        };
        cso3.AttributeValues = new List<ConnectedSystemObjectAttributeValue>
        {
            new()
            {
                Id = Guid.NewGuid(),
                GuidValue = TestConstants.CS_OBJECT_3_HR_ID,
                Attribute = userObjectType.Attributes.Single(q => q.Name == MockAttributeName.HR_ID.ToString()),
                ConnectedSystemObject = cso3
            },
            new()
            {
                Id = Guid.NewGuid(),
                IntValue = 3,
                Attribute = userObjectType.Attributes.Single(q => q.Name == MockAttributeName.EMPLOYEE_ID.ToString()),
                ConnectedSystemObject = cso3
            },
            new()
            {
                Id = Guid.NewGuid(),
                StringValue = TestConstants.CS_OBJECT_3_DISPLAY_NAME,
                Attribute = userObjectType.Attributes.Single(q => q.Name == MockAttributeName.DISPLAY_NAME.ToString()),
                ConnectedSystemObject = cso3
            },
            new()
            {
                Id = Guid.NewGuid(),
                StringValue = TestConstants.CS_OBJECT_3_EMAIL,
                Attribute = userObjectType.Attributes.Single(q => q.Name == MockAttributeName.EMAIL_ADDRESS.ToString()),
                ConnectedSystemObject = cso3
            }
        };
        ConnectedSystemObjectsData.Add(cso3);
        
        // group
        var cso4 = new ConnectedSystemObject
        {
            Id = TestConstants.CS_OBJECT_4_ID,
            ConnectedSystemId = 1,
            ConnectedSystem = ConnectedSystemsData.First(),
            Type = groupObjectType,
            ExternalIdAttributeId = (int)MockAttributeName.GROUP_UID
        };
        cso4.AttributeValues = new List<ConnectedSystemObjectAttributeValue>
        {
            new()
            {
                Id = Guid.NewGuid(),
                GuidValue = TestConstants.CS_OBJECT_4_GROUP_UID,
                Attribute = groupObjectType.Attributes.Single(q => q.Name == MockAttributeName.GROUP_UID.ToString()),
                ConnectedSystemObject = cso4
            },
            new()
            {
                Id = Guid.NewGuid(),
                StringValue = TestConstants.CS_OBJECT_4_DISPLAY_NAME,
                Attribute = groupObjectType.Attributes.Single(q => q.Name == MockAttributeName.DISPLAY_NAME.ToString()),
                ConnectedSystemObject = cso4
            }
        };
        ConnectedSystemObjectsData.Add(cso4);
    }
    #endregion
}