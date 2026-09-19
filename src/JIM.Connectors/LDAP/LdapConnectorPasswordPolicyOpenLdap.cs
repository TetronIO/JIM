// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Staging;
using JIM.Utilities;
using Serilog;
using System.DirectoryServices.Protocols;
using static JIM.Connectors.LDAP.LdapConnectorPasswordPolicy;
namespace JIM.Connectors.LDAP;

/// <summary>
/// Reads the password policy OpenLDAP's ppolicy overlay applies by default, and establishes whether some entries
/// are governed by a policy other than that one.
/// <para>
/// The overlay names its default policy per database in the server configuration (olcPPolicyDefault on the
/// overlay entry under cn=config), and the policy itself is an ordinary pwdPolicy entry in the data tree. A
/// least-privileged service account can often read the second and not the first, so the reader copes with either
/// being refused: with one policy entry there is no doubt which the default is; with several and no configuration
/// to say which applies, it does not guess.
/// </para>
/// <para>
/// Three searches at most: the overlay configuration, the policy entries under the user naming context, and a
/// presence probe for entries carrying their own pwdPolicySubentry. Each degrades to a recorded outcome rather
/// than an exception, because a policy that cannot be read must not fail the schema import that asked for it.
/// </para>
/// </summary>
internal class LdapConnectorPasswordPolicyOpenLdap
{
    private readonly ILdapOperationExecutor _executor;
    private readonly ILogger _logger;

    internal LdapConnectorPasswordPolicyOpenLdap(ILdapOperationExecutor executor, ILogger logger)
    {
        _executor = executor;
        _logger = logger;
    }

    internal async Task<ConnectedSystemPasswordPolicy?> GetPasswordPolicyAsync(LdapPasswordPolicyScope scope)
    {
        if (!scope.AdvertisesPasswordPolicyControl)
        {
            // The one case where absence can be proved: without the overlay loaded, there is no default policy
            // and nothing that could override it.
            _logger.Debug("LdapConnectorPasswordPolicy: The directory does not advertise the password policy control, so the ppolicy overlay is not loaded and no policy is published.");
            return NotPublished();
        }

        var userContext = scope.PrimaryNamingContext;
        if (userContext == null)
        {
            _logger.Warning("LdapConnectorPasswordPolicy: The directory published no naming context that could hold users, so the password policy could not be looked for.");
            return null;
        }

        var configuration = await ReadOverlayConfigurationAsync(scope.ConfigContext);
        var policyEntries = await ReadPolicyEntriesAsync(userContext);
        var probe = await ProbeForOverridesAsync(_executor, _logger,
            new SearchRequest(userContext, $"({AttributePolicySubentry}=*)", SearchScope.Subtree, "objectClass"),
            "entries carrying their own policy");

        var policy = new ConnectedSystemPasswordPolicy
        {
            Discovered = DateTime.UtcNow,
            PolicyOverrideSignal = probe
        };

        // Several policy entries mean some accounts are pointed at one that is not the default, and two databases
        // with different defaults mean the same, whether or not the probe could see the pointers.
        if (policyEntries is { Count: > 1 } || configuration?.HasDifferingDefaults == true)
            policy.PolicyOverrideSignal = PolicyOverrideSignal.Present;

        if (policyEntries == null)
        {
            policy.DiscoveryOutcome = PasswordPolicyDiscoveryOutcome.ConfigurationNotReadable;
            return policy;
        }

        if (configuration == null)
            ApplyWithoutConfiguration(policy, policyEntries, userContext);
        else
            ApplyWithConfiguration(policy, policyEntries, configuration, userContext);

        _logger.Debug("LdapConnectorPasswordPolicy: Read policy under '{UserContext}'. Outcome={Outcome}, MinimumLength={MinimumLength}, FurtherChecks={FurtherChecks}, Overrides={Overrides}",
            LogSanitiser.Sanitise(userContext), policy.DiscoveryOutcome, policy.MinimumLength, policy.FurtherChecksApply, policy.PolicyOverrideSignal);

        return policy;
    }

