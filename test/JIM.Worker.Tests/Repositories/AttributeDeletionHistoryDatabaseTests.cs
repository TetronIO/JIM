// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Staging;
using JIM.Models.Utility;
using JIM.PostgresData;
using JIM.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Deleting an attribute leaves the change history that names it exactly as it was written (#2931). A change record's
/// attribute rows name their attribute by a plain id, as change records name their object (#2669): no foreign key, so
/// the deletion makes the database rewrite nothing, and the id is kept rather than cleared. The change history readers
/// then show the attribute by the name and type the row copied when it was written.
/// </summary>
/// <remarks>
/// The history is written by the writers synchronisation uses, so the test also proves they record the attribute's id.
/// Opt-in via the <c>JIM_TEST_RESET_*</c> environment variables; ignored when <c>JIM_TEST_RESET_DB</c> is absent.
/// </remarks>
[TestFixture]
[Category("RequiresPostgres")]
public class AttributeDeletionHistoryDatabaseTests
{
    private static readonly DateTime T0 = new(2026, 11, 1, 9, 0, 0, DateTimeKind.Utc);

    private string _connectionString = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        var connectionString = PostgresTestDatabase.ConnectionStringFromEnvironment();
        if (connectionString == null)
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping the real-PostgreSQL attribute deletion history tests.");
        _connectionString = connectionString!;

        using var ctx = NewContext();
        // The repository sets Npgsql's timestamp switch, as the Worker does, before anything is written.
        _ = new PostgresDataRepository(ctx);
        ctx.Database.Migrate();
    }

    [SetUp]
    public async Task SetUp() => await PostgresTestDatabase.ResetAsync(_connectionString);

    private JimDbContext NewContext() => new(new DbContextOptionsBuilder<JimDbContext>()
        .UseNpgsql(_connectionString)
        .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking)
        .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
        .Options);

    private JimApplication NewJim() => new(new PostgresDataRepository(NewContext()));

    // ─── Connected System Objects ───

    [Test]
    public async Task DeletingAConnectedSystemAttribute_LeavesItsChangeHistoryUntouchedAndReadableAsync()
    {
        var (connectedSystemId, typeId, externalIdAttributeId, title) = await SeedConnectedSystemAsync();
        var csoId = await CreateCsoAsync(connectedSystemId, typeId, externalIdAttributeId);

        var change = new ConnectedSystemObjectChange
        {
            ConnectedSystemId = connectedSystemId,
            ConnectedSystemObjectId = csoId,
            ChangeType = ObjectChangeType.Updated,
            ChangeTime = T0
        };
        var attributeChange = new ConnectedSystemObjectChangeAttribute
        {
            Attribute = title, AttributeName = title.Name, AttributeType = title.Type, ConnectedSystemChange = change
        };
        attributeChange.ValueChanges.Add(new ConnectedSystemObjectChangeAttributeValue
        {
            ConnectedSystemObjectChangeAttribute = attributeChange, ValueChangeType = ValueChangeType.Add, StringValue = "Engineer"
        });
        attributeChange.ValueChanges.Add(new ConnectedSystemObjectChangeAttributeValue
        {
            ConnectedSystemObjectChangeAttribute = attributeChange, ValueChangeType = ValueChangeType.Remove, StringValue = "Intern"
        });
        change.AttributeChanges.Add(attributeChange);
        await using (var ctx = NewContext())
            await new PostgresDataRepository(ctx).Sync.PersistCsoChangesAsync([change]);

        var before = await ReadAttributeRowAsync("ConnectedSystemObjectChangeAttributes", attributeChange.Id);
        Assert.That(before.AttributeId, Is.EqualTo(title.Id), "the writer records the attribute's id");

        // As the Connected System deletion's own statement removes the attributes (step 12).
        await ExecuteAsync("""DELETE FROM "ConnectedSystemAttributes" WHERE "Id" = @id""", title.Id);

        var after = await ReadAttributeRowAsync("ConnectedSystemObjectChangeAttributes", attributeChange.Id);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(after.AttributeId, Is.EqualTo(title.Id), "the history keeps naming the deleted attribute by its id");
            Assert.That(after.Xmin, Is.EqualTo(before.Xmin), "the deletion rewrote the history row");
        }

        var jim = NewJim();
        var (items, total) = await jim.ConnectedSystems.GetCsoChangeHistoryAsync(csoId, 1, 10);
        Assert.That(total, Is.EqualTo(1));
        var historyAttribute = items.Single().AttributeChanges.Single();
        var rows = await jim.ConnectedSystems.GetCsoChangeValueRowsAsync(
            new ChangeValueRowQuery { ChangeIds = [change.Id], Count = 10, IncludeTotalCount = true });
        var summaries = await jim.ConnectedSystems.GetCsoChangeAttributeValueSummariesAsync(change.Id);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(historyAttribute.AttributeName, Is.EqualTo("title"));
            Assert.That(historyAttribute.AttributeType, Is.EqualTo(AttributeDataType.Text));
            Assert.That(historyAttribute.ValueChanges.Select(v => v.StringValue), Is.EquivalentTo(new[] { "Engineer", "Intern" }));

            Assert.That(rows.TotalResults, Is.EqualTo(1), "a single-valued replacement reads as one Set row");
            var row = rows.Results.Single();
            Assert.That(row.AttributeName, Is.EqualTo("title"));
            Assert.That(row.AttributeType, Is.EqualTo(AttributeDataType.Text));
            Assert.That(row.Operation, Is.EqualTo(ChangeValueOperation.Set));
            Assert.That(row.Value.StringValue, Is.EqualTo("Engineer"));
            Assert.That(row.PreviousValue?.StringValue, Is.EqualTo("Intern"));

            Assert.That(summaries.Select(s => s.AttributeName), Is.EqualTo(new[] { "title" }));
        }
    }

    // ─── Metaverse Objects ───

    [Test]
    public async Task DeletingAMetaverseAttribute_LeavesItsChangeHistoryUntouchedAndReadableAsync()
    {
        var (typeId, jobTitle) = await SeedMetaverseAsync();
        var mvoId = await CreateMvoAsync(typeId);

        var change = new MetaverseObjectChange
        {
            MetaverseObjectId = mvoId,
            ChangeType = ObjectChangeType.Updated,
            ChangeInitiatorType = MetaverseObjectChangeInitiatorType.SynchronisationRule,
            ChangeTime = T0
        };
        var attributeChange = new MetaverseObjectChangeAttribute
        {
            Attribute = jobTitle, AttributeName = jobTitle.Name, AttributeType = jobTitle.Type, MetaverseObjectChange = change
        };
        attributeChange.ValueChanges.Add(new MetaverseObjectChangeAttributeValue
        {
            MetaverseObjectChangeAttribute = attributeChange, ValueChangeType = ValueChangeType.Add, StringValue = "Engineer"
        });
        attributeChange.ValueChanges.Add(new MetaverseObjectChangeAttributeValue
        {
            MetaverseObjectChangeAttribute = attributeChange, ValueChangeType = ValueChangeType.Remove, StringValue = "Intern"
        });
        change.AttributeChanges.Add(attributeChange);
        await using (var ctx = NewContext())
            await new PostgresDataRepository(ctx).Metaverse.CreateMetaverseObjectChangeDirectAsync(change);

        var before = await ReadAttributeRowAsync("MetaverseObjectChangeAttributes", attributeChange.Id);
        Assert.That(before.AttributeId, Is.EqualTo(jobTitle.Id), "the writer records the attribute's id");

        // The administrator's deletion, as the portal, the REST API and PowerShell make it.
        var impact = await NewJim().Metaverse.DeleteMetaverseAttributeWithCascadeAsync(jobTitle, TestUtilities.GetInitiatedBy());
        Assert.That(impact.BlockedByValues || impact.BlockedByReaders, Is.False, "the attribute was deleted");

        var after = await ReadAttributeRowAsync("MetaverseObjectChangeAttributes", attributeChange.Id);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(after.AttributeId, Is.EqualTo(jobTitle.Id), "the history keeps naming the deleted attribute by its id");
            Assert.That(after.Xmin, Is.EqualTo(before.Xmin), "the deletion rewrote the history row");
        }

        var jim = NewJim();
        var (items, total) = await jim.Metaverse.GetMvoChangeHistoryAsync(mvoId, 1, 10);
        Assert.That(total, Is.EqualTo(1));
        var historyAttribute = items.Single().AttributeChanges.Single();
        var rows = await jim.Metaverse.GetMvoChangeValueRowsAsync(
            new ChangeValueRowQuery { ChangeIds = [change.Id], Count = 10, IncludeTotalCount = true });
        var summaries = await jim.Metaverse.GetMvoChangeAttributeValueSummariesAsync(change.Id);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(historyAttribute.AttributeName, Is.EqualTo("jobTitle"));
            Assert.That(historyAttribute.AttributeType, Is.EqualTo(AttributeDataType.Text));
            Assert.That(historyAttribute.AttributeId, Is.Null, "nothing links to an attribute that is no longer there");
            Assert.That(historyAttribute.ValueChanges.Select(v => v.StringValue), Is.EquivalentTo(new[] { "Engineer", "Intern" }));

            Assert.That(rows.TotalResults, Is.EqualTo(1), "a single-valued replacement reads as one Set row");
            var row = rows.Results.Single();
            Assert.That(row.AttributeName, Is.EqualTo("jobTitle"));
            Assert.That(row.AttributeType, Is.EqualTo(AttributeDataType.Text));
            Assert.That(row.AttributeId, Is.Null, "nothing links to an attribute that is no longer there");
            Assert.That(row.Operation, Is.EqualTo(ChangeValueOperation.Set));
            Assert.That(row.Value.StringValue, Is.EqualTo("Engineer"));
            Assert.That(row.PreviousValue?.StringValue, Is.EqualTo("Intern"));

            Assert.That(summaries.Select(s => s.AttributeName), Is.EqualTo(new[] { "jobTitle" }));
        }
    }

    [Test]
    public async Task MetaverseChangeHistory_AttributeStillHeld_NamesItsIdAsync()
    {
        var (typeId, jobTitle) = await SeedMetaverseAsync();
        var mvoId = await CreateMvoAsync(typeId);
        var change = new MetaverseObjectChange { MetaverseObjectId = mvoId, ChangeType = ObjectChangeType.Updated, ChangeTime = T0 };
        var attributeChange = new MetaverseObjectChangeAttribute
        {
            Attribute = jobTitle, AttributeName = jobTitle.Name, AttributeType = jobTitle.Type, MetaverseObjectChange = change
        };
        attributeChange.ValueChanges.Add(new MetaverseObjectChangeAttributeValue
        {
            MetaverseObjectChangeAttribute = attributeChange, ValueChangeType = ValueChangeType.Add, StringValue = "Engineer"
        });
        change.AttributeChanges.Add(attributeChange);
        await using (var ctx = NewContext())
            await new PostgresDataRepository(ctx).Metaverse.CreateMetaverseObjectChangeDirectAsync(change);

        var jim = NewJim();
        var (items, _) = await jim.Metaverse.GetMvoChangeHistoryAsync(mvoId, 1, 10);
        var rows = await jim.Metaverse.GetMvoChangeValueRowsAsync(new ChangeValueRowQuery { ChangeIds = [change.Id], Count = 10 });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(items.Single().AttributeChanges.Single().AttributeId, Is.EqualTo(jobTitle.Id));
            Assert.That(rows.Results.Single().AttributeId, Is.EqualTo(jobTitle.Id));
        }
    }

    // ─── Seeding and reading ───

    private async Task<(int ConnectedSystemId, int TypeId, int ExternalIdAttributeId, ConnectedSystemObjectTypeAttribute Title)> SeedConnectedSystemAsync()
    {
        await using var ctx = NewContext();
        var connectorDefinition = new ConnectorDefinition { Name = "Test Connector", BuiltIn = true };
        var system = new ConnectedSystem { Name = "Panoply Directory", ConnectorDefinition = connectorDefinition };
        var type = new ConnectedSystemObjectType { Name = "user", ConnectedSystem = system, Selected = true };
        var externalId = new ConnectedSystemObjectTypeAttribute
        {
            Name = "employeeId", ConnectedSystemObjectType = type, Type = AttributeDataType.Text, IsExternalId = true, Selected = true
        };
        var title = new ConnectedSystemObjectTypeAttribute
        {
            Name = "title", ConnectedSystemObjectType = type, Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued, Selected = true
        };
        type.Attributes.AddRange([externalId, title]);
        ctx.AddRange(connectorDefinition, system, type);
        await ctx.SaveChangesAsync();
        return (system.Id, type.Id, externalId.Id, title);
    }

    private async Task<Guid> CreateCsoAsync(int connectedSystemId, int typeId, int externalIdAttributeId)
    {
        await using var ctx = NewContext();
        var cso = new ConnectedSystemObject
        {
            Id = TimeOrderedIds.New(),
            ConnectedSystemId = connectedSystemId,
            TypeId = typeId,
            ExternalIdAttributeId = externalIdAttributeId,
            Created = T0
        };
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { ConnectedSystemObject = cso, AttributeId = externalIdAttributeId, StringValue = "E1001" });
        ctx.Add(cso);
        await ctx.SaveChangesAsync();
        return cso.Id;
    }

    private async Task<(int TypeId, MetaverseAttribute JobTitle)> SeedMetaverseAsync()
    {
        await using var ctx = NewContext();
        var type = new MetaverseObjectType { Name = "Person", PluralName = "People" };
        var jobTitle = new MetaverseAttribute { Name = "jobTitle", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.SingleValued };
        ctx.MetaverseObjectTypes.Add(type);
        ctx.MetaverseAttributes.Add(jobTitle);
        await ctx.SaveChangesAsync();
        return (type.Id, jobTitle);
    }

    private async Task<Guid> CreateMvoAsync(int typeId)
    {
        await using var ctx = NewContext();
        var mvo = new MetaverseObject { Id = TimeOrderedIds.New(), Origin = MetaverseObjectOrigin.Projected, CachedDisplayName = "Tina Adams" };
        ctx.Add(mvo);
        ctx.Entry(mvo).Property("TypeId").CurrentValue = typeId;
        await ctx.SaveChangesAsync();
        return mvo.Id;
    }

    /// <summary>
    /// A history attribute row's attribute id and its <c>xmin</c>, the id of the transaction that last wrote it: any
    /// update of the row, a foreign key's <c>SET NULL</c> included, gives it a new one.
    /// </summary>
    private async Task<(int? AttributeId, string Xmin)> ReadAttributeRowAsync(string table, Guid id)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"""SELECT "AttributeId", xmin::text FROM "{table}" WHERE "Id" = @id""", connection);
        command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.That(await reader.ReadAsync(), Is.True, $"the {table} row is still there");
        return (reader.IsDBNull(0) ? null : reader.GetInt32(0), reader.GetString(1));
    }

    private async Task ExecuteAsync(string sql, int id)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", id);
        await command.ExecuteNonQueryAsync();
    }
}
