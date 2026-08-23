// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Web.Causality;

/// <summary>
/// Names the Timeline's synthetic source row, in plain language or in JIM's own vocabulary
/// according to the technical-names toggle.
/// </summary>
/// <remarks>
/// The row is synthetic: it stands for the record the run was handed rather than for a recorded outcome, so
/// it has no <see cref="CausalityEvent"/> to carry a plain and technical label the way every other row does,
/// and the views previously hard-coded the plain wording. That made the toggle a half-truth: it swapped
/// every row except the first one on screen. The retired Flow and Graph views shared this class too (its
/// <c>Title</c> retired with them); the Spine names objects from the model instead.
/// </remarks>
public static class CausalitySourceLabels
{
    /// <summary>
    /// The Timeline's opening verb, which reads as a sentence rather than as a card title.
    /// </summary>
    public static string Verb(bool technicalNames)
    {
        return technicalNames ? "Connected System Object processed" : "Record processed";
    }
}