    /// <summary>
    /// The configuration said which entry is the default, so that is the one read, and a named check module on
    /// either the overlay or the entry is what marks further checks.
    /// </summary>
    private void ApplyWithConfiguration(ConnectedSystemPasswordPolicy policy, List<SearchResultEntry> policyEntries, OverlayConfiguration configuration, string userContext)
    {
        var overlay = configuration.OverlayFor(userContext);
        if (overlay?.DefaultPolicyDn == null)
        {
            _logger.Debug("LdapConnectorPasswordPolicy: The ppolicy overlay on the database holding '{UserContext}' names no default policy, so no rules apply by default.",
                LogSanitiser.Sanitise(userContext));
            policy.DiscoveryOutcome = PasswordPolicyDiscoveryOutcome.NoPolicyConfigured;
            return;
        }

        var defaultEntry = policyEntries.FirstOrDefault(entry => DistinguishedNamesMatch(entry.DistinguishedName, overlay.DefaultPolicyDn));
        if (defaultEntry == null)
        {
            // The configuration names a default the account could not then read. Whether the entry is hidden by
            // access control or does not exist cannot be told from here, and reading the second as "no rules"
            // would be the confident wrong answer, so the account's rights are what gets reported.
            _logger.Debug("LdapConnectorPasswordPolicy: The default policy '{DefaultPolicy}' named by the ppolicy overlay was not among the readable policy entries under '{UserContext}'.",
                LogSanitiser.Sanitise(overlay.DefaultPolicyDn), LogSanitiser.Sanitise(userContext));
            policy.DiscoveryOutcome = PasswordPolicyDiscoveryOutcome.ConfigurationNotReadable;
            return;
        }

        Map(defaultEntry, policy);
        policy.FurtherChecksApply = IsQualityCheckingOn(defaultEntry) &&
            (ReadRaw(defaultEntry, AttributeCheckModule) != null || overlay.CheckModule != null);
        policy.DiscoveryOutcome = PasswordPolicyDiscoveryOutcome.Read;
    }

    /// <summary>
    /// Without the configuration, which entry is the default is known only when there is exactly one. The
    /// further-checks rule falls back to the coarser one: quality checking on may mean a module JIM cannot see.
    /// </summary>
    private void ApplyWithoutConfiguration(ConnectedSystemPasswordPolicy policy, List<SearchResultEntry> policyEntries, string userContext)
    {
        switch (policyEntries.Count)
        {
            case 0:
                _logger.Debug("LdapConnectorPasswordPolicy: No policy entry exists under '{UserContext}', so no rules apply by default.",
                    LogSanitiser.Sanitise(userContext));
                policy.DiscoveryOutcome = PasswordPolicyDiscoveryOutcome.NoPolicyConfigured;
                return;

            case 1:
                Map(policyEntries[0], policy);
                policy.FurtherChecksApply = IsQualityCheckingOn(policyEntries[0]);
                policy.DiscoveryOutcome = PasswordPolicyDiscoveryOutcome.Read;
                return;

            default:
                _logger.Debug("LdapConnectorPasswordPolicy: {Count} policy entries exist under '{UserContext}' and the overlay configuration naming the default could not be read, so which applies by default cannot be known.",
                    policyEntries.Count, LogSanitiser.Sanitise(userContext));
                policy.DiscoveryOutcome = PasswordPolicyDiscoveryOutcome.ConfigurationNotReadable;
                return;
        }
    }

    /// <summary>
    /// The overlay treats zero as "no rule" for each of these, so zero and absent both read as null. Complexity
    /// is not something the overlay expresses: quality checking gates the length rule and hands anything further
    /// to a module, so the character class fields stay unset.
    /// </summary>
    private static void Map(SearchResultEntry entry, ConnectedSystemPasswordPolicy policy)
    {
        policy.MinimumLength = PositiveOrNull(ReadInt(entry, AttributeMinimumLength));
        policy.PasswordHistoryLength = PositiveOrNull(ReadInt(entry, AttributeHistoryDepth));
        policy.MaximumPasswordAge = ParseSeconds(ReadLong(entry, AttributeMaximumAge));
        policy.MinimumPasswordAge = ParseSeconds(ReadLong(entry, AttributeMinimumAge));
    }

