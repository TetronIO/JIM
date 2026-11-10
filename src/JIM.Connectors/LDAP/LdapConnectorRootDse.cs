// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using System.Text.Json.Serialization;

namespace JIM.Connectors.LDAP;

/// <summary>
/// Holder of key synchronisation-related information about an LDAP directory from its rootDSE object: its type and
/// capabilities, the domain controller reached and pinned, and where its changelog is. Persisted between
/// synchronisation runs as the Connected System's connector data, so later runs and the Connected System page know
/// what the directory is.
/// </summary>
/// <remarks>
/// It holds no Delta Import position. Each Partition keeps its own (<see cref="LdapPartitionWatermark"/>, #2715), so a
/// run that reads one Partition cannot move another's. A record written before then carries the positions it used to
/// hold (HighestCommittedUsn, LastChangeNumber, LastChangeTime, LastAccesslogTimestamp,
/// LastAccesslogTimestampIsLogged); they are ignored on read and dropped by the next write.
/// </remarks>
internal class LdapConnectorRootDse
{
    /// <summary>
    /// The DNS hostname of the connected directory server.
    /// </summary>
    public string? DnsHostName { get; set; }

    /// <summary>
    /// For Active Directory and Samba AD: the domain controller's highestCommittedUSN as this import's rootDSE read
    /// found it, for the check that it has not gone back behind a Partition's watermark. Never persisted: it is the
    /// directory's position now, and a Partition's position is its own watermark.
    /// </summary>
    [JsonIgnore]
    public long? CurrentHighestCommittedUsn { get; set; }

    /// <summary>
    /// Where the directory says its changelog is (the rootDSE's <c>changelog</c> attribute), or null when it
    /// advertises none, in which case the conventional <c>cn=changelog</c> is tried. Recorded so that a later
    /// import and Schema Discovery read the same place.
    /// </summary>
    public string? ChangelogDn { get; set; }

    /// <summary>
    /// The oldest change number the changelog still holds, when the rootDSE advertises it. A baseline older than
    /// this has had changes trimmed from under it, which a Delta Import must refuse rather than silently skip.
    /// </summary>
    public long? FirstChangeNumber { get; set; }

    /// <summary>
    /// The newest change number, when the rootDSE advertises it. Taken as each Partition's watermark in preference
    /// to enumerating the changelog to find it.
    /// </summary>
    public long? AdvertisedLastChangeNumber { get; set; }

    /// <summary>
    /// The detected directory server type, determined from rootDSE capabilities during connection.
    /// Drives all directory-specific behaviour: schema discovery, external ID, delta strategy, etc.
    /// </summary>
    public LdapDirectoryType DirectoryType { get; set; } = LdapDirectoryType.Generic;

    /// <summary>
    /// For Active Directory / Samba AD: the invocationId of the domain controller's NTDS Settings object.
    /// AD USNs are scoped to the DC that issued them, so a USN watermark is only meaningful when read back
    /// against the same DC; each Partition's watermark carries the invocationId it was taken against, and this is
    /// the one the last import reached, shown on the Connected System page. The invocationId changes when a DC is restored from backup (a new
    /// invocationId is generated to prevent USN reuse against stale replication state), so comparing it
    /// between the run that produced the watermark and the current connection detects both a restore and a
    /// connection to an entirely different DC (for example, via DNS round-robin against a domain name Host).
    /// Null for non-AD-family directories, and for AD-family directories where the value could not be read
    /// (for example, insufficient permissions on the NTDS Settings object); a null value means "identity
    /// unknown", not "no mismatch".
    /// </summary>
    public Guid? InvocationId { get; set; }

    /// <summary>
    /// The vendor name of the directory server (e.g., "Samba Team", "Microsoft", "OpenLDAP").
    /// Retained for logging and diagnostics.
    /// </summary>
    public string? VendorName { get; set; }

    /// <summary>
    /// The FQDN of the domain controller all connections for this Connected System are pinned to (issue
    /// #230 Phase 2). Discovered from the directory's dnsHostName on first connection when no Preferred
    /// Domain Controller setting is configured, so that every parallel connection in a run, and every
    /// subsequent run, resolves to the same domain controller (replication consistency, and a stable
    /// identity for the USN watermark). Null when pinning does not apply: non-AD-family directories
    /// (OpenLDAP, Generic), or when a Preferred Domain Controller is explicitly configured, in which case
    /// the setting owns domain controller selection and any previous pin must not survive. Old persisted
    /// JSON predating this property deserialises this to null, which is the intended compatibility path
    /// (equivalent to "no pin yet").
    /// </summary>
    public string? PinnedDirectoryServer { get; set; }

