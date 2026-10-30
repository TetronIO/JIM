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
        public int PageSize { get; init; } = 2;

        public ConnectedSystemObject Add(int typeId, DateTime? created = null)
        {
            var cso = new ConnectedSystemObject { Id = Guid.NewGuid(), TypeId = typeId, Created = created ?? RunStart.AddDays(-1) };
            Objects.Add(cso);
            return cso;
        }

        public Task<PagedResultSet<ConnectedSystemObject>> LoadAsync(Guid afterId, int? typeId, DateTime? floor)
        {
            Calls.Add((afterId, typeId, floor));
            var results = Objects
                .Where(c => typeId == null || c.TypeId == typeId)
                .Where(c => floor == null || c.Created >= floor)
                .OrderBy(c => c.Id)
                .Where(c => c.Id.CompareTo(afterId) > 0)
                .Take(PageSize)
                .ToList();
            return Task.FromResult(new PagedResultSet<ConnectedSystemObject> { Results = results, PageSize = PageSize });
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

        var pages = await DrainAsync(new TypeOrderedCsoPager(store.LoadAsync, [1, 2], RunStart));

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

        var pager = new TypeOrderedCsoPager(store.LoadAsync, [1, 2], RunStart);
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

        var pages = await DrainAsync(new TypeOrderedCsoPager(store.LoadAsync, [1, 2], RunStart));

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

        var pager = new TypeOrderedCsoPager(store.LoadAsync, [1, 2], RunStart);
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
    public async Task NextPageAsync_NoTypes_SweepsOnlyAndTerminatesAsync()
    {
        var store = new FakeStore();
        store.Add(typeId: 1);

        var pages = await DrainAsync(new TypeOrderedCsoPager(store.LoadAsync, [], RunStart));

        Assert.That(pages, Is.Empty);
        Assert.That(store.Calls.All(c => c.Floor == RunStart), Is.True);
    }
}
