// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Exceptions;
using JIM.Models.Logic;

namespace JIM.Application.Exceptions;

/// <summary>
/// Thrown at the start of a run when the enabled Metaverse-Derived Attribute Flows (#1750) contain a dependency
/// cycle, so no evaluation order exists (plan decision 11). Save-time validation refuses a cycle, so reaching this
/// means one slipped past it: two concurrent saves that each passed validation, or configuration saved while the
/// feature was off. The run must not proceed on a guessed order; the message names every attribute and
/// Synchronisation Rule on each cycle so the administrator can break it.
/// <para>
/// An <see cref="OperationalException"/>: a configuration fault the administrator fixes, not a defect in JIM, so the
/// failed Activity records the message without a stack trace, as for JIM's other operational failures.
/// </para>
/// </summary>
public class DerivedFlowCycleException : OperationalException
{
    /// <summary>
    /// Every cycle found in the run-time graph.
    /// </summary>
    public IReadOnlyList<DerivedFlowCycle> Cycles { get; }

    public DerivedFlowCycleException(string message, IReadOnlyList<DerivedFlowCycle> cycles)
        : base(message)
    {
        Cycles = cycles;
    }
}
