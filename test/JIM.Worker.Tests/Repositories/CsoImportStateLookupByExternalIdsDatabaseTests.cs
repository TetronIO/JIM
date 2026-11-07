// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Staging;
using JIM.PostgresData;
using JIM.PostgresData.Repositories;
using JIM.TestSupport;
using JIM.Utilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// #2705: a Delta Import looks up only the External IDs a page returns, one set-based query per page, instead of loading
/// every object of the Connected System. For the keys it is asked about, the answer must be exactly what the whole lookup
/// (<see cref="ConnectedSystemRepository.GetAllCsoImportStateLookupAsync"/>) holds: a key it misses reads as a new object,
/// and the import creates a duplicate of one JIM already holds. And it must find its rows through indexes keyed on the
/// values sought, or a page would cost what the whole lookup costs.
/// </summary>
/// <remarks>
/// Runs against real PostgreSQL: the folding and the plans under test are the database's own. Opt-in via the same
/// <c>JIM_TEST_RESET_*</c> environment variables as the other RequiresPostgres fixtures.
/// </remarks>
[TestFixture]
[Category("RequiresPostgres")]
public class CsoImportStateLookupByExternalIdsDatabaseTests
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
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL External ID lookup tests.");

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
    }

    [Test]
    public async Task GetCsoImportStateLookupByExternalIdsAsync_KnownAndUnknownValuesOfEveryDataType_ReturnsExactlyTheHeldObjectsAsync()
    {
        var seeded = await SeedAsync();
        var guid = Guid.NewGuid();
        var held = new[]
        {
            await seeded.AddAsync(seeded.GuidAnchor, guidValue: guid),
            await seeded.AddAsync(seeded.TextAnchor, stringValue: "JBloggs"),
            await seeded.AddAsync(seeded.CaseExactAnchor, stringValue: "Abc"),
            await seeded.AddAsync(seeded.NumberAnchor, intValue: 42),
            await seeded.AddAsync(seeded.LongAnchor, longValue: 9_000_000_000L),
            await seeded.AddAsync(seeded.DecimalAnchor, decimalValue: decimal.Parse("4200.00", CultureInfo.InvariantCulture))
        };
        await seeded.AddAsync(seeded.GuidAnchor, guidValue: Guid.NewGuid());
        await seeded.AddAsync(seeded.TextAnchor, stringValue: "Someone Else");

        await using var ctx = NewContext();
        var lookup = await new PostgresDataRepository(ctx).ConnectedSystems.GetCsoImportStateLookupByExternalIdsAsync(seeded.SystemId,
        [
            Probe(seeded.GuidAnchor, guid.ToString()), Probe(seeded.GuidAnchor, Guid.NewGuid().ToString()),
            Probe(seeded.TextAnchor, "jbloggs"), Probe(seeded.TextAnchor, "nobody"),
            Probe(seeded.CaseExactAnchor, "Abc"), Probe(seeded.CaseExactAnchor, "ABC"),
            Probe(seeded.NumberAnchor, "42"), Probe(seeded.NumberAnchor, "43"),
            Probe(seeded.LongAnchor, "9000000000"),
            Probe(seeded.DecimalAnchor, "4200")
        ]);

        Assert.That(lookup.ToDictionary(e => e.Key, e => e.Value.CsoId), Is.EquivalentTo(new Dictionary<string, Guid>
        {
            [Key(seeded, seeded.GuidAnchor, guid.ToString())] = held[0],
            [Key(seeded, seeded.TextAnchor, "jbloggs")] = held[1],
            [Key(seeded, seeded.CaseExactAnchor, "Abc")] = held[2],
            [Key(seeded, seeded.NumberAnchor, "42")] = held[3],
            [Key(seeded, seeded.LongAnchor, "9000000000")] = held[4],
            [Key(seeded, seeded.DecimalAnchor, "4200")] = held[5]
        }));
    }

    [Test]
    public async Task GetCsoImportStateLookupByExternalIdsAsync_EveryKeyOfTheWholeLookup_AnswersAsTheWholeLookupDoesAsync()
    {
        var seeded = await SeedAsync();
        await seeded.AddAsync(seeded.GuidAnchor, guidValue: Guid.NewGuid(), status: ConnectedSystemObjectStatus.Obsolete, partitionId: seeded.PartitionId);
        await seeded.AddAsync(seeded.TextAnchor, stringValue: "JBloggs");
        await seeded.AddAsync(seeded.CaseExactAnchor, stringValue: "Abc");
        await seeded.AddAsync(seeded.CaseExactAnchor, stringValue: "abc");
        await seeded.AddAsync(seeded.NumberAnchor, intValue: -7);
        await seeded.AddAsync(seeded.LongAnchor, longValue: long.MaxValue);
        await seeded.AddAsync(seeded.DecimalAnchor, decimalValue: 17.5m);
        await seeded.AddAsync(seeded.GuidAnchor, secondaryValue: "CN=Pending,DC=Glitterband", status: ConnectedSystemObjectStatus.PendingProvisioning);
        await seeded.AddAsync(seeded.GuidAnchor, guidValue: Guid.NewGuid(), secondaryValue: "CN=Normal,DC=Glitterband");

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx).ConnectedSystems;
        var whole = await repository.GetAllCsoImportStateLookupAsync(seeded.SystemId);
        var lookup = await repository.GetCsoImportStateLookupByExternalIdsAsync(seeded.SystemId, whole.Keys.Select(key => ProbeFromKey(seeded, key)).ToList());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(whole, Has.Count.EqualTo(9), "precondition: one key per object");
            Assert.That(lookup, Is.EquivalentTo(whole));
        }
    }

    [Test]
    public async Task GetCsoImportStateLookupByExternalIdsAsync_ObjectWithNoExternalIdYet_IsFoundOnlyByItsSecondaryExternalIdAsync()
    {
        var seeded = await SeedAsync();
        var pending = await seeded.AddAsync(seeded.GuidAnchor, secondaryValue: "CN=Pending,DC=Glitterband", status: ConnectedSystemObjectStatus.PendingProvisioning);
        await seeded.AddAsync(seeded.GuidAnchor, guidValue: Guid.NewGuid(), secondaryValue: "CN=Normal,DC=Glitterband");

        await using var ctx = NewContext();
        var lookup = await new PostgresDataRepository(ctx).ConnectedSystems.GetCsoImportStateLookupByExternalIdsAsync(seeded.SystemId,
            [Probe(seeded.Secondary, "cn=pending,dc=glitterband"), Probe(seeded.Secondary, "cn=normal,dc=glitterband")]);

        Assert.That(lookup.ToDictionary(e => e.Key, e => e.Value.CsoId), Is.EquivalentTo(new Dictionary<string, Guid>
        {
            [Key(seeded, seeded.Secondary, "cn=pending,dc=glitterband")] = pending
        }), "An object with an External ID is keyed by it alone, as the whole lookup keys it");
    }

    /// <summary>
    /// A schema refresh can change an attribute's data type while values stay stored under the old one (#2128). The
    /// whole lookup keys such a value from whichever column holds it, so the per-page lookup must find it there too.
    /// </summary>
    [Test]
    public async Task GetCsoImportStateLookupByExternalIdsAsync_ValueHeldUnderTheAttributesEarlierDataType_IsFoundAsync()
    {
        var seeded = await SeedAsync();
        var guid = Guid.NewGuid();
        var heldAsText = await seeded.AddAsync(seeded.GuidAnchor, stringValue: guid.ToString().ToUpperInvariant());

        await using var ctx = NewContext();
        var lookup = await new PostgresDataRepository(ctx).ConnectedSystems.GetCsoImportStateLookupByExternalIdsAsync(seeded.SystemId,
            [Probe(seeded.GuidAnchor, guid.ToString())]);

        Assert.That(lookup.Values.Select(entry => entry.CsoId), Is.EqualTo(new[] { heldAsText }));
    }

    [Test]
    public async Task GetCsoImportStateLookupByExternalIdsAsync_ObjectInAnotherConnectedSystem_IsNotFoundAsync()
    {
        var seeded = await SeedAsync();
        var other = await SeedAsync();
        await other.AddAsync(other.TextAnchor, stringValue: "JBloggs");

        await using var ctx = NewContext();
        var lookup = await new PostgresDataRepository(ctx).ConnectedSystems.GetCsoImportStateLookupByExternalIdsAsync(seeded.SystemId,
            [Probe(seeded.TextAnchor, "jbloggs"), new CsoExternalIdProbe(other.TextAnchor.Id, false, "jbloggs")]);

        Assert.That(lookup, Is.Empty);
    }

    [Test]
    public async Task GetCsoImportStateLookupByExternalIdsAsync_KeyIgnoringCaseOutsideAscii_ThrowsAsync()
    {
        var seeded = await SeedAsync();

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx).ConnectedSystems;

        Assert.That(async () => await repository.GetCsoImportStateLookupByExternalIdsAsync(seeded.SystemId, [Probe(seeded.TextAnchor, "émile")]),
            Throws.ArgumentException);
    }

    /// <summary>
    /// The lookup must find each value through an index keyed on it, or a page of a Delta Import would read every External
    /// ID JIM holds, which is the cost #2705 removes. 20,000 objects of each kind are enough for the planner to prefer an
    /// index to a scan. It must also be costed as what it is: without statistics on <c>lower("StringValue")</c> the planner
    /// guessed thousands of rows per text value, joined every Connected System Object by hash and compiled the statement.
    /// </summary>
    [Test]
    public async Task GetCsoImportStateLookupByExternalIdsAsync_ManyObjects_PlanFindsEachValueThroughAnIndexAsync()
    {
        var seeded = await SeedAsync();
        var guid = Guid.NewGuid();
        var guidTemplate = await seeded.AddAsync(seeded.GuidAnchor, guidValue: guid);
        var textTemplate = await seeded.AddAsync(seeded.TextAnchor, stringValue: "JBloggs");
        var numberTemplate = await seeded.AddAsync(seeded.NumberAnchor, intValue: 42);

        await using var ctx = NewContext();
        var connection = (NpgsqlConnection)ctx.Database.GetDbConnection();
        await connection.OpenAsync();

        // Copied from the seeded rows so every other column takes a valid value.
        await using (var populate = new NpgsqlCommand("""
            INSERT INTO "ConnectedSystemObjects"
            SELECT (jsonb_populate_record(cso, jsonb_build_object('Id', md5(t.kind || n)::uuid))).*
            FROM "ConnectedSystemObjects" AS cso
            JOIN (VALUES ('guid', @guidCsoId), ('text', @textCsoId), ('number', @numberCsoId)) AS t(kind, template) ON cso."Id" = t.template
            CROSS JOIN generate_series(1, 20000) AS n;

            INSERT INTO "ConnectedSystemObjectAttributeValues"
            SELECT (jsonb_populate_record(av, jsonb_build_object(
                'Id', md5(t.kind || 'value' || n)::uuid,
                'ConnectedSystemObjectId', md5(t.kind || n)::uuid,
                'GuidValue', CASE WHEN t.kind = 'guid' THEN md5('g' || n)::uuid END,
                'StringValue', CASE WHEN t.kind = 'text' THEN 'User' || n END,
                'IntValue', CASE WHEN t.kind = 'number' THEN n END))).*
            FROM "ConnectedSystemObjectAttributeValues" AS av
            JOIN (VALUES ('guid', @guidCsoId), ('text', @textCsoId), ('number', @numberCsoId)) AS t(kind, template) ON av."ConnectedSystemObjectId" = t.template
            CROSS JOIN generate_series(1, 20000) AS n;

            ANALYZE "ConnectedSystemObjects";
            ANALYZE "ConnectedSystemObjectAttributeValues";
            """, connection))
        {
            populate.Parameters.AddWithValue("guidCsoId", guidTemplate);
            populate.Parameters.AddWithValue("textCsoId", textTemplate);
            populate.Parameters.AddWithValue("numberCsoId", numberTemplate);
            await populate.ExecuteNonQueryAsync();
        }

        await using var explain = new NpgsqlCommand { Connection = connection };
        // A page's worth of each kind of value, as a Delta Import page asks for.
        var probes = Enumerable.Range(1, 500)
            .SelectMany(n => new[] { Probe(seeded.GuidAnchor, Guid.NewGuid().ToString()), Probe(seeded.TextAnchor, $"user{n}"), Probe(seeded.NumberAnchor, n.ToString()) })
            .Append(Probe(seeded.GuidAnchor, guid.ToString()))
            .ToList();
        ConnectedSystemRepository.PrepareCsoImportStateLookupByExternalIdsCommand(explain, seeded.SystemId, probes);
        explain.CommandText = "EXPLAIN " + explain.CommandText;

        var plan = new List<string>();
        await using (var reader = await explain.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
                plan.Add(reader.GetString(0));
        }

        var planText = string.Join("\n", plan);
        var indexConditions = plan.Where(line => line.Contains("Index Cond:")).ToList();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(plan.Any(line => line.Contains("Seq Scan on \"ConnectedSystemObjectAttributeValues\"")), Is.False,
                "The lookup reads every attribute value.\n" + planText);
            Assert.That(indexConditions.Any(line => line.Contains("\"GuidValue\" =")), Is.True, "A GUID must be an index condition.\n" + planText);
            Assert.That(indexConditions.Any(line => line.Contains("\"IntValue\" =")), Is.True, "A number must be an index condition.\n" + planText);
            Assert.That(indexConditions.Any(line => line.Contains("lower(")), Is.True, "Text ignoring case must be an index condition.\n" + planText);
            Assert.That(LowerTextRowsEstimatedPerValue(plan), Is.LessThan(10),
                "Each text value is estimated to match thousands of rows, so the planner costs a page as reading most of the table.\n" + planText);
            Assert.That(plan.Any(line => line.StartsWith("JIT:", StringComparison.Ordinal)), Is.False,
                "A page's lookup is costed as if it read most of the table, so PostgreSQL compiles it.\n" + planText);
        }
    }

    #region helpers

    /// <summary>
    /// The rows the planner expects one text value to match through the lowered-text index, read from its plan line.
    /// </summary>
    private static int LowerTextRowsEstimatedPerValue(IEnumerable<string> plan)
    {
        var line = plan.First(l => l.Contains("_AttributeId_LowerString") && l.Contains("rows="));
        var rows = System.Text.RegularExpressions.Regex.Match(line, @"rows=(\d+)").Groups[1].Value;
        return int.Parse(rows, CultureInfo.InvariantCulture);
    }

    private static CsoExternalIdProbe Probe(ConnectedSystemObjectTypeAttribute anchor, string valueKey) => new(anchor.Id, anchor.CaseExact, valueKey);

    private static string Key(Seeded seeded, ConnectedSystemObjectTypeAttribute anchor, string valueKey) => CsoLookupKey.For(seeded.SystemId, anchor.Id, valueKey);

    private static CsoExternalIdProbe ProbeFromKey(Seeded seeded, string key)
    {
        // cso:{connected system}:{attribute}:{value key}
        var parts = key.Split(':', 4);
        var attributeId = int.Parse(parts[2], CultureInfo.InvariantCulture);
        return new CsoExternalIdProbe(attributeId, attributeId == seeded.CaseExactAnchor.Id, parts[3]);
    }

    private sealed class Seeded(Func<JimDbContext> newContext, int systemId, int objectTypeId, int partitionId,
        ConnectedSystemObjectTypeAttribute guidAnchor, ConnectedSystemObjectTypeAttribute textAnchor, ConnectedSystemObjectTypeAttribute caseExactAnchor,
        ConnectedSystemObjectTypeAttribute numberAnchor, ConnectedSystemObjectTypeAttribute longAnchor, ConnectedSystemObjectTypeAttribute decimalAnchor,
        ConnectedSystemObjectTypeAttribute secondary)
    {
        public int SystemId { get; } = systemId;
        public int PartitionId { get; } = partitionId;
        public ConnectedSystemObjectTypeAttribute GuidAnchor { get; } = guidAnchor;
        public ConnectedSystemObjectTypeAttribute TextAnchor { get; } = textAnchor;
        public ConnectedSystemObjectTypeAttribute CaseExactAnchor { get; } = caseExactAnchor;
        public ConnectedSystemObjectTypeAttribute NumberAnchor { get; } = numberAnchor;
        public ConnectedSystemObjectTypeAttribute LongAnchor { get; } = longAnchor;
        public ConnectedSystemObjectTypeAttribute DecimalAnchor { get; } = decimalAnchor;
        public ConnectedSystemObjectTypeAttribute Secondary { get; } = secondary;

        /// <summary>
        /// One object anchored by <paramref name="anchor"/>, holding whichever values are given, and the Secondary External
        /// ID when one is.
        /// </summary>
        public async Task<Guid> AddAsync(ConnectedSystemObjectTypeAttribute anchor, Guid? guidValue = null, string? stringValue = null, int? intValue = null,
            long? longValue = null, decimal? decimalValue = null, string? secondaryValue = null,
            ConnectedSystemObjectStatus status = ConnectedSystemObjectStatus.Normal, int? partitionId = null)
        {
            await using var ctx = newContext();
            var cso = new ConnectedSystemObject
            {
                ConnectedSystemId = SystemId,
                TypeId = objectTypeId,
                ExternalIdAttributeId = anchor.Id,
                SecondaryExternalIdAttributeId = secondaryValue != null ? Secondary.Id : null,
                Status = status,
                PartitionId = partitionId,
                ImportStateHash = Guid.NewGuid(),
                ImportStateFingerprint = Guid.NewGuid()
            };
            if (guidValue != null || stringValue != null || intValue != null || longValue != null || decimalValue != null)
            {
                cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
                {
                    ConnectedSystemObject = cso, AttributeId = anchor.Id, GuidValue = guidValue, StringValue = stringValue, IntValue = intValue,
                    LongValue = longValue, DecimalValue = decimalValue
                });
            }
            if (secondaryValue != null)
                cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { ConnectedSystemObject = cso, AttributeId = Secondary.Id, StringValue = secondaryValue });

            ctx.ConnectedSystemObjects.Add(cso);
            await ctx.SaveChangesAsync();
            return cso.Id;
        }
    }

    /// <summary>
    /// One Connected System with one Object Type carrying an anchor of every data type an External ID can have, a
    /// case-exact text anchor beside one ignoring case, a Secondary External ID, and a Partition.
    /// </summary>
    private async Task<Seeded> SeedAsync()
    {
        await using var seed = NewContext();

        var connectorDefinition = new ConnectorDefinition { Name = "Test Connector " + Guid.NewGuid(), BuiltIn = true };
        var connectedSystem = new ConnectedSystem { Name = "Glitterband " + Guid.NewGuid(), ConnectorDefinition = connectorDefinition };
        var objectType = new ConnectedSystemObjectType { Name = "user", ConnectedSystem = connectedSystem, Selected = true };
        ConnectedSystemObjectTypeAttribute Attribute(string name, AttributeDataType type, bool caseExact = false, bool secondary = false) => new()
        {
            Name = name, ConnectedSystemObjectType = objectType, Type = type, AttributePlurality = AttributePlurality.SingleValued,
            Selected = true, IsExternalId = !secondary, IsSecondaryExternalId = secondary, CaseExact = caseExact
        };
        var guidAnchor = Attribute("objectGUID", AttributeDataType.Guid);
        var textAnchor = Attribute("accountId", AttributeDataType.Text);
        var caseExactAnchor = Attribute("code", AttributeDataType.Text, caseExact: true);
        var numberAnchor = Attribute("employeeNumber", AttributeDataType.Number);
        var longAnchor = Attribute("bigNumber", AttributeDataType.LongNumber);
        var decimalAnchor = Attribute("decimalNumber", AttributeDataType.Decimal);
        var secondary = Attribute("distinguishedName", AttributeDataType.Text, secondary: true);
        objectType.Attributes.AddRange([guidAnchor, textAnchor, caseExactAnchor, numberAnchor, longAnchor, decimalAnchor, secondary]);
        var partition = new ConnectedSystemPartition { Name = "dc=glitterband", ExternalId = "dc=glitterband", ConnectedSystem = connectedSystem };
        seed.AddRange(connectorDefinition, connectedSystem, objectType, partition);
        await seed.SaveChangesAsync();

        return new Seeded(NewContext, connectedSystem.Id, objectType.Id, partition.Id,
            guidAnchor, textAnchor, caseExactAnchor, numberAnchor, longAnchor, decimalAnchor, secondary);
    }

    #endregion
}
