// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Logic.Scoping;

/// <summary>
/// One attribute an expression criterion read (#2054), and the values the object held for it, so an explanation can
/// show what the expression was given as well as what it computed.
/// </summary>
public sealed class ScopingExpressionInputExplanation
{
    /// <summary>How the expression reads the attribute, for example <c>cs["department"]</c>.</summary>
    public string Accessor { get; set; } = string.Empty;

    /// <summary>
    /// The values the object held for the attribute, rendered; empty when it held none, or when they are withheld
    /// because the attribute may hold a credential. At most <see cref="MaxValuesShown"/> are listed.
    /// </summary>
    public List<string> Values { get; set; } = [];

    /// <summary>How many values the object held for the attribute, whether or not they are listed.</summary>
    public int ValueCount { get; set; }

    /// <summary>True when the values are withheld because the attribute may hold a credential.</summary>
    public bool Masked { get; set; }

    /// <summary>The most values listed for one input; a multi-valued attribute's remainder is counted, not listed.</summary>
    public const int MaxValuesShown = 10;
}
