// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Staging;
using JIM.Models.Utility;

namespace JIM.Worker.Processors;

/// <summary>
/// Hands a synchronisation its Connected System Object pages one Object Type at a time, in dependency order (#2492),
/// then sweeps every type once more for objects created while the run was in flight. A page ends at the page size or at
/// a budget of values, whichever comes first (#2934).
/// </summary>
/// <remarks>
/// <para>
/// Paging in plain id order interleaves types, so a group's page usually arrives before its members have been
/// projected and every such group is deferred to the cross-page reference step, which then redoes its work. Paging
/// the referenced types first lets references between types resolve in the main loop.
/// </para>
/// <para>
/// Each type is walked with its own keyset cursor, page until empty (#2092). A type's walk cannot see an object of
/// that type created after the walk moved on, so the closing sweep selects, across all types, the objects created at
/// or after <c>createdFloor</c>, skipping those the type walks already handed out. An object the sweep also misses
/// keeps its synchronisation-pending mark and is picked up by the next run, as one created behind an id cursor always
/// has been.
/// </para>
/// <para>
/// The sweep reads each page of its selection as ids first, and loads in full only the ids the type walks did not hand
/// out (#2861). Right after an initial Full Import every object was created inside the floor's clock-skew margin, so the
/// selection is the whole Connector Space and every id in it was handed out: loading each page whole and then dropping
/// it reloaded every object with every value and its Metaverse Object (90 seconds at 500,000 objects), and left all of
/// it tracked. The pages handed out are the same either way: the same objects, in the same order, in the same pages.
/// </para>
/// <para>
/// Before each page is loaded, the next page's candidates are read as ids with their value counts, capped just above the
/// budget, and the page takes as many of them as <see cref="ValueBudgetedBatchPlanner.FirstBatchSize"/> allows (#2934). A
/// page of people is never near the budget and keeps the page size; a page of large groups ends early, and a group over the
/// budget gets a page of its own. Since object ids are time-ordered (#2929), a directory's largest groups are imported, and
/// so paged, together: one page at 500,000 objects held 2.0 million member values. Only the boundaries move: every object
/// is handed out once, in the same order as before.
/// </para>
/// </remarks>
public sealed class TypeOrderedCsoPager
{
    /// <summary>
    /// Loads one page: the keyset cursor, the Object Type (null for the sweep), the creation floor (sweep only), the ids
    /// to restrict the page to (sweep only: those of an id page the type walks did not hand out) and the most objects the
    /// page may hold.
    /// </summary>
    public delegate Task<PagedResultSet<ConnectedSystemObject>> PageLoader(Guid afterId, int? objectTypeId, DateTime? createdOnOrAfter,
        IReadOnlyCollection<Guid>? onlyIds, int maxObjects);

    /// <summary>
    /// Reads the objects the page loader would load from the same arguments, ids only, each with how many attribute values
    /// it holds, capped at <c>valueCountCap</c>; in the page loader's order, at most <c>maxObjects</c> of them.
    /// </summary>
    public delegate Task<List<(Guid Id, int ValueCount)>> ValueCountLoader(Guid afterId, int? objectTypeId, DateTime? createdOnOrAfter,
        IReadOnlyCollection<Guid>? onlyIds, int maxObjects, int valueCountCap);

    /// <summary>
    /// Reads one page of the sweep's selection, ids only, in the page loader's order: the objects created at or after the
    /// floor whose ids sort after the cursor, at most a page size of them.
    /// </summary>
    public delegate Task<List<Guid>> SweepIdLoader(Guid afterId, DateTime createdOnOrAfter);

    private readonly PageLoader _loadPage;
    private readonly ValueCountLoader _loadValueCounts;
    private readonly SweepIdLoader _loadSweepIds;
    private readonly IReadOnlyList<int> _typeIds;
    private readonly Dictionary<int, int> _walkPositionByTypeId;
    private readonly DateTime _createdFloor;
    private readonly int _pageSize;
    private readonly int _valueBudget;
    private readonly int _valueCountCap;
    private readonly HashSet<Guid> _handedOutSinceFloor = [];
    private int _typeIndex;
    private Guid _cursor = Guid.Empty;
    private bool _sweeping;
    private bool _finished;

