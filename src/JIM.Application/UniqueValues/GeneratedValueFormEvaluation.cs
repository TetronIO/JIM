// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Application.UniqueValues;

/// <summary>
/// What a candidate's forms are (Generated Value Forms, #2023): the checks it must pass besides its own, or, when a
/// form needs an input the object has no value for, why it cannot be checked yet.
/// </summary>
public sealed class GeneratedValueFormEvaluation
{
    private GeneratedValueFormEvaluation(IReadOnlyList<GeneratedValueFormCheck> checks, IReadOnlyList<GeneratedValueFormWait> waits)
    {
        Checks = checks;
        Waits = waits;
    }

    /// <summary>
    /// One check per form that applies to the object and has a value for the candidate; empty when none does.
    /// </summary>
    public IReadOnlyList<GeneratedValueFormCheck> Checks { get; }

    /// <summary>
    /// The inputs the forms need and the object does not have yet; empty unless the generated value waits.
    /// </summary>
    public IReadOnlyList<GeneratedValueFormWait> Waits { get; }

    public bool IsWaiting => Waits.Count > 0;

    public static GeneratedValueFormEvaluation Checking(IReadOnlyList<GeneratedValueFormCheck> checks) => new(checks, []);

    public static GeneratedValueFormEvaluation Waiting(IReadOnlyList<GeneratedValueFormWait> waits) => new([], waits);
}
