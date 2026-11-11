// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Staging;

namespace JIM.Application.Services;

/// <summary>
/// What the export's deferred pass has already read of the anchors its references are written as (#2605), so a later
/// chunk asks the database only for objects it has not seen. Users are members of many groups, so the chunks name the
/// same objects again and again: at Scale750k40kGroups the pass looked up 7.68 million Metaverse Object ids for about
/// 790,000 objects.
/// </summary>
/// <remarks>
/// <para>
/// Only an anchor that cannot change during the pass is remembered, so an answer from here is the one the database would
/// give. That is an anchor the object already holds, of an object whose anchor no export of the pass supplies
/// (<see cref="Models.Transactional.DeferredExportOrderingSummary.AnchorSupplierMetaverseObjectIds"/>): the same rule the
/// pass's ordering (#2905) stands on. An object holding no anchor yet, or none at all in the target system, is asked for
/// again every time: one created earlier in the pass may gain its anchor later in it. An object an export of the pass
/// renames is never remembered, so a reference resolved after the rename is written names it by its new name.
/// </para>
/// <para>
/// Bounded at <see cref="DefaultCapacity"/> entries, roughly 200 MB with directory-style anchors (the id, the summary
/// record, and its anchor string), which is the order of what one chunk of the largest groups already loads. Once full it
/// keeps what it holds and remembers nothing more: the objects named most are named from the first chunks on, and an
/// unremembered object is simply read, as before. Not thread-safe: the pass prepares one chunk at a time.
/// </para>
/// </remarks>
public sealed class ReferenceAnchorCache
{
    /// <summary>
    /// The most anchors a pass remembers: about 200 MB, enough to hold every user of the largest templates.
    /// </summary>
    public const int DefaultCapacity = 1_000_000;

    private readonly Dictionary<Guid, ReferenceAnchorSummary> _anchors = new();
    private readonly IReadOnlySet<Guid> _neverRemembered;
    private readonly int _capacity;

    /// <param name="neverRemembered">The Metaverse Objects whose anchor an export of the pass supplies.</param>
    /// <param name="capacity">The most anchors remembered.</param>
    public ReferenceAnchorCache(IReadOnlySet<Guid> neverRemembered, int capacity = DefaultCapacity)
    {
        ArgumentNullException.ThrowIfNull(neverRemembered);
        _neverRemembered = neverRemembered;
        _capacity = Math.Max(0, capacity);
    }

    /// <summary>How many anchors are remembered.</summary>
    public int Count => _anchors.Count;

    /// <summary>How many ids have been asked about.</summary>
    public long Lookups { get; private set; }

    /// <summary>How many of those were answered from what was remembered.</summary>
    public long Hits { get; private set; }

    /// <summary>
    /// Answers what it can of <paramref name="metaverseObjectIds"/> into <paramref name="into"/>, and returns the rest,
    /// which the caller reads from the database and hands to <see cref="Remember"/>.
    /// </summary>
    public List<Guid> TakeKnown(IReadOnlyCollection<Guid> metaverseObjectIds, IDictionary<Guid, ReferenceAnchorSummary> into)
    {
        ArgumentNullException.ThrowIfNull(metaverseObjectIds);
        ArgumentNullException.ThrowIfNull(into);

        var toFetch = new List<Guid>(metaverseObjectIds.Count);
        foreach (var id in metaverseObjectIds)
        {
            if (_anchors.TryGetValue(id, out var anchor))
                into[id] = anchor;
            else
                toFetch.Add(id);
        }

        Lookups += metaverseObjectIds.Count;
        Hits += metaverseObjectIds.Count - toFetch.Count;
        return toFetch;
    }

    /// <summary>
    /// Remembers what was read from the database: each anchor an object holds, unless an export of the pass supplies it,
    /// while there is room.
    /// </summary>
    public void Remember(IReadOnlyDictionary<Guid, ReferenceAnchorSummary> fetched)
    {
        ArgumentNullException.ThrowIfNull(fetched);

        foreach (var (id, anchor) in fetched)
        {
            if (_anchors.Count >= _capacity)
                return;
            if (anchor.AnchorValue != null && !_neverRemembered.Contains(id))
                _anchors[id] = anchor;
        }
    }
}
