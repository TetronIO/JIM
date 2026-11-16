// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Staging;
using JIM.PostgresData;
using JIM.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Deletion detection's two reads (#2521, Phase 3), against the single-value lookup by attribute they replace: resolving
/// many missing External IDs in one statement must name, for each, exactly the object the lookup names (the first by id,
/// any Object Type and status, text compared as the attribute declares, #2051, decimals numerically, #1283), and loading
/// the objects it marks by id must give the graph the lookup gave, tracked as it was.
/// </summary>
/// <remarks>
/// Runs against real PostgreSQL: the folding and ordering under test are the database's own, which the in-memory suite
/// cannot see. Opt-in via the <c>JIM_TEST_RESET_*</c> environment variables; <c>SetUp</c> TRUNCATEs every table.
/// </remarks>
[TestFixture]
[Category("RequiresPostgres")]
public class DeletionCandidateLookupDatabaseTests
{
    private string _connectionString = null!;
    private int _systemId;
    private readonly Dictionary<string, int> _attributeIds = [];
    private readonly Dictionary<string, int> _typeIds = [];

    private static readonly Guid GuidAnchor = Guid.Parse("6d1d4a2c-0000-4000-8000-00000000b001");

    private JimDbContext NewContext(bool trackAll = false)
    {
        var builder = new DbContextOptionsBuilder<JimDbContext>()
            .UseNpgsql(_connectionString)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning));
        // The Worker tracks by default (src/JIM.Worker/Program.cs); deletion detection runs on its context.
        if (trackAll)
            builder.UseQueryTrackingBehavior(QueryTrackingBehavior.TrackAll);
        return new JimDbContext(builder.Options);
    }

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        var connectionString = PostgresTestDatabase.ConnectionStringFromEnvironment();
        if (connectionString == null)
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL deletion candidate lookup tests.");
        _connectionString = connectionString;

        TestUtilities.SetEnvironmentVariables();
        using var ctx = NewContext();
        ctx.Database.Migrate();
    }

    [SetUp]
    public async Task SetUpAsync()
    {
        await PostgresTestDatabase.ResetAsync(_connectionString);
        _attributeIds.Clear();
        _typeIds.Clear();
        await SeedAsync();
    }

    [Test]
    public async Task GetConnectedSystemObjectExternalIdMatchesAsync_TextIgnoringCase_NamesWhatTheLookupNamesAsync()
    {
        // Another case, two objects holding one value, two differing only in case, a non-ASCII value in another case, a
        // value nobody holds, and a value asked for twice.
        await AssertSameAsLookupAsync("user.id", ["KEPT", "duplicate", "twin", "émile", "nobody", "kept"],
            (repository, value) => repository.GetConnectedSystemObjectByAttributeAsync(_systemId, _attributeIds["user.id"], value));
    }

    [Test]
    public async Task GetConnectedSystemObjectExternalIdMatchesAsync_TextCaseExact_NamesWhatTheLookupNamesAsync()
    {
        await AssertSameAsLookupAsync("badge.code", ["ABC", "abc", "Abc", "nobody"],
            (repository, value) => repository.GetConnectedSystemObjectByAttributeAsync(_systemId, _attributeIds["badge.code"], value));
    }

    [Test]
    public async Task GetConnectedSystemObjectExternalIdMatchesAsync_Number_NamesWhatTheLookupNamesAsync()
    {
        await AssertSameAsLookupAsync("account.number", [1, 2, 3, 2],
            (repository, value) => repository.GetConnectedSystemObjectByAttributeAsync(_systemId, _attributeIds["account.number"], value));
    }

    [Test]
    public async Task GetConnectedSystemObjectExternalIdMatchesAsync_LongNumber_NamesWhatTheLookupNamesAsync()
    {
        await AssertSameAsLookupAsync("row.key", [9_007_199_254_740_993L, 7L],
            (repository, value) => repository.GetConnectedSystemObjectByAttributeAsync(_systemId, _attributeIds["row.key"], value));
    }

    [Test]
    public async Task GetConnectedSystemObjectExternalIdMatchesAsync_Decimal_ComparesNumericallyAsTheLookupDoesAsync()
    {
        await AssertSameAsLookupAsync("ledger.number",
            [decimal.Parse("4200", CultureInfo.InvariantCulture), decimal.Parse("4200.00", CultureInfo.InvariantCulture), 1m],
            (repository, value) => repository.GetConnectedSystemObjectByAttributeAsync(_systemId, _attributeIds["ledger.number"], value));
    }

    [Test]
    public async Task GetConnectedSystemObjectExternalIdMatchesAsync_Guid_NamesWhatTheLookupNamesAsync()
    {
        await AssertSameAsLookupAsync("device.deviceId", [GuidAnchor, Guid.Parse("6d1d4a2c-0000-4000-8000-00000000b0ff")],
            (repository, value) => repository.GetConnectedSystemObjectByAttributeAsync(_systemId, _attributeIds["device.deviceId"], value));
    }

    [Test]
    public async Task GetConnectedSystemObjectExternalIdMatchesAsync_NoValues_AnswersNothingAsync()
    {
        await using var ctx = NewContext();
        var matches = await new PostgresDataRepository(ctx).ConnectedSystems
            .GetConnectedSystemObjectExternalIdMatchesAsync(_systemId, _attributeIds["user.id"], Array.Empty<string>());

        Assert.That(matches, Is.Empty);
    }

    [Test]
    public async Task GetConnectedSystemObjectsForDeletionDetectionAsync_LoadsTheGraphTheLookupLoadsAsync()
    {
        Guid groupId;
        await using (var lookupContext = NewContext(trackAll: true))
        {
            var group = await new PostgresDataRepository(lookupContext).ConnectedSystems
                .GetConnectedSystemObjectByAttributeAsync(_systemId, _attributeIds["user.id"], "group");
            groupId = group!.Id;
        }

        await using var ctx = NewContext(trackAll: true);
        var repository = new PostgresDataRepository(ctx).ConnectedSystems;
        var loaded = await repository.GetConnectedSystemObjectsForDeletionDetectionAsync(_systemId, [groupId, Guid.Parse("00000000-0000-0000-0000-0000000000ff")]);

        Assert.That(loaded.Select(cso => cso.Id), Is.EqualTo(new[] { groupId }), "an id not found is left out");
        var loadedGroup = loaded[0];

        await using var lookupCtx = NewContext(trackAll: true);
        var lookedUp = (await new PostgresDataRepository(lookupCtx).ConnectedSystems
            .GetConnectedSystemObjectByAttributeAsync(_systemId, _attributeIds["user.id"], "group"))!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ctx.Entry(loadedGroup).State, Is.EqualTo(EntityState.Unchanged), "tracked, as the lookup's object was");
            Assert.That(lookupCtx.Entry(lookedUp).State, Is.EqualTo(EntityState.Unchanged), "precondition: the lookup tracks");
            Assert.That(Describe(loadedGroup), Is.EqualTo(Describe(lookedUp)));
            Assert.That(loadedGroup.AttributeValues.Single(av => av.ReferenceValueId.HasValue).ReferenceValue?.Type, Is.Not.Null,
                "a resolved reference carries the referenced object's Type");
        }
    }

    /// <summary>
    /// Asks for every value at once and each one alone through the single-value lookup, and requires the same object (and
    /// its status and Object Type) for each, in order.
    /// </summary>
    private async Task AssertSameAsLookupAsync<T>(string attribute, T[] values,
        Func<JIM.Data.Repositories.IConnectedSystemRepository, T, Task<ConnectedSystemObject?>> lookUp) where T : notnull
    {
        await using var ctx = NewContext(trackAll: true);
        var repository = new PostgresDataRepository(ctx).ConnectedSystems;

        var matches = await repository.GetConnectedSystemObjectExternalIdMatchesAsync(_systemId, _attributeIds[attribute], values);

        var expected = new List<string>();
        for (var i = 0; i < values.Length; i++)
        {
            var cso = await lookUp(repository, values[i]);
            expected.Add(cso == null ? $"{values[i]}: none" : $"{values[i]}: {cso.Id} {cso.Status} {cso.TypeId}");
        }

        var actual = values.Zip(matches, (value, match) => match == null ? $"{value}: none" : $"{value}: {match.Id} {match.Status} {match.TypeId}").ToList();
        Assert.That(actual, Is.EqualTo(expected));
    }

    private static string Describe(ConnectedSystemObject cso) =>
        $"type={cso.Type.Name} typeAttributes={cso.Type.Attributes.Count} values=" + string.Join(",", cso.AttributeValues
            .OrderBy(av => av.Id)
            .Select(av => $"{av.Attribute.Name}:{av.StringValue}{av.ReferenceValueId}:{av.ReferenceValue?.Type.Name}"));

    private async Task SeedAsync()
    {
        await using (var seed = NewContext())
        {
            var connectorDefinition = new ConnectorDefinition { Name = "JIM Test Connector", BuiltIn = true };
            var system = new ConnectedSystem { Name = "Directory", ConnectorDefinition = connectorDefinition };

            ConnectedSystemObjectType ObjectType(string name, string externalIdName, AttributeDataType externalIdType, bool caseExact = false)
            {
                var objectType = new ConnectedSystemObjectType { Name = name, ConnectedSystem = system, Selected = true };
                objectType.Attributes.Add(new ConnectedSystemObjectTypeAttribute
                {
                    Name = externalIdName, ConnectedSystemObjectType = objectType, Type = externalIdType, CaseExact = caseExact,
                    AttributePlurality = AttributePlurality.SingleValued, Selected = true, IsExternalId = true
                });
                return objectType;
            }

            var user = ObjectType("user", "id", AttributeDataType.Text);
            user.Attributes.Add(new ConnectedSystemObjectTypeAttribute
            {
                Name = "member", ConnectedSystemObjectType = user, Type = AttributeDataType.Reference,
                AttributePlurality = AttributePlurality.MultiValued, Selected = true
            });
            var types = new[]
            {
                user,
                ObjectType("badge", "code", AttributeDataType.Text, caseExact: true),
                ObjectType("account", "number", AttributeDataType.Number),
                ObjectType("row", "key", AttributeDataType.LongNumber),
                ObjectType("ledger", "number", AttributeDataType.Decimal),
                ObjectType("device", "deviceId", AttributeDataType.Guid)
            };

            seed.AddRange(connectorDefinition, system);
            seed.AddRange(types);
            await seed.SaveChangesAsync();

            _systemId = system.Id;
            foreach (var objectType in types)
            {
                _typeIds[objectType.Name] = objectType.Id;
                foreach (var attribute in objectType.Attributes)
                    _attributeIds[$"{objectType.Name}.{attribute.Name}"] = attribute.Id;
            }
        }

        var kept = Cso("00000000-0000-0000-0000-00000000c001", "user", "id", av => av.StringValue = "kept");
        var csos = new List<ConnectedSystemObject>
        {
            kept,
            Cso("00000000-0000-0000-0000-00000000c003", "user", "id", av => av.StringValue = "duplicate", ConnectedSystemObjectStatus.Obsolete),
            Cso("00000000-0000-0000-0000-00000000c002", "user", "id", av => av.StringValue = "duplicate"),
            Cso("00000000-0000-0000-0000-00000000c005", "user", "id", av => av.StringValue = "TWIN"),
            Cso("00000000-0000-0000-0000-00000000c004", "user", "id", av => av.StringValue = "twin", ConnectedSystemObjectStatus.PendingProvisioning),
            Cso("00000000-0000-0000-0000-00000000c006", "user", "id", av => av.StringValue = "ÉMILE"),
            Cso("00000000-0000-0000-0000-00000000c011", "badge", "code", av => av.StringValue = "ABC"),
            Cso("00000000-0000-0000-0000-00000000c012", "badge", "code", av => av.StringValue = "abc"),
            Cso("00000000-0000-0000-0000-00000000c021", "account", "number", av => av.IntValue = 1),
            Cso("00000000-0000-0000-0000-00000000c023", "account", "number", av => av.IntValue = 2),
            Cso("00000000-0000-0000-0000-00000000c022", "account", "number", av => av.IntValue = 2, ConnectedSystemObjectStatus.Obsolete),
            Cso("00000000-0000-0000-0000-00000000c031", "row", "key", av => av.LongValue = 9_007_199_254_740_993L),
            Cso("00000000-0000-0000-0000-00000000c041", "ledger", "number", av => av.DecimalValue = decimal.Parse("4200.00", CultureInfo.InvariantCulture)),
            Cso("00000000-0000-0000-0000-00000000c051", "device", "deviceId", av => av.GuidValue = GuidAnchor)
        };

        // A group holding a resolved reference to the kept user, so a loaded object's references can be compared.
        var group = Cso("00000000-0000-0000-0000-00000000c061", "user", "id", av => av.StringValue = "group");
        group.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(), AttributeId = _attributeIds["user.member"], ReferenceValueId = kept.Id, UnresolvedReferenceValue = "kept"
        });
        csos.Add(group);

        await using var write = NewContext();
        await new PostgresDataRepository(write).Sync.CreateConnectedSystemObjectsAsync(csos);
    }

    private ConnectedSystemObject Cso(string id, string objectType, string externalIdName, Action<ConnectedSystemObjectAttributeValue> setExternalId,
        ConnectedSystemObjectStatus status = ConnectedSystemObjectStatus.Normal)
    {
        var attributeId = _attributeIds[$"{objectType}.{externalIdName}"];
        var cso = new ConnectedSystemObject
        {
            Id = Guid.Parse(id), ConnectedSystemId = _systemId, TypeId = _typeIds[objectType], ExternalIdAttributeId = attributeId,
            Status = status, Created = DateTime.UtcNow.AddHours(-1)
        };
        var value = new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = attributeId };
        setExternalId(value);
        cso.AttributeValues.Add(value);
        return cso;
    }
}
