// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Web.Models;

/// <summary>
/// What an expression editor suggestion would insert (#2053).
/// </summary>
public enum ExpressionSuggestionKind
{
    /// <summary>A function from the function catalogue, inserted as its name and an opening parenthesis.</summary>
    Function,

    /// <summary>A Metaverse attribute's name, inside <c>mv["..."]</c>.</summary>
    MetaverseAttribute,

    /// <summary>A Connected System attribute's name, inside <c>cs["..."]</c>.</summary>
    ConnectedSystemAttribute
}
