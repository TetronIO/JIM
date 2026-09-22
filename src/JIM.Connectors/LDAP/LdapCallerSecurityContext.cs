// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Connectors.LDAP.Security;
using JIM.Utilities;
using Serilog;
using System.DirectoryServices.Protocols;
namespace JIM.Connectors.LDAP;

/// <summary>
/// Reads the security context of the account JIM is bound as: its own security identifier and every group it is
/// transitively a member of, which is the set an access check compares an access control list against.
/// <para>
/// Shared by every rights check the LDAP Connector performs, so that each asks the directory the same question
/// and treats its silences the same way. <b>Anything short of a full set is returned as null, never as a smaller
/// set:</b> an access check evaluated against a partial context produces a denial the account may not deserve,
/// and a denial claimed on the strength of a silence is exactly the wrong answer for a least-privileged
/// deployment.
/// </para>
/// </summary>
internal static class LdapCallerSecurityContext
{
    /// <summary>
    /// The security identifiers of the context the connection authenticated as, read from the rootDSE.
    /// </summary>
    internal const string AttributeTokenGroups = "tokenGroups";

    /// <summary>
    /// Who the directory considers the connection to be. Read for diagnostics rather than for the check itself.
    /// </summary>
    internal const string AttributePrincipalName = "msDS-PrincipalName";

    private const string WellKnownSidEveryone = "S-1-1-0";
    private const string WellKnownSidAuthenticatedUsers = "S-1-5-11";
    private const string WellKnownSidNetwork = "S-1-5-2";

    /// <summary>
    /// Reads the security identifiers of the account JIM is bound as, from the rootDSE.
    /// <para>
    /// The rootDSE form of tokenGroups reports the security context of the connection itself, which sidesteps
    /// having to work out the bound account's Distinguished Name from a configured username that might be a
    /// Distinguished Name, a user principal name, or a down-level logon name.
    /// </para>
    /// </summary>
    /// <param name="executor">The connection to read through.</param>
    /// <param name="logger">Where to record why nothing was returned.</param>
    /// <param name="logContext">Prefixes each log line, so a reader can tell which check asked.</param>
    /// <returns>The security identifiers, or null when the directory did not report them.</returns>
    internal static async Task<HashSet<string>?> ReadAsync(ILdapOperationExecutor executor, ILogger logger, string logContext)
    {
        var request = new SearchRequest { Scope = SearchScope.Base };
        request.Attributes.Add(AttributeTokenGroups);
        request.Attributes.Add(AttributePrincipalName);

        SearchResponse response;
        try
        {
            response = (SearchResponse)await executor.SendRequestAsync(request);
        }
        catch (DirectoryOperationException ex)
        {
            logger.Debug("{Context}: The directory refused to report the connection's security context: {Message}", logContext, LogSanitiser.Sanitise(ex.Message));
            return null;
        }
        catch (LdapException ex)
        {
            logger.Debug("{Context}: Could not read the connection's security context: {Message}", logContext, LogSanitiser.Sanitise(ex.Message));
            return null;
        }

        if (response.Entries.Count == 0)
            return null;

        var attribute = response.Entries[0].Attributes[AttributeTokenGroups];

        // Active Directory omits this entirely, without an error, when it cannot reach a Global Catalog to expand
        // the memberships. Reading that as "belongs to nothing" would deny an account that holds the right
        // through a group.
        if (attribute == null || attribute.Count == 0)
        {
            logger.Debug("{Context}: The directory reported no group memberships for the connection, so its rights cannot be evaluated.", logContext);
            return null;
        }

        // Anything that will not parse is dropped rather than failing the read: one unreadable identifier among
        // many is not a reason to abandon the caller's whole security context.
        var sids = attribute.GetValues(typeof(byte[])).OfType<byte[]>()
            .Select(value => SecurityIdentifier.TryParse(value, 0))
            .Where(sid => sid != null)
            .Select(sid => sid!.Value)
            .ToHashSet(StringComparer.Ordinal);

        if (sids.Count == 0)
            return null;

        // Group expansion does not include the memberships every authenticated network connection has by virtue
        // of being one, and a directory can legitimately grant a right to those. Note the deliberate absence of
        // S-1-5-10 (SELF): that identifier stands for the object being examined rather than the caller, so adding
        // it here would match entries meant for a user acting on their own account.
        sids.Add(WellKnownSidEveryone);
        sids.Add(WellKnownSidAuthenticatedUsers);
        sids.Add(WellKnownSidNetwork);

        return sids;
    }
}
