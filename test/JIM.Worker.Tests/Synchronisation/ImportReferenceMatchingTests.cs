using JIM.Application;
using JIM.Connectors.Mock;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.PostgresData;
using JIM.Worker.Processors;
using JIM.Worker.Tests.Models;
using Microsoft.EntityFrameworkCore;
using MockQueryable.Moq;
using Moq;

namespace JIM.Worker.Tests.Synchronisation;

/// <summary>
/// Tests for import reference matching resilience when EF Core's AsSplitQuery() fails to
/// materialise ReferenceValue navigation properties (dotnet/efcore#33826).
/// </summary>
public class ImportReferenceMatchingTests
{
    #region accessors
    private MetaverseObject InitiatedBy { get; set; } = null!;
    private List<ConnectedSystem> ConnectedSystemsData { get; set; } = null!;
    private Mock<DbSet<ConnectedSystem>> MockDbSetConnectedSystems { get; set; } = null!;
    private List<ConnectedSystemRunProfile> ConnectedSystemRunProfilesData { get; set; } = null!;
    private Mock<DbSet<ConnectedSystemRunProfile>> MockDbSetConnectedSystemRunProfiles { get; set; } = null!;
    private List<ConnectedSystemObject> ConnectedSystemObjectsData { get; } = new();
    private List<ConnectedSystemObjectType> ConnectedSystemObjectTypesData { get; set; } = null!;
    private Mock<DbSet<ConnectedSystemObjectType>> MockDbSetConnectedSystemObjectTypes { get; set; } = null!;
    private List<ConnectedSystemPartition> ConnectedSystemPartitionsData { get; set; } = null!;
    private Mock<DbSet<ConnectedSystemPartition>> MockDbSetConnectedSystemPartitions { get; set; } = null!;
    private List<Activity> ActivitiesData { get; set; } = null!;
    private Mock<DbSet<Activity>> MockDbSetActivities { get; set; } = null!;
    private List<ServiceSetting> ServiceSettingsData { get; set; } = null!;
    private Mock<DbSet<ServiceSetting>> MockDbSetServiceSettings { get; set; } = null!;
    private List<PendingExport> PendingExportsData { get; set; } = null!;
    private Mock<DbSet<PendingExport>> MockDbSetPendingExports { get; set; } = null!;
    private Mock<JimDbContext> MockJimDbContext { get; set; } = null!;
    private JimApplication Jim { get; set; } = null!;
    #endregion

    [TearDown]
    public void TearDown()
    {
        Jim?.Dispose();
    }

    [SetUp]
    public void Setup()
    {
        TestUtilities.SetEnvironmentVariables();
        InitiatedBy = TestUtilities.GetInitiatedBy();

        ConnectedSystemsData = TestUtilities.GetConnectedSystemData();
        MockDbSetConnectedSystems = ConnectedSystemsData.BuildMockDbSet();

        ConnectedSystemRunProfilesData = TestUtilities.GetConnectedSystemRunProfileData();
        MockDbSetConnectedSystemRunProfiles = ConnectedSystemRunProfilesData.BuildMockDbSet();

        ConnectedSystemObjectTypesData = TestUtilities.GetConnectedSystemObjectTypeData();
        MockDbSetConnectedSystemObjectTypes = ConnectedSystemObjectTypesData.BuildMockDbSet();

        ConnectedSystemPartitionsData = TestUtilities.GetConnectedSystemPartitionData();
        MockDbSetConnectedSystemPartitions = ConnectedSystemPartitionsData.BuildMockDbSet();

        var fullImportRunProfile = ConnectedSystemRunProfilesData[0];
        ActivitiesData = TestUtilities.GetActivityData(fullImportRunProfile.RunType, fullImportRunProfile.Id);
        MockDbSetActivities = ActivitiesData.BuildMockDbSet();

        ServiceSettingsData = TestUtilities.GetServiceSettingsData();
        MockDbSetServiceSettings = ServiceSettingsData.BuildMockDbSet();

        PendingExportsData = new List<PendingExport>();
        MockDbSetPendingExports = PendingExportsData.BuildMockDbSet();

        MockJimDbContext = new Mock<JimDbContext>();
        MockJimDbContext.Setup(m => m.Activities).Returns(MockDbSetActivities.Object);
        MockJimDbContext.Setup(m => m.ConnectedSystems).Returns(MockDbSetConnectedSystems.Object);
        MockJimDbContext.Setup(m => m.ConnectedSystemObjectTypes).Returns(MockDbSetConnectedSystemObjectTypes.Object);
        MockJimDbContext.Setup(m => m.ConnectedSystemRunProfiles).Returns(MockDbSetConnectedSystemRunProfiles.Object);
        MockJimDbContext.Setup(m => m.ConnectedSystemPartitions).Returns(MockDbSetConnectedSystemPartitions.Object);
        MockJimDbContext.Setup(m => m.ServiceSettingItems).Returns(MockDbSetServiceSettings.Object);
        MockJimDbContext.Setup(m => m.PendingExports).Returns(MockDbSetPendingExports.Object);

        Jim = new JimApplication(new PostgresDataRepository(MockJimDbContext.Object));

        var mockDbSetConnectedSystemObject = ConnectedSystemObjectsData.BuildMockDbSet();
        mockDbSetConnectedSystemObject.Setup(set => set.AddRange(It.IsAny<IEnumerable<ConnectedSystemObject>>())).Callback((IEnumerable<ConnectedSystemObject> entities) =>
        {
            var connectedSystemObjects = entities as ConnectedSystemObject[] ?? entities.ToArray();
            foreach (var entity in connectedSystemObjects)
                entity.Id = Guid.NewGuid();
            ConnectedSystemObjectsData.AddRange(connectedSystemObjects);
        });
        MockJimDbContext.Setup(m => m.ConnectedSystemObjects).Returns(mockDbSetConnectedSystemObject.Object);
    }

    /// <summary>
    /// Tests that when AsSplitQuery() drops ReferenceValue navigations (setting them to null),
    /// existing resolved member references are NOT spuriously removed if the navigation chain
    /// has the data. This is the baseline test where navigations are healthy.
    /// </summary>
    [Test]
    public async Task FullImportUpdate_ReferenceWithHealthyNavigation_NoSpuriousRemovalsAsync()
    {
        InitialiseConnectedSystemObjectsData();

        var groupObjectType = ConnectedSystemObjectTypesData.Single(q => q.Name.Equals("SOURCE_GROUP", StringComparison.InvariantCultureIgnoreCase));
        var groupToSetup = ConnectedSystemObjectsData.Single(q => q.Id == TestConstants.CS_OBJECT_4_ID);
        var member1 = ConnectedSystemObjectsData.Single(q => q.Id == TestConstants.CS_OBJECT_1_ID);
        var member2 = ConnectedSystemObjectsData.Single(q => q.Id == TestConstants.CS_OBJECT_2_ID);
        var member3 = ConnectedSystemObjectsData.Single(q => q.Id == TestConstants.CS_OBJECT_3_ID);

        // Set up secondary external ID attribute on members (simulating LDAP DN as secondary ext ID)
        var memberAttribute = groupObjectType.Attributes.Single(q => q.Name == MockSourceSystemAttributeNames.MEMBER.ToString());

        // Add resolved member references with healthy ReferenceValue navigations
        groupToSetup.AttributeValues.Add(CreateResolvedMemberRef(member1, memberAttribute, groupToSetup, TestConstants.CS_OBJECT_1_HR_ID.ToString()));
        groupToSetup.AttributeValues.Add(CreateResolvedMemberRef(member2, memberAttribute, groupToSetup, TestConstants.CS_OBJECT_2_HR_ID.ToString()));
        groupToSetup.AttributeValues.Add(CreateResolvedMemberRef(member3, memberAttribute, groupToSetup, TestConstants.CS_OBJECT_3_HR_ID.ToString()));

        // Import the same group with the same 3 members — no changes expected
        var mockFileConnector = new MockFileConnector();
        mockFileConnector.TestImportObjects.Add(CreateGroupImportObject(
            TestConstants.CS_OBJECT_1_HR_ID.ToString(),
            TestConstants.CS_OBJECT_2_HR_ID.ToString(),
            TestConstants.CS_OBJECT_3_HR_ID.ToString()));

        var connectedSystem = await Jim.ConnectedSystems.GetConnectedSystemAsync(1);
        Assert.That(connectedSystem, Is.Not.Null);

        var activity = ActivitiesData.First();
        var runProfile = ConnectedSystemRunProfilesData.Single(q => q.ConnectedSystemId == connectedSystem!.Id && q.RunType == ConnectedSystemRunType.FullImport);
        var importProcessor = new SyncImportTaskProcessor(Jim, mockFileConnector, connectedSystem!, runProfile, TestUtilities.CreateTestWorkerTask(activity, InitiatedBy), new CancellationTokenSource());
        await importProcessor.PerformFullImportAsync();

        // Verify all 3 member references are preserved
        var cso4 = await Jim.ConnectedSystems.GetConnectedSystemObjectAsync(1, TestConstants.CS_OBJECT_4_ID);
        Assert.That(cso4, Is.Not.Null);
        var memberAttributes = cso4!.GetAttributeValues(MockSourceSystemAttributeNames.MEMBER.ToString());
        Assert.That(memberAttributes, Has.Count.EqualTo(3), "Expected all 3 member references to be preserved when navigations are healthy.");
    }

