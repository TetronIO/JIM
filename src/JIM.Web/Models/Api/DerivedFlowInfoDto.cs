// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Logic;

namespace JIM.Web.Models.Api;

/// <summary>
/// What makes an import Attribute Flow a Metaverse-Derived Attribute Flow: the Metaverse attributes its Expression
/// reads with <c>mv["..."]</c>, and where it sits in its Metaverse Object Type's evaluation order, as "step N of M".
/// </summary>
public class DerivedFlowInfoDto
{
    /// <summary>
    /// The step the flow is evaluated at, from 2: step 1 is the ordinary pass, and each later step evaluates the
    /// derived flows whose inputs are all settled by the step before. Null when the flow cannot be ordered because it
    /// is on, or depends on, a dependency cycle.
    /// </summary>
    public int? Step { get; set; }

    /// <summary>
    /// How many steps the Metaverse Object Type's evaluation has in all. Disabled derived flows count, so enabling one
    /// never renumbers the others.
    /// </summary>
    public int StepCount { get; set; }

    /// <summary>
    /// The Metaverse attributes the Expression reads, as written, in the order first mentioned.
    /// </summary>
    public List<string> MetaverseInputs { get; set; } = new();

    /// <summary>
    /// Maps the Application layer's step facts to their API representation.
    /// </summary>
    public static DerivedFlowInfoDto FromModel(DerivedFlowStepInfo model) => new()
    {
        Step = model.Step,
        StepCount = model.StepCount,
        MetaverseInputs = model.MetaverseInputs.ToList()
    };
}
