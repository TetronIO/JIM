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
/// Only Active Directory publishes a domain-wide policy an ordinary client can read. Directories running a
/// password policy overlay hold the equivalent settings in entries whose location is a matter of local
/// configuration and is not advertised anywhere a client can find it, so discovery there returns nothing rather
/// than guessing.
/// </para>
/// <para>
/// Whatever is discovered is a floor, never a guarantee: Active Directory allows stricter policies to be applied
/// to subsets of accounts, and password filters installed on a Domain Controller are invisible over LDAP
/// entirely.
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

    private bool IsActiveDirectory =>
        _directoryType is LdapDirectoryType.ActiveDirectory or LdapDirectoryType.SambaAD;

    /// <summary>
    /// Reads the policy from the domain root, and separately establishes whether policies exist that override it
    /// for some accounts.
    /// </summary>
    /// <param name="domainRootDn">The domain naming context, from the rootDSE.</param>
    internal async Task<ConnectedSystemPasswordPolicy?> GetPasswordPolicyAsync(string domainRootDn)
    {
        if (!IsActiveDirectory)
        {
            _logger.Debug("LdapConnectorPasswordPolicy: This directory does not publish a discoverable domain-wide password policy, so none was read.");
            return null;
        }

        if (string.IsNullOrEmpty(domainRootDn))
        {
            _logger.Warning("LdapConnectorPasswordPolicy: No domain naming context was available, so the password policy could not be read.");
            return null;
        }

        var request = new SearchRequest(domainRootDn, "(objectClass=*)", SearchScope.Base,
            AttributeMinPwdLength, AttributePwdProperties, AttributePwdHistoryLength, AttributeMaxPwdAge, AttributeMinPwdAge);

        SearchResponse response;
        try
        {
            response = (SearchResponse)await _executor.SendRequestAsync(request);
        }
        catch (DirectoryOperationException ex)
        {
            _logger.Warning("LdapConnectorPasswordPolicy: The directory refused the password policy read on '{DomainRoot}': {Message}",
                LogSanitiser.Sanitise(domainRootDn), LogSanitiser.Sanitise(ex.Message));
            return null;
        }
        catch (LdapException ex)
        {
            _logger.Warning("LdapConnectorPasswordPolicy: Could not read the password policy from '{DomainRoot}': {Message}",
                LogSanitiser.Sanitise(domainRootDn), LogSanitiser.Sanitise(ex.Message));
            return null;
        }

        if (response.Entries.Count == 0)
        {
            _logger.Warning("LdapConnectorPasswordPolicy: The domain root '{DomainRoot}' returned no entry, so no password policy was read.",
                LogSanitiser.Sanitise(domainRootDn));
            return null;
        }

        var entry = response.Entries[0];
        var complexityRequired = ReadInt(entry, AttributePwdProperties) is { } pwdProperties
            ? IsComplexityRequired(pwdProperties)
            : (bool?)null;

        var policy = new ConnectedSystemPasswordPolicy
        {
            Discovered = DateTime.UtcNow,
            MinimumLength = ReadInt(entry, AttributeMinPwdLength),
            ComplexityRequired = complexityRequired,
            PasswordHistoryLength = ReadInt(entry, AttributePwdHistoryLength),
            MaximumPasswordAge = ParseInterval(ReadLong(entry, AttributeMaxPwdAge)),
            MinimumPasswordAge = ParseInterval(ReadLong(entry, AttributeMinPwdAge)),
            FineGrainedPolicySignal = await DetectFineGrainedPoliciesAsync(domainRootDn)
        };

        // Active Directory's complexity rule is fixed rather than configurable: when the flag is on, a password
        // must draw on three of five character categories. Recording it explicitly keeps the model free of
        // Active Directory assumptions and lets the generator validate against it without special-casing.
        if (complexityRequired == true)
        {
            policy.RequiredCharacterClassCount = ActiveDirectoryRequiredCharacterClasses;
            policy.RecognisedCharacterClasses =
                PasswordCharacterClasses.Uppercase |
                PasswordCharacterClasses.Lowercase |
                PasswordCharacterClasses.Digit |
                PasswordCharacterClasses.Symbol |
                PasswordCharacterClasses.OtherUnicodeLetter;
        }

        _logger.Debug("LdapConnectorPasswordPolicy: Read policy from '{DomainRoot}'. MinimumLength={MinimumLength}, ComplexityRequired={Complexity}, FineGrained={FineGrained}",
            LogSanitiser.Sanitise(domainRootDn), policy.MinimumLength, policy.ComplexityRequired, policy.FineGrainedPolicySignal);

        return policy;
    }

    /// <summary>
    /// Establishes whether the domain holds policies that override the domain-wide one for some accounts, without
    /// reading what they say.
    /// <para>
    /// Presence is what an administrator needs: it tells them the discovered policy is a floor rather than the
    /// whole story. Reading the policies themselves needs privileges JIM's service account has no business
    /// holding, so JIM asks only whether any exist.
    /// </para>
    /// <para>
    /// <b>An empty result does not mean there are none.</b> Active Directory applies access control to searches as
    /// a silent filter: a caller without rights over the Password Settings Container gets a successful search
    /// returning zero entries, indistinguishable from a domain that genuinely has no policies. The container is
    /// readable only by Domain Admins unless someone has delegated it, which is the common case for a
    /// least-privilege service account, so treating empty as "none" would hand most deployments a confident and
    /// wrong answer. Empty is therefore reported as undetermined.
    /// </para>
    /// <para>
    /// The one circumstance in which their absence can be proved is a domain whose functional level predates the
    /// feature, where they cannot exist at all.
    /// </para>
    /// </summary>
    private async Task<FineGrainedPolicySignal> DetectFineGrainedPoliciesAsync(string domainRootDn)
    {
        if (await IsBelowFineGrainedPolicyFunctionalLevelAsync())
        {
            _logger.Debug("LdapConnectorPasswordPolicy: The domain functional level predates Fine-Grained Password Policies, so none can exist.");
            return FineGrainedPolicySignal.Absent;
        }

        var containerDn = $"{PasswordSettingsContainerRdn},{domainRootDn}";
        var request = new SearchRequest(containerDn, $"(objectClass={PasswordSettingsObjectClass})", SearchScope.OneLevel, "objectClass");

        try
        {
            var response = (SearchResponse)await _executor.SendRequestAsync(request);
            if (response.Entries.Count > 0)
                return FineGrainedPolicySignal.Present;

            _logger.Debug("LdapConnectorPasswordPolicy: No Fine-Grained Password Policies were returned from the Password Settings Container in '{Domain}'. This cannot be told apart from having no rights over it, so the result is undetermined.",
                LogSanitiser.Sanitise(domainRootDn));
            return FineGrainedPolicySignal.CouldNotDetermine;
        }
        catch (DirectoryOperationException ex)
        {
            // Including noSuchObject. An inaccessible object and an absent one are reported the same way, so this
            // is not evidence that the container does not exist.
            _logger.Debug("LdapConnectorPasswordPolicy: Could not determine whether Fine-Grained Password Policies exist in the Password Settings Container in '{Domain}': {Message}",
                LogSanitiser.Sanitise(domainRootDn), LogSanitiser.Sanitise(ex.Message));
            return FineGrainedPolicySignal.CouldNotDetermine;
        }
        catch (LdapException ex)
        {
            _logger.Debug("LdapConnectorPasswordPolicy: Could not determine whether Fine-Grained Password Policies exist in the Password Settings Container in '{Domain}': {Message}",
                LogSanitiser.Sanitise(domainRootDn), LogSanitiser.Sanitise(ex.Message));
            return FineGrainedPolicySignal.CouldNotDetermine;
        }
    }

    /// <summary>
    /// Whether the domain functional level is below the one that introduced Fine-Grained Password Policies, which
    /// is the only way to prove they are absent rather than merely invisible.
    /// </summary>
    private async Task<bool> IsBelowFineGrainedPolicyFunctionalLevelAsync()
    {
        var request = new SearchRequest { Scope = SearchScope.Base };
        request.Attributes.Add(AttributeDomainFunctionality);

        try
        {
            var response = (SearchResponse)await _executor.SendRequestAsync(request);
            if (response.Entries.Count == 0)
                return false;

            var raw = ReadRaw(response.Entries[0], AttributeDomainFunctionality);
            return int.TryParse(raw, out var level) && level < DomainFunctionalityWindows2008;
        }
        catch (DirectoryOperationException)
        {
            return false;
        }
        catch (LdapException)
        {
            return false;
        }
    }

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
    /// Whether the domain requires passwords to meet Active Directory's complexity rule, which is bit 0 of
    /// pwdProperties (DOMAIN_PASSWORD_COMPLEX).
    /// </summary>
    internal static bool IsComplexityRequired(int pwdProperties) =>
        (pwdProperties & DomainPasswordComplex) == DomainPasswordComplex;

    private static int? ReadInt(SearchResultEntry entry, string attributeName)
    {
        var raw = ReadRaw(entry, attributeName);
        return int.TryParse(raw, out var value) ? value : null;
    }

    private static long? ReadLong(SearchResultEntry entry, string attributeName)
    {
        var raw = ReadRaw(entry, attributeName);
        return long.TryParse(raw, out var value) ? value : null;
    }

    private static string? ReadRaw(SearchResultEntry entry, string attributeName)
    {
        var attribute = entry.Attributes[attributeName];
        return attribute == null || attribute.Count == 0 ? null : attribute[0]?.ToString();
    }

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
    #endregion
}
