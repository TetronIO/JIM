// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Staging;
using JIM.Utilities;
using Serilog;
using System.DirectoryServices.Protocols;
namespace JIM.Connectors.LDAP;

/// <summary>
/// Reads the password policy a directory enforces, so that initial password settings can be pre-filled from the
/// target rather than retyped by an administrator.
/// <para>
/// Each directory family publishes its policy somewhere different: Active Directory on the domain root, OpenLDAP
/// in a policy entry the ppolicy overlay names from cn=config, and 389 Directory Server on cn=config itself. This
/// class picks the reader for the detected directory type and holds what the readers share. A directory JIM does
/// not recognise publishes nothing a client can find, and says so rather than guessing.
/// </para>
/// <para>
/// Whatever is discovered is a floor, never a guarantee: every supported directory allows stricter policies to be
/// applied to subsets of accounts, and password filters and check modules are invisible over LDAP entirely. Each
/// reader records what it could establish about both as
/// <see cref="ConnectedSystemPasswordPolicy.PolicyOverrideSignal"/> and
/// <see cref="ConnectedSystemPasswordPolicy.FurtherChecksApply"/>, and why it read what it read as
/// <see cref="ConnectedSystemPasswordPolicy.DiscoveryOutcome"/>.
/// </para>
/// </summary>
internal class LdapConnectorPasswordPolicy
{
    private readonly ILdapOperationExecutor _executor;
    private readonly ILogger _logger;
    private readonly LdapDirectoryType _directoryType;

    internal LdapConnectorPasswordPolicy(ILdapOperationExecutor executor, ILogger logger, LdapDirectoryType directoryType)
    {
        _executor = executor;
        _logger = logger;
        _directoryType = directoryType;
    }

    /// <summary>
    /// Reads the policy the directory applies by default, and separately establishes whether policies exist that
    /// override it for some accounts.
    /// </summary>
    /// <param name="scope">Where the directory keeps its data and configuration, from the rootDSE.</param>
    /// <returns>
    /// The policy, with its outcome recorded, or null when the Connector could not say anything at all (which
    /// leaves any previously discovered policy in place).
    /// </returns>
    internal async Task<ConnectedSystemPasswordPolicy?> GetPasswordPolicyAsync(LdapPasswordPolicyScope scope)
    {
        switch (_directoryType)
        {
            case LdapDirectoryType.ActiveDirectory:
            case LdapDirectoryType.SambaAD:
                return await new LdapConnectorPasswordPolicyActiveDirectory(_executor, _logger).GetPasswordPolicyAsync(scope.DefaultNamingContext);

            case LdapDirectoryType.OpenLDAP:
                return await new LdapConnectorPasswordPolicyOpenLdap(_executor, _logger).GetPasswordPolicyAsync(scope);

            case LdapDirectoryType.DirectoryServer389:
                return await new LdapConnectorPasswordPolicy389(_executor, _logger).GetPasswordPolicyAsync(scope);

            default:
                _logger.Debug("LdapConnectorPasswordPolicy: This directory is not one whose password policy JIM knows how to read, so none was looked for.");
                return NotPublished();
        }
    }

    /// <summary>
    /// The row for a directory that publishes no policy a client can read: nothing discovered, nothing to
    /// override, and an outcome that says why.
    /// </summary>
    internal static ConnectedSystemPasswordPolicy NotPublished() => new()
    {
        Discovered = DateTime.UtcNow,
        DiscoveryOutcome = PasswordPolicyDiscoveryOutcome.NotPublished,
        PolicyOverrideSignal = PolicyOverrideSignal.Absent
    };

