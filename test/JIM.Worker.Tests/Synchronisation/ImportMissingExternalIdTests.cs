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
using MockQueryable.Moq;
using Moq;
using SyncRepository = JIM.InMemoryData.SyncRepository;

namespace JIM.Worker.Tests.Synchronisation;

/// <summary>
/// An imported object with no External ID value cannot be told apart from any other (#3013). Before this, an import into
/// an empty Connected System created a Connected System Object for it with no External ID and counted it a success, and
/// an import into one holding objects reported it as an unhandled error. These run real Full Imports against one
/// in-memory repository, so each starts from what the previous one left.
/// </summary>
[TestFixture]
public class ImportMissingExternalIdTests
{
    private static readonly string HrId = MockSourceSystemAttributeNames.HR_ID.ToString();
    private static readonly string DisplayName = MockSourceSystemAttributeNames.DISPLAY_NAME.ToString();

    private MetaverseObject _initiatedBy = null!;
    private List<ConnectedSystemRunProfile> _runProfiles = null!;
    private JimApplication _jim = null!;
    private SyncRepository _syncRepo = null!;

    [TearDown]
    public void TearDown() => _jim?.Dispose();

    [SetUp]
    public void Setup()
    {
        TestUtilities.SetEnvironmentVariables();
        _initiatedBy = TestUtilities.GetInitiatedBy();

        var connectedSystems = TestUtilities.GetConnectedSystemData();
        _runProfiles = TestUtilities.GetConnectedSystemRunProfileData();
        var objectTypes = TestUtilities.GetConnectedSystemObjectTypeData();
        var partitions = TestUtilities.GetConnectedSystemPartitionData();
        var fullImportRunProfile = _runProfiles[0];
        var activities = TestUtilities.GetActivityData(fullImportRunProfile.RunType, fullImportRunProfile.Id);
        var serviceSettings = TestUtilities.GetServiceSettingsData();

        var mockDbContext = new Mock<JimDbContext>();
        TestUtilities.SetUpEmptyConnectedSystemGraphMocks(mockDbContext);
        mockDbContext.Setup(m => m.Activities).Returns(activities.BuildMockDbSet().Object);
        mockDbContext.Setup(m => m.ConnectedSystems).Returns(connectedSystems.BuildMockDbSet().Object);
        mockDbContext.Setup(m => m.ConnectedSystemObjectTypes).Returns(objectTypes.BuildMockDbSet().Object);
        mockDbContext.Setup(m => m.ConnectedSystemRunProfiles).Returns(_runProfiles.BuildMockDbSet().Object);
        mockDbContext.Setup(m => m.ConnectedSystemPartitions).Returns(partitions.BuildMockDbSet().Object);
        mockDbContext.Setup(m => m.ServiceSettingItems).Returns(serviceSettings.BuildMockDbSet().Object);
        mockDbContext.Setup(m => m.PendingExports).Returns(new List<PendingExport>().BuildMockDbSet().Object);
        mockDbContext.Setup(m => m.ConnectedSystemObjects).Returns(new List<ConnectedSystemObject>().BuildMockDbSet().Object);

        _syncRepo = TestUtilities.CreateSyncRepository(activity: activities.First());
        _jim = new JimApplication(new PostgresDataRepository(mockDbContext.Object), syncRepository: _syncRepo);
    }

