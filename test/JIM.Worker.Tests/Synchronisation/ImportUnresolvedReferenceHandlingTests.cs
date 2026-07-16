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
/// Tests for the per-Connected System <see cref="UnresolvedReferenceHandling"/> setting that governs how
/// <see cref="SyncImportTaskProcessor.ResolveReferencesAsync"/> reports references that cannot be resolved to a
/// Connected System Object during import (Error, Warn, Ignore). See PRD_UNRESOLVED_REFERENCE_HANDLING.md.
/// </summary>
public class ImportUnresolvedReferenceHandlingTests
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
    /// Default/Error mode (the pre-existing behaviour): an unresolved reference marks the affected object's
    /// Run Profile Execution Item as an error, using the same message text as before this feature. This test
    /// regression-pins current behaviour and may pass immediately; that is expected, not a red-test failure -
    /// it proves the Error path is byte-for-byte unchanged now that the other two modes exist as alternatives.
    /// </summary>
    [Test]
    public async Task ResolveReferencesAsync_DefaultErrorMode_MarksRpeiAsUnresolvedReferenceErrorAsync()
    {
        var unresolvedMemberRef = Guid.NewGuid().ToString();
        var mockFileConnector = new MockFileConnector();
        mockFileConnector.TestImportObjects.Add(CreateGroupImportObject(unresolvedMemberRef));

        var connectedSystem = await Jim.ConnectedSystems.GetConnectedSystemAsync(1);
        Assert.That(connectedSystem, Is.Not.Null);
        Assert.That(connectedSystem!.UnresolvedReferenceHandling, Is.EqualTo(UnresolvedReferenceHandling.Error),
            "Expected the default Connected System setting to be Error.");

        var activity = ActivitiesData.First();
        var runProfile = ConnectedSystemRunProfilesData.Single(q => q.ConnectedSystemId == connectedSystem!.Id && q.RunType == ConnectedSystemRunType.FullImport);
        var importProcessor = new SyncImportTaskProcessor(Jim, SyncRepo, new SyncServer(Jim), new JIM.Application.Servers.SyncEngine(), mockFileConnector, connectedSystem, runProfile, TestUtilities.CreateTestWorkerTask(activity, InitiatedBy), new CancellationTokenSource());
        await importProcessor.PerformImportAsync();

        var errorItem = activity.RunProfileExecutionItems.FirstOrDefault(item => item.ErrorType == ActivityRunProfileExecutionItemErrorType.UnresolvedReference);
        Assert.That(errorItem, Is.Not.Null, "Expected an error item for the unresolved reference.");
        Assert.That(errorItem!.ErrorMessage, Does.Contain(unresolvedMemberRef), "Expected the error message to mention the unresolved reference value.");
        Assert.That(errorItem!.ErrorMessage, Does.Contain("Container Scope"), "Expected the existing error message text to be preserved.");
        Assert.That(activity.WarningMessage, Is.Null.Or.Empty, "Error mode must not set the Activity warning message.");
    }

    /// <summary>
    /// Warn mode: the Run Profile Execution Item is NOT marked as errored, and the Activity's WarningMessage
    /// carries a count-bearing summary so the Activity still completes with a warning status.
    /// </summary>
    [Test]
    public async Task ResolveReferencesAsync_WarnMode_LeavesRpeiUnerroredAndSetsActivityWarningAsync()
    {
        var unresolvedMemberRef = Guid.NewGuid().ToString();
        var mockFileConnector = new MockFileConnector();
        mockFileConnector.TestImportObjects.Add(CreateGroupImportObject(unresolvedMemberRef));

        var connectedSystem = await Jim.ConnectedSystems.GetConnectedSystemAsync(1);
        Assert.That(connectedSystem, Is.Not.Null);
        connectedSystem!.UnresolvedReferenceHandling = UnresolvedReferenceHandling.Warn;

        var activity = ActivitiesData.First();
        var runProfile = ConnectedSystemRunProfilesData.Single(q => q.ConnectedSystemId == connectedSystem!.Id && q.RunType == ConnectedSystemRunType.FullImport);
        var importProcessor = new SyncImportTaskProcessor(Jim, SyncRepo, new SyncServer(Jim), new JIM.Application.Servers.SyncEngine(), mockFileConnector, connectedSystem, runProfile, TestUtilities.CreateTestWorkerTask(activity, InitiatedBy), new CancellationTokenSource());
        await importProcessor.PerformImportAsync();

        var errorItem = activity.RunProfileExecutionItems.FirstOrDefault(item => item.ErrorType == ActivityRunProfileExecutionItemErrorType.UnresolvedReference);
        Assert.That(errorItem, Is.Null, "Warn mode must not mark the Run Profile Execution Item as errored.");

        Assert.That(activity.WarningMessage, Is.Not.Null.And.Not.Empty, "Expected the Activity warning message to be set.");
        Assert.That(activity.WarningMessage, Does.Contain("1 reference value"), "Expected the warning message to include the unresolved reference count.");
        Assert.That(activity.WarningMessage, Does.Contain("Container Scope"), "Expected the warning message to explain the likely cause.");
    }

    /// <summary>
    /// Warn mode with a pre-existing connector warning already recorded on the Activity: the unresolved
    /// reference summary must be appended on a new line, preserving the original connector warning text.
    /// </summary>
    [Test]
    public async Task ResolveReferencesAsync_WarnModeWithExistingConnectorWarning_AppendsSummaryAsync()
    {
        const string existingConnectorWarning = "Delta import watermark unavailable; fell back to full import.";

        var unresolvedMemberRef = Guid.NewGuid().ToString();
        var mockFileConnector = new MockFileConnector();
        mockFileConnector.TestImportObjects.Add(CreateGroupImportObject(unresolvedMemberRef));

        var connectedSystem = await Jim.ConnectedSystems.GetConnectedSystemAsync(1);
        Assert.That(connectedSystem, Is.Not.Null);
        connectedSystem!.UnresolvedReferenceHandling = UnresolvedReferenceHandling.Warn;

        var activity = ActivitiesData.First();
        activity.WarningMessage = existingConnectorWarning;

        var runProfile = ConnectedSystemRunProfilesData.Single(q => q.ConnectedSystemId == connectedSystem!.Id && q.RunType == ConnectedSystemRunType.FullImport);
        var importProcessor = new SyncImportTaskProcessor(Jim, SyncRepo, new SyncServer(Jim), new JIM.Application.Servers.SyncEngine(), mockFileConnector, connectedSystem, runProfile, TestUtilities.CreateTestWorkerTask(activity, InitiatedBy), new CancellationTokenSource());
        await importProcessor.PerformImportAsync();

        Assert.That(activity.WarningMessage, Does.StartWith(existingConnectorWarning), "Expected the original connector warning to be preserved.");
        Assert.That(activity.WarningMessage, Does.Contain("1 reference value"), "Expected the unresolved reference summary to be appended.");
        Assert.That(activity.WarningMessage!.IndexOf('\n'), Is.GreaterThan(-1), "Expected the summary to be appended on a new line.");
    }

    /// <summary>
    /// Ignore mode: the Run Profile Execution Item is NOT marked as errored and the Activity's WarningMessage
    /// is left untouched, so the import completes without warning noise. The unresolved value is still
    /// recorded on the Connected System Object itself (existing behaviour, unaffected by this setting).
    /// </summary>
    [Test]
    public async Task ResolveReferencesAsync_IgnoreMode_LeavesRpeiUnerroredAndActivityWarningUnsetAsync()
    {
        var unresolvedMemberRef = Guid.NewGuid().ToString();
        var mockFileConnector = new MockFileConnector();
        mockFileConnector.TestImportObjects.Add(CreateGroupImportObject(unresolvedMemberRef));

        var connectedSystem = await Jim.ConnectedSystems.GetConnectedSystemAsync(1);
        Assert.That(connectedSystem, Is.Not.Null);
        connectedSystem!.UnresolvedReferenceHandling = UnresolvedReferenceHandling.Ignore;

        var activity = ActivitiesData.First();
        var runProfile = ConnectedSystemRunProfilesData.Single(q => q.ConnectedSystemId == connectedSystem!.Id && q.RunType == ConnectedSystemRunType.FullImport);
        var importProcessor = new SyncImportTaskProcessor(Jim, SyncRepo, new SyncServer(Jim), new JIM.Application.Servers.SyncEngine(), mockFileConnector, connectedSystem, runProfile, TestUtilities.CreateTestWorkerTask(activity, InitiatedBy), new CancellationTokenSource());
        await importProcessor.PerformImportAsync();

        var errorItem = activity.RunProfileExecutionItems.FirstOrDefault(item => item.ErrorType == ActivityRunProfileExecutionItemErrorType.UnresolvedReference);
        Assert.That(errorItem, Is.Null, "Ignore mode must not mark the Run Profile Execution Item as errored.");
        Assert.That(activity.WarningMessage, Is.Null.Or.Empty, "Ignore mode must not set the Activity warning message.");
    }

    #region helpers

    /// <summary>
    /// Builds a SOURCE_GROUP import object with a single multi-valued MEMBER reference. Passing a value that
    /// does not match any Connected System Object's external ID (in this batch or the store) leaves the
    /// reference unresolved, exercising Phase 3 of <see cref="SyncImportTaskProcessor.ResolveReferencesAsync"/>.
    /// </summary>
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

    #endregion
}
