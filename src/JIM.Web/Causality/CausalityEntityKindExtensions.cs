// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Web.Shared;

namespace JIM.Web.Causality;

/// <summary>
/// Maps the causality model's own entity classification onto <see cref="ObjectChipKind"/>, the one chip
/// vocabulary every object reference on the site now renders through. The causality builders keep
/// <see cref="CausalityEntityKind"/> internally: the outcome-to-link mapping, the sentence segments, the
/// per-card <c>ChipKinds</c> filters and every builder test key on it, and re-typing that whole pipeline
/// onto <see cref="ObjectChipKind"/> would ripple through JIM.Web.Causality for no reader-visible gain. This
/// extension is the one place the two vocabularies meet: every call site that renders an
/// <see cref="ObjectChip"/> from a <see cref="CausalityEntityLink"/> or <see cref="CausalityEntityKind"/>
/// converts through it, so there is exactly one mapping to keep in step rather than one per call site.
/// </summary>
public static class CausalityEntityKindExtensions
{
    /// <summary>
    /// The <see cref="ObjectChipKind"/> an <see cref="ObjectChip"/> renders for this <see cref="CausalityEntityKind"/>.
    /// </summary>
    public static ObjectChipKind ToObjectChipKind(this CausalityEntityKind kind) => kind switch
    {
        CausalityEntityKind.ConnectedSystem => ObjectChipKind.ConnectedSystem,
        CausalityEntityKind.Record => ObjectChipKind.ConnectedSystemObject,
        CausalityEntityKind.Identity => ObjectChipKind.MetaverseObject,
        CausalityEntityKind.SynchronisationRule => ObjectChipKind.SynchronisationRule,
        CausalityEntityKind.PendingExport => ObjectChipKind.PendingExport,
        CausalityEntityKind.DeletionRecord => ObjectChipKind.DeletionRecord,
        CausalityEntityKind.RunProfile => ObjectChipKind.RunProfile,
        _ => ObjectChipKind.RunProfile
    };
}
