// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Staging;
using JIM.Models.Utility;
using JIM.Worker.Processors;
using NUnit.Framework;

namespace JIM.Worker.Tests.Processors;

/// <summary>
/// Pins how <see cref="TypeOrderedCsoPager"/> walks Object Types in order and sweeps for objects created mid-run (#2492).
/// </summary>
[TestFixture]
public class TypeOrderedCsoPagerTests
{
    private static readonly DateTime RunStart = new(2026, 10, 25, 9, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// A stand-in for the repository: objects are held per type, keyset-paged by id, and the sweep filters on Created.
    /// </summary>
    private sealed class FakeStore
    {
        public List<ConnectedSystemObject> Objects { get; } = [];
        public List<(Guid AfterId, int? TypeId, DateTime? Floor)> Calls { get; } = [];
        public List<(Guid AfterId, DateTime Floor)> SweepIdCalls { get; } = [];

        /// <summary>Every object a sweep load (one with a creation floor) brought back, in load order.</summary>
        public List<Guid> LoadedBySweep { get; } = [];
        public int PageSize { get; init; } = 2;

        /// <summary>The value-count caps the pager asked for, one per count read.</summary>
        public List<int> ValueCountCaps { get; } = [];

        public ConnectedSystemObject Add(int typeId, DateTime? created = null, int values = 0, Guid? id = null)
        {
            var cso = new ConnectedSystemObject { Id = id ?? Guid.NewGuid(), TypeId = typeId, Created = created ?? RunStart.AddDays(-1) };
            for (var i = 0; i < values; i++)
                cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = 1, StringValue = "v" });
            Objects.Add(cso);
            return cso;
        }

        public TypeOrderedCsoPager Pager(IReadOnlyList<int> typeIds, DateTime createdFloor, int valueBudget = int.MaxValue) =>
            new(LoadAsync, LoadValueCountsAsync, LoadSweepIdsAsync, typeIds, createdFloor, PageSize, valueBudget);

        private IEnumerable<ConnectedSystemObject> Page(Guid afterId, int? typeId, DateTime? floor, IReadOnlyCollection<Guid>? onlyIds,
            int maxObjects) =>
            Objects
                .Where(c => typeId == null || c.TypeId == typeId)
                .Where(c => floor == null || c.Created >= floor)
                .Where(c => onlyIds == null || onlyIds.Contains(c.Id))
                .OrderBy(c => c.Id)
                .Where(c => c.Id.CompareTo(afterId) > 0)
                .Take(maxObjects);

        public Task<PagedResultSet<ConnectedSystemObject>> LoadAsync(Guid afterId, int? typeId, DateTime? floor, IReadOnlyCollection<Guid>? onlyIds,
            int maxObjects)
        {
            Calls.Add((afterId, typeId, floor));
            var results = Page(afterId, typeId, floor, onlyIds, Math.Min(maxObjects, PageSize)).ToList();
            if (floor != null)
                LoadedBySweep.AddRange(results.Select(c => c.Id));
            return Task.FromResult(new PagedResultSet<ConnectedSystemObject> { Results = results, PageSize = PageSize });
        }

        /// <summary>Runs after each count read, before the page load that follows it: a writer committing in between.</summary>
        public Action? AfterCount { get; set; }

        /// <summary>Every object counted, once per count of it, in count order: the count work the pager asked for.</summary>
        public List<Guid> CountedIds { get; } = [];

        /// <summary>The earlier candidates the pager named with each count read, in call order.</summary>
        public List<List<Guid>> EarlierCandidateIdsByCall { get; } = [];

        public Task<List<(Guid Id, int ValueCount)>> LoadValueCountsAsync(Guid afterId, int? typeId, DateTime? floor,
            IReadOnlyCollection<Guid>? onlyIds, IReadOnlyCollection<Guid> earlierCandidateIds, int maxObjects, int valueCountCap)
        {
            ValueCountCaps.Add(valueCountCap);
            EarlierCandidateIdsByCall.Add(earlierCandidateIds.ToList());
            var counts = Page(afterId, typeId, floor, onlyIds, Math.Min(maxObjects, PageSize))
                .Select(c => (c.Id, Math.Min(c.AttributeValues.Count, valueCountCap)))
                .ToList();
            CountedIds.AddRange(counts.Select(c => c.Id));
            AfterCount?.Invoke();
            return Task.FromResult(counts);
        }

