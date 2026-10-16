// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Logic;

/// <summary>
/// A value built from a generated value, described for display (Generated Value Forms, #2023): the read model behind
/// the "Built from generated value" block on an Attribute Flow, the "Values built from ..." table on a generated
/// Attribute Flow, the REST mapping DTO's <c>builtFromGeneratedValue</c> and <c>generation.builtValues</c>, and
/// <c>Get-JIMSyncRuleMapping</c>.
/// </summary>
public sealed class GeneratedValueBuiltValueDescription
{
    /// <summary>
    /// The Attribute Flow that builds the value; 0 for one being edited and not yet saved.
    /// </summary>
    public int SyncRuleMappingId { get; init; }

    /// <summary>
    /// The Synchronisation Rule holding that Attribute Flow.
    /// </summary>
    public int SyncRuleId { get; init; }

    /// <summary>
    /// The Synchronisation Rule's name.
    /// </summary>
    public string SyncRuleName { get; init; } = string.Empty;

    /// <summary>
    /// The generated Attribute Flow the value is described relative to.
    /// </summary>
    public int GeneratedSyncRuleMappingId { get; init; }

    /// <summary>
    /// The generated Metaverse attribute the value is built from (Account Name).
    /// </summary>
    public string GeneratedAttributeName { get; init; } = string.Empty;

    /// <summary>
    /// Every generated Metaverse attribute the value is built from, <see cref="GeneratedAttributeName"/> first; more than
    /// one for a value <see cref="GeneratedValueBuiltValueKind.BuiltFromSeveralGeneratedValues"/>.
    /// </summary>
    public IReadOnlyList<string> BuiltFromAttributeNames { get; init; } = [];

    /// <summary>
    /// The Metaverse attribute the value is held in, for a value a Derived Attribute Flow builds; null for an export
    /// expression.
    /// </summary>
    public string? MetaverseAttributeName { get; init; }

    /// <summary>
    /// The Connected System attribute an export expression writes; null for a value a Derived Attribute Flow builds.
    /// </summary>
    public string? ConnectedSystemAttributeName { get; init; }

    /// <summary>
    /// The Metaverse attributes the value is built through, nearest the generated value first; empty when it reads the
    /// generated value itself.
    /// </summary>
    public IReadOnlyList<string> Via { get; init; } = [];

    /// <summary>
    /// How the value relates to the generated value.
    /// </summary>
    public GeneratedValueBuiltValueKind Kind { get; init; }

    /// <summary>
    /// The value's "Keep unique with ..." switch.
    /// </summary>
    public bool KeepUnique { get; init; }

    /// <summary>
    /// The generated Attribute Flow's own Collision Remediation switch. While it is off, nothing built from the generated
    /// value is corrected, whatever <see cref="KeepUnique"/> says.
    /// </summary>
    public bool CollisionRemediation { get; init; } = true;

    /// <summary>
    /// Every Connected System attribute the value is written to unchanged.
    /// </summary>
    public IReadOnlyList<GeneratedValueBuiltValueTarget> ExportedTo { get; init; } = [];

    /// <summary>
    /// Whether the value is checked before the generated value is issued: built on the generated flow's own
    /// Synchronisation Rule, its Keep unique switch on, and bound for at least one Connected System the generated value's
    /// exclusions leave in. A candidate whose value is already held is passed over.
    /// </summary>
    public bool CheckedBeforeIssuing { get; init; }

    /// <summary>
    /// The Connected Systems the value is probed in before the generated value is issued, besides JIM's own records, by
    /// name; empty when it is not checked, or checked against JIM's records alone.
    /// </summary>
    public IReadOnlyList<string> ProbedIn { get; init; } = [];

    /// <summary>
    /// For a value built on another Synchronisation Rule whose Attribute Flow reads only Metaverse attributes, the
    /// generated flow's own rule: moving the flow there would have the value checked before issuing. Null otherwise.
    /// </summary>
    public string? MoveToSyncRuleName { get; init; }

    /// <summary>
    /// The expression that builds the value, for the generated flow's preview to work it out from a candidate; null when
    /// the Attribute Flow has no single expression source.
    /// </summary>
    public string? Expression { get; init; }
}
