// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Web.Causality;

/// <summary>
/// Names the Timeline's synthetic source row, in the portal's own vocabulary.
/// </summary>
/// <remarks>
/// The row is synthetic: it stands for the object the run was handed rather than for a recorded outcome, so
/// it has no <see cref="CausalityEvent"/> to carry a label the way every other row does, and the views
/// previously hard-coded the wording here. The retired Flow and Graph views shared this class too (its
/// <c>Title</c> retired with them); the Lineage names objects from the model instead.
/// </remarks>
public static class CausalitySourceLabels
{
    /// <summary>
    /// The Timeline's opening verb, which reads as a sentence rather than as a card title. In the
    /// conditional mood for a Sync Preview (#1519): nothing has been processed yet, so the root reads
    /// as what a run would do.
    /// </summary>
    public static string Verb(bool isSpeculative = false)
    {
        return isSpeculative ? "Connected System Object would be processed" : "Connected System Object processed";
    }
}