    /// <summary>
    /// The naming contexts actually hosted by the connected directory server, from rootDSE (issue #230).
    /// Used to fail fast when a selected Partition is not hosted by the server the connector is talking
    /// to: AD's crossRef-based partition discovery (CN=Partitions,CN=Configuration) lists every domain in
    /// the forest, including domains the connected domain controller does not hold a naming context for,
    /// and a domain controller does not chase referrals to serve those objects.
    /// Null when the rootDSE query did not return the attribute (for example, insufficient permissions);
    /// a null or empty value means "hosting could not be verified", not "nothing is hosted", so it must
    /// never itself fail an import. Persisted between synchronisation runs like the rest of this class;
    /// also intended to support future capability surfacing (for example, showing which partitions are
    /// actually reachable during discovery).
    /// </summary>
    public List<string>? NamingContexts { get; set; }

    /// <summary>
    /// The vendorVersion attribute from rootDSE, where the directory publishes one (for example
    /// "389-Directory/2.4.5"). Used alongside <see cref="VendorName"/> for directory type detection and retained
    /// for diagnostics. Null when not published, and null in persisted JSON written before it was captured.
    /// </summary>
    public string? VendorVersion { get; set; }

    /// <summary>
    /// The DN of the directory's configuration naming context, from rootDSE's configContext ("cn=config" on
    /// OpenLDAP and 389 Directory Server). This is where those directories hold their password policy
    /// configuration, so password policy discovery reads it from here rather than assuming a location. Null
    /// when the directory does not publish one; Active Directory does not.
    /// </summary>
    public string? ConfigContext { get; set; }

    /// <summary>
    /// The control OIDs the directory advertises in rootDSE's supportedControl. Password policy discovery uses it
    /// to tell whether OpenLDAP has the ppolicy overlay loaded at all. Null when the rootDSE query did not return
    /// the attribute, which is "unknown" rather than "none".
    /// </summary>
    public List<string>? SupportedControls { get; set; }

    /// <summary>
    /// The directory's defaultNamingContext, which Active Directory publishes as the domain root and which
    /// other directories may or may not publish. Null when absent; password policy discovery then falls back to
    /// <see cref="NamingContexts"/>.
    /// </summary>
    public string? DefaultNamingContext { get; set; }

    // -----------------------------------------------------------------------
    // Computed properties — centralised directory-type-specific behaviour
    // -----------------------------------------------------------------------

    /// <summary>
    /// The attribute name used as the unique, immutable external identifier for directory objects.
    /// AD/Samba AD use objectGUID (binary GUID in Microsoft byte order); OpenLDAP uses entryUUID (RFC 4530, string format).
    /// </summary>
    public string ExternalIdAttributeName => DirectoryType switch
    {
        LdapDirectoryType.ActiveDirectory => "objectGUID",
        LdapDirectoryType.SambaAD => "objectGUID",
        LdapDirectoryType.OpenLDAP => "entryUUID",
        LdapDirectoryType.Generic => "entryUUID",
        LdapDirectoryType.DirectoryServer389 => "entryUUID",
        _ => "entryUUID"

    };

    /// <summary>
    /// The data type of the external ID attribute in JIM's attribute model.
    /// AD/Samba AD objectGUID is a binary GUID; OpenLDAP entryUUID is a string representation of a UUID.
    /// </summary>
    public AttributeDataType ExternalIdDataType => DirectoryType switch
    {
        LdapDirectoryType.ActiveDirectory => AttributeDataType.Guid,
        LdapDirectoryType.SambaAD => AttributeDataType.Guid,
        LdapDirectoryType.OpenLDAP => AttributeDataType.Text,
        LdapDirectoryType.Generic => AttributeDataType.Text,
        LdapDirectoryType.DirectoryServer389 => AttributeDataType.Text,
        _ => AttributeDataType.Text

    };

