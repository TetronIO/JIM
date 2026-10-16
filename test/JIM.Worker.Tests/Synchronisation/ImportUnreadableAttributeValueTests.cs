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
/// A value a Connector found but could not read is not the same as no value (#2066). Before this, the Connector left
/// the attribute out of the object, JIM read the gap as "the source holds nothing", removed the value it held, and
/// synchronisation carried the removal on to the Metaverse and every system the attribute flows to. These run real
/// Full Imports against one in-memory repository, so each starts from what the previous one left, content hash
/// stamps included.
/// </summary>
[TestFixture]
public class ImportUnreadableAttributeValueTests
{
    private static readonly string HrId = MockSourceSystemAttributeNames.HR_ID.ToString();
    private static readonly string DisplayName = MockSourceSystemAttributeNames.DISPLAY_NAME.ToString();
    private static readonly string StartDate = MockSourceSystemAttributeNames.START_DATE.ToString();
    private static readonly string Qualifications = MockSourceSystemAttributeNames.QUALIFICATIONS.ToString();
    private static readonly string ContractedWeeklyHours = MockSourceSystemAttributeNames.CONTRACTED_WEEKLY_HOURS.ToString();
    private static readonly string Leaver = MockSourceSystemAttributeNames.LEAVER.ToString();

    private static readonly DateTime FirstOfMarch = new(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);

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
    public async Task FullImport_ValueUnreadableOnAnExistingObject_KeepsTheStoredValueAsync()
    {
        var hrId = Guid.NewGuid();
        await RunFullImportAsync(Person(hrId, "Sam Okafor", startDate: FirstOfMarch));

        var unreadable = Person(hrId, "Sam Okafor");
        unreadable.AddAttributeValueError(StartDate, "31/02/2026", "Not a valid date and time.");
        await RunFullImportAsync(unreadable);

        Assert.That(StoredValues(hrId, StartDate).Select(v => v.DateTimeValue).ToList(), Is.EqualTo(new List<DateTime?> { FirstOfMarch }),
            "The source still holds a start date, just one that could not be read; removing it would flow a removal everywhere the attribute goes.");
    }

