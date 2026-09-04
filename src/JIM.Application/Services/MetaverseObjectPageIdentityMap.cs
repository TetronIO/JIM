// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Staging;
using Serilog;

namespace JIM.Application.Services;

/// <summary>
/// Resolves every load of a given Metaverse Object row, within one sync page, onto a single canonical CLR
/// instance (#1612). A sync page runs two ordered passes (see <c>SyncTaskProcessorBase</c>): Pass 1 tears
/// down obsolete Connected System Objects and, via <c>MarkMvoForDeletionAsync</c>, sets in-memory
/// grace-period deletion markers directly on the Metaverse Object instance a CSO's own navigation already
/// holds; Pass 2 processes joins, and a CSO matching an existing Metaverse Object loads it separately
/// through <c>ObjectMatchingServer</c> -&gt; <c>MetaverseRepository.GetMetaverseObjectAsync</c>. A separate
/// load of that kind is the shape that CAN return a DISTINCT CLR instance of the same database row. A
/// runtime probe against real PostgreSQL found the same-page disconnect-then-rejoin cancellation already
/// works correctly today: JIM's single tracking <c>DbContext</c> resolves both loads to one instance via
/// EF's own identity map, so the split this class guards against is latent, not observed live. This map is
/// defence in depth against a future change to how Metaverse Objects are loaded reintroducing a genuine
/// split (see <c>docs/developer/diagrams/MVO_DELETION_AND_GRACE_PERIOD.md</c> > Same-Page
/// Disconnect-Then-Rejoin Hardening), and it protects every other reference-equality-sensitive accumulator
/// in <c>SyncTaskProcessorBase</c> the same way. Without it, code evaluating state on a load that missed
/// the map (for example <c>EstablishJoinAsync</c> reading <c>LastConnectorDisconnectedDate</c> to decide
/// whether a rejoin cancels a scheduled deletion) would not see Pass 1's markers, leaving a scheduled
/// deletion uncancelled despite a source standing behind the identity again.
/// </summary>
/// <remarks>
/// Canonical-wins is correct because nothing else writes a Metaverse Object row mid-page: the first
/// instance seen for an Id carries whatever in-memory state has already accumulated on it (deletion
/// markers, pending attribute-value lists, its <see cref="MetaverseObject.ConnectedSystemObjects"/>
/// collection), and every later load of the same row is a stale read that must be discarded in the
/// canonical instance's favour, not merged into. A page's identity map must be cleared alongside the EF
/// change tracker at every page boundary (see <c>SyncTaskProcessorBase.ClearPageTrackingState</c>): its
/// lifetime is "while the tracker holds this page's instances", never longer.
/// </remarks>
public sealed class MetaverseObjectPageIdentityMap
{
    private readonly Dictionary<Guid, MetaverseObject> _byId = [];

    /// <summary>
    /// The number of times <see cref="Resolve"/> discarded a distinct instance in favour of the canonical
    /// one already registered for its Id. Zero for the common case where every load in the page goes
    /// through this map; a nonzero count is the tripwire for a load site that bypassed it. Cumulative for
    /// the lifetime of this instance (one map per processor, one processor per run): <see cref="Clear"/>
    /// resets the registered instances at each page boundary but deliberately leaves this counter alone, so
    /// it still answers "did this run ever see a same-page split" after the run completes.
    /// </summary>
    public int AbsorbedCount { get; private set; }

    /// <summary>
    /// Resolves a freshly loaded Metaverse Object onto the canonical instance for its Id. A <c>Guid.Empty</c>
    /// Id (a not-yet-persisted projection) passes through unregistered, since two distinct projections in
    /// the same page must never collide on the shared empty Id. The first sight of a real Id registers and
    /// returns the loaded instance unchanged; every later sight returns the already-registered instance
    /// instead, discarding the newly loaded one.
    /// </summary>
    public MetaverseObject Resolve(MetaverseObject loaded)
    {
        if (loaded.Id == Guid.Empty)
            return loaded;

        if (_byId.TryGetValue(loaded.Id, out var canonical))
        {
            if (!ReferenceEquals(canonical, loaded))
            {
                AbsorbedCount++;
                Log.Debug(
                    "MetaverseObjectPageIdentityMap: absorbed a distinct load of Metaverse Object {MvoId} " +
                    "into the page's canonical instance (a same-page identity split; see #1612).",
                    loaded.Id);
            }

            return canonical;
        }

        _byId[loaded.Id] = loaded;
        return loaded;
    }

    /// <summary>
    /// Rewrites every Connected System Object's <see cref="ConnectedSystemObject.MetaverseObject"/>
    /// navigation in the batch onto the page's canonical instance, via <see cref="Resolve"/>. Call after
    /// every batch load that can bring in already-joined Metaverse Objects (a page's CSO load, a cross-page
    /// reference resolution reload), so every navigation into a given Id lands on the one instance the page
    /// is tracking state on. CSOs with no join (<c>MetaverseObject == null</c>) are left untouched.
    /// </summary>
    public void Seed(IEnumerable<ConnectedSystemObject> csos)
    {
        foreach (var cso in csos)
        {
            if (cso.MetaverseObject != null)
                cso.MetaverseObject = Resolve(cso.MetaverseObject);
        }
    }

    /// <summary>
    /// Registers a newly persisted Metaverse Object once it has a real Id, so a later same-page load of the
    /// same row (a rejoin further down the same page, or during cross-page reference resolution) resolves
    /// onto this instance rather than starting a fresh entry.
    /// </summary>
    public void Register(MetaverseObject persisted)
    {
        if (persisted.Id == Guid.Empty)
            return;

        _byId[persisted.Id] = persisted;
    }

    /// <summary>
    /// Drops every registered instance so the next page starts with an empty map. Call alongside the EF
    /// change tracker clear at every page boundary; the map's lifetime must never outlive the tracker's.
    /// Deliberately does not reset <see cref="AbsorbedCount"/> (see its own remarks).
    /// </summary>
    public void Clear()
    {
        _byId.Clear();
    }
}
