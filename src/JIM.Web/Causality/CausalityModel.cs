// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Web.Causality;

/// <summary>
/// The complete causality visualisation model for one Run Profile Execution Item: the page context
/// and the tree of causality events, with roots ordered by Ordinal.
/// </summary>
public sealed class CausalityModel
{
    /// <summary>
    /// The page-level context the model was built with.
    /// </summary>
    public required CausalityPageContext Context { get; init; }

    /// <summary>
    /// Root events (outcomes without a parent), ordered by Ordinal.
    /// </summary>
    public required IReadOnlyList<CausalityEvent> Roots { get; init; }

    /// <summary>
    /// Enumerates every event in the tree, depth-first in display order.
    /// </summary>
    public IEnumerable<CausalityEvent> AllEvents()
    {
        var stack = new Stack<CausalityEvent>();
        for (var i = Roots.Count - 1; i >= 0; i--)
            stack.Push(Roots[i]);

        while (stack.Count > 0)
        {
            var current = stack.Pop();
            yield return current;
            for (var i = current.Children.Count - 1; i >= 0; i--)
                stack.Push(current.Children[i]);
        }
    }
}