        public Task<List<Guid>> LoadSweepIdsAsync(Guid afterId, DateTime floor)
        {
            SweepIdCalls.Add((afterId, floor));
            return Task.FromResult(Page(afterId, null, floor, null, PageSize).Select(c => c.Id).ToList());
        }
    }

    /// <summary>An id that sorts by its number, so a test can place objects in paging order.</summary>
    private static Guid IdOf(int number) => new($"00000000-0000-0000-0000-{number:D12}");

    private static List<int> PageSizes(IEnumerable<(int? TypeId, List<ConnectedSystemObject> Page)> pages) => pages.Select(p => p.Page.Count).ToList();

    private static async Task<List<(int? TypeId, List<ConnectedSystemObject> Page)>> DrainAsync(TypeOrderedCsoPager pager)
    {
        var pages = new List<(int?, List<ConnectedSystemObject>)>();
        while (await pager.NextPageAsync() is { } page)
            pages.Add((pager.CurrentTypeId, page.Results.ToList()));
        return pages;
    }

    [Test]
    public async Task NextPageAsync_TwoTypes_WalksEveryObjectOfTheFirstTypeBeforeTheSecondAsync()
    {
        var store = new FakeStore();
        var groups = Enumerable.Range(0, 3).Select(_ => store.Add(typeId: 2)).ToList();
        var people = Enumerable.Range(0, 5).Select(_ => store.Add(typeId: 1)).ToList();

        var pages = await DrainAsync(store.Pager([1, 2], RunStart));

        var handedOut = pages.SelectMany(p => p.Page).ToList();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(handedOut.Take(5).Select(c => c.Id), Is.EquivalentTo(people.Select(c => c.Id)));
            Assert.That(handedOut.Skip(5).Select(c => c.Id), Is.EquivalentTo(groups.Select(c => c.Id)));
            Assert.That(handedOut, Has.Count.EqualTo(8));
            Assert.That(pages.Where(p => p.TypeId == 1).SelectMany(p => p.Page).All(c => c.TypeId == 1), Is.True);
        }
    }

    [Test]
    public async Task NextPageAsync_ObjectCreatedMidRunBehindAFinishedType_IsHandedOutByTheSweepAsync()
    {
        var store = new FakeStore();
        store.Add(typeId: 1);
        store.Add(typeId: 2);
        ConnectedSystemObject? late = null;

        var pager = store.Pager([1, 2], RunStart);
        var handedOut = new List<ConnectedSystemObject>();
        while (await pager.NextPageAsync() is { } page)
        {
            handedOut.AddRange(page.Results);
            // Once type 1 is done, an import commits a new type-1 object.
            if (pager.CurrentTypeId == 2 && late == null)
                late = store.Add(typeId: 1, created: RunStart.AddMinutes(5));
        }

        Assert.That(handedOut.Select(c => c.Id), Does.Contain(late!.Id));
        Assert.That(handedOut.Select(c => c.Id), Is.Unique);
    }

    [Test]
    public async Task NextPageAsync_ObjectCreatedMidRunAheadOfItsTypeCursor_IsHandedOutOnceAsync()
    {
        var store = new FakeStore { PageSize = 1 };
        store.Add(typeId: 1);
        store.Add(typeId: 1);
        // Created after the run started but present before its type is walked: the walk hands it out, and the sweep
        // must not hand it out again.
        var early = store.Add(typeId: 2, created: RunStart.AddMinutes(1));

        var pages = await DrainAsync(store.Pager([1, 2], RunStart));

        var ids = pages.SelectMany(p => p.Page).Select(c => c.Id).ToList();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(ids, Is.Unique);
            Assert.That(ids, Does.Contain(early.Id));
            Assert.That(ids, Has.Count.EqualTo(3));
        }
    }

    [Test]
    public async Task NextPageAsync_SweepPageWhollyAlreadyHandedOut_ContinuesToTheNextSweepPageAsync()
    {
        var store = new FakeStore { PageSize = 2 };
        var walked = Enumerable.Range(0, 4).Select(_ => store.Add(typeId: 1, created: RunStart.AddMinutes(1))).ToList();
        ConnectedSystemObject? late = null;

        var pager = store.Pager([1, 2], RunStart);
        var handedOut = new List<Guid>();
        while (await pager.NextPageAsync() is { } page)
        {
            handedOut.AddRange(page.Results.Select(c => c.Id));
            // A type-2 object created with an id sorting after every walked one, so the sweep's first pages hold
            // only objects already handed out.
            if (late == null && pager.CurrentTypeId == 1)
            {
                late = new ConnectedSystemObject { Id = Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff"), TypeId = 3, Created = RunStart.AddMinutes(2) };
                store.Objects.Add(late);
            }
        }

        Assert.That(handedOut, Is.EquivalentTo(walked.Select(c => c.Id).Append(late!.Id)));
    }

    [Test]
    public async Task NextPageAsync_EveryObjectCreatedInsideTheMarginAndWalked_SweepLoadsNoneOfThemAgainAsync()
    {
        // An initial load: the Full Import created every object moments before the run, inside the sweep's margin (#2861).
        var store = new FakeStore { PageSize = 2 };
        var objects = Enumerable.Range(0, 5).Select(i => store.Add(typeId: i % 2 + 1, created: RunStart.AddMinutes(-1))).ToList();

        var pages = await DrainAsync(store.Pager([1, 2], RunStart.AddMinutes(-5)));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(pages.SelectMany(p => p.Page).Select(c => c.Id), Is.EquivalentTo(objects.Select(c => c.Id)),
                "the type walks hand every object out once");
            Assert.That(store.LoadedBySweep, Is.Empty, "the sweep must not load again what the type walks handed out");
        }
    }

    [Test]
    public async Task NextPageAsync_ObjectCreatedMidRunAmongWalkedObjects_SweepLoadsOnlyThatObjectAsync()
    {
        var store = new FakeStore { PageSize = 2 };
        var walked = Enumerable.Range(0, 6).Select(_ => store.Add(typeId: 1, created: RunStart.AddMinutes(-1))).ToList();
        var group = store.Add(typeId: 2);
        ConnectedSystemObject? late = null;

        var pager = store.Pager([1, 2], RunStart.AddMinutes(-5));
        var handedOut = new List<Guid>();
        while (await pager.NextPageAsync() is { } page)
        {
            handedOut.AddRange(page.Results.Select(c => c.Id));
            // Once type 1 is done, an import commits a new type-1 object, behind the type's cursor and among the walked ones.
            if (late == null && pager.CurrentTypeId == 2)
            {
                late = new ConnectedSystemObject { Id = MidpointOf(walked), TypeId = 1, Created = RunStart.AddMinutes(1) };
                store.Objects.Add(late);
            }
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(handedOut, Is.EqualTo(walked.Select(c => c.Id).Order().Append(group.Id).Append(late!.Id)),
                "the walks hand out every object in type and id order, then the sweep the one created behind them");
            Assert.That(store.LoadedBySweep, Is.EqualTo(new[] { late.Id }), "the sweep loads the one object no walk handed out");
        }
    }

    /// <summary>An id sorting between the first and last of <paramref name="objects"/> in .NET's ordering, and equal to none.</summary>
    private static Guid MidpointOf(IEnumerable<ConnectedSystemObject> objects)
    {
        var ordered = objects.Select(c => c.Id).Order().ToList();
        Guid candidate;
        do
            candidate = Guid.NewGuid();
        while (candidate.CompareTo(ordered[0]) <= 0 || candidate.CompareTo(ordered[^1]) >= 0 || ordered.Contains(candidate));
        return candidate;
    }

    [Test]
    public async Task NextPageAsync_NoTypes_SweepsOnlyAndTerminatesAsync()
    {
        var store = new FakeStore();
        store.Add(typeId: 1);

        var pages = await DrainAsync(store.Pager([], RunStart));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(pages, Is.Empty);
            Assert.That(store.SweepIdCalls, Is.Not.Empty);
            Assert.That(store.SweepIdCalls.All(c => c.Floor == RunStart), Is.True);
            Assert.That(store.Calls.All(c => c.Floor == RunStart), Is.True);
        }
    }

    // --- Value-budgeted pages (#2934): a page ends at the page size or at the value budget, whichever comes first. ---

    [Test]
    public async Task NextPageAsync_PeopleWellUnderTheBudget_KeepThePageSizeAsync()
    {
        var store = new FakeStore { PageSize = 5 };
        for (var i = 0; i < 12; i++)
            store.Add(typeId: 1, values: 12);

        var pager = store.Pager([1], RunStart, valueBudget: 100_000);
        var pages = await DrainAsync(pager);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(PageSizes(pages), Is.EqualTo(new[] { 5, 5, 2 }));
            Assert.That(pager.LastPageEndedAtValueBudget, Is.False);
        }
    }

    [Test]
    public async Task NextPageAsync_BudgetExactlyMet_EndsThePageAtTheObjectThatMeetsItAsync()
    {
        var store = new FakeStore { PageSize = 10 };
        store.Add(typeId: 1, values: 40, id: IdOf(1));
        store.Add(typeId: 1, values: 60, id: IdOf(2));
        store.Add(typeId: 1, values: 1, id: IdOf(3));

        var pages = await DrainAsync(store.Pager([1], RunStart, valueBudget: 100));

        Assert.That(pages.Select(p => p.Page.Select(c => c.Id).ToList()),
            Is.EqualTo(new[] { new List<Guid> { IdOf(1), IdOf(2) }, new List<Guid> { IdOf(3) } }));
    }

    [Test]
    public async Task NextPageAsync_ObjectOverTheBudget_GetsAPageOfItsOwnAsync()
    {
        var store = new FakeStore { PageSize = 10 };
        store.Add(typeId: 1, values: 5, id: IdOf(1));
        store.Add(typeId: 1, values: 5, id: IdOf(2));
        store.Add(typeId: 1, values: 750, id: IdOf(3));
        store.Add(typeId: 1, values: 5, id: IdOf(4));

        var pager = store.Pager([1], RunStart, valueBudget: 100);
        var first = await pager.NextPageAsync();
        var endedAtBudget = pager.LastPageEndedAtValueBudget;
        var rest = await DrainAsync(pager);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(first!.Results.Select(c => c.Id), Is.EqualTo(new[] { IdOf(1), IdOf(2) }));
            Assert.That(endedAtBudget, Is.True, "the first page ended before the large group, at the budget");
            Assert.That(rest.Select(p => p.Page.Select(c => c.Id).ToList()),
                Is.EqualTo(new[] { new List<Guid> { IdOf(3) }, new List<Guid> { IdOf(4) } }));
            Assert.That(store.ValueCountCaps, Is.All.EqualTo(101), "no object is counted past one value over the budget");
        }
    }

    [Test]
    public async Task NextPageAsync_MixedSizes_EveryPageWithinTheBudgetOrOneObjectAndEveryObjectOnceInOrderAsync()
    {
        var store = new FakeStore { PageSize = 6 };
        int[] sizes = [12, 12, 3_000, 40, 50, 12, 12, 400, 12, 90, 9, 12, 12, 12, 12, 12, 12, 12, 1];
        for (var i = 0; i < sizes.Length; i++)
            store.Add(typeId: 1, values: sizes[i], id: IdOf(i + 1));

        var pages = await DrainAsync(store.Pager([1], RunStart, valueBudget: 100));

        var handedOut = pages.SelectMany(p => p.Page).Select(c => c.Id).ToList();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(handedOut, Is.EqualTo(Enumerable.Range(1, sizes.Length).Select(IdOf)), "every object once, in id order");
            Assert.That(pages.All(p => p.Page.Count <= 6), Is.True, "no page exceeds the page size");
            Assert.That(pages.All(p => p.Page.Count == 1 || p.Page.Sum(c => c.AttributeValues.Count) <= 100), Is.True,
                "every page holds at most the budget, unless it holds one object");
            Assert.That(PageSizes(pages), Is.EqualTo(new[] { 2, 1, 2, 2, 1, 1, 2, 6, 2 }));
        }
    }

    [Test]
    public async Task NextPageAsync_EmptyTypeBetweenTwoTypes_IsSkippedWithNoPageAsync()
    {
        var store = new FakeStore { PageSize = 2 };
        var people = Enumerable.Range(0, 3).Select(_ => store.Add(typeId: 1, values: 12)).ToList();
        var groups = Enumerable.Range(0, 2).Select(_ => store.Add(typeId: 3, values: 80)).ToList();

        var pages = await DrainAsync(store.Pager([1, 2, 3], RunStart, valueBudget: 100));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(pages.Select(p => p.TypeId), Is.EqualTo(new int?[] { 1, 1, 3, 3 }), "type 2 holds nothing and gives no page");
            Assert.That(pages.SelectMany(p => p.Page), Is.EquivalentTo(people.Concat(groups)));
        }
    }

    [Test]
    public async Task NextPageAsync_TypeBoundary_APageNeverSpansTwoTypesAndTheNextTypeIsBudgetedAfreshAsync()
    {
        var store = new FakeStore { PageSize = 4 };
        store.Add(typeId: 1, values: 30, id: IdOf(1));
        store.Add(typeId: 1, values: 30, id: IdOf(2));
        store.Add(typeId: 2, values: 60, id: IdOf(3));
        store.Add(typeId: 2, values: 40, id: IdOf(4));
        store.Add(typeId: 2, values: 10, id: IdOf(5));

        var pages = await DrainAsync(store.Pager([1, 2], RunStart, valueBudget: 100));

        Assert.That(pages.Select(p => (p.TypeId, p.Page.Select(c => c.Id).ToList())), Is.EqualTo(new[]
        {
            ((int?)1, new List<Guid> { IdOf(1), IdOf(2) }),
            ((int?)2, new List<Guid> { IdOf(3), IdOf(4) }),
            ((int?)2, new List<Guid> { IdOf(5) })
        }));
    }

    [Test]
    public async Task NextPageAsync_SweepOfLargeObjectsCreatedMidRun_EndsItsPagesAtTheBudgetAndHandsEachOutOnceAsync()
    {
        var store = new FakeStore { PageSize = 4 };
        store.Add(typeId: 1, values: 1, id: IdOf(100));
        var late = new List<ConnectedSystemObject>();

        var pager = store.Pager([1], RunStart, valueBudget: 100);
        var pages = new List<List<ConnectedSystemObject>>();
        while (await pager.NextPageAsync() is { } page)
        {
            pages.Add(page.Results.ToList());
            if (late.Count == 0)
            {
                // Behind type 1's cursor, an import commits three large groups.
                late.AddRange(Enumerable.Range(1, 3).Select(i => store.Add(typeId: 1, created: RunStart.AddMinutes(1), values: 70, id: IdOf(i))));
            }
        }

        var sweepPages = pages.Skip(1).ToList();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(sweepPages.Select(p => p.Count), Is.EqualTo(new[] { 1, 1, 1 }), "70 values each: two would pass the budget");
            Assert.That(sweepPages.SelectMany(p => p).Select(c => c.Id), Is.EqualTo(late.Select(c => c.Id).Order()));
            Assert.That(store.LoadedBySweep, Is.Unique);
        }
    }

    [Test]
    public async Task NextPageAsync_ObjectsCountedThenDeletedBeforeTheirLoad_StepsPastThemAndHandsOutTheRestAsync()
    {
        var store = new FakeStore { PageSize = 10 };
        var deleted = new[] { store.Add(typeId: 1, values: 60, id: IdOf(1)), store.Add(typeId: 1, values: 30, id: IdOf(2)) };
        store.Add(typeId: 2, values: 5, id: IdOf(3));

        // Type 1's first page is planned as objects 1 and 2; both are deleted before it loads, so it loads nothing.
        store.AfterCount = () =>
        {
            foreach (var cso in deleted)
                store.Objects.Remove(cso);
            store.AfterCount = null;
        };

        var pages = await DrainAsync(store.Pager([1, 2], RunStart, valueBudget: 100));

        Assert.That(pages.Select(p => (p.TypeId, p.Page.Select(c => c.Id).ToList())), Is.EqualTo(new[] { ((int?)2, new List<Guid> { IdOf(3) }) }),
            "the walk steps past the objects that went, finishes type 1 and carries on with type 2");
    }

    [Test]
    public async Task NextPageAsync_PlannedObjectDeletedBeforeTheLoadAndTheNextOneLarge_LoadsOnlyWhatWasPlannedAsync()
    {
        var store = new FakeStore { PageSize = 10 };
        var a = store.Add(typeId: 1, values: 10, id: IdOf(1));
        store.Add(typeId: 1, values: 10, id: IdOf(2));
        store.Add(typeId: 1, values: 95, id: IdOf(3));

        // The first page is planned as 1 and 2 (20 values; 3 would take it to 115). Object 1 is deleted before it loads, and
        // a load that took the next two objects from the cursor would bring 3's 95 values with 2's 10.
        store.AfterCount = () =>
        {
            store.Objects.Remove(a);
            store.AfterCount = null;
        };

        var pager = store.Pager([1], RunStart, valueBudget: 100);
        var first = await pager.NextPageAsync();
        var firstValues = pager.LastPageValueCount;
        var rest = await DrainAsync(pager);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(first!.Results.Select(c => c.Id), Is.EqualTo(new[] { IdOf(2) }), "the page holds what was planned and is still there");
            Assert.That(firstValues, Is.EqualTo(10), "the page's value count is what it loaded");
            Assert.That(rest.Select(p => p.Page.Select(c => c.Id).ToList()), Is.EqualTo(new[] { new List<Guid> { IdOf(3) } }));
        }
    }

    /// <summary>
    /// Counting is linear (#2934): an object counted for one page and left for a later one is not counted again. Paging
    /// many objects over the budget, one to a page, used to count a page size of them for every page.
    /// </summary>
    [Test]
    public async Task NextPageAsync_ManyObjectsOverTheBudget_CountsEachObjectOnceAsync()
    {
        var store = new FakeStore { PageSize = 10 };
        var groups = Enumerable.Range(1, 40).Select(i => store.Add(typeId: 1, values: 150, id: IdOf(i))).ToList();

        var pages = await DrainAsync(store.Pager([1], RunStart, valueBudget: 100));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(pages.Select(p => p.Page.Count), Is.All.EqualTo(1), "each over the budget, alone on its page");
            Assert.That(pages.SelectMany(p => p.Page).Select(c => c.Id), Is.EqualTo(groups.Select(c => c.Id)));
            Assert.That(store.CountedIds, Is.EquivalentTo(groups.Select(c => c.Id)), "each object counted exactly once");
        }
    }

    [Test]
    public async Task NextPageAsync_SweepOfManyObjectsOverTheBudget_CountsEachObjectOnceAsync()
    {
        var store = new FakeStore { PageSize = 10 };
        store.Add(typeId: 1, values: 1, id: IdOf(100));
        var late = new List<ConnectedSystemObject>();

        var pager = store.Pager([1], RunStart, valueBudget: 100);
        var pages = new List<List<ConnectedSystemObject>>();
        while (await pager.NextPageAsync() is { } page)
        {
            pages.Add(page.Results.ToList());
            if (late.Count == 0)
                late.AddRange(Enumerable.Range(1, 10).Select(i => store.Add(typeId: 1, created: RunStart.AddMinutes(1), values: 150, id: IdOf(i))));
        }

        var countedBySweep = store.CountedIds.Where(id => late.Exists(c => c.Id == id)).ToList();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(pages.Skip(1).Select(p => p.Count), Is.All.EqualTo(1));
            Assert.That(pages.Skip(1).SelectMany(p => p).Select(c => c.Id), Is.EqualTo(late.Select(c => c.Id)));
            Assert.That(countedBySweep, Is.EquivalentTo(late.Select(c => c.Id)), "the sweep counts each object once");
        }
    }

    /// <summary>
    /// A count read for a page some of whose candidates were counted before names those earlier candidates, since a load
    /// that leaves values out of an object decides by which objects share its page (#2818).
    /// </summary>
    [Test]
    public async Task NextPageAsync_TopUpOfEarlierCandidates_NamesThemToTheCountAsync()
    {
        var store = new FakeStore { PageSize = 4 };
        int[] sizes = [90, 5, 5, 5, 5, 5];
        for (var i = 0; i < sizes.Length; i++)
            store.Add(typeId: 1, values: sizes[i], id: IdOf(i + 1));

        var pages = await DrainAsync(store.Pager([1], RunStart, valueBudget: 100));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(pages.Select(p => p.Page.Count), Is.EqualTo(new[] { 3, 3 }));
            Assert.That(store.EarlierCandidateIdsByCall[0], Is.Empty, "the first count has nothing before it");
            Assert.That(store.EarlierCandidateIdsByCall[1], Is.EqualTo(new[] { IdOf(4) }),
                "the second page's count is a top-up of object 4, counted for the first page and left for this one");
            Assert.That(store.CountedIds, Is.Unique);
        }
    }

    [Test]
    public void ExpectedPageCount_PageSizeAboveWhatTheLoadersReturn_DividesByWhatTheyReturn()
    {
        var store = new FakeStore { PageSize = 5000 };

        var pager = store.Pager([1], RunStart);

        Assert.That(pager.ExpectedPageCount(10_000), Is.EqualTo(5), "the loaders return at most 2,000 objects a page");
    }

    [Test]
    public async Task ExpectedPageCount_PagesEndingAtTheBudget_GrowsAndIsNeverBelowThePagesHandedOutAsync()
    {
        var store = new FakeStore { PageSize = 5 };
        for (var i = 0; i < 10; i++)
            store.Add(typeId: 1, values: 60);

        var pager = store.Pager([1], RunStart, valueBudget: 100);
        var expectedBefore = pager.ExpectedPageCount(10);
        var expected = new List<int>();
        while (await pager.NextPageAsync() is not null)
            expected.Add(pager.ExpectedPageCount(10));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(expectedBefore, Is.EqualTo(2), "before any page, ten objects at five a page");
            Assert.That(expected, Is.EqualTo(new[] { 3, 4, 5, 6, 6, 7, 8, 9, 10, 10 }));
            Assert.That(expected.Select((count, index) => count >= index + 1), Is.All.True);
            Assert.That(pager.PagesHandedOut, Is.EqualTo(10));
        }
    }

    /// <summary>
    /// Equivalence (#2934): a budget moves page boundaries, never what is handed out. Over many random connector spaces with
    /// objects of random sizes, some reached only by the closing sweep, the pager with a budget hands out exactly the objects
    /// the pager without one does, in the same order, every page within the page size and within the budget unless it holds
    /// one object.
    /// </summary>
    [Test]
    public async Task NextPageAsync_RandomConnectorSpacesWithABudget_HandOutTheSameObjectsInTheSameOrderAsTheUnbudgetedPagerAsync()
    {
        var floor = RunStart.AddMinutes(-5);
        for (var seed = 0; seed < 500; seed++)
        {
            var unbudgeted = SizedScenario(seed, floor);
            var expected = new List<Guid>();
            var unbudgetedPager = unbudgeted.Store.Pager(unbudgeted.TypeIds, floor);
            var unbudgetedPages = 0;
            while (await unbudgetedPager.NextPageAsync() is { } page)
            {
                expected.AddRange(page.Results.Select(c => c.Id));
                unbudgetedPages++;
            }

            var budgeted = SizedScenario(seed, floor);
            var budget = new Random(seed).Next(1, 40);
            var pager = budgeted.Store.Pager(budgeted.TypeIds, floor, budget);
            var actual = new List<Guid>();
            var pages = new List<List<ConnectedSystemObject>>();
            while (await pager.NextPageAsync() is { } page)
            {
                pages.Add(page.Results.ToList());
                actual.AddRange(page.Results.Select(c => c.Id));
            }

            Assert.That(actual, Is.EqualTo(expected), $"seed {seed}: the objects handed out, or their order, differ from the unbudgeted pager's");
            Assert.That(actual, Is.Unique, $"seed {seed}: an object was handed out twice");
            Assert.That(pages.All(p => p.Count <= budgeted.Store.PageSize), Is.True, $"seed {seed}: a page exceeds the page size");
            Assert.That(pages.All(p => p.Count == 1 || p.Sum(c => c.AttributeValues.Count) <= budget), Is.True,
                $"seed {seed}: a page of several objects exceeds the budget");
            Assert.That(pages, Has.Count.GreaterThanOrEqualTo(unbudgetedPages), $"seed {seed}: a budget can only add pages");
        }
    }

    /// <summary>
    /// Equivalence (#2861): reading the sweep as ids first changes what is loaded, never what is handed out. Over many
    /// random connector spaces (objects of several types, created inside and outside the margin, page sizes from one to
    /// four, objects created mid-run at random points, some behind their type's cursor, some stamped below the floor),
    /// the pager hands out exactly the pages the sweep handed out before the change: the same objects, in the same
    /// order, grouped into the same pages.
    /// </summary>
    [Test]
    public async Task NextPageAsync_RandomConnectorSpacesWithObjectsCreatedMidRun_HandsOutExactlyThePagesTheLoadThenDropSweepDidAsync()
    {
        var floor = RunStart.AddMinutes(-5);
        for (var seed = 0; seed < 500; seed++)
        {
            var reference = Scenario(seed, floor);
            var expected = await DrainLoadThenDropAsync(reference.Store, reference.TypeIds, floor, reference.AfterPage);

            var actual = Scenario(seed, floor);
            var pager = actual.Store.Pager(actual.TypeIds, floor);
            var pages = new List<List<Guid>>();
            while (await pager.NextPageAsync() is { } page)
            {
                pages.Add(page.Results.Select(c => c.Id).ToList());
                actual.AfterPage(pages.Count);
            }

            Assert.That(pages, Is.EqualTo(expected), $"seed {seed}: the pages handed out differ from the load-then-drop sweep's");
            Assert.That(actual.Store.LoadedBySweep, Is.Unique, $"seed {seed}: the sweep loaded an object twice");
            Assert.That(actual.Store.LoadedBySweep, Is.SubsetOf(pages.SelectMany(p => p)),
                $"seed {seed}: the sweep loaded an object it did not hand out");
        }
    }

    private sealed record RandomScenario(FakeStore Store, IReadOnlyList<int> TypeIds, Action<int> AfterPage);

    /// <summary>
    /// A random connector space and a schedule of objects created mid-run, each committed once a given number of pages has
    /// been handed out. The same seed builds the same scenario, ids included.
    /// </summary>
    private static RandomScenario Scenario(int seed, DateTime floor)
    {
        var random = new Random(seed);
        var store = new FakeStore { PageSize = random.Next(1, 5) };

        Guid NextId()
        {
            var bytes = new byte[16];
            random.NextBytes(bytes);
            return new Guid(bytes);
        }

        // Inside the margin, before it, or (for a writer whose clock lags) below the floor though created mid-run.
        DateTime NextCreated() => random.Next(3) switch
        {
            0 => floor.AddMinutes(-random.Next(1, 60)),
            1 => floor.AddSeconds(random.Next(0, 300)),
            _ => RunStart.AddSeconds(random.Next(0, 300))
        };

        for (var i = random.Next(0, 12); i > 0; i--)
            store.Objects.Add(new ConnectedSystemObject { Id = NextId(), TypeId = random.Next(1, 4), Created = NextCreated() });

        var typeIds = new[] { 1, 2, 3 }.OrderBy(_ => random.Next()).Take(random.Next(0, 4)).ToList();
        var schedule = Enumerable.Range(0, random.Next(0, 5))
            .Select(_ => (AfterPage: random.Next(1, 8), Cso: new ConnectedSystemObject { Id = NextId(), TypeId = random.Next(1, 4), Created = NextCreated() }))
            .ToList();

        return new RandomScenario(store, typeIds, pagesHandedOut =>
        {
            foreach (var (_, cso) in schedule.Where(s => s.AfterPage == pagesHandedOut))
                store.Objects.Add(cso);
        });
    }

    /// <summary>
    /// A random connector space of objects of random sizes, a few of them groups far larger than the rest, created inside
    /// the sweep's margin, before it or after it, and the types to walk (a type not walked is reached by the sweep only).
    /// The same seed builds the same space, ids included.
    /// </summary>
    private static (FakeStore Store, IReadOnlyList<int> TypeIds) SizedScenario(int seed, DateTime floor)
    {
        var random = new Random(seed);
        var store = new FakeStore { PageSize = random.Next(1, 7) };
        for (var i = random.Next(0, 20); i > 0; i--)
        {
            var bytes = new byte[16];
            random.NextBytes(bytes);
            var created = random.Next(3) switch
            {
                0 => floor.AddMinutes(-random.Next(1, 60)),
                1 => floor.AddSeconds(random.Next(0, 300)),
                _ => RunStart.AddSeconds(random.Next(0, 300))
            };
            store.Add(random.Next(1, 4), created, values: random.Next(4) == 0 ? random.Next(0, 80) : random.Next(0, 8), id: new Guid(bytes));
        }

        var typeIds = new[] { 1, 2, 3 }.OrderBy(_ => random.Next()).Take(random.Next(0, 4)).ToList();
        return (store, typeIds);
    }

    /// <summary>
    /// The pager as it stood before #2861, the oracle for the equivalence test: each type walked to an empty page, then
    /// the sweep loads every page of its selection whole and drops what the walks handed out.
    /// </summary>
    private static async Task<List<List<Guid>>> DrainLoadThenDropAsync(FakeStore store, IReadOnlyList<int> typeIds, DateTime floor,
        Action<int> afterPage)
    {
        var pages = new List<List<Guid>>();
        var handedOutSinceFloor = new HashSet<Guid>();

        void HandOut(List<ConnectedSystemObject> page)
        {
            pages.Add(page.Select(c => c.Id).ToList());
            afterPage(pages.Count);
        }

        foreach (var typeId in typeIds)
        {
            var cursor = Guid.Empty;
            while ((await store.LoadAsync(cursor, typeId, null, null, store.PageSize)).Results is { Count: > 0 } page)
            {
                cursor = page[^1].Id;
                foreach (var cso in page.Where(c => c.Created >= floor))
                    handedOutSinceFloor.Add(cso.Id);
                HandOut(page);
            }
        }

        var sweepCursor = Guid.Empty;
        while ((await store.LoadAsync(sweepCursor, null, floor, null, store.PageSize)).Results is { Count: > 0 } sweepPage)
        {
            sweepCursor = sweepPage[^1].Id;
            sweepPage.RemoveAll(c => !handedOutSinceFloor.Add(c.Id));
            if (sweepPage.Count > 0)
                HandOut(sweepPage);
        }

        return pages;
    }
}
