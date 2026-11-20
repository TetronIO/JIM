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
/// An imported object rejected before a Connected System Object existed for it has no change record, so before #874 its
/// item carried only an error message and the administrator could not see what the Connector had returned. These run
/// real Full Imports and check that every such item, whichever check rejected it, carries a snapshot of the object, and
/// that no other item does.
/// </summary>
[TestFixture]
public class ImportRejectedObjectSnapshotTests
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
    public async Task FullImport_DuplicateAttributes_RecordsBothCopiesOfTheAttributeAsync()
    {
        var person = Person(Guid.NewGuid(), "Priya Shah");
        person.Attributes.Add(new ConnectedSystemImportObjectAttribute { Name = DisplayName.ToLowerInvariant(), StringValues = ["Priya S."] });

        var item = await ImportOneAsync(person);

        var snapshot = ImportedObjectSnapshot.FromJson(item.ImportedObjectSnapshotJson);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(item.ErrorType, Is.EqualTo(ActivityRunProfileExecutionItemErrorType.DuplicateImportedAttributes));
            Assert.That(snapshot, Is.Not.Null);
            Assert.That(snapshot!.ObjectType, Is.EqualTo("SOURCE_USER"));
            Assert.That(snapshot!.Attributes.Where(a => a.Name.Equals(DisplayName, StringComparison.OrdinalIgnoreCase)).SelectMany(a => a.Values),
                Is.EqualTo(new[] { "Priya Shah", "Priya S." }));
            Assert.That(snapshot!.Attributes.Where(a => a.NamedInError == true).Select(a => a.Name),
                Is.EqualTo(new[] { DisplayName, DisplayName.ToLowerInvariant() }), "Both copies are what the error is about.");
        }
    }

    [Test]
    public async Task FullImport_UnknownObjectType_RecordsTheObjectAsync()
    {
        var person = Person(Guid.NewGuid(), "Priya Shah");
        person.ObjectType = "CONTRACTOR";

        var item = await ImportOneAsync(person);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(item.ErrorType, Is.EqualTo(ActivityRunProfileExecutionItemErrorType.CouldNotMatchObjectType));
            Assert.That(ImportedObjectSnapshot.FromJson(item.ImportedObjectSnapshotJson)?.ObjectType, Is.EqualTo("CONTRACTOR"));
        }
    }

    [Test]
    public async Task FullImport_ConnectorReportsAnObjectLevelError_RecordsTheObjectAsync()
    {
        var person = Person(Guid.NewGuid(), "Priya Shah");
        person.ErrorType = ConnectedSystemImportObjectError.ExternalIdAttributes;
        person.ErrorMessage = "The row has no HR_ID.";

        var item = await ImportOneAsync(person);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(item.ErrorType, Is.EqualTo(ActivityRunProfileExecutionItemErrorType.MissingExternalIdAttributeValue));
            Assert.That(ImportedObjectSnapshot.FromJson(item.ImportedObjectSnapshotJson)?.Attributes.Select(a => a.Name),
                Is.EqualTo(new[] { HrId, DisplayName }));
        }
    }

    [Test]
    public async Task FullImport_SameExternalIdTwiceOnOnePage_RecordsEveryCopyAsync()
    {
        var hrId = Guid.NewGuid();

        var activity = await RunFullImportAsync(Person(hrId, "Priya Shah"), Person(hrId, "Priya Shah-Okafor"));

        var items = activity.RunProfileExecutionItems.Where(i => i.ErrorType == ActivityRunProfileExecutionItemErrorType.DuplicateObject).ToList();
        Assert.That(items.Select(i => ImportedObjectSnapshot.FromJson(i.ImportedObjectSnapshotJson)?.Attributes.Single(a => a.Name == DisplayName).Values.Single()),
            Is.EquivalentTo(new[] { "Priya Shah", "Priya Shah-Okafor" }),
            "Seeing both copies side by side is what lets the administrator tell which one the source should not hold.");
    }

    [Test]
    public async Task FullImport_UnexpectedAttribute_RecordsTheObjectWithTheCredentialValueWithheldAsync()
    {
        var person = Person(Guid.NewGuid(), "Priya Shah");
        person.Attributes.Add(new ConnectedSystemImportObjectAttribute { Name = "initialPassword", StringValues = ["Hunter2!"] });

        var item = await ImportOneAsync(person);

        var snapshot = ImportedObjectSnapshot.FromJson(item.ImportedObjectSnapshotJson);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(item.ErrorType, Is.EqualTo(ActivityRunProfileExecutionItemErrorType.UnexpectedAttribute));
            Assert.That(snapshot?.Attributes.Single(a => a.Name == "initialPassword").Withheld, Is.True);
            Assert.That(snapshot?.Attributes.Single(a => a.Name == "initialPassword").NamedInError, Is.True,
                "The attribute the schema does not hold is what the error is about.");
            Assert.That(item.ImportedObjectSnapshotJson, Does.Not.Contain("Hunter2"));
        }
    }

    [Test]
    public async Task FullImport_ObjectImported_RecordsNoSnapshotAsync()
    {
        var joiner = Person(Guid.NewGuid(), "Priya Shah");
        joiner.AddAttributeValueError(MockSourceSystemAttributeNames.START_DATE.ToString(), "31/02/2026", "Not a valid date and time.");

        var item = await ImportOneAsync(joiner);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(item.ErrorType, Is.EqualTo(ActivityRunProfileExecutionItemErrorType.ImportAttributeValueError),
                "An error on an object JIM still imported: its Connected System Object and change record show what came in.");
            Assert.That(item.ImportedObjectSnapshotJson, Is.Null);
        }
    }

    [Test]
    public async Task FullImport_MoreRejectedObjectsThanTheRunLimit_RecordsTheLimitAndMarksTheRestAsync()
    {
        var rejected = Enumerable.Range(0, ImportedObjectSnapshot.MaxSnapshotsPerRun + 2).Select(i =>
        {
            var person = Person(Guid.NewGuid(), $"Person {i}");
            person.ObjectType = "CONTRACTOR";
            return person;
        }).ToArray();

        var activity = await RunFullImportAsync(rejected);

        var snapshots = activity.RunProfileExecutionItems.Select(i => ImportedObjectSnapshot.FromJson(i.ImportedObjectSnapshotJson)).ToList();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(snapshots, Has.All.Not.Null);
            Assert.That(snapshots.Count(s => s!.RunLimitReached != true), Is.EqualTo(ImportedObjectSnapshot.MaxSnapshotsPerRun));
            Assert.That(snapshots.Count(s => s!.RunLimitReached == true), Is.EqualTo(2));
        }
    }

    #region Helpers

    private async Task<ActivityRunProfileExecutionItem> ImportOneAsync(ConnectedSystemImportObject importObject)
    {
        var activity = await RunFullImportAsync(importObject);
        return activity.RunProfileExecutionItems.Single();
    }

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

    #endregion
}
