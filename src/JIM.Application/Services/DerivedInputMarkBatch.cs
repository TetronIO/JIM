// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Data.Repositories;
using JIM.Models.Core;
using JIM.Models.Sync;
using Serilog;

namespace JIM.Application.Services;

/// <summary>
/// Collects Metaverse-Derived Attribute Flow marks (#1750, plan Phase 4, FR 9) for a writer that changes Metaverse
/// attribute values outside a Connected System's own synchronisation (Synchronisation Rule deletion recall, Synchronised
/// Deprovisioning, the stranded value sweep, a direct Metaverse Object edit), and applies them in bulk: one
/// <see cref="ISyncRepository.MarkConnectedSystemObjectsDerivedInputChangePendingAsync"/> call per flush, at the
/// writer's existing batch boundary, never one per object.
/// <para>
/// No Connected System is excluded. Inside synchronisation the system being synchronised is left out because its own
/// derived pass has just run on the new values; outside synchronisation nothing has run any derived pass, so every
/// hosting system, the one whose data the writer touched included, must re-evaluate. A writer whose Synchronisation
/// Rules are going away (a rule being deleted, a system being deprovisioned) excludes them by building
/// <see cref="Graph"/> from the rules that survive, so a departing rule's own derived flows neither mark its system nor
/// carry transitivity onwards.
/// </para>
/// <para>
/// Inert when <see cref="Graph"/> is null (the feature is off): nothing is collected, nothing is flushed, and the
/// repository is never called.
/// </para>
/// </summary>
public sealed class DerivedInputMarkBatch
{
    private readonly HashSet<DerivedInputChangeMark> _pending = [];
    private readonly string _writerName;

    /// <param name="graph">The run-time derived flow graph of the Synchronisation Rules that will exist once the write
    /// completes (<see cref="DerivedFlowGraphFactory.CreateAsync"/>), or null when the feature is off.</param>
    /// <param name="writerName">Names the writer in log messages.</param>
    public DerivedInputMarkBatch(DerivedFlowGraph? graph, string writerName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(writerName);
        Graph = graph;
        _writerName = writerName;
    }

    /// <summary>
    /// The graph marks are computed from; null when the feature is off.
    /// </summary>
    public DerivedFlowGraph? Graph { get; }

    /// <summary>
    /// Distinct (Metaverse Object, Connected System) marks applied so far, across every flush.
    /// </summary>
    public int MarksRequested { get; private set; }

    /// <summary>
    /// Connected System Objects newly marked so far (not already marked before the flush), across every flush.
    /// </summary>
    public int MarksSet { get; private set; }

    /// <summary>
    /// Number of repository calls made so far: one per non-empty flush.
    /// </summary>
    public int FlushCount { get; private set; }

    /// <summary>
    /// Marks collected and not yet flushed.
    /// </summary>
    public int PendingCount => _pending.Count;

    /// <summary>
    /// Records, in memory, a mark for every Connected System hosting a derived flow that reads (directly or
    /// transitively) an attribute of <paramref name="changedValues"/> on <paramref name="metaverseObject"/>.
    /// </summary>
    /// <param name="metaverseObject">The persisted Metaverse Object whose attribute values the writer changed.</param>
    /// <param name="changedValues">The values added and removed: additions, removals and re-elected survivors. A
    /// provenance-only change stages neither and so must not be passed.</param>
    /// <exception cref="ArgumentException">The Metaverse Object has no id: a writer outside synchronisation only ever
    /// changes persisted objects, so this is a caller defect.</exception>
    public void Collect(MetaverseObject metaverseObject, IEnumerable<MetaverseObjectAttributeValue> changedValues)
    {
        ArgumentNullException.ThrowIfNull(metaverseObject);
        ArgumentNullException.ThrowIfNull(changedValues);

        if (Graph == null)
            return;

        if (metaverseObject.Id == Guid.Empty)
            throw new ArgumentException($"{_writerName}: cannot mark derived inputs of a Metaverse Object with no id.", nameof(metaverseObject));

        if (metaverseObject.Type == null)
        {
            // The writers load the Metaverse Object with its type; without it the graph cannot be consulted, and a
            // silent skip would leave a derived value stale indefinitely, so say so (the Phase 3 worker does the same).
            Log.Warning("{Writer}: Metaverse Object {MvoId} has no Metaverse Object Type loaded; cannot determine which Connected " +
                "Systems' derived Attribute Flows read its changed attributes, so none are marked.", _writerName, metaverseObject.Id);
            return;
        }

        foreach (var connectedSystemId in DerivedInputMarking.GetConnectedSystemsToMark(
                     Graph, metaverseObject.Type.Id, changedValues, excludedConnectedSystemId: null))
        {
            _pending.Add(new DerivedInputChangeMark(metaverseObject.Id, connectedSystemId));
        }
    }

    /// <summary>
    /// Applies the collected marks in one bulk update and clears them. Call at the writer's batch boundary, after the
    /// Metaverse changes that caused them are persisted: a mark set first could be cleared by a hosting-system run that
    /// evaluated the old values. A no-op, with no repository call, when nothing is pending.
    /// </summary>
    /// <returns>The number of Connected System Objects newly marked by this flush.</returns>
    public async Task<int> FlushAsync(ISyncRepository repository)
    {
        ArgumentNullException.ThrowIfNull(repository);

        if (_pending.Count == 0)
            return 0;

        var marks = _pending.ToList();
        _pending.Clear();

        var marked = await repository.MarkConnectedSystemObjectsDerivedInputChangePendingAsync(marks);
        FlushCount++;
        MarksRequested += marks.Count;
        MarksSet += marked;

        Log.Debug("{Writer}: marked {Marked} Connected System Object(s) for derived Attribute Flow re-evaluation from {Requested} " +
            "distinct (Metaverse Object, Connected System) mark(s).", _writerName, marked, marks.Count);
        return marked;
    }

    /// <summary>
    /// Logs the operation's summary statistics (sync integrity: every batch operation logs its totals). Silent when
    /// the feature is off.
    /// </summary>
    public void LogSummary()
    {
        if (Graph == null)
            return;

        if (_pending.Count > 0)
        {
            // Every writer flushes at its batch boundary; marks left here were never applied.
            Log.Warning("{Writer}: {Count} derived-input mark(s) were collected but never flushed.", _writerName, _pending.Count);
        }

        Log.Information("{Writer}: derived Attribute Flow inputs changed outside synchronisation; marked {Marked} Connected System " +
            "Object(s) for re-evaluation by their hosting systems' next synchronisation ({Requested} distinct mark(s), {Flushes} bulk update(s)).",
            _writerName, MarksSet, MarksRequested, FlushCount);
    }
}
