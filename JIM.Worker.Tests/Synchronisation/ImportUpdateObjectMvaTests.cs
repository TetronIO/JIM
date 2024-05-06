﻿using JIM.Application;
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
        Assert.That(ConnectedSystemObjectsData, Has.Count.EqualTo(4), $"Expected four Connected System Objects to remain persisted. Found {ConnectedSystemObjectsData.Count}.");
        
        // get the Connected System Object for the user we changed some attribute values for in the mocked connector
        var cso1 = await Jim.ConnectedSystems.GetConnectedSystemObjectAsync(1, TestConstants.CS_OBJECT_1_ID);
        Assert.That(cso1, Is.Not.EqualTo(null), "Expected to be able to retrieve the first CSO to validate.");

        var proxyAddressesAttribute = cso1.GetAttributeValues(MockAttributeName.PROXY_ADDRESSES.ToString());
        Assert.That(proxyAddressesAttribute, Is.Not.Null);
        Assert.That(proxyAddressesAttribute.Count, Is.EqualTo(3));
        
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
        Assert.That(ConnectedSystemObjectsData, Has.Count.EqualTo(4), $"Expected four Connected System Objects to remain persisted. Found {ConnectedSystemObjectsData.Count}.");
        
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
        Assert.That(ConnectedSystemObjectsData, Has.Count.EqualTo(4), $"Expected four Connected System Objects to remain persisted. Found {ConnectedSystemObjectsData.Count}.");
        
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
    public async Task FullImportUpdateAddReferenceMvaTestAsync()
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
    
    // todo: create a test for when the object ids are not system-unique, but object-type unique, we have two types and two objects with the same external id value
    // and reference one of them in a group. i.e. overlapping external id values, but differentiated by object type. Expecting this to fail.
    // i.e.
    // group1.externalid = 1
    // group1.member.unresolvedreference = 1
    // user1.externalid = 1
    // will the group member resolve to the group, or the user? it could be either. how do we know what object type is being referenced?
    // not an issue whilst all ids in use on objects and in references are system-unique.
    
    [Test]
    public async Task FullImportUpdateRemoveIntMvaTestAsync()
    {
        InitialiseConnectedSystemObjectsData();
        
        // mock up a connector that will return updates for our existing connected system objects above.
        // changes: MEMBER has values removed.
        
        // add COMPLETED_COURSE_IDS to our CSO first
        var userObjectType = ConnectedSystemObjectTypesData.Single(q => q.Name.Equals("User", StringComparison.InvariantCultureIgnoreCase));
        var cso1ToSetup = ConnectedSystemObjectsData.Single(q => q.Id == TestConstants.CS_OBJECT_1_ID);
        cso1ToSetup.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            IntValue = 1,
            Attribute = userObjectType.Attributes.Single(q => q.Name == MockAttributeName.COMPLETED_COURSE_IDS.ToString()),
            ConnectedSystemObject = cso1ToSetup
        });
        cso1ToSetup.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            IntValue = 2,
            Attribute = userObjectType.Attributes.Single(q => q.Name == MockAttributeName.COMPLETED_COURSE_IDS.ToString()),
            ConnectedSystemObject = cso1ToSetup
        });
        cso1ToSetup.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            IntValue = 3,
            Attribute = userObjectType.Attributes.Single(q => q.Name == MockAttributeName.COMPLETED_COURSE_IDS.ToString()),
            ConnectedSystemObject = cso1ToSetup
        });
        cso1ToSetup.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            IntValue = 4,
            Attribute = userObjectType.Attributes.Single(q => q.Name == MockAttributeName.COMPLETED_COURSE_IDS.ToString()),
            ConnectedSystemObject = cso1ToSetup
        });
        cso1ToSetup.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            IntValue = 5,
            Attribute = userObjectType.Attributes.Single(q => q.Name == MockAttributeName.COMPLETED_COURSE_IDS.ToString()),
            ConnectedSystemObject = cso1ToSetup
        });
        
        // now build the import object, lacking some of the COMPLETED_COURSE_IDS
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
                    IntValues = new List<int> { 1,2,3 },
                    Type = AttributeDataType.Number
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
        
        // get the Connected System Object for the user we changed some attribute values for in the mocked connector
        var cso1 = await Jim.ConnectedSystems.GetConnectedSystemObjectAsync(1, TestConstants.CS_OBJECT_1_ID);
        Assert.That(cso1, Is.Not.EqualTo(null), "Expected to be able to retrieve the first CSO to validate.");

        var completedCourseIdsAttributes = cso1.GetAttributeValues(MockAttributeName.COMPLETED_COURSE_IDS.ToString());
        Assert.That(completedCourseIdsAttributes, Is.Not.Null);
        Assert.That(completedCourseIdsAttributes, Has.Count.EqualTo(3));
        
        Assert.That(completedCourseIdsAttributes[0].IntValue.HasValue);
        Assert.That(completedCourseIdsAttributes[0].IntValue.Value, Is.EqualTo(1));
        
        Assert.That(completedCourseIdsAttributes[1].IntValue.HasValue);
        Assert.That(completedCourseIdsAttributes[1].IntValue.Value, Is.EqualTo(2));
        
        Assert.That(completedCourseIdsAttributes[2].IntValue.HasValue);
        Assert.That(completedCourseIdsAttributes[2].IntValue.Value, Is.EqualTo(3));
        
        Assert.Pass();
    }
    
    [Test]
    public async Task FullImportUpdateRemoveTextMvaTestAsync()
    {
        InitialiseConnectedSystemObjectsData();
        
        // mock up a connector that will return updates for our existing connected system objects above.
        // changes: PROXY_ADDRESSES has some values removed.
        
        // add PROXY_ADDRESSES to our CSO first
        const string proxyAddress1 = "SMTP:jane.smith@phlebas.tetron.io";
        const string proxyAddress2 = "smtp:jane.wright@phlebas.tetron.io";
        const string proxyAddress3 = "smtp:cto@phlebas.tetron.io";
        var userObjectType = ConnectedSystemObjectTypesData.Single(q => q.Name.Equals("User", StringComparison.InvariantCultureIgnoreCase));
        var cso1ToSetup = ConnectedSystemObjectsData.Single(q => q.Id == TestConstants.CS_OBJECT_1_ID);
        cso1ToSetup.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            StringValue = proxyAddress1,
            Attribute = userObjectType.Attributes.Single(q => q.Name == MockAttributeName.PROXY_ADDRESSES.ToString()),
            ConnectedSystemObject = cso1ToSetup
        });
        cso1ToSetup.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            StringValue = proxyAddress2,
            Attribute = userObjectType.Attributes.Single(q => q.Name == MockAttributeName.PROXY_ADDRESSES.ToString()),
            ConnectedSystemObject = cso1ToSetup
        });
        cso1ToSetup.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            StringValue = proxyAddress3,
            Attribute = userObjectType.Attributes.Single(q => q.Name == MockAttributeName.PROXY_ADDRESSES.ToString()),
            ConnectedSystemObject = cso1ToSetup
        });
        cso1ToSetup.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            StringValue = "smtp:cto-pa@phlebas.tetron.io",
            Attribute = userObjectType.Attributes.Single(q => q.Name == MockAttributeName.PROXY_ADDRESSES.ToString()),
            ConnectedSystemObject = cso1ToSetup
        });
        cso1ToSetup.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            StringValue = "smtp:innovation@phlebas.tetron.io",
            Attribute = userObjectType.Attributes.Single(q => q.Name == MockAttributeName.PROXY_ADDRESSES.ToString()),
            ConnectedSystemObject = cso1ToSetup
        });
        
        // now build the import object, lacking some of the COMPLETED_COURSE_IDS
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
        
        // get the Connected System Object for the user we changed some attribute values for in the mocked connector
        var cso1 = await Jim.ConnectedSystems.GetConnectedSystemObjectAsync(1, TestConstants.CS_OBJECT_1_ID);
        Assert.That(cso1, Is.Not.EqualTo(null), "Expected to be able to retrieve the first CSO to validate.");

        var proxyAddressesAttributes = cso1.GetAttributeValues(MockAttributeName.PROXY_ADDRESSES.ToString());
        Assert.That(proxyAddressesAttributes, Is.Not.Null);
        Assert.That(proxyAddressesAttributes, Has.Count.EqualTo(3));
        
        Assert.That(proxyAddressesAttributes[0].StringValue, Is.EqualTo(proxyAddress1));
        Assert.That(proxyAddressesAttributes[1].StringValue, Is.EqualTo(proxyAddress2));
        Assert.That(proxyAddressesAttributes[2].StringValue, Is.EqualTo(proxyAddress3));
        
        Assert.Pass();
    }
    
    [Test]
    public async Task FullImportUpdateRemoveGuidMvaTestAsync()
    {
        InitialiseConnectedSystemObjectsData();
        
        // mock up a connector that will return updates for our existing connected system objects above.
        // changes: PREVIOUS_LOCATION_IDS has some values removed.
        
        // add PREVIOUS_LOCATION_IDS to our CSO first
        var previousLocation1 = Guid.NewGuid();
        var previousLocation2 = Guid.NewGuid();
        var previousLocation3 = Guid.NewGuid();
        var userObjectType = ConnectedSystemObjectTypesData.Single(q => q.Name.Equals("User", StringComparison.InvariantCultureIgnoreCase));
        var cso1ToSetup = ConnectedSystemObjectsData.Single(q => q.Id == TestConstants.CS_OBJECT_1_ID);
        cso1ToSetup.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            GuidValue = previousLocation1,
            Attribute = userObjectType.Attributes.Single(q => q.Name == MockAttributeName.PREVIOUS_LOCATION_IDS.ToString()),
            ConnectedSystemObject = cso1ToSetup
        });
        cso1ToSetup.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            GuidValue = previousLocation2,
            Attribute = userObjectType.Attributes.Single(q => q.Name == MockAttributeName.PREVIOUS_LOCATION_IDS.ToString()),
            ConnectedSystemObject = cso1ToSetup
        });
        cso1ToSetup.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            GuidValue = previousLocation3,
            Attribute = userObjectType.Attributes.Single(q => q.Name == MockAttributeName.PREVIOUS_LOCATION_IDS.ToString()),
            ConnectedSystemObject = cso1ToSetup
        });
        cso1ToSetup.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            GuidValue = Guid.NewGuid(),
            Attribute = userObjectType.Attributes.Single(q => q.Name == MockAttributeName.PREVIOUS_LOCATION_IDS.ToString()),
            ConnectedSystemObject = cso1ToSetup
        });
        cso1ToSetup.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            GuidValue = Guid.NewGuid(),
            Attribute = userObjectType.Attributes.Single(q => q.Name == MockAttributeName.PREVIOUS_LOCATION_IDS.ToString()),
            ConnectedSystemObject = cso1ToSetup
        });
        
        // now build the import object, lacking some of the PREVIOUS_LOCATION_IDS
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
        
        // get the Connected System Object for the user we changed some attribute values for in the mocked connector
        var cso1 = await Jim.ConnectedSystems.GetConnectedSystemObjectAsync(1, TestConstants.CS_OBJECT_1_ID);
        Assert.That(cso1, Is.Not.EqualTo(null), "Expected to be able to retrieve the first CSO to validate.");

        var previousLocationIdsAttributes = cso1.GetAttributeValues(MockAttributeName.PREVIOUS_LOCATION_IDS.ToString());
        Assert.That(previousLocationIdsAttributes, Is.Not.Null);
        Assert.That(previousLocationIdsAttributes, Has.Count.EqualTo(3));
        
        Assert.That(previousLocationIdsAttributes[0].GuidValue, Is.EqualTo(previousLocation1));
        Assert.That(previousLocationIdsAttributes[1].GuidValue, Is.EqualTo(previousLocation2));
        Assert.That(previousLocationIdsAttributes[2].GuidValue, Is.EqualTo(previousLocation3));
        
        Assert.Pass();
    }
    
    [Test]
    public async Task FullImportUpdateRemoveByteMvaTestAsync()
    {
        InitialiseConnectedSystemObjectsData();
        
        // mock up a connector that will return updates for our existing connected system objects above.
        // changes: CERTIFICATES has some values removed.
        
        // add CERTIFICATES to our CSO first
        var certificate1 = Convert.FromHexString(TestConstants.IMAGE_1_HEX);
        var certificate2 = Convert.FromHexString(TestConstants.IMAGE_2_HEX);
        var userObjectType = ConnectedSystemObjectTypesData.Single(q => q.Name.Equals("User", StringComparison.InvariantCultureIgnoreCase));
        var cso1ToSetup = ConnectedSystemObjectsData.Single(q => q.Id == TestConstants.CS_OBJECT_1_ID);
        cso1ToSetup.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            ByteValue = certificate1,
            Attribute = userObjectType.Attributes.Single(q => q.Name == MockAttributeName.CERTIFICATES.ToString()),
            ConnectedSystemObject = cso1ToSetup
        });
        cso1ToSetup.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            ByteValue = certificate2,
            Attribute = userObjectType.Attributes.Single(q => q.Name == MockAttributeName.CERTIFICATES.ToString()),
            ConnectedSystemObject = cso1ToSetup
        });
        cso1ToSetup.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            ByteValue = Convert.FromHexString("63d7e1c4060385ab79b6"),
            Attribute = userObjectType.Attributes.Single(q => q.Name == MockAttributeName.CERTIFICATES.ToString()),
            ConnectedSystemObject = cso1ToSetup
        });
        cso1ToSetup.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            ByteValue = Convert.FromHexString("117302f584f512760d58"),
            Attribute = userObjectType.Attributes.Single(q => q.Name == MockAttributeName.CERTIFICATES.ToString()),
            ConnectedSystemObject = cso1ToSetup
        });
        
        // now build the import object, lacking some of the CERTIFICATES
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
                        certificate2
                    },
                    Type = AttributeDataType.Binary
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
        
        // get the Connected System Object for the user we changed some attribute values for in the mocked connector
        var cso1 = await Jim.ConnectedSystems.GetConnectedSystemObjectAsync(1, TestConstants.CS_OBJECT_1_ID);
        Assert.That(cso1, Is.Not.EqualTo(null), "Expected to be able to retrieve the first CSO to validate.");

        var certificatesAttributes = cso1.GetAttributeValues(MockAttributeName.CERTIFICATES.ToString());
        Assert.That(certificatesAttributes, Is.Not.Null);
        Assert.That(certificatesAttributes, Has.Count.EqualTo(2));
        Assert.That(certificatesAttributes[0].ByteValue, Is.EqualTo(certificate1));
        Assert.That(certificatesAttributes[1].ByteValue, Is.EqualTo(certificate2));
        
        Assert.Pass();
    }
    
    [Test]
    public async Task FullImportUpdateRemoveReferenceMvaTestAsync()
    {
        InitialiseConnectedSystemObjectsData();
        
        // mock up a connector that will return updates for our existing connected system objects above.
        // changes: CERTIFICATES has some values removed.
        
        // add some MEMBER references to our group CSO first
        var groupObjectType = ConnectedSystemObjectTypesData.Single(q => q.Name.Equals("Group", StringComparison.InvariantCultureIgnoreCase));
        var groupToSetup = ConnectedSystemObjectsData.Single(q => q.Id == TestConstants.CS_OBJECT_4_ID);
        var member1 = ConnectedSystemObjectsData.Single(q => q.Id == TestConstants.CS_OBJECT_1_ID);
        var member2 = ConnectedSystemObjectsData.Single(q => q.Id == TestConstants.CS_OBJECT_2_ID);
        var member3 = ConnectedSystemObjectsData.Single(q => q.Id == TestConstants.CS_OBJECT_3_ID);
        groupToSetup.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            ReferenceValue = member1,
            ReferenceValueId = member1.Id,
            UnresolvedReferenceValue = TestConstants.CS_OBJECT_1_HR_ID.ToString(),
            Attribute = groupObjectType.Attributes.Single(q => q.Name == MockAttributeName.MEMBER.ToString()),
            ConnectedSystemObject = groupToSetup
        });
        groupToSetup.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            ReferenceValue = member2,
            ReferenceValueId = member2.Id,
            UnresolvedReferenceValue = TestConstants.CS_OBJECT_2_HR_ID.ToString(),
            Attribute = groupObjectType.Attributes.Single(q => q.Name == MockAttributeName.MEMBER.ToString()),
            ConnectedSystemObject = groupToSetup
        });
        groupToSetup.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            ReferenceValue = member3,
            ReferenceValueId = member3.Id,
            UnresolvedReferenceValue = TestConstants.CS_OBJECT_3_HR_ID.ToString(),
            Attribute = groupObjectType.Attributes.Single(q => q.Name == MockAttributeName.MEMBER.ToString()),
            ConnectedSystemObject = groupToSetup
        });
        
        // now build the import object, lacking some of the MEMBER references
        var mockFileConnector = new MockFileConnector();
        mockFileConnector.TestImportObjects.Add(new ConnectedSystemImportObject
        {
            ChangeType = ObjectChangeType.Create,
            ObjectType = "Group",
            Attributes = new List<ConnectedSystemImportObjectAttribute>()
            {
                new ()
                {
                    Name = MockAttributeName.GROUP_UID.ToString(),
                    GuidValues = new List<Guid> { TestConstants.CS_OBJECT_4_GROUP_UID },
                    Type = AttributeDataType.Guid
                },
                new ()
                {
                    Name = MockAttributeName.DISPLAY_NAME.ToString(),
                    StringValues = new List<string> { TestConstants.CS_OBJECT_4_DISPLAY_NAME },
                    Type = AttributeDataType.Text
                },
                new ()
                {
                    Name = MockAttributeName.MEMBER.ToString(),
                    ReferenceValues = new List<string>
                    {
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
        
        // get the Connected System Object for the user we changed some attribute values for in the mocked connector
        var cso4 = await Jim.ConnectedSystems.GetConnectedSystemObjectAsync(1, TestConstants.CS_OBJECT_4_ID);
        Assert.That(cso4, Is.Not.EqualTo(null), "Expected to be able to retrieve the fourth CSO to validate.");

        var memberAttributes = cso4.GetAttributeValues(MockAttributeName.MEMBER.ToString());
        Assert.That(memberAttributes, Is.Not.Null, "Expected there to be a member attribute value");
        Assert.That(memberAttributes, Has.Count.EqualTo(1), $"Expected there to only be a single member now. There are {memberAttributes.Count}");
        Assert.That(memberAttributes[0].ReferenceValue, Is.Not.Null, "Expected the sole member reference value to not be null.");
        Assert.That(memberAttributes[0].ReferenceValue.Id, Is.EqualTo(member3.Id), "Expected the sole member to be the third cso user.");
        
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