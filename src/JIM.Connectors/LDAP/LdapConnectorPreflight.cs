// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;
using JIM.Connectors.Resources;
using JIM.Models.Localisation;
using JIM.Models.Staging;
using JIM.Utilities;
using Serilog;
using System.DirectoryServices.Protocols;
namespace JIM.Connectors.LDAP;

/// <summary>
/// Establishes whether the password channel to a directory is likely to work, without setting a password on
/// anything.
/// <para>
/// Every check here is a read. That is a deliberate ceiling rather than a limitation of effort: proving a password
/// set works means really setting one, and every route to doing that against a directory JIM did not create is a
/// password reset against somebody's account. The checks instead cover what surrounds the password, which is where
/// most failures actually are.
/// </para>
/// <para>
/// The connection itself is established by the caller, because a failure to connect is the first finding rather
/// than something this class can report on.
/// </para>
/// </summary>
internal class LdapConnectorPreflight
{
    private readonly ILdapOperationExecutor _executor;
    private readonly ILogger _logger;
    private readonly LdapDirectoryType _directoryType;
    private readonly bool _supportsPasswordModifyExtension;
    private readonly bool _isConnectionEncrypted;

    internal LdapConnectorPreflight(
        ILdapOperationExecutor executor,
        ILogger logger,
        LdapDirectoryType directoryType,
        bool supportsPasswordModifyExtension,
        bool isConnectionEncrypted)
    {
        _executor = executor;
        _logger = logger;
        _directoryType = directoryType;
        _supportsPasswordModifyExtension = supportsPasswordModifyExtension;
        _isConnectionEncrypted = isConnectionEncrypted;
    }

    private bool IsActiveDirectory =>
        _directoryType is LdapDirectoryType.ActiveDirectory or LdapDirectoryType.SambaAD;

    /// <summary>
    /// Runs every check that can be answered once a connection is open.
    /// </summary>
    /// <param name="containerExternalIds">
    /// The Distinguished Names of the containers this Connected System manages. Rights are checked in these,
    /// because a directory grants them per part of the tree and checking elsewhere answers a question nobody asked.
    /// </param>
    /// <param name="scope">Where the directory keeps its data and configuration, from the rootDSE.</param>
    internal async Task<List<PasswordPreflightCheckResult>> RunAsync(
        IReadOnlyList<string> containerExternalIds,
        LdapPasswordPolicyScope scope,
        CancellationToken cancellationToken)
    {
        var checks = new List<PasswordPreflightCheckResult>
        {
            CheckEncryption(),
            CheckPasswordMechanism()
        };

        cancellationToken.ThrowIfCancellationRequested();
        checks.Add(await CheckResetRightsAsync(containerExternalIds, cancellationToken));

        cancellationToken.ThrowIfCancellationRequested();
        checks.Add(await CheckPolicyDiscoveryAsync(scope));

        return checks;
    }

    /// <summary>
    /// Whether the channel carrying the password is encrypted.
    /// <para>
    /// A warning rather than a failure, because JIM allows an unencrypted password channel: some directories
    /// genuinely cannot serve TLS, and locking those deployments out of password management entirely helps nobody.
    /// The administrator gets to make that call, having been told plainly what it costs.
    /// </para>
    /// </summary>
    private PasswordPreflightCheckResult CheckEncryption()
    {
        if (_isConnectionEncrypted)
            return PasswordPreflightCheckResult.Passed(PasswordPreflightCheck.Encryption, Pf(nameof(ConnectorMessages.PfEncrypted)));

        var details = new List<JimMessage>
        {
            Pf(nameof(ConnectorMessages.PfEnableSecureConnection))
        };

        // Active Directory refuses the write itself rather than accepting it insecurely, so for those targets this
        // is very likely to be the thing that stops a password set outright. It is still not reported as a failure:
        // a signed and sealed bind satisfies Active Directory without TLS, and JIM cannot tell from here whether
        // one is in use.
        if (IsActiveDirectory)
            details.Add(Pf(nameof(ConnectorMessages.PfAdRefusesUnencrypted)));

        return PasswordPreflightCheckResult.Warning(PasswordPreflightCheck.Encryption, Pf(nameof(ConnectorMessages.PfNotEncrypted)), details);
    }