    // The sweep's current id page: where its loads start, and the ids of it still to hand out, in order.
    private Guid _sweepPageCursor = Guid.Empty;
    private List<Guid> _sweepIdsToHandOut = [];

    private int _objectsHandedOut;

    /// <param name="loadPage">The repository page load.</param>
    /// <param name="loadValueCounts">The repository read of the objects <paramref name="loadPage"/> would load, ids with
    /// their value counts.</param>
    /// <param name="loadSweepIds">The repository read of the sweep's selection, ids only: the same selection
    /// <paramref name="loadPage"/> makes for the sweep.</param>
    /// <param name="typeIds">Every Object Type id to walk, in paging order.</param>
    /// <param name="createdFloor">Objects created at or after this instant are swept once the type walks finish:
    /// the run's start, less a margin for clock skew between the writer and this run.</param>
    /// <param name="pageSize">The most objects a page holds.</param>
    /// <param name="valueBudget">The most attribute values a page holds, unless a single object holds more; that object
    /// gets a page of its own.</param>
    public TypeOrderedCsoPager(PageLoader loadPage, ValueCountLoader loadValueCounts, SweepIdLoader loadSweepIds, IReadOnlyList<int> typeIds,
        DateTime createdFloor, int pageSize, int valueBudget)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(valueBudget, 1);
        _loadPage = loadPage;
        _loadValueCounts = loadValueCounts;
        _loadSweepIds = loadSweepIds;
        _typeIds = typeIds;
        _walkPositionByTypeId = typeIds.Select((typeId, position) => (typeId, position)).ToDictionary(t => t.typeId, t => t.position);
        _createdFloor = createdFloor;
        _pageSize = pageSize;
        _valueBudget = valueBudget;