    /// <summary>
    /// Tests the confirming import scenario where AsSplitQuery() drops ReferenceValue navigations.
    /// In the real scenario:
    /// - Target CSO member refs were created during provisioning with UnresolvedReferenceValue = MVO GUID
    /// - The confirming import reads DNs from LDAP (different format from MVO GUID)
    /// - ImportRefMatchesCsoValue must match DN against ReferenceValue.SecondaryExternalIdAttributeValue
    /// - When AsSplitQuery drops ReferenceValue, this match fails
    ///
    /// In this test, UnresolvedReferenceValue is set to an MVO GUID (not matching the import string),
    /// simulating the real confirming import scenario. Without the SQL dictionary fallback, the
    /// reference check falls through to the unresolved comparison which also fails (format mismatch).
    ///
    /// Note: On non-relational provider, GetReferenceExternalIdsAsync returns empty, so the null
    /// navigation refs match via UnresolvedReferenceValue (which we set to the import string for
    /// test simplicity — in production, the unresolved ref would be an MVO GUID and would NOT match).
    /// </summary>
    [Test]
    public async Task FullImportUpdate_NullReferenceNavigation_WithMatchingUnresolvedRef_NoSpuriousRemovalsAsync()
    {
        InitialiseConnectedSystemObjectsData();

        var groupObjectType = ConnectedSystemObjectTypesData.Single(q => q.Name.Equals("SOURCE_GROUP", StringComparison.InvariantCultureIgnoreCase));
        var groupToSetup = ConnectedSystemObjectsData.Single(q => q.Id == TestConstants.CS_OBJECT_4_ID);
        var member1 = ConnectedSystemObjectsData.Single(q => q.Id == TestConstants.CS_OBJECT_1_ID);
        var member2 = ConnectedSystemObjectsData.Single(q => q.Id == TestConstants.CS_OBJECT_2_ID);
        var member3 = ConnectedSystemObjectsData.Single(q => q.Id == TestConstants.CS_OBJECT_3_ID);
        var memberAttribute = groupObjectType.Attributes.Single(q => q.Name == MockSourceSystemAttributeNames.MEMBER.ToString());

        // Simulate AsSplitQuery bug: ReferenceValueId is set but ReferenceValue is null.
        // UnresolvedReferenceValue matches the import string (test simplification — in the real
        // confirming import scenario, this would be an MVO GUID that doesn't match the DN).
        groupToSetup.AttributeValues.Add(CreateNullNavigationMemberRef(member1.Id, memberAttribute, groupToSetup, TestConstants.CS_OBJECT_1_HR_ID.ToString()));
        groupToSetup.AttributeValues.Add(CreateNullNavigationMemberRef(member2.Id, memberAttribute, groupToSetup, TestConstants.CS_OBJECT_2_HR_ID.ToString()));
        // member3 has healthy navigation (mixed scenario)
        groupToSetup.AttributeValues.Add(CreateResolvedMemberRef(member3, memberAttribute, groupToSetup, TestConstants.CS_OBJECT_3_HR_ID.ToString()));

        // Import the same group with all 3 members
        var mockFileConnector = new MockFileConnector();
        mockFileConnector.TestImportObjects.Add(CreateGroupImportObject(
            TestConstants.CS_OBJECT_1_HR_ID.ToString(),
            TestConstants.CS_OBJECT_2_HR_ID.ToString(),
            TestConstants.CS_OBJECT_3_HR_ID.ToString()));

        var connectedSystem = await Jim.ConnectedSystems.GetConnectedSystemAsync(1);
        Assert.That(connectedSystem, Is.Not.Null);

        var activity = ActivitiesData.First();
        var runProfile = ConnectedSystemRunProfilesData.Single(q => q.ConnectedSystemId == connectedSystem!.Id && q.RunType == ConnectedSystemRunType.FullImport);
        var importProcessor = new SyncImportTaskProcessor(Jim, mockFileConnector, connectedSystem!, runProfile, TestUtilities.CreateTestWorkerTask(activity, InitiatedBy), new CancellationTokenSource());
        await importProcessor.PerformFullImportAsync();

        // Since UnresolvedReferenceValue matches the import string (case-sensitive), all 3
        // refs are preserved even though ReferenceValue is null on member1 and member2.
        var cso4 = await Jim.ConnectedSystems.GetConnectedSystemObjectAsync(1, TestConstants.CS_OBJECT_4_ID);
        Assert.That(cso4, Is.Not.Null);
        var memberAttributes = cso4!.GetAttributeValues(MockSourceSystemAttributeNames.MEMBER.ToString());
        Assert.That(memberAttributes, Has.Count.EqualTo(3),
            "Expected all 3 member references to be preserved via UnresolvedReferenceValue match.");
    }

