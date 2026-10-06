// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Core.DTOs;
using JIM.Models.Search;
using JIM.PostgresData;
using JIM.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Real-PostgreSQL verification that a Predefined Search's criteria tree loads, is evaluated, and is accounted for,
/// at every depth an administrator can build.
/// </summary>
/// <remarks>
/// Criteria groups nest to any depth: the REST API and PowerShell add a child group under any group of the search,
/// and the repository places no limit. The search loaders used to spell their Includes out two levels deep, so a
/// group below that was absent when the search ran; its parent then rendered as an empty group, which matches
/// everything, and the search returned objects its criteria exclude. The same two-level assumption sat in the
/// attribute-reference check that guards attribute removal, and in the configuration snapshot (which recurses, but
/// only over what was loaded). The EF Core in-memory provider fixes navigations up regardless of Includes and cannot
/// show any of this, hence a real database.
///
/// Opt-in via the same <c>JIM_TEST_RESET_*</c> environment variables as the other real-database fixtures; ignored
/// when <c>JIM_TEST_RESET_DB</c> is absent.
/// </remarks>
[TestFixture]
[Category("RequiresPostgres")]
public class PredefinedSearchCriteriaDepthDatabaseTests
{
    private const string SearchUri = "deep-criteria";

    private string _connectionString = null!;

    public enum Overload
    {
        ById,
        ByUri,
        DefaultForObjectType
    }

    private JimDbContext NewContext(QueryTrackingBehavior tracking = QueryTrackingBehavior.NoTracking)
    {
        var options = new DbContextOptionsBuilder<JimDbContext>()
            .UseNpgsql(_connectionString)
            .UseQueryTrackingBehavior(tracking)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        return new JimDbContext(options);
    }

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        var dbName = Environment.GetEnvironmentVariable("JIM_TEST_RESET_DB");
        if (string.IsNullOrEmpty(dbName))
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL Predefined Search criteria depth tests.");

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

    [TestCase(Overload.ById)]
    [TestCase(Overload.ByUri)]
    [TestCase(Overload.DefaultForObjectType)]
    public async Task GetPredefinedSearchAsync_CriteriaTreeFourLevelsDeep_LoadsEveryLevelAsync(Overload overload)
    {
        var ids = await SeedAsync();
        var searchId = await PersistSearchAsync(ids.TypeId, Chain(depth: 4, Text(ids.DepartmentAttributeId, "Finance")));

        await using var ctx = NewContext();
        var jim = new JimApplication(new PostgresDataRepository(ctx));
        var search = overload switch
        {
            Overload.ById => await jim.Search.GetPredefinedSearchAsync(searchId),
            Overload.ByUri => await jim.Search.GetPredefinedSearchAsync(SearchUri),
            _ => await jim.Search.GetPredefinedSearchAsync(await ctx.MetaverseObjectTypes.SingleAsync(t => t.Id == ids.TypeId))
        };

        Assert.That(search, Is.Not.Null);
        AssertChainLoaded(search!, depth: 4);
    }

