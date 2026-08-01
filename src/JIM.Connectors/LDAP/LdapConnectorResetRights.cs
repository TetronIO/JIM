// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Connectors.LDAP.Security;
using JIM.Utilities;
using Serilog;
using System.DirectoryServices.Protocols;
namespace JIM.Connectors.LDAP;

/// <summary>
/// What JIM established about its ability to reset a password in one part of a directory.
/// </summary>
internal enum ResetRightsOutcome
{
    /// <summary>The account JIM binds as holds the reset-password right here.</summary>
    Granted,

    /// <summary>It does not, and JIM saw enough to be sure of that.</summary>
    Denied,

    /// <summary>JIM could not see enough to say. Never to be presented as a denial.</summary>
    CouldNotDetermine
}

/// <summary>
/// What the rights check found for one container.
/// </summary>
internal sealed class ResetRightsFinding
{
    internal required string ContainerDn { get; init; }

    internal required ResetRightsOutcome Outcome { get; init; }

    /// <summary>A plain statement of what was found, and where the outcome is not a grant, why.</summary>
    internal required string Detail { get; init; }
}

/// <summary>
/// Establishes whether the account JIM binds as can reset passwords where JIM would be provisioning, by reading
/// the target's access control list and evaluating it, without writing anything.
/// <para>
/// The obvious approach does not work and is worth naming so it is not tried again: Active Directory's
/// allowedAttributesEffective lists the attributes the caller may write, computed purely from a
/// RIGHT_DS_WRITE_PROPERTY check ([MS-ADTS] 3.1.1.4.5.7), whereas resetting a password is granted by the
/// User-Force-Change-Password control access right ([MS-ADTS] 3.1.1.3.1.5.1). The two are disjoint, so that check
/// reports a least-privileged delegate as having no rights and a Domain Admin as having them: precisely backwards.
/// </para>
/// <para>
/// So JIM reads the security descriptor of a sample object and evaluates it itself. Reading the object's own
/// descriptor is sufficient: a directory materialises inherited entries into every descendant, and resolves which
/// of them apply to the object's class, so there is no need to walk the container chain and replay inheritance
/// ([MS-ADTS] 6.1.3).
/// </para>
/// <para>
/// <b>Every silence is an unknown, never a denial.</b> A directory withholds what the caller may not see by
/// omitting it: an attribute simply absent from an entry, or a search returning no rows, both with a success
/// result code. Each of those paths reports <see cref="ResetRightsOutcome.CouldNotDetermine"/>, because a denial
/// claimed on the strength of a silence is exactly the wrong answer for a least-privileged deployment.
/// </para>
/// </summary>
internal class LdapConnectorResetRights
{
    private readonly ILdapOperationExecutor _executor;
    private readonly ILogger _logger;

    internal LdapConnectorResetRights(ILdapOperationExecutor executor, ILogger logger)
    {
        _executor = executor;
        _logger = logger;
    }

    /// <summary>
    /// Checks each container JIM manages, reporting them separately because a directory grants rights per part of
    /// its tree and "everywhere except one place" is the answer an administrator needs to see.
    /// </summary>
    internal async Task<IReadOnlyList<ResetRightsFinding>> CheckAsync(IReadOnlyList<string> containerDns, CancellationToken cancellationToken)
    {
        var callerSids = await GetCallerSecurityContextAsync();
        if (callerSids == null)
        {
            // Without the full set of groups the account belongs to, no denial can be justified: the right may
            // well be held through a group JIM never saw.
            return containerDns.Select(dn => new ResetRightsFinding
            {
                ContainerDn = dn,
                Outcome = ResetRightsOutcome.CouldNotDetermine,
                Detail = "JIM could not read which groups the account it connects as belongs to, so it cannot tell what that account is allowed to do."
            }).ToList();
        }

        var findings = new List<ResetRightsFinding>(containerDns.Count);
        foreach (var containerDn in containerDns)
        {
            cancellationToken.ThrowIfCancellationRequested();
            findings.Add(await CheckContainerAsync(containerDn, callerSids));
        }

        return findings;
    }