    /// <summary>
    /// Whether the mechanism JIM would use to set a password is available on this target.
    /// </summary>
    private PasswordPreflightCheckResult CheckPasswordMechanism()
    {
        if (IsActiveDirectory)
            return PasswordPreflightCheckResult.Passed(PasswordPreflightCheck.PasswordMechanism,
                Pf(nameof(ConnectorMessages.PfMechanismAd), LdapConnectorPassword.AttributeUnicodePwd));

        if (_supportsPasswordModifyExtension)
            return PasswordPreflightCheckResult.Passed(PasswordPreflightCheck.PasswordMechanism, Pf(nameof(ConnectorMessages.PfMechanismPasswordModify)));

        // Writing userPassword directly is the obvious alternative and JIM will not do it: a directory applies its
        // configured hashing to the extended operation, but stores a directly written attribute exactly as it is
        // given. That would put cleartext passwords into the directory, so an unsupported target is refused.
        return PasswordPreflightCheckResult.Failed(PasswordPreflightCheck.PasswordMechanism,
            Pf(nameof(ConnectorMessages.PfMechanismUnsupported)),
            [
                Pf(nameof(ConnectorMessages.PfMechanismLooksFor), LdapConnectorPassword.PasswordModifyExtensionOid),
                Pf(nameof(ConnectorMessages.PfMechanismNoDirectWrite))
            ]);
    }

    /// <summary>
    /// Whether the account JIM connects as can reset a password on the objects it would provision.
    /// <para>
    /// The obvious mechanism does not work, and its failure mode is bad enough to record here so nobody reaches
    /// for it again. Active Directory publishes allowedAttributesEffective, listing the attributes the calling
    /// account may write on an object; looking for unicodePwd in it seems like exactly the right question and is
    /// wrong. [MS-ADTS] 3.1.1.4.5.7 computes that list purely from a RIGHT_DS_WRITE_PROPERTY check, whereas
    /// [MS-ADTS] 3.1.1.3.1.5.1 grants a reset through the "User-Force-Change-Password" control access right. The
    /// two are disjoint, so an account delegated resets in the normal, least-privileged way would be reported as
    /// having no rights, while a Domain Admin would be reported as having them: it passes for the account someone
    /// tests with and fails for the one they deploy.
    /// </para>
    /// <para>
    /// JIM therefore reads the access control list of a sample object and evaluates it directly. Only Active
    /// Directory exposes what that needs; other directories publish no portable equivalent, so the answer there
    /// is an unknown rather than a guess.
    /// </para>
    /// </summary>
    private async Task<PasswordPreflightCheckResult> CheckResetRightsAsync(IReadOnlyList<string> containerExternalIds, CancellationToken cancellationToken)
    {
        if (!IsActiveDirectory)
            return PasswordPreflightCheckResult.CouldNotDetermine(PasswordPreflightCheck.ResetRights,
                Pf(nameof(ConnectorMessages.PfRightsNotCheckable)),
                [Pf(nameof(ConnectorMessages.PfRightsNotCheckableDetail))]);

        if (containerExternalIds.Count == 0)
            return PasswordPreflightCheckResult.CouldNotDetermine(PasswordPreflightCheck.ResetRights,
                Pf(nameof(ConnectorMessages.PfRightsNoContainers)),
                [Pf(nameof(ConnectorMessages.PfRightsSelectContainers))]);

        // Rights are read per container, and a Connected System can manage a great many. Checking every one of
        // them turns a diagnostic into a long-running directory scan, so the number is bounded and the bound is
        // reported rather than quietly applied.
        var checkedContainers = containerExternalIds.Take(MaximumContainersToCheck).ToList();
        var findings = await new LdapConnectorResetRights(_executor, _logger).CheckAsync(checkedContainers, cancellationToken);

        var details = findings
            .Select(f => Pf(nameof(ConnectorMessages.PfRightsFinding), DescribeOutcome(f.Outcome), f.ContainerDn, f.Detail))
            .ToList();

        if (containerExternalIds.Count > checkedContainers.Count)
            details.Add(Pf(nameof(ConnectorMessages.PfRightsCheckedFirst),
                checkedContainers.Count.ToString(CultureInfo.InvariantCulture), containerExternalIds.Count.ToString(CultureInfo.InvariantCulture)));

        if (findings.Any(f => f.Outcome == ResetRightsOutcome.Denied))
            return PasswordPreflightCheckResult.Failed(PasswordPreflightCheck.ResetRights,
                Pf(nameof(ConnectorMessages.PfRightsDenied)), details);

        if (findings.Any(f => f.Outcome == ResetRightsOutcome.CouldNotDetermine))
            return PasswordPreflightCheckResult.CouldNotDetermine(PasswordPreflightCheck.ResetRights,
                Pf(nameof(ConnectorMessages.PfRightsUnconfirmed)), details);

        return PasswordPreflightCheckResult.Passed(PasswordPreflightCheck.ResetRights,
            Pf(nameof(ConnectorMessages.PfRightsAllowed)), details);
    }