        // Any count above the budget sends its object to a page of its own, so no object need be counted further than one
        // value past it: a 500,000-member group is counted to 100,001.
        _valueCountCap = valueBudget == int.MaxValue ? int.MaxValue : valueBudget + 1;
        _sweeping = typeIds.Count == 0;
    }

    /// <summary>
    /// The Object Type the last page came from, or null once the closing sweep has started.
    /// </summary>
    public int? CurrentTypeId => _sweeping ? null : _typeIds[_typeIndex];

    /// <summary>
    /// Whether the last page came from the closing sweep.
    /// </summary>
    public bool IsSweeping => _sweeping;

    /// <summary>
    /// Objects created at or after this instant are swept once the type walks finish, so a type's walk is only certain to
    /// have handed out the objects created before it.
    /// </summary>
    public DateTime CreatedFloor => _createdFloor;

    /// <summary>
    /// The pages handed out so far.
    /// </summary>
    public int PagesHandedOut { get; private set; }

    /// <summary>
    /// The attribute values the last page's objects held when it was planned, each counted at most one past the budget.
    /// </summary>
    public long LastPageValueCount { get; private set; }

    /// <summary>
    /// Whether the last page ended at the value budget rather than at the page size or at the end of its selection.
    /// </summary>
    public bool LastPageEndedAtValueBudget { get; private set; }

    /// <summary>
    /// The pages the run can now expect in all: those handed out, and enough full pages for the rest of
    /// <paramref name="expectedObjects"/>. Pages that end at the value budget, and each type's last page, make the count
    /// grow as the run goes; it is never below the pages handed out.
    /// </summary>
    /// <param name="expectedObjects">The objects the run expects to process, counted when it started.</param>
    public int ExpectedPageCount(int expectedObjects)
    {
        var remaining = Math.Max(0, expectedObjects - _objectsHandedOut);
        return PagesHandedOut + (int)Math.Ceiling(remaining / (double)_pageSize);
    }

    /// <summary>
    /// Whether the walk of <paramref name="typeId"/> has finished: every object of that type created before
    /// <see cref="CreatedFloor"/> has been handed out, so none of them is processed again later in the run. False for the
    /// type being walked now, and for a type this run does not walk.
    /// </summary>
    public bool HasFinishedWalking(int typeId) =>
        _walkPositionByTypeId.TryGetValue(typeId, out var position) && (_sweeping || position < _typeIndex);

    /// <summary>
    /// Loads the next non-empty page, or returns null when every type and the sweep are exhausted.
    /// </summary>
    public async Task<PagedResultSet<ConnectedSystemObject>?> NextPageAsync()
    {
        while (!_finished)
        {
            if (!_sweeping)
            {
                var typeId = _typeIds[_typeIndex];
                var candidates = await _loadValueCounts(_cursor, typeId, null, null, _pageSize, _valueCountCap);
                if (candidates.Count > 0)
                {
                    var take = PlanPage(candidates);
                    var page = await _loadPage(_cursor, typeId, null, null, take);
                    if (page.Results.Count > 0)
                    {
                        _cursor = page.Results[^1].Id;
                        foreach (var cso in page.Results.Where(c => c.Created >= _createdFloor))
                            _handedOutSinceFloor.Add(cso.Id);
                        return HandOut(page);
                    }

                    // The objects counted had gone by the time the page loaded (an object deleted in between): step past
                    // them, as the page that loaded none of them would have.
                    _cursor = candidates[take - 1].Id;
                    continue;
                }

                _cursor = Guid.Empty;
                _typeIndex++;
                if (_typeIndex >= _typeIds.Count)
                {
                    _typeIndex = _typeIds.Count - 1;
                    _sweeping = true;
                }
                continue;
            }

            if (_sweepIdsToHandOut.Count == 0)
            {
                var sweepIds = await _loadSweepIds(_cursor, _createdFloor);
                if (sweepIds.Count == 0)
                {
                    _finished = true;
                    break;
                }

                // Advance on the id page as the repository returned it, before filtering: the keyset cursor must be the
                // last row in the database's own ordering.
                _sweepPageCursor = _cursor;
                _cursor = sweepIds[^1];
                _sweepIdsToHandOut = sweepIds.Where(id => !_handedOutSinceFloor.Contains(id)).ToList();
                continue;
            }

            // The page the sweep would have loaded from this id page, restricted to what it would have kept of it, and
            // ended at the value budget. An id not counted has left the selection, and no load would return it.
            var sweepCandidates = await _loadValueCounts(_sweepPageCursor, null, _createdFloor, _sweepIdsToHandOut, _pageSize, _valueCountCap);
            if (sweepCandidates.Count == 0)
            {
                _sweepIdsToHandOut = [];
                continue;
            }

            var sweepTake = PlanPage(sweepCandidates);
            var batch = sweepCandidates.Take(sweepTake).Select(c => c.Id).ToList();
            _sweepIdsToHandOut = sweepCandidates.Skip(sweepTake).Select(c => c.Id).ToList();
            var sweepPage = await _loadPage(_sweepPageCursor, null, _createdFloor, batch, batch.Count);

            // Records what is handed out, and keeps the guarantee that nothing is handed out twice whatever the loader returns.
            sweepPage.Results.RemoveAll(c => !_handedOutSinceFloor.Add(c.Id));
            if (sweepPage.Results.Count > 0)
                return HandOut(sweepPage);
        }

        return null;
    }

    /// <summary>
    /// How many of the candidates the page takes, recording the page's value count and why it ended.
    /// </summary>
    private int PlanPage(List<(Guid Id, int ValueCount)> candidates)
    {
        var counts = candidates.Select(c => c.ValueCount).ToList();
        var take = ValueBudgetedBatchPlanner.FirstBatchSize(counts, _pageSize, _valueBudget);
        LastPageValueCount = counts.Take(take).Sum(count => (long)Math.Max(count, 0));

        // The candidates are at most a page size, so a page that leaves any of them out ended at the budget.
        LastPageEndedAtValueBudget = take < candidates.Count;
        return take;
    }

    private PagedResultSet<ConnectedSystemObject> HandOut(PagedResultSet<ConnectedSystemObject> page)
    {
        PagesHandedOut++;
        _objectsHandedOut += page.Results.Count;
        return page;
    }
}
