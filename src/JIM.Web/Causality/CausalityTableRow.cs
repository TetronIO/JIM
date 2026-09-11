// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Web.Causality;

/// <summary>
/// One row of the Table view (#1519 Phase 3): either an object-level fact derived from a causality
/// event's outcome type (Scope, Join / Projection, Disconnect, Delete, Deprovision, Provision, Export
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
/// The attribute name for an attribute-change row, or the object-level subject for every other kind
/// ("Metaverse Object", "connector: Glitterband EMEA", or the attribute a No Contributor/Values
/// Preserved fact names).
/// </param>
/// <param name="Current">The current (recorded: before) value or state text; null where there is none.</param>
/// <param name="WouldBe">The would-be (recorded: after) value or state text; null where there is none.</param>
/// <param name="Via">
/// What decided this row: the event's Synchronisation Rule name, or Deletion Rule / out-of-scope
/// reasoning text drawn from the event's detail message where it carries one; null when neither is
/// present.
/// </param>
/// <param name="OutcomeLabel">
/// The owning event's plain-language label (already the conditional-mood <c>SpeculativeLabel</c> where
/// the model is speculative; see <see cref="CausalityModelBuilder.BuildSpeculative"/>).
/// </param>
/// <param name="TechnicalLabel">The owning event's technical label, shown when the panel's technical-names toggle is on.</param>
/// <param name="Tone">The owning event's visual tone.</param>
public sealed record CausalityTableRow(
    string ObjectKey,
    CausalityTableChangeKind ChangeKind,
    string Attribute,
    string? Current,
    string? WouldBe,
    string? Via,
    string OutcomeLabel,
    string TechnicalLabel,
    CausalityTone Tone)
{
    /// <summary>
    /// The plain label for <see cref="ChangeKind"/>, shown in the grid's Change column.
    /// </summary>
    public string ChangeKindLabel => ChangeKind switch
    {
        CausalityTableChangeKind.Scope => "Scope",
        CausalityTableChangeKind.JoinOrProjection => "Join / Projection",
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
