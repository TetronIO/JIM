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
    /// Every class beneath one of these is internal; <see cref="InternalClassOids"/> names the classes of a vendor
    /// whose arc cannot be treated that way.
    /// </summary>
    /// <remarks>
    /// One rule decides both lists: a class whose entries configure or record the server's own behaviour is
    /// internal; a class that describes an identity, a group or a resource is not, whoever defined it.
    /// <para>
    /// An arc is assigned to an enterprise by IANA and does not change, which makes it a far steadier signal than a
    /// class name: matching names would hide a customer's own <c>auditTrail</c> class, and would still miss
    /// OpenLDAP's <c>OpenLDAProotDSE</c>, which shares no prefix with anything. Everything a stock OpenLDAP publishes
    /// under its own arc is server machinery: cn=config (<c>olc*</c>, 1.3.6.1.4.1.4203.1.12.2), the accesslog
    /// overlay (<c>audit*</c>, 1.3.6.1.4.1.4203.666.11.5.2) and the root DSE class (1.3.6.1.4.1.4203.1.4.1). The
    /// classes an administrator manages come from the X.500, COSINE and Internet standards arcs instead, and a
    /// customer's own extensions from the customer's own arc. So for OpenLDAP the arc alone is the whole answer.
    /// </para>
    /// </remarks>
    private static readonly string[] InternalOidArcs =
    [
        "1.3.6.1.4.1.4203" // OpenLDAP
    ];

    /// <summary>
    /// Exact OIDs of the object classes 389 Directory Server ships for itself, matched as whole strings and never as
    /// a prefix. The same rule as <see cref="InternalOidArcs"/> applies (server behaviour is internal; identities,
    /// groups and resources are not), expressed class by class because 389's arc cannot carry it.
    /// </summary>
    /// <remarks>
    /// The Netscape arc, 2.16.840.1.113730.3.2, is not OpenLDAP's arc. Its numbering is flat, with no sub-arc that
    /// separates machinery from data, and it mixes the two freely: <c>inetOrgPerson</c> (.2, RFC 2798) sits beside
    /// <c>nsslapdConfig</c> (.39), and the classes 389 itself recommends for people, <c>nsPerson</c> (.333),
    /// <c>nsAccount</c> (.331), <c>nsOrgPerson</c> (.334) and <c>nsMemberOf</c> (.329), are numbered among the
    /// replication and plug-in configuration classes. Treating the arc as internal would hide exactly the classes a
    /// 389 administrator is told to use, and a name rule (<c>ns*</c>) would do the same, so each class is judged and
    /// listed by its OID. A class this list does not name shows as noise until it is judged; it is never hidden by
    /// accident, which is the safe way for an exact list to be wrong.
    /// <para>
    /// The entries were captured from a 389 Directory Server 3.1.2 subschema and are grouped by the schema file each
    /// class comes from. 24 of them are the Netscape console classes from <c>30ns-common.ldif</c>,
    /// <c>50ns-*.ldif</c> and <c>01core389.ldif</c>, whose OID is the descriptive placeholder <c>&lt;name&gt;-oid</c>
    /// rather than a number; they ship with every 389 install and are matched by that placeholder verbatim.
    /// <c>nsContainer</c> is the one judgement call and is in: 389 uses it for its own configuration tree, and JIM
    /// synchronises identities and groups, not containers. An internal Object Type is still selectable via
    /// "Show internal object types", so a deployment that does use it loses nothing.
    /// </para>
    /// <para>
    /// Deliberately left visible, so that nobody "fixes" them into the list: <c>inetOrgPerson</c>, <c>nsPerson</c>,
    /// <c>nsAccount</c>, <c>nsOrgPerson</c>, <c>nsMemberOf</c>, <c>mailRecipient</c>, <c>mailGroup</c>,
    /// <c>groupOfMailEnhancedUniqueNames</c>, <c>groupOfURLs</c>, <c>groupOfCertificates</c>, <c>dynamicGroup</c>,
    /// <c>inetUser</c>, <c>inetSubscriber</c>, <c>inetDomain</c>, <c>inetAdmin</c>, <c>ntUser</c>, <c>ntGroup</c>,
    /// <c>mepManagedEntry</c>, <c>mepOriginEntry</c>, <c>nsLicenseUser</c>, <c>netscapeMailServer</c>,
    /// <c>netscapeReversiblePasswordObject</c>, and every third-party schema 389 ships (Samba, Kerberos, sudo,
    /// eduPerson and so on). Each describes something an administrator manages, whoever defined it.
    /// </para>
    /// </remarks>
    private static readonly HashSet<string> InternalClassOids = new(StringComparer.Ordinal)
    {
        // 01core389.ldif
        "2.16.840.1.113730.3.2.39", // nsslapdConfig (structural)
        "2.16.840.1.113730.3.2.40", // directoryServerFeature (structural)
        "2.16.840.1.113730.3.2.41", // nsslapdPlugin (structural)
        "2.16.840.1.113730.3.2.43", // nsSNMP (structural)
        "2.16.840.1.113730.3.2.44", // nsIndex (structural)
        "2.16.840.1.113730.3.2.103", // nsDS5ReplicationAgreement (structural)
        "2.16.840.1.113730.3.2.104", // nsContainer (structural)
        "2.16.840.1.113730.3.2.108", // nsDS5Replica (structural)
        "2.16.840.1.113730.3.2.109", // nsBackendInstance (structural)
        "2.16.840.1.113730.3.2.110", // nsMappingTree (structural)
        "2.16.840.1.113730.3.2.113", // nsTombstone (structural)
        "2.16.840.1.113730.3.2.317", // nsSaslMapping (structural)
        "2.16.840.1.113730.3.2.327", // rootDNPluginConfig (structural)
        "2.16.840.1.113730.3.2.328", // nsSchemaPolicy (structural)
        "2.16.840.1.113730.3.2.332", // nsChangelogConfig (structural)
        "2.16.840.1.113730.3.2.337", // rewriterEntry (structural)
        "2.16.840.1.113730.3.2.340", // pwdPBKDF2PluginConfig (structural)
        "nsEncryptionConfig-oid", // nsEncryptionConfig (structural)
        "nsEncryptionModule-oid", // nsEncryptionModule (structural)

        // 02common.ldif
        "2.16.840.1.113730.3.2.1", // changeLogEntry (structural)
        "2.16.840.1.113730.3.2.6", // referral (structural)
        "2.16.840.1.113730.3.2.10", // netscapeServer (structural)
        "2.16.840.1.113730.3.2.12", // passwordObject (structural)
        "2.16.840.1.113730.3.2.13", // passwordPolicy (structural)
        "2.16.840.1.113730.3.2.30", // glue (structural)
        "2.16.840.1.113730.3.2.32", // netscapeMachineData (structural)
        "2.16.840.1.113730.3.2.35", // LDAPServer (structural)
        "2.16.840.1.113730.3.2.38", // vlvSearch (structural)
        "2.16.840.1.113730.3.2.42", // vlvIndex (structural)
        "2.16.840.1.113730.3.2.84", // cosDefinition (structural)
        "2.16.840.1.113730.3.2.93", // nsRoleDefinition (structural)
        "2.16.840.1.113730.3.2.94", // nsSimpleRoleDefinition (structural)
        "2.16.840.1.113730.3.2.95", // nsComplexRoleDefinition (structural)
        "2.16.840.1.113730.3.2.96", // nsManagedRoleDefinition (structural)
        "2.16.840.1.113730.3.2.97", // nsFilteredRoleDefinition (structural)
        "2.16.840.1.113730.3.2.98", // nsNestedRoleDefinition (structural)
        "2.16.840.1.113730.3.2.99", // cosSuperDefinition (structural)
        "2.16.840.1.113730.3.2.100", // cosClassicDefinition (structural)
        "2.16.840.1.113730.3.2.101", // cosPointerDefinition (structural)
        "2.16.840.1.113730.3.2.102", // cosIndirectDefinition (structural)
        "2.16.840.1.113730.3.2.128", // costemplate (structural)
        "2.16.840.1.113730.3.2.304", // nsView (auxiliary)
        "2.16.840.1.113730.3.2.316", // nsAttributeEncryption (structural)
        "2.16.840.1.113730.3.2.335", // nsSlapiTask (structural)
        "2.16.840.1.113730.3.2.503", // nsDSWindowsReplicationAgreement (structural)

        // 10automember-plugin.ldif
        "2.16.840.1.113730.3.2.322", // autoMemberDefinition (structural)
        "2.16.840.1.113730.3.2.323", // autoMemberRegexRule (structural)

        // 10dna-plugin.ldif
        "2.16.840.1.113730.3.2.324", // dnaPluginConfig (auxiliary)
        "2.16.840.1.113730.3.2.325", // dnaSharedConfig (auxiliary)

        // 10mep-plugin.ldif
        "2.16.840.1.113730.3.2.321", // mepTemplateEntry (auxiliary)
        "2.16.840.1.113730.3.2.336", // mepConfigEntry (auxiliary)

        // 30ns-common.ldif
        "nsAdminDomain-oid", // nsAdminDomain (structural)
        "nsAdminGroup-oid", // nsAdminGroup (structural)
        "nsAdminObject-oid", // nsAdminObject (structural)
        "nsApplication-oid", // nsApplication (structural)
        "nsConfig-oid", // nsConfig (structural)
        "nsDirectoryInfo-oid", // nsDirectoryInfo (structural)
        "nsHost-oid", // nsHost (structural)
        "nsResourceRef-oid", // nsResourceRef (structural)
        "nsTask-oid", // nsTask (structural)
        "nsTaskGroup-oid", // nsTaskGroup (structural)
        "2.16.840.1.113730.3.2.338", // nsLDAPIAuthMap (structural)
        "2.16.840.1.113730.3.2.339", // nsLDAPIFixedAuthMap (structural)

        // 50ns-admin.ldif
        "nsAdminConfig-oid", // nsAdminConfig (structural)
        "nsAdminConsoleUser-oid", // nsAdminConsoleUser (structural)
        "nsAdminGlobalParameters-oid", // nsAdminGlobalParameters (structural)
        "nsAdminResourceEditorExtension-oid", // nsAdminResourceEditorExtension (structural)
        "nsAdminServer-oid", // nsAdminServer (structural)
        "nsCustomView-oid", // nsCustomView (structural)
        "nsDefaultObjectClasses-oid", // nsDefaultObjectClasses (structural)
        "nsGlobalParameters-oid", // nsGlobalParameters (structural)
        "nsTopologyCustomView-oid", // nsTopologyCustomView (structural)
        "nsTopologyPlugin-oid", // nsTopologyPlugin (structural)

        // 50ns-certificate.ldif
        "nsCertificateServer-oid", // nsCertificateServer (structural)
        "2.16.840.1.113730.3.2.18", // netscapeCertificateServer (structural)

        // 50ns-directory.ldif
        "nsDirectoryServer-oid", // nsDirectoryServer (structural)
        "2.16.840.1.113730.3.2.11", // cirReplicaSource (structural)
        "2.16.840.1.113730.3.2.23", // netscapeDirectoryServer (structural)
        "2.16.840.1.113730.3.2.36", // LDAPReplica (structural)
        "2.16.840.1.113730.3.2.82", // nsChangelog4Config (structural)
        "2.16.840.1.113730.3.2.114", // nsConsumer4Config (structural)

        // 50ns-value.ldif
        "2.16.840.1.113730.3.2.45", // nsValueItem (structural)

        // 50ns-web.ldif
        "2.16.840.1.113730.3.2.29", // netscapeWebServer (structural)

        // 60pam-plugin.ldif
        "2.16.840.1.113730.3.2.318", // pamConfig (auxiliary)
    };

    /// <summary>
    /// Whether an RFC 4512 object class is one the directory keeps for itself, judged from its OID (under an internal
    /// arc, or an exact member of the internal class list) and its OBSOLETE flag. Reports nothing for a class an
    /// administrator may legitimately manage.
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
        var isInternal = isObsolete || (!string.IsNullOrWhiteSpace(oid) && (IsUnderInternalArc(oid) || InternalClassOids.Contains(oid)));
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
