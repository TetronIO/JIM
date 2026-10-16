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
/// A schema refresh can redefine an attribute whose Connected System Objects already hold values (#2128: Active
/// Directory's Enumeration attributes, such as <c>sAMAccountType</c>, were discovered as Boolean and are Number). The
/// refresh keeps the attribute, so the values it held stay stored under the old type. The next import that carries the
/// attribute says what it holds, so what it held under the old type must go: left beside the value read under the new
/// type, a single-valued attribute holds two values, and every single-valued Attribute Flow from it fails on every
/// synchronisation of every such object. These run real Full Imports against one in-memory repository, so each starts
/// from what the previous one left.
/// </summary>
[TestFixture]
public class ImportAttributeDataTypeChangeTests
{
    private static readonly string HrId = MockSourceSystemAttributeNames.HR_ID.ToString();
    private static readonly string DisplayName = MockSourceSystemAttributeNames.DISPLAY_NAME.ToString();
    private static readonly string Leaver = MockSourceSystemAttributeNames.LEAVER.ToString();
    private static readonly string CourseCount = MockSourceSystemAttributeNames.COURSE_COUNT.ToString();

    private MetaverseObject _initiatedBy = null!;
    private List<ConnectedSystemRunProfile> _runProfiles = null!;
    private List<ConnectedSystemObjectType> _objectTypes = null!;
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
        _objectTypes = TestUtilities.GetConnectedSystemObjectTypeData();
        var partitions = TestUtilities.GetConnectedSystemPartitionData();
        var fullImportRunProfile = _runProfiles[0];
        var activities = TestUtilities.GetActivityData(fullImportRunProfile.RunType, fullImportRunProfile.Id);
        var serviceSettings = TestUtilities.GetServiceSettingsData();

        var mockDbContext = new Mock<JimDbContext>();
        TestUtilities.SetUpEmptyConnectedSystemGraphMocks(mockDbContext);
        mockDbContext.Setup(m => m.Activities).Returns(activities.BuildMockDbSet().Object);
        mockDbContext.Setup(m => m.ConnectedSystems).Returns(connectedSystems.BuildMockDbSet().Object);
        mockDbContext.Setup(m => m.ConnectedSystemObjectTypes).Returns(_objectTypes.BuildMockDbSet().Object);
        mockDbContext.Setup(m => m.ConnectedSystemRunProfiles).Returns(_runProfiles.BuildMockDbSet().Object);
        mockDbContext.Setup(m => m.ConnectedSystemPartitions).Returns(partitions.BuildMockDbSet().Object);
        mockDbContext.Setup(m => m.ServiceSettingItems).Returns(serviceSettings.BuildMockDbSet().Object);
        mockDbContext.Setup(m => m.PendingExports).Returns(new List<PendingExport>().BuildMockDbSet().Object);
        mockDbContext.Setup(m => m.ConnectedSystemObjects).Returns(new List<ConnectedSystemObject>().BuildMockDbSet().Object);