    /// <summary>
    /// Runs a presence probe: a search whose only question is whether anything matches, capped at one entry so it
    /// costs the same on a directory of ten entries and a directory of ten million.
    /// <para>
    /// <b>An empty result never means absent.</b> Directories apply access control to searches as a silent
    /// filter, and the attributes these probes look for are operational, so a caller without rights gets a
    /// successful, empty result indistinguishable from a directory where nothing matches. Empty is therefore
    /// undetermined, and so is a refusal, including the administrative limit some directories apply to an
    /// unindexed search. A size limit error is the opposite: the directory found more than it was allowed to
    /// return, which proves presence.
    /// </para>
    /// </summary>
    internal static async Task<PolicyOverrideSignal> ProbeForOverridesAsync(ILdapOperationExecutor executor, ILogger logger, SearchRequest request, string description)
    {
        request.SizeLimit = 1;

        try
        {
            var response = (SearchResponse)await executor.SendRequestAsync(request);
            if (response.Entries.Count > 0)
                return PolicyOverrideSignal.Present;

            logger.Debug("LdapConnectorPasswordPolicy: The probe for {Description} under '{Base}' returned nothing. This cannot be told apart from having no rights to see them, so the result is undetermined.",
                description, LogSanitiser.Sanitise(request.DistinguishedName));
            return PolicyOverrideSignal.CouldNotDetermine;
        }
        catch (DirectoryOperationException ex) when (ProvesPresence(ex.Response))
        {
            return PolicyOverrideSignal.Present;
        }
        catch (DirectoryOperationException ex)
        {
            logger.Debug("LdapConnectorPasswordPolicy: Could not determine whether {Description} exist under '{Base}': {Message}",
                description, LogSanitiser.Sanitise(request.DistinguishedName), LogSanitiser.Sanitise(ex.Message));
            return PolicyOverrideSignal.CouldNotDetermine;
        }
        catch (LdapException ex) when (ex.ErrorCode == (int)ResultCode.SizeLimitExceeded)
        {
            return PolicyOverrideSignal.Present;
        }
        catch (LdapException ex)
        {
            logger.Debug("LdapConnectorPasswordPolicy: Could not determine whether {Description} exist under '{Base}': {Message}",
                description, LogSanitiser.Sanitise(request.DistinguishedName), LogSanitiser.Sanitise(ex.Message));
            return PolicyOverrideSignal.CouldNotDetermine;
        }
    }

    /// <summary>
    /// Whether a search that ended in an error nonetheless proved something matched: either entries came back
    /// before the error, or the error was the size limit, which a directory only reports when more matched than
    /// it was allowed to return.
    /// </summary>
    private static bool ProvesPresence(DirectoryResponse? response) =>
        response is SearchResponse partial &&
        (partial.ResultCode == ResultCode.SizeLimitExceeded || partial.Entries?.Count > 0);

    /// <summary>
    /// Converts one of Active Directory's Interval-syntax duration values into a <see cref="TimeSpan"/>.
    /// <para>
    /// These are counts of 100-nanosecond intervals, stored as the negative of the duration, so a 90 day maximum
    /// password age reads as roughly -77.7 trillion. Two values mean "no limit" and both occur in the wild:
    /// the minimum possible 64-bit integer, which is what Active Directory writes for "never expires", and zero,
    /// which is what setting a maximum age of zero days produces. Treating either as a duration would yield an
    /// absurd expiry date, and treating zero as "expires immediately" would be worse still.
    /// </para>
    /// </summary>
    /// <returns>The duration, or null where the directory imposes no limit.</returns>
    internal static TimeSpan? ParseInterval(long? rawValue)
    {
        if (rawValue is not { } value || value == 0 || value == long.MinValue)
            return null;

        // Stored negative by convention, but read defensively: a directory writing the positive form should not
        // produce a negative TimeSpan.
        var ticks = Math.Abs(value);
        return TimeSpan.FromTicks(ticks);
    }

    /// <summary>
    /// Converts a duration in whole seconds, which is how OpenLDAP and 389 Directory Server express password
    /// ages, into a <see cref="TimeSpan"/>. Zero and absent both mean "no limit" on both directories.
    /// </summary>
    internal static TimeSpan? ParseSeconds(long? rawValue) =>
        rawValue is > 0 and var seconds ? TimeSpan.FromSeconds(seconds) : null;

