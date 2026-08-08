// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Staging;
using Serilog;
using System.DirectoryServices.Protocols;
namespace JIM.Connectors.LDAP;

/// <summary>
/// Discovers the domain controllers in an AD-family forest, with the Active Directory Site each belongs to
/// (issue #1167). Only called for directories where <see cref="LdapConnectorRootDse.UseUsnDeltaImport"/> is true
/// (Active Directory, Samba AD); callers are responsible for that check, since discovery relies on the
/// CN=Sites,CN=Configuration hierarchy other LDAP directories do not have.
/// </summary>
internal class LdapConnectorDirectoryServers
{
    private readonly LdapConnection _connection;
    private readonly ILogger _logger;

    internal LdapConnectorDirectoryServers(LdapConnection ldapConnection, ILogger logger)
    {
        _connection = ldapConnection;
        _logger = logger;
    }

    /// <summary>
    /// Searches CN=Sites,&lt;configurationNamingContext&gt; for nTDSDSA (NTDS Settings) objects, one of which
    /// exists per domain controller in the forest, then reads the dNSHostName of each one's parent server
    /// object.
    /// </summary>
    /// <exception cref="InvalidOperationException">The rootDSE did not expose a configurationNamingContext, so the CN=Sites subtree cannot be located.</exception>
    internal async Task<List<ConnectorDirectoryServer>> GetDirectoryServersAsync()
    {
        return await Task.Run(() =>
        {
            var configurationNamingContext = LdapConnectorUtilities.GetConfigurationNamingContext(_connection, _logger);
            if (string.IsNullOrEmpty(configurationNamingContext))
                throw new InvalidOperationException("Couldn't get configuration naming context from rootDSE, so domain controllers cannot be discovered.");

            var sitesDn = $"CN=Sites,{configurationNamingContext}";
            var request = new SearchRequest(sitesDn, "(objectClass=nTDSDSA)", SearchScope.Subtree);
            request.Attributes.Add("distinguishedName");
            var response = (SearchResponse)_connection.SendRequest(request);

            _logger.Debug("GetDirectoryServersAsync: Found {Count} nTDSDSA entries under {SitesDn}", response.Entries.Count, sitesDn);

            // One base-scope lookup per server object for its dNSHostName. The number of domain controllers in
            // a forest is small (single/low-double digits in virtually every deployment), so an N+1 query shape
            // here is a non-issue; it also keeps the mapping logic below independent of any batching scheme.
            var entries = response.Entries
                .Cast<SearchResultEntry>()
                .Select(entry =>
                {
                    var ntdsDsaDn = entry.DistinguishedName;
                    var serverDn = LdapConnectorUtilities.GetServerDnFromNtdsDsaDn(ntdsDsaDn);
                    var dnsHostName = serverDn != null ? GetDnsHostName(serverDn) : null;
                    return (NtdsDsaDn: ntdsDsaDn, DnsHostName: dnsHostName);
                });

            return LdapConnectorUtilities.MapNtdsDsaEntriesToDirectoryServers(entries, _logger);
        });
    }

    /// <summary>
    /// Reads dNSHostName from a server object. Returns null (rather than throwing) when the object cannot be
    /// read: a server object missing or unreadable means JIM has nothing to offer for that domain controller,
    /// not that discovery as a whole has failed.
    /// </summary>
    private string? GetDnsHostName(string serverDn)
    {
        try
        {
            var request = new SearchRequest(serverDn, "(objectClass=server)", SearchScope.Base);
            request.Attributes.Add("dNSHostName");
            var response = (SearchResponse)_connection.SendRequest(request);

            return response.Entries.Count == 0
                ? null
                : LdapConnectorUtilities.GetEntryAttributeStringValue(response.Entries[0], "dNSHostName");
        }
        catch (DirectoryOperationException ex) when (ex.Response?.ResultCode == ResultCode.NoSuchObject)
        {
            return null;
        }
        catch (LdapException ex) when (ex.ErrorCode == 32) // LDAP_NO_SUCH_OBJECT
        {
            return null;
        }
    }
}
