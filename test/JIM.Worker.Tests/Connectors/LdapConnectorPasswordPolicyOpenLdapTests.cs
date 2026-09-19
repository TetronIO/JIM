// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Connectors.LDAP;
using JIM.Models.Staging;
using Moq;
using NUnit.Framework;
using Serilog;
using System.DirectoryServices.Protocols;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// Covers discovery of the password policy OpenLDAP's ppolicy overlay enforces.
/// <para>
/// The overlay names its default policy in the server configuration under cn=config, which a least-privileged
/// service account frequently cannot read, and marks per-entry overrides with an operational attribute that access
/// control can hide. What these tests protect is that every one of those refusals degrades to a stated outcome
/// rather than a confident wrong answer, and that the one case where absence can be proved (the overlay is not
/// loaded) is the only one reported as absence.
/// </para>
/// </summary>
[TestFixture]
public class LdapConnectorPasswordPolicyOpenLdapTests
{
    private const string UserContext = "dc=yellowstone,dc=local";
    private const string ConfigContext = "cn=config";
    private const string DefaultPolicyDn = "cn=default,ou=Policies,dc=yellowstone,dc=local";
    private const string OverlayDn = "olcOverlay={0}ppolicy,olcDatabase={1}mdb,cn=config";

    private Mock<ILdapOperationExecutor> _executor = null!;

    [SetUp]
    public void SetUp() => _executor = new Mock<ILdapOperationExecutor>();

    #region mapping

    /// <summary>
    /// PRD Scenario 1: pwdMinLength 12, pwdInHistory 5, pwdMaxAge 7776000 seconds (90 days), and a pwdMinAge of
    /// zero meaning "can be changed straight away".
    /// </summary>
    [Test]
    public async Task GetPasswordPolicyAsync_WithTheDefaultPolicyReadable_MapsEveryPublishedValueAsync()
    {
        SetupDirectory(
            config: ConfigNamingTheDefault(),
            policies: LdapTestResponses.SearchResponseWith(DefaultPolicyDn,
                ("pwdMinLength", "12"),
                ("pwdInHistory", "5"),
                ("pwdMaxAge", "7776000"),
                ("pwdMinAge", "0"),
                ("pwdCheckQuality", "2")),
            probe: LdapTestResponses.EmptySearchResponse());

        var policy = await CreateReader().GetPasswordPolicyAsync(Scope());

        Assert.That(policy, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(policy!.DiscoveryOutcome, Is.EqualTo(PasswordPolicyDiscoveryOutcome.Read));
            Assert.That(policy.MinimumLength, Is.EqualTo(12));
            Assert.That(policy.PasswordHistoryLength, Is.EqualTo(5));
            Assert.That(policy.MaximumPasswordAge, Is.EqualTo(TimeSpan.FromDays(90)));
            Assert.That(policy.MinimumPasswordAge, Is.Null);
            Assert.That(policy.ComplexityRequired, Is.Null);
            Assert.That(policy.RequiredCharacterClassCount, Is.Null);
            Assert.That(policy.RecognisedCharacterClasses, Is.EqualTo(PasswordCharacterClasses.None));
            Assert.That(policy.FurtherChecksApply, Is.False);
            Assert.That(policy.HasAnyDiscoveredConstraint, Is.True);
        }
    }

