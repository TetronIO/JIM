// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Logic;

namespace JIM.Models.Sync;

/// <summary>
/// A generated export mapping's (Unique Value Generation, #242) result from export staging, carried on the
/// <see cref="Transactional.PendingExportAttributeValueChange"/> it belongs to in place of a resolved value,
/// exactly as <see cref="PendingGeneratedValue"/> stands in for a resolved value on the import side. The
/// synchronous engine performs no I/O and knows nothing of the unique value generation service that actually
/// produces a value (plan decision 6): it evaluates the mapping's base expression, records this marker and
/// stops, so a later pass (the worker, before the Pending Export is persisted) can resolve it.
/// <para>
/// Unlike <see cref="PendingGeneratedValue"/>, this is not held on a transient collection: the change it
/// belongs to is itself an ordinary mapped entity, so this property is annotated
/// <c>[NotMapped]</c> directly. A change carrying a non-null marker must never reach persistence; the worker's
/// integrity guard in <c>SyncTaskProcessorBase.FlushPendingExportOperationsAsync</c> throws if one does.
/// </para>
/// </summary>
public sealed class PendingGeneratedExportValue
{
    /// <summary>
    /// The generated mapping that produced this marker, carrying <see cref="SyncRuleMapping.Generation"/> (the
    /// uniqueness token and its settings) and <see cref="SyncRuleMapping.TargetConnectedSystemAttribute"/>.
    /// </summary>
    public required SyncRuleMapping Mapping { get; init; }

    /// <summary>
    /// The generated mapping's base expression, evaluated exactly as an ordinary export expression mapping's
    /// value would be. Null when the mapping has no base expression at all (valid for a Sequence or Random
    /// token), or when <see cref="BaseUnavailable"/> is set.
    /// </summary>
    public string? BaseValue { get; init; }

    /// <summary>
    /// True when the base expression could not be evaluated for this object this pass: an input it reads has no
    /// value and the mapping's Missing Input Behaviour is "contribute no value" (the default for a generated
    /// mapping, FR 29), or evaluation produced no usable value. The worker resolves this as
    /// <c>StickyOnly</c>: any value the Connected System Object already holds (an existing generated
    /// assignment) is kept and reasserted; nothing new is generated or adopted this pass.
    /// </summary>
    public bool BaseUnavailable { get; init; }
}
