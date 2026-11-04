// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.DirectoryServices.Protocols;
using JIM.Utilities;
using Serilog;

namespace JIM.Connectors.LDAP;

/// <summary>
/// Reads, over the LDAP Connector's own bound connection, what the Exchange sign-in needs from the directory (#2021):
/// the domain the connection reached (its naming context, its NetBIOS name and the domain controller's DNS name), and
/// the account the Username names. <see cref="LdapExchangeSignIn"/> decides what they mean.
/// </summary>
internal static class LdapExchangeDirectoryLookup
{
    /// <summary>
    /// Works out the Exchange sign-in for the Username, or why there cannot be one.
    /// </summary>
    internal static LdapExchangeSignInResolution Resolve(LdapConnection connection, string username, ILogger logger)
    {
        var rootDse = ReadOne(connection, new SearchRequest(null, "(objectClass=*)", SearchScope.Base,
            "dnsHostName", "defaultNamingContext", "configurationNamingContext"));
        var namingContext = rootDse == null ? null : LdapConnectorUtilities.GetEntryAttributeStringValue(rootDse, "defaultNamingContext");
        if (string.IsNullOrEmpty(namingContext))
        {
            return new LdapExchangeSignInResolution(null,
                "The directory did not say which domain it holds, so JIM cannot find the account to sign in to Exchange as. Exchange recipients can be managed only on Active Directory.");
        }

        var domain = new LdapExchangeDomain(
            namingContext,
            ReadNetBiosName(connection, namingContext, LdapConnectorUtilities.GetEntryAttributeStringValue(rootDse!, "configurationNamingContext")),
            LdapConnectorUtilities.GetEntryAttributeStringValue(rootDse!, "dnsHostName"));

        var lookup = LdapExchangeSignIn.ParseUsername(username);
        var account = FindAccount(connection, LdapExchangeSignIn.SearchFor(lookup, namingContext), logger);

        return LdapExchangeSignIn.Resolve(lookup, account, domain);
    }

    /// <summary>
    /// The domain's NetBIOS name, from its crossRef in the configuration partition, or null when it cannot be read: the
    /// NetBIOS check is then skipped, and an account in another domain is still refused by where its entry sits.
    /// </summary>
    private static string? ReadNetBiosName(LdapConnection connection, string namingContext, string? configurationContext)
    {
        if (string.IsNullOrEmpty(configurationContext))
            return null;

        var crossRef = ReadOne(connection, new SearchRequest($"CN=Partitions,{configurationContext}",
            $"(&(objectClass=crossRef)(nCName={LdapConnectorUtilities.EscapeLdapFilterValue(namingContext)}))", SearchScope.OneLevel, "nETBIOSName"));
        return crossRef == null ? null : LdapConnectorUtilities.GetEntryAttributeStringValue(crossRef, "nETBIOSName");
    }

    private static LdapExchangeAccount? FindAccount(LdapConnection connection, LdapExchangeAccountSearch search, ILogger logger)
    {
        var request = new SearchRequest(search.BaseDn, search.Filter, search.WholeSubtree ? SearchScope.Subtree : SearchScope.Base,
            "sAMAccountName") { SizeLimit = 2 };
        try
        {
            var response = (SearchResponse)connection.SendRequest(request);

            // More than one match is not an account JIM can sign in as with confidence; none is not an account at all.
            if (response.Entries.Count != 1)
                return null;

            var entry = response.Entries[0];
            var samAccountName = LdapConnectorUtilities.GetEntryAttributeStringValue(entry, "sAMAccountName");
            return string.IsNullOrEmpty(samAccountName) ? null : new LdapExchangeAccount(entry.DistinguishedName, samAccountName);
        }
        catch (DirectoryOperationException ex)
        {
            // A distinguished name that is not in this directory comes back as no such object, or a referral elsewhere.
            logger.Debug("Exchange sign-in: the account search under {BaseDn} found nothing: {Message}",
                LogSanitiser.Sanitise(search.BaseDn), LogSanitiser.Sanitise(ex.Message));
            return null;
        }
    }

    private static SearchResultEntry? ReadOne(LdapConnection connection, SearchRequest request)
    {
        try
        {
            var response = (SearchResponse)connection.SendRequest(request);
            return response.Entries.Count > 0 ? response.Entries[0] : null;
        }
        catch (DirectoryOperationException)
        {
            return null;
        }
    }
}