    private static JimMessage DescribeOutcome(ResetRightsOutcome outcome) => outcome switch
    {
        ResetRightsOutcome.Granted => Pf(nameof(ConnectorMessages.PfOutcomeAllowed)),
        ResetRightsOutcome.Denied => Pf(nameof(ConnectorMessages.PfOutcomeDenied)),
        _ => Pf(nameof(ConnectorMessages.PfOutcomeUnknown))
    };

    private static JimMessage Pf(string key, params MessageArgument[] arguments) =>
        JimMessage.Create(ConnectorMessageCatalogues.Connectors, key, arguments);

    /// <summary>
    /// How many managed containers a single check will read. A bound, not a sample: the containers checked are
    /// named in the result so nothing is silently skipped.
    /// </summary>
    internal const int MaximumContainersToCheck = 10;

    /// <summary>
    /// Whether the target's password policy could be read, so that a generator can be pre-filled from it.
    /// <para>
    /// Never a failure. An unreadable policy does not stop a password being set; it means the administrator
    /// configures the generator from what they know rather than from what the target published, and finds out
    /// about a mismatch through a rejection instead of up front.
    /// </para>
    /// <para>
    /// The wording follows the directory: Active Directory has a domain password policy and Fine-Grained Password
    /// Policies; everything else has a directory password policy and some other mechanism for overriding it.
    /// </para>
    /// </summary>
    private async Task<PasswordPreflightCheckResult> CheckPolicyDiscoveryAsync(LdapPasswordPolicyScope scope)
    {
        var policyReader = new LdapConnectorPasswordPolicy(_executor, _logger, _directoryType);
        var policy = await policyReader.GetPasswordPolicyAsync(scope);

        if (policy == null)
            return PasswordPreflightCheckResult.CouldNotDetermine(PasswordPreflightCheck.PolicyDiscovery,
                Pf(nameof(ConnectorMessages.PfPolicyUnreadable), PolicyNoun),
                [PolicyReadRemedy]);

        switch (policy.DiscoveryOutcome)
        {
            case PasswordPolicyDiscoveryOutcome.NotPublished:
                return PasswordPreflightCheckResult.CouldNotDetermine(PasswordPreflightCheck.PolicyDiscovery,
                    Pf(nameof(ConnectorMessages.PfPolicyNotPublished)),
                    NotPublishedDetails());

            case PasswordPolicyDiscoveryOutcome.ConfigurationNotReadable:
                return PasswordPreflightCheckResult.CouldNotDetermine(PasswordPreflightCheck.PolicyDiscovery,
                    Pf(nameof(ConnectorMessages.PfPolicyConfigNotReadable)),
                    [ConfigurationRemedy]);

            case PasswordPolicyDiscoveryOutcome.NoPolicyConfigured:
                return PasswordPreflightCheckResult.Passed(PasswordPreflightCheck.PolicyDiscovery,
                    Pf(nameof(ConnectorMessages.PfPolicyNoneConfigured)),
                    DescribeCaveats(policy));
        }

        if (!policy.HasAnyDiscoveredConstraint)
            return PasswordPreflightCheckResult.CouldNotDetermine(PasswordPreflightCheck.PolicyDiscovery,
                Pf(nameof(ConnectorMessages.PfPolicyUnreadable), PolicyNoun),
                [PolicyReadRemedy]);

        var details = new List<JimMessage>();
        if (policy.MinimumLength is { } minimumLength)
            details.Add(Pf(nameof(ConnectorMessages.PfMinimumLength), minimumLength.ToString(CultureInfo.CurrentCulture)));
        if (policy.ComplexityRequired is { } complexityRequired)
            details.Add(complexityRequired
                ? Pf(nameof(ConnectorMessages.PfComplexityRequired), $"{policy.RequiredCharacterClassCount}")
                : Pf(nameof(ConnectorMessages.PfComplexityNotRequired)));
        details.AddRange(DescribeCaveats(policy));

        return PasswordPreflightCheckResult.Passed(PasswordPreflightCheck.PolicyDiscovery,
            Pf(nameof(ConnectorMessages.PfPolicyRead), PolicyNoun),
            details);
    }

