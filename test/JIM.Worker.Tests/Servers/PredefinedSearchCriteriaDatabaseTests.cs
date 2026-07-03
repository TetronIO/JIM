// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Models.Core;
using JIM.Models.Search;
using JIM.PostgresData;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Real-PostgreSQL verification that creating and updating Predefined Search criteria groups and criteria
/// actually persists.
/// </summary>
/// <remarks>
/// Regression guard for a silent data-loss bug: the global DbContext default is NoTracking (see JIM.Web
/// Program.cs), and the new SearchRepository write methods loaded their target entity without an explicit
/// AsTracking(), then mutated a navigation collection and called SaveChanges. Against a NoTracking context the
/// loaded entity is detached, so SaveChanges persisted nothing while reporting success: adding a criteria group
/// or criterion looked like it worked but never stuck. These tests load through a NoTracking context (matching
/// production) and assert the changes are durable; before the fix the create/update assertions fail.
///
/// The EF Core in-memory provider tracks by default and so cannot reproduce this, hence a real database. Opt-in
/// via the same <c>JIM_TEST_RESET_*</c> environment variables as the sync-rule database tests; ignored when
/// <c>JIM_TEST_RESET_DB</c> is absent.
/// </remarks>
[TestFixture]
[Category("RequiresPostgres")]
public class PredefinedSearchCriteriaDatabaseTests
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
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL predefined-search criteria tests.");

        var host = Environment.GetEnvironmentVariable("JIM_TEST_RESET_HOST") ?? "localhost";
        var user = Environment.GetEnvironmentVariable("JIM_TEST_RESET_USER") ?? "postgres";
        var pass = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PASSWORD") ?? "postgres";
        _connectionString = $"Host={host};Database={dbName};Username={user};Password={pass}";

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

    private record SeedIds(int PredefinedSearchId, int MetaverseAttributeId);

    /// <summary>
    /// Seeds a Metaverse Object Type with one DateTime attribute and an empty Predefined Search over it.
    /// </summary>
    private async Task<SeedIds> SeedAsync()
    {
        await using var seed = NewContext();
        var mvType = new MetaverseObjectType { Name = "User", PluralName = "Users", BuiltIn = true };
        var mvAttr = new MetaverseAttribute { Name = "Account Expires", Type = AttributeDataType.DateTime, AttributePlurality = AttributePlurality.SingleValued, BuiltIn = true };
        mvType.Attributes.Add(mvAttr);

        var search = new PredefinedSearch
        {
            Name = "Users",
            Uri = "users-test",
            MetaverseObjectType = mvType
        };

        seed.MetaverseObjectTypes.Add(mvType);
        seed.PredefinedSearches.Add(search);
        await seed.SaveChangesAsync();

        return new SeedIds(search.Id, mvAttr.Id);
    }

    [Test]
    public async Task CreatePredefinedSearchCriteriaGroupAsync_LoadedNoTracking_PersistsAsync()
    {
        var ids = await SeedAsync();

        int groupId;
        await using (var ctx = NewContext())
        {
            var jim = new JimApplication(new PostgresDataRepository(ctx));
            var group = await jim.Search.CreatePredefinedSearchCriteriaGroupAsync(ids.PredefinedSearchId, null, SearchGroupType.All, 0);
            groupId = group.Id;
            Assert.That(groupId, Is.GreaterThan(0));
        }

        await using var verify = NewContext();
        var persisted = await verify.PredefinedSearchCriteriaGroups.SingleOrDefaultAsync(g => g.Id == groupId);
        Assert.That(persisted, Is.Not.Null, "Creating a Predefined Search Criteria Group must persist.");

        // The group must be linked back to the search (the FK is a shadow property, so assert via the navigation).
        var search = await verify.PredefinedSearches.Include(s => s.CriteriaGroups).SingleAsync(s => s.Id == ids.PredefinedSearchId);
        Assert.That(search.CriteriaGroups.Any(g => g.Id == groupId), Is.True, "The group must belong to the search.");
    }

    [Test]
    public async Task CreatePredefinedSearchCriterionAsync_LoadedNoTracking_PersistsAsync()
    {
        var ids = await SeedAsync();

        int criterionId;
        await using (var ctx = NewContext())
        {
            var jim = new JimApplication(new PostgresDataRepository(ctx));
            var group = await jim.Search.CreatePredefinedSearchCriteriaGroupAsync(ids.PredefinedSearchId, null, SearchGroupType.All, 0);
            var criterion = await jim.Search.CreatePredefinedSearchCriterionAsync(group.Id, new PredefinedSearchCriteria
            {
                MetaverseAttributeId = ids.MetaverseAttributeId,
                ComparisonType = SearchComparisonType.LessThanOrEquals,
                ValueMode = DateCriteriaValueMode.Relative,
                RelativeCount = 7,
                RelativeUnit = RelativeDateUnit.Days,
                RelativeDirection = RelativeDateDirection.FromNow
            });
            Assert.That(criterion, Is.Not.Null);
            criterionId = criterion!.Id;
            Assert.That(criterionId, Is.GreaterThan(0));
        }

        await using var verify = NewContext();
        var persisted = await verify.PredefinedSearchCriteria.SingleOrDefaultAsync(c => c.Id == criterionId);
        Assert.That(persisted, Is.Not.Null, "Creating a Predefined Search Criterion must persist.");
        Assert.That(persisted!.RelativeCount, Is.EqualTo(7));
        Assert.That(persisted.RelativeUnit, Is.EqualTo(RelativeDateUnit.Days));
        Assert.That(persisted.RelativeDirection, Is.EqualTo(RelativeDateDirection.FromNow));
    }

    [Test]
    public async Task UpdatePredefinedSearchCriterionAsync_LoadedNoTracking_PersistsAsync()
    {
        var ids = await SeedAsync();

        int criterionId;
        await using (var ctx = NewContext())
        {
            var jim = new JimApplication(new PostgresDataRepository(ctx));
            var group = await jim.Search.CreatePredefinedSearchCriteriaGroupAsync(ids.PredefinedSearchId, null, SearchGroupType.All, 0);
            var criterion = await jim.Search.CreatePredefinedSearchCriterionAsync(group.Id, new PredefinedSearchCriteria
            {
                MetaverseAttributeId = ids.MetaverseAttributeId,
                ComparisonType = SearchComparisonType.LessThanOrEquals,
                ValueMode = DateCriteriaValueMode.Relative,
                RelativeCount = 7,
                RelativeUnit = RelativeDateUnit.Days,
                RelativeDirection = RelativeDateDirection.FromNow
            });
            criterionId = criterion!.Id;
        }

        // Edit the relative window 7 -> 14 days, in place, on a fresh NoTracking context.
        await using (var ctx = NewContext())
        {
            var jim = new JimApplication(new PostgresDataRepository(ctx));
            var updated = await jim.Search.UpdatePredefinedSearchCriterionAsync(new PredefinedSearchCriteria
            {
                Id = criterionId,
                MetaverseAttributeId = ids.MetaverseAttributeId,
                ComparisonType = SearchComparisonType.LessThanOrEquals,
                ValueMode = DateCriteriaValueMode.Relative,
                RelativeCount = 14,
                RelativeUnit = RelativeDateUnit.Days,
                RelativeDirection = RelativeDateDirection.FromNow
            });
            Assert.That(updated, Is.Not.Null);
        }

        await using var verify = NewContext();
        var persisted = await verify.PredefinedSearchCriteria.SingleAsync(c => c.Id == criterionId);
        Assert.That(persisted.RelativeCount, Is.EqualTo(14), "Editing a criterion in place must persist.");
    }

    [Test]
    public async Task UpdatePredefinedSearchCriteriaGroupAsync_LoadedNoTracking_PersistsAsync()
    {
        var ids = await SeedAsync();

        int groupId;
        await using (var ctx = NewContext())
        {
            var jim = new JimApplication(new PostgresDataRepository(ctx));
            var group = await jim.Search.CreatePredefinedSearchCriteriaGroupAsync(ids.PredefinedSearchId, null, SearchGroupType.All, 0);
            groupId = group.Id;
        }

        await using (var ctx = NewContext())
        {
            var jim = new JimApplication(new PostgresDataRepository(ctx));
            var updated = await jim.Search.UpdatePredefinedSearchCriteriaGroupAsync(groupId, SearchGroupType.Any, 3);
            Assert.That(updated, Is.Not.Null);
        }

        await using var verify = NewContext();
        var persisted = await verify.PredefinedSearchCriteriaGroups.SingleAsync(g => g.Id == groupId);
        Assert.That(persisted.Type, Is.EqualTo(SearchGroupType.Any), "Editing a group's logic type must persist.");
        Assert.That(persisted.Position, Is.EqualTo(3));
    }
}
