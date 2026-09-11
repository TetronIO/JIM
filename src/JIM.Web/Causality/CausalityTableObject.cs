// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Web.Causality;

/// <summary>
/// One object touched by the causality model's events, as grouped for the Table view's left
/// navigation (#1519 Phase 3): the object being synchronised, the Identity, a downstream object, or
/// the synthetic "Everything" entry that flattens every row.
/// </summary>
/// <param name="Key">
/// Stable key matching <see cref="CausalityTableRow.ObjectKey"/>: "everything", "source", "identity",
/// or "ds:&lt;system id or name&gt;" for a downstream object.
/// </param>
/// <param name="Role">Which group this object's entry renders under.</param>
/// <param name="DisplayName">The object's display name.</param>
/// <param name="Subtitle">
/// The object's subtitle (system and/or type), or null where neither is known.
/// </param>
/// <param name="Tone">
/// The strongest tone among this object's rows (error &gt; warning &gt; info/primary &gt;
/// success/secondary), or <see cref="CausalityTone.Secondary"/> when it carries no rows.
/// </param>
/// <param name="RowCount">How many rows belong to this object.</param>
public sealed record CausalityTableObject(
    string Key,
    CausalityTableObjectRole Role,
    string DisplayName,
    string? Subtitle,
    CausalityTone Tone,
    int RowCount);
