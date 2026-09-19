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
/// Covers discovery of the global password policy 389 Directory Server holds on cn=config.
/// <para>
/// 389 publishes every policy figure whether or not the switch that makes the server apply it is on, so a reader
/// that copied the numbers would report rules the server does not enforce. The tests per switch are the point of
/// this fixture.
/// </para>
/// </summary>
[TestFixture]
public class LdapConnectorPasswordPolicy389Tests
{
    private const string UserContext = "dc=example,dc=local";
    private const string ConfigDn = "cn=config";

    private Mock<ILdapOperationExecutor> _executor = null!;

    [SetUp]
    public void SetUp() => _executor = new Mock<ILdapOperationExecutor>();

    #region mapping

    /// <summary>
    /// PRD Scenario 3: syntax checking on, minimum length 10, three of five categories.
    /// </summary>
    [Test]
    public async Task GetPasswordPolicyAsync_WithSyntaxCheckingOn_MapsLengthAndCategoriesAsync()
    {
        SetupDirectory(
            config: Config(
                ("passwordCheckSyntax", "on"),
                ("passwordMinLength", "10"),
                ("passwordMinCategories", "3"),
                ("passwordHistory", "on"),
                ("passwordInHistory", "6"),
                ("passwordExp", "on"),
                ("passwordMaxAge", "8640000"),
                ("passwordMinAge", "86400")),
            probe: LdapTestResponses.EmptySearchResponse());

        var policy = await CreateReader().GetPasswordPolicyAsync(Scope());

        Assert.That(policy, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(policy!.DiscoveryOutcome, Is.EqualTo(PasswordPolicyDiscoveryOutcome.Read));
            Assert.That(policy.MinimumLength, Is.EqualTo(10));
            Assert.That(policy.ComplexityRequired, Is.True);
            Assert.That(policy.RequiredCharacterClassCount, Is.EqualTo(3));
            Assert.That(policy.RecognisedCharacterClasses, Is.EqualTo(
                PasswordCharacterClasses.Uppercase | PasswordCharacterClasses.Lowercase | PasswordCharacterClasses.Digit |
                PasswordCharacterClasses.Symbol | PasswordCharacterClasses.OtherUnicodeLetter));
            Assert.That(policy.PasswordHistoryLength, Is.EqualTo(6));
            Assert.That(policy.MaximumPasswordAge, Is.EqualTo(TimeSpan.FromDays(100)));
            Assert.That(policy.MinimumPasswordAge, Is.EqualTo(TimeSpan.FromDays(1)));
            Assert.That(policy.FurtherChecksApply, Is.False);
        }
    }