    #region helpers

    private static ConnectedSystemObjectAttributeValue CreateResolvedMemberRef(
        ConnectedSystemObject referencedCso,
        ConnectedSystemObjectTypeAttribute attribute,
        ConnectedSystemObject ownerCso,
        string unresolvedRefValue)
    {
        return new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            ReferenceValue = referencedCso,
            ReferenceValueId = referencedCso.Id,
            UnresolvedReferenceValue = unresolvedRefValue,
            Attribute = attribute,
            ConnectedSystemObject = ownerCso
        };
    }

    private static ConnectedSystemObjectAttributeValue CreateNullNavigationMemberRef(
        Guid referencedCsoId,
        ConnectedSystemObjectTypeAttribute attribute,
        ConnectedSystemObject ownerCso,
        string unresolvedRefValue)
    {
        // Simulates AsSplitQuery materialisation failure:
        // ReferenceValueId is set (FK exists in DB) but ReferenceValue navigation is null.
        // UnresolvedReferenceValue is preserved from when the reference was originally created.
        return new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            ReferenceValue = null,
            ReferenceValueId = referencedCsoId,
            UnresolvedReferenceValue = unresolvedRefValue,
            Attribute = attribute,
            ConnectedSystemObject = ownerCso
        };
    }

    private static ConnectedSystemImportObject CreateGroupImportObject(params string[] memberRefs)
    {
        return new ConnectedSystemImportObject
        {
            ChangeType = ObjectChangeType.NotSet,
            ObjectType = "SOURCE_GROUP",
            Attributes = new List<ConnectedSystemImportObjectAttribute>
            {
                new()
                {
                    Name = MockSourceSystemAttributeNames.GROUP_UID.ToString(),
                    GuidValues = new List<Guid> { TestConstants.CS_OBJECT_4_GROUP_UID },
                    Type = AttributeDataType.Guid
                },
                new()
                {
                    Name = MockSourceSystemAttributeNames.DISPLAY_NAME.ToString(),
                    StringValues = new List<string> { TestConstants.CS_OBJECT_4_DISPLAY_NAME },
                    Type = AttributeDataType.Text
                },
                new()
                {
                    Name = MockSourceSystemAttributeNames.MEMBER.ToString(),
                    ReferenceValues = new List<string>(memberRefs),
                    Type = AttributeDataType.Reference
                }
            }
        };
    }

    private void InitialiseConnectedSystemObjectsData()
    {
        ConnectedSystemObjectsData.Clear();

        var userObjectType = ConnectedSystemObjectTypesData.Single(q => q.Name.Equals("SOURCE_USER", StringComparison.InvariantCultureIgnoreCase));
        var groupObjectType = ConnectedSystemObjectTypesData.Single(q => q.Name.Equals("SOURCE_GROUP", StringComparison.InvariantCultureIgnoreCase));

        // user 1
        var cso1 = new ConnectedSystemObject
        {
            Id = TestConstants.CS_OBJECT_1_ID,
            ConnectedSystemId = 1,
            ConnectedSystem = ConnectedSystemsData.First(),
            Type = userObjectType,
            ExternalIdAttributeId = (int)MockSourceSystemAttributeNames.HR_ID
        };
        cso1.AttributeValues = new List<ConnectedSystemObjectAttributeValue>
        {
            new() { Id = Guid.NewGuid(), GuidValue = TestConstants.CS_OBJECT_1_HR_ID, Attribute = userObjectType.Attributes.Single(q => q.Name == MockSourceSystemAttributeNames.HR_ID.ToString()), ConnectedSystemObject = cso1 },
            new() { Id = Guid.NewGuid(), IntValue = 1, Attribute = userObjectType.Attributes.Single(q => q.Name == MockSourceSystemAttributeNames.EMPLOYEE_ID.ToString()), ConnectedSystemObject = cso1 },
            new() { Id = Guid.NewGuid(), StringValue = TestConstants.CS_OBJECT_1_DISPLAY_NAME, Attribute = userObjectType.Attributes.Single(q => q.Name == MockSourceSystemAttributeNames.DISPLAY_NAME.ToString()), ConnectedSystemObject = cso1 }
        };
        ConnectedSystemObjectsData.Add(cso1);

        // user 2
        var cso2 = new ConnectedSystemObject
        {
            Id = TestConstants.CS_OBJECT_2_ID,
            ConnectedSystemId = 1,
            ConnectedSystem = ConnectedSystemsData.First(),
            Type = userObjectType,
            ExternalIdAttributeId = (int)MockSourceSystemAttributeNames.HR_ID
        };
        cso2.AttributeValues = new List<ConnectedSystemObjectAttributeValue>
        {
            new() { Id = Guid.NewGuid(), GuidValue = TestConstants.CS_OBJECT_2_HR_ID, Attribute = userObjectType.Attributes.Single(q => q.Name == MockSourceSystemAttributeNames.HR_ID.ToString()), ConnectedSystemObject = cso2 },
            new() { Id = Guid.NewGuid(), IntValue = 2, Attribute = userObjectType.Attributes.Single(q => q.Name == MockSourceSystemAttributeNames.EMPLOYEE_ID.ToString()), ConnectedSystemObject = cso2 },
            new() { Id = Guid.NewGuid(), StringValue = TestConstants.CS_OBJECT_2_DISPLAY_NAME, Attribute = userObjectType.Attributes.Single(q => q.Name == MockSourceSystemAttributeNames.DISPLAY_NAME.ToString()), ConnectedSystemObject = cso2 }
        };
        ConnectedSystemObjectsData.Add(cso2);

        // user 3
        var cso3 = new ConnectedSystemObject
        {
            Id = TestConstants.CS_OBJECT_3_ID,
            ConnectedSystemId = 1,
            ConnectedSystem = ConnectedSystemsData.First(),
            Type = userObjectType,
            ExternalIdAttributeId = (int)MockSourceSystemAttributeNames.HR_ID
        };
        cso3.AttributeValues = new List<ConnectedSystemObjectAttributeValue>
        {
            new() { Id = Guid.NewGuid(), GuidValue = TestConstants.CS_OBJECT_3_HR_ID, Attribute = userObjectType.Attributes.Single(q => q.Name == MockSourceSystemAttributeNames.HR_ID.ToString()), ConnectedSystemObject = cso3 },
            new() { Id = Guid.NewGuid(), IntValue = 3, Attribute = userObjectType.Attributes.Single(q => q.Name == MockSourceSystemAttributeNames.EMPLOYEE_ID.ToString()), ConnectedSystemObject = cso3 },
            new() { Id = Guid.NewGuid(), StringValue = TestConstants.CS_OBJECT_3_DISPLAY_NAME, Attribute = userObjectType.Attributes.Single(q => q.Name == MockSourceSystemAttributeNames.DISPLAY_NAME.ToString()), ConnectedSystemObject = cso3 }
        };
        ConnectedSystemObjectsData.Add(cso3);

        // group
        var cso4 = new ConnectedSystemObject
        {
            Id = TestConstants.CS_OBJECT_4_ID,
            ConnectedSystemId = 1,
            ConnectedSystem = ConnectedSystemsData.First(),
            Type = groupObjectType,
            ExternalIdAttributeId = (int)MockSourceSystemAttributeNames.GROUP_UID
        };
        cso4.AttributeValues = new List<ConnectedSystemObjectAttributeValue>
        {
            new() { Id = Guid.NewGuid(), GuidValue = TestConstants.CS_OBJECT_4_GROUP_UID, Attribute = groupObjectType.Attributes.Single(q => q.Name == MockSourceSystemAttributeNames.GROUP_UID.ToString()), ConnectedSystemObject = cso4 },
            new() { Id = Guid.NewGuid(), StringValue = TestConstants.CS_OBJECT_4_DISPLAY_NAME, Attribute = groupObjectType.Attributes.Single(q => q.Name == MockSourceSystemAttributeNames.DISPLAY_NAME.ToString()), ConnectedSystemObject = cso4 }
        };
        ConnectedSystemObjectsData.Add(cso4);
    }

    #endregion
}