    [Test]
    public async Task FullImport_SeveralValuesReportedForAOneValueAttribute_KeepsTheStoredValueAsync()
    {
        // Guard, not a red test: the LDAP Connector reports every value of a date or Boolean attribute holding more
        // than one (#2153) and leaves the attribute off the object, carrying it only as those reported values. JIM
        // must read that as "leave it as held", for a Boolean as much as for a date.
        var hrId = Guid.NewGuid();
        var held = Person(hrId, "Sam Okafor", startDate: FirstOfMarch);
        held.Attributes.Add(new ConnectedSystemImportObjectAttribute { Name = Leaver, BoolValues = { false } });
        await RunFullImportAsync(held);

        var several = Person(hrId, "Sam Okafor");
        several.AddAttributeValueError(StartDate, "20260301000000Z", "JIM holds one value for a date and time attribute, and the directory returned 2.");
        several.AddAttributeValueError(StartDate, "20260401000000Z", "JIM holds one value for a date and time attribute, and the directory returned 2.");
        several.AddAttributeValueError(Leaver, "TRUE", "JIM holds one value for a true or false attribute, and the directory returned 2.");
        several.AddAttributeValueError(Leaver, "FALSE", "JIM holds one value for a true or false attribute, and the directory returned 2.");
        var activity = await RunFullImportAsync(several);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(StoredValues(hrId, StartDate).Select(v => v.DateTimeValue).ToList(), Is.EqualTo(new List<DateTime?> { FirstOfMarch }));
            Assert.That(StoredValues(hrId, Leaver).Select(v => v.BoolValue).ToList(), Is.EqualTo(new List<bool?> { false }));
            Assert.That(ImportedAttributeValueErrors.FromJson(activity.RunProfileExecutionItems.Single().AttributeValueErrorsJson)?.Total, Is.EqualTo(4),
                "The item lists every value, not one per attribute.");
        }
    }

    [Test]
    public async Task FullImport_SomeValuesOfAnAttributeUnreadable_LeavesTheWholeAttributeAsItWasAsync()
    {
        var hrId = Guid.NewGuid();
        await RunFullImportAsync(Person(hrId, "Sam Okafor", qualifications: ["First Aid", "Fire Warden"]));

        // The Connector read one of the two values now in the source and not the other, so it cannot say which values
        // the attribute holds; adding the one it read and removing the one JIM held would both be guesses.
        var partial = Person(hrId, "Sam Okafor", qualifications: ["First Aid", "Forklift"]);
        partial.AddAttributeValueError(Qualifications, "\u0000\u0001", "Not valid text.");
        await RunFullImportAsync(partial);

        Assert.That(StoredValues(hrId, Qualifications).Select(v => v.StringValue), Is.EquivalentTo(new[] { "First Aid", "Fire Warden" }));
    }

    [Test]
    public async Task FullImport_ValueUnreadableOnANewObject_ImportsTheObjectWithoutThatAttributeAsync()
    {
        var hrId = Guid.NewGuid();
        var joiner = Person(hrId, "Sam Okafor", qualifications: ["First Aid"]);
        joiner.AddAttributeValueError(Qualifications, "\u0000\u0001", "Not valid text.");
        await RunFullImportAsync(joiner);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(StoredValues(hrId, DisplayName).Select(v => v.StringValue), Is.EqualTo(new[] { "Sam Okafor" }),
                "One unreadable value is no reason to withhold a joiner.");
            Assert.That(StoredValues(hrId, Qualifications), Is.Empty,
                "Part of a set read as though it were the whole of it would present a partial object as a complete one.");
        }
    }

    [Test]
    public async Task FullImport_SeveralUnreadableValues_TheItemNamesEveryOneAsync()
    {
        var hrId = Guid.NewGuid();
        var person = Person(hrId, "Sam Okafor");
        person.AddAttributeValueError(StartDate, "31/02/2026", "Not a valid date and time.");
        person.AddAttributeValueError(ContractedWeeklyHours, "37.5h", "Not a valid whole number.");
        var activity = await RunFullImportAsync(person);

        var item = activity.RunProfileExecutionItems.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(item.ErrorType, Is.EqualTo(ActivityRunProfileExecutionItemErrorType.ImportAttributeValueError));
            Assert.That(item.ErrorMessage, Does.Contain(StartDate).And.Contain("31/02/2026"));
            Assert.That(item.ErrorMessage, Does.Contain(ContractedWeeklyHours).And.Contain("37.5h"));
        }
    }

    [Test]
    public async Task FullImport_UnreadableValues_AreRecordedOnTheItemWithTheirTypesAndWhereTheyWereReadAsync()
    {
        var hrId = Guid.NewGuid();
        var person = Person(hrId, "Sam Okafor");
        person.AddAttributeValueError(StartDate, "31/02/2026", "Not a valid date and time.");
        person.AddAttributeValueError(ContractedWeeklyHours, "37.5h", "Not a valid whole number.");
        person.SourceLocation = "hr-users.csv, row 4,182";
        var activity = await RunFullImportAsync(person);

        var item = activity.RunProfileExecutionItems.Single();
        var recorded = ImportedAttributeValueErrors.FromJson(item.AttributeValueErrorsJson);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(recorded?.Total, Is.EqualTo(2));
            Assert.That(recorded?.Values.Select(v => (v.AttributeName, v.AttributeType, v.Value, v.Reason)), Is.EqualTo(new[]
            {
                (StartDate, (AttributeDataType?)AttributeDataType.DateTime, (string?)"31/02/2026", "Not a valid date and time."),
                (ContractedWeeklyHours, (AttributeDataType?)AttributeDataType.Number, (string?)"37.5h", "Not a valid whole number.")
            }), "The type is recorded as it stood, so the item still says what was expected after the schema changes.");
            Assert.That(item.SourceLocation, Is.EqualTo("hr-users.csv, row 4,182"));
        }
    }

    [Test]
    public async Task FullImport_UnreadableExternalId_IsRecordedOnTheItemAsync()
    {
        var unidentifiable = Person(Guid.NewGuid(), "Alex Byrne");
        unidentifiable.Attributes.RemoveAll(a => a.Name == HrId);
        unidentifiable.AddAttributeValueError(HrId, "not-a-guid", "Not a valid GUID.");
        var activity = await RunFullImportAsync(unidentifiable);

        var recorded = ImportedAttributeValueErrors.FromJson(activity.RunProfileExecutionItems.Single().AttributeValueErrorsJson);
        Assert.That(recorded?.Values.Select(v => v.Value), Is.EqualTo(new[] { "not-a-guid" }));
    }

    [Test]
    public async Task FullImport_FlaggedObjectWhoseReadableContentIsUnchanged_IsStillReportedAsync()
    {
        // The first import stamps the object's content hash with a start date genuinely absent. The second carries the
        // same readable content, so its hash matches the stamp, but its start date is now present and unreadable. An
        // object skipped as unchanged gets no execution item, which would drop the error without a word.
        var hrId = Guid.NewGuid();
        await RunFullImportAsync(Person(hrId, "Sam Okafor"));

        var unreadable = Person(hrId, "Sam Okafor");
        unreadable.AddAttributeValueError(StartDate, "31/02/2026", "Not a valid date and time.");
        var activity = await RunFullImportAsync(unreadable);

        Assert.That(activity.RunProfileExecutionItems.Select(i => i.ErrorType),
            Does.Contain(ActivityRunProfileExecutionItemErrorType.ImportAttributeValueError));
    }

    [Test]
    public async Task FullImport_FlaggedObjectWhoseReadableContentIsUnchanged_CountsItUnchangedAndInErrorAsync()
    {
        // A flagged object is always compared in full, never skipped by its content hash; finding its readable content
        // unchanged is not an update (#2129), but the value it could not read is still an error.
        var hrId = Guid.NewGuid();
        await RunFullImportAsync(Person(hrId, "Sam Okafor"));

        var unreadable = Person(hrId, "Sam Okafor");
        unreadable.AddAttributeValueError(StartDate, "31/02/2026", "Not a valid date and time.");
        var activity = await RunFullImportAsync(unreadable);

        Assert.That(activity.Message, Does.Contain("1 object (0 created, 0 updated, 1 unchanged, 1 error)"));
    }

    [Test]
    public async Task FullImport_ValueUnreadableOnANewObject_CountsItCreatedAndInErrorAsync()
    {
        // Guard, not a red test: a joiner with a value the Connector could not read is still created.
        var joiner = Person(Guid.NewGuid(), "Sam Okafor");
        joiner.AddAttributeValueError(StartDate, "31/02/2026", "Not a valid date and time.");
        var activity = await RunFullImportAsync(joiner);

        Assert.That(activity.Message, Does.Contain("1 object (1 created, 0 updated, 1 error)"));
    }

    [Test]
    public async Task FullImport_FlaggedObjectWithAnotherValueChanged_CountsItUpdatedAndInErrorAsync()
    {
        // Guard, not a red test: one object can be both updated and in error.
        var hrId = Guid.NewGuid();
        await RunFullImportAsync(Person(hrId, "Sam Okafor"));

        var unreadable = Person(hrId, "Sam Okafor-Byrne");
        unreadable.AddAttributeValueError(StartDate, "31/02/2026", "Not a valid date and time.");
        var activity = await RunFullImportAsync(unreadable);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(activity.Message, Does.Contain("1 object (0 created, 1 updated, 1 error)"));
            Assert.That(activity.Message, Does.Not.Contain("unchanged"));
        }
    }

    [Test]
    public async Task FullImport_ValueUnreadableWhileAnotherIsRemoved_TheNextImportStillComparesTheObjectAsync()
    {
        // The second import removes a qualification and cannot read the start date, so JIM stores the removal and keeps
        // the date: what it holds now matches neither the first import's content nor the second's. The first import's
        // stamp must not survive that, or the third import, which returns the first's content exactly, would look
        // unchanged and be skipped, and the qualification would never come back.
        var hrId = Guid.NewGuid();
        await RunFullImportAsync(Person(hrId, "Sam Okafor", startDate: FirstOfMarch, qualifications: ["First Aid", "Fire Warden"]));

        var unreadable = Person(hrId, "Sam Okafor", qualifications: ["First Aid"]);
        unreadable.AddAttributeValueError(StartDate, "31/02/2026", "Not a valid date and time.");
        await RunFullImportAsync(unreadable);

        await RunFullImportAsync(Person(hrId, "Sam Okafor", startDate: FirstOfMarch, qualifications: ["First Aid", "Fire Warden"]));

        Assert.That(StoredValues(hrId, Qualifications).Select(v => v.StringValue), Is.EquivalentTo(new[] { "First Aid", "Fire Warden" }));
    }

    [Test]
    public async Task FullImport_ExternalIdUnreadable_NoObjectOfThatTypeIsMarkedDeletedAsync()
    {
        // An object whose External ID cannot be read cannot be matched to the object JIM holds for it, so deletion
        // detection would find that object missing from the import and mark it deleted, although the source still holds
        // it. JIM cannot tell which object it was, so it holds back deletion detection for the Object Type and says why.
        var samId = Guid.NewGuid();
        var alexId = Guid.NewGuid();
        await RunFullImportAsync(Person(samId, "Sam Okafor"), Person(alexId, "Alex Byrne"));

        var unidentifiable = Person(alexId, "Alex Byrne");
        unidentifiable.Attributes.RemoveAll(a => a.Name == HrId);
        unidentifiable.AddAttributeValueError(HrId, "not-a-guid", "Not a valid GUID.");
        var activity = await RunFullImportAsync(Person(samId, "Sam Okafor"), unidentifiable);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(StoredObject(alexId).Status, Is.EqualTo(ConnectedSystemObjectStatus.Normal));
            Assert.That(activity.WarningMessage, Does.Contain("SOURCE_USER"),
                "The administrator needs to know deletion detection did not run for the Object Type, and why.");
            Assert.That(activity.RunProfileExecutionItems.Select(i => i.ErrorType),
                Does.Contain(ActivityRunProfileExecutionItemErrorType.ImportAttributeValueError),
                "The item reports the unreadable value, not a missing External ID.");
        }
    }

    [Test]
    public async Task FullImport_ExternalIdUnreadable_AnObjectGenuinelyGoneFromAnotherRunIsStillNotDeletedThisRunAsync()
    {
        // The held-back type is held back whole: JIM cannot tell the unreadable object from the one that is really gone,
        // so both stay until a Full Import can read every External ID again.
        var samId = Guid.NewGuid();
        var alexId = Guid.NewGuid();
        var leaverId = Guid.NewGuid();
        await RunFullImportAsync(Person(samId, "Sam Okafor"), Person(alexId, "Alex Byrne"), Person(leaverId, "Lee Ver"));

        var unidentifiable = Person(alexId, "Alex Byrne");
        unidentifiable.Attributes.RemoveAll(a => a.Name == HrId);
        unidentifiable.AddAttributeValueError(HrId, "not-a-guid", "Not a valid GUID.");
        await RunFullImportAsync(Person(samId, "Sam Okafor"), unidentifiable);

        Assert.That(StoredObject(leaverId).Status, Is.EqualTo(ConnectedSystemObjectStatus.Normal));

        await RunFullImportAsync(Person(samId, "Sam Okafor"), Person(alexId, "Alex Byrne"));

        Assert.That(StoredObject(leaverId).Status, Is.EqualTo(ConnectedSystemObjectStatus.Obsolete),
            "Once every External ID reads again, deletion detection resumes.");
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

    private static ConnectedSystemImportObject Person(Guid hrId, string displayName, DateTime? startDate = null, List<string>? qualifications = null)
    {
        var attributes = new List<ConnectedSystemImportObjectAttribute>
        {
            new() { Name = HrId, GuidValues = [hrId] },
            new() { Name = DisplayName, StringValues = [displayName] }
        };
        if (startDate.HasValue)
            attributes.Add(new ConnectedSystemImportObjectAttribute { Name = StartDate, DateTimeValues = { startDate.Value } });
        if (qualifications != null)
            attributes.Add(new ConnectedSystemImportObjectAttribute { Name = Qualifications, StringValues = qualifications });

        return new ConnectedSystemImportObject
        {
            ChangeType = ObjectChangeType.NotSet,
            ObjectType = "SOURCE_USER",
            Attributes = attributes
        };
    }

    private ConnectedSystemObject StoredObject(Guid hrId) =>
        _syncRepo.ConnectedSystemObjects.Values.Single(cso => cso.AttributeValues.Any(av => av.GuidValue == hrId));

    private List<ConnectedSystemObjectAttributeValue> StoredValues(Guid hrId, string attributeName) =>
        StoredObject(hrId).AttributeValues.Where(av => av.Attribute?.Name == attributeName).ToList();

    #endregion
}
