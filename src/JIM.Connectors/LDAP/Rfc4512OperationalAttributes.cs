// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Staging;

namespace JIM.Connectors.LDAP;

/// <summary>
/// Decides which of an RFC 4512 directory's operational attribute types (USAGE other than <c>userApplications</c>)
/// schema discovery offers on its structural Object Types (#2153, D8).
/// </summary>
/// <remarks>
/// <para>
/// An operational attribute belongs to no object class, so walking the classes' MUST and MAY never reaches it, yet
/// several are exactly what identity management wants to read: OpenLDAP's password policy state
/// (<c>pwdFailureTime</c>, <c>pwdAccountLockedTime</c>), creation and modification stamps, <c>memberOf</c>. A
/// directory returns one only when it is requested by name, which is what import does with a selected attribute.
/// </para>
/// <para>
/// Offered means readable on an ordinary entry. Three kinds of operational type never are, so they are left out:
/// a type the directory has declared obsolete; the attributes RFC 4512 confines to the subschema subentry (4.2)
/// and to the root DSE (5.1), with the root DSE attributes other standards and OpenLDAP add; and OpenLDAP's access
/// control pseudo-attributes, which name a target in an ACL and never hold a value. Credential material
/// (<c>pwdHistory</c>, <c>passwordHistory</c>) is readable but never offered, because JIM never manages it.
/// </para>
/// </remarks>
internal static class Rfc4512OperationalAttributes
{
    private static readonly HashSet<string> NeverOnAnOrdinaryEntry = new(StringComparer.OrdinalIgnoreCase)
    {
        // RFC 4512 4.2: the subschema subentry's own attributes.
        "attributeTypes", "objectClasses", "matchingRules", "matchingRuleUse", "ldapSyntaxes",
        "dITContentRules", "dITStructureRules", "nameForms",

        // RFC 4512 5.1: the root DSE.
        "altServer", "namingContexts", "supportedControl", "supportedExtension", "supportedFeatures",
        "supportedLDAPVersion", "supportedSASLMechanisms",

        // Root DSE attributes from other standards (RFC 3045, RFC 2589, RFC 3112) and OpenLDAP itself.
        "vendorName", "vendorVersion", "dynamicSubtrees", "supportedAuthPasswordSchemes",
        "configContext", "monitorContext",

        // OpenLDAP's access control pseudo-attributes.
        "entry", "children"
    };

    /// <summary>
    /// Whether discovery offers <paramref name="attributeType"/> as an operational attribute: its usage is not
    /// <c>userApplications</c>, it is not obsolete, and an ordinary entry can carry a value for it.
    /// </summary>
    internal static bool IsDiscoverable(Rfc4512AttributeTypeDescription attributeType) =>
        attributeType.Usage != Rfc4512AttributeUsage.UserApplications &&
        !attributeType.IsObsolete &&
        attributeType.Name != null &&
        !NeverOnAnOrdinaryEntry.Contains(attributeType.Name) &&
        !CredentialAttributes.IsCredentialAttribute(attributeType.Name);
}
