// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Staging;

namespace JIM.Utilities;

/// <summary>
/// How two text values of a Connected System attribute compare when JIM asks whether a value has changed: export
/// staging's no-net-change check, export confirmation and Drift Detection (#2051, #2195).
/// </summary>
/// <remarks>
/// <para>
/// A change of case is a change, so ordinary text compares exactly: a display name stored as given must have its new
/// case exported. An identifier is the exception, because its value is the thing it identifies. A reference's value is
/// the object it points to, and an External ID or Secondary External ID (an LDAP distinguished name) names its object
/// whatever its case unless the attribute is declared case-exact. A directory reports a distinguished name in the case
/// it stored, and the case of its parent components is the directory's, not JIM's to assert.
/// </para>
/// <para>
/// References only carry text where they are unresolved; resolved references compare by the referenced Connected
/// System Object, which callers check first.
/// </para>
/// </remarks>
public static class AttributeValueComparison
{
    /// <summary>
    /// Whether the attribute identifies objects, so that its values answer "the same thing?" rather than "changed?".
    /// </summary>
    public static bool IsIdentifier(ConnectedSystemObjectTypeAttribute? attribute) =>
        attribute != null && (attribute.Type == AttributeDataType.Reference || attribute.IsExternalId || attribute.IsSecondaryExternalId);

    /// <summary>
    /// Whether two text values of the attribute are the same: an identity comparison for an identifier, following its
    /// case-exact declaration (<see cref="IdentifierKey"/>), and an exact one for everything else (including when the
    /// attribute is not known).
    /// </summary>
    public static bool AreSame(ConnectedSystemObjectTypeAttribute? attribute, string? first, string? second) =>
        ComparerFor(attribute).Equals(first, second);

    /// <summary>
    /// The comparer matching <see cref="AreSame"/>, for sets of the attribute's values.
    /// </summary>
    public static StringComparer ComparerFor(ConnectedSystemObjectTypeAttribute? attribute) =>
        IsIdentifier(attribute) ? IdentifierKey.Comparer(attribute!.CaseExact) : StringComparer.Ordinal;
}
