// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Application.Servers;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Staging;
using JIM.PostgresData;
using JIM.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using NUnit.Framework;
using JimActivity = JIM.Models.Activities.Activity;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Every writer of change records gives the rows it inserts time-ordered (version 7) ids (#2669), so each insert lands
/// at the growing end of the change tables' primary keys and of the child tables' indexes on their parent's id, rather
/// than on a random page of each. With random (version 4) ids, a page of 500 deleted people wrote its 5,500 attribute
/// rows and their values to as many cold index pages, which is where a leaver cohort's change record time went.
/// </summary>
/// <remarks>
/// Reads every id in the three Metaverse Object and three Connected System Object change tables back on a connection of
/// the test's own, so a writer that assigns an id the test never sees is still caught. Opt-in via the
/// <c>JIM_TEST_RESET_*</c> environment variables; ignored when <c>JIM_TEST_RESET_DB</c> is absent.
/// </remarks>
[TestFixture]
[Category("RequiresPostgres")]
public class ChangeRecordIdVersionDatabaseTests
{
    private static readonly string[] MvoChangeIdColumns =
    [
        """SELECT "Id" FROM "MetaverseObjectChanges" """,
        """SELECT "Id" FROM "MetaverseObjectChangeAttributes" """,
        """SELECT "MetaverseObjectChangeId" FROM "MetaverseObjectChangeAttributes" """,
        """SELECT "Id" FROM "MetaverseObjectChangeAttributeValues" """,
        """SELECT "MetaverseObjectChangeAttributeId" FROM "MetaverseObjectChangeAttributeValues" """
    ];

    private static readonly string[] CsoChangeIdColumns =
    [
        """SELECT "Id" FROM "ConnectedSystemObjectChanges" """,
        """SELECT "Id" FROM "ConnectedSystemObjectChangeAttributes" """,
        """SELECT "ConnectedSystemChangeId" FROM "ConnectedSystemObjectChangeAttributes" """,
        """SELECT "Id" FROM "ConnectedSystemObjectChangeAttributeValues" """,
        """SELECT "ConnectedSystemObjectChangeAttributeId" FROM "ConnectedSystemObjectChangeAttributeValues" """
    ];

    private string _connectionString = null!;
    private MetaverseObjectType _personType = null!;
    private MetaverseAttribute _displayName = null!;
    private ConnectedSystemObjectTypeAttribute _employeeId = null!;
    private Guid _activityId;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        var connectionString = PostgresTestDatabase.ConnectionStringFromEnvironment();
        if (connectionString == null)
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping the real-PostgreSQL change record id version tests.");
        _connectionString = connectionString!;

        using var ctx = NewContext();
        // The repository sets Npgsql's timestamp switch, as the Worker does, before anything is written.
        _ = new PostgresDataRepository(ctx);
        ctx.Database.Migrate();
    }

    [SetUp]
    public async Task SetUpAsync()
    {
        await PostgresTestDatabase.ResetAsync(_connectionString);

        await using var seed = NewContext();
        var connectorDefinition = new ConnectorDefinition { Name = "Test Connector", BuiltIn = true };
        var system = new ConnectedSystem { Name = "Panoply Directory", ConnectorDefinition = connectorDefinition };
        var type = new ConnectedSystemObjectType { Name = "user", ConnectedSystem = system, Selected = true };
        _employeeId = new ConnectedSystemObjectTypeAttribute
        {
            Name = "employeeId", ConnectedSystemObjectType = type, Type = AttributeDataType.Text, IsExternalId = true, Selected = true
        };
        type.Attributes.Add(_employeeId);
        _personType = new MetaverseObjectType { Name = "Person", PluralName = "People" };
        _displayName = new MetaverseAttribute { Name = "Display Name", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.SingleValued };
        var activity = new JimActivity
        {
            TargetName = "Leaver run",
            TargetType = ActivityTargetType.ConnectedSystemRunProfile,
            InitiatedByType = ActivityInitiatorType.System,
            Created = DateTime.UtcNow
        };
        seed.AddRange(connectorDefinition, system, type, _personType, _displayName, activity);
        await seed.SaveChangesAsync();
        _activityId = activity.Id;
    }

    private JimDbContext NewContext() => new(new DbContextOptionsBuilder<JimDbContext>()
        .UseNpgsql(_connectionString)
        .UseQueryTrackingBehavior(QueryTrackingBehavior.TrackAll)
        .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning, RelationalEventId.MultipleCollectionIncludeWarning))
        .Options);

    [Test]
    public async Task PersistPendingMvoChangesAsync_NewChangesAndAppends_WritesVersion7IdsAsync()
    {
        var appendParent = MvoChange("Appended to");
        await using (var ctx = NewContext())
            await new PostgresDataRepository(ctx).Sync.PersistPendingMvoChangesAsync([appendParent], []);

        // An append keeps its parent's id and gives only the new attribute and value rows theirs.
        var append = new MetaverseObjectChange { Id = appendParent.Id, MetaverseObjectId = appendParent.MetaverseObjectId };
        append.AddAttributeValueChange(DisplayNameValue("Appended"), ValueChangeType.Add);

        await using (var ctx = NewContext())
            await new PostgresDataRepository(ctx).Sync.PersistPendingMvoChangesAsync([MvoChange("New")], [append]);

        // Two change rows (the appended-to one and the new one), three attribute rows and three values.
        await AssertEveryIdIsVersion7Async(MvoChangeIdColumns, expectedRows: 2);
    }

    [Test]
    public async Task CreateMetaverseObjectChangeDirectAsync_Change_WritesVersion7IdsAsync()
    {
        await using (var ctx = NewContext())
            await new PostgresDataRepository(ctx).Metaverse.CreateMetaverseObjectChangeDirectAsync(MvoChange("Direct"));

        await AssertEveryIdIsVersion7Async(MvoChangeIdColumns, expectedRows: 1);
    }

    /// <summary>
    /// The EF Core writer: the provider generates version 7 ids for unset Guid keys, so this holds without JIM's help.
    /// </summary>
    [Test]
    public async Task CreateMetaverseObjectChangeAsync_Change_WritesVersion7IdsAsync()
    {
        await using (var ctx = NewContext())
        {
            var change = MvoChange("Through EF Core");
            // A tracked attribute, so the save does not insert it a second time.
            change.AttributeChanges.Single().Attribute = await ctx.MetaverseAttributes.SingleAsync(a => a.Id == _displayName.Id);
            await new PostgresDataRepository(ctx).Metaverse.CreateMetaverseObjectChangeAsync(change);
        }

        await AssertEveryIdIsVersion7Async(MvoChangeIdColumns, expectedRows: 1);
    }

    [Test]
    public async Task DeleteMetaverseObjectsAsync_SynchronisationDeletion_WritesVersion7DeletionRecordIdsAsync()
    {
        var mvoId = Guid.NewGuid();
        await using (var ctx = NewContext())
        {
            var person = new MetaverseObject { Id = mvoId, Type = await ctx.MetaverseObjectTypes.SingleAsync(t => t.Id == _personType.Id) };
            person.AttributeValues.Add(new MetaverseObjectAttributeValue
            {
                MetaverseObject = person,
                Attribute = await ctx.MetaverseAttributes.SingleAsync(a => a.Id == _displayName.Id),
                StringValue = "Joan Leaver"
            });
            ctx.MetaverseObjects.Add(person);
            await ctx.SaveChangesAsync();
        }

        await using (var ctx = NewContext())
        {
            var repository = new PostgresDataRepository(ctx);
            using var jim = new JimApplication(repository, syncRepository: repository.Sync);
            var person = await ctx.MetaverseObjects
                .Include(m => m.Type)
                .Include(m => m.AttributeValues).ThenInclude(v => v.Attribute)
                .SingleAsync(m => m.Id == mvoId);
            var activity = await ctx.Activities.SingleAsync(a => a.Id == _activityId);
            await new SyncServer(jim).DeleteMetaverseObjectsAsync([(person, person.AttributeValues.ToList())], activity);
        }

        await AssertEveryIdIsVersion7Async(MvoChangeIdColumns, expectedRows: 1);
    }

    [Test]
    public async Task PersistCsoChangesAsync_Change_WritesVersion7IdsAsync()
    {
        await using (var ctx = NewContext())
            await new PostgresDataRepository(ctx).Sync.PersistCsoChangesAsync([CsoChange("E1001")]);

        await AssertEveryIdIsVersion7Async(CsoChangeIdColumns, expectedRows: 1);
    }

    /// <summary>
    /// The execution item writer, which writes the change records its items' outcomes carry (a Pending Export's
    /// snapshot) beside the items themselves.
    /// </summary>
    [Test]
    public async Task BulkInsertRpeisAsync_OutcomeCarryingChange_WritesVersion7ChangeIdsAsync()
    {
        var item = new ActivityRunProfileExecutionItem { ActivityId = _activityId, ObjectChangeType = ObjectChangeType.Updated };
        item.SyncOutcomes.Add(new ActivityRunProfileExecutionItemSyncOutcome
        {
            ActivityRunProfileExecutionItem = item,
            OutcomeType = ActivityRunProfileExecutionItemSyncOutcomeType.PendingExportCreated,
            ConnectedSystemObjectChange = CsoChange("E1002")
        });

        await using (var ctx = NewContext())
            await new PostgresDataRepository(ctx).Sync.BulkInsertRpeisAsync([item]);

        await AssertEveryIdIsVersion7Async(CsoChangeIdColumns, expectedRows: 1);
    }

    /// <summary>
    /// The Activity repository's execution item writer, through EF Core: the provider's version 7 ids again.
    /// </summary>
    [Test]
    public async Task CreateActivityRunProfileExecutionItemsAsync_ItemCarryingChange_WritesVersion7ChangeIdsAsync()
    {
        var change = CsoChange("E1003");
        var item = new ActivityRunProfileExecutionItem
        {
            ActivityId = _activityId, ObjectChangeType = ObjectChangeType.Updated, ConnectedSystemObjectChange = change
        };

        await using (var ctx = NewContext())
            await new PostgresDataRepository(ctx).Activity.CreateActivityRunProfileExecutionItemsAsync([item]);

        await AssertEveryIdIsVersion7Async(CsoChangeIdColumns, expectedRows: 1);
    }

    private MetaverseObjectChange MvoChange(string displayName)
    {
        var change = new MetaverseObjectChange
        {
            MetaverseObjectId = Guid.NewGuid(),
            ChangeType = ObjectChangeType.Updated,
            ChangeTime = DateTime.UtcNow,
            InitiatedByType = ActivityInitiatorType.System,
            ActivityId = _activityId
        };
        change.AddAttributeValueChange(DisplayNameValue(displayName), ValueChangeType.Add);
        return change;
    }

    private MetaverseObjectAttributeValue DisplayNameValue(string value) =>
        new() { Attribute = _displayName, AttributeId = _displayName.Id, StringValue = value };

    private ConnectedSystemObjectChange CsoChange(string employeeId)
    {
        var change = new ConnectedSystemObjectChange
        {
            ConnectedSystemId = _employeeId.ConnectedSystemObjectType.ConnectedSystem.Id,
            ConnectedSystemObjectId = Guid.NewGuid(),
            ChangeType = ObjectChangeType.Updated,
            ChangeTime = DateTime.UtcNow,
            InitiatedByType = ActivityInitiatorType.System
        };
        var attributeChange = new ConnectedSystemObjectChangeAttribute
        {
            ConnectedSystemChange = change,
            Attribute = _employeeId,
            AttributeName = _employeeId.Name,
            AttributeType = _employeeId.Type
        };
        attributeChange.ValueChanges.Add(new ConnectedSystemObjectChangeAttributeValue(attributeChange, ValueChangeType.Add, employeeId));
        change.AttributeChanges.Add(attributeChange);
        return change;
    }

    /// <summary>
    /// Reads every id each query returns, and asserts each query returned rows and that every id is version 7.
    /// </summary>
    private async Task AssertEveryIdIsVersion7Async(IEnumerable<string> queries, int expectedRows)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();

        var ids = new Dictionary<string, List<Guid>>();
        foreach (var sql in queries)
        {
            await using var command = new NpgsqlCommand(sql, connection);
            await using var reader = await command.ExecuteReaderAsync();
            var read = new List<Guid>();
            while (await reader.ReadAsync())
                read.Add(reader.GetGuid(0));
            ids[sql.Trim()] = read;
        }

        using (Assert.EnterMultipleScope())
        {
            foreach (var (sql, read) in ids)
            {
                Assert.That(read, Has.Count.GreaterThanOrEqualTo(expectedRows), $"{sql} returned the rows written");
                Assert.That(read.Select(id => id.Version), Is.All.EqualTo(7), $"{sql}: every id is time-ordered (version 7)");
            }
        }
    }
}
