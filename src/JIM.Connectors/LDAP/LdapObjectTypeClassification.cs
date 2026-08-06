// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Staging;

namespace JIM.Connectors.LDAP;

/// <summary>
/// Maps how a directory describes an object class onto JIM's connector-agnostic classification vocabulary, so that
/// the schema screen can tell a structural class from an auxiliary one whichever discovery path found it.
/// </summary>
/// <remarks>
/// The two paths learn the same fact differently: an RFC 4512 subschema states the kind in the class definition,
/// while Active Directory carries it as objectClassCategory on the classSchema entry. Where a directory says
/// something JIM has no equivalent for, nothing is reported and the object type is left unclassified; that is a
/// supported state, and better than guessing.
/// </remarks>
internal static class LdapObjectTypeClassification
{
    /// <summary>
    /// OID arcs whose classes belong to the directory server rather than to the directory an administrator manages.
    /// </summary>
    /// <remarks>
    /// An arc is assigned to an enterprise by IANA and does not change, which makes it a far steadier signal than a
    /// class name: matching names would hide a customer's own <c>auditTrail</c> class, and would still miss
    /// OpenLDAP's <c>OpenLDAProotDSE</c>, which shares no prefix with anything. Everything a stock OpenLDAP publishes
    /// under its own arc is server machinery: cn=config (<c>olc*</c>, 1.3.6.1.4.1.4203.1.12.2), the accesslog
    /// overlay (<c>audit*</c>, 1.3.6.1.4.1.4203.666.11.5.2) and the root DSE class (1.3.6.1.4.1.4203.1.4.1). The
    /// classes an administrator manages come from the X.500, COSINE and Internet standards arcs instead, and a
    /// customer's own extensions from the customer's own arc.
    /// </remarks>
    private static readonly string[] InternalOidArcs =
    [
        "1.3.6.1.4.1.4203" // OpenLDAP
    ];

    /// <summary>
    /// Whether an RFC 4512 object class is one the directory keeps for itself, judged from its OID arc and its
    /// OBSOLETE flag. Reports nothing for a class an administrator may legitimately manage.
    /// </summary>
    /// <remarks>
    /// Only <see cref="ObjectTypeTags.Values.VisibilityInternal"/> is ever reported. An object type carrying no
    /// visibility tag already means "show it" under the classification contract, so stating the standard case
    /// explicitly would add a row per object type and tell a consumer nothing it did not already know.
    /// <para>
    /// This brings RFC 4512 directories to where Active Directory already is: the Active Directory discovery path
    /// asks the server for the same judgement in its enumeration filter, via <c>defaultHidingValue</c> and
    /// <c>isDefunct</c>, so it needs nothing here.
    /// </para>
    /// </remarks>
    internal static ConnectorSchemaObjectTypeTag? FromRfc4512Definition(string? oid, bool isObsolete)
    {
        var isInternal = isObsolete || (!string.IsNullOrWhiteSpace(oid) && IsUnderInternalArc(oid));
        return isInternal
            ? new ConnectorSchemaObjectTypeTag(ObjectTypeTags.Keys.Visibility, ObjectTypeTags.Values.VisibilityInternal)
            : null;
    }

    /// <summary>
    /// Whether an OID sits beneath one of the internal arcs. The arc must be followed by a separator, so that
    /// enterprise 42031 is not mistaken for a child of enterprise 4203.
    /// </summary>
    private static bool IsUnderInternalArc(string oid)
    {
        return InternalOidArcs.Any(arc => oid.StartsWith(arc + ".", StringComparison.Ordinal));
    }

    /// <summary>
    /// The class kind from an RFC 4512 subschema object class definition.
    /// </summary>
    internal static ConnectorSchemaObjectTypeTag? FromRfc4512Kind(Rfc4512ObjectClassKind kind)
    {
        var value = kind switch
        {
            Rfc4512ObjectClassKind.Structural => ObjectTypeTags.Values.ClassKindStructural,
            Rfc4512ObjectClassKind.Auxiliary => ObjectTypeTags.Values.ClassKindAuxiliary,
            Rfc4512ObjectClassKind.Abstract => ObjectTypeTags.Values.ClassKindAbstract,
            _ => null
        };

        return value == null ? null : new ConnectorSchemaObjectTypeTag(ObjectTypeTags.Keys.ClassKind, value);
    }

    /// <summary>
    /// The class kind from an Active Directory classSchema entry's objectClassCategory: 1 = structural,
    /// 2 = abstract, 3 = auxiliary. Category 0 is a legacy "88 class" predating those categories, which has no
    /// equivalent in the RFC vocabulary and so is left unclassified.
    /// </summary>
    internal static ConnectorSchemaObjectTypeTag? FromActiveDirectoryObjectClassCategory(string? objectClassCategory)
    {
        var value = objectClassCategory switch
        {
            "1" => ObjectTypeTags.Values.ClassKindStructural,
            "2" => ObjectTypeTags.Values.ClassKindAbstract,
            "3" => ObjectTypeTags.Values.ClassKindAuxiliary,
            _ => null
        };

        return value == null ? null : new ConnectorSchemaObjectTypeTag(ObjectTypeTags.Keys.ClassKind, value);
    }
}
