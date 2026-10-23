// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.History;
using JIM.PostgresData;
using JIM.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Real-PostgreSQL verification of the Metaverse Object change search
/// (<see cref="JIM.Application.Servers.MetaverseServer.SearchMetaverseObjectChangesRangeAsync"/>) behind the Object
/// Changes page, its REST endpoint and its cmdlet. The query matches typed values through nested collection
/// predicates and ILIKE, which the in-memory provider cannot execute faithfully.
/// </summary>
/// <remarks>
/// Opt-in via the <c>JIM_TEST_RESET_*</c> environment variables; ignored when <c>JIM_TEST_RESET_DB</c> is absent.
/// </remarks>
[TestFixture]
[Category("RequiresPostgres")]
public class MetaverseObjectChangeSearchDatabaseTests
{
    private static readonly DateTime BaseTime = new(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc);

    private string _connectionString = null!;
    private Seed _seed = null!;

    private sealed class Seed
    {
        public int PersonTypeId;
        public int GroupTypeId;
        public int StatusId;
        public int DepartmentId;
        public int ManagerId;
        public int EmployeeNumberId;
        public int StartDateId;
        public Guid AliceId;
        public Guid CarolId;
        public Guid ActivityId;

        // Change ids, named for what each records.
        public Guid AliceDisabledAndLeaver;
        public Guid BobDisabled;
        public Guid AliceReEnabled;
        public Guid GroupDisabled;
        public Guid BobManagerAndNumber;
        public Guid CarolDisabled;
        public Guid CarolDeleted;
        public Guid AliceStarted;
    }

