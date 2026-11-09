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

        public ConnectedSystemObject Add(int typeId, DateTime? created = null)
        {
            var cso = new ConnectedSystemObject { Id = Guid.NewGuid(), TypeId = typeId, Created = created ?? RunStart.AddDays(-1) };
            Objects.Add(cso);
            return cso;
        }

        public TypeOrderedCsoPager Pager(IReadOnlyList<int> typeIds, DateTime createdFloor) =>
            new(LoadAsync, LoadSweepIdsAsync, typeIds, createdFloor);

        private IEnumerable<ConnectedSystemObject> Page(Guid afterId, int? typeId, DateTime? floor, IReadOnlyCollection<Guid>? onlyIds) =>
            Objects
                .Where(c => typeId == null || c.TypeId == typeId)
                .Where(c => floor == null || c.Created >= floor)
                .Where(c => onlyIds == null || onlyIds.Contains(c.Id))
                .OrderBy(c => c.Id)
                .Where(c => c.Id.CompareTo(afterId) > 0)
                .Take(PageSize);

        public Task<PagedResultSet<ConnectedSystemObject>> LoadAsync(Guid afterId, int? typeId, DateTime? floor, IReadOnlyCollection<Guid>? onlyIds)
        {
            Calls.Add((afterId, typeId, floor));
            var results = Page(afterId, typeId, floor, onlyIds).ToList();
            if (floor != null)
                LoadedBySweep.AddRange(results.Select(c => c.Id));
            return Task.FromResult(new PagedResultSet<ConnectedSystemObject> { Results = results, PageSize = PageSize });
        }

        public Task<List<Guid>> LoadSweepIdsAsync(Guid afterId, DateTime floor)
        {
            SweepIdCalls.Add((afterId, floor));
            return Task.FromResult(Page(afterId, null, floor, null).Select(c => c.Id).ToList());
        }
    }

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
            while ((await store.LoadAsync(cursor, typeId, null, null)).Results is { Count: > 0 } page)
            {
                cursor = page[^1].Id;
                foreach (var cso in page.Where(c => c.Created >= floor))
                    handedOutSinceFloor.Add(cso.Id);
                HandOut(page);
            }
        }

        var sweepCursor = Guid.Empty;
        while ((await store.LoadAsync(sweepCursor, null, floor, null)).Results is { Count: > 0 } sweepPage)
        {
            sweepCursor = sweepPage[^1].Id;
            sweepPage.RemoveAll(c => !handedOutSinceFloor.Add(c.Id));
            if (sweepPage.Count > 0)
                HandOut(sweepPage);
        }

        return pages;
    }
}
