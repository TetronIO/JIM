// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Application.Servers;
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
using SyncRepository = JIM.InMemoryData.SyncRepository;

namespace JIM.Worker.Tests.Synchronisation;

/// <summary>
/// Import reference resolution must be scoped by Object Type (#1285).
/// </summary>
/// <remarks>
/// <para>
/// Two Object Types in one Connected System may legitimately share an anchor value space; a view over a table
/// has the table's keys by construction. Before #1285 the reference lookups were keyed by value alone, so the
/// first shared value aborted the whole Full Import with a duplicate-external-id exception, and resolution
/// guessed that every reference pointed at the referencer's own Object Type.
/// </para>
/// <para>
/// After #1285: the lookups are partitioned by Object Type; a Reference attribute that declares its target
/// (<see cref="ConnectedSystemObjectTypeAttribute.ReferencedObjectTypeId"/>, stated by the SQL Connector's
/// schema document) resolves within that type alone; an undeclared reference resolves when its value is
/// unambiguous across all types and is reported per the Connected System's unresolved-reference handling when
/// it is not. A genuine duplicate anchor within one Object Type still fails fast, naming what collided.
/// </para>
/// </remarks>
public class ImportTypeScopedReferenceResolutionTests
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
    private SyncRepository SyncRepo { get; set; } = null!;
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
        TestUtilities.SetUpEmptyConnectedSystemGraphMocks(MockJimDbContext);
        MockJimDbContext.Setup(m => m.Activities).Returns(MockDbSetActivities.Object);
        MockJimDbContext.Setup(m => m.ConnectedSystems).Returns(MockDbSetConnectedSystems.Object);
        MockJimDbContext.Setup(m => m.ConnectedSystemObjectTypes).Returns(MockDbSetConnectedSystemObjectTypes.Object);
        MockJimDbContext.Setup(m => m.ConnectedSystemRunProfiles).Returns(MockDbSetConnectedSystemRunProfiles.Object);
        MockJimDbContext.Setup(m => m.ConnectedSystemPartitions).Returns(MockDbSetConnectedSystemPartitions.Object);
        MockJimDbContext.Setup(m => m.ServiceSettingItems).Returns(MockDbSetServiceSettings.Object);
        MockJimDbContext.Setup(m => m.PendingExports).Returns(MockDbSetPendingExports.Object);

        SyncRepo = TestUtilities.CreateSyncRepository(activity: ActivitiesData.First());
        Jim = new JimApplication(new PostgresDataRepository(MockJimDbContext.Object), syncRepository: SyncRepo);

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
    /// The #1285 crash itself: a user and a group whose anchors hold the same value must both import. Before
    /// the fix, BuildExternalIdLookups threw a duplicate-external-id exception and the whole run aborted.
    /// </summary>
    [Test]
    public async Task FullImport_TwoObjectTypesSharingAnAnchorValue_CompletesWithoutErrorAsync()
    {
        var sharedAnchorValue = Guid.NewGuid();
        var mockFileConnector = new MockFileConnector();
        mockFileConnector.TestImportObjects.Add(CreateUserImportObject(sharedAnchorValue, "Shared Anchor User"));
        mockFileConnector.TestImportObjects.Add(CreateGroupImportObject(sharedAnchorValue, "Shared Anchor Group"));

        var (connectedSystem, activity) = await RunFullImportAsync(mockFileConnector);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(SyncRepo.ConnectedSystemObjects.Count, Is.EqualTo(2),
                "A user and a group legitimately share an anchor value space; both must import.");
            Assert.That(activity.RunProfileExecutionItems.Where(item =>
                item.ErrorType != null && item.ErrorType != ActivityRunProfileExecutionItemErrorType.NotSet), Is.Empty,
                "Nothing about the shared value is an error; the Reference lookups must partition by Object Type.");
            Assert.That(connectedSystem, Is.Not.Null);
        }
    }

    /// <summary>
    /// A Reference attribute that declares its target Object Type resolves within that type alone, even when
    /// another Object Type holds the same anchor value.
    /// </summary>
    [Test]
    public async Task FullImport_ADeclaredReferenceTarget_ResolvesWithinThatObjectTypeAsync()
    {
        var sharedAnchorValue = Guid.NewGuid();
        var referencingGroupUid = Guid.NewGuid();

        var mockFileConnector = new MockFileConnector();
        mockFileConnector.TestImportObjects.Add(CreateUserImportObject(sharedAnchorValue, "Referenced User"));
        mockFileConnector.TestImportObjects.Add(CreateGroupImportObject(sharedAnchorValue, "Decoy Group"));
        mockFileConnector.TestImportObjects.Add(CreateGroupImportObject(referencingGroupUid, "Referencing Group", sharedAnchorValue.ToString()));

        var (connectedSystem, _) = await RunFullImportAsync(mockFileConnector, declareMemberTargetsUsers: true);

        var referencingGroup = SyncRepo.ConnectedSystemObjects.Values.Single(cso =>
            cso.AttributeValues.Any(av => av.GuidValue == referencingGroupUid));
        var member = referencingGroup.AttributeValues.Single(av => av.Attribute.Name == MockSourceSystemAttributeNames.MEMBER.ToString());
        using (Assert.EnterMultipleScope())
        {
            Assert.That(member.ReferenceValue, Is.Not.Null, "The declared target disambiguates the shared value; the reference must resolve.");
            Assert.That(member.ReferenceValue!.Type.Name, Is.EqualTo("SOURCE_USER"),
                "The attribute declares it references users, so the decoy group holding the same anchor value must never be chosen.");
            Assert.That(connectedSystem, Is.Not.Null);
        }
    }

    /// <summary>
    /// Regression pin: an undeclared reference whose value exists in exactly one Object Type keeps resolving,
    /// exactly as it did before #1285. This is every CSV and legacy schema.
    /// </summary>
    [Test]
    public async Task FullImport_AnUndeclaredReferenceMatchingOneObjectType_StillResolvesAsync()
    {
        var userAnchor = Guid.NewGuid();
        var groupUid = Guid.NewGuid();

        var mockFileConnector = new MockFileConnector();
        mockFileConnector.TestImportObjects.Add(CreateUserImportObject(userAnchor, "Referenced User"));
        mockFileConnector.TestImportObjects.Add(CreateGroupImportObject(groupUid, "Referencing Group", userAnchor.ToString()));

        await RunFullImportAsync(mockFileConnector);

        var group = SyncRepo.ConnectedSystemObjects.Values.Single(cso =>
            cso.AttributeValues.Any(av => av.GuidValue == groupUid));
        var member = group.AttributeValues.Single(av => av.Attribute.Name == MockSourceSystemAttributeNames.MEMBER.ToString());
        Assert.That(member.ReferenceValue?.Type.Name, Is.EqualTo("SOURCE_USER"));
    }

    /// <summary>
    /// An undeclared reference whose value exists in two Object Types is genuinely ambiguous: it is reported
    /// per the Connected System's unresolved-reference handling, naming the candidates, and the run continues.
    /// </summary>
    [Test]
    public async Task FullImport_AnUndeclaredReferenceMatchingTwoObjectTypes_IsReportedNamingTheCandidatesAsync()
    {
        var sharedAnchorValue = Guid.NewGuid();
        var referencingGroupUid = Guid.NewGuid();

        var mockFileConnector = new MockFileConnector();
        mockFileConnector.TestImportObjects.Add(CreateUserImportObject(sharedAnchorValue, "Ambiguous User"));
        mockFileConnector.TestImportObjects.Add(CreateGroupImportObject(sharedAnchorValue, "Ambiguous Group"));
        mockFileConnector.TestImportObjects.Add(CreateGroupImportObject(referencingGroupUid, "Referencing Group", sharedAnchorValue.ToString()));

        var (_, activity) = await RunFullImportAsync(mockFileConnector);

        var referencingGroup = SyncRepo.ConnectedSystemObjects.Values.Single(cso =>
            cso.AttributeValues.Any(av => av.GuidValue == referencingGroupUid));
        var member = referencingGroup.AttributeValues.Single(av => av.Attribute.Name == MockSourceSystemAttributeNames.MEMBER.ToString());
        var errorItem = activity.RunProfileExecutionItems.FirstOrDefault(item => item.ErrorType == ActivityRunProfileExecutionItemErrorType.UnresolvedReference);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(SyncRepo.ConnectedSystemObjects.Count, Is.EqualTo(3), "Ambiguity is a per-object condition; the run must complete.");
            Assert.That(member.ReferenceValue, Is.Null, "An ambiguous reference must never be resolved by guessing.");
            Assert.That(errorItem, Is.Not.Null, "Error handling mode marks the referencing object's Run Profile Execution Item.");
            Assert.That(errorItem!.ErrorMessage, Does.Contain("SOURCE_USER").And.Contain("SOURCE_GROUP"),
                "An administrator can only fix an ambiguity the message names.");
        }
    }

    /// <summary>
    /// The mirror of the declared-target test above: a Reference attribute declaring the GROUP Object Type
    /// resolves to the group even when a user holds the same anchor value. Both directions are tested because
    /// a resolver that always preferred one partition (first built, lowest id) would pass a single direction.
    /// </summary>
    [Test]
    public async Task FullImport_ADeclaredReferenceTarget_ResolvesWithinThatObjectTypeInTheOtherDirectionAsync()
    {
        var sharedAnchorValue = Guid.NewGuid();
        var referencingUserAnchor = Guid.NewGuid();

        var mockFileConnector = new MockFileConnector();
        mockFileConnector.TestImportObjects.Add(CreateUserImportObject(sharedAnchorValue, "Decoy User"));
        mockFileConnector.TestImportObjects.Add(CreateGroupImportObject(sharedAnchorValue, "Referenced Group"));
        mockFileConnector.TestImportObjects.Add(CreateUserImportObject(referencingUserAnchor, "Referencing User", managerRef: sharedAnchorValue.ToString()));

        var connectedSystem = await Jim.ConnectedSystems.GetConnectedSystemAsync(1);
        Assert.That(connectedSystem, Is.Not.Null);

        // Declare SOURCE_USER's MANAGER attribute as referencing the SOURCE_GROUP Object Type. Semantically
        // contrived (a manager who is a group), deliberately: it is the exact mirror of MEMBER -> SOURCE_USER,
        // so the two tests together prove the declared target decides the partition, not the partition order.
        var userType = connectedSystem!.ObjectTypes!.Single(t => t.Name == "SOURCE_USER");
        var groupType = connectedSystem!.ObjectTypes!.Single(t => t.Name == "SOURCE_GROUP");
        var managerAttribute = userType.Attributes.Single(a => a.Name == MockSourceSystemAttributeNames.MANAGER.ToString());
        managerAttribute.ReferencedObjectTypeId = groupType.Id;
        managerAttribute.ReferencedObjectType = groupType;

        var activity = ActivitiesData.First();
        var runProfile = ConnectedSystemRunProfilesData.Single(q => q.ConnectedSystemId == connectedSystem!.Id && q.RunType == ConnectedSystemRunType.FullImport);
        var importProcessor = new SyncImportTaskProcessor(Jim, SyncRepo, new SyncServer(Jim), new SyncEngine(), mockFileConnector, connectedSystem, runProfile, TestUtilities.CreateTestWorkerTask(activity, InitiatedBy), new CancellationTokenSource());
        await importProcessor.PerformImportAsync();

        var referencingUser = SyncRepo.ConnectedSystemObjects.Values.Single(cso =>
            cso.AttributeValues.Any(av => av.GuidValue == referencingUserAnchor));
        var manager = referencingUser.AttributeValues.Single(av => av.Attribute.Name == MockSourceSystemAttributeNames.MANAGER.ToString());
        using (Assert.EnterMultipleScope())
        {
            Assert.That(manager.ReferenceValue, Is.Not.Null);
            Assert.That(manager.ReferenceValue!.Type.Name, Is.EqualTo("SOURCE_GROUP"),
                "The attribute declares it references groups, so the decoy user holding the same anchor value must never be chosen.");
        }
    }

    /// <summary>
    /// Ambiguity honours the Warn handling mode: no per-object error, a summary warning on the Activity.
    /// </summary>
    [Test]
    public async Task FullImport_AnAmbiguousReferenceUnderWarnHandling_WarnsTheActivityWithoutErroringTheObjectAsync()
    {
        var sharedAnchorValue = Guid.NewGuid();
        var referencingGroupUid = Guid.NewGuid();

        var mockFileConnector = new MockFileConnector();
        mockFileConnector.TestImportObjects.Add(CreateUserImportObject(sharedAnchorValue, "Ambiguous User"));
        mockFileConnector.TestImportObjects.Add(CreateGroupImportObject(sharedAnchorValue, "Ambiguous Group"));
        mockFileConnector.TestImportObjects.Add(CreateGroupImportObject(referencingGroupUid, "Referencing Group", sharedAnchorValue.ToString()));

        var connectedSystem = await Jim.ConnectedSystems.GetConnectedSystemAsync(1);
        Assert.That(connectedSystem, Is.Not.Null);
        connectedSystem!.UnresolvedReferenceHandling = UnresolvedReferenceHandling.Warn;

        var activity = ActivitiesData.First();
        var runProfile = ConnectedSystemRunProfilesData.Single(q => q.ConnectedSystemId == connectedSystem!.Id && q.RunType == ConnectedSystemRunType.FullImport);
        var importProcessor = new SyncImportTaskProcessor(Jim, SyncRepo, new SyncServer(Jim), new SyncEngine(), mockFileConnector, connectedSystem, runProfile, TestUtilities.CreateTestWorkerTask(activity, InitiatedBy), new CancellationTokenSource());
        await importProcessor.PerformImportAsync();

        var errorItem = activity.RunProfileExecutionItems.FirstOrDefault(item => item.ErrorType == ActivityRunProfileExecutionItemErrorType.UnresolvedReference);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(errorItem, Is.Null, "Warn mode must not mark the Run Profile Execution Item as errored.");
            Assert.That(activity.WarningMessage, Does.Contain("more than one Object Type"),
                "Warn mode carries the ambiguity as an Activity warning, so it is worth a glance without reading as a failure.");
        }
    }

    /// <summary>
    /// Ambiguity honours the Ignore handling mode: no per-object error and no Activity warning.
    /// </summary>
    [Test]
    public async Task FullImport_AnAmbiguousReferenceUnderIgnoreHandling_IsSilentAsync()
    {
        var sharedAnchorValue = Guid.NewGuid();
        var referencingGroupUid = Guid.NewGuid();

        var mockFileConnector = new MockFileConnector();
        mockFileConnector.TestImportObjects.Add(CreateUserImportObject(sharedAnchorValue, "Ambiguous User"));
        mockFileConnector.TestImportObjects.Add(CreateGroupImportObject(sharedAnchorValue, "Ambiguous Group"));
        mockFileConnector.TestImportObjects.Add(CreateGroupImportObject(referencingGroupUid, "Referencing Group", sharedAnchorValue.ToString()));

        var connectedSystem = await Jim.ConnectedSystems.GetConnectedSystemAsync(1);
        Assert.That(connectedSystem, Is.Not.Null);
        connectedSystem!.UnresolvedReferenceHandling = UnresolvedReferenceHandling.Ignore;

        var activity = ActivitiesData.First();
        var runProfile = ConnectedSystemRunProfilesData.Single(q => q.ConnectedSystemId == connectedSystem!.Id && q.RunType == ConnectedSystemRunType.FullImport);
        var importProcessor = new SyncImportTaskProcessor(Jim, SyncRepo, new SyncServer(Jim), new SyncEngine(), mockFileConnector, connectedSystem, runProfile, TestUtilities.CreateTestWorkerTask(activity, InitiatedBy), new CancellationTokenSource());
        await importProcessor.PerformImportAsync();

        var errorItem = activity.RunProfileExecutionItems.FirstOrDefault(item => item.ErrorType == ActivityRunProfileExecutionItemErrorType.UnresolvedReference);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(errorItem, Is.Null, "Ignore mode must not mark the Run Profile Execution Item as errored.");
            Assert.That(activity.WarningMessage, Is.Null.Or.Empty, "Ignore mode must not set the Activity warning message.");
        }
    }

    /// <summary>
    /// References to objects that already exist in JIM but are not part of the run's batches (the Delta Import
    /// norm: the referenced object did not change) resolve through the database fallback. Before #1285 the
    /// fallback batched every unresolved value onto the first item's anchor attribute, guessed the referencer's
    /// own type, and matched stored StringValue only, so a typed-anchor (Guid, int) reference held by a second
    /// Object Type could not resolve.
    /// </summary>
    [Test]
    public async Task DeltaImport_FallbackReferencesHeldByTwoObjectTypes_BothResolveAsync()
    {
        var existingUser1Anchor = Guid.NewGuid();
        var existingUser2Anchor = Guid.NewGuid();
        SeedExistingUser(existingUser1Anchor, "Existing User 1");
        SeedExistingUser(existingUser2Anchor, "Existing User 2");

        var importedUserAnchor = Guid.NewGuid();
        var importedGroupUid = Guid.NewGuid();
        var mockFileConnector = new MockFileConnector();
        // A user whose MANAGER references an existing user, and a group whose MEMBER references another:
        // two referencing Object Types with different anchor attributes in one fallback batch.
        mockFileConnector.TestImportObjects.Add(CreateUserImportObject(importedUserAnchor, "Imported User", managerRef: existingUser1Anchor.ToString(), changeType: ObjectChangeType.Added));
        mockFileConnector.TestImportObjects.Add(CreateGroupImportObject(importedGroupUid, "Imported Group", ObjectChangeType.Added, existingUser2Anchor.ToString()));

        await RunDeltaImportAsync(mockFileConnector, declareMemberTargetsUsers: true);

        var importedUser = SyncRepo.ConnectedSystemObjects.Values.Single(cso =>
            cso.AttributeValues.Any(av => av.GuidValue == importedUserAnchor));
        var importedGroup = SyncRepo.ConnectedSystemObjects.Values.Single(cso =>
            cso.AttributeValues.Any(av => av.GuidValue == importedGroupUid));
        var manager = importedUser.AttributeValues.Single(av => av.Attribute.Name == MockSourceSystemAttributeNames.MANAGER.ToString());
        var member = importedGroup.AttributeValues.Single(av => av.Attribute.Name == MockSourceSystemAttributeNames.MEMBER.ToString());
        using (Assert.EnterMultipleScope())
        {
            Assert.That(manager.ReferenceValue, Is.Not.Null, "The user's MANAGER reference must resolve to the existing user via the database fallback.");
            Assert.That(member.ReferenceValue, Is.Not.Null,
                "The group's MEMBER reference must resolve too: the fallback must query per referenced Object Type's anchor attribute, not batch everything onto the first item's.");
        }
    }

    /// <summary>
    /// The other half of the partitioning: a duplicate anchor within ONE Object Type is a genuine data error
    /// and still fails fast, and the exception now names what collided so an administrator can act on it.
    /// </summary>
    [Test]
    public void BuildExternalIdLookups_AnIntraTypeDuplicateAnchor_ThrowsNamingTheObjectTypeAttributeAndValue()
    {
        var userObjectType = ConnectedSystemObjectTypesData.Single(t => t.Name == "SOURCE_USER");
        var duplicatedValue = Guid.NewGuid();
        var first = CreateCsoWithGuidAnchor(userObjectType, duplicatedValue);
        var second = CreateCsoWithGuidAnchor(userObjectType, duplicatedValue);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            SyncImportTaskProcessor.BuildExternalIdLookups([first, second], []));

        Assert.That(exception!.Message, Does.Contain("SOURCE_USER").And.Contain("HR_ID").And.Contain(duplicatedValue.ToString()),
            "The failure must name the Object Type, the anchor attribute and the value; an administrator cannot act on a zero Guid.");
    }

    /// <summary>
    /// The partitioning seam itself: the same value on two Object Types builds without complaint.
    /// </summary>
    [Test]
    public void BuildExternalIdLookups_TwoObjectTypesSharingAValue_PartitionsThemByType()
    {
        var userObjectType = ConnectedSystemObjectTypesData.Single(t => t.Name == "SOURCE_USER");
        var groupObjectType = ConnectedSystemObjectTypesData.Single(t => t.Name == "SOURCE_GROUP");
        var sharedValue = Guid.NewGuid();
        var user = CreateCsoWithGuidAnchor(userObjectType, sharedValue);
        var group = CreateCsoWithGuidAnchor(groupObjectType, sharedValue);

        Assert.That(() => SyncImportTaskProcessor.BuildExternalIdLookups([user, group], []), Throws.Nothing,
            "Two Object Types sharing an anchor value space is normal (a view over a table); only an intra-type duplicate is a data error.");
    }

    #region helpers

    /// <summary>
    /// Runs a Full Import for Connected System 1 with the supplied mock connector, optionally first declaring
    /// SOURCE_GROUP's MEMBER attribute as referencing the SOURCE_USER Object Type (what the SQL Connector's
    /// schema document states via referencesObjectType).
    /// </summary>
    private async Task<(ConnectedSystem ConnectedSystem, Activity Activity)> RunFullImportAsync(
        MockFileConnector mockFileConnector, bool declareMemberTargetsUsers = false)
    {
        var connectedSystem = await Jim.ConnectedSystems.GetConnectedSystemAsync(1);
        Assert.That(connectedSystem, Is.Not.Null);

        if (declareMemberTargetsUsers)
            DeclareMemberTargetsUsers(connectedSystem!);

        var activity = ActivitiesData.First();
        var runProfile = ConnectedSystemRunProfilesData.Single(q => q.ConnectedSystemId == connectedSystem!.Id && q.RunType == ConnectedSystemRunType.FullImport);
        var importProcessor = new SyncImportTaskProcessor(Jim, SyncRepo, new SyncServer(Jim), new SyncEngine(), mockFileConnector, connectedSystem!, runProfile, TestUtilities.CreateTestWorkerTask(activity, InitiatedBy), new CancellationTokenSource());
        await importProcessor.PerformImportAsync();

        return (connectedSystem!, activity);
    }

    /// <summary>
    /// Runs a Delta Import for Connected System 1, applying the same optional MEMBER target declaration as
    /// <see cref="RunFullImportAsync"/>.
    /// </summary>
    private async Task<(ConnectedSystem ConnectedSystem, Activity Activity)> RunDeltaImportAsync(
        MockFileConnector mockFileConnector, bool declareMemberTargetsUsers = false)
    {
        var connectedSystem = await Jim.ConnectedSystems.GetConnectedSystemAsync(1);
        Assert.That(connectedSystem, Is.Not.Null);

        if (declareMemberTargetsUsers)
            DeclareMemberTargetsUsers(connectedSystem!);

        var deltaRunProfile = new ConnectedSystemRunProfile
        {
            Id = 999,
            Name = "Delta Import Test",
            RunType = ConnectedSystemRunType.DeltaImport,
            ConnectedSystemId = connectedSystem!.Id
        };
        var activity = new Activity { Id = Guid.NewGuid(), RunProfileExecutionItems = new List<ActivityRunProfileExecutionItem>() };
        var importProcessor = new SyncImportTaskProcessor(Jim, SyncRepo, new SyncServer(Jim), new SyncEngine(), mockFileConnector, connectedSystem, deltaRunProfile, TestUtilities.CreateTestWorkerTask(activity, InitiatedBy), new CancellationTokenSource());
        await importProcessor.PerformImportAsync();

        return (connectedSystem, activity);
    }

    /// <summary>
    /// Declares SOURCE_GROUP's MEMBER attribute as referencing the SOURCE_USER Object Type (what the SQL
    /// Connector's schema document states via referencesObjectType).
    /// </summary>
    private static void DeclareMemberTargetsUsers(ConnectedSystem connectedSystem)
    {
        var userType = connectedSystem!.ObjectTypes!.Single(t => t.Name == "SOURCE_USER");
        var groupType = connectedSystem!.ObjectTypes!.Single(t => t.Name == "SOURCE_GROUP");
        var memberAttribute = groupType.Attributes.Single(a => a.Name == MockSourceSystemAttributeNames.MEMBER.ToString());
        memberAttribute.ReferencedObjectTypeId = userType.Id;
        memberAttribute.ReferencedObjectType = userType;
    }

    private static ConnectedSystemImportObject CreateUserImportObject(Guid hrId, string displayName, string? managerRef = null, ObjectChangeType changeType = ObjectChangeType.NotSet)
    {
        var importObject = new ConnectedSystemImportObject
        {
            ChangeType = changeType,
            ObjectType = "SOURCE_USER",
            Attributes =
            [
                new ConnectedSystemImportObjectAttribute
                {
                    Name = MockSourceSystemAttributeNames.HR_ID.ToString(),
                    GuidValues = [hrId],
                    Type = AttributeDataType.Guid
                },
                new ConnectedSystemImportObjectAttribute
                {
                    Name = MockSourceSystemAttributeNames.DISPLAY_NAME.ToString(),
                    StringValues = [displayName],
                    Type = AttributeDataType.Text
                }
            ]
        };

        if (managerRef != null)
        {
            importObject.Attributes.Add(new ConnectedSystemImportObjectAttribute
            {
                Name = MockSourceSystemAttributeNames.MANAGER.ToString(),
                ReferenceValues = [managerRef],
                Type = AttributeDataType.Reference
            });
        }

        return importObject;
    }

    private static ConnectedSystemImportObject CreateGroupImportObject(Guid groupUid, string displayName, params string[] memberRefs) =>
        CreateGroupImportObject(groupUid, displayName, ObjectChangeType.NotSet, memberRefs);

    private static ConnectedSystemImportObject CreateGroupImportObject(Guid groupUid, string displayName, ObjectChangeType changeType, params string[] memberRefs)
    {
        return new ConnectedSystemImportObject
        {
            ChangeType = changeType,
            ObjectType = "SOURCE_GROUP",
            Attributes =
            [
                new ConnectedSystemImportObjectAttribute
                {
                    Name = MockSourceSystemAttributeNames.GROUP_UID.ToString(),
                    GuidValues = [groupUid],
                    Type = AttributeDataType.Guid
                },
                new ConnectedSystemImportObjectAttribute
                {
                    Name = MockSourceSystemAttributeNames.DISPLAY_NAME.ToString(),
                    StringValues = [displayName],
                    Type = AttributeDataType.Text
                },
                new ConnectedSystemImportObjectAttribute
                {
                    Name = MockSourceSystemAttributeNames.MEMBER.ToString(),
                    ReferenceValues = [.. memberRefs],
                    Type = AttributeDataType.Reference
                }
            ]
        };
    }

    /// <summary>
    /// Seeds a persisted SOURCE_USER Connected System Object into the sync repository, so a reference to it
    /// can only resolve through the database fallback (it is in no import batch).
    /// </summary>
    private void SeedExistingUser(Guid hrId, string displayName)
    {
        var userObjectType = ConnectedSystemObjectTypesData.Single(t => t.Name == "SOURCE_USER");
        var cso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = 1,
            Type = userObjectType,
            TypeId = userObjectType.Id,
            ExternalIdAttributeId = (int)MockSourceSystemAttributeNames.HR_ID
        };
        cso.AttributeValues =
        [
            new ConnectedSystemObjectAttributeValue
            {
                Id = Guid.NewGuid(),
                GuidValue = hrId,
                Attribute = userObjectType.Attributes.Single(a => a.Name == MockSourceSystemAttributeNames.HR_ID.ToString()),
                AttributeId = (int)MockSourceSystemAttributeNames.HR_ID,
                ConnectedSystemObject = cso
            },
            new ConnectedSystemObjectAttributeValue
            {
                Id = Guid.NewGuid(),
                StringValue = displayName,
                Attribute = userObjectType.Attributes.Single(a => a.Name == MockSourceSystemAttributeNames.DISPLAY_NAME.ToString()),
                AttributeId = (int)MockSourceSystemAttributeNames.DISPLAY_NAME,
                ConnectedSystemObject = cso
            }
        ];
        SyncRepo.SeedConnectedSystemObject(cso);
    }

    private static ConnectedSystemObject CreateCsoWithGuidAnchor(ConnectedSystemObjectType objectType, Guid anchorValue)
    {
        var anchorAttribute = objectType.Attributes.Single(a => a.IsExternalId);
        var cso = new ConnectedSystemObject
        {
            ConnectedSystemId = 1,
            Type = objectType,
            TypeId = objectType.Id,
            ExternalIdAttributeId = anchorAttribute.Id
        };
        cso.AttributeValues =
        [
            new ConnectedSystemObjectAttributeValue
            {
                GuidValue = anchorValue,
                Attribute = anchorAttribute,
                AttributeId = anchorAttribute.Id,
                ConnectedSystemObject = cso
            }
        ];
        return cso;
    }

    #endregion
}
