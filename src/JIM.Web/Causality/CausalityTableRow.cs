// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Web.Causality;

/// <summary>
/// One row of the Table view (#1519 Phase 3): either an object-level fact derived from a causality
/// event's outcome type (Scope, Projection, Join, Disconnect, Delete, Deprovision, Provision, Export
/// queued, No contributor, Values preserved), or an individual attribute change carried by an event's
/// own <see cref="CausalityAttributeRow"/>s. <see cref="CausalityTableModelBuilder"/> is the only
/// place that derives rows; this type is a plain data carrier.
/// </summary>
/// <param name="ObjectKey">
/// The key of the <see cref="CausalityTableObject"/> this row belongs to, matching
/// <see cref="CausalityTableObject.Key"/>.
/// </param>
/// <param name="ChangeKind">What kind of change this row states.</param>
/// <param name="Attribute">
/// The attribute name for an attribute-change row, or the attribute a No Contributor/Values Preserved
/// fact names; null for every other object-level row (Scope, Projection, Join, Disconnect, Delete,
/// Deprovision, Provision, Export queued), which states no attribute at all.
/// </param>
/// <param name="Current">The current (recorded: before) value or state text; null where there is none.</param>
/// <param name="WouldBe">The would-be (recorded: after) value or state text; null where there is none.</param>
/// <param name="Via">
/// What decided this row: the event's effective Synchronisation Rule name (its own, or the nearest
/// ancestor's; see <see cref="CausalityEvent.EffectiveSyncRuleName"/>), or Deletion Rule / out-of-scope
/// reasoning text drawn from the event's detail message where it carries one; null when neither is
/// present.
/// </param>
/// <param name="SyncRuleId">
/// The Synchronisation Rule to link <see cref="Via"/> to, when it names one; null when <see cref="Via"/>
/// is reasoning text, an unlinked legacy name snapshot, or absent.
/// </param>
/// <param name="OutcomeLabel">
/// The owning event's one label, in the portal's own vocabulary (already the conditional-mood
/// <c>SpeculativeLabel</c> where the model is speculative; see
/// <see cref="CausalityModelBuilder.BuildSpeculative"/>).
/// </param>
/// <param name="Tone">The owning event's visual tone.</param>
/// <param name="OutcomeDetail">
/// A muted secondary line shown under <see cref="OutcomeLabel"/> in the Outcome cell, for the one
/// non-attribute row whose reasoning is not already stated in full by its outcome label: a scheduled
/// deletion's grace text (<see cref="CausalityTableModelBuilder"/>). Null for every other row, including
/// every attribute-change row (its reasoning is the value change itself).
/// </param>
public sealed record CausalityTableRow(
    string ObjectKey,
    CausalityTableChangeKind ChangeKind,
    string? Attribute,
    string? Current,
    string? WouldBe,
    string? Via,
    int? SyncRuleId,
    string OutcomeLabel,
    CausalityTone Tone,
    string? OutcomeDetail = null)
{
    /// <summary>
    /// The plain label for <see cref="ChangeKind"/>, shown in the grid's Change column.
    /// </summary>
    public string ChangeKindLabel => ChangeKind switch
    {
        CausalityTableChangeKind.Scope => "Scope",
        CausalityTableChangeKind.Projection => "Projection",
        CausalityTableChangeKind.Join => "Join",
        CausalityTableChangeKind.Disconnect => "Disconnect",
        CausalityTableChangeKind.Delete => "Delete",
        CausalityTableChangeKind.Deprovision => "Deprovision",
        CausalityTableChangeKind.Provision => "Provision",
        CausalityTableChangeKind.ExportQueued => "Export queued",
        CausalityTableChangeKind.NoContributor => "No contributor",
        CausalityTableChangeKind.ValuesPreserved => "Values preserved",
        CausalityTableChangeKind.AttributeChange => "Attribute change",
        _ => ChangeKind.ToString()
    };
}