    /// <summary>
    /// pwdCheckQuality 1 or 2: the overlay checks the password's quality, refusing it on failure (2) or only
    /// where it can read it (1). At 0 no check runs and any module named is inert.
    /// </summary>
    private static bool IsQualityCheckingOn(SearchResultEntry entry) =>
        ReadInt(entry, AttributeCheckQuality) is 1 or 2;

    /// <summary>
    /// Reads the databases and ppolicy overlays under cn=config, so the default policy per database is known.
    /// Null when the configuration was refused, including the silent refusal of an empty result: a cn=config
    /// tree that answers a search with nothing is one the account has no rights over, since it always holds at
    /// least the databases.
    /// </summary>
    private async Task<OverlayConfiguration?> ReadOverlayConfigurationAsync(string? configContext)
    {
        if (configContext == null)
        {
            _logger.Debug("LdapConnectorPasswordPolicy: The directory publishes no configuration context, so the ppolicy overlay configuration cannot be read.");
            return null;
        }

        var request = new SearchRequest(configContext,
            $"(|(objectClass={ObjectClassOverlayConfig})(objectClass={ObjectClassDatabaseConfig}))",
            SearchScope.Subtree, AttributeOlcPPolicyDefault, AttributeOlcPPolicyCheckModule, AttributeOlcSuffix)
        {
            SizeLimit = MaximumConfigurationEntries
        };

        try
        {
            var response = (SearchResponse)await _executor.SendRequestAsync(request);
            if (response.Entries.Count > 0)
                return OverlayConfiguration.From(response.Entries);

            _logger.Debug("LdapConnectorPasswordPolicy: The search of '{ConfigContext}' returned nothing, which is how a refusal to read the configuration shows up.",
                LogSanitiser.Sanitise(configContext));
            return null;
        }
        catch (DirectoryOperationException ex)
        {
            _logger.Debug("LdapConnectorPasswordPolicy: The directory refused the read of the ppolicy overlay configuration under '{ConfigContext}': {Message}",
                LogSanitiser.Sanitise(configContext), LogSanitiser.Sanitise(ex.Message));
            return null;
        }
        catch (LdapException ex)
        {
            _logger.Debug("LdapConnectorPasswordPolicy: Could not read the ppolicy overlay configuration under '{ConfigContext}': {Message}",
                LogSanitiser.Sanitise(configContext), LogSanitiser.Sanitise(ex.Message));
            return null;
        }
    }

    /// <summary>
    /// Reads every pwdPolicy entry under the user naming context: the default among them, and how many there are.
    /// Null when the search was refused. A search that overran its size limit still yields what came back, since
    /// the count is already more than enough to say overrides exist.
    /// </summary>
    private async Task<List<SearchResultEntry>?> ReadPolicyEntriesAsync(string userContext)
    {
        var request = new SearchRequest(userContext, $"(objectClass={ObjectClassPolicy})", SearchScope.Subtree,
            AttributeMinimumLength, AttributeHistoryDepth, AttributeMaximumAge, AttributeMinimumAge, AttributeCheckQuality, AttributeCheckModule)
        {
            SizeLimit = MaximumPolicyEntries
        };

        try
        {
            var response = (SearchResponse)await _executor.SendRequestAsync(request);
            return response.Entries.Cast<SearchResultEntry>().ToList();
        }
        catch (DirectoryOperationException ex) when (ex.Response is SearchResponse { ResultCode: ResultCode.SizeLimitExceeded } partial)
        {
            return partial.Entries.Cast<SearchResultEntry>().ToList();
        }
        catch (DirectoryOperationException ex)
        {
            _logger.Warning("LdapConnectorPasswordPolicy: The directory refused the search for policy entries under '{UserContext}': {Message}",
                LogSanitiser.Sanitise(userContext), LogSanitiser.Sanitise(ex.Message));
            return null;
        }
        catch (LdapException ex)
        {
            _logger.Warning("LdapConnectorPasswordPolicy: Could not search for policy entries under '{UserContext}': {Message}",
                LogSanitiser.Sanitise(userContext), LogSanitiser.Sanitise(ex.Message));
            return null;
        }
    }

