// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Web.Models;

/// <summary>
/// The Mapping Type an Attribute Flow is filtered under on the Attribute Flow tab (see
/// <see cref="AttributeFlowMappingTypes.Of"/>): its source type, with Derived Attribute Flows (#1750) apart from ordinary
/// Expressions. Declared in the order the filter's chips are shown.
/// </summary>
public enum AttributeFlowMappingType
{
    AttributeMapping = 0,
    ExpressionMapping = 1,

    /// <summary>An import Expression that reads the Metaverse Object with <c>mv["..."]</c>.</summary>
    Derived = 2,

    GeneratedMapping = 3,
    AdvancedMapping = 4,
    NotSet = 5
}
