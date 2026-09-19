// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Staging;
using JIM.Utilities;
using Serilog;
using System.DirectoryServices.Protocols;
using static JIM.Connectors.LDAP.LdapConnectorPasswordPolicy;
namespace JIM.Connectors.LDAP;

/// <summary>
/// Reads the global password policy 389 Directory Server holds on cn=config, and establishes whether subtree or
/// user policies exist that override it.
/// <para>
/// 389 publishes every figure whether or not the switch that makes the server apply it is on: passwordMinLength
/// is there with syntax checking off, passwordInHistory with history off, passwordMaxAge with expiry off. A reader
/// that copied the numbers would report rules the server does not enforce, so each figure is taken only when its
/// switch is on.
/// </para>
/// <para>
/// Two searches for a directory with one user suffix: the base read of cn=config, and one presence probe per
/// user naming context (capped) for a policy container or an entry carrying its own policy. Absence is never
/// provable: the container can live anywhere in the tree and the subentry attribute is operational, so an empty
/// probe is an unknown.
/// </para>
/// </summary>
internal class LdapConnectorPasswordPolicy389
{
    private readonly ILdapOperationExecutor _executor;
    private readonly ILogger _logger;

    internal LdapConnectorPasswordPolicy389(ILdapOperationExecutor executor, ILogger logger)
    {
        _executor = executor;
        _logger = logger;
    }

    internal async Task<ConnectedSystemPasswordPolicy?> GetPasswordPolicyAsync(LdapPasswordPolicyScope scope)
    {
        var configurationDn = scope.ConfigContext ?? ConfigurationDn;
        var configuration = await ReadConfigurationAsync(configurationDn);

        var policy = new ConnectedSystemPasswordPolicy { Discovered = DateTime.UtcNow };

        if (configuration == null)
        {
            policy.DiscoveryOutcome = PasswordPolicyDiscoveryOutcome.ConfigurationNotReadable;
        }
        else
        {
            Map(configuration, policy);
            policy.DiscoveryOutcome = PasswordPolicyDiscoveryOutcome.Read;
        }

        policy.PolicyOverrideSignal = await ProbeForOverridesAsync(scope.NamingContexts);

        _logger.Debug("LdapConnectorPasswordPolicy: Read policy from '{Configuration}'. Outcome={Outcome}, MinimumLength={MinimumLength}, RequiredClasses={RequiredClasses}, FurtherChecks={FurtherChecks}, Overrides={Overrides}",
            LogSanitiser.Sanitise(configurationDn), policy.DiscoveryOutcome, policy.MinimumLength, policy.RequiredCharacterClassCount, policy.FurtherChecksApply, policy.PolicyOverrideSignal);

        return policy;
    }

    /// <summary>
    /// Applies the figures whose switches are on. passwordMinCategories counts the five classes 389 recognises
    /// (upper, lower, digit, special, 8-bit), so it maps straight onto the model's class count; a requirement of
    /// one category is no rule at all.
    /// </summary>
    private static void Map(SearchResultEntry configuration, ConnectedSystemPasswordPolicy policy)
    {
        var syntaxChecking = IsOn(configuration, AttributeCheckSyntax);
        if (syntaxChecking)
        {
            policy.MinimumLength = PositiveOrNull(ReadInt(configuration, AttributeMinimumLength));

            if (ReadInt(configuration, AttributeMinimumCategories) is { } categories)
            {
                policy.ComplexityRequired = categories > 1;
                if (categories > 1)
                {
                    policy.RequiredCharacterClassCount = categories;
                    policy.RecognisedCharacterClasses = FiveRecognisedCharacterClasses;
                }
            }

            policy.FurtherChecksApply = FurtherCheckAttributes.Any(attribute => IsSet(configuration, attribute));
        }

        if (IsOn(configuration, AttributeHistoryEnabled))
            policy.PasswordHistoryLength = PositiveOrNull(ReadInt(configuration, AttributeHistoryDepth));

        if (IsOn(configuration, AttributeExpiryEnabled))
            policy.MaximumPasswordAge = ParseSeconds(ReadLong(configuration, AttributeMaximumAge));

        policy.MinimumPasswordAge = ParseSeconds(ReadLong(configuration, AttributeMinimumAge));
    }

