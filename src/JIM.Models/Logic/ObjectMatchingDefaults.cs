// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Staging;

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

    /// <summary>
    /// The default for a rule reading these sources: respect case when any source attribute is declared case-exact
    /// (its Connected System treats <c>JBloggs</c> and <c>jbloggs</c> as two identifiers), otherwise
    /// <see cref="CaseSensitive"/>. An expression source has no declaration, so it ignores case (#2051).
    /// </summary>
    /// <remarks>
    /// Any one case-exact source is enough, because sources are alternatives and joining two different identities
    /// on a case-insensitive match is worse than failing to join one. The source attribute must be loaded; a source
    /// carrying only its id counts as having no declaration.
    /// </remarks>
    public static bool CaseSensitiveFor(IEnumerable<ObjectMatchingRuleSource> sources) =>
        CaseSensitiveFor(sources.Select(source => source.ConnectedSystemAttribute));

    /// <summary>
    /// The default for a rule whose sources read these attributes, where a null stands for a source with no
    /// declaration (an expression); see the overload taking the sources.
    /// </summary>
    public static bool CaseSensitiveFor(IEnumerable<ConnectedSystemObjectTypeAttribute?> sourceAttributes) =>
        sourceAttributes.Any(attribute => attribute?.CaseExact == true) || CaseSensitive;
}
