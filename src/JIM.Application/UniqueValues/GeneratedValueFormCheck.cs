// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Application.UniqueValues;

/// <summary>
/// One value a candidate must keep free besides itself (Generated Value Forms, #2023): a form's value for that
/// candidate, and where it is checked. Each passes the gates the generated value passes, for its own attribute (PRD
/// requirement 4), and the candidate is issued only if every one does (requirement 5).
/// </summary>
public sealed record GeneratedValueFormCheck
{
    /// <summary>
    /// The value's name for messages: its Metaverse attribute (Email), or the Connected System attribute an export
    /// expression writes.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// The form's value for the candidate.
    /// </summary>
    public required string Value { get; init; }

    /// <summary>
    /// <see cref="GeneratedValueMode.Import"/> for a value held in a Metaverse attribute, checked against every other
    /// Metaverse Object's value; <see cref="GeneratedValueMode.Export"/> for an export expression's value, held only in
    /// the Connected System attribute it writes.
    /// </summary>
    public required GeneratedValueMode Mode { get; init; }

    /// <summary>
    /// The Metaverse attribute (<see cref="GeneratedValueMode.Import"/>) or Connected System attribute
    /// (<see cref="GeneratedValueMode.Export"/>) the value is held in.
    /// </summary>
    public required int AttributeId { get; init; }

    /// <inheritdoc cref="GeneratedValueForm.ConnectorSpaceAttributeIds"/>
    public IReadOnlyCollection<int> ConnectorSpaceAttributeIds { get; init; } = [];

    /// <inheritdoc cref="GeneratedValueForm.ProbeTargets"/>
    public IReadOnlyList<UniquenessProbeTarget> ProbeTargets { get; init; } = [];

    /// <inheritdoc cref="GeneratedValueForm.ProbeExemptValues"/>
    public IReadOnlyCollection<string> ProbeExemptValues { get; init; } = [];

    /// <inheritdoc cref="GeneratedValueForm.RejectedValues"/>
    public IReadOnlyCollection<string> RejectedValues { get; init; } = [];
}
