// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Models.Core;
using JIM.Models.Search;
using JIM.Models.Staging;
using JIM.PostgresData;
using JIM.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Real-PostgreSQL verification that the predefined-search query translator returns the correct Metaverse Objects
/// for typed (non-text) criteria (#849) and for All/Any group composition and nesting (#850).
/// </summary>
/// <remarks>
/// These exercise <see cref="JIM.Application.Servers.MetaverseServer.GetMetaverseObjectHeadersPagedAsync"/>, whose
/// repository implementation is hand-written raw PostgreSQL (NpgsqlCommand, EXISTS sub-queries, Postgres-only
/// syntax). The EF Core in-memory provider used by unit/workflow tests cannot execute that SQL, so the query
/// semantics that are the entire point of #849 and #850 are only verifiable against a real database. The existing
/// model/API tests assert criteria are created and validated, not that a search returns the right rows; this file
/// closes that gap.
///
/// Opt-in via the same <c>JIM_TEST_RESET_*</c> environment variables as the other database-backed tests; ignored
/// when <c>JIM_TEST_RESET_DB</c> is absent.
/// </remarks>
[TestFixture]
[Category("RequiresPostgres")]
public class PredefinedSearchQueryDatabaseTests
{
    private string _connectionString = null!;

    private JimDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<JimDbContext>()
            .UseNpgsql(_connectionString)
            .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        return new JimDbContext(options);
    }

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        var dbName = Environment.GetEnvironmentVariable("JIM_TEST_RESET_DB");
        if (string.IsNullOrEmpty(dbName))
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL predefined-search query tests.");

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

    private record SeedIds(int TypeId, int DepartmentAttrId, int AgeAttrId, int AccountExpiresAttrId, int ActiveAttrId);

    private static readonly DateTime CarolExpires = new(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime AliceExpires = new(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime BobExpires = new(2026, 12, 31, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// Seeds a User object type with Text/Number/DateTime/Boolean attributes and four users:
    /// Alice (Finance, 30, expires 2026-07-01, active), Bob (Sales, 45, 2026-12-31, active),
    /// Carol (Finance, 50, 2025-01-01, inactive), Dave (Engineering, 25, no expiry, active).
    /// </summary>
    private async Task<SeedIds> SeedAsync()
    {
        await using var ctx = NewContext();
        var type = new MetaverseObjectType { Name = "User", PluralName = "Users", BuiltIn = true };
        var department = new MetaverseAttribute { Name = "Department", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.SingleValued, BuiltIn = true };
        var age = new MetaverseAttribute { Name = "Age", Type = AttributeDataType.Number, AttributePlurality = AttributePlurality.SingleValued, BuiltIn = true };
        var accountExpires = new MetaverseAttribute { Name = "Account Expires", Type = AttributeDataType.DateTime, AttributePlurality = AttributePlurality.SingleValued, BuiltIn = true };
        var active = new MetaverseAttribute { Name = "Active", Type = AttributeDataType.Boolean, AttributePlurality = AttributePlurality.SingleValued, BuiltIn = true };
        type.Attributes.Add(department);
        type.Attributes.Add(age);
        type.Attributes.Add(accountExpires);
        type.Attributes.Add(active);

        MetaverseObject User(string name, string dept, int years, DateTime? expires, bool isActive)
        {
            var mvo = new MetaverseObject { Type = type, CachedDisplayName = name };
            mvo.AttributeValues.Add(new MetaverseObjectAttributeValue { Attribute = department, StringValue = dept });
            mvo.AttributeValues.Add(new MetaverseObjectAttributeValue { Attribute = age, IntValue = years });
            if (expires.HasValue)
                mvo.AttributeValues.Add(new MetaverseObjectAttributeValue { Attribute = accountExpires, DateTimeValue = expires.Value });
            mvo.AttributeValues.Add(new MetaverseObjectAttributeValue { Attribute = active, BoolValue = isActive });
            return mvo;
        }

        ctx.MetaverseObjectTypes.Add(type);
        ctx.MetaverseObjects.Add(User("Alice", "Finance", 30, AliceExpires, true));
        ctx.MetaverseObjects.Add(User("Bob", "Sales", 45, BobExpires, true));
        ctx.MetaverseObjects.Add(User("Carol", "Finance", 50, CarolExpires, false));
        ctx.MetaverseObjects.Add(User("Dave", "Engineering", 25, null, true));
        await ctx.SaveChangesAsync();

        return new SeedIds(type.Id, department.Id, age.Id, accountExpires.Id, active.Id);
    }

    // ─── criterion / group builders ───

    private static PredefinedSearchCriteria Text(int attrId, SearchComparisonType op, string value, bool caseSensitive = false) =>
        new() { MetaverseAttributeId = attrId, ComparisonType = op, StringValue = value, CaseSensitive = caseSensitive };

    private static PredefinedSearchCriteria Number(int attrId, SearchComparisonType op, int value) =>
        new() { MetaverseAttributeId = attrId, ComparisonType = op, IntValue = value };

    private static PredefinedSearchCriteria Date(int attrId, SearchComparisonType op, DateTime utc) =>
        new() { MetaverseAttributeId = attrId, ComparisonType = op, DateTimeValue = utc };

    private static PredefinedSearchCriteria Bool(int attrId, SearchComparisonType op, bool value) =>
        new() { MetaverseAttributeId = attrId, ComparisonType = op, BoolValue = value };

    private static PredefinedSearchCriteriaGroup Group(SearchGroupType type, IEnumerable<PredefinedSearchCriteria>? criteria = null, IEnumerable<PredefinedSearchCriteriaGroup>? children = null)
    {
        var group = new PredefinedSearchCriteriaGroup { Type = type };
        if (criteria != null) group.Criteria.AddRange(criteria);
        if (children != null) group.ChildGroups.AddRange(children);
        return group;
    }

    /// <summary>Persists a predefined search over the seeded type with the supplied top-level groups; returns its id.</summary>
    private async Task<int> PersistSearchAsync(int typeId, params PredefinedSearchCriteriaGroup[] topGroups)
    {
        await using var ctx = NewContext();
        // Track the type so assigning it to the new search's navigation sets the FK rather than re-inserting it
        // (the context defaults to NoTracking).
        var type = await ctx.MetaverseObjectTypes.AsTracking().SingleAsync(t => t.Id == typeId);
        var search = new PredefinedSearch { Name = "Query Test", Uri = "query-test", MetaverseObjectType = type };
        foreach (var g in topGroups)
            search.CriteriaGroups.Add(g);
        ctx.PredefinedSearches.Add(search);
        await ctx.SaveChangesAsync();
        return search.Id;
    }

    /// <summary>Loads the search (populating attribute navigations) and runs it, returning matched display names sorted.</summary>
    private async Task<List<string>> RunAndGetNamesAsync(int searchId)
    {
        await using var ctx = NewContext();
        var jim = new JimApplication(new PostgresDataRepository(ctx));
        var search = await jim.Search.GetPredefinedSearchAsync(searchId);
        Assert.That(search, Is.Not.Null);
        var result = await jim.Metaverse.GetMetaverseObjectHeadersPagedAsync(search!, 1, 100);
        return result.Results.Select(r => r.CachedDisplayName!).OrderBy(n => n).ToList();
    }

    // ─── #849: typed (non-text) criteria comparison ───

    [Test]
    public async Task TypedCriteria_TextEqualsCaseInsensitive_ReturnsMatchesAsync()
    {
        var ids = await SeedAsync();
        var id = await PersistSearchAsync(ids.TypeId, Group(SearchGroupType.All, new[] { Text(ids.DepartmentAttrId, SearchComparisonType.Equals, "finance") }));
        Assert.That(await RunAndGetNamesAsync(id), Is.EqualTo(new[] { "Alice", "Carol" }));
    }

    [Test]
    public async Task TypedCriteria_TextContains_ReturnsMatchesAsync()
    {
        var ids = await SeedAsync();
        // "Engineering" contains "ng"; Finance / Sales do not.
        var id = await PersistSearchAsync(ids.TypeId, Group(SearchGroupType.All, new[] { Text(ids.DepartmentAttrId, SearchComparisonType.Contains, "ng") }));
        Assert.That(await RunAndGetNamesAsync(id), Is.EqualTo(new[] { "Dave" }));
    }

    [Test]
    public async Task TypedCriteria_NumberGreaterThan_ReturnsMatchesAsync()
    {
        var ids = await SeedAsync();
        var id = await PersistSearchAsync(ids.TypeId, Group(SearchGroupType.All, new[] { Number(ids.AgeAttrId, SearchComparisonType.GreaterThan, 40) }));
        Assert.That(await RunAndGetNamesAsync(id), Is.EqualTo(new[] { "Bob", "Carol" }));
    }

    [Test]
    public async Task TypedCriteria_NumberLessThanOrEquals_ReturnsMatchesAsync()
    {
        var ids = await SeedAsync();
        var id = await PersistSearchAsync(ids.TypeId, Group(SearchGroupType.All, new[] { Number(ids.AgeAttrId, SearchComparisonType.LessThanOrEquals, 30) }));
        Assert.That(await RunAndGetNamesAsync(id), Is.EqualTo(new[] { "Alice", "Dave" }));
    }

    [Test]
    public async Task TypedCriteria_DateTimeLessThan_ReturnsMatchesAndExcludesNullAsync()
    {
        var ids = await SeedAsync();
        var boundary = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var id = await PersistSearchAsync(ids.TypeId, Group(SearchGroupType.All, new[] { Date(ids.AccountExpiresAttrId, SearchComparisonType.LessThan, boundary) }));
        // Carol (2025) matches; Alice/Bob (2026) do not; Dave (no value) is excluded from ordering comparisons.
        Assert.That(await RunAndGetNamesAsync(id), Is.EqualTo(new[] { "Carol" }));
    }

    [Test]
    public async Task TypedCriteria_BooleanEquals_ReturnsMatchesAsync()
    {
        var ids = await SeedAsync();
        var id = await PersistSearchAsync(ids.TypeId, Group(SearchGroupType.All, new[] { Bool(ids.ActiveAttrId, SearchComparisonType.Equals, false) }));
        Assert.That(await RunAndGetNamesAsync(id), Is.EqualTo(new[] { "Carol" }));
    }

    // ─── #850: All/Any group semantics and nesting ───

    [Test]
    public async Task GroupSemantics_All_CombinesWithAndAsync()
    {
        var ids = await SeedAsync();
        // Finance AND Age > 40 -> only Carol (Alice is Finance but 30).
        var id = await PersistSearchAsync(ids.TypeId, Group(SearchGroupType.All, new[]
        {
            Text(ids.DepartmentAttrId, SearchComparisonType.Equals, "Finance"),
            Number(ids.AgeAttrId, SearchComparisonType.GreaterThan, 40)
        }));
        Assert.That(await RunAndGetNamesAsync(id), Is.EqualTo(new[] { "Carol" }));
    }

    [Test]
    public async Task GroupSemantics_Any_CombinesWithOrAsync()
    {
        var ids = await SeedAsync();
        // Sales OR Age < 30 -> Bob (Sales) and Dave (25).
        var id = await PersistSearchAsync(ids.TypeId, Group(SearchGroupType.Any, new[]
        {
            Text(ids.DepartmentAttrId, SearchComparisonType.Equals, "Sales"),
            Number(ids.AgeAttrId, SearchComparisonType.LessThan, 30)
        }));
        Assert.That(await RunAndGetNamesAsync(id), Is.EqualTo(new[] { "Bob", "Dave" }));
    }

    [Test]
    public async Task GroupSemantics_NestedAnyWithinAll_EvaluatesMixedLogicAsync()
    {
        var ids = await SeedAsync();
        // (Department = Finance OR Department = Sales) AND Active = true -> Alice, Bob (Carol is Finance but inactive).
        var nested = Group(SearchGroupType.Any, new[]
        {
            Text(ids.DepartmentAttrId, SearchComparisonType.Equals, "Finance"),
            Text(ids.DepartmentAttrId, SearchComparisonType.Equals, "Sales")
        });
        var id = await PersistSearchAsync(ids.TypeId, Group(SearchGroupType.All,
            criteria: new[] { Bool(ids.ActiveAttrId, SearchComparisonType.Equals, true) },
            children: new[] { nested }));
        Assert.That(await RunAndGetNamesAsync(id), Is.EqualTo(new[] { "Alice", "Bob" }));
    }

    [Test]
    public async Task GroupSemantics_MultipleTopLevelGroups_AreOredAsync()
    {
        var ids = await SeedAsync();
        // Two top-level groups: (Age < 30) and (Age >= 50). Top-level groups OR together -> Dave and Carol.
        var id = await PersistSearchAsync(ids.TypeId,
            Group(SearchGroupType.All, new[] { Number(ids.AgeAttrId, SearchComparisonType.LessThan, 30) }),
            Group(SearchGroupType.All, new[] { Number(ids.AgeAttrId, SearchComparisonType.GreaterThanOrEquals, 50) }));
        Assert.That(await RunAndGetNamesAsync(id), Is.EqualTo(new[] { "Carol", "Dave" }));
    }

    [Test]
    public async Task GroupSemantics_EmptyGroup_MatchesAllObjectsOfTypeAsync()
    {
        var ids = await SeedAsync();
        var id = await PersistSearchAsync(ids.TypeId, Group(SearchGroupType.All));
        Assert.That(await RunAndGetNamesAsync(id), Is.EqualTo(new[] { "Alice", "Bob", "Carol", "Dave" }));
    }

    // ─── #1962: negated operators over multi-valued attributes and missing values ───

    private static readonly Guid BadgeA = new("2d1f5c7a-8b0e-4f3a-9c61-000000000001");
    private static readonly Guid BadgeB = new("2d1f5c7a-8b0e-4f3a-9c61-000000000002");
    private static readonly Guid BadgeC = new("2d1f5c7a-8b0e-4f3a-9c61-000000000003");

    private record MultiValuedSeedIds(int TypeId, int GroupsAttrId, int LevelsAttrId, int BadgesAttrId);

    /// <summary>
    /// Seeds a Member type with three multi-valued attributes (Text Groups, Number Levels, Guid Badges) and five
    /// members: Mixed holds a matching value among others; Clear holds only non-matching values; Empty holds nothing;
    /// MarkerOnly holds only asserted-null markers (#91); MarkerAndClear holds a marker beside non-matching values.
    /// "Matching" means: Groups "Finance Readers", Levels 10, Badges A.
    /// </summary>
    private async Task<MultiValuedSeedIds> SeedMultiValuedAsync()
    {
        await using var ctx = NewContext();
        var type = new MetaverseObjectType { Name = "Member", PluralName = "Members", BuiltIn = true };
        var groups = new MetaverseAttribute { Name = "Groups", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.MultiValued, BuiltIn = true };
        var levels = new MetaverseAttribute { Name = "Levels", Type = AttributeDataType.Number, AttributePlurality = AttributePlurality.MultiValued, BuiltIn = true };
        var badges = new MetaverseAttribute { Name = "Badges", Type = AttributeDataType.Guid, AttributePlurality = AttributePlurality.MultiValued, BuiltIn = true };
        type.Attributes.Add(groups);
        type.Attributes.Add(levels);
        type.Attributes.Add(badges);

        MetaverseObject Member(string name, string[] groupValues, int[] levelValues, Guid[] badgeValues, bool withMarkers = false)
        {
            var mvo = new MetaverseObject { Type = type, CachedDisplayName = name };
            mvo.AttributeValues.AddRange(groupValues.Select(v => new MetaverseObjectAttributeValue { Attribute = groups, StringValue = v }));
            mvo.AttributeValues.AddRange(levelValues.Select(v => new MetaverseObjectAttributeValue { Attribute = levels, IntValue = v }));
            mvo.AttributeValues.AddRange(badgeValues.Select(v => new MetaverseObjectAttributeValue { Attribute = badges, GuidValue = v }));
            if (withMarkers)
            {
                mvo.AttributeValues.Add(new MetaverseObjectAttributeValue { Attribute = groups, NullValue = true });
                mvo.AttributeValues.Add(new MetaverseObjectAttributeValue { Attribute = levels, NullValue = true });
                mvo.AttributeValues.Add(new MetaverseObjectAttributeValue { Attribute = badges, NullValue = true });
            }
            return mvo;
        }

        ctx.MetaverseObjectTypes.Add(type);
        ctx.MetaverseObjects.Add(Member("Mixed", ["All Staff", "Finance Readers"], [3, 10], [BadgeA, BadgeB]));
        ctx.MetaverseObjects.Add(Member("Clear", ["All Staff", "Sales"], [3, 20], [BadgeB, BadgeC]));
        ctx.MetaverseObjects.Add(Member("Empty", [], [], []));
        ctx.MetaverseObjects.Add(Member("MarkerOnly", [], [], [], withMarkers: true));
        ctx.MetaverseObjects.Add(Member("MarkerAndClear", ["Sales"], [20], [BadgeC], withMarkers: true));
        await ctx.SaveChangesAsync();

        return new MultiValuedSeedIds(type.Id, groups.Id, levels.Id, badges.Id);
    }

    private static PredefinedSearchCriteria Badge(int attrId, SearchComparisonType op, Guid value) =>
        new() { MetaverseAttributeId = attrId, ComparisonType = op, GuidValue = value };

    /// <summary>
    /// Every negated operator, over every value shape. A negated operator is met when the object holds at least one
    /// value and no value matches the operator it negates, exactly as Synchronisation Rule scoping evaluates it (#1923):
    /// Mixed holds a matching value, Empty and MarkerOnly hold no value at all, and an asserted-null marker is not a
    /// value, so only Clear and MarkerAndClear qualify.
    /// </summary>
    [TestCase("Groups", SearchComparisonType.NotEquals, "Finance Readers")]
    [TestCase("Groups", SearchComparisonType.NotStartsWith, "Fin")]
    [TestCase("Groups", SearchComparisonType.NotEndsWith, "Readers")]
    [TestCase("Groups", SearchComparisonType.NotContains, "Finance")]
    [TestCase("Levels", SearchComparisonType.NotEquals, null)]
    [TestCase("Badges", SearchComparisonType.NotEquals, null)]
    public async Task NegatedOperator_MultiValuedAndMissingValues_MatchesOnlyObjectsHoldingNoMatchingValueAsync(string attribute, SearchComparisonType op, string? textValue)
    {
        var ids = await SeedMultiValuedAsync();
        var criterion = attribute switch
        {
            "Groups" => Text(ids.GroupsAttrId, op, textValue!, caseSensitive: true),
            "Levels" => Number(ids.LevelsAttrId, op, 10),
            _ => Badge(ids.BadgesAttrId, op, BadgeA)
        };

        var id = await PersistSearchAsync(ids.TypeId, Group(SearchGroupType.All, new[] { criterion }));

        Assert.That(await RunAndGetNamesAsync(id), Is.EqualTo(new[] { "Clear", "MarkerAndClear" }));
    }

    [Test]
    public async Task NegatedOperator_SingleValuedAttribute_ExcludesObjectsWithNoValueAsync()
    {
        // Dave has no Account Expires; a negated operator needs a value to be met, as NotEquals already did and as
        // scoping does. The text family used to include him.
        var ids = await SeedAsync();
        var id = await PersistSearchAsync(ids.TypeId, Group(SearchGroupType.All, new[] { Date(ids.AccountExpiresAttrId, SearchComparisonType.NotEquals, CarolExpires) }));
        Assert.That(await RunAndGetNamesAsync(id), Is.EqualTo(new[] { "Alice", "Bob" }));
    }

    [Test]
    public async Task PositiveOperator_MultiValuedAttribute_MatchesWhenAnyValueMatchesAsync()
    {
        var ids = await SeedMultiValuedAsync();
        var id = await PersistSearchAsync(ids.TypeId, Group(SearchGroupType.All, new[] { Text(ids.GroupsAttrId, SearchComparisonType.Contains, "Finance", caseSensitive: true) }));
        Assert.That(await RunAndGetNamesAsync(id), Is.EqualTo(new[] { "Mixed" }));
    }

    /// <summary>
    /// The Synchronisation Rules documentation suggests a Predefined Search to see which Metaverse Objects an export
    /// rule's criteria cover; that advice holds only while the SQL translator and the scoping evaluator agree. Runs every
    /// operator through both over the same persisted objects and requires identical results.
    /// </summary>
    [TestCase(SearchComparisonType.Equals)]
    [TestCase(SearchComparisonType.NotEquals)]
    [TestCase(SearchComparisonType.StartsWith)]
    [TestCase(SearchComparisonType.NotStartsWith)]
    [TestCase(SearchComparisonType.EndsWith)]
    [TestCase(SearchComparisonType.NotEndsWith)]
    [TestCase(SearchComparisonType.Contains)]
    [TestCase(SearchComparisonType.NotContains)]
    public async Task EveryTextOperator_AgreesWithSynchronisationRuleScopingAsync(SearchComparisonType op)
    {
        var ids = await SeedMultiValuedAsync();
        var value = op switch
        {
            SearchComparisonType.Equals or SearchComparisonType.NotEquals => "Finance Readers",
            SearchComparisonType.StartsWith or SearchComparisonType.NotStartsWith => "Fin",
            SearchComparisonType.EndsWith or SearchComparisonType.NotEndsWith => "Readers",
            _ => "Finance"
        };
        var id = await PersistSearchAsync(ids.TypeId, Group(SearchGroupType.All, new[] { Text(ids.GroupsAttrId, op, value, caseSensitive: true) }));

        var searchResults = await RunAndGetNamesAsync(id);
        var scopingResults = await EvaluateScopingAsync(ids.TypeId, ids.GroupsAttrId, op, value);

        Assert.That(searchResults, Is.EqualTo(scopingResults));
    }

    /// <summary>Evaluates one Text scoping criterion against every persisted object of the type, returning the names in scope, sorted.</summary>
    private async Task<List<string>> EvaluateScopingAsync(int typeId, int attributeId, SearchComparisonType op, string value)
    {
        await using var ctx = NewContext();
        var attribute = await ctx.MetaverseAttributes.SingleAsync(a => a.Id == attributeId);
        var mvos = await ctx.MetaverseObjects
            .Include(m => m.AttributeValues)
            .Where(m => m.Type.Id == typeId)
            .ToListAsync();

        var rule = new JIM.Models.Logic.SyncRule { Name = "Parity", Direction = JIM.Models.Logic.SyncRuleDirection.Export };
        var group = new JIM.Models.Logic.SyncRuleScopingCriteriaGroup { Type = SearchGroupType.All };
        group.Criteria.Add(new JIM.Models.Logic.SyncRuleScopingCriteria { MetaverseAttribute = attribute, ComparisonType = op, StringValue = value, CaseSensitive = true });
        rule.ObjectScopingCriteriaGroups.Add(group);

        var scoping = new JIM.Application.Servers.ScopingEvaluationServer();
        return mvos.Where(m => scoping.IsMvoInScopeForExportRule(m, rule)).Select(m => m.CachedDisplayName!).OrderBy(n => n).ToList();
    }
}