    /// <summary>
    /// The ppolicy overlays under cn=config, each correlated with the database it sits under by DN.
    /// </summary>
    private sealed class OverlayConfiguration
    {
        private readonly List<(string DatabaseDn, string Suffix)> _databases = [];
        private readonly List<Overlay> _overlays = [];

        internal static OverlayConfiguration From(SearchResultEntryCollection entries)
        {
            var configuration = new OverlayConfiguration();
            foreach (SearchResultEntry entry in entries)
            {
                var suffix = ReadRaw(entry, AttributeOlcSuffix);
                if (suffix != null)
                {
                    configuration._databases.Add((entry.DistinguishedName, suffix));
                    continue;
                }

                // An overlay entry sits directly under its database: olcOverlay={n}ppolicy,olcDatabase={n}mdb,cn=config.
                if (entry.DistinguishedName.StartsWith($"{AttributeOlcOverlay}=", StringComparison.OrdinalIgnoreCase))
                    configuration._overlays.Add(new Overlay(
                        ParentDn(entry.DistinguishedName),
                        ReadRaw(entry, AttributeOlcPPolicyDefault),
                        ReadRaw(entry, AttributeOlcPPolicyCheckModule)));
            }

            return configuration;
        }

        /// <summary>
        /// The overlay on the database whose suffix is the given naming context, or failing a match by suffix,
        /// the first overlay there is.
        /// </summary>
        internal Overlay? OverlayFor(string namingContext)
        {
            var database = _databases.FirstOrDefault(d => DistinguishedNamesMatch(d.Suffix, namingContext));
            if (database.DatabaseDn != null)
                return _overlays.FirstOrDefault(o => DistinguishedNamesMatch(o.DatabaseDn, database.DatabaseDn));

            return _overlays.FirstOrDefault();
        }

        /// <summary>
        /// Whether two databases name different default policies, in which case accounts in one are governed by a
        /// policy other than the one read from the other.
        /// </summary>
        internal bool HasDifferingDefaults =>
            _overlays.Where(o => o.DefaultPolicyDn != null)
                .Select(o => o.DefaultPolicyDn!)
                .Distinct(DnComparer.Instance)
                .Count() > 1;

        private static string ParentDn(string dn)
        {
            var separator = dn.IndexOf(',');
            return separator < 0 ? string.Empty : dn[(separator + 1)..].Trim();
        }
    }

    private sealed record Overlay(string DatabaseDn, string? DefaultPolicyDn, string? CheckModule);

    private sealed class DnComparer : IEqualityComparer<string>
    {
        internal static readonly DnComparer Instance = new();
        public bool Equals(string? x, string? y) => DistinguishedNamesMatch(x, y);
        public int GetHashCode(string obj) => string.Join(",", obj.Split(',').Select(c => c.Trim())).ToUpperInvariant().GetHashCode();
    }

    #region constants
    internal const string ObjectClassPolicy = "pwdPolicy";
    internal const string ObjectClassOverlayConfig = "olcPPolicyConfig";
    internal const string ObjectClassDatabaseConfig = "olcDatabaseConfig";

    internal const string AttributeMinimumLength = "pwdMinLength";
    internal const string AttributeHistoryDepth = "pwdInHistory";
    internal const string AttributeMaximumAge = "pwdMaxAge";
    internal const string AttributeMinimumAge = "pwdMinAge";
    internal const string AttributeCheckQuality = "pwdCheckQuality";
    internal const string AttributeCheckModule = "pwdCheckModule";
    internal const string AttributePolicySubentry = "pwdPolicySubentry";

    internal const string AttributeOlcPPolicyDefault = "olcPPolicyDefault";
    internal const string AttributeOlcPPolicyCheckModule = "olcPPolicyCheckModule";
    internal const string AttributeOlcSuffix = "olcSuffix";
    internal const string AttributeOlcOverlay = "olcOverlay";

    /// <summary>
    /// A bound on the policy entry search. Well above anything a real directory holds, since policies are
    /// configured by hand and applied by reference; past it the count already says all it needs to.
    /// </summary>
    internal const int MaximumPolicyEntries = 100;

    /// <summary>
    /// A bound on the configuration search, which returns one entry per database plus one per ppolicy overlay.
    /// </summary>
    internal const int MaximumConfigurationEntries = 100;
    #endregion
}
