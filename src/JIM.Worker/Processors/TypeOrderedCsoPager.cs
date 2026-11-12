// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
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
/// budget, and the page takes as many of them as <see cref="ValueBudgetedBatchPlanner.FirstBatchSize"/> allows (#2934),
/// loading exactly those. Candidates left for a later page are kept, so every object is counted once. A
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
    /// the load would bring, capped at <c>valueCountCap</c>; in the page loader's order, at most <c>maxObjects</c> of them.
    /// <c>earlierCandidateIds</c> are the objects counted before these and not yet handed out, which the next page can hold
    /// alongside them (a load that leaves values out of an object can depend on which others share its page, #2818).
    /// </summary>
    public delegate Task<List<(Guid Id, int ValueCount)>> ValueCountLoader(Guid afterId, int? objectTypeId, DateTime? createdOnOrAfter,
        IReadOnlyCollection<Guid>? onlyIds, IReadOnlyCollection<Guid> earlierCandidateIds, int maxObjects, int valueCountCap);

    /// <summary>
    /// Reads one page of the sweep's selection, ids only, in the page loader's order: the objects created at or after the
    /// floor whose ids sort after the cursor, at most a page size of them.
    /// </summary>
    public delegate Task<List<Guid>> SweepIdLoader(Guid afterId, DateTime createdOnOrAfter);

    /// <summary>
    /// For a planned page, what the load would bring for those of its objects whose values depend on the company they keep
    /// on that page (#2818): a load that leaves values out of an object decides by which objects share its page, which no
    /// count made before the page was planned could know. Objects not named are as counted; a value is counted to
    /// <c>valueCountCap</c> at most.
    /// </summary>
    public delegate Task<IReadOnlyDictionary<Guid, int>> PlannedPageReviser(int? objectTypeId, IReadOnlyList<Guid> plannedIds, int valueCountCap);

    private readonly PageLoader _loadPage;
    private readonly ValueCountLoader _loadValueCounts;
    private readonly SweepIdLoader _loadSweepIds;
    private readonly PlannedPageReviser? _revisePlannedPage;
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

    // The objects counted and not yet handed out, in the loaders' order: the walk's next objects of its type, or the next
    // objects of the sweep's current id page. Each object is counted once (#2934): planning a page from a fresh count of a
    // page size of candidates, and taking one, counted the rest again for every page, which for a run of large groups is
    // quadratic in index reads.
    private readonly List<(Guid Id, int ValueCount)> _candidates = [];

    // Whether nothing follows the candidates, as of the last count.
    private bool _candidatesExhausted;

    // The walk: the last object counted, where its next count starts.
    private Guid _countCursor = Guid.Empty;

    // The sweep's current id page: where its loads start, and the ids of it not yet counted, in order.
    private Guid _sweepPageCursor = Guid.Empty;
    private List<Guid> _sweepIdsToCount = [];

    // The values per object of the last count, which sizes the next: once the candidates hold the budget, counting further
    // ahead only spends reads on objects for later pages.
    private double? _valuesPerObjectCounted;

    private int _objectsHandedOut;

    /// <param name="loadPage">The repository page load.</param>
    /// <param name="loadValueCounts">The repository read of the objects <paramref name="loadPage"/> would load, ids with
    /// their value counts.</param>
    /// <param name="loadSweepIds">The repository read of the sweep's selection, ids only: the same selection
    /// <paramref name="loadPage"/> makes for the sweep.</param>
    /// <param name="typeIds">Every Object Type id to walk, in paging order.</param>
    /// <param name="createdFloor">Objects created at or after this instant are swept once the type walks finish:
    /// the run's start, less a margin for clock skew between the writer and this run.</param>
    /// <param name="pageSize">The most objects a page holds (<c>Sync.PageSize</c>); no more than the loaders return in a page,
    /// <see cref="Constants.SynchronisationLimits.MaxPageSize"/>.</param>
    /// <param name="valueBudget">The most attribute values a page holds, unless a single object holds more; that object
    /// gets a page of its own.</param>
    /// <param name="revisePlannedPage">What the load would bring for a planned page's objects whose values depend on the page they
    /// share (#2818), checked before the page loads; null where the load brings every value whatever the page.</param>
    public TypeOrderedCsoPager(PageLoader loadPage, ValueCountLoader loadValueCounts, SweepIdLoader loadSweepIds, IReadOnlyList<int> typeIds,
        DateTime createdFloor, int pageSize, int valueBudget, PlannedPageReviser? revisePlannedPage = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(valueBudget, 1);
        _loadPage = loadPage;
        _loadValueCounts = loadValueCounts;
        _loadSweepIds = loadSweepIds;
        _revisePlannedPage = revisePlannedPage;
        _typeIds = typeIds;
        _walkPositionByTypeId = typeIds.Select((typeId, position) => (typeId, position)).ToDictionary(t => t.typeId, t => t.position);
        _createdFloor = createdFloor;
        _pageSize = Math.Min(pageSize, Constants.SynchronisationLimits.MaxPageSize);
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
    /// The attribute values the last page's objects were loaded with.
    /// </summary>
    public long LastPageValueCount { get; private set; }

    /// <summary>
    /// Whether the last page was planned to end at the value budget, rather than at the page size or at the end of its
    /// selection.
    /// </summary>
    public bool LastPageEndedAtValueBudget { get; private set; }

    /// <summary>
    /// The pages the run can now expect in all: those handed out, and enough full pages for the rest of
    /// <paramref name="expectedObjects"/>. Pages that end at the value budget, and each type's last page, make the count
    /// grow as the run goes; it is never below the pages handed out, and it is an estimate: a closing sweep page, or an
    /// object entering the selection mid-run, can follow a page it called the last.
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

                // A walk pages until a count finds nothing more (#2092), so with no candidates left it counts afresh,
                // reaching any object written behind the last count's end.
                if (_candidates.Count == 0)
                    _candidatesExhausted = false;

                await FillCandidatesAsync(async want =>
                {
                    var counted = await _loadValueCounts(_countCursor, typeId, null, null, CandidateIds(), want, _valueCountCap);
                    if (counted.Count > 0)
                        _countCursor = counted[^1].Id;
                    return (counted, counted.Count < want);
                });

                if (_candidates.Count > 0)
                {
                    var planned = await TakePlannedPageAsync(typeId);

                    // The page loads only the objects planned for it: one counted and since deleted is simply not there,
                    // and a load of the next objects from the cursor could bring one far over the budget in its place. The
                    // next page starts after the last object planned, whatever came back; an object written since the count
                    // inside the planned range is left to the closing sweep, as one written behind the cursor always was.
                    var page = await _loadPage(_cursor, typeId, null, planned, planned.Count);
                    _cursor = planned[^1];
                    if (page.Results.Count > 0)
                    {
                        foreach (var cso in page.Results.Where(c => c.Created >= _createdFloor))
                            _handedOutSinceFloor.Add(cso.Id);
                        return HandOut(page);
                    }

                    continue;
                }

                _cursor = Guid.Empty;
                _countCursor = Guid.Empty;
                _typeIndex++;
                if (_typeIndex >= _typeIds.Count)
                {
                    _typeIndex = _typeIds.Count - 1;
                    _sweeping = true;
                    _candidatesExhausted = true;
                }
                continue;
            }

            if (_candidates.Count == 0 && _sweepIdsToCount.Count == 0)
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
                _sweepIdsToCount = sweepIds.Where(id => !_handedOutSinceFloor.Contains(id)).ToList();
                _candidatesExhausted = _sweepIdsToCount.Count == 0;
                continue;
            }

            // The page the sweep would have loaded from this id page, restricted to what it would have kept of it, and
            // ended at the value budget. An id the count does not return has left the selection, and no load would return it.
            await FillCandidatesAsync(async want =>
            {
                var ids = _sweepIdsToCount.Take(want).ToList();
                _sweepIdsToCount.RemoveRange(0, ids.Count);
                var counted = await _loadValueCounts(_sweepPageCursor, null, _createdFloor, ids, CandidateIds(), ids.Count, _valueCountCap);
                return (counted, _sweepIdsToCount.Count == 0);
            });

            if (_candidates.Count == 0)
                continue;

            var batch = await TakePlannedPageAsync(null);
            var sweepPage = await _loadPage(_sweepPageCursor, null, _createdFloor, batch, batch.Count);

            // Records what is handed out, and keeps the guarantee that nothing is handed out twice whatever the loader returns.
            sweepPage.Results.RemoveAll(c => !_handedOutSinceFloor.Add(c.Id));
            if (sweepPage.Results.Count > 0)
                return HandOut(sweepPage);
        }

        return null;
    }

    /// <summary>
    /// Counts further candidates until the next page is decided: it fills the page size, the budget closes it before the
    /// candidates run out, or nothing more is there. Each object is counted once, whatever page it ends up on.
    /// </summary>
    /// <param name="countNext">Counts up to the given number of the next objects, returning them in the loaders' order and
    /// whether nothing follows them.</param>
    private async Task FillCandidatesAsync(Func<int, Task<(List<(Guid Id, int ValueCount)> Counted, bool Exhausted)>> countNext)
    {
        while (!_candidatesExhausted && _candidates.Count < _pageSize && !NextPageClosesWithinCandidates())
        {
            var (counted, exhausted) = await countNext(NextCountSize());
            _candidates.AddRange(counted);
            if (counted.Count > 0)
                _valuesPerObjectCounted = counted.Average(c => (double)Math.Max(c.ValueCount, 0));
            _candidatesExhausted = exhausted;
        }
    }

    /// <summary>
    /// Whether the budget closes the next page before the candidates run out, so counting further cannot change it.
    /// </summary>
    private bool NextPageClosesWithinCandidates()
    {
        if (_candidates.Count == 0)
            return false;

        var take = ValueBudgetedBatchPlanner.FirstBatchSize(CandidateValueCounts(), _pageSize, _valueBudget);
        return take < _candidates.Count || PlannedValues(take) > _valueBudget;
    }

    /// <summary>
    /// How many objects the next count asks for: enough, at the last count's values per object, to reach the budget from
    /// what the candidates hold, and one more to show where the page ends; never more than the page has room for. A page
    /// of people (about 12 values each) asks for the whole page at once; a run of large groups a few at a time.
    /// </summary>
    private int NextCountSize()
    {
        var room = _pageSize - _candidates.Count;
        if (_valuesPerObjectCounted is not { } perObject || perObject <= 0)
            return room;

        var remaining = Math.Max(0, (double)_valueBudget - PlannedValues(_candidates.Count));
        var wanted = Math.Ceiling(remaining / perObject) + 1;
        return (int)Math.Clamp(wanted, 1, room);
    }

    /// <summary>
    /// Takes the next page's objects from the front of the candidates, recording why the page ends.
    /// </summary>
    /// <remarks>
    /// Where values can depend on the page an object shares (<see cref="PlannedPageReviser"/>), the planned page is checked
    /// before it loads: a count that grew is raised and the page planned again, until a check raises nothing. Counts only
    /// rise, so the page only shrinks, and each pass raises at least one count to at most the cap, so it ends; on a page
    /// with nothing to raise it costs one check.
    /// </remarks>
    private async Task<List<Guid>> TakePlannedPageAsync(int? objectTypeId)
    {
        var take = ValueBudgetedBatchPlanner.FirstBatchSize(CandidateValueCounts(), _pageSize, _valueBudget);
        while (_revisePlannedPage != null)
        {
            var revised = await _revisePlannedPage(objectTypeId, _candidates.Take(take).Select(c => c.Id).ToList(), _valueCountCap);
            var raised = false;
            for (var i = 0; i < take; i++)
            {
                if (!revised.TryGetValue(_candidates[i].Id, out var count) || count <= _candidates[i].ValueCount)
                    continue;

                _candidates[i] = (_candidates[i].Id, Math.Min(count, _valueCountCap));
                raised = true;
            }

            if (!raised)
                break;

            take = ValueBudgetedBatchPlanner.FirstBatchSize(CandidateValueCounts(), _pageSize, _valueBudget);
        }

        LastPageEndedAtValueBudget = take < _pageSize && (take < _candidates.Count || PlannedValues(take) > _valueBudget);
        var planned = _candidates.Take(take).Select(c => c.Id).ToList();
        _candidates.RemoveRange(0, take);
        return planned;
    }

    private List<int> CandidateValueCounts() => _candidates.Select(c => c.ValueCount).ToList();

    private long PlannedValues(int take) => _candidates.Take(take).Sum(c => (long)Math.Max(c.ValueCount, 0));

    private List<Guid> CandidateIds() => _candidates.Select(c => c.Id).ToList();

    private PagedResultSet<ConnectedSystemObject> HandOut(PagedResultSet<ConnectedSystemObject> page)
    {
        PagesHandedOut++;
        _objectsHandedOut += page.Results.Count;
        LastPageValueCount = page.Results.Sum(c => (long)c.AttributeValues.Count);
        return page;
    }
}
