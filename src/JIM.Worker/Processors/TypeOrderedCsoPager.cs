// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Staging;
using JIM.Models.Utility;

namespace JIM.Worker.Processors;

/// <summary>
/// Hands a synchronisation its Connected System Object pages one Object Type at a time, in dependency order (#2492),
/// then sweeps every type once more for objects created while the run was in flight.
/// </summary>
/// <remarks>
/// <para>
/// Paging in plain id order interleaves types, so a group's page usually arrives before its members have been
/// projected and every such group is deferred to the cross-page reference step, which then redoes its work. Paging
/// the referenced types first lets references between types resolve in the main loop.
/// </para>
/// <para>
/// Each type is walked with its own keyset cursor, page until empty (#2092). A type's walk cannot see an object of
/// that type created after the walk moved on, so the closing sweep reloads, across all types, the objects created at
/// or after <c>createdFloor</c>, skipping those the type walks already handed out. An object the sweep also misses
/// keeps its synchronisation-pending mark and is picked up by the next run, as one created behind an id cursor always
/// has been.
/// </para>
/// </remarks>
public sealed class TypeOrderedCsoPager
{
    /// <summary>
    /// Loads one page: the keyset cursor, the Object Type (null for the sweep) and the creation floor (sweep only).
    /// </summary>
    public delegate Task<PagedResultSet<ConnectedSystemObject>> PageLoader(Guid afterId, int? objectTypeId, DateTime? createdOnOrAfter);

    private readonly PageLoader _loadPage;
    private readonly IReadOnlyList<int> _typeIds;
    private readonly Dictionary<int, int> _walkPositionByTypeId;
    private readonly DateTime _createdFloor;
    private readonly HashSet<Guid> _handedOutSinceFloor = [];
    private int _typeIndex;
    private Guid _cursor = Guid.Empty;
    private bool _sweeping;
    private bool _finished;

    /// <param name="loadPage">The repository page load.</param>
    /// <param name="typeIds">Every Object Type id to walk, in paging order.</param>
    /// <param name="createdFloor">Objects created at or after this instant are swept once the type walks finish:
    /// the run's start, less a margin for clock skew between the writer and this run.</param>
    public TypeOrderedCsoPager(PageLoader loadPage, IReadOnlyList<int> typeIds, DateTime createdFloor)
    {
        _loadPage = loadPage;
        _typeIds = typeIds;
        _walkPositionByTypeId = typeIds.Select((typeId, position) => (typeId, position)).ToDictionary(t => t.typeId, t => t.position);
        _createdFloor = createdFloor;
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
                var page = await _loadPage(_cursor, _typeIds[_typeIndex], null);
                if (page.Results.Count > 0)
                {
                    _cursor = page.Results[^1].Id;
                    foreach (var cso in page.Results.Where(c => c.Created >= _createdFloor))
                        _handedOutSinceFloor.Add(cso.Id);
                    return page;
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

            var sweepPage = await _loadPage(_cursor, null, _createdFloor);
            if (sweepPage.Results.Count == 0)
            {
                _finished = true;
                break;
            }

            // Advance on the page as the repository returned it, before filtering: the keyset cursor must be the
            // last row in the database's own ordering.
            _cursor = sweepPage.Results[^1].Id;
            sweepPage.Results.RemoveAll(c => !_handedOutSinceFloor.Add(c.Id));
            if (sweepPage.Results.Count > 0)
                return sweepPage;
        }

        return null;
    }
}
