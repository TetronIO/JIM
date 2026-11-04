// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Interfaces;
using JIM.Models.Staging;

namespace JIM.Connectors.LDAP;

/// <summary>
/// Places directory objects among a set of Partitions by distinguished name: an object is in the Partition whose DN is
/// the longest suffix of its own, compared RDN by RDN as LDAP compares names (#2723). The one rule for where a directory
/// object is: <see cref="LdapConnectorUtilities.PartitionForDn"/>, which judges which Partition's Delta Import watermark
/// covers a change (#2715), is this placer too.
/// </summary>
/// <remarks>
/// <para>
/// The longest, because OpenLDAP and 389 Directory Server can host one suffix beneath another (a subordinate database),
/// and Active Directory's application partitions (<c>DC=DomainDnsZones</c>) sit beneath the domain's, so an entry in
/// the nested suffix lies under both. RDN by RDN, because a raw suffix comparison would put
/// <c>uid=pat,adc=corp,dc=local</c>, or a leaf RDN whose value holds an escaped comma, under <c>dc=corp,dc=local</c>.
/// </para>
/// <para>
/// Each Partition's DN is parsed once, here, and the Partitions are held longest first, so placing an object costs one
/// parse of its own DN and a comparison of its last RDNs against each Partition until one matches. A Partition whose DN
/// will not parse holds nothing.
/// </para>
/// </remarks>
internal sealed class LdapPartitionPlacer : IConnectorPartitionPlacer
{
    private readonly List<(ConnectedSystemPartition Partition, LdapDistinguishedName Dn)> _longestFirst;

    internal LdapPartitionPlacer(IReadOnlyCollection<ConnectedSystemPartition> partitions)
    {
        // OrderByDescending is stable, so of two Partitions naming the same suffix the first given wins.
        _longestFirst = partitions
            .Select(partition => (Partition: partition, Dn: LdapDistinguishedName.TryParse(partition.ExternalId, out var partitionDn) ? partitionDn : null))
            .Where(candidate => candidate.Dn != null)
            .Select(candidate => (candidate.Partition, Dn: candidate.Dn!))
            .OrderByDescending(candidate => candidate.Dn.Rdns.Count)
            .ToList();
    }

    public ConnectedSystemPartition? GetPartition(string? objectIdentifier)
    {
        if (_longestFirst.Count == 0 || !LdapDistinguishedName.TryParse(objectIdentifier, out var dn))
            return null;

        return _longestFirst
            .Where(candidate => EndsWithRdns(dn, candidate.Dn))
            .Select(candidate => candidate.Partition)
            .FirstOrDefault();
    }

    /// <summary>Whether a DN's last RDNs are exactly those of a suffix, each compared as LDAP compares names.</summary>
    private static bool EndsWithRdns(LdapDistinguishedName dn, LdapDistinguishedName suffix)
    {
        var offset = dn.Rdns.Count - suffix.Rdns.Count;
        if (offset < 0)
            return false;

        for (var i = 0; i < suffix.Rdns.Count; i++)
        {
            if (!RdnsAreEqual(dn.Rdns[offset + i], suffix.Rdns[i]))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Whether two RDNs name the same thing: the same attribute types with the same values, case-insensitively, in any
    /// order (a multi-valued RDN's components are a set). Values are compared unescaped, so <c>cn=a\,b</c> and
    /// <c>cn=a\2Cb</c> agree.
    /// </summary>
    private static bool RdnsAreEqual(LdapRelativeDistinguishedName left, LdapRelativeDistinguishedName right) =>
        left.Components.Count == right.Components.Count
        && left.Components.All(component => right.Components.Any(other =>
            string.Equals(component.Type, other.Type, StringComparison.OrdinalIgnoreCase)
            && string.Equals(component.Value.Trim(), other.Value.Trim(), StringComparison.OrdinalIgnoreCase)));
}
