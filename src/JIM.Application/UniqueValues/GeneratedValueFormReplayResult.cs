// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Logic;

namespace JIM.Application.UniqueValues;

/// <summary>
/// One candidate's forms, as <see cref="GeneratedValueFormReplay"/> worked them out (Generated Value Forms, #2023): the
/// value of each form that applies to the object and has one, or why the candidate cannot be checked yet.
/// </summary>
internal sealed class GeneratedValueFormReplayResult
{
    private GeneratedValueFormReplayResult(IReadOnlyList<(GeneratedValueBuiltValue Form, string Value)> values, IReadOnlyList<GeneratedValueFormWait> waits)
    {
        Values = values;
        Waits = waits;
    }

    /// <summary>
    /// Each form that applies and has a value for the candidate, with that value, in the order the forms were given. A
    /// form that does not apply (a higher-priority contributor holds the attribute), or whose chain gives no value, is
    /// absent: there is nothing of it to check.
    /// </summary>
    public IReadOnlyList<(GeneratedValueBuiltValue Form, string Value)> Values { get; }

    /// <summary>
    /// The inputs the forms need and the object does not have; empty unless the candidate cannot be checked yet.
    /// </summary>
    public IReadOnlyList<GeneratedValueFormWait> Waits { get; }

    public bool IsWaiting => Waits.Count > 0;

    public static GeneratedValueFormReplayResult Worked(IReadOnlyList<(GeneratedValueBuiltValue Form, string Value)> values) => new(values, []);

    public static GeneratedValueFormReplayResult Waiting(IReadOnlyList<GeneratedValueFormWait> waits) => new([], waits);
}