    /// <summary>
    /// The overlay treats zero as "no rule" for every one of these, so zero and absent must read the same.
    /// </summary>
    [Test]
    public async Task GetPasswordPolicyAsync_WithZeroAndAbsentValues_LeavesThemNullAsync()
    {
        SetupDirectory(
            config: ConfigNamingTheDefault(),
            policies: LdapTestResponses.SearchResponseWith(DefaultPolicyDn,
                ("pwdMinLength", "0"),
                ("pwdInHistory", "0"),
                ("pwdMaxAge", "0")),
            probe: LdapTestResponses.EmptySearchResponse());

        var policy = await CreateReader().GetPasswordPolicyAsync(Scope());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(policy!.MinimumLength, Is.Null);
            Assert.That(policy.PasswordHistoryLength, Is.Null);
            Assert.That(policy.MaximumPasswordAge, Is.Null);
            Assert.That(policy.MinimumPasswordAge, Is.Null);
            Assert.That(policy.HasAnyDiscoveredConstraint, Is.False);
            Assert.That(policy.DiscoveryOutcome, Is.EqualTo(PasswordPolicyDiscoveryOutcome.Read));
        }
    }

    #endregion

    #region outcomes

    /// <summary>
    /// The one case where absence can be proved: a directory that does not advertise the ppolicy control has no
    /// overlay loaded, so nothing can override a policy that does not exist. No search is spent on it.
    /// </summary>
    [Test]
    public async Task GetPasswordPolicyAsync_WhenTheControlIsNotAdvertised_ReportsNotPublishedAndAbsentAsync()
    {
        var policy = await CreateReader().GetPasswordPolicyAsync(Scope(advertisesControl: false));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(policy!.DiscoveryOutcome, Is.EqualTo(PasswordPolicyDiscoveryOutcome.NotPublished));
            Assert.That(policy.PolicyOverrideSignal, Is.EqualTo(PolicyOverrideSignal.Absent));
            Assert.That(policy.HasAnyDiscoveredConstraint, Is.False);
        }
        _executor.Verify(e => e.SendRequestAsync(It.IsAny<DirectoryRequest>()), Times.Never);
    }

    /// <summary>
    /// A service account that cannot read cn=config can still read the policy entries, and where there is only
    /// one there is no ambiguity about which the default is.
    /// </summary>
    [Test]
    public async Task GetPasswordPolicyAsync_WhenTheConfigurationIsRefusedAndOnePolicyExists_ReadsItAsync()
    {
        SetupDirectory(
            config: null,
            policies: LdapTestResponses.SearchResponseWith(DefaultPolicyDn, ("pwdMinLength", "12")),
            probe: LdapTestResponses.EmptySearchResponse());

        var policy = await CreateReader().GetPasswordPolicyAsync(Scope());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(policy!.DiscoveryOutcome, Is.EqualTo(PasswordPolicyDiscoveryOutcome.Read));
            Assert.That(policy.MinimumLength, Is.EqualTo(12));
        }
    }

    /// <summary>
    /// Access control filters a cn=config search silently, so an empty result is a refusal, not an empty
    /// configuration.
    /// </summary>
    [Test]
    public async Task GetPasswordPolicyAsync_WhenTheConfigurationReadsEmptyAndOnePolicyExists_ReadsItAsync()
    {
        SetupDirectory(
            config: LdapTestResponses.EmptySearchResponse(),
            policies: LdapTestResponses.SearchResponseWith(DefaultPolicyDn, ("pwdMinLength", "12")),
            probe: LdapTestResponses.EmptySearchResponse());

        var policy = await CreateReader().GetPasswordPolicyAsync(Scope());

        Assert.That(policy!.MinimumLength, Is.EqualTo(12));
    }

    /// <summary>
    /// With several policies and no way to read which one the overlay applies by default, guessing would pre-fill
    /// the generator from a policy that may govern nobody. The answer is that the configuration was not readable,
    /// and that overrides exist.
    /// </summary>
    [Test]
    public async Task GetPasswordPolicyAsync_WhenTheConfigurationIsRefusedAndSeveralPoliciesExist_CannotTellWhichIsTheDefaultAsync()
    {
        SetupDirectory(
            config: null,
            policies: LdapTestResponses.SearchResponseWithEntries(
                LdapTestResponses.Entry(DefaultPolicyDn, ("pwdMinLength", "12")),
                LdapTestResponses.Entry("cn=strict,ou=Policies,dc=yellowstone,dc=local", ("pwdMinLength", "20"))),
            probe: LdapTestResponses.EmptySearchResponse());

        var policy = await CreateReader().GetPasswordPolicyAsync(Scope());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(policy!.DiscoveryOutcome, Is.EqualTo(PasswordPolicyDiscoveryOutcome.ConfigurationNotReadable));
            Assert.That(policy.PolicyOverrideSignal, Is.EqualTo(PolicyOverrideSignal.Present));
            Assert.That(policy.HasAnyDiscoveredConstraint, Is.False);
        }
    }

    /// <summary>
    /// With the configuration unreadable and no policy entry anywhere under the user context, there is nothing the
    /// overlay could be applying by default.
    /// </summary>
    [Test]
    public async Task GetPasswordPolicyAsync_WhenNoPolicyEntryExists_ReportsNoPolicyConfiguredAsync()
    {
        SetupDirectory(
            config: null,
            policies: LdapTestResponses.EmptySearchResponse(),

            probe: LdapTestResponses.EmptySearchResponse());

        var policy = await CreateReader().GetPasswordPolicyAsync(Scope());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(policy!.DiscoveryOutcome, Is.EqualTo(PasswordPolicyDiscoveryOutcome.NoPolicyConfigured));
            Assert.That(policy.HasAnyDiscoveredConstraint, Is.False);
        }
    }

    /// <summary>
    /// The overlay is loaded on the database but names no default, so entries without their own subentry are
    /// governed by nothing. Policy entries that happen to exist elsewhere are not the default.
    /// </summary>
    [Test]
    public async Task GetPasswordPolicyAsync_WhenTheOverlayNamesNoDefault_ReportsNoPolicyConfiguredAsync()
    {
        SetupDirectory(
            config: LdapTestResponses.SearchResponseWithEntries(
                LdapTestResponses.Entry("olcDatabase={1}mdb,cn=config", ("olcSuffix", UserContext)),
                LdapTestResponses.Entry(OverlayDn, ("objectClass", "olcPPolicyConfig"))),
            policies: LdapTestResponses.SearchResponseWith(DefaultPolicyDn, ("pwdMinLength", "12")),
            probe: LdapTestResponses.EmptySearchResponse());

        var policy = await CreateReader().GetPasswordPolicyAsync(Scope());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(policy!.DiscoveryOutcome, Is.EqualTo(PasswordPolicyDiscoveryOutcome.NoPolicyConfigured));
            Assert.That(policy.MinimumLength, Is.Null);
        }
    }

    /// <summary>
    /// The configuration names a default the account cannot then read: the policy is held somewhere the account
    /// has no rights over, which is a rights problem rather than an absent policy.
    /// </summary>
    [Test]
    public async Task GetPasswordPolicyAsync_WhenTheNamedDefaultCannotBeRead_ReportsConfigurationNotReadableAsync()
    {
        SetupDirectory(
            config: ConfigNamingTheDefault(),
            policies: LdapTestResponses.EmptySearchResponse(),
            probe: LdapTestResponses.EmptySearchResponse());

        var policy = await CreateReader().GetPasswordPolicyAsync(Scope());

        Assert.That(policy!.DiscoveryOutcome, Is.EqualTo(PasswordPolicyDiscoveryOutcome.ConfigurationNotReadable));
    }

    [Test]
    public async Task GetPasswordPolicyAsync_WhenThePolicySearchIsRefused_ReportsConfigurationNotReadableRatherThanThrowingAsync()
    {
        _executor.Setup(e => e.SendRequestAsync(It.IsAny<SearchRequest>()))
            .ThrowsAsync(new LdapException((int)ResultCode.InsufficientAccessRights, "insufficient access rights"));

        var policy = await CreateReader().GetPasswordPolicyAsync(Scope());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(policy!.DiscoveryOutcome, Is.EqualTo(PasswordPolicyDiscoveryOutcome.ConfigurationNotReadable));
            Assert.That(policy.PolicyOverrideSignal, Is.EqualTo(PolicyOverrideSignal.CouldNotDetermine));
        }
    }

    [Test]
    public async Task GetPasswordPolicyAsync_WithNoUserNamingContext_ReturnsNullRatherThanThrowingAsync()
    {
        var policy = await CreateReader().GetPasswordPolicyAsync(new LdapPasswordPolicyScope(null, [], ConfigContext, true));

        Assert.That(policy, Is.Null);
    }

    #endregion

    #region overrides

    [Test]
    public async Task GetPasswordPolicyAsync_WhenAnEntryCarriesItsOwnPolicy_ReportsOverridesPresentAsync()
    {
        SetupDirectory(
            config: ConfigNamingTheDefault(),
            policies: LdapTestResponses.SearchResponseWith(DefaultPolicyDn, ("pwdMinLength", "12")),
            probe: LdapTestResponses.SearchResponseWith("uid=ceo,ou=People,dc=yellowstone,dc=local", ("objectClass", "inetOrgPerson")));

        var policy = await CreateReader().GetPasswordPolicyAsync(Scope());

        Assert.That(policy!.PolicyOverrideSignal, Is.EqualTo(PolicyOverrideSignal.Present));
    }

    /// <summary>
    /// A probe with a size limit of one against two or more matching entries comes back as a size limit error
    /// carrying the first entry, which is evidence of presence rather than a failure.
    /// </summary>
    [Test]
    public async Task GetPasswordPolicyAsync_WhenTheProbeExceedsItsSizeLimit_ReportsOverridesPresentAsync()
    {
        var partial = LdapTestResponses.Create<SearchResponse>(ResultCode.SizeLimitExceeded);
        SetupDirectory(
            config: ConfigNamingTheDefault(),
            policies: LdapTestResponses.SearchResponseWith(DefaultPolicyDn, ("pwdMinLength", "12")),
            probe: null,
            probeException: new DirectoryOperationException(partial, "size limit exceeded"));

        var policy = await CreateReader().GetPasswordPolicyAsync(Scope());

        Assert.That(policy!.PolicyOverrideSignal, Is.EqualTo(PolicyOverrideSignal.Present));
    }

    /// <summary>
    /// pwdPolicySubentry is operational and access control can hide it, so an empty probe is not evidence that
    /// nothing carries one. This is PRD decision 2: Scenario 1 shows the softer alert.
    /// </summary>
    [Test]
    public async Task GetPasswordPolicyAsync_WhenTheProbeIsEmpty_ReportsUndeterminedRatherThanAbsentAsync()
    {
        SetupDirectory(
            config: ConfigNamingTheDefault(),
            policies: LdapTestResponses.SearchResponseWith(DefaultPolicyDn, ("pwdMinLength", "12")),
            probe: LdapTestResponses.EmptySearchResponse());

        var policy = await CreateReader().GetPasswordPolicyAsync(Scope());

        Assert.That(policy!.PolicyOverrideSignal, Is.EqualTo(PolicyOverrideSignal.CouldNotDetermine));
    }

    [Test]
    public async Task GetPasswordPolicyAsync_WhenTheProbeIsRefused_ReportsUndeterminedAsync()
    {
        SetupDirectory(
            config: ConfigNamingTheDefault(),
            policies: LdapTestResponses.SearchResponseWith(DefaultPolicyDn, ("pwdMinLength", "12")),
            probe: null,
            probeException: new LdapException((int)ResultCode.InsufficientAccessRights, "insufficient access rights"));

        var policy = await CreateReader().GetPasswordPolicyAsync(Scope());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(policy!.PolicyOverrideSignal, Is.EqualTo(PolicyOverrideSignal.CouldNotDetermine));
            Assert.That(policy.MinimumLength, Is.EqualTo(12), "A refused probe must not lose the policy that was read.");
        }
    }

    /// <summary>
    /// Several policy entries mean some accounts are pointed at one that is not the default, even when the probe
    /// cannot see the pointers.
    /// </summary>
    [Test]
    public async Task GetPasswordPolicyAsync_WithSeveralPolicyEntries_ReportsOverridesPresentAsync()
    {
        SetupDirectory(
            config: ConfigNamingTheDefault(),
            policies: LdapTestResponses.SearchResponseWithEntries(
                LdapTestResponses.Entry(DefaultPolicyDn, ("pwdMinLength", "12")),
                LdapTestResponses.Entry("cn=strict,ou=Policies,dc=yellowstone,dc=local", ("pwdMinLength", "20"))),
            probe: LdapTestResponses.EmptySearchResponse());

        var policy = await CreateReader().GetPasswordPolicyAsync(Scope());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(policy!.PolicyOverrideSignal, Is.EqualTo(PolicyOverrideSignal.Present));
            Assert.That(policy.MinimumLength, Is.EqualTo(12), "The named default is still the one read.");
            Assert.That(policy.DiscoveryOutcome, Is.EqualTo(PasswordPolicyDiscoveryOutcome.Read));
        }
    }

    [Test]
    public async Task GetPasswordPolicyAsync_WithTwoDatabasesNamingDifferentDefaults_ReportsOverridesPresentAsync()
    {
        SetupDirectory(
            config: LdapTestResponses.SearchResponseWithEntries(
                LdapTestResponses.Entry("olcDatabase={1}mdb,cn=config", ("olcSuffix", UserContext)),
                LdapTestResponses.Entry(OverlayDn, ("olcPPolicyDefault", DefaultPolicyDn)),
                LdapTestResponses.Entry("olcDatabase={2}mdb,cn=config", ("olcSuffix", "dc=grandcanyon,dc=local")),
                LdapTestResponses.Entry("olcOverlay={0}ppolicy,olcDatabase={2}mdb,cn=config", ("olcPPolicyDefault", "cn=other,ou=Policies,dc=grandcanyon,dc=local"))),
            policies: LdapTestResponses.SearchResponseWith(DefaultPolicyDn, ("pwdMinLength", "12")),
            probe: LdapTestResponses.EmptySearchResponse());

        var policy = await CreateReader().GetPasswordPolicyAsync(Scope());

        Assert.That(policy!.PolicyOverrideSignal, Is.EqualTo(PolicyOverrideSignal.Present));
    }

    #endregion

    #region further checks

    /// <summary>
    /// The overlay only enforces pwdMinLength when pwdCheckQuality is above zero, so quality alone flags nothing:
    /// it is a named check module that applies rules JIM cannot see.
    /// </summary>
    [Test]
    public async Task GetPasswordPolicyAsync_WithQualityCheckingButNoModule_DoesNotFlagFurtherChecksAsync()
    {
        SetupDirectory(
            config: ConfigNamingTheDefault(),
            policies: LdapTestResponses.SearchResponseWith(DefaultPolicyDn, ("pwdMinLength", "12"), ("pwdCheckQuality", "2")),
            probe: LdapTestResponses.EmptySearchResponse());

        var policy = await CreateReader().GetPasswordPolicyAsync(Scope());

        Assert.That(policy!.FurtherChecksApply, Is.False);
    }

    [Test]
    public async Task GetPasswordPolicyAsync_WithACheckModuleOnThePolicyEntry_FlagsFurtherChecksAsync()
    {
        SetupDirectory(
            config: ConfigNamingTheDefault(),
            policies: LdapTestResponses.SearchResponseWith(DefaultPolicyDn,
                ("pwdMinLength", "12"), ("pwdCheckQuality", "1"), ("pwdCheckModule", "check_password.so")),
            probe: LdapTestResponses.EmptySearchResponse());

        var policy = await CreateReader().GetPasswordPolicyAsync(Scope());

        Assert.That(policy!.FurtherChecksApply, Is.True);
    }

    [Test]
    public async Task GetPasswordPolicyAsync_WithACheckModuleOnTheOverlay_FlagsFurtherChecksAsync()
    {
        SetupDirectory(
            config: LdapTestResponses.SearchResponseWithEntries(
                LdapTestResponses.Entry("olcDatabase={1}mdb,cn=config", ("olcSuffix", UserContext)),
                LdapTestResponses.Entry(OverlayDn, ("olcPPolicyDefault", DefaultPolicyDn), ("olcPPolicyCheckModule", "pqchecker.so"))),
            policies: LdapTestResponses.SearchResponseWith(DefaultPolicyDn, ("pwdMinLength", "12"), ("pwdCheckQuality", "2")),
            probe: LdapTestResponses.EmptySearchResponse());

        var policy = await CreateReader().GetPasswordPolicyAsync(Scope());

        Assert.That(policy!.FurtherChecksApply, Is.True);
    }

    /// <summary>
    /// A module is only ever consulted when quality checking is on, so a module with pwdCheckQuality 0 is inert.
    /// </summary>
    [Test]
    public async Task GetPasswordPolicyAsync_WithACheckModuleButQualityCheckingOff_DoesNotFlagFurtherChecksAsync()
    {
        SetupDirectory(
            config: ConfigNamingTheDefault(),
            policies: LdapTestResponses.SearchResponseWith(DefaultPolicyDn,
                ("pwdMinLength", "12"), ("pwdCheckQuality", "0"), ("pwdCheckModule", "check_password.so")),
            probe: LdapTestResponses.EmptySearchResponse());

        var policy = await CreateReader().GetPasswordPolicyAsync(Scope());

        Assert.That(policy!.FurtherChecksApply, Is.False);
    }

    /// <summary>
    /// With the overlay configuration unreadable, whether a module is named cannot be known, so the coarser rule
    /// applies: quality checking on means checks JIM cannot see may apply.
    /// </summary>
    [Test]
    public async Task GetPasswordPolicyAsync_WhenTheConfigurationIsRefused_FallsBackToTheQualityFlagAsync()
    {
        SetupDirectory(
            config: null,
            policies: LdapTestResponses.SearchResponseWith(DefaultPolicyDn, ("pwdMinLength", "12"), ("pwdCheckQuality", "1")),
            probe: LdapTestResponses.EmptySearchResponse());

        var policy = await CreateReader().GetPasswordPolicyAsync(Scope());

        Assert.That(policy!.FurtherChecksApply, Is.True);
    }

    #endregion

    #region search budget

    [Test]
    public async Task GetPasswordPolicyAsync_SpendsExactlyThreeSearchesAsync()
    {
        SetupDirectory(
            config: ConfigNamingTheDefault(),
            policies: LdapTestResponses.SearchResponseWith(DefaultPolicyDn, ("pwdMinLength", "12")),
            probe: LdapTestResponses.EmptySearchResponse());

        await CreateReader().GetPasswordPolicyAsync(Scope());

        _executor.Verify(e => e.SendRequestAsync(It.IsAny<DirectoryRequest>()), Times.Exactly(3));
    }

    [Test]
    public async Task GetPasswordPolicyAsync_LimitsTheProbeToOneEntryAsync()
    {
        SearchRequest? probeRequest = null;
        _executor.Setup(e => e.SendRequestAsync(It.IsAny<SearchRequest>()))
            .Returns((SearchRequest request) =>
            {
                if (request.Filter?.ToString()?.Contains("pwdPolicySubentry", StringComparison.OrdinalIgnoreCase) == true)
                    probeRequest = request;
                return Task.FromResult<DirectoryResponse>(LdapTestResponses.EmptySearchResponse());
            });

        await CreateReader().GetPasswordPolicyAsync(Scope());

        Assert.That(probeRequest, Is.Not.Null);
        Assert.That(probeRequest!.SizeLimit, Is.EqualTo(1));
        Assert.That(probeRequest.Scope, Is.EqualTo(SearchScope.Subtree));
    }

    #endregion

    #region helpers

    private LdapConnectorPasswordPolicy CreateReader() =>
        new(_executor.Object, Log.Logger, LdapDirectoryType.OpenLDAP);

    private static LdapPasswordPolicyScope Scope(bool advertisesControl = true) =>
        new(null, [UserContext], ConfigContext, advertisesControl);

    /// <summary>
    /// The shape cn=config returns to an account that can read it: the database with its suffix, and the overlay
    /// beneath it naming the default policy.
    /// </summary>
    private static SearchResponse ConfigNamingTheDefault() =>
        LdapTestResponses.SearchResponseWithEntries(
            LdapTestResponses.Entry("olcDatabase={1}mdb,cn=config", ("olcSuffix", UserContext)),
            LdapTestResponses.Entry(OverlayDn, ("olcPPolicyDefault", DefaultPolicyDn)));

    /// <summary>
    /// Routes the three searches by where they are aimed: cn=config, the pwdPolicySubentry probe (by filter) and
    /// the policy entry search. A null config response is a refusal.
    /// </summary>
    private void SetupDirectory(SearchResponse? config, SearchResponse policies, SearchResponse? probe, Exception? probeException = null)
    {
        _executor.Setup(e => e.SendRequestAsync(It.IsAny<SearchRequest>()))
            .Returns((SearchRequest request) =>
            {
                if (string.Equals(request.DistinguishedName, ConfigContext, StringComparison.OrdinalIgnoreCase))
                    return config != null
                        ? Task.FromResult<DirectoryResponse>(config)
                        : throw new LdapException((int)ResultCode.InsufficientAccessRights, "insufficient access rights");

                if (request.Filter?.ToString()?.Contains("pwdPolicySubentry", StringComparison.OrdinalIgnoreCase) == true)
                    return probe != null
                        ? Task.FromResult<DirectoryResponse>(probe)
                        : throw probeException!;

                return Task.FromResult<DirectoryResponse>(policies);
            });
    }

    #endregion
}