    /// <summary>
    /// A count where zero means "no rule", which is how both OpenLDAP and 389 Directory Server treat a zero
    /// minimum length or history depth.
    /// </summary>
    internal static int? PositiveOrNull(int? value) => value is > 0 ? value : null;

    /// <summary>
    /// Whether the domain requires passwords to meet Active Directory's complexity rule, which is bit 0 of
    /// pwdProperties (DOMAIN_PASSWORD_COMPLEX).
    /// </summary>
    internal static bool IsComplexityRequired(int pwdProperties) =>
        (pwdProperties & DomainPasswordComplex) == DomainPasswordComplex;

    internal static int? ReadInt(SearchResultEntry entry, string attributeName)
    {
        var raw = ReadRaw(entry, attributeName);
        return int.TryParse(raw, out var value) ? value : null;
    }

    internal static long? ReadLong(SearchResultEntry entry, string attributeName)
    {
        var raw = ReadRaw(entry, attributeName);
        return long.TryParse(raw, out var value) ? value : null;
    }

    internal static string? ReadRaw(SearchResultEntry entry, string attributeName)
    {
        var attribute = entry.Attributes[attributeName];
        return attribute == null || attribute.Count == 0 ? null : attribute[0]?.ToString();
    }

    /// <summary>
    /// Whether two Distinguished Names name the same entry, allowing for the case and spacing differences a
    /// directory tolerates when they are written by different hands.
    /// </summary>
    internal static bool DistinguishedNamesMatch(string? left, string? right)
    {
        if (left == null || right == null)
            return false;

        return string.Equals(NormaliseDn(left), NormaliseDn(right), StringComparison.OrdinalIgnoreCase);
    }

    private static string NormaliseDn(string dn) =>
        string.Join(",", dn.Split(',').Select(component => component.Trim()));

    #region constants
    internal const string AttributeMinPwdLength = "minPwdLength";
    internal const string AttributePwdProperties = "pwdProperties";
    internal const string AttributePwdHistoryLength = "pwdHistoryLength";
    internal const string AttributeMaxPwdAge = "maxPwdAge";
    internal const string AttributeMinPwdAge = "minPwdAge";

    /// <summary>
    /// DOMAIN_PASSWORD_COMPLEX, bit 0 of pwdProperties.
    /// </summary>
    internal const int DomainPasswordComplex = 0x00000001;

    /// <summary>
    /// Active Directory's complexity rule requires characters from three of its five recognised categories.
    /// The rule is fixed in the product and cannot be configured, so the number is a constant rather than
    /// something read from the directory.
    /// </summary>
    internal const int ActiveDirectoryRequiredCharacterClasses = 3;

    /// <summary>
    /// Where Active Directory holds the policies that override the domain-wide one, relative to the domain root.
    /// </summary>
    internal const string PasswordSettingsContainerRdn = "CN=Password Settings Container,CN=System";

    internal const string PasswordSettingsObjectClass = "msDS-PasswordSettings";

    internal const string AttributeDomainFunctionality = "domainFunctionality";

    /// <summary>
    /// The domainFunctionality value for Windows Server 2008, the level at which Fine-Grained Password Policies
    /// became available. Below this they cannot exist.
    /// </summary>
    internal const int DomainFunctionalityWindows2008 = 3;

    /// <summary>
    /// The five categories a directory that counts character classes recognises. Active Directory's rule and 389
    /// Directory Server's passwordMinCategories both count these, with letters outside the cased alphabets (389
    /// calls them 8-bit characters) as the fifth.
    /// </summary>
    internal const PasswordCharacterClasses FiveRecognisedCharacterClasses =
        PasswordCharacterClasses.Uppercase |
        PasswordCharacterClasses.Lowercase |
        PasswordCharacterClasses.Digit |
        PasswordCharacterClasses.Symbol |
        PasswordCharacterClasses.OtherUnicodeLetter;
    #endregion
}