    [Test]
    public async Task GetPredefinedSearchAsync_BranchingTree_PlacesEveryGroupUnderItsOwnParentInPositionOrderAsync()
    {
        var ids = await SeedAsync();

        // Two top-level groups, each with two children, one of which has a child of its own. Groups are added out of
        // position order so the assertion proves the loader orders siblings by Position rather than by insertion.
        var firstTop = Group(SearchGroupType.All, position: 0,
            children:
            [
                Group(SearchGroupType.Any, position: 1, criteria: [Text(ids.DepartmentAttributeId, "Sales")]),
                Group(SearchGroupType.All, position: 0,
                    children: [Group(SearchGroupType.Any, position: 0, criteria: [Text(ids.DepartmentAttributeId, "Finance")])])
            ]);
        var secondTop = Group(SearchGroupType.Any, position: 1,
            children:
            [
                Group(SearchGroupType.All, position: 0, criteria: [Bool(ids.ActiveAttributeId, true)]),
                Group(SearchGroupType.Any, position: 1, criteria: [Text(ids.DepartmentAttributeId, "Engineering")])
            ]);
        var searchId = await PersistSearchAsync(ids.TypeId, secondTop, firstTop);

        await using var ctx = NewContext();
        var search = await new JimApplication(new PostgresDataRepository(ctx)).Search.GetPredefinedSearchAsync(searchId);

        Assert.That(search, Is.Not.Null);
        var loaded = search!;
        Assert.That(loaded.CriteriaGroups.Select(g => g.Position), Is.EqualTo(new[] { 0, 1 }), "top-level groups in position order");
        var top0 = loaded.CriteriaGroups[0];
        var top1 = loaded.CriteriaGroups[1];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(top0.ChildGroups.Select(g => g.Position), Is.EqualTo(new[] { 0, 1 }));
            Assert.That(top0.ChildGroups[0].ChildGroups.Single().Criteria.Single().StringValue, Is.EqualTo("Finance"));
            Assert.That(top0.ChildGroups[0].ChildGroups.Single().ParentGroup, Is.SameAs(top0.ChildGroups[0]));
            Assert.That(top0.ChildGroups[1].Criteria.Single().StringValue, Is.EqualTo("Sales"));
            Assert.That(top0.ChildGroups[1].ChildGroups, Is.Empty);
            Assert.That(top1.ChildGroups.Select(g => g.Position), Is.EqualTo(new[] { 0, 1 }));
            Assert.That(top1.ChildGroups[0].Criteria.Single().BoolValue, Is.True);
            Assert.That(top1.ChildGroups[1].Criteria.Single().StringValue, Is.EqualTo("Engineering"));
        }
    }

    [Test]
    public async Task GetPredefinedSearchAsync_OnAContextThatTracksByDefault_LeavesNothingToSaveAsync()
    {
        var ids = await SeedAsync();
        await PersistSearchAsync(ids.TypeId, Chain(depth: 4, Text(ids.DepartmentAttributeId, "Finance")));

        // Seeding looks Predefined Searches up by URI, and may run on a context that tracks by default. The loaded
        // groups must be tracked with the search: untracked groups hung off a tracked search look like new rows to
        // EF, and the next save would try to insert the whole tree again.
        await using var ctx = NewContext(QueryTrackingBehavior.TrackAll);
        var search = await new JimApplication(new PostgresDataRepository(ctx)).Search.GetPredefinedSearchAsync(SearchUri);

        Assert.That(search, Is.Not.Null);
        AssertChainLoaded(search!, depth: 4);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(ctx.ChangeTracker.HasChanges(), Is.False, "loading must not leave pending changes");
            Assert.That(await ctx.SaveChangesAsync(), Is.Zero, "nothing should be written");
            Assert.That(await ctx.PredefinedSearchCriteriaGroups.CountAsync(), Is.EqualTo(4), "no group duplicated");
        }
    }

    [Test]
    public async Task CreatePredefinedSearchCriteriaGroupAsync_UnderAGroupThreeLevelsDeep_LoadsAsTheFourthLevelAsync()
    {
        var ids = await SeedAsync();
        var searchId = await PersistSearchAsync(ids.TypeId);

        // The REST API and PowerShell path: each child is created under a group found on the freshly loaded search.
        // Before the fix the third level was never loaded, so the fourth could not be created through the API at all.
        await using (var ctx = NewContext())
        {
            var jim = new JimApplication(new PostgresDataRepository(ctx));
            int? parentId = null;
            for (var level = 1; level <= 4; level++)
            {
                var search = await jim.Search.GetPredefinedSearchAsync(searchId);
                Assert.That(search, Is.Not.Null);
                if (parentId.HasValue)
                    Assert.That(FindGroup(search!.CriteriaGroups, parentId.Value), Is.Not.Null, $"the level {level - 1} group should be on the loaded search");

                var created = await jim.Search.CreatePredefinedSearchCriteriaGroupAsync(searchId, parentId, SearchGroupType.All, 0);
                parentId = created.Id;
            }

            Assert.That(await jim.Search.CreatePredefinedSearchCriterionAsync(parentId!.Value,
                Text(ids.DepartmentAttributeId, "Finance")), Is.Not.Null);
        }

        await using var verify = NewContext();
        var loaded = await new JimApplication(new PostgresDataRepository(verify)).Search.GetPredefinedSearchAsync(searchId);
        Assert.That(loaded, Is.Not.Null);
        AssertChainLoaded(loaded!, depth: 4);
    }

    [Test]
    public async Task GetMetaverseObjectHeadersPagedAsync_OnlyCriterionFourLevelsDeep_DoesNotWidenResultsAsync()
    {
        var ids = await SeedAsync();

        // The only criterion is four levels down and requires Finance. If a level is not loaded, its parent renders
        // as an empty group (TRUE) and every user is returned.
        var searchId = await PersistSearchAsync(ids.TypeId, Chain(depth: 4, Text(ids.DepartmentAttributeId, "Finance")));

        Assert.That(await RunAndGetNamesAsync(searchId), Is.EqualTo(new[] { "Alice", "Carol" }));
    }

    [Test]
    public async Task GetMetaverseObjectHeadersPagedAsync_DeepGroupBesideCriteria_IsCombinedWithThemAsync()
    {
        var ids = await SeedAsync();

        // All( Any( Department = Sales, All( Department = Finance, Active = true ) ) ) -> Bob (Sales) and Alice
        // (active Finance); Carol is Finance but inactive. If the third level is not loaded, its clause is silently
        // dropped from the OR and Alice goes missing.
        var third = Group(SearchGroupType.All, criteria: [Text(ids.DepartmentAttributeId, "Finance"), Bool(ids.ActiveAttributeId, true)]);
        var second = Group(SearchGroupType.Any, criteria: [Text(ids.DepartmentAttributeId, "Sales")], children: [third]);
        var searchId = await PersistSearchAsync(ids.TypeId, Group(SearchGroupType.All, children: [second]));

        Assert.That(await RunAndGetNamesAsync(searchId), Is.EqualTo(new[] { "Alice", "Bob" }));
    }

    [Test]
    public async Task GetAttributeReferencesForObjectTypeAsync_AttributeUsedOnlyByACriterionThreeLevelsDeep_ReportsTheCriterionAsync()
    {
        var ids = await SeedAsync();
        await PersistSearchAsync(ids.TypeId, Chain(depth: 3, Text(ids.DepartmentAttributeId, "Finance")));

        // This is the check that stops an attribute being removed from a type while something still uses it. A
        // criterion it cannot see is left filtering on an attribute the type no longer has.
        await using var ctx = NewContext();
        var references = await new PostgresDataRepository(ctx).Metaverse
            .GetAttributeReferencesForObjectTypeAsync(ids.DepartmentAttributeId, ids.TypeId);

        Assert.That(references.Count(r => r.Kind == AttributeReferenceKind.PredefinedSearchCriterion), Is.EqualTo(1));
    }

    [Test]
    public async Task CreateSnapshot_CriteriaTreeFourLevelsDeep_RecordsEveryGroupAsync()
    {
        var ids = await SeedAsync();
        var searchId = await PersistSearchAsync(ids.TypeId, Chain(depth: 4, Text(ids.DepartmentAttributeId, "Finance")));

        // Change history snapshots the search as the repository loads it, so a group the load misses is a group the
        // history never records.
        await using var ctx = NewContext();
        var jim = new JimApplication(new PostgresDataRepository(ctx));
        var search = await jim.Search.GetPredefinedSearchAsync(searchId);
        Assert.That(search, Is.Not.Null);
        var snapshot = jim.ConfigurationSnapshots.CreateSnapshot(search!, new byte[32]);

        var groups = snapshot.Root.Children!.Single(n => n.Key == "criteriaGroups").Children!;
        var depth = 0;
        ConfigurationSnapshotNode? deepest = null;
        while (groups.Count > 0)
        {
            depth++;
            deepest = groups.Single();
            groups = deepest.Children!.Single(n => n.Key == "criteriaGroups").Children!;
        }

        Assert.That(depth, Is.EqualTo(4), "every level of the tree should be in the snapshot");
        Assert.That(deepest!.Children!.Single(n => n.Key == "criteria").Children, Has.Count.EqualTo(1), "the deepest group's criterion");
    }

    private static void AssertChainLoaded(PredefinedSearch search, int depth)
    {
        Assert.That(search.CriteriaGroups, Has.Count.EqualTo(1), "top-level group");
        var group = search.CriteriaGroups.Single();
        for (var level = 2; level <= depth; level++)
        {
            Assert.That(group.ChildGroups, Has.Count.EqualTo(1), $"group at level {level}");
            var child = group.ChildGroups.Single();
            Assert.That(child.ParentGroup, Is.SameAs(group), $"group at level {level} should point at its parent");
            group = child;
        }

        Assert.That(group.ChildGroups, Is.Empty, "the deepest group has no children");
        Assert.That(group.Criteria.Single().MetaverseAttribute, Is.Not.Null, "the criterion's attribute must load for evaluation");
    }

    private static PredefinedSearchCriteriaGroup? FindGroup(IEnumerable<PredefinedSearchCriteriaGroup> groups, int groupId)
    {
        foreach (var group in groups)
        {
            if (group.Id == groupId)
                return group;
            if (FindGroup(group.ChildGroups, groupId) is { } match)
                return match;
        }
        return null;
    }

    // ─── tree builders ───

    private static PredefinedSearchCriteria Text(int attributeId, string value) =>
        new() { MetaverseAttributeId = attributeId, ComparisonType = SearchComparisonType.Equals, StringValue = value, CaseSensitive = false };

    private static PredefinedSearchCriteria Bool(int attributeId, bool value) =>
        new() { MetaverseAttributeId = attributeId, ComparisonType = SearchComparisonType.Equals, BoolValue = value };

    private static PredefinedSearchCriteriaGroup Group(SearchGroupType type, int position = 0,
        IEnumerable<PredefinedSearchCriteria>? criteria = null, IEnumerable<PredefinedSearchCriteriaGroup>? children = null)
    {
        var group = new PredefinedSearchCriteriaGroup { Type = type, Position = position };
        if (criteria != null)
            group.Criteria.AddRange(criteria);
        if (children != null)
            group.ChildGroups.AddRange(children);
        return group;
    }

    /// <summary>
    /// Builds a chain of alternating All / Any groups, <paramref name="depth"/> long, with one criterion in the deepest
    /// group, and returns the top-level group.
    /// </summary>
    private static PredefinedSearchCriteriaGroup Chain(int depth, PredefinedSearchCriteria criterion)
    {
        var top = Group(SearchGroupType.All);
        var current = top;
        for (var level = 2; level <= depth; level++)
        {
            var child = Group(level % 2 == 0 ? SearchGroupType.Any : SearchGroupType.All);
            current.ChildGroups.Add(child);
            current = child;
        }
        current.Criteria.Add(criterion);
        return top;
    }

    // ─── seeding and running ───

    private record SeedIds(int TypeId, int DepartmentAttributeId, int ActiveAttributeId);

    /// <summary>
    /// Seeds a User type with Department (Text) and Active (Boolean) and four users: Alice (Finance, active), Bob
    /// (Sales, active), Carol (Finance, inactive) and Dave (Engineering, active).
    /// </summary>
    private async Task<SeedIds> SeedAsync()
    {
        await using var ctx = NewContext();
        var type = new MetaverseObjectType { Name = "User", PluralName = "Users", BuiltIn = true };
        var department = new MetaverseAttribute { Name = "Department", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.SingleValued, BuiltIn = true };
        var active = new MetaverseAttribute { Name = "Active", Type = AttributeDataType.Boolean, AttributePlurality = AttributePlurality.SingleValued, BuiltIn = true };
        type.Attributes.Add(department);
        type.Attributes.Add(active);

        MetaverseObject User(string name, string dept, bool isActive)
        {
            var mvo = new MetaverseObject { Type = type, CachedDisplayName = name };
            mvo.AttributeValues.Add(new MetaverseObjectAttributeValue { Attribute = department, StringValue = dept });
            mvo.AttributeValues.Add(new MetaverseObjectAttributeValue { Attribute = active, BoolValue = isActive });
            return mvo;
        }

        ctx.MetaverseObjectTypes.Add(type);
        ctx.MetaverseObjects.Add(User("Alice", "Finance", true));
        ctx.MetaverseObjects.Add(User("Bob", "Sales", true));
        ctx.MetaverseObjects.Add(User("Carol", "Finance", false));
        ctx.MetaverseObjects.Add(User("Dave", "Engineering", true));
        await ctx.SaveChangesAsync();

        return new SeedIds(type.Id, department.Id, active.Id);
    }

    /// <summary>
    /// Persists an enabled Predefined Search over the seeded type, the type's default, with the supplied top-level
    /// groups; returns its id.
    /// </summary>
    private async Task<int> PersistSearchAsync(int typeId, params PredefinedSearchCriteriaGroup[] topGroups)
    {
        await using var ctx = NewContext();
        // Track the type so assigning it to the new search's navigation sets the FK rather than re-inserting it.
        var type = await ctx.MetaverseObjectTypes.AsTracking().SingleAsync(t => t.Id == typeId);
        var search = new PredefinedSearch
        {
            Name = "Deep Criteria",
            Uri = SearchUri,
            MetaverseObjectType = type,
            IsDefaultForMetaverseObjectType = true
        };
        foreach (var group in topGroups)
            search.CriteriaGroups.Add(group);
        ctx.PredefinedSearches.Add(search);
        await ctx.SaveChangesAsync();
        return search.Id;
    }

    /// <summary>Loads the search as the portal and API do, runs it, and returns the matched display names, sorted.</summary>
    private async Task<List<string>> RunAndGetNamesAsync(int searchId)
    {
        await using var ctx = NewContext();
        var jim = new JimApplication(new PostgresDataRepository(ctx));
        var search = await jim.Search.GetPredefinedSearchAsync(searchId);
        Assert.That(search, Is.Not.Null);
        var result = await jim.Metaverse.GetMetaverseObjectHeadersPagedAsync(search!, 1, 100);
        return result.Results.Select(r => r.CachedDisplayName!).OrderBy(n => n).ToList();
    }
}
