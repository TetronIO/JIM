// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;

namespace JIM.Web.Models.Api;

/// <summary>
/// An attribute a configuration test reads, such as one a Synchronisation Rule's scoping criteria compare or an Object
/// Matching Rule reads, with the values the test used for it.
/// </summary>
public class AttributeSampleDto
{
    /// <summary>
    /// The attribute's name.
    /// </summary>
    public string Attribute { get; set; } = null!;

    /// <summary>
    /// The attribute's type.
    /// </summary>
    public AttributeDataType Type { get; set; }

    /// <summary>
    /// Whether the attribute holds one value or several.
    /// </summary>
    public AttributePlurality Plurality { get; set; }

    /// <summary>
    /// The values tested, one line each; empty for no value.
    /// </summary>
    public List<string> ValueLines { get; set; } = [];
}
