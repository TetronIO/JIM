// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.PostgresData;
using JIM.PostgresData.Repositories;
using JIM.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Real-PostgreSQL verification of the lookup behind Object Matching's import join (#2052): one query for every value
/// a rule computed, returning distinct Metaverse Object ids. Several values that belong to one Metaverse Object are
/// one match; values belonging to different ones are several, which the caller reports as ambiguous rather than
/// picking. The in-memory provider can prove none of this: the lookup is raw SQL, and its case-insensitive path
/// depends on PostgreSQL's <c>lower()</c> and the expression index built for it.
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
public class MetaverseMatchValueLookupDatabaseTests
{
    private string _connectionString = null!;

    private JimDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<JimDbContext>()
            .UseNpgsql(_connectionString)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        return new JimDbContext(options);
    }

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        var dbName = Environment.GetEnvironmentVariable("JIM_TEST_RESET_DB");
        if (string.IsNullOrEmpty(dbName))
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL match value lookup tests.");

        var host = Environment.GetEnvironmentVariable("JIM_TEST_RESET_HOST") ?? "localhost";
        var user = Environment.GetEnvironmentVariable("JIM_TEST_RESET_USER") ?? "postgres";
        var pass = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PASSWORD") ?? "postgres";
        var port = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PORT") ?? "5432";
        _connectionString = $"Host={host};Port={port};Database={dbName};Username={user};Password={pass}";

        using var ctx = NewContext();
        ctx.Database.Migrate();
    }

    [SetUp]
    public async Task SetUp() => await PostgresTestDatabase.ResetAsync(_connectionString);

    [Test]
    public async Task FindMetaverseObjectIdsByMatchValuesAsync_IgnoringCase_FindsAValueInAnotherCaseAsync()
    {
        var seeded = await SeedAsync();
        var bloggs = await AddMvoAsync(seeded.Person, seeded.EmployeeId, av => av.StringValue = "JBloggs");

        var ids = await FindAsync(seeded.Person, seeded.EmployeeId, AttributeDataType.Text, ["jbloggs"], caseSensitive: false);

        Assert.That(ids, Is.EqualTo(new[] { bloggs.Id }));
    }

    [Test]
    public async Task FindMetaverseObjectIdsByMatchValuesAsync_RespectingCase_DoesNotFindAValueInAnotherCaseAsync()
    {
        var seeded = await SeedAsync();
        await AddMvoAsync(seeded.Person, seeded.EmployeeId, av => av.StringValue = "JBloggs");

        var ids = await FindAsync(seeded.Person, seeded.EmployeeId, AttributeDataType.Text, ["jbloggs"], caseSensitive: true);

        Assert.That(ids, Is.Empty);
    }

    [TestCase("j_smith", "jxsmith")]
    [TestCase("j%smith", "jxxsmith")]
    public async Task FindMetaverseObjectIdsByMatchValuesAsync_IgnoringCase_TreatsPatternCharactersLiterallyAsync(string sought, string held)
    {
        var seeded = await SeedAsync();
        await AddMvoAsync(seeded.Person, seeded.EmployeeId, av => av.StringValue = held);

        var ids = await FindAsync(seeded.Person, seeded.EmployeeId, AttributeDataType.Text, [sought], caseSensitive: false);

        Assert.That(ids, Is.Empty, $"'{sought}' must not match '{held}': this is equality, not a pattern");
    }

    [Test]
    public async Task FindMetaverseObjectIdsByMatchValuesAsync_SeveralValuesOfOneIdentity_ReturnsItOnceAsync()
    {
        var seeded = await SeedAsync();
        var bloggs = await AddMvoAsync(seeded.Person, seeded.EmployeeId, av => av.StringValue = "E100");
        await AddValueAsync(bloggs, seeded.EmployeeId, av => av.StringValue = "E200");

        var ids = await FindAsync(seeded.Person, seeded.EmployeeId, AttributeDataType.Text, ["E100", "E200"], caseSensitive: false);

        Assert.That(ids, Is.EqualTo(new[] { bloggs.Id }));
    }

    [Test]
    public async Task FindMetaverseObjectIdsByMatchValuesAsync_ValuesOfTwoIdentities_ReturnsBothAsync()
    {
        var seeded = await SeedAsync();
        var first = await AddMvoAsync(seeded.Person, seeded.EmployeeId, av => av.StringValue = "E100");
        var second = await AddMvoAsync(seeded.Person, seeded.EmployeeId, av => av.StringValue = "E200");

        var ids = await FindAsync(seeded.Person, seeded.EmployeeId, AttributeDataType.Text, ["E100", "E200"], caseSensitive: false);

        Assert.That(ids, Is.EquivalentTo(new[] { first.Id, second.Id }));
    }

    [Test]
    public async Task FindMetaverseObjectIdsByMatchValuesAsync_AnotherTypesObject_IsNotReturnedAsync()
    {
        var seeded = await SeedAsync();
        await AddMvoAsync(seeded.Group, seeded.EmployeeId, av => av.StringValue = "E100");

        var ids = await FindAsync(seeded.Person, seeded.EmployeeId, AttributeDataType.Text, ["E100"], caseSensitive: false);

        Assert.That(ids, Is.Empty);
    }

    [Test]
    public async Task FindMetaverseObjectIdsByMatchValuesAsync_Take_LimitsTheIdsReturnedAsync()
    {
        var seeded = await SeedAsync();
        for (var i = 0; i < 4; i++)
            await AddMvoAsync(seeded.Person, seeded.EmployeeId, av => av.StringValue = "duplicate");

        var ids = await FindAsync(seeded.Person, seeded.EmployeeId, AttributeDataType.Text, ["duplicate"], caseSensitive: false, take: 2);

        Assert.That(ids, Has.Count.EqualTo(2));
    }

    [Test]
    public async Task CountMetaverseObjectsByMatchValuesAsync_CountsEveryDistinctMatchOfTheTypeAndNoOtherAsync()
    {
        var seeded = await SeedAsync();
        var held = await AddMvoAsync(seeded.Person, seeded.EmployeeId, av => av.StringValue = "duplicate");
        await AddValueAsync(held, seeded.EmployeeId, av => av.StringValue = "DUPLICATE-2");
        for (var i = 0; i < 2; i++)
            await AddMvoAsync(seeded.Person, seeded.EmployeeId, av => av.StringValue = "Duplicate");
        await AddMvoAsync(seeded.Group, seeded.EmployeeId, av => av.StringValue = "duplicate");
        await AddMvoAsync(seeded.Person, seeded.EmployeeId, av => av.StringValue = "unrelated");

        await using var ctx = NewContext();
        var count = await new PostgresDataRepository(ctx).Metaverse.CountMetaverseObjectsByMatchValuesAsync(
            seeded.Person.Id, seeded.EmployeeId.Id, AttributeDataType.Text, ["duplicate", "duplicate-2"], caseSensitive: false);

        Assert.That(count, Is.EqualTo(3), "an identity holding both values counts once; the Group and the unrelated value not at all");
    }

    [Test]
    public async Task FindMetaverseObjectIdsByMatchValuesAsync_Number_FindsTheValueAsync()
    {
        var seeded = await SeedAsync();
        var mvo = await AddMvoAsync(seeded.Person, seeded.StaffNumber, av => av.IntValue = 48213);

        var ids = await FindAsync(seeded.Person, seeded.StaffNumber, AttributeDataType.Number, [48213], caseSensitive: false);

        Assert.That(ids, Is.EqualTo(new[] { mvo.Id }));
    }

    [Test]
    public async Task FindMetaverseObjectIdsByMatchValuesAsync_LongNumber_FindsTheValueAsync()
    {
        var seeded = await SeedAsync();
        var mvo = await AddMvoAsync(seeded.Person, seeded.BadgeNumber, av => av.LongValue = 9_000_000_000L);

        var ids = await FindAsync(seeded.Person, seeded.BadgeNumber, AttributeDataType.LongNumber, [9_000_000_000L], caseSensitive: false);

        Assert.That(ids, Is.EqualTo(new[] { mvo.Id }));
    }

    [Test]
    public async Task FindMetaverseObjectIdsByMatchValuesAsync_DecimalDifferingOnlyInScale_FindsTheValueAsync()
    {
        var seeded = await SeedAsync();
        var mvo = await AddMvoAsync(seeded.Person, seeded.Rate, av => av.DecimalValue = 12.50m);

        var ids = await FindAsync(seeded.Person, seeded.Rate, AttributeDataType.Decimal, [12.5m], caseSensitive: false);

        Assert.That(ids, Is.EqualTo(new[] { mvo.Id }));
    }

    [Test]
    public async Task FindMetaverseObjectIdsByMatchValuesAsync_Guid_FindsTheValueAsync()
    {
        var seeded = await SeedAsync();
        var key = Guid.NewGuid();
        var mvo = await AddMvoAsync(seeded.Person, seeded.ObjectKey, av => av.GuidValue = key);

        var ids = await FindAsync(seeded.Person, seeded.ObjectKey, AttributeDataType.Guid, [key], caseSensitive: false);

        Assert.That(ids, Is.EqualTo(new[] { mvo.Id }));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task FindMetaverseObjectIdsByMatchValuesAsync_TextLookup_SeeksTheValueThroughAnIndexAsync(bool caseSensitive)
    {
        // The lookup runs once per object on the synchronisation hot path, so it must be indexable. The plan is read
        // against analysed statistics for a populated attribute: on a near-empty table the planner chooses between
        // indexes from whatever statistics earlier tests left behind, so which one it names depends on test order.
        var seeded = await SeedAsync();
        await AddMvoAsync(seeded.Person, seeded.EmployeeId, av => av.StringValue = "E100");

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using (var populate = new NpgsqlCommand("""
            INSERT INTO "MetaverseObjects" ("Id", "Created", "TypeId", "Status", "DeletionInitiatedByType", "Origin")
            SELECT md5('mvo' || n)::uuid, now(), @typeId, 0, 0, 0 FROM generate_series(1, 5000) AS n;
            INSERT INTO "MetaverseObjectAttributeValues" ("Id", "AttributeId", "MetaverseObjectId", "StringValue")
            SELECT md5('av' || n)::uuid, @attributeId, md5('mvo' || n)::uuid, 'E' || (100000 + n) FROM generate_series(1, 5000) AS n;
            ANALYZE "MetaverseObjects";
            ANALYZE "MetaverseObjectAttributeValues";
            SET enable_seqscan = off;
            """, connection))
        {
            populate.Parameters.AddWithValue("typeId", seeded.Person.Id);
            populate.Parameters.AddWithValue("attributeId", seeded.EmployeeId.Id);
            await populate.ExecuteNonQueryAsync();
        }

        await using var explain = new NpgsqlCommand(
            "EXPLAIN " + MetaverseRepository.BuildMatchValueLookupSql(AttributeDataType.Text, caseSensitive), connection);
        MetaverseRepository.AddMatchValueLookupParameters(explain, seeded.Person.Id, seeded.EmployeeId.Id, AttributeDataType.Text, ["e100"], take: 2);

        var plan = new List<string>();
        await using (var reader = await explain.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
                plan.Add(reader.GetString(0));
        }

        // The value must drive an index condition. Matched by a filter instead, the scan reads every value the
        // attribute holds before discarding them, whichever index it walks.
        var planText = string.Join("\n", plan);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(plan.Where(line => line.Contains("Index Cond:")).Any(line => line.Contains("StringValue")), Is.True, planText);
            Assert.That(plan.Where(line => line.Contains("Filter:")).Any(line => line.Contains("StringValue\" =") || line.Contains("lower(")), Is.False, planText);
        }
    }

    private async Task<IReadOnlyList<Guid>> FindAsync(MetaverseObjectType type, MetaverseAttribute attribute, AttributeDataType valueType,
        IReadOnlyList<object> values, bool caseSensitive, int take = 11)
    {
        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);
        return await repository.Metaverse.FindMetaverseObjectIdsByMatchValuesAsync(type.Id, attribute.Id, valueType, values, caseSensitive, take);
    }

    private sealed record Seeded(
        MetaverseObjectType Person,
        MetaverseObjectType Group,
        MetaverseAttribute EmployeeId,
        MetaverseAttribute StaffNumber,
        MetaverseAttribute BadgeNumber,
        MetaverseAttribute Rate,
        MetaverseAttribute ObjectKey);

    private async Task<Seeded> SeedAsync()
    {
        await using var seed = NewContext();
        var seeded = new Seeded(
            new MetaverseObjectType { Name = "Person", PluralName = "People", BuiltIn = false },
            new MetaverseObjectType { Name = "Group", PluralName = "Groups", BuiltIn = false },
            new MetaverseAttribute { Name = "Employee ID", Type = AttributeDataType.Text },
            new MetaverseAttribute { Name = "Staff Number", Type = AttributeDataType.Number },
            new MetaverseAttribute { Name = "Badge Number", Type = AttributeDataType.LongNumber },
            new MetaverseAttribute { Name = "Rate", Type = AttributeDataType.Decimal },
            new MetaverseAttribute { Name = "Object Key", Type = AttributeDataType.Guid });
        seed.AddRange(seeded.Person, seeded.Group, seeded.EmployeeId, seeded.StaffNumber, seeded.BadgeNumber, seeded.Rate, seeded.ObjectKey);
        await seed.SaveChangesAsync();
        return seeded;
    }

    private async Task<MetaverseObject> AddMvoAsync(MetaverseObjectType type, MetaverseAttribute attribute, Action<MetaverseObjectAttributeValue> setValue)
    {
        await using var seed = NewContext();
        seed.Attach(type);
        seed.Attach(attribute);
        var value = new MetaverseObjectAttributeValue { Attribute = attribute, AttributeId = attribute.Id };
        setValue(value);
        var mvo = new MetaverseObject { Type = type, AttributeValues = [value] };
        seed.Add(mvo);
        await seed.SaveChangesAsync();
        return mvo;
    }

    private async Task AddValueAsync(MetaverseObject mvo, MetaverseAttribute attribute, Action<MetaverseObjectAttributeValue> setValue)
    {
        await using var seed = NewContext();
        var value = new MetaverseObjectAttributeValue { AttributeId = attribute.Id };
        seed.Entry(value).Property("MetaverseObjectId").CurrentValue = mvo.Id;
        setValue(value);
        seed.Add(value);
        await seed.SaveChangesAsync();
    }
}