    private JimMessage PolicyNoun => Pf(IsActiveDirectory ? nameof(ConnectorMessages.PfPolicyNounDomain) : nameof(ConnectorMessages.PfPolicyNounDirectory));

    private JimMessage PolicyReadRemedy => Pf(_directoryType switch
    {
        LdapDirectoryType.ActiveDirectory or LdapDirectoryType.SambaAD => nameof(ConnectorMessages.PfPolicyRemedyActiveDirectory),
        LdapDirectoryType.OpenLDAP => nameof(ConnectorMessages.PfPolicyRemedyOpenLdap),
        LdapDirectoryType.DirectoryServer389 => nameof(ConnectorMessages.PfPolicyRemedyDirectoryServer389),
        _ => nameof(ConnectorMessages.PfPolicyRemedyOther)
    });

    private JimMessage ConfigurationRemedy => Pf(_directoryType switch
    {
        LdapDirectoryType.OpenLDAP => nameof(ConnectorMessages.PfConfigRemedyOpenLdap),
        LdapDirectoryType.DirectoryServer389 => nameof(ConnectorMessages.PfConfigRemedyDirectoryServer389),
        _ => nameof(ConnectorMessages.PfConfigRemedyOther)
    });

    private List<JimMessage> NotPublishedDetails()
    {
        var details = new List<JimMessage>();
        if (_directoryType == LdapDirectoryType.OpenLDAP)
            details.Add(Pf(nameof(ConnectorMessages.PfPpolicyNotLoaded)));
        details.Add(Pf(nameof(ConnectorMessages.PfSetByHand)));
        return details;
    }

    /// <summary>
    /// The caveats worth surfacing here as well as on the policy panel: a preflight is what an administrator runs
    /// when they want to know whether this will work, and "the policy JIM read may not be the policy that
    /// applies" is exactly the caveat that belongs in that answer.
    /// </summary>
    private List<JimMessage> DescribeCaveats(ConnectedSystemPasswordPolicy policy)
    {
        var details = new List<JimMessage>();

        if (policy.PolicyOverrideSignal == PolicyOverrideSignal.Present)
            details.Add(Pf(IsActiveDirectory ? nameof(ConnectorMessages.PfFgppPresent) : nameof(ConnectorMessages.PfOverridePresent)));
        else if (policy.PolicyOverrideSignal == PolicyOverrideSignal.CouldNotDetermine)
            details.Add(Pf(IsActiveDirectory ? nameof(ConnectorMessages.PfFgppUnknown) : nameof(ConnectorMessages.PfOverrideUnknown)));

        if (policy.FurtherChecksApply)
            details.Add(Pf(nameof(ConnectorMessages.PfFurtherChecks)));

        return details;
    }
}
