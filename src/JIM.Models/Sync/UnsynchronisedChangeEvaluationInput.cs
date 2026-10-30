// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;

namespace JIM.Models.Sync;

/// <summary>
/// What export evaluation is given for a Metaverse Object whose attribute values were changed by a write outside
/// synchronisation (#2615), in the shape a synchronisation-driven change gives it: the values that changed (the
/// object's current values for the attributes that changed, then the removed values) and the removed values on their
/// own.
/// </summary>
/// <param name="ChangedAttributes">The object's current values for each changed attribute, followed by the values
/// removed from it.</param>
/// <param name="RemovedAttributes">The values removed and not held again; null when there are none. Unsaved instances,
/// which export staging matches by the referenced object and then by content.</param>
/// <param name="SkippedAttributeIds">Recorded attributes that no longer exist, which were left out.</param>
/// <param name="RecordUnreadable">Whether the object carries a record that could not be read, in which case nothing
/// was recorded as changed and the object is reviewed for scope alone.</param>
public sealed record UnsynchronisedChangeEvaluationInput(
    List<MetaverseObjectAttributeValue> ChangedAttributes,
    HashSet<MetaverseObjectAttributeValue>? RemovedAttributes,
    IReadOnlyList<int> SkippedAttributeIds,
    bool RecordUnreadable = false)
{
    /// <summary>
    /// The input for an object with nothing recorded: reviewed for scope alone, as a flag raised by a configuration
    /// change or the Temporal Scope Reconciler always has been.
    /// </summary>
    public static UnsynchronisedChangeEvaluationInput None => new([], null, []);
}
