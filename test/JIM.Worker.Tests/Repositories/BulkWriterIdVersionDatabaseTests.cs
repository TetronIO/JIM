// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.PostgresData;
using JIM.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// The raw SQL writers of JIM's largest live tables give the rows they insert time-ordered (version 7) ids where the
/// caller set none (#2929): Connected System Objects and their values, Metaverse Objects and their values, and Pending
/// Exports and their changes. A random (version 4) id lands each insert on a random page of every index keyed on it, the
/// primary key and each child table's index on its parent's id; a version 7 id lands it on the growing end.
/// </summary>
/// <remarks>
/// The rows are given no ids, so each writer must assign them, and every id is read back on a connection of the test's
/// own, so an id the writer assigns without handing it back is still caught. The change record tables have their own
/// fixture (<see cref="ChangeRecordIdVersionDatabaseTests"/>). Opt-in via the <c>JIM_TEST_RESET_*</c> environment
/// variables; ignored when <c>JIM_TEST_RESET_DB</c> is absent.
/// </remarks>
[TestFixture]
[Category("RequiresPostgres")]
public class BulkWriterIdVersionDatabaseTests
{
    private static readonly string[] CsoIdColumns =
    [
        """SELECT "Id" FROM "ConnectedSystemObjects" """,
        """SELECT "Id" FROM "ConnectedSystemObjectAttributeValues" """
    ];

    private static readonly string[] MvoIdColumns =
    [
        """SELECT "Id" FROM "MetaverseObjects" """,
        """SELECT "Id" FROM "MetaverseObjectAttributeValues" """
    ];

    private static readonly string[] PendingExportIdColumns =
    [
        """SELECT "Id" FROM "PendingExports" """,
        """SELECT "Id" FROM "PendingExportAttributeValueChanges" """
    ];

    private string _connectionString = null!;
    private int _systemId;
    private int _typeId;
    private int _employeeIdAttributeId;
    private int _displayNameAttributeId;
    private MetaverseObjectType _personType = null!;
    private MetaverseAttribute _displayName = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        var connectionString = PostgresTestDatabase.ConnectionStringFromEnvironment();
        if (connectionString == null)
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping the real-PostgreSQL bulk writer id version tests.");
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
        var employeeId = new ConnectedSystemObjectTypeAttribute
        {
            Name = "employeeId", ConnectedSystemObjectType = type, Type = AttributeDataType.Text, IsExternalId = true, Selected = true
        };
        var displayName = new ConnectedSystemObjectTypeAttribute
        {
            Name = "displayName", ConnectedSystemObjectType = type, Type = AttributeDataType.Text, Selected = true
        };
        type.Attributes.Add(employeeId);
        type.Attributes.Add(displayName);
        _personType = new MetaverseObjectType { Name = "Person", PluralName = "People" };
        _displayName = new MetaverseAttribute { Name = "Display Name", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.SingleValued };
        seed.AddRange(connectorDefinition, system, type, _personType, _displayName);
        await seed.SaveChangesAsync();

