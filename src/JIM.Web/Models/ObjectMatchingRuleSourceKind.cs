// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Web.Models;

/// <summary>
/// What an Object Matching Rule being composed in the portal matches on (#2052): a Connected System attribute's own
/// values, or the result of an expression over the Connected System Object.
/// </summary>
public enum ObjectMatchingRuleSourceKind
{
    Attribute,
    Expression
}
