// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;

namespace JIM.Models.Preview;

/// <summary>
/// The deletion settings an administrator is proposing for a Metaverse Object Type, as the preview framework's
/// pilot adapter receives them (#1114). Deliberately a flat, JSON-round-trippable record rather than a
/// <see cref="MetaverseObjectType"/>: a preview may be evaluated in JIM.Worker, so the proposal has to survive
/// being serialised onto a queue, and handing over an entity graph would invite an adapter to read the saved
/// configuration instead of the proposed one.
/// </summary>
/// <param name="DeletionRule">Which objects the type would delete automatically.</param>
/// <param name="DeletionGracePeriod">How long after disconnection deletion would wait. Null means no wait.</param>
/// <param name="DeletionTriggerConnectedSystemIds">
/// The authoritative sources whose disconnection would trigger deletion under
/// <see cref="MetaverseObjectDeletionRule.WhenAuthoritativeSourceDisconnected"/>.
///
/// Carried for validation only, and this is worth stating plainly: the trigger list decides what happens **at the
/// moment a Connected System Object disconnects**, so changing it moves no object's deletion date today and the
/// preview will honestly report no impact from it alone. What it can do is make the proposal invalid, which stage
/// 1 catches.
/// </param>
/// <param name="DeletionTriggerMode">
/// Whether any one authoritative source disconnecting triggers deletion, or all of them must have gone (#119).
/// Read at the same moment, and therefore with the same standing impact as the trigger list: none.
/// </param>
public record MetaverseObjectTypeDeletionSettingsProposal(
    MetaverseObjectDeletionRule DeletionRule,
    TimeSpan? DeletionGracePeriod,
    IReadOnlyList<int> DeletionTriggerConnectedSystemIds,
    AuthoritativeSourceTriggerMode DeletionTriggerMode = AuthoritativeSourceTriggerMode.AllSourcesDisconnect)
{
    /// <summary>
    /// The settings this proposal would put in force that decide a marked object's fate. The trigger list and mode
    /// are absent by design: they are read when a Connected System Object disconnects, not by the housekeeping
    /// sweep, so they cannot move the deletion date of an object that is already marked.
    /// </summary>
    public MetaverseObjectDeletionSettings ToSettings() =>
        new(DeletionRule, DeletionGracePeriod);
}