        (_systemId, _typeId, _employeeIdAttributeId, _displayNameAttributeId) = (system.Id, type.Id, employeeId.Id, displayName.Id);
    }

    private JimDbContext NewContext() => new(new DbContextOptionsBuilder<JimDbContext>()
        .UseNpgsql(_connectionString)
        .UseQueryTrackingBehavior(QueryTrackingBehavior.TrackAll)
        .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning, RelationalEventId.MultipleCollectionIncludeWarning))
        .Options);

    /// <summary>
    /// The synchronisation's writer of new Connected System Objects (provisioning, and an import's batches through
    /// <c>PrepareConnectedSystemObjectsForCreate</c>), on its single-connection path and its parallel one.
    /// </summary>
    [TestCase(false)]
    [TestCase(true)]
    public async Task SyncCreateConnectedSystemObjectsAsync_ObjectsWithoutIds_WritesVersion7IdsAsync(bool parallel)
    {
        using var parallelPath = parallel ? ParallelWritePathScope.Enter() : null;
        // Each object writes itself and two values.
        var count = parallel ? ParallelWritePathScope.Threshold / 3 + 1 : 3;
        if (parallel)
            ParallelWritePathScope.AssertEngaged(count * 3);

        await using (var ctx = NewContext())
            await new PostgresDataRepository(ctx).Sync.CreateConnectedSystemObjectsAsync(Enumerable.Range(0, count).Select(Cso).ToList());

        await AssertEveryIdIsVersion7Async(CsoIdColumns, expectedRows: count);
    }

    /// <summary>
    /// The import's own writer of new Connected System Objects.
    /// </summary>
    [Test]
    public async Task ConnectedSystemsCreateConnectedSystemObjectsAsync_ObjectsWithoutIds_WritesVersion7IdsAsync()
    {
        await using (var ctx = NewContext())
            await new PostgresDataRepository(ctx).ConnectedSystems.CreateConnectedSystemObjectsAsync([Cso(0), Cso(1)]);

        await AssertEveryIdIsVersion7Async(CsoIdColumns, expectedRows: 2);
    }

    /// <summary>
    /// The writer of values added to Connected System Objects already held: an import's updates.
    /// </summary>
    [Test]
    public async Task UpdateConnectedSystemObjectsWithNewAttributeValuesAsync_ValuesWithoutIds_WritesVersion7IdsAsync()
    {
        var cso = Cso(0);
        cso.Id = Guid.CreateVersion7();
        foreach (var value in cso.AttributeValues)
            value.Id = Guid.CreateVersion7();
        await using (var ctx = NewContext())
            await new PostgresDataRepository(ctx).Sync.CreateConnectedSystemObjectsAsync([cso]);

        await using (var ctx = NewContext())
        {
            var held = await ctx.ConnectedSystemObjects.Include(c => c.AttributeValues).SingleAsync(c => c.Id == cso.Id);
            var added = new ConnectedSystemObjectAttributeValue
            {
                ConnectedSystemObject = held, AttributeId = _displayNameAttributeId, StringValue = "Joan (moved)"
            };
            await new PostgresDataRepository(ctx).Sync.UpdateConnectedSystemObjectsWithNewAttributeValuesAsync([(held, [added])]);
        }

        await AssertEveryIdIsVersion7Async(CsoIdColumns, expectedRows: 1);
    }

    /// <summary>
    /// The synchronisation's writer of projected Metaverse Objects, on its single-connection path and its parallel one.
    /// </summary>
    [TestCase(false)]
    [TestCase(true)]
    public async Task CreateMetaverseObjectsAsync_ObjectsWithoutIds_WritesVersion7IdsAsync(bool parallel)
    {
        using var parallelPath = parallel ? ParallelWritePathScope.Enter() : null;
        // Each object writes itself and one value.
        var count = parallel ? ParallelWritePathScope.Threshold / 2 + 1 : 3;
        if (parallel)
            ParallelWritePathScope.AssertEngaged(count * 2);

        await using (var ctx = NewContext())
            await new PostgresDataRepository(ctx).Sync.CreateMetaverseObjectsAsync(Enumerable.Range(0, count).Select(Mvo).ToList());

        await AssertEveryIdIsVersion7Async(MvoIdColumns, expectedRows: count);
    }

    /// <summary>
    /// The writer of values added to Metaverse Objects already held: a synchronisation's updates.
    /// </summary>
    [Test]
    public async Task UpdateMetaverseObjectsAsync_ValueWithoutId_WritesVersion7IdAsync()
    {
        var mvo = Mvo(0);
        mvo.Id = Guid.CreateVersion7();
        mvo.AttributeValues.Single().Id = Guid.CreateVersion7();
        await using (var ctx = NewContext())
            await new PostgresDataRepository(ctx).Sync.CreateMetaverseObjectsAsync([mvo]);

        mvo.AttributeValues.Add(new MetaverseObjectAttributeValue
        {
            MetaverseObject = mvo, Attribute = _displayName, AttributeId = _displayName.Id, StringValue = "Joan (also)"
        });
        await using (var ctx = NewContext())
            await new PostgresDataRepository(ctx).Sync.UpdateMetaverseObjectsAsync([mvo]);

        await AssertEveryIdIsVersion7Async(MvoIdColumns, expectedRows: 1);
        await using var check = NewContext();
        Assert.That(await check.MetaverseObjectAttributeValues.CountAsync(v => v.MetaverseObject.Id == mvo.Id), Is.EqualTo(2),
            "the added value was written");
    }

    /// <summary>
    /// The writer of staged Pending Exports, on its single-connection path and its parallel one.
    /// </summary>
    [TestCase(false)]
    [TestCase(true)]
    public async Task CreatePendingExportsAsync_ExportsWithoutIds_WritesVersion7IdsAsync(bool parallel)
    {
        using var parallelPath = parallel ? ParallelWritePathScope.Enter() : null;
        // Each export writes itself and two changes.
        var count = parallel ? ParallelWritePathScope.Threshold / 3 + 1 : 3;
        if (parallel)
            ParallelWritePathScope.AssertEngaged(count * 3);

        var exports = Enumerable.Range(0, count).Select(n => new PendingExport
        {
            ConnectedSystemId = _systemId,
            ChangeType = PendingExportChangeType.Create,
            Status = PendingExportStatus.Pending,
            CreatedAt = DateTime.UtcNow,
            AttributeValueChanges =
            {
                new PendingExportAttributeValueChange
                {
                    AttributeId = _employeeIdAttributeId, StringValue = $"E{n:D5}", ChangeType = PendingExportAttributeChangeType.Add
                },
                new PendingExportAttributeValueChange
                {
                    AttributeId = _displayNameAttributeId, StringValue = $"Person {n}", ChangeType = PendingExportAttributeChangeType.Add
                }
            }
        }).ToList();

        await using (var ctx = NewContext())
            await new PostgresDataRepository(ctx).Sync.CreatePendingExportsAsync(exports);

        await AssertEveryIdIsVersion7Async(PendingExportIdColumns, expectedRows: count);
    }

    private ConnectedSystemObject Cso(int n)
    {
        var cso = new ConnectedSystemObject
        {
            ConnectedSystemId = _systemId,
            TypeId = _typeId,
            ExternalIdAttributeId = _employeeIdAttributeId,
            Status = ConnectedSystemObjectStatus.Normal,
            JoinType = ConnectedSystemObjectJoinType.NotJoined,
            Created = DateTime.UtcNow
        };
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            ConnectedSystemObject = cso, AttributeId = _employeeIdAttributeId, StringValue = $"E{n:D5}"
        });
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            ConnectedSystemObject = cso, AttributeId = _displayNameAttributeId, StringValue = $"Person {n}"
        });
        return cso;
    }

    private MetaverseObject Mvo(int n)
    {
        var mvo = new MetaverseObject { Type = _personType, Created = DateTime.UtcNow };
        mvo.AttributeValues.Add(new MetaverseObjectAttributeValue
        {
            MetaverseObject = mvo, Attribute = _displayName, AttributeId = _displayName.Id, StringValue = $"Person {n}"
        });
        return mvo;
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
