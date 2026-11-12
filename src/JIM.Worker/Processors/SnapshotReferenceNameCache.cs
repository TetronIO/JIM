// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Serilog;

namespace JIM.Worker.Processors;

/// <summary>
/// The names a synchronisation's export change snapshots give the objects their references point at: for each
/// (Metaverse Object, target Connected System), the display identifier of the object it is joined to there (#2492, #2939).
/// One instance belongs to one synchronisation run.
/// </summary>
/// <remarks>
/// <para>
/// <b>A name found is kept for the whole run.</b> The groups of a large directory share most of their members, so the
/// same objects are named again and again: a 500,000-person Full Synchronisation named 2.94 million references to
/// 500,000 objects, and asking the database again on every page cost it more than the one lookup each needs.
/// </para>
/// <para>
/// <b>A lookup that found nothing is kept only until <see cref="ForgetMisses"/>,</b> which the run calls at every page
/// boundary and once a page's new target objects are written: the object may have been provisioned since.
/// </para>
/// <para>
/// <b>Why a name cannot go stale within the run.</b> During a synchronisation, a target object's name values are written
/// only when it is provisioned, which adds the object and its secondary external ID and nothing else (the values an
/// export carries reach the object only through the export run's optimistic apply or the next import, neither of which
/// is part of a synchronisation). A target object disconnected or deleted later in the run keeps the name it was found
/// with, which is the right text for a snapshot taken while it was joined. The one way round this is a Schedule that
/// runs an import or an export of the target system in parallel with this synchronisation; a snapshot taken after the
/// rename then shows the name the object had when this run first looked it up. It is display text in the change
/// history, never what is exported.
/// </para>
/// </remarks>
public sealed class SnapshotReferenceNameCache(int capacity = SnapshotReferenceNameCache.DefaultCapacity)
{
    /// <summary>
    /// The most names one run keeps. An entry costs roughly 100 bytes (its key, and a name of a few dozen characters;
    /// a name the object holds as a long DN costs more), so about 100 MB at the cap: about one page's memory budget
    /// (#2934), held for the run, and room for every object of a 1,000,000-object directory. Once full, nothing more is
    /// kept and the names it holds still answer; the rest are looked up as they would be without it.
    /// </summary>
    public const int DefaultCapacity = 1_000_000;

    private readonly Dictionary<(Guid MetaverseObjectId, int ConnectedSystemId), string> _found = [];
    private readonly HashSet<(Guid MetaverseObjectId, int ConnectedSystemId)> _missed = [];

    /// <summary>The most names this cache keeps.</summary>
    public int Capacity { get; } = capacity;

    /// <summary>How many names it holds.</summary>
    public int Count => _found.Count;

    /// <summary>Whether it holds <see cref="Capacity"/> names, and so keeps no more.</summary>
    public bool IsFull => _found.Count >= Capacity;

    /// <summary>
    /// Whether the object's name in the system is known: true with the name if one was found this run, true with null if
    /// a lookup found none since misses were last forgotten, and false if it must be looked up.
    /// </summary>
    public bool TryGet(Guid metaverseObjectId, int connectedSystemId, out string? name)
    {
        if (_found.TryGetValue((metaverseObjectId, connectedSystemId), out var found))
        {
            name = found;
            return true;
        }

        name = null;
        return _missed.Contains((metaverseObjectId, connectedSystemId));
    }

    /// <summary>Keeps a name found for the object, for the rest of the run, unless the cache is full.</summary>
    public void RecordFound(Guid metaverseObjectId, int connectedSystemId, string name)
    {
        var key = (metaverseObjectId, connectedSystemId);
        _missed.Remove(key);
        if (_found.ContainsKey(key))
        {
            _found[key] = name;
            return;
        }

        if (IsFull)
            return;

        _found.Add(key, name);
        if (IsFull)
            Log.Debug("SnapshotReferenceNameCache: Holding {Capacity} names, its limit; further names found this run are not kept", Capacity);
    }

    /// <summary>Notes that the object has no target object in the system, until misses are next forgotten.</summary>
    public void RecordMiss(Guid metaverseObjectId, int connectedSystemId) => _missed.Add((metaverseObjectId, connectedSystemId));

    /// <summary>Forgets every lookup that found nothing, so each is asked again.</summary>
    public void ForgetMisses() => _missed.Clear();
}
