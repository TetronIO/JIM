// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Web.Causality;

/// <summary>
/// The Table view's projection of a <see cref="CausalityModel"/> (#1519 Phase 3, D-S8): a flat,
/// per-object change list, for a recorded Run Profile Execution Item as much as for a Sync Preview.
/// Built once by <see cref="CausalityTableModelBuilder.Build"/>.
/// </summary>
public sealed class CausalityTableModel
{
    /// <summary>
    /// Every object the model's events touch, "Everything" first, then the object being synchronised,
    /// the Identity, and any downstream objects in the order they were first encountered.
    /// </summary>
    public required IReadOnlyList<CausalityTableObject> Objects { get; init; }

    /// <summary>
    /// Every row across every object: object-level rows first, then attribute-change rows, each group
    /// in the order their owning events were encountered.
    /// </summary>
    public required IReadOnlyList<CausalityTableRow> Rows { get; init; }

    /// <summary>
    /// The heading for the "before" column: "Current" for a speculative model, "Before" for a recorded one.
    /// </summary>
    public required string CurrentHeading { get; init; }

    /// <summary>
    /// The heading for the "after" column: "Would be" for a speculative model, "After" for a recorded one.
    /// </summary>
    public required string NextHeading { get; init; }

    /// <summary>
    /// True when the underlying <see cref="CausalityModel"/> was speculative (a Sync Preview).
    /// </summary>
    public bool IsSpeculative { get; init; }
}
