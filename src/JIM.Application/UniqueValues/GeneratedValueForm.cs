// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Logic;

namespace JIM.Application.UniqueValues;

/// <summary>
/// A value built from a generated value that is checked before the value is issued (Generated Value Forms, #2023): a
/// form whose Keep unique switch is on, with where its value is checked besides JIM's own records for its attribute
/// (<see cref="GeneratedValueParticipation.DescribeForms"/>).
/// </summary>
public sealed record GeneratedValueForm
{
    /// <summary>
    /// The value, as the shared resolver describes it: the flow that builds it, its attribute, and the way it is built.
    /// </summary>
    public required GeneratedValueBuiltValue BuiltValue { get; init; }

    /// <summary>
    /// The Connected System attributes the value is written to unchanged, whose connector space is checked.
    /// </summary>
    public IReadOnlyCollection<int> ConnectorSpaceAttributeIds { get; init; } = [];

    /// <summary>
    /// The Connected System attributes the value is probed in, where the Connector can probe.
    /// </summary>
    public IReadOnlyList<UniquenessProbeTarget> ProbeTargets { get; init; } = [];

    /// <summary>
    /// Values the object's own accounts already hold in <see cref="ProbeTargets"/>, which a probe finding is no collision.
    /// </summary>
    public IReadOnlyCollection<string> ProbeExemptValues { get; init; } = [];

    /// <summary>
    /// Values of this form a target has just refused (Collision Remediation), which are taken whatever JIM's records say.
    /// </summary>
    public IReadOnlyCollection<string> RejectedValues { get; init; } = [];
}
