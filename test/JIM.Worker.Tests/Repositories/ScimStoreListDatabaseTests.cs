// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Data.Repositories;
using JIM.Models.Scim;
using JIM.Models.Staging;
using JIM.PostgresData;
using JIM.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Listing a SCIM store (#124, Phase 2c) against real PostgreSQL: the predicate model rendered as parameterised SQL,
/// index paging with an accurate total, and keyset cursors that a deletion mid-walk cannot make skip a live resource.
/// </summary>
/// <remarks>
/// Opt-in via the <c>JIM_TEST_RESET_*</c> environment variables like the other RequiresPostgres fixtures. The in-memory
/// provider cannot run any of this SQL.
/// </remarks>
[TestFixture]
[Category("RequiresPostgres")]
public class ScimStoreListDatabaseTests
{
    private const string EnterpriseUrn = "urn:ietf:params:scim:schemas:extension:enterprise:2.0:User";

    private string _connectionString = null!;
    private int _systemId;
    private int _userTypeId;
    private int _groupTypeId;

    private JimDbContext NewContext() => new(new DbContextOptionsBuilder<JimDbContext>()
        .UseNpgsql(_connectionString)
        .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking)
        .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
        .Options);

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        var dbName = Environment.GetEnvironmentVariable("JIM_TEST_RESET_DB");
        if (string.IsNullOrEmpty(dbName))
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL SCIM store list tests.");

        var host = Environment.GetEnvironmentVariable("JIM_TEST_RESET_HOST") ?? "localhost";
        var user = Environment.GetEnvironmentVariable("JIM_TEST_RESET_USER") ?? "postgres";
        var pass = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PASSWORD") ?? "postgres";
        var port = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PORT") ?? "5432";
        _connectionString = $"Host={host};Port={port};Database={dbName};Username={user};Password={pass}";

        using var ctx = NewContext();
        ctx.Database.Migrate();
    }

    [SetUp]
    public async Task SetUpAsync()
    {
        await PostgresTestDatabase.ResetAsync(_connectionString);
        (_systemId, _userTypeId, _groupTypeId) = await SeedSystemAsync("list");
    }

    // ─── Paging ───

    [Test]
    public async Task ListLiveResourcesAsync_IndexPaging_OrdersByIdAndCountsEverythingAsync()
    {
        var ids = (await InsertUsersAsync(5)).Order().ToList();

        var first = await ListAsync(new ScimStoreQuery { ResourceTypeId = _userTypeId, Count = 2 });
        var last = await ListAsync(new ScimStoreQuery { ResourceTypeId = _userTypeId, Count = 2, StartIndex = 5 });
        var beyond = await ListAsync(new ScimStoreQuery { ResourceTypeId = _userTypeId, Count = 2, StartIndex = 7 });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(first.TotalResults, Is.EqualTo(5));
            Assert.That(first.HasMore, Is.True);
            Assert.That(last.Resources, Has.Count.EqualTo(1));
            Assert.That(last.HasMore, Is.False);
            Assert.That(beyond.Resources, Is.Empty);
            Assert.That(beyond.TotalResults, Is.EqualTo(5));
        }

        // PostgreSQL orders uuids by their bytes, which is not .NET's Guid order; paging through must still see each once.
        var walked = new List<Guid>();
        for (var start = 1; start <= 5; start += 2)
            walked.AddRange((await ListAsync(new ScimStoreQuery { ResourceTypeId = _userTypeId, Count = 2, StartIndex = start })).Resources.Select(r => r.Id));
        Assert.That(walked, Is.EquivalentTo(ids));
    }

    [Test]
    public async Task ListLiveResourcesAsync_Cursor_WalksEveryLiveResourceOnceEvenWhenOneIsDeletedMidWalkAsync()
    {
        var ids = await InsertUsersAsync(7);

        var walked = new List<Guid>();
        Guid? after = null;
        var deleted = Guid.Empty;
        while (true)
        {
            var page = await ListAsync(new ScimStoreQuery { ResourceTypeId = _userTypeId, Count = 2, After = after });
            walked.AddRange(page.Resources.Select(r => r.Id));
            if (deleted == Guid.Empty)
            {
                // Delete one already seen: with index paging this shifts every later page by one and skips a resource.
                deleted = page.Resources[0].Id;
                await TombstoneAsync(_userTypeId, deleted);
            }

            if (!page.HasMore)
                break;
            after = page.Resources[^1].Id;
        }

        Assert.That(walked, Is.EquivalentTo(ids), "every resource live when the walk began is seen exactly once");
    }

    [Test]
    public async Task ListLiveResourcesAsync_CountZero_ReturnsOnlyTheTotalAsync()
    {
        await InsertUsersAsync(3);

        var page = await ListAsync(new ScimStoreQuery { ResourceTypeId = _userTypeId, Count = 0 });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(page.TotalResults, Is.EqualTo(3));
            Assert.That(page.Resources, Is.Empty);
        }
    }

    [Test]
    public async Task ListLiveResourcesAsync_LeavesOutTombstonesOtherTypesAndOtherSystemsAsync()
    {
        var ids = await InsertUsersAsync(3);
        await TombstoneAsync(_userTypeId, ids[0]);
        await InsertAsync(_groupTypeId, """{"displayName":"Staff"}""");
        var (otherSystem, otherUserType, _) = await SeedSystemAsync("other");
        await InsertAsync(otherUserType, """{"userName":"elsewhere"}""", systemId: otherSystem);

        var page = await ListAsync(new ScimStoreQuery { ResourceTypeId = _userTypeId, Count = 10 });

        Assert.That(page.Resources.Select(r => r.Id), Is.EquivalentTo(ids.Skip(1)));
    }

    // ─── Filters ───

    [Test]
    public async Task Filter_UniqueValue_FindsTheHolderThroughTheUniqueValueRowsAsync()
    {
        var ada = await InsertAsync(_userTypeId, """{"userName":"Ada"}""", [("userName", "ADA")]);
        await InsertAsync(_userTypeId, """{"userName":"Grace"}""", [("userName", "GRACE")]);

        Assert.That(await MatchAsync(new ScimStoreUniqueValueFilter("userName", "ADA")), Is.EqualTo(new[] { ada }));
    }

    [Test]
    public async Task Filter_IdAndNothingAsync()
    {
        var ada = await InsertAsync(_userTypeId, """{"userName":"Ada"}""");
        await InsertAsync(_userTypeId, """{"userName":"Grace"}""");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(await MatchAsync(new ScimStoreIdFilter(ada)), Is.EqualTo(new[] { ada }));
            Assert.That(await MatchAsync(ScimStoreNothingFilter.Instance), Is.Empty);
        }
    }

    [Test]
    public async Task Filter_LastModified_ComparesTheStampAsync()
    {
        var early = await InsertAsync(_userTypeId, """{"userName":"early"}""");
        var cut = await LastModifiedAsync(early);
        var late = await InsertAsync(_userTypeId, """{"userName":"late"}""");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(await MatchAsync(new ScimStoreLastModifiedFilter(ScimStoreComparison.GreaterThan, cut)), Is.EqualTo(new[] { late }));
            Assert.That(await MatchAsync(new ScimStoreLastModifiedFilter(ScimStoreComparison.GreaterOrEqual, cut)), Is.EquivalentTo(new[] { early, late }));
            Assert.That(await MatchAsync(new ScimStoreLastModifiedFilter(ScimStoreComparison.LessThan, cut)), Is.Empty);
            Assert.That(await MatchAsync(new ScimStoreLastModifiedFilter(ScimStoreComparison.LessOrEqual, cut)), Is.EqualTo(new[] { early }));
            Assert.That(await MatchAsync(new ScimStoreLastModifiedFilter(ScimStoreComparison.Equal, cut)), Is.EqualTo(new[] { early }));
        }
    }

    [TestCase(ScimStoreTextMatch.Equal, "lovelace", false, true)]
    [TestCase(ScimStoreTextMatch.Equal, "lovelace", true, false)]
    [TestCase(ScimStoreTextMatch.Equal, "Lovelace", true, true)]
    [TestCase(ScimStoreTextMatch.Contains, "VEL", false, true)]
    [TestCase(ScimStoreTextMatch.Contains, "VEL", true, false)]
    [TestCase(ScimStoreTextMatch.StartsWith, "love", false, true)]
    [TestCase(ScimStoreTextMatch.StartsWith, "lace", false, false)]
    public async Task Filter_Text_MatchesWithTheAttributesCaseRuleAsync(ScimStoreTextMatch match, string text, bool caseExact, bool expected)
    {
        var ada = await InsertAsync(_userTypeId, """{"userName":"ada","name":{"familyName":"Lovelace"}}""");

        var matched = await MatchAsync(new ScimStoreTextFilter(["name", "familyName"], false, null, match, text, caseExact));

        Assert.That(matched, expected ? Is.EqualTo(new[] { ada }) : Is.Empty);
    }

    [Test]
    public async Task Filter_TextInAMultiValuedComplexAttribute_MatchesAnyEntryAsync()
    {
        var ada = await InsertAsync(_userTypeId, """{"userName":"ada","emails":[{"value":"ada@home.example","type":"home"},{"value":"ada@work.example"}]}""");
        await InsertAsync(_userTypeId, """{"userName":"grace","emails":[{"value":"grace@work.example"}]}""");
        await InsertAsync(_userTypeId, """{"userName":"alan"}""");

        var matched = await MatchAsync(new ScimStoreTextFilter(["emails"], true, "value", ScimStoreTextMatch.Equal, "ADA@WORK.EXAMPLE", false));

        Assert.That(matched, Is.EqualTo(new[] { ada }));
    }

    [Test]
    public async Task Filter_TextInAMultiValuedSimpleAttribute_MatchesAnyValueAsync()
    {
        var ada = await InsertAsync(_userTypeId, """{"userName":"ada","tags":["red","blue"]}""");
        await InsertAsync(_userTypeId, """{"userName":"grace","tags":"blue-not-an-array"}""");

        var matched = await MatchAsync(new ScimStoreTextFilter(["tags"], true, null, ScimStoreTextMatch.Equal, "blue", true));

        Assert.That(matched, Is.EqualTo(new[] { ada }), "a value that is not the array the schema promises matches nothing rather than failing");
    }

    [Test]
    public async Task Filter_TextUnderAnExtension_FollowsThePathAsync()
    {
        var ada = await InsertAsync(_userTypeId, $$$"""{"userName":"ada","{{{EnterpriseUrn}}}":{"department":"R&D"}}""");
        await InsertAsync(_userTypeId, """{"userName":"grace"}""");

        Assert.That(await MatchAsync(new ScimStoreTextFilter([EnterpriseUrn, "department"], false, null, ScimStoreTextMatch.Equal, "r&d", false)),
            Is.EqualTo(new[] { ada }));
    }

    [Test]
    public async Task Filter_Boolean_MatchesTheJsonBooleanAsync()
    {
        var off = await InsertAsync(_userTypeId, """{"userName":"ada","active":false}""");
        await InsertAsync(_userTypeId, """{"userName":"grace","active":true}""");
        await InsertAsync(_userTypeId, """{"userName":"alan","active":"false"}""");

        Assert.That(await MatchAsync(new ScimStoreBooleanFilter(["active"], false)), Is.EqualTo(new[] { off }));
    }

    [Test]
    public async Task Filter_AndOr_CombineAsync()
    {
        var ada = await InsertAsync(_userTypeId, """{"userName":"ada","active":true}""", [("userName", "ADA")]);
        var grace = await InsertAsync(_userTypeId, """{"userName":"grace","active":false}""", [("userName", "GRACE")]);
        await InsertAsync(_userTypeId, """{"userName":"alan","active":true}""", [("userName", "ALAN")]);

        var either = new ScimStoreLogicalFilter(ScimStoreLogicalOperator.Or,
            new ScimStoreUniqueValueFilter("userName", "ADA"), new ScimStoreUniqueValueFilter("userName", "GRACE"));
        var both = new ScimStoreLogicalFilter(ScimStoreLogicalOperator.And, either, new ScimStoreBooleanFilter(["active"], true));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(await MatchAsync(either), Is.EquivalentTo(new[] { ada, grace }));
            Assert.That(await MatchAsync(both), Is.EqualTo(new[] { ada }));
        }
    }

    [Test]
    public async Task Filter_ValuesAndPathsAreParametersNotSqlAsync()
    {
        const string hostile = """x' OR '1'='1"; DROP TABLE "ScimStoreResources"; --""";
        var ada = await InsertAsync(_userTypeId, """{"userName":"ada"}""");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(await MatchAsync(new ScimStoreTextFilter([hostile], false, null, ScimStoreTextMatch.Contains, hostile, false)), Is.Empty);
            Assert.That(await MatchAsync(new ScimStoreTextFilter(["emails"], true, hostile, ScimStoreTextMatch.Equal, hostile, false)), Is.Empty);
            Assert.That(await MatchAsync(new ScimStoreUniqueValueFilter(hostile, hostile)), Is.Empty);
            Assert.That(await MatchAsync(new ScimStoreIdFilter(ada)), Is.EqualTo(new[] { ada }), "the table is still there");
        }
    }

    [Test]
    public async Task Filter_TotalCountsOnlyMatchesAsync()
    {
        await InsertAsync(_userTypeId, """{"userName":"ada","active":true}""");
        await InsertAsync(_userTypeId, """{"userName":"grace","active":false}""");

        var page = await ListAsync(new ScimStoreQuery { ResourceTypeId = _userTypeId, Count = 10, Filter = new ScimStoreBooleanFilter(["active"], true) });

        Assert.That(page.TotalResults, Is.EqualTo(1));
    }

    // ─── References for a page ───

    [Test]
    public async Task GetReferencesAsync_ForSeveralSources_GroupsThemBySourceAsync()
    {
        var ada = await InsertAsync(_userTypeId, """{"userName":"ada"}""");
        var staff = await InsertAsync(_groupTypeId, """{"displayName":"Staff"}""");
        var admins = await InsertAsync(_groupTypeId, """{"displayName":"Admins"}""");
        var empty = await InsertAsync(_groupTypeId, """{"displayName":"Empty"}""");
        await WriteAsync(async s =>
        {
            await s.AddReferencesAsync(staff, "members", [ada]);
            await s.AddReferencesAsync(admins, "members", [ada, staff]);
        });

        await using var ctx = NewContext();
        var references = await new PostgresDataRepository(ctx).ScimStore.GetReferencesAsync([staff, admins, empty]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(references[staff], Is.EqualTo(new[] { new ScimStoreReferenceTarget("members", ada, _userTypeId) }));
            Assert.That(references[admins].Select(r => r.TargetId), Is.EquivalentTo(new[] { ada, staff }));
            Assert.That(references.ContainsKey(empty), Is.False);
        }
    }

    // ─── Reads for import ───

    [Test]
    public async Task ReadForImportAsync_Full_WalksEveryLiveResourceOfEveryTypeByIdWithTheirReferencesAsync()
    {
        var ada = await InsertAsync(_userTypeId, """{"userName":"ada"}""");
        var grace = await InsertAsync(_userTypeId, """{"userName":"grace"}""");
        var gone = await InsertAsync(_userTypeId, """{"userName":"gone"}""");
        await TombstoneAsync(_userTypeId, gone);
        var staff = await InsertAsync(_groupTypeId, """{"displayName":"Staff"}""");
        await WriteAsync(async s => await s.AddReferencesAsync(staff, "members", [ada, grace]));

        var walked = new List<ScimStoreResource>();
        var references = new Dictionary<Guid, IReadOnlyList<ScimStoreReferenceTarget>>();
        Guid? after = null;
        while (true)
        {
            var page = await ReadForImportAsync(new ScimStoreImportQuery { After = after, Count = 2 });
            walked.AddRange(page.Resources);
            foreach (var (source, held) in page.References)
                references[source] = held;
            if (!page.HasMore)
                break;
            after = page.Resources[^1].Id;
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(walked.Select(r => r.Id), Is.EquivalentTo(new[] { ada, grace, staff }), "live resources of every type; no tombstone");
            Assert.That(walked.Select(r => r.ResourceTypeId).Distinct(), Is.EquivalentTo(new[] { _userTypeId, _groupTypeId }));
            Assert.That(references[staff].Select(r => r.TargetId), Is.EquivalentTo(new[] { ada, grace }));
        }
    }

    [Test]
    public async Task ReadForImportAsync_Changes_ReadsLiveResourcesAndTombstonesInTheRangeOnlyAsync()
    {
        var before = await InsertAsync(_userTypeId, """{"userName":"before"}""");
        var from = await StateSequenceAsync();
        var ada = await InsertAsync(_userTypeId, """{"userName":"ada"}""");
        await TombstoneAsync(_userTypeId, before);
        var upTo = await StateSequenceAsync();
        var after = await InsertAsync(_userTypeId, """{"userName":"after"}""");

        var page = await ReadForImportAsync(new ScimStoreImportQuery { Count = 10, ChangedAfter = from, ChangedUpTo = upTo });

        var byId = page.Resources.ToDictionary(r => r.Id);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(byId.Keys, Is.EquivalentTo(new[] { ada, before }), "the deletion in the range is read; the write after it is not");
            Assert.That(byId[before].DeletedAt, Is.Not.Null);
            Assert.That(byId[before].Payload, Is.Null);
            Assert.That(byId[ada].DeletedAt, Is.Null);
            Assert.That(byId.ContainsKey(after), Is.False);
        }
    }

    [Test]
    public async Task ReadForImportAsync_Changes_PagesByIdWithinTheRangeAsync()
    {
        var from = await StateSequenceAsync();
        var ids = await InsertUsersAsync(5);
        var upTo = await StateSequenceAsync();

        var walked = new List<Guid>();
        Guid? after = null;
        while (true)
        {
            var page = await ReadForImportAsync(new ScimStoreImportQuery { After = after, Count = 2, ChangedAfter = from, ChangedUpTo = upTo });
            walked.AddRange(page.Resources.Select(r => r.Id));
            if (!page.HasMore)
                break;
            after = page.Resources[^1].Id;
        }

        Assert.That(walked, Is.EquivalentTo(ids));
    }

    // ─── Helpers ───

    private async Task<ScimStorePage> ListAsync(ScimStoreQuery query)
    {
        await using var ctx = NewContext();
        return await new PostgresDataRepository(ctx).ScimStore.ListLiveResourcesAsync(_systemId, query);
    }

    private async Task<ScimStoreImportPage> ReadForImportAsync(ScimStoreImportQuery query)
    {
        await using var ctx = NewContext();
        return await new PostgresDataRepository(ctx).ScimStore.ReadForImportAsync(_systemId, query);
    }

    private async Task<long> StateSequenceAsync()
    {
        await using var ctx = NewContext();
        return (await new PostgresDataRepository(ctx).ScimStore.GetStateAsync(_systemId))?.LastChangeSequence ?? 0;
    }

    private async Task<List<Guid>> MatchAsync(ScimStoreFilter filter) =>
        (await ListAsync(new ScimStoreQuery { ResourceTypeId = _userTypeId, Count = 100, Filter = filter })).Resources.Select(r => r.Id).ToList();

    private async Task<List<Guid>> InsertUsersAsync(int count)
    {
        var ids = new List<Guid>();
        for (var i = 0; i < count; i++)
            ids.Add(await InsertAsync(_userTypeId, $$"""{"userName":"user{{i}}"}"""));
        return ids;
    }

    private async Task<Guid> InsertAsync(int typeId, string payload, (string Attribute, string Value)[]? unique = null, int? systemId = null)
    {
        var id = Guid.NewGuid();
        await WriteAsync(async s => await s.InsertResourceAsync(
            new ScimStoreResource { Id = id, ResourceTypeId = typeId, Created = DateTime.UtcNow, Payload = payload },
            (unique ?? []).Select(u => new ScimStoreUniqueValue { Attribute = u.Attribute, Value = u.Value }).ToList()), systemId);
        return id;
    }

    private Task TombstoneAsync(int typeId, Guid id) => WriteAsync(async s => await s.TombstoneResourceAsync(typeId, id));

    private async Task WriteAsync(Func<IScimStoreWriteSession, Task> write, int? systemId = null)
    {
        await using var ctx = NewContext();
        await using var session = (await new PostgresDataRepository(ctx).ScimStore.BeginWriteAsync(systemId ?? _systemId))!;
        await write(session);
        await session.CommitAsync();
    }

    private async Task<DateTime> LastModifiedAsync(Guid id)
    {
        await using var ctx = NewContext();
        return (await ctx.ScimStoreResources.SingleAsync(r => r.Id == id)).LastModified;
    }

    private async Task<(int SystemId, int UserTypeId, int GroupTypeId)> SeedSystemAsync(string suffix)
    {
        int systemId;
        await using (var ctx = NewContext())
        {
            var definition = new ConnectorDefinition { Name = $"scim-list-def-{suffix}" };
            ctx.ConnectorDefinitions.Add(definition);
            await ctx.SaveChangesAsync();
            var system = new ConnectedSystem { Name = $"scim-list-system-{suffix}", ConnectorDefinitionId = definition.Id };
            ctx.ConnectedSystems.Add(system);
            await ctx.SaveChangesAsync();
            systemId = system.Id;
        }

        await using var context = NewContext();
        var store = new PostgresDataRepository(context).ScimStore;
        await store.AddDefinitionsAsync(systemId,
            [Schema("urn:test:User"), Schema("urn:test:Group")],
            [ResourceType("User", "/Users", "urn:test:User"), ResourceType("Group", "/Groups", "urn:test:Group")]);
        var types = await store.GetResourceTypesAsync(systemId);
        return (systemId, types.Single(t => t.Name == "User").Id, types.Single(t => t.Name == "Group").Id);
    }

    private static ScimStoreSchema Schema(string urn) =>
        new() { Urn = urn, Definition = $$"""{"id":"{{urn}}","name":"{{urn}}","attributes":[]}""" };

    private static ScimStoreResourceType ResourceType(string name, string endpoint, string schemaUrn) => new()
    {
        Name = name,
        Endpoint = endpoint,
        Definition = $$"""{"id":"{{name}}","name":"{{name}}","endpoint":"{{endpoint}}","schema":"{{schemaUrn}}","schemaExtensions":[]}"""
    };
}
