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
/// #2153 S5: an import carries every date and every true or false value a Connected System holds, and the import diff
/// treats them as sets: one row per value on create, adds and removes by canonical value on update, order never a
/// change, rows that hold nothing swept, and what JIM cannot hold reported rather than chosen. Real Full Imports against
/// one in-memory repository, so each starts from what the previous one left.
/// </summary>
[TestFixture]
public class ImportMultiValuedDateAndBooleanTests
{
    private static readonly string HrId = MockSourceSystemAttributeNames.HR_ID.ToString();
    private static readonly string DisplayName = MockSourceSystemAttributeNames.DISPLAY_NAME.ToString();
    private static readonly string StartDate = MockSourceSystemAttributeNames.START_DATE.ToString();
    private static readonly string Leaver = MockSourceSystemAttributeNames.LEAVER.ToString();

    private static readonly DateTime A = new(2026, 3, 1, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime B = new(2026, 4, 1, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime C = new(2026, 5, 1, 9, 0, 0, DateTimeKind.Utc);

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
    public async Task FullImport_NewObjectWithSeveralDates_CreatesOneRowPerValueAsync()
    {
        var hrId = Guid.NewGuid();

        await RunFullImportAsync(Person(hrId, dates: [A, B, C]));

        Assert.That(StoredValues(hrId, StartDate).Select(v => v.DateTimeValue), Is.EquivalentTo(new DateTime?[] { A, B, C }));
    }

    [Test]
    public async Task FullImport_NewObjectWithAnEmptyDateList_CreatesNoRowAsync()
    {
        var hrId = Guid.NewGuid();
        var person = Person(hrId);
        person.Attributes.Add(new ConnectedSystemImportObjectAttribute { Name = StartDate, DateTimeValues = [] });

        await RunFullImportAsync(person);

        Assert.That(StoredValues(hrId, StartDate), Is.Empty, "A row that holds nothing is never created.");
    }

    [Test]
    public async Task FullImport_DatesChange_AddsAndRemovesByValueAsync()
    {
        var hrId = Guid.NewGuid();
        await RunFullImportAsync(Person(hrId, dates: [A, B]));

        await RunFullImportAsync(Person(hrId, dates: [B, C]));

        Assert.That(StoredValues(hrId, StartDate).Select(v => v.DateTimeValue), Is.EquivalentTo(new DateTime?[] { B, C }));
    }

    [Test]
    public async Task FullImport_SameDatesInAnotherOrder_ChangesNothingAsync()
    {
        var hrId = Guid.NewGuid();
        await RunFullImportAsync(Person(hrId, dates: [A, B, C]));
        var rowsBefore = StoredValues(hrId, StartDate).Select(v => v.Id).ToHashSet();

        await RunFullImportAsync(Person(hrId, dates: [C, A, B, A]));

        Assert.That(StoredValues(hrId, StartDate).Select(v => v.Id), Is.EquivalentTo(rowsBefore), "Order and a repeated value are not changes.");
    }

    [Test]
    public async Task FullImport_SameInstantReadInAnotherKind_ChangesNothingAsync()
    {
        var hrId = Guid.NewGuid();
        await RunFullImportAsync(Person(hrId, dates: [A, B]));
        var rowsBefore = StoredValues(hrId, StartDate).Select(v => v.Id).ToHashSet();

        await RunFullImportAsync(Person(hrId, dates: [A.ToLocalTime(), DateTime.SpecifyKind(B, DateTimeKind.Unspecified)]));

        Assert.That(StoredValues(hrId, StartDate).Select(v => v.Id), Is.EquivalentTo(rowsBefore));
    }

    [Test]
    public async Task FullImport_TwoDatesLessThanAMicrosecondApart_ReportsBothAndKeepsWhatJimHeldAsync()
    {
        var hrId = Guid.NewGuid();
        await RunFullImportAsync(Person(hrId, dates: [A]));

        var activity = await RunFullImportAsync(Person(hrId, displayName: "Sam Okafor-Byrne", dates: [B.AddTicks(1), B.AddTicks(9)]));

        var item = activity.RunProfileExecutionItems.Single();
        var recorded = ImportedAttributeValueErrors.FromJson(item.AttributeValueErrorsJson);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(StoredValues(hrId, StartDate).Select(v => v.DateTimeValue), Is.EqualTo(new DateTime?[] { A }), "The attribute is left as JIM held it.");
            Assert.That(StoredValues(hrId, DisplayName).Select(v => v.StringValue), Is.EqualTo(new[] { "Sam Okafor-Byrne" }), "The object's other attributes import.");
            Assert.That(item.ErrorType, Is.EqualTo(ActivityRunProfileExecutionItemErrorType.ImportAttributeValueError));
            Assert.That(recorded!.Values, Has.Count.EqualTo(2));
            Assert.That(recorded.Values.All(v => v.Source == ImportAttributeValueErrorSource.Jim), Is.True);
            Assert.That(item.ErrorMessage, Does.Contain("import").And.Not.Contain("Couldn't read"),
                "A value JIM cannot hold was read; the message must not say the Connector could not read it.");
        }
    }

    [Test]
    public async Task FullImport_TrueAndFalse_ReportsBothAndKeepsWhatJimHeldAsync()
    {
        var hrId = Guid.NewGuid();
        await RunFullImportAsync(Person(hrId, booleans: [false]));

        var activity = await RunFullImportAsync(Person(hrId, booleans: [true, false]));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(StoredValues(hrId, Leaver).Select(v => v.BoolValue), Is.EqualTo(new bool?[] { false }));
            Assert.That(ImportedAttributeValueErrors.FromJson(activity.RunProfileExecutionItems.Single().AttributeValueErrorsJson)!.Total, Is.EqualTo(2));
        }
    }