    /// <summary>
    /// A single category is no complexity rule at all: every password draws on at least one.
    /// </summary>
    [Test]
    public async Task GetPasswordPolicyAsync_WithOneCategory_RecordsNoComplexityRuleAsync()
    {
        SetupDirectory(
            config: Config(("passwordCheckSyntax", "on"), ("passwordMinLength", "8"), ("passwordMinCategories", "1")),
            probe: LdapTestResponses.EmptySearchResponse());

        var policy = await CreateReader().GetPasswordPolicyAsync(Scope());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(policy!.ComplexityRequired, Is.False);
            Assert.That(policy.RequiredCharacterClassCount, Is.Null);
            Assert.That(policy.RecognisedCharacterClasses, Is.EqualTo(PasswordCharacterClasses.None));
        }
    }

    [Test]
    public async Task GetPasswordPolicyAsync_WithSyntaxCheckingOff_LeavesLengthAndCategoriesNullAsync()
    {
        SetupDirectory(
            config: Config(("passwordCheckSyntax", "off"), ("passwordMinLength", "10"), ("passwordMinCategories", "3")),
            probe: LdapTestResponses.EmptySearchResponse());

        var policy = await CreateReader().GetPasswordPolicyAsync(Scope());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(policy!.MinimumLength, Is.Null);
            Assert.That(policy.ComplexityRequired, Is.Null);
            Assert.That(policy.RequiredCharacterClassCount, Is.Null);
            Assert.That(policy.DiscoveryOutcome, Is.EqualTo(PasswordPolicyDiscoveryOutcome.Read));
        }
    }

    [Test]
    public async Task GetPasswordPolicyAsync_WithHistoryOff_LeavesHistoryLengthNullAsync()
    {
        SetupDirectory(
            config: Config(("passwordHistory", "off"), ("passwordInHistory", "6")),
            probe: LdapTestResponses.EmptySearchResponse());

        var policy = await CreateReader().GetPasswordPolicyAsync(Scope());

        Assert.That(policy!.PasswordHistoryLength, Is.Null);
    }

    [Test]
    public async Task GetPasswordPolicyAsync_WithExpiryOff_LeavesMaximumAgeNullAsync()
    {
        SetupDirectory(
            config: Config(("passwordExp", "off"), ("passwordMaxAge", "8640000")),
            probe: LdapTestResponses.EmptySearchResponse());

        var policy = await CreateReader().GetPasswordPolicyAsync(Scope());

        Assert.That(policy!.MaximumPasswordAge, Is.Null);
    }

    [Test]
    public async Task GetPasswordPolicyAsync_WithExpiryOnButAZeroMaximumAge_LeavesMaximumAgeNullAsync()
    {
        SetupDirectory(
            config: Config(("passwordExp", "on"), ("passwordMaxAge", "0")),
            probe: LdapTestResponses.EmptySearchResponse());

        var policy = await CreateReader().GetPasswordPolicyAsync(Scope());

        Assert.That(policy!.MaximumPasswordAge, Is.Null);
    }

    [Test]
    public async Task GetPasswordPolicyAsync_WithAZeroMinimumAge_LeavesMinimumAgeNullAsync()
    {
        SetupDirectory(
            config: Config(("passwordMinAge", "0")),
            probe: LdapTestResponses.EmptySearchResponse());

        var policy = await CreateReader().GetPasswordPolicyAsync(Scope());

        Assert.That(policy!.MinimumPasswordAge, Is.Null);
    }

    #endregion

    #region outcomes

    /// <summary>
    /// PRD Scenario 4: an account without rights on cn=config gets an empty read, which is a refusal by another
    /// name. The outcome has to say the configuration was not readable so the remedy is a right, not a policy.
    /// </summary>
    [Test]
    public async Task GetPasswordPolicyAsync_WhenTheConfigurationReadsEmpty_ReportsConfigurationNotReadableAndUndeterminedAsync()
    {
        SetupDirectory(
            config: LdapTestResponses.EmptySearchResponse(),
            probe: LdapTestResponses.EmptySearchResponse());

        var policy = await CreateReader().GetPasswordPolicyAsync(Scope());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(policy!.DiscoveryOutcome, Is.EqualTo(PasswordPolicyDiscoveryOutcome.ConfigurationNotReadable));
            Assert.That(policy.PolicyOverrideSignal, Is.EqualTo(PolicyOverrideSignal.CouldNotDetermine));
            Assert.That(policy.HasAnyDiscoveredConstraint, Is.False);
        }
    }

    [Test]
    public async Task GetPasswordPolicyAsync_WhenTheConfigurationReadIsRefused_ReportsConfigurationNotReadableRatherThanThrowingAsync()
    {
        _executor.Setup(e => e.SendRequestAsync(It.IsAny<SearchRequest>()))
            .ThrowsAsync(new DirectoryOperationException(LdapTestResponses.Create<SearchResponse>(ResultCode.InsufficientAccessRights), "insufficient access"));

        var policy = await CreateReader().GetPasswordPolicyAsync(Scope());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(policy!.DiscoveryOutcome, Is.EqualTo(PasswordPolicyDiscoveryOutcome.ConfigurationNotReadable));
            Assert.That(policy.PolicyOverrideSignal, Is.EqualTo(PolicyOverrideSignal.CouldNotDetermine));
        }
    }

    #endregion

    #region overrides

    [Test]
    public async Task GetPasswordPolicyAsync_WhenAPolicyContainerExists_ReportsOverridesPresentAsync()
    {
        SetupDirectory(
            config: Config(("passwordCheckSyntax", "on"), ("passwordMinLength", "10")),
            probe: LdapTestResponses.SearchResponseWith("cn=nsPwPolicyContainer,ou=People,dc=example,dc=local", ("objectClass", "nsPwPolicyContainer")));

        var policy = await CreateReader().GetPasswordPolicyAsync(Scope());

        Assert.That(policy!.PolicyOverrideSignal, Is.EqualTo(PolicyOverrideSignal.Present));
    }

    [Test]
    public async Task GetPasswordPolicyAsync_WhenAnEntryCarriesASubentry_ReportsOverridesPresentAsync()
    {
        SetupDirectory(
            config: Config(("passwordCheckSyntax", "on"), ("passwordMinLength", "10")),
            probe: LdapTestResponses.SearchResponseWith("uid=ceo,ou=People,dc=example,dc=local", ("pwdpolicysubentry", "cn=cn=ceoPolicy,cn=nsPwPolicyContainer,ou=People,dc=example,dc=local")));

        var policy = await CreateReader().GetPasswordPolicyAsync(Scope());

        Assert.That(policy!.PolicyOverrideSignal, Is.EqualTo(PolicyOverrideSignal.Present));
    }

    /// <summary>
    /// 389 refuses an unindexed search from a non-privileged account with an administrative limit error, which is
    /// a refusal to look rather than evidence of absence.
    /// </summary>
    [Test]
    public async Task GetPasswordPolicyAsync_WhenTheProbeHitsAnAdministrativeLimit_ReportsUndeterminedAsync()
    {
        SetupDirectory(
            config: Config(("passwordCheckSyntax", "on"), ("passwordMinLength", "10")),
            probe: null,
            probeException: new DirectoryOperationException(LdapTestResponses.Create<SearchResponse>(ResultCode.AdminLimitExceeded), "administrative limit exceeded"));

        var policy = await CreateReader().GetPasswordPolicyAsync(Scope());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(policy!.PolicyOverrideSignal, Is.EqualTo(PolicyOverrideSignal.CouldNotDetermine));
            Assert.That(policy.MinimumLength, Is.EqualTo(10), "A refused probe must not lose the policy that was read.");
        }
    }

    /// <summary>
    /// Absence is never provable on 389: the subentry attribute is operational and the container can live anywhere
    /// in the tree, so an empty probe is an unknown.
    /// </summary>
    [Test]
    public async Task GetPasswordPolicyAsync_WhenTheProbeIsEmpty_ReportsUndeterminedRatherThanAbsentAsync()
    {
        SetupDirectory(
            config: Config(("passwordCheckSyntax", "on"), ("passwordMinLength", "10")),
            probe: LdapTestResponses.EmptySearchResponse());

        var policy = await CreateReader().GetPasswordPolicyAsync(Scope());

        Assert.That(policy!.PolicyOverrideSignal, Is.EqualTo(PolicyOverrideSignal.CouldNotDetermine));
    }

    [Test]
    public async Task GetPasswordPolicyAsync_ProbesEveryUserNamingContextUpToTheCapAsync()
    {
        var probed = new List<string>();
        _executor.Setup(e => e.SendRequestAsync(It.IsAny<SearchRequest>()))
            .Returns((SearchRequest request) =>
            {
                if (request.DistinguishedName != ConfigDn)
                    probed.Add(request.DistinguishedName);
                return Task.FromResult<DirectoryResponse>(request.DistinguishedName == ConfigDn
                    ? Config(("passwordMinLength", "10"))
                    : LdapTestResponses.EmptySearchResponse());
            });
        var contexts = Enumerable.Range(1, 7).Select(i => $"dc=suffix{i},dc=local").ToList();

        await CreateReader().GetPasswordPolicyAsync(new LdapPasswordPolicyScope(null, contexts, null, false));

        Assert.That(probed, Is.EqualTo(contexts.Take(LdapConnectorPasswordPolicy389.MaximumNamingContextsToProbe)));
        Assert.That(LdapConnectorPasswordPolicy389.MaximumNamingContextsToProbe, Is.EqualTo(5));
    }

    [Test]
    public async Task GetPasswordPolicyAsync_StopsProbingOnceOverridesAreFoundAsync()
    {
        _executor.Setup(e => e.SendRequestAsync(It.IsAny<SearchRequest>()))
            .Returns((SearchRequest request) => Task.FromResult<DirectoryResponse>(request.DistinguishedName == ConfigDn
                ? Config(("passwordMinLength", "10"))
                : LdapTestResponses.SearchResponseWith($"cn=nsPwPolicyContainer,{request.DistinguishedName}", ("objectClass", "nsPwPolicyContainer"))));

        var policy = await CreateReader().GetPasswordPolicyAsync(new LdapPasswordPolicyScope(null, ["dc=a", "dc=b", "dc=c"], null, false));

        Assert.That(policy!.PolicyOverrideSignal, Is.EqualTo(PolicyOverrideSignal.Present));
        _executor.Verify(e => e.SendRequestAsync(It.IsAny<DirectoryRequest>()), Times.Exactly(2));
    }

    #endregion

    #region further checks

    [Test]
    public async Task GetPasswordPolicyAsync_WithTheDictionaryCheckOn_FlagsFurtherChecksAsync()
    {
        SetupDirectory(
            config: Config(("passwordCheckSyntax", "on"), ("passwordMinLength", "10"), ("passwordDictCheck", "on")),
            probe: LdapTestResponses.EmptySearchResponse());

        var policy = await CreateReader().GetPasswordPolicyAsync(Scope());

        Assert.That(policy!.FurtherChecksApply, Is.True);
    }

    [Test]
    public async Task GetPasswordPolicyAsync_WithAPerClassMinimum_FlagsFurtherChecksAsync()
    {
        SetupDirectory(
            config: Config(("passwordCheckSyntax", "on"), ("passwordMinLength", "10"), ("passwordMinDigits", "2")),
            probe: LdapTestResponses.EmptySearchResponse());

        var policy = await CreateReader().GetPasswordPolicyAsync(Scope());

        Assert.That(policy!.FurtherChecksApply, Is.True);
    }

    /// <summary>
    /// Every one of the extra checks is gated by syntax checking, so with that off none of them applies.
    /// </summary>
    [Test]
    public async Task GetPasswordPolicyAsync_WithTheDictionaryCheckOnButSyntaxCheckingOff_DoesNotFlagFurtherChecksAsync()
    {
        SetupDirectory(
            config: Config(("passwordCheckSyntax", "off"), ("passwordDictCheck", "on"), ("passwordMinDigits", "2")),
            probe: LdapTestResponses.EmptySearchResponse());

        var policy = await CreateReader().GetPasswordPolicyAsync(Scope());

        Assert.That(policy!.FurtherChecksApply, Is.False);
    }

    /// <summary>
    /// 389 returns these attributes with their defaults (off and zero) whether or not an administrator set them,
    /// so presence alone must not flag anything.
    /// </summary>
    [Test]
    public async Task GetPasswordPolicyAsync_WithEveryExtraCheckAtItsDefault_DoesNotFlagFurtherChecksAsync()
    {
        SetupDirectory(
            config: Config(
                ("passwordCheckSyntax", "on"), ("passwordMinLength", "10"),
                ("passwordDictCheck", "off"), ("passwordPalindrome", "off"), ("passwordMaxRepeats", "0"),
                ("passwordMaxSequence", "0"), ("passwordMaxSeqSets", "0"), ("passwordMaxClassChars", "0"),
                ("passwordMinDigits", "0"), ("passwordMinAlphas", "0"), ("passwordMinUppers", "0"),
                ("passwordMinLowers", "0"), ("passwordMinSpecials", "0"), ("passwordMin8Bit", "0"),
                ("passwordMinTokenLength", "0")),
            probe: LdapTestResponses.EmptySearchResponse());

        var policy = await CreateReader().GetPasswordPolicyAsync(Scope());

        Assert.That(policy!.FurtherChecksApply, Is.False);
    }

    #endregion

    #region search budget

    [Test]
    public async Task GetPasswordPolicyAsync_WithOneUserContext_SpendsExactlyTwoSearchesAsync()
    {
        SetupDirectory(
            config: Config(("passwordCheckSyntax", "on"), ("passwordMinLength", "10")),
            probe: LdapTestResponses.EmptySearchResponse());

        await CreateReader().GetPasswordPolicyAsync(Scope());

        _executor.Verify(e => e.SendRequestAsync(It.IsAny<DirectoryRequest>()), Times.Exactly(2));
    }

    [Test]
    public async Task GetPasswordPolicyAsync_LimitsTheProbeToOneEntryAsync()
    {
        SearchRequest? probeRequest = null;
        _executor.Setup(e => e.SendRequestAsync(It.IsAny<SearchRequest>()))
            .Returns((SearchRequest request) =>
            {
                if (request.DistinguishedName != ConfigDn)
                    probeRequest = request;
                return Task.FromResult<DirectoryResponse>(request.DistinguishedName == ConfigDn
                    ? Config(("passwordMinLength", "10"))
                    : LdapTestResponses.EmptySearchResponse());
            });

        await CreateReader().GetPasswordPolicyAsync(Scope());

        Assert.That(probeRequest, Is.Not.Null);
        Assert.That(probeRequest!.SizeLimit, Is.EqualTo(1));
        Assert.That(probeRequest.Scope, Is.EqualTo(SearchScope.Subtree));
    }

    #endregion

    #region helpers

    private LdapConnectorPasswordPolicy CreateReader() =>
        new(_executor.Object, Log.Logger, LdapDirectoryType.DirectoryServer389);

    private static LdapPasswordPolicyScope Scope() => new(null, [UserContext], null, false);

    private static SearchResponse Config(params (string Name, string Value)[] attributes) =>
        LdapTestResponses.SearchResponseWith(ConfigDn, attributes);

    /// <summary>
    /// Routes the two reads by DN: cn=config, and the probe of the user naming context.
    /// </summary>
    private void SetupDirectory(SearchResponse config, SearchResponse? probe, Exception? probeException = null)
    {
        _executor.Setup(e => e.SendRequestAsync(It.IsAny<SearchRequest>()))
            .Returns((SearchRequest request) =>
            {
                if (request.DistinguishedName == ConfigDn)
                    return Task.FromResult<DirectoryResponse>(config);

                return probe != null
                    ? Task.FromResult<DirectoryResponse>(probe)
                    : throw probeException!;
            });
    }

    #endregion
}