    /// <summary>
    /// Reads the security identifiers of the account JIM is bound as, from the rootDSE.
    /// <para>
    /// The rootDSE form of tokenGroups reports the security context of the connection itself, which sidesteps
    /// having to work out the bound account's Distinguished Name from a configured username that might be a
    /// Distinguished Name, a user principal name, or a down-level logon name.
    /// </para>
    /// </summary>
    /// <returns>The security identifiers, or null when the directory did not report them.</returns>
    private async Task<HashSet<string>?> GetCallerSecurityContextAsync()
    {
        var request = new SearchRequest { Scope = SearchScope.Base };
        request.Attributes.Add(AttributeTokenGroups);
        request.Attributes.Add(AttributePrincipalName);

        SearchResponse response;
        try
        {
            response = (SearchResponse)await _executor.SendRequestAsync(request);
        }
        catch (DirectoryOperationException ex)
        {
            _logger.Debug("LdapConnectorResetRights: The directory refused to report the connection's security context: {Message}", LogSanitiser.Sanitise(ex.Message));
            return null;
        }
        catch (LdapException ex)
        {
            _logger.Debug("LdapConnectorResetRights: Could not read the connection's security context: {Message}", LogSanitiser.Sanitise(ex.Message));
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
            _logger.Debug("LdapConnectorResetRights: The directory reported no group memberships for the connection, so its rights cannot be evaluated.");
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

    /// <summary>
    /// Finds one ordinary user in the container and evaluates its access control list.
    /// </summary>
    private async Task<ResetRightsFinding> CheckContainerAsync(string containerDn, HashSet<string> callerSids)
    {
        var request = new SearchRequest(containerDn, SampleUserFilter, SearchScope.Subtree, AttributeSecurityDescriptor)
        {
            SizeLimit = 1
        };

        // Without this, the directory reads the request as also asking for the audit portion of the descriptor,
        // which needs a privilege a least-privileged service account has no reason to hold. It then omits the
        // whole attribute rather than refusing, so every object would look like it had no access control list.
        request.Controls.Add(new SecurityDescriptorFlagControl(SecurityMasks.Owner | SecurityMasks.Group | SecurityMasks.Dacl));

        SearchResponse response;
        try
        {
            response = (SearchResponse)await _executor.SendRequestAsync(request);
        }
        catch (DirectoryOperationException ex)
        {
            return Undetermined(containerDn, $"The directory refused the read: {ex.Message}");
        }
        catch (LdapException ex)
        {
            return Undetermined(containerDn, $"JIM could not read from this container: {ex.Message}");
        }

        if (response.Entries.Count == 0)
            return Undetermined(containerDn,
                "JIM found no account here to check against. That is what a container with no accounts looks like, and also what one JIM is not allowed to search looks like.");

        var entry = response.Entries[0];
        var attribute = entry.Attributes[AttributeSecurityDescriptor];
        if (attribute == null || attribute.Count == 0)
            return Undetermined(containerDn,
                "The directory did not return the permissions for the account JIM sampled here, which usually means the account JIM connects as is not allowed to read them.");

        if (attribute.GetValues(typeof(byte[])).OfType<byte[]>().FirstOrDefault() is not { } descriptorBytes)
            return Undetermined(containerDn, "The directory returned the permissions in a form JIM could not read.");

        var securityDescriptor = SecurityDescriptorParser.TryParse(descriptorBytes);
        if (securityDescriptor == null)
            return Undetermined(containerDn, "JIM could not make sense of the permissions the directory returned.");

        var outcome = ControlAccessRightEvaluator.Evaluate(securityDescriptor, callerSids, ResetPasswordRight);

        _logger.Debug("LdapConnectorResetRights: Evaluated reset rights in '{Container}' against '{Sample}': {Outcome}",
            LogSanitiser.Sanitise(containerDn), LogSanitiser.Sanitise(entry.DistinguishedName), outcome);

        return outcome == AccessCheckOutcome.Granted
            ? new ResetRightsFinding
            {
                ContainerDn = containerDn,
                Outcome = ResetRightsOutcome.Granted,
                Detail = "The account JIM connects as can reset passwords here."
            }
            : new ResetRightsFinding
            {
                ContainerDn = containerDn,
                Outcome = ResetRightsOutcome.Denied,
                Detail = "The account JIM connects as cannot reset passwords here. Grant it the 'Reset Password' permission on this container; it does not need to be a Domain Admin."
            };
    }

    private static ResetRightsFinding Undetermined(string containerDn, string detail) =>
        new() { ContainerDn = containerDn, Outcome = ResetRightsOutcome.CouldNotDetermine, Detail = detail };

    #region constants
    /// <summary>
    /// The security identifiers of the context the connection authenticated as, read from the rootDSE.
    /// </summary>
    internal const string AttributeTokenGroups = "tokenGroups";

    /// <summary>
    /// Who the directory considers the connection to be. Read for diagnostics rather than for the check itself.
    /// </summary>
    internal const string AttributePrincipalName = "msDS-PrincipalName";

    internal const string AttributeSecurityDescriptor = "nTSecurityDescriptor";

    /// <summary>
    /// An ordinary user account to sample.
    /// <para>
    /// Accounts with adminCount set are deliberately excluded. A directory periodically overwrites the access
    /// control list of accounts in its privileged groups from a template and switches off inheritance on them, so
    /// a delegation made on the container does not apply. Sampling one would report the container as denied when
    /// every ordinary account in it is fine.
    /// </para>
    /// </summary>
    internal const string SampleUserFilter = "(&(objectCategory=person)(objectClass=user)(!(adminCount=1)))";

    /// <summary>
    /// User-Force-Change-Password, the control access right that permits resetting another account's password
    /// without knowing the current one ([MS-ADTS] 5.1.3.2.1).
    /// </summary>
    internal static readonly Guid ResetPasswordRight = new("00299570-246d-11d0-a768-00aa006e0529");

    private const string WellKnownSidEveryone = "S-1-1-0";
    private const string WellKnownSidAuthenticatedUsers = "S-1-5-11";
    private const string WellKnownSidNetwork = "S-1-5-2";
    #endregion
}
