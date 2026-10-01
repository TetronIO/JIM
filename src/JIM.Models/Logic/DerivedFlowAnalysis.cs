// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Logic;

/// <summary>
/// A read-only analysis of a proposed import Attribute Flow against the Metaverse-Derived Attribute Flow dependency
/// graph (#1750), for the portal to show live as an administrator types: whether the flow is derived, what it reads,
/// where it would sit in the evaluation order, and exactly what the save would refuse or warn about. Nothing is
/// written; the proposal need not be saved.
/// </summary>
public sealed class DerivedFlowAnalysis
{
    /// <summary>
    /// What the proposal is.
    /// </summary>
    public DerivedFlowAnalysisStatus Status { get; init; }

    /// <summary>
    /// The Metaverse attributes the expression reads with <c>mv["..."]</c>, as written, in the order first
    /// mentioned; names that are not attributes of the Metaverse Object Type are included (and reported in
    /// <see cref="Errors"/>). Empty unless <see cref="Status"/> is <see cref="DerivedFlowAnalysisStatus.Derived"/>.
    /// </summary>
    public IReadOnlyList<string> MetaverseInputs { get; init; } = [];

    /// <summary>
    /// The step the flow would be evaluated at, from 1. A derived flow is at step 2 or later; an ordinary flow
    /// contributing an attribute a derived flow reads is at step 1. Null when not applicable, for an ordinary flow
    /// no derived flow reads, and for a derived flow that cannot be ordered because of a dependency cycle.
    /// </summary>
    public int? Step { get; init; }

    /// <summary>
    /// How many steps the Metaverse Object Type's evaluation would have with the proposal saved; 0 when not
    /// applicable.
    /// </summary>
    public int StepCount { get; init; }

    /// <summary>
    /// The evaluation order through the flow's inputs (transitively) to its target, step by step, for display.
    /// Attributes that cannot be ordered because of a cycle are left out. Empty unless the flow is derived.
    /// </summary>
    public IReadOnlyList<DerivedFlowAnalysisStep> Steps { get; init; } = [];

    /// <summary>
    /// Every reason the save would be refused, each exactly as the save reports it. Empty when it would succeed.
    /// </summary>
    public IReadOnlyList<string> Errors { get; init; } = [];

    /// <summary>
    /// The message the save would be refused with (the reasons joined, exactly as the save's error carries them);
    /// null when the save would succeed.
    /// </summary>
    public string? BlockingError => Errors.Count == 0 ? null : string.Join(" ", Errors);

    /// <summary>
    /// The non-blocking warnings the save would raise about the flow, each exactly as the save reports it.
    /// </summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>
    /// The result for a proposal with nothing to analyse.
    /// </summary>
    public static DerivedFlowAnalysis NotApplicable { get; } = new() { Status = DerivedFlowAnalysisStatus.NotApplicable };
}
