// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Web.Causality;

/// <summary>
/// Names the synthetic source row that opens each causality view, in plain language or in JIM's own
/// vocabulary according to the technical-names toggle.
/// </summary>
/// <remarks>
/// The row is synthetic: it stands for the record the run was handed rather than for a recorded outcome, so
/// it has no <see cref="CausalityEvent"/> to carry a plain and technical label the way every other row does,
/// and all three views previously hard-coded the plain wording. That made the toggle a half-truth: it swapped
/// every row except the first one on screen.
///
/// One class rather than three literals for the same reason <see cref="CausalityPageContext.RecordLabel"/>
/// exists: the Flow, Timeline and Graph views each rendered their own copy of the record's mention until the
/// copies drifted apart.
/// </remarks>
public static class CausalitySourceLabels
{
    /// <summary>
    /// The source card's and source node's title, as used by the Flow and Graph views.
    /// </summary>
    /// <remarks>
    /// The technical form drops "Source" rather than qualifying it: both views already head the column or
    /// place the node where the reader can see it is the source, and the Graph truncates its titles at
    /// <see cref="CausalityGraphLayoutCalculator.TitleMaxLength"/> characters, which "Source Connected System
    /// Object" would exceed.
    /// </remarks>
    public static string Title(bool technicalNames)
    {
        return technicalNames ? "Connected System Object" : "Source record";
    }

    /// <summary>
    /// The Timeline's opening verb, which reads as a sentence rather than as a card title.
    /// </summary>
    public static string Verb(bool technicalNames)
    {
        return technicalNames ? "Connected System Object processed" : "Record processed";
    }
}