    [Test]
    public async Task FullImport_ObjectHoldingTrueAndFalse_ConvergesOnTheOneValueImportedAsync()
    {
        // A Connected System Object can hold both from before the Boolean rule; the next import that diffs it (a changed
        // display name keeps the object from being skipped by its content hash) settles it on the one value imported.
        var hrId = Guid.NewGuid();
        await RunFullImportAsync(Person(hrId, booleans: [true]));
        AddStoredValue(hrId, Leaver, v => v.BoolValue = false);

        await RunFullImportAsync(Person(hrId, displayName: "Sam Okafor-Byrne", booleans: [true]));

        Assert.That(StoredValues(hrId, Leaver).Select(v => v.BoolValue), Is.EqualTo(new bool?[] { true }));
    }

    [Test]
    public async Task FullImport_ValuelessRowOfAnImportedDateAttribute_IsSweptAsync()
    {
        var hrId = Guid.NewGuid();
        await RunFullImportAsync(Person(hrId, dates: [A]));
        AddStoredValue(hrId, StartDate, _ => { });

        await RunFullImportAsync(Person(hrId, displayName: "Sam Okafor-Byrne", dates: [A]));

        Assert.That(StoredValues(hrId, StartDate).Select(v => v.DateTimeValue), Is.EqualTo(new DateTime?[] { A }), "The row that held nothing goes.");
    }

    [Test]
    public async Task FullImport_ValuelessRowOfAnImportedBooleanAttribute_IsSweptAsync()
    {
        var hrId = Guid.NewGuid();
        await RunFullImportAsync(Person(hrId, booleans: [false]));
        AddStoredValue(hrId, Leaver, _ => { });

        await RunFullImportAsync(Person(hrId, displayName: "Sam Okafor-Byrne", booleans: [false]));

        Assert.That(StoredValues(hrId, Leaver).Select(v => v.BoolValue), Is.EqualTo(new bool?[] { false }));
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

    private static ConnectedSystemImportObject Person(Guid hrId, string displayName = "Sam Okafor", List<DateTime>? dates = null, List<bool>? booleans = null)
    {
        var attributes = new List<ConnectedSystemImportObjectAttribute>
        {
            new() { Name = HrId, GuidValues = [hrId] },
            new() { Name = DisplayName, StringValues = [displayName] }
        };
        if (dates != null)
            attributes.Add(new ConnectedSystemImportObjectAttribute { Name = StartDate, DateTimeValues = dates });
        if (booleans != null)
            attributes.Add(new ConnectedSystemImportObjectAttribute { Name = Leaver, BoolValues = booleans });

        return new ConnectedSystemImportObject
        {
            ChangeType = ObjectChangeType.NotSet,
            ObjectType = "SOURCE_USER",
            Attributes = attributes
        };
    }

    private void AddStoredValue(Guid hrId, string attributeName, Action<ConnectedSystemObjectAttributeValue> set)
    {
        var cso = StoredObject(hrId);
        var attribute = cso.Type.Attributes.Single(a => a.Name == attributeName);
        var value = new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            Attribute = attribute,
            AttributeId = attribute.Id,
            ConnectedSystemObject = cso
        };
        set(value);
        cso.AttributeValues.Add(value);
    }

    private ConnectedSystemObject StoredObject(Guid hrId) =>
        _syncRepo.ConnectedSystemObjects.Values.Single(cso => cso.AttributeValues.Any(av => av.GuidValue == hrId));

    private List<ConnectedSystemObjectAttributeValue> StoredValues(Guid hrId, string attributeName) =>
        StoredObject(hrId).AttributeValues.Where(av => av.Attribute?.Name == attributeName).ToList();

    #endregion
}