    private static bool IsOn(SearchResultEntry entry, string attributeName) =>
        string.Equals(ReadRaw(entry, attributeName), "on", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether one of the further checks is configured to do anything. 389 returns every one of these with its
    /// default (off, or zero) whether or not an administrator touched it, so presence alone means nothing.
    /// </summary>
    private static bool IsSet(SearchResultEntry entry, string attributeName)
    {
        var raw = ReadRaw(entry, attributeName);
        if (raw == null)
            return false;

        if (string.Equals(raw, "on", StringComparison.OrdinalIgnoreCase))
            return true;

        return int.TryParse(raw, out var value) && value > 0;
    }

    /// <summary>
    /// Reads the password attributes off cn=config. Null when the read was refused, or answered with nothing:
    /// cn=config always exists, so an empty answer is access control filtering it out.
    /// </summary>
    private async Task<SearchResultEntry?> ReadConfigurationAsync(string configurationDn)
    {
        var request = new SearchRequest(configurationDn, "(objectClass=*)", SearchScope.Base, PolicyAttributes);

        try
        {
            var response = (SearchResponse)await _executor.SendRequestAsync(request);
            if (response.Entries.Count > 0)
                return response.Entries[0];

            _logger.Debug("LdapConnectorPasswordPolicy: The read of '{Configuration}' returned nothing, which is how a refusal to read the configuration shows up.",
                LogSanitiser.Sanitise(configurationDn));
            return null;
        }
        catch (DirectoryOperationException ex)
        {
            _logger.Warning("LdapConnectorPasswordPolicy: The directory refused the password policy read on '{Configuration}': {Message}",
                LogSanitiser.Sanitise(configurationDn), LogSanitiser.Sanitise(ex.Message));
            return null;
        }
        catch (LdapException ex)
        {
            _logger.Warning("LdapConnectorPasswordPolicy: Could not read the password policy from '{Configuration}': {Message}",
                LogSanitiser.Sanitise(configurationDn), LogSanitiser.Sanitise(ex.Message));
            return null;
        }
    }

    /// <summary>
    /// Probes each user naming context, up to the cap, for a policy container or an entry carrying its own
    /// policy, stopping at the first that proves presence. Absence is never provable here, so the answer is
    /// otherwise undetermined.
    /// </summary>
    private async Task<PolicyOverrideSignal> ProbeForOverridesAsync(IReadOnlyList<string> namingContexts)
    {
        foreach (var namingContext in namingContexts.Take(MaximumNamingContextsToProbe))
        {
            var request = new SearchRequest(namingContext,
                $"(|(objectClass={ObjectClassPolicyContainer})({AttributePolicySubentry}=*))",
                SearchScope.Subtree, "objectClass");

            var signal = await LdapConnectorPasswordPolicy.ProbeForOverridesAsync(_executor, _logger, request, "subtree or user password policies");
            if (signal == PolicyOverrideSignal.Present)
                return PolicyOverrideSignal.Present;
        }

        return PolicyOverrideSignal.CouldNotDetermine;
    }

    #region constants
    /// <summary>
    /// Where 389 Directory Server holds its global password policy. Used when the rootDSE does not name a
    /// configuration context.
    /// </summary>
    internal const string ConfigurationDn = "cn=config";

    /// <summary>
    /// How many user naming contexts are probed for overriding policies. A bound rather than a sample: a directory
    /// with more suffixes than this is unusual, and every probe is a subtree search.
    /// </summary>
    internal const int MaximumNamingContextsToProbe = 5;

    internal const string ObjectClassPolicyContainer = "nsPwPolicyContainer";
    internal const string AttributePolicySubentry = "pwdpolicysubentry";

    internal const string AttributeCheckSyntax = "passwordCheckSyntax";
    internal const string AttributeMinimumLength = "passwordMinLength";
    internal const string AttributeMinimumCategories = "passwordMinCategories";
    internal const string AttributeHistoryEnabled = "passwordHistory";
    internal const string AttributeHistoryDepth = "passwordInHistory";
    internal const string AttributeExpiryEnabled = "passwordExp";
    internal const string AttributeMaximumAge = "passwordMaxAge";
    internal const string AttributeMinimumAge = "passwordMinAge";

    /// <summary>
    /// The checks 389 applies beyond length and categories when syntax checking is on, none of which JIM can
    /// generate against: dictionary and palindrome checks, repeat and sequence limits, per-class minimums and the
    /// trivial-word check against the entry's own attributes.
    /// </summary>
    internal static readonly string[] FurtherCheckAttributes =
    [
        "passwordDictCheck", "passwordPalindrome", "passwordMaxRepeats", "passwordMaxSequence", "passwordMaxSeqSets",
        "passwordMaxClassChars", "passwordMinDigits", "passwordMinAlphas", "passwordMinUppers", "passwordMinLowers",
        "passwordMinSpecials", "passwordMin8Bit", "passwordMinTokenLength"
    ];

    private static readonly string[] PolicyAttributes =
    [
        AttributeCheckSyntax, AttributeMinimumLength, AttributeMinimumCategories,
        AttributeHistoryEnabled, AttributeHistoryDepth, AttributeExpiryEnabled, AttributeMaximumAge, AttributeMinimumAge,
        .. FurtherCheckAttributes
    ];
    #endregion
}