    private JimDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<JimDbContext>()
            .UseNpgsql(_connectionString)
            .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        return new JimDbContext(options);
    }

    private JimApplication NewJim() => new(new PostgresDataRepository(NewContext()));

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        var dbName = Environment.GetEnvironmentVariable("JIM_TEST_RESET_DB");
        if (string.IsNullOrEmpty(dbName))
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL Metaverse Object change search tests.");

        var host = Environment.GetEnvironmentVariable("JIM_TEST_RESET_HOST") ?? "localhost";
        var user = Environment.GetEnvironmentVariable("JIM_TEST_RESET_USER") ?? "postgres";
        var pass = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PASSWORD") ?? "postgres";
        var port = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PORT") ?? "5432";
        _connectionString = $"Host={host};Port={port};Database={dbName};Username={user};Password={pass}";

        using var ctx = NewContext();
        ctx.Database.Migrate();
    }

    [SetUp]
    public async Task SetUp()
    {
        await PostgresTestDatabase.ResetAsync(_connectionString);
        _seed = await SeedAsync();
    }

    /// <summary>
    /// Seeds two Person objects (Alice, Bob), a Group (Engineers) and a since-deleted Person (Carol), with changes
    /// one minute apart in the order the <see cref="Seed"/> fields list them.
    /// </summary>
    private async Task<Seed> SeedAsync()
    {
        await using var ctx = NewContext();
        var seed = new Seed();

        var person = new MetaverseObjectType { Name = "Person", PluralName = "People", BuiltIn = true };
        var group = new MetaverseObjectType { Name = "Group", PluralName = "Groups", BuiltIn = true };
        var status = Attribute("Employee Status", AttributeDataType.Text);
        var department = Attribute("Department", AttributeDataType.Text);
        var manager = Attribute("Manager", AttributeDataType.Reference);
        var employeeNumber = Attribute("Employee Number", AttributeDataType.Number);
        var startDate = Attribute("Start Date", AttributeDataType.DateTime);
        ctx.MetaverseObjectTypes.AddRange(person, group);
        ctx.MetaverseAttributes.AddRange(status, department, manager, employeeNumber, startDate);
        await ctx.SaveChangesAsync();

        var alice = new MetaverseObject { Id = Guid.NewGuid(), Type = person, CachedDisplayName = "Alice" };
        var bob = new MetaverseObject { Id = Guid.NewGuid(), Type = person, CachedDisplayName = "Bob" };
        var engineers = new MetaverseObject { Id = Guid.NewGuid(), Type = group, CachedDisplayName = "Engineers" };
        ctx.MetaverseObjects.AddRange(alice, bob, engineers);

        var activity = new Activity
        {
            Id = Guid.NewGuid(),
            TargetType = ActivityTargetType.ConnectedSystemRunProfile,
            TargetOperationType = ActivityTargetOperationType.Execute,
            Status = ActivityStatus.Complete,
            InitiatedByType = ActivityInitiatorType.System,
            InitiatedByName = "System",
            Executed = BaseTime
        };
        var rpei = new ActivityRunProfileExecutionItem { Id = Guid.NewGuid(), Activity = activity, ObjectChangeType = ObjectChangeType.AttributeFlow };
        ctx.Activities.Add(activity);
        ctx.ActivityRunProfileExecutionItems.Add(rpei);

        var carolId = Guid.NewGuid();
        var minute = 0;
        MetaverseObjectChange Change(MetaverseObject? mvo) => new()
        {
            Id = Guid.NewGuid(),
            MetaverseObject = mvo,
            ChangeType = ObjectChangeType.AttributeFlow,
            ChangeTime = BaseTime.AddMinutes(++minute)
        };

        var c1 = Change(alice);
        Text(c1, status, ValueChangeType.Add, "Disabled");
        Text(c1, status, ValueChangeType.Remove, "Active");
        Text(c1, department, ValueChangeType.Add, "Leavers");
        Text(c1, department, ValueChangeType.Remove, "Finance");

        var c2 = Change(bob);
        c2.ActivityRunProfileExecutionItem = rpei;
        Text(c2, status, ValueChangeType.Add, "Disabled");
        Text(c2, status, ValueChangeType.Remove, "Active");

        var c3 = Change(alice);
        Text(c3, status, ValueChangeType.Add, "Active");
        Text(c3, status, ValueChangeType.Remove, "Disabled");

        var c4 = Change(engineers);
        Text(c4, status, ValueChangeType.Add, "Disabled");

        var c5 = Change(bob);
        Value(c5, manager, new MetaverseObjectChangeAttributeValue { ValueChangeType = ValueChangeType.Add, ReferenceValueId = alice.Id });
        Value(c5, employeeNumber, new MetaverseObjectChangeAttributeValue { ValueChangeType = ValueChangeType.Add, IntValue = 42 });

        // Carol's earlier change was re-keyed when she was deleted: no live object, only her preserved id.
        var c6 = Change(null);
        c6.DeletedMetaverseObjectId = carolId;
        Text(c6, status, ValueChangeType.Add, "Disabled");

        var c7 = Change(null);
        c7.ChangeType = ObjectChangeType.Deleted;
        c7.DeletedMetaverseObjectId = carolId;
        c7.DeletedObjectTypeId = person.Id;
        c7.DeletedObjectDisplayName = "Carol";
        Text(c7, status, ValueChangeType.Remove, "Disabled");

        var c8 = Change(alice);
        Value(c8, startDate, new MetaverseObjectChangeAttributeValue
        {
            ValueChangeType = ValueChangeType.Add,
            DateTimeValue = new DateTime(2026, 10, 5, 14, 30, 0, DateTimeKind.Utc)
        });

        ctx.MetaverseObjectChanges.AddRange(c1, c2, c3, c4, c5, c6, c7, c8);
        await ctx.SaveChangesAsync();

        seed.PersonTypeId = person.Id;
        seed.GroupTypeId = group.Id;
        seed.StatusId = status.Id;
        seed.DepartmentId = department.Id;
        seed.ManagerId = manager.Id;
        seed.EmployeeNumberId = employeeNumber.Id;
        seed.StartDateId = startDate.Id;
        seed.AliceId = alice.Id;
        seed.CarolId = carolId;
        seed.ActivityId = activity.Id;
        seed.AliceDisabledAndLeaver = c1.Id;
        seed.BobDisabled = c2.Id;
        seed.AliceReEnabled = c3.Id;
        seed.GroupDisabled = c4.Id;
        seed.BobManagerAndNumber = c5.Id;
        seed.CarolDisabled = c6.Id;
        seed.CarolDeleted = c7.Id;
        seed.AliceStarted = c8.Id;
        return seed;
    }

    private static MetaverseAttribute Attribute(string name, AttributeDataType type) =>
        new() { Name = name, Type = type, AttributePlurality = AttributePlurality.SingleValued, BuiltIn = true };

    private static void Text(MetaverseObjectChange change, MetaverseAttribute attribute, ValueChangeType valueChangeType, string text) =>
        Value(change, attribute, new MetaverseObjectChangeAttributeValue { ValueChangeType = valueChangeType, StringValue = text });

    private static void Value(MetaverseObjectChange change, MetaverseAttribute attribute, MetaverseObjectChangeAttributeValue value)
    {
        var attributeChange = change.AttributeChanges.SingleOrDefault(ac => ac.Attribute == attribute);
        if (attributeChange == null)
        {
            attributeChange = new MetaverseObjectChangeAttribute
            {
                Id = Guid.NewGuid(),
                MetaverseObjectChange = change,
                Attribute = attribute,
                AttributeName = attribute.Name,
                AttributeType = attribute.Type
            };
            change.AttributeChanges.Add(attributeChange);
        }

        value.Id = Guid.NewGuid();
        value.MetaverseObjectChangeAttribute = attributeChange;
        attributeChange.ValueChanges.Add(value);
    }

    private async Task<List<Guid>> SearchAsync(MetaverseObjectChangeSearchCriteria criteria)
    {
        var result = await NewJim().Metaverse.SearchMetaverseObjectChangesRangeAsync(criteria, offset: 0, count: 100);
        Assert.That(result.TotalResults, Is.EqualTo(result.Results.Count), "the total should count the same match set as the window");
        return result.Results.Select(r => r.Change.Id).ToList();
    }

    private MetaverseObjectChangeSearchCriteria Condition(int attributeId, AttributeChangeOperator op, string? value) => new()
    {
        AttributeConditions = [new MetaverseObjectChangeAttributeCondition { AttributeId = attributeId, Operator = op, Value = value }]
    };

    [Test]
    public async Task Search_NoCriteria_ReturnsEveryChangeNewestFirstAsync()
    {
        var ids = await SearchAsync(new MetaverseObjectChangeSearchCriteria());

        Assert.That(ids, Is.EqualTo(new[]
        {
            _seed.AliceStarted, _seed.CarolDeleted, _seed.CarolDisabled, _seed.BobManagerAndNumber,
            _seed.GroupDisabled, _seed.AliceReEnabled, _seed.BobDisabled, _seed.AliceDisabledAndLeaver
        }));
    }

    [Test]
    public async Task Search_Window_ReturnsSliceAndFullTotalAsync()
    {
        var result = await NewJim().Metaverse.SearchMetaverseObjectChangesRangeAsync(new MetaverseObjectChangeSearchCriteria(), offset: 2, count: 2);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.TotalResults, Is.EqualTo(8));
            Assert.That(result.Results.Select(r => r.Change.Id), Is.EqualTo(new[] { _seed.CarolDisabled, _seed.BobManagerAndNumber }));
        }
    }

    [Test]
    public async Task Search_TotalNotRequested_ReturnsNullTotalAsync()
    {
        var result = await NewJim().Metaverse.SearchMetaverseObjectChangesRangeAsync(
            new MetaverseObjectChangeSearchCriteria(), offset: 0, count: 2, includeTotalCount: false);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.TotalResults, Is.Null);
            Assert.That(result.Results, Has.Count.EqualTo(2));
        }
    }

    [Test]
    public async Task Search_ChangedTo_MatchesOnlyAddedValuesAsync()
    {
        var ids = await SearchAsync(Condition(_seed.StatusId, AttributeChangeOperator.ChangedTo, "Disabled"));

        Assert.That(ids, Is.EqualTo(new[] { _seed.CarolDisabled, _seed.GroupDisabled, _seed.BobDisabled, _seed.AliceDisabledAndLeaver }));
    }

    [Test]
    public async Task Search_ChangedFrom_MatchesOnlyRemovedValuesAsync()
    {
        var ids = await SearchAsync(Condition(_seed.StatusId, AttributeChangeOperator.ChangedFrom, "Disabled"));

        Assert.That(ids, Is.EqualTo(new[] { _seed.CarolDeleted, _seed.AliceReEnabled }));
    }

    [Test]
    public async Task Search_AnyChangeWithoutValue_MatchesEveryChangeToTheAttributeAsync()
    {
        var ids = await SearchAsync(Condition(_seed.DepartmentId, AttributeChangeOperator.AnyChange, null));

        Assert.That(ids, Is.EqualTo(new[] { _seed.AliceDisabledAndLeaver }));
    }

    [Test]
    public async Task Search_TextValue_MatchesCaseInsensitivelyAndWholeValueOnlyAsync()
    {
        var exact = await SearchAsync(Condition(_seed.StatusId, AttributeChangeOperator.ChangedTo, "disabled"));
        var partial = await SearchAsync(Condition(_seed.StatusId, AttributeChangeOperator.ChangedTo, "Disab"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(exact, Has.Count.EqualTo(4));
            Assert.That(partial, Is.Empty);
        }
    }

    [Test]
    public async Task Search_TextWildcard_MatchesAnyRunOfCharactersAsync()
    {
        var ids = await SearchAsync(Condition(_seed.StatusId, AttributeChangeOperator.ChangedTo, "Dis*"));

        Assert.That(ids, Has.Count.EqualTo(4));
    }

    [Test]
    public async Task Search_ObjectType_IncludesDeletedObjectsOfThatTypeAndExcludesOthersAsync()
    {
        var criteria = Condition(_seed.StatusId, AttributeChangeOperator.ChangedTo, "Disabled");
        criteria.ObjectTypeId = _seed.PersonTypeId;

        var ids = await SearchAsync(criteria);

        Assert.That(ids, Is.EqualTo(new[] { _seed.CarolDisabled, _seed.BobDisabled, _seed.AliceDisabledAndLeaver }));
    }

    [Test]
    public async Task Search_AllConditions_RequiresEveryConditionInTheSameChangeAsync()
    {
        var criteria = new MetaverseObjectChangeSearchCriteria
        {
            MatchMode = AttributeConditionMatchMode.All,
            AttributeConditions =
            [
                new() { AttributeId = _seed.StatusId, Operator = AttributeChangeOperator.ChangedTo, Value = "Disabled" },
                new() { AttributeId = _seed.DepartmentId, Operator = AttributeChangeOperator.ChangedTo, Value = "Leavers" }
            ]
        };

        var ids = await SearchAsync(criteria);

        Assert.That(ids, Is.EqualTo(new[] { _seed.AliceDisabledAndLeaver }));
    }

    [Test]
    public async Task Search_AnyCondition_MatchesChangesMeetingEitherAsync()
    {
        var criteria = new MetaverseObjectChangeSearchCriteria
        {
            MatchMode = AttributeConditionMatchMode.Any,
            AttributeConditions =
            [
                new() { AttributeId = _seed.DepartmentId, Operator = AttributeChangeOperator.ChangedTo, Value = "Leavers" },
                new() { AttributeId = _seed.EmployeeNumberId, Operator = AttributeChangeOperator.ChangedTo, Value = "42" }
            ]
        };

        var ids = await SearchAsync(criteria);

        Assert.That(ids, Is.EqualTo(new[] { _seed.BobManagerAndNumber, _seed.AliceDisabledAndLeaver }));
    }

    [Test]
    public async Task Search_ReferenceValue_MatchesTheReferencedObjectsDisplayNameAsync()
    {
        var exact = await SearchAsync(Condition(_seed.ManagerId, AttributeChangeOperator.ChangedTo, "alice"));
        var wildcard = await SearchAsync(Condition(_seed.ManagerId, AttributeChangeOperator.ChangedTo, "Al*"));
        var other = await SearchAsync(Condition(_seed.ManagerId, AttributeChangeOperator.ChangedTo, "Bob"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(exact, Is.EqualTo(new[] { _seed.BobManagerAndNumber }));
            Assert.That(wildcard, Is.EqualTo(new[] { _seed.BobManagerAndNumber }));
            Assert.That(other, Is.Empty);
        }
    }

    [Test]
    public async Task Search_NumberValue_MatchesNumericallyAsync()
    {
        var ids = await SearchAsync(Condition(_seed.EmployeeNumberId, AttributeChangeOperator.AnyChange, "42"));

        Assert.That(ids, Is.EqualTo(new[] { _seed.BobManagerAndNumber }));
    }

    [Test]
    public async Task Search_DateValue_MatchesTheWholeDayAsync()
    {
        var match = await SearchAsync(Condition(_seed.StartDateId, AttributeChangeOperator.ChangedTo, "2026-10-05"));
        var otherDay = await SearchAsync(Condition(_seed.StartDateId, AttributeChangeOperator.ChangedTo, "2026-10-06"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(match, Is.EqualTo(new[] { _seed.AliceStarted }));
            Assert.That(otherDay, Is.Empty);
        }
    }

    [Test]
    public async Task Search_TimeRange_IsInclusiveAtBothEndsAsync()
    {
        var ids = await SearchAsync(new MetaverseObjectChangeSearchCriteria
        {
            FromDate = BaseTime.AddMinutes(2),
            ToDate = BaseTime.AddMinutes(4)
        });

        Assert.That(ids, Is.EqualTo(new[] { _seed.GroupDisabled, _seed.AliceReEnabled, _seed.BobDisabled }));
    }

    [Test]
    public async Task Search_DisplayName_MatchesLiveAndDeletedObjectsAsync()
    {
        var carol = await SearchAsync(new MetaverseObjectChangeSearchCriteria { DisplayNameSearch = "car" });
        var bob = await SearchAsync(new MetaverseObjectChangeSearchCriteria { DisplayNameSearch = "BO" });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(carol, Is.EqualTo(new[] { _seed.CarolDeleted, _seed.CarolDisabled }));
            Assert.That(bob, Is.EqualTo(new[] { _seed.BobManagerAndNumber, _seed.BobDisabled }));
        }
    }

    [Test]
    public async Task Search_Results_DescribeTheObjectAndActivityAsync()
    {
        var result = await NewJim().Metaverse.SearchMetaverseObjectChangesRangeAsync(
            Condition(_seed.StatusId, AttributeChangeOperator.ChangedTo, "Disabled"), offset: 0, count: 100);
        var carol = result.Results.Single(r => r.Change.Id == _seed.CarolDisabled);
        var bob = result.Results.Single(r => r.Change.Id == _seed.BobDisabled);
        var alice = result.Results.Single(r => r.Change.Id == _seed.AliceDisabledAndLeaver);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(carol.MetaverseObjectDeleted, Is.True);
            Assert.That(carol.MetaverseObjectId, Is.EqualTo(_seed.CarolId));
            Assert.That(carol.MetaverseObjectDisplayName, Is.EqualTo("Carol"));
            Assert.That(carol.MetaverseObjectTypeName, Is.EqualTo("Person"));
            Assert.That(bob.MetaverseObjectDeleted, Is.False);
            Assert.That(bob.MetaverseObjectDisplayName, Is.EqualTo("Bob"));
            Assert.That(bob.MetaverseObjectTypeId, Is.EqualTo(_seed.PersonTypeId));
            Assert.That(bob.ActivityId, Is.EqualTo(_seed.ActivityId));
            Assert.That(alice.ActivityId, Is.Null);
            Assert.That(alice.MatchedAttributeIds, Is.EqualTo(new[] { _seed.StatusId }));
            Assert.That(alice.Change.AttributeChanges.Select(ac => ac.AttributeName), Is.EquivalentTo(new[] { "Department", "Employee Status" }));
        }
    }

    [Test]
    public async Task Search_ReferenceChange_CarriesTheReferencedObjectsNameAsync()
    {
        var result = await NewJim().Metaverse.SearchMetaverseObjectChangesRangeAsync(
            Condition(_seed.ManagerId, AttributeChangeOperator.AnyChange, null), offset: 0, count: 100);

        var manager = result.Results.Single().Change.AttributeChanges.Single(ac => ac.AttributeName == "Manager");
        Assert.That(manager.ValueChanges.Single().ReferenceValue!.DisplayName, Is.EqualTo("Alice"));
    }

    [Test]
    public void Search_UnknownAttribute_IsRefusedWithAReasonAsync()
    {
        var ex = Assert.ThrowsAsync<ArgumentException>(() => NewJim().Metaverse.SearchMetaverseObjectChangesRangeAsync(
            Condition(999_999, AttributeChangeOperator.AnyChange, null), offset: 0, count: 10));

        Assert.That(ex!.Message, Does.Contain("999999"));
    }

    [Test]
    public void Search_ValueNotOfTheAttributesType_IsRefusedNamingTheAttributeAsync()
    {
        var ex = Assert.ThrowsAsync<ArgumentException>(() => NewJim().Metaverse.SearchMetaverseObjectChangesRangeAsync(
            Condition(_seed.EmployeeNumberId, AttributeChangeOperator.ChangedTo, "forty-two"), offset: 0, count: 10));

        Assert.That(ex!.Message, Does.Contain("Employee Number").And.Contain("forty-two"));
    }
}
