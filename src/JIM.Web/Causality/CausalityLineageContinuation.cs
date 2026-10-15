// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Web.Causality;

/// <summary>
/// A way to carry on past where the walk stopped (#2100): a link to the farthest item it reached, under the host's link
/// policy, so on the item page it opens that item and in the Causality Explorer it re-centres there.
/// </summary>
/// <param name="Text">What the link says ("Continue from Liam Allen").</param>
/// <param name="Href">Where it goes.</param>
public sealed record CausalityLineageContinuation(string Text, string Href);
