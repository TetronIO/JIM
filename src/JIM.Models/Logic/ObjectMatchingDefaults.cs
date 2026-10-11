// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Logic;

/// <summary>
/// The defaults an Object Matching Rule takes when whoever creates it leaves a setting out. Every surface (portal,
/// REST API, PowerShell, preview) reads them from here, so a rule joins the same way whatever created it.
/// </summary>
public static class ObjectMatchingDefaults
{
    /// <summary>
    /// Object Matching asks "is this the same thing?", and the case-sensitivity policy answers that ignoring case:
    /// <c>JBloggs</c> in one system and <c>jbloggs</c> in another are one identifier. A rule respects case only when
    /// an administrator asks it to.
    /// </summary>
    public const bool CaseSensitive = false;
}
