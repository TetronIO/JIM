// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Staging;
using JIM.PostgresData;
using JIM.TestSupport;
using JIM.Worker.Processors;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Real-PostgreSQL verification of value-budgeted synchronisation pages (#2934): the page plan's value counts, and a
/// type-ordered walk over the real page loaders that ends each page at the page size or the value budget.
/// </summary>
/// <remarks>
/// The counts are a correlated subquery capped with a limit, and the plan's keyset must match the page load's, so both
/// are asserted against PostgreSQL. Opt-in via the same <c>JIM_TEST_RESET_*</c> environment variables as the other
/// RequiresPostgres fixtures.
/// </remarks>
[TestFixture]
[Category("RequiresPostgres")]
public class ValueBudgetedCsoPagingDatabaseTests
{
    private const int PageSize = 10;
    private const int ValueBudget = 100;

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
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL value-budgeted paging tests.");

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

    private sealed record Seeded(int SystemId, int PersonTypeId, int GroupTypeId, List<Guid> PersonIds, List<Guid> SmallGroupIds,
        List<Guid> LargeGroupIds, Dictionary<Guid, int> ValueCounts);

    /// <summary>
    /// People of three values each; then, in import order, small groups, the large groups one after another (as a
    /// directory's largest groups are imported together), and more small groups. A group's values are its external id and
    /// its members, references to the people.
    /// </summary>
    private async Task<Seeded> SeedAsync(int people, int smallGroupsBefore, int[] largeGroupMembers, int smallGroupsAfter)
    {
        await using var seed = NewContext();

        var connectorDefinition = new ConnectorDefinition { Name = "Test Connector", BuiltIn = true };
        var system = new ConnectedSystem { Name = "Yellowstone", ConnectorDefinition = connectorDefinition };
        var person = NewType(system, "person");
        person.Attributes.Add(new ConnectedSystemObjectTypeAttribute
        {
            Name = "mail", ConnectedSystemObjectType = person, Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.MultiValued, Selected = true
        });
        var group = NewType(system, "group");
        group.Attributes.Add(new ConnectedSystemObjectTypeAttribute
        {
            Name = "member", ConnectedSystemObjectType = group, Type = AttributeDataType.Reference,
            AttributePlurality = AttributePlurality.MultiValued, Selected = true
        });
        seed.AddRange(connectorDefinition, system, person, group);
        await seed.SaveChangesAsync();

        var valueCounts = new Dictionary<Guid, int>();
        var personIds = new List<Guid>();
        for (var i = 0; i < people; i++)
        {
            var cso = NewObject(system, person, $"person{i}");
            cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Attribute = person.Attributes[1], AttributeId = person.Attributes[1].Id, StringValue = $"a{i}@example.test" });
            cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Attribute = person.Attributes[1], AttributeId = person.Attributes[1].Id, StringValue = $"b{i}@example.test" });
            seed.Add(cso);
            personIds.Add(cso.Id);
            valueCounts[cso.Id] = 3;
        }
        await seed.SaveChangesAsync();

        var random = new Random(2934);
        var groupNumber = 0;
        List<Guid> AddGroups(IEnumerable<int> memberCounts)
        {
            var ids = new List<Guid>();
            foreach (var members in memberCounts)
            {
                var cso = NewObject(system, group, $"group{groupNumber++}");
                foreach (var memberId in personIds.OrderBy(_ => random.Next()).Take(members))
                {
                    cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
                    {
                        Attribute = group.Attributes[1], AttributeId = group.Attributes[1].Id, ReferenceValueId = memberId
                    });
                }
                seed.Add(cso);
                ids.Add(cso.Id);
                valueCounts[cso.Id] = members + 1;
            }
            return ids;
        }

        var smallGroupIds = AddGroups(Enumerable.Range(0, smallGroupsBefore).Select(_ => random.Next(0, 6)));
        var largeGroupIds = AddGroups(largeGroupMembers);
        smallGroupIds.AddRange(AddGroups(Enumerable.Range(0, smallGroupsAfter).Select(_ => random.Next(0, 6))));
        await seed.SaveChangesAsync();

        return new Seeded(system.Id, person.Id, group.Id, personIds, smallGroupIds, largeGroupIds, valueCounts);
    }

    private static ConnectedSystemObjectType NewType(ConnectedSystem system, string name)
    {
        var type = new ConnectedSystemObjectType { Name = name, ConnectedSystem = system, Selected = true };
        type.Attributes.Add(new ConnectedSystemObjectTypeAttribute
        {
            Name = "id", ConnectedSystemObjectType = type, Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued, Selected = true, IsExternalId = true
        });
        return type;
    }

    private int _nextObjectNumber;

    /// <summary>
    /// An object whose id sorts by creation, in PostgreSQL's ordering and .NET's alike, as an import's time-ordered ids do
    /// across batches. (Two version 7 ids made in the same millisecond can sort either way, which would shuffle the import
    /// order this fixture depends on.)
    /// </summary>
    private ConnectedSystemObject NewObject(ConnectedSystem system, ConnectedSystemObjectType type, string externalId)
    {
        var cso = new ConnectedSystemObject
        {
            Id = new Guid($"01900000-0000-7000-8000-{++_nextObjectNumber:D12}"),
            Type = type, ConnectedSystem = system, Status = ConnectedSystemObjectStatus.Normal,
            ExternalIdAttributeId = type.Attributes[0].Id
        };
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Attribute = type.Attributes[0], AttributeId = type.Attributes[0].Id, StringValue = externalId });
        return cso;
    }

    /// <summary>
    /// Walks the pager over the real Full Synchronisation loaders, as the Worker runs it, on one context per walk.
    /// </summary>
    private async Task<List<List<ConnectedSystemObject>>> WalkFullAsync(Seeded seeded, int valueBudget)
    {
        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);
        var pager = new TypeOrderedCsoPager(
            (afterId, objectTypeId, createdOnOrAfter, onlyIds, maxObjects) => repository.Sync.GetConnectedSystemObjectsAsync(
                seeded.SystemId, 1, maxObjects, seeded.ValueCounts.Count, afterId, objectTypeId, createdOnOrAfter, onlyIds),
            (afterId, objectTypeId, createdOnOrAfter, onlyIds, maxObjects, valueCountCap) => repository.Sync.GetConnectedSystemObjectValueCountsAsync(
                seeded.SystemId, maxObjects, afterId, objectTypeId, createdOnOrAfter, onlyIds, valueCountCap),
            (afterId, createdOnOrAfter) => repository.Sync.GetConnectedSystemObjectIdsAsync(seeded.SystemId, PageSize, afterId, createdOnOrAfter),
            [seeded.PersonTypeId, seeded.GroupTypeId], DateTime.UtcNow.AddMinutes(-5), PageSize, valueBudget);

        var pages = new List<List<ConnectedSystemObject>>();
        while (await pager.NextPageAsync() is { } page)
            pages.Add(page.Results.ToList());
        return pages;
    }

    [Test]
    public async Task NextPageAsync_TypeWithAFewLargeGroupsAndManySmallOnes_PagesByValuesAndVisitsEveryObjectOnceAsync()
    {
        // The three large groups are imported one after another, so by object count alone they share a page.
        var seeded = await SeedAsync(people: 120, smallGroupsBefore: 7, largeGroupMembers: [110, 60, 99], smallGroupsAfter: 23);

        var budgeted = await WalkFullAsync(seeded, ValueBudget);
        var unbudgeted = await WalkFullAsync(seeded, int.MaxValue);

        var budgetedIds = budgeted.SelectMany(p => p).Select(c => c.Id).ToList();
        var largeGroupPages = budgeted.Where(p => p.Any(c => seeded.LargeGroupIds.Contains(c.Id))).ToList();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(budgetedIds, Is.Unique, "no object is visited twice");
            Assert.That(budgetedIds, Is.EquivalentTo(seeded.ValueCounts.Keys), "every object is visited");
            Assert.That(budgetedIds, Is.EqualTo(unbudgeted.SelectMany(p => p).Select(c => c.Id)),
                "the same objects in the same order as paging by object count alone");

            Assert.That(budgeted.All(p => p.Count <= PageSize), Is.True, "no page exceeds the page size");
            Assert.That(budgeted.All(p => p.Count == 1 || p.Sum(c => c.AttributeValues.Count) <= ValueBudget), Is.True,
                "every page of several objects holds at most the budget's values, as loaded");
            Assert.That(budgeted.Take(12).Select(p => p.Count), Is.All.EqualTo(PageSize), "people (three values each) keep the page size");

            Assert.That(largeGroupPages, Has.Count.EqualTo(3), "each large group on a page of its own");
            Assert.That(largeGroupPages.Select(p => p.Count), Is.All.EqualTo(1));
            Assert.That(largeGroupPages.Select(p => p[0].AttributeValues.Count), Is.EqualTo(new[] { 111, 61, 100 }),
                "each loaded whole, in import order: over the budget or not, it is never split");
            Assert.That(unbudgeted.Any(p => p.Count(c => seeded.LargeGroupIds.Contains(c.Id)) == 3), Is.True,
                "by object count alone the three large groups share one page");
        }
    }

    [Test]
    public async Task GetConnectedSystemObjectValueCountsAsync_EachPage_CountsExactlyTheValuesThePageLoadReturnsInItsOrderAsync()
    {
        var seeded = await SeedAsync(people: 30, smallGroupsBefore: 3, largeGroupMembers: [25], smallGroupsAfter: 2);

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);

        foreach (var typeId in new[] { seeded.PersonTypeId, seeded.GroupTypeId })
        {
            var cursor = Guid.Empty;
            for (var guard = 0; ; guard++)
            {
                Assert.That(guard, Is.LessThan(100), "the keyset walk did not terminate");
                var counts = await repository.Sync.GetConnectedSystemObjectValueCountsAsync(seeded.SystemId, 4, cursor, typeId, null, null, int.MaxValue);
                var page = await repository.Sync.GetConnectedSystemObjectsAsync(seeded.SystemId, 1, 4, seeded.ValueCounts.Count, cursor, typeId);
                Assert.That(counts, Is.EqualTo(page.Results.Select(c => (c.Id, c.AttributeValues.Count))),
                    "the plan names the page's objects, in its order, each with the values it loads");
                if (counts.Count == 0)
                    break;
                cursor = counts[^1].Id;
            }
        }
    }

    [Test]
    public async Task GetConnectedSystemObjectValueCountsAsync_ObjectOverTheCap_IsCountedOnlyToTheCapAsync()
    {
        var seeded = await SeedAsync(people: 30, smallGroupsBefore: 0, largeGroupMembers: [25], smallGroupsAfter: 1);

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);
        var counts = await repository.Sync.GetConnectedSystemObjectValueCountsAsync(seeded.SystemId, 10, Guid.Empty, seeded.GroupTypeId, null, null,
            valueCountCap: 11);

        Assert.That(counts, Is.EqualTo(new[] { (seeded.LargeGroupIds[0], 11), (seeded.SmallGroupIds[0], seeded.ValueCounts[seeded.SmallGroupIds[0]]) }));
    }

    /// <summary>
    /// The Delta Synchronisation's plan selects as its page load does: the modified objects only, each with every value it
    /// holds, from the same cursor and filters.
    /// </summary>
    [Test]
    public async Task GetConnectedSystemObjectValueCountsModifiedSinceAsync_CountsOnlyTheModifiedObjectsThePageLoadReturnsAsync()
    {
        var seeded = await SeedAsync(people: 30, smallGroupsBefore: 4, largeGroupMembers: [25, 12], smallGroupsAfter: 4);
        var watermark = DateTime.UtcNow.AddMinutes(5);
        var marked = seeded.LargeGroupIds.Append(seeded.SmallGroupIds[1]).ToHashSet();
        await using (var mark = NewContext())
            await mark.ConnectedSystemObjects.Where(c => marked.Contains(c.Id)).ExecuteUpdateAsync(s => s.SetProperty(c => c.SynchronisationPending, true));

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);
        var counts = await repository.Sync.GetConnectedSystemObjectValueCountsModifiedSinceAsync(seeded.SystemId, watermark, 10, Guid.Empty,
            seeded.GroupTypeId, null, null, int.MaxValue);
        var page = await repository.Sync.GetConnectedSystemObjectsModifiedSinceAsync(seeded.SystemId, watermark, 1, 10, 3, Guid.Empty,
            seeded.GroupTypeId);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(counts.Select(c => c.Id), Is.EquivalentTo(marked), "the marked groups only");
            Assert.That(counts, Is.EqualTo(page.Results.Select(c => (c.Id, c.AttributeValues.Count))), "as the page load returns them");
        }
    }
}
