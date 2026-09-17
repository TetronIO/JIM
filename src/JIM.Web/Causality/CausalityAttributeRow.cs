// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Web.Causality;

/// <summary>
/// A normalised attribute change row for the causality attribute detail. Single-valued Add and
/// Remove pairs collapse into one Set row carrying the previous value; multi-valued changes keep
/// their individual Add/Remove rows.
/// </summary>
/// <param name="Operation">The change operation.</param>
/// <param name="Name">The attribute name.</param>
/// <param name="TypeAndPlurality">Demoted type and plurality text (e.g. "Text · Single-valued").</param>
/// <param name="Value">The new (or removed) value as display text.</param>
/// <param name="PreviousValue">The previous value for collapsed single-valued updates, else null.</param>
/// <param name="SyncRuleId">
/// The Synchronisation Rule that contributed this value (the Add side of a collapsed Set row), where
/// known. Null when the value was not contributed by a rule, the contributing rule has since been
/// deleted, or this row's source carries no attribution (e.g. a type/plurality-less preview delta).
/// </param>
/// <param name="SyncRuleName">Snapshot of the contributing Synchronisation Rule's name, surviving deletion of the rule.</param>
public sealed record CausalityAttributeRow(
    CausalityAttributeOperation Operation,
    string Name,
    string TypeAndPlurality,
    string? Value,
    string? PreviousValue,
    int? SyncRuleId = null,
    string? SyncRuleName = null);
