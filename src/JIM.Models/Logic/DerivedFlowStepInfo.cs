// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Logic;

/// <summary>
/// Where a saved Metaverse-Derived Attribute Flow sits in its Metaverse Object Type's evaluation order (#1750), as
/// the read surfaces report it. Administrators see "step N of M", never the underlying level.
/// </summary>
/// <param name="Step">The step the flow is evaluated at, from 2 (step 1 is the ordinary pass); null when the flow
/// cannot be ordered because it is on, or depends on, a dependency cycle.</param>
/// <param name="StepCount">How many steps the Metaverse Object Type's evaluation has in all (its deepest step).</param>
/// <param name="MetaverseInputs">The Metaverse attributes the flow's expression reads, as written, in the order first
/// mentioned.</param>
public sealed record DerivedFlowStepInfo(int? Step, int StepCount, IReadOnlyList<string> MetaverseInputs);