    [Test]
    public async Task FullImport_EmptyConnectedSystem_ObjectWithNoExternalIdIsRejectedAndNotCreatedAsync()
    {
        var activity = await RunFullImportAsync(Person(Guid.NewGuid(), "Sam Okafor"), WithoutExternalId("Jordan Ellis"));

        var rejected = activity.RunProfileExecutionItems.Single(i => i.ErrorType != null && i.ErrorType != ActivityRunProfileExecutionItemErrorType.NotSet);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(rejected.ErrorType, Is.EqualTo(ActivityRunProfileExecutionItemErrorType.MissingExternalIdAttributeValue));
            Assert.That(rejected.ErrorMessage, Does.Contain(HrId), "The message names the attribute the object should have carried.");
            Assert.That(StoredDisplayNames(), Is.EqualTo(new[] { "Sam Okafor" }),
                "An object JIM cannot identify must not reach the Connector Space: nothing could ever match it again.");
            Assert.That(ImportedObjectSnapshot.FromJson(rejected.ImportedObjectSnapshotJson)?.Attributes.Select(a => a.Name),
                Is.EqualTo(new[] { DisplayName }), "The item shows what the Connector did return.");
        }
    }

    [Test]
    public async Task FullImport_ExternalIdAttributeWithNoValue_IsRejectedAsync()
    {
        var empty = WithoutExternalId("Jordan Ellis");
        empty.Attributes.Insert(0, new ConnectedSystemImportObjectAttribute { Name = HrId, GuidValues = [] });

        var activity = await RunFullImportAsync(empty);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(activity.RunProfileExecutionItems.Single().ErrorType, Is.EqualTo(ActivityRunProfileExecutionItemErrorType.MissingExternalIdAttributeValue));
            Assert.That(StoredDisplayNames(), Is.Empty);
        }
    }

    [Test]
    public async Task FullImport_ConnectedSystemHoldingObjects_ObjectWithNoExternalIdIsReportedAsMissingNotUnhandledAsync()
    {
        var samId = Guid.NewGuid();
        await RunFullImportAsync(Person(samId, "Sam Okafor"));

        var activity = await RunFullImportAsync(Person(samId, "Sam Okafor"), WithoutExternalId("Jordan Ellis"));

        Assert.That(activity.RunProfileExecutionItems.Select(i => i.ErrorType),
            Does.Contain(ActivityRunProfileExecutionItemErrorType.MissingExternalIdAttributeValue)
                .And.No.EqualTo(ActivityRunProfileExecutionItemErrorType.UnhandledError));
    }

    [Test]
    public async Task FullImport_ObjectWithNoExternalId_NoObjectOfThatTypeIsMarkedDeletedAsync()
    {
        // The source holds an object JIM cannot identify, which may be one JIM already holds: marking whichever object
        // the import did not see as deleted could remove one the source still has.
        var samId = Guid.NewGuid();
        var alexId = Guid.NewGuid();
        await RunFullImportAsync(Person(samId, "Sam Okafor"), Person(alexId, "Alex Byrne"));

        var activity = await RunFullImportAsync(Person(samId, "Sam Okafor"), WithoutExternalId("Alex Byrne"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(StoredObject(alexId).Status, Is.EqualTo(ConnectedSystemObjectStatus.Normal));
            Assert.That(activity.WarningMessage, Does.Contain("SOURCE_USER").And.Contain("no External ID"),
                "The administrator needs to know deletion detection did not run for the Object Type, and why.");
        }
    }

    [Test]
    public async Task FullImport_EveryObjectHasItsExternalIdAgain_DeletionDetectionResumesAsync()
    {
        var samId = Guid.NewGuid();
        var leaverId = Guid.NewGuid();
        await RunFullImportAsync(Person(samId, "Sam Okafor"), Person(leaverId, "Lee Ver"));
        await RunFullImportAsync(Person(samId, "Sam Okafor"), WithoutExternalId("Jordan Ellis"));

        Assert.That(StoredObject(leaverId).Status, Is.EqualTo(ConnectedSystemObjectStatus.Normal));

        await RunFullImportAsync(Person(samId, "Sam Okafor"));

        Assert.That(StoredObject(leaverId).Status, Is.EqualTo(ConnectedSystemObjectStatus.Obsolete));
    }

    #region Helpers

    private async Task<Activity> RunFullImportAsync(params ConnectedSystemImportObject[] importObjects)
    {
        var connector = new MockFileConnector();
        connector.TestImportObjects.AddRange(importObjects);

        var connectedSystem = await _jim.ConnectedSystems.GetConnectedSystemAsync(1);
        var runProfile = _runProfiles.Single(q => q.ConnectedSystemId == connectedSystem!.Id && q.RunType == ConnectedSystemRunType.FullImport);
        var activity = new Activity
        {
            Id = Guid.NewGuid(),
            RunProfileExecutionItems = new List<ActivityRunProfileExecutionItem>()
        };

        var processor = new SyncImportTaskProcessor(
            _jim, _syncRepo, new SyncServer(_jim), new SyncEngine(),
            connector, connectedSystem!, runProfile,
            TestUtilities.CreateTestWorkerTask(activity, _initiatedBy),
            new CancellationTokenSource());

        await processor.PerformImportAsync();
        return activity;
    }

    private static ConnectedSystemImportObject Person(Guid hrId, string displayName) => new()
    {
        ChangeType = ObjectChangeType.NotSet,
        ObjectType = "SOURCE_USER",
        Attributes =
        [
            new ConnectedSystemImportObjectAttribute { Name = HrId, GuidValues = [hrId] },
            new ConnectedSystemImportObjectAttribute { Name = DisplayName, StringValues = [displayName] }
        ]
    };

    private static ConnectedSystemImportObject WithoutExternalId(string displayName) => new()
    {
        ChangeType = ObjectChangeType.NotSet,
        ObjectType = "SOURCE_USER",
        Attributes = [new ConnectedSystemImportObjectAttribute { Name = DisplayName, StringValues = [displayName] }]
    };

    private ConnectedSystemObject StoredObject(Guid hrId) =>
        _syncRepo.ConnectedSystemObjects.Values.Single(cso => cso.AttributeValues.Any(av => av.GuidValue == hrId));

    private List<string?> StoredDisplayNames() =>
        _syncRepo.ConnectedSystemObjects.Values
            .Select(cso => cso.AttributeValues.FirstOrDefault(av => av.Attribute?.Name == DisplayName)?.StringValue)
            .ToList();

    #endregion
}
