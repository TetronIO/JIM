// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Models.Core;
using JIM.Models.Search;
using JIM.Models.Staging;
using JIM.PostgresData;
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
        await using var ctx = NewContext();
        await ctx.Database.ExecuteSqlRawAsync(@"
            DO $$
            DECLARE r RECORD;
            BEGIN
                FOR r IN (SELECT tablename FROM pg_tables WHERE schemaname = 'public' AND tablename <> '__EFMigrationsHistory') LOOP
                    EXECUTE 'TRUNCATE TABLE ""' || r.tablename || '"" RESTART IDENTITY CASCADE';
                END LOOP;
            END $$;");
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
}