        _syncRepo = TestUtilities.CreateSyncRepository(activity: activities.First());
        _jim = new JimApplication(new PostgresDataRepository(mockDbContext.Object), syncRepository: _syncRepo);
    }

    [Test]
    public async Task FullImport_AttributeRedefinedFromBooleanToNumber_ReplacesTheValueHeldAsABooleanAsync()
    {
        // The Connector read 805306368 as a Boolean, which came out false, and JIM stored that.
        var hrId = Guid.NewGuid();
        await RunFullImportAsync(Person(hrId, new ConnectedSystemImportObjectAttribute { Name = Leaver, BoolValues = { false } }));

        RedefineAttribute(Leaver, AttributeDataType.Number);
        var activity = await RunFullImportAsync(Person(hrId, new ConnectedSystemImportObjectAttribute { Name = Leaver, IntValues = [805306368] }));

        var stored = StoredValues(hrId, Leaver);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(stored.Select(v => v.IntValue), Is.EqualTo(new int?[] { 805306368 }),
                "The attribute holds the one value the source holds, read as its new type.");
            Assert.That(stored.Where(v => v.BoolValue.HasValue), Is.Empty,
                "The value held under the old type would otherwise sit beside the new one for good.");
            Assert.That(activity.RunProfileExecutionItems.Single().ErrorType, Is.EqualTo(ActivityRunProfileExecutionItemErrorType.NotSet),
                "Removing a value held under the old type is not a corrupt value, so the change history records it.");
        }
    }

    [Test]
    public async Task FullImport_AttributeRedefinedToASingleValuedType_ReplacesEveryValueHeldUnderTheOldTypeAsync()
    {
        // A Boolean and a date are compared as the attribute's one value, so two values held under the old type
        // must not reach that comparison; it would fail the object rather than replace them.
        var hrId = Guid.NewGuid();
        await RunFullImportAsync(Person(hrId, new ConnectedSystemImportObjectAttribute { Name = CourseCount, IntValues = [1, 2] }));

        RedefineAttribute(CourseCount, AttributeDataType.Boolean);
        var activity = await RunFullImportAsync(Person(hrId, new ConnectedSystemImportObjectAttribute { Name = CourseCount, BoolValues = { true } }));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(activity.RunProfileExecutionItems.Select(i => i.ErrorType),
                Has.None.EqualTo(ActivityRunProfileExecutionItemErrorType.UnhandledError));
            Assert.That(StoredValues(hrId, CourseCount).Select(v => (v.BoolValue, v.IntValue)),
                Is.EqualTo(new (bool?, int?)[] { (true, null) }));
        }
    }

    [Test]
    public async Task FullImport_AttributeRedefinedButUnreadableThisTime_KeepsWhatItHeldAsync()
    {
        // An unreadable value is not a statement of what the attribute holds (#2066), so it removes nothing, whatever
        // column what JIM held is in.
        var hrId = Guid.NewGuid();
        await RunFullImportAsync(Person(hrId, new ConnectedSystemImportObjectAttribute { Name = Leaver, BoolValues = { false } }));

        RedefineAttribute(Leaver, AttributeDataType.Number);
        var unreadable = Person(hrId);
        unreadable.AddAttributeValueError(Leaver, "eight", "Not a valid whole number.");
        await RunFullImportAsync(unreadable);

        Assert.That(StoredValues(hrId, Leaver).Select(v => v.BoolValue), Is.EqualTo(new bool?[] { false }));
    }

    #region Helpers

    private void RedefineAttribute(string attributeName, AttributeDataType type)
    {
        // What a schema refresh does: the attribute keeps its id and its stored values, and only its type changes.
        foreach (var attribute in _objectTypes.SelectMany(t => t.Attributes).Where(a => a.Name == attributeName))
            attribute.Type = type;
        foreach (var attribute in _syncRepo.ObjectTypes.Values.SelectMany(t => t.Attributes).Where(a => a.Name == attributeName))
            attribute.Type = type;
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

    private static ConnectedSystemImportObject Person(Guid hrId, params ConnectedSystemImportObjectAttribute[] attributes)
    {
        var all = new List<ConnectedSystemImportObjectAttribute>
        {
            new() { Name = HrId, GuidValues = [hrId] },
            new() { Name = DisplayName, StringValues = ["Sam Okafor"] }
        };
        all.AddRange(attributes);

        return new ConnectedSystemImportObject
        {
            ChangeType = ObjectChangeType.NotSet,
            ObjectType = "SOURCE_USER",
            Attributes = all
        };
    }

    private List<ConnectedSystemObjectAttributeValue> StoredValues(Guid hrId, string attributeName) =>
        _syncRepo.ConnectedSystemObjects.Values
            .Single(cso => cso.AttributeValues.Any(av => av.GuidValue == hrId))
            .AttributeValues.Where(av => av.Attribute?.Name == attributeName).ToList();

    #endregion
}
