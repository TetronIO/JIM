// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Staging;
using JIM.Utilities;
using Serilog;
using System.DirectoryServices.Protocols;
using static JIM.Connectors.LDAP.LdapConnectorPasswordPolicy;
namespace JIM.Connectors.LDAP;

/// <summary>
/// Reads the domain-wide password policy Active Directory and Samba AD publish on the domain root, and establishes
/// whether Fine-Grained Password Policies exist that override it for some accounts.
/// </summary>
internal class LdapConnectorPasswordPolicyActiveDirectory
{
    private readonly ILdapOperationExecutor _executor;
    private readonly ILogger _logger;

    internal LdapConnectorPasswordPolicyActiveDirectory(ILdapOperationExecutor executor, ILogger logger)
    {
        _executor = executor;
        _logger = logger;
    }

    /// <summary>
    /// Reads the policy from the domain root, and separately establishes whether policies exist that override it
    /// for some accounts.
    /// </summary>
    /// <param name="domainRootDn">The domain naming context, from the rootDSE.</param>
    internal async Task<ConnectedSystemPasswordPolicy?> GetPasswordPolicyAsync(string? domainRootDn)
    {
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
            DiscoveryOutcome = PasswordPolicyDiscoveryOutcome.Read,
            MinimumLength = ReadInt(entry, AttributeMinPwdLength),
            ComplexityRequired = complexityRequired,
            PasswordHistoryLength = ReadInt(entry, AttributePwdHistoryLength),
            MaximumPasswordAge = ParseInterval(ReadLong(entry, AttributeMaxPwdAge)),
            MinimumPasswordAge = ParseInterval(ReadLong(entry, AttributeMinPwdAge)),
            PolicyOverrideSignal = await DetectOverridingPoliciesAsync(domainRootDn)
        };

        // Active Directory's complexity rule is fixed rather than configurable: when the flag is on, a password
        // must draw on three of five character categories. Recording it explicitly keeps the model free of
        // Active Directory assumptions and lets the generator validate against it without special-casing.
        if (complexityRequired == true)
        {
            policy.RequiredCharacterClassCount = ActiveDirectoryRequiredCharacterClasses;
            policy.RecognisedCharacterClasses = FiveRecognisedCharacterClasses;
        }

        _logger.Debug("LdapConnectorPasswordPolicy: Read policy from '{DomainRoot}'. MinimumLength={MinimumLength}, ComplexityRequired={Complexity}, Overrides={Overrides}",
            LogSanitiser.Sanitise(domainRootDn), policy.MinimumLength, policy.ComplexityRequired, policy.PolicyOverrideSignal);

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
    private async Task<PolicyOverrideSignal> DetectOverridingPoliciesAsync(string domainRootDn)
    {
        if (await IsBelowFineGrainedPolicyFunctionalLevelAsync())
        {
            _logger.Debug("LdapConnectorPasswordPolicy: The domain functional level predates Fine-Grained Password Policies, so none can exist.");
            return PolicyOverrideSignal.Absent;
        }

        var containerDn = $"{PasswordSettingsContainerRdn},{domainRootDn}";
        var request = new SearchRequest(containerDn, $"(objectClass={PasswordSettingsObjectClass})", SearchScope.OneLevel, "objectClass");

        try
        {
            var response = (SearchResponse)await _executor.SendRequestAsync(request);
            if (response.Entries.Count > 0)
                return PolicyOverrideSignal.Present;

            _logger.Debug("LdapConnectorPasswordPolicy: No Fine-Grained Password Policies were returned from the Password Settings Container in '{Domain}'. This cannot be told apart from having no rights over it, so the result is undetermined.",
                LogSanitiser.Sanitise(domainRootDn));
            return PolicyOverrideSignal.CouldNotDetermine;
        }
        catch (DirectoryOperationException ex)
        {
            // Including noSuchObject. An inaccessible object and an absent one are reported the same way, so this
            // is not evidence that the container does not exist.
            _logger.Debug("LdapConnectorPasswordPolicy: Could not determine whether Fine-Grained Password Policies exist in the Password Settings Container in '{Domain}': {Message}",
                LogSanitiser.Sanitise(domainRootDn), LogSanitiser.Sanitise(ex.Message));
            return PolicyOverrideSignal.CouldNotDetermine;
        }
        catch (LdapException ex)
        {
            _logger.Debug("LdapConnectorPasswordPolicy: Could not determine whether Fine-Grained Password Policies exist in the Password Settings Container in '{Domain}': {Message}",
                LogSanitiser.Sanitise(domainRootDn), LogSanitiser.Sanitise(ex.Message));
            return PolicyOverrideSignal.CouldNotDetermine;
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
}
