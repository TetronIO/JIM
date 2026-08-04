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
