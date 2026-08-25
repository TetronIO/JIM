// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Web.Causality;

/// <summary>
/// One run of text within a "Caused by" sentence, and whether it names a Metaverse Attribute (#1223).
/// </summary>
/// <remarks>
/// The sentence is composed as parts rather than as a string because one span inside it, the reference
/// attribute the cascade acted through, is highlighted: "they were removed from Project Diamond's
/// <i>Static Members</i>". Returning markup from the composer would put styling in a plain class and
/// force the caller to trust it; returning the whole sentence as one string would leave the component
/// hunting for the attribute name inside it, which breaks the moment an attribute is named the same as
/// something else in the sentence.
/// </remarks>
/// <param name="Text">The text of this run, already spaced for concatenation with its neighbours.</param>
/// <param name="IsAttributeName">
/// True when this run is the Metaverse Attribute's name, which the chain highlights so the relationship
/// stands out from the sentence carrying it.
/// </param>
public sealed record CausalityCauseSentencePart(string Text, bool IsAttributeName = false);