    /// <summary>
    /// Whether the directory is Active Directory or Samba AD, the family whose domain controllers JIM discovers,
    /// pins and verifies the identity of, and whose partitions it checks are hosted by the server it reached.
    /// </summary>
    public bool IsActiveDirectoryFamily => DirectoryType is LdapDirectoryType.ActiveDirectory or LdapDirectoryType.SambaAD;

    /// <summary>
    /// Whether the directory advertises the Permissive Modify control in its supportedControl, and so will accept an
    /// export's add of a value already present, or delete of one already absent, as the success it is. False when the
    /// control is not listed and when supportedControl could not be read: the control is critical, and a directory
    /// that does not understand it refuses the whole modify, so it is never sent on assumption.
    /// </summary>
    public bool AdvertisesPermissiveModify =>
        SupportedControls?.Contains(LdapConnectorConstants.LDAP_SERVER_PERMISSIVE_MODIFY_OID, StringComparer.Ordinal) == true;

    /// <summary>
    /// Where this directory keeps its record of what changed, and so which <see cref="ILdapDeltaSource"/> a Delta
    /// Import reads through. The one place the directory type is mapped to a change source: Active Directory and
    /// Samba AD track uSNChanged and keep tombstones in the Deleted Objects container; OpenLDAP logs writes in the
    /// accesslog overlay; 389 Directory Server and generic directories publish a draft-good-ldap-changelog.
    /// </summary>
    public LdapDeltaSourceKind DeltaSourceKind => DirectoryType switch
    {
        LdapDirectoryType.ActiveDirectory or LdapDirectoryType.SambaAD => LdapDeltaSourceKind.Usn,
        LdapDirectoryType.OpenLDAP => LdapDeltaSourceKind.Accesslog,
        _ => LdapDeltaSourceKind.Changelog
    };

    /// <summary>
    /// Whether the directory's SAM layer enforces single-valued semantics on certain multi-valued schema attributes
    /// (e.g., 'description' on user/group objects). Applies to both Microsoft AD and Samba AD.
    /// </summary>
    public bool EnforcesSamSingleValuedRules => DirectoryType is LdapDirectoryType.ActiveDirectory or LdapDirectoryType.SambaAD;

    /// <summary>
    /// The recommended export concurrency for this directory type.
    /// AD DS, OpenLDAP and 389 Directory Server handle concurrent operations well; Samba AD and
    /// unknown servers are kept conservative due to known quirks (e.g. paged search duplicates).
    /// OpenLDAP gets 8 rather than 16 (#2897): the LDAP Connector's batch-parallelism
    /// recommendation holds the total in flight at 32, so 8 here means 4 batches at once, which
    /// exported the 500k initial load 19.5% faster than 2 batches of 16 at the same load.
    /// </summary>
    public int RecommendedExportConcurrency => DirectoryType switch
    {
        LdapDirectoryType.ActiveDirectory => 16,
        LdapDirectoryType.OpenLDAP => 8,
        LdapDirectoryType.SambaAD => LdapConnectorConstants.DEFAULT_EXPORT_CONCURRENCY,
        LdapDirectoryType.Generic => LdapConnectorConstants.DEFAULT_EXPORT_CONCURRENCY,
        LdapDirectoryType.DirectoryServer389 => 16,
        _ => LdapConnectorConstants.DEFAULT_EXPORT_CONCURRENCY

    };

    /// <summary>
    /// Whether the directory supports paged search results.
    /// Microsoft AD supports paging; Samba AD claims support but returns duplicate results.
    /// OpenLDAP supports paging via Simple Paged Results control.
    /// </summary>
    public bool SupportsPaging => DirectoryTypeSupportsPaging(DirectoryType);

    /// <summary>
    /// Whether a directory of the given type honours the paged-results control, for callers that hold the type
    /// rather than the rootDSE it was read from.
    /// </summary>
    internal static bool DirectoryTypeSupportsPaging(LdapDirectoryType directoryType) => directoryType switch
    {
        LdapDirectoryType.ActiveDirectory => true,
        LdapDirectoryType.SambaAD => false,
        LdapDirectoryType.OpenLDAP => true,
        LdapDirectoryType.Generic => true,
        LdapDirectoryType.DirectoryServer389 => true,
        _ => true
    };
}
