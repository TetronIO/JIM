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
/// Covers discovery of the password policy a directory enforces.
/// <para>
/// Two things here are easy to get confidently wrong. Active Directory stores durations as negative counts of
/// 100-nanosecond intervals with two different sentinels meaning "no limit", one of which cannot be negated
/// without overflowing. And it applies access control to searches as a silent filter, so an empty result is not
/// evidence of absence: reading it as such would tell most administrators there are no overriding policies when
/// JIM simply had no rights to look.
/// </para>
/// </summary>
[TestFixture]
public class LdapConnectorPasswordPolicyTests
{
    private const string DomainRoot = "DC=testdomain,DC=local";
    private const string PasswordSettingsContainer = "CN=Password Settings Container,CN=System,DC=testdomain,DC=local";

    private Mock<ILdapOperationExecutor> _executor = null!;

    [SetUp]
    public void SetUp() => _executor = new Mock<ILdapOperationExecutor>();

    #region interval parsing

    /// <summary>
    /// A 90 day maximum password age is stored as the negative of 90 days in 100-nanosecond intervals.
    /// </summary>
    [Test]
    public void ParseInterval_WithANegativeDuration_ReturnsThePositiveEquivalent()
    {
        var ninetyDaysInTicks = TimeSpan.FromDays(90).Ticks;

        var result = LdapConnectorPasswordPolicy.ParseInterval(-ninetyDaysInTicks);

        Assert.That(result, Is.EqualTo(TimeSpan.FromDays(90)));
    }

    /// <summary>
    /// The sentinel Active Directory writes for "passwords never expire". It is also long.MinValue, which cannot
    /// be negated or passed to Math.Abs without overflowing, so it has to be recognised before any arithmetic.
    /// </summary>
    [Test]
    public void ParseInterval_WithTheNeverExpiresSentinel_ReturnsNullWithoutOverflowing()
    {
        Assert.That(long.MinValue, Is.EqualTo(-9223372036854775808), "This test is about the exact sentinel Active Directory writes.");

        TimeSpan? result = null;
        Assert.DoesNotThrow(() => result = LdapConnectorPasswordPolicy.ParseInterval(long.MinValue));

        Assert.That(result, Is.Null);
    }

    /// <summary>
    /// Setting a maximum age of zero days means "never expires", not "expires immediately". Reading zero as a
    /// duration would expire every password the moment it was set.
    /// </summary>
    [Test]
    public void ParseInterval_WithZero_ReturnsNullRatherThanAnImmediateExpiry()
    {
        Assert.That(LdapConnectorPasswordPolicy.ParseInterval(0), Is.Null);
    }

    [Test]
    public void ParseInterval_WithNoValue_ReturnsNull()
    {
        Assert.That(LdapConnectorPasswordPolicy.ParseInterval(null), Is.Null);
    }

    /// <summary>
    /// The convention is negative, but a directory writing the positive form should not yield a negative duration.
    /// </summary>
    [Test]
    public void ParseInterval_WithAPositiveDuration_StillReturnsAPositiveDuration()
    {
        var result = LdapConnectorPasswordPolicy.ParseInterval(TimeSpan.FromDays(30).Ticks);

        Assert.That(result, Is.EqualTo(TimeSpan.FromDays(30)));
    }

    #endregion

    #region complexity flag

    [Test]
    public void IsComplexityRequired_WithTheComplexityBitSet_ReturnsTrue()
    {
        Assert.That(LdapConnectorPasswordPolicy.IsComplexityRequired(1), Is.True);
    }

    /// <summary>
    /// pwdProperties carries six unrelated flags. Reading the whole value as a boolean, or testing the wrong bit,
    /// would report complexity purely because some other option was enabled.
    /// </summary>
    [Test]
    public void IsComplexityRequired_WithOtherFlagsButNotComplexity_ReturnsFalse()
    {
        const int noAnonChange = 0x02;
        const int noClearChange = 0x04;
        const int lockoutAdmins = 0x08;
        const int storeCleartext = 0x10;
        const int refusePasswordChange = 0x20;

        var everythingElse = noAnonChange | noClearChange | lockoutAdmins | storeCleartext | refusePasswordChange;

        Assert.That(LdapConnectorPasswordPolicy.IsComplexityRequired(everythingElse), Is.False);
    }

    [Test]
    public void IsComplexityRequired_WithComplexityAlongsideOtherFlags_ReturnsTrue()
    {
        Assert.That(LdapConnectorPasswordPolicy.IsComplexityRequired(0x01 | 0x10), Is.True);
    }

    [Test]
    public void IsComplexityRequired_WithNoFlags_ReturnsFalse()
    {
        Assert.That(LdapConnectorPasswordPolicy.IsComplexityRequired(0), Is.False);
    }

    #endregion

    #region reading the domain policy

    [Test]
    public async Task GetPasswordPolicyAsync_AgainstActiveDirectory_MapsEveryDiscoveredValueAsync()
    {
        SetupDirectory(
            domainPolicy: LdapTestResponses.SearchResponseWith(DomainRoot,
                ("minPwdLength", "12"),
                ("pwdProperties", "1"),
                ("pwdHistoryLength", "24"),
                ("maxPwdAge", (-TimeSpan.FromDays(90).Ticks).ToString()),
                ("minPwdAge", (-TimeSpan.FromDays(1).Ticks).ToString())),
            fineGrained: LdapTestResponses.EmptySearchResponse());

        var policy = await CreateReader(LdapDirectoryType.ActiveDirectory).GetPasswordPolicyAsync(DomainRoot);

        Assert.That(policy, Is.Not.Null);
        Assert.That(policy!.MinimumLength, Is.EqualTo(12));
        Assert.That(policy.ComplexityRequired, Is.True);
        Assert.That(policy.PasswordHistoryLength, Is.EqualTo(24));
        Assert.That(policy.MaximumPasswordAge, Is.EqualTo(TimeSpan.FromDays(90)));
        Assert.That(policy.MinimumPasswordAge, Is.EqualTo(TimeSpan.FromDays(1)));
        Assert.That(policy.HasAnyDiscoveredConstraint, Is.True);
    }

    /// <summary>
    /// Active Directory's complexity rule is fixed in the product: three of five categories. Recording it on the
    /// policy is what lets the generator validate a composed passphrase without knowing it is talking to Active
    /// Directory.
    /// </summary>
    [Test]
    public async Task GetPasswordPolicyAsync_WithComplexityRequired_RecordsTheThreeOfFiveCategoryRuleAsync()
    {
        SetupDirectory(
            domainPolicy: LdapTestResponses.SearchResponseWith(DomainRoot, ("pwdProperties", "1")),
            fineGrained: LdapTestResponses.EmptySearchResponse());

        var policy = await CreateReader(LdapDirectoryType.ActiveDirectory).GetPasswordPolicyAsync(DomainRoot);

        Assert.That(policy!.RequiredCharacterClassCount, Is.EqualTo(3));
        Assert.That(policy.RecognisedCharacterClasses, Is.EqualTo(
            PasswordCharacterClasses.Uppercase | PasswordCharacterClasses.Lowercase | PasswordCharacterClasses.Digit |
            PasswordCharacterClasses.Symbol | PasswordCharacterClasses.OtherUnicodeLetter));
    }

    [Test]
    public async Task GetPasswordPolicyAsync_WithComplexityNotRequired_RecordsNoCategoryRuleAsync()
    {
        SetupDirectory(
            domainPolicy: LdapTestResponses.SearchResponseWith(DomainRoot, ("pwdProperties", "0")),
            fineGrained: LdapTestResponses.EmptySearchResponse());

        var policy = await CreateReader(LdapDirectoryType.ActiveDirectory).GetPasswordPolicyAsync(DomainRoot);

        Assert.That(policy!.ComplexityRequired, Is.False);
        Assert.That(policy.RequiredCharacterClassCount, Is.Null);
        Assert.That(policy.RecognisedCharacterClasses, Is.EqualTo(PasswordCharacterClasses.None));
    }

    /// <summary>
    /// A service account is frequently allowed to read some of the policy and not the rest. Half a policy is far
    /// more useful to an administrator than none, so absent attributes stay null instead of failing the read.
    /// </summary>
    [Test]
    public async Task GetPasswordPolicyAsync_WithOnlySomeAttributesReadable_ReturnsThePartialPolicyAsync()
    {
        SetupDirectory(
            domainPolicy: LdapTestResponses.SearchResponseWith(DomainRoot, ("minPwdLength", "8")),
            fineGrained: LdapTestResponses.EmptySearchResponse());

        var policy = await CreateReader(LdapDirectoryType.ActiveDirectory).GetPasswordPolicyAsync(DomainRoot);

        Assert.That(policy!.MinimumLength, Is.EqualTo(8));
        Assert.That(policy.ComplexityRequired, Is.Null);
        Assert.That(policy.PasswordHistoryLength, Is.Null);
        Assert.That(policy.HasAnyDiscoveredConstraint, Is.True);
    }

    /// <summary>
    /// Directories that are not Active Directory hold their policy in an entry whose location is local
    /// configuration and is not advertised to clients, so there is nothing to find. Returning an empty policy
    /// would imply JIM had looked and found no constraints.
    /// </summary>
    [Test]
    public async Task GetPasswordPolicyAsync_AgainstADirectoryThatPublishesNoPolicy_ReturnsNullAsync()
    {
        var policy = await CreateReader(LdapDirectoryType.OpenLDAP).GetPasswordPolicyAsync(DomainRoot);

        Assert.That(policy, Is.Null);
        _executor.Verify(e => e.SendRequestAsync(It.IsAny<DirectoryRequest>()), Times.Never);
    }

    [Test]
    public async Task GetPasswordPolicyAsync_WhenTheDomainRootCannotBeRead_ReturnsNullRatherThanThrowingAsync()
    {
        _executor.Setup(e => e.SendRequestAsync(It.IsAny<SearchRequest>()))
            .ThrowsAsync(new LdapException(81, "server down"));

        var policy = await CreateReader(LdapDirectoryType.ActiveDirectory).GetPasswordPolicyAsync(DomainRoot);

        Assert.That(policy, Is.Null, "A failed policy read must never fail the schema import that triggered it.");
    }

    #endregion

    #region policies that override the domain default

    /// <summary>
    /// The regression test for the mistake this code originally made. Active Directory applies access control to
    /// searches as a silent filter, so a caller with no rights over the Password Settings Container receives a
    /// successful, empty result: exactly what a domain with no overriding policies returns. The container is
    /// Domain Admins only by default, so reading empty as "there are none" would give the wrong answer in most
    /// deployments, and give it confidently.
    /// </summary>
    [Test]
    public async Task GetPasswordPolicyAsync_WithAnEmptyPolicyContainer_ReportsUndeterminedRatherThanAbsentAsync()
    {
        SetupDirectory(
            domainPolicy: LdapTestResponses.SearchResponseWith(DomainRoot, ("minPwdLength", "8")),
            fineGrained: LdapTestResponses.EmptySearchResponse());

        var policy = await CreateReader(LdapDirectoryType.ActiveDirectory).GetPasswordPolicyAsync(DomainRoot);

        Assert.That(policy!.FineGrainedPolicySignal, Is.EqualTo(FineGrainedPolicySignal.CouldNotDetermine));
    }

    [Test]
    public async Task GetPasswordPolicyAsync_WithPoliciesInTheContainer_ReportsThemPresentAsync()
    {
        SetupDirectory(
            domainPolicy: LdapTestResponses.SearchResponseWith(DomainRoot, ("minPwdLength", "8")),
            fineGrained: LdapTestResponses.SearchResponseWithEntries(
                LdapTestResponses.Entry($"CN=Executives,{PasswordSettingsContainer}", ("objectClass", "msDS-PasswordSettings"))));

        var policy = await CreateReader(LdapDirectoryType.ActiveDirectory).GetPasswordPolicyAsync(DomainRoot);

        Assert.That(policy!.FineGrainedPolicySignal, Is.EqualTo(FineGrainedPolicySignal.Present));
    }

    /// <summary>
    /// The one case where absence can be proved rather than inferred: a domain whose functional level predates the
    /// feature cannot hold these policies at all.
    /// </summary>
    [Test]
    public async Task GetPasswordPolicyAsync_BelowTheFunctionalLevelThatSupportsThem_ReportsThemAbsentAsync()
    {
        _executor.Setup(e => e.SendRequestAsync(It.IsAny<SearchRequest>()))
            .Returns((SearchRequest request) => Task.FromResult<DirectoryResponse>(
                request.DistinguishedName == DomainRoot
                    ? LdapTestResponses.SearchResponseWith(DomainRoot, ("minPwdLength", "8"))
                    : string.IsNullOrEmpty(request.DistinguishedName)
                        ? LdapTestResponses.SearchResponseWith("", ("domainFunctionality", "2"))
                        : LdapTestResponses.EmptySearchResponse()));

        var policy = await CreateReader(LdapDirectoryType.ActiveDirectory).GetPasswordPolicyAsync(DomainRoot);

        Assert.That(policy!.FineGrainedPolicySignal, Is.EqualTo(FineGrainedPolicySignal.Absent));
    }

    /// <summary>
    /// A refusal is not evidence of absence either. An inaccessible object and a non-existent one are reported
    /// the same way, so neither can be read as "there are none".
    /// </summary>
    [Test]
    public async Task GetPasswordPolicyAsync_WhenTheContainerReadIsRefused_ReportsUndeterminedAsync()
    {
        _executor.Setup(e => e.SendRequestAsync(It.IsAny<SearchRequest>()))
            .Returns((SearchRequest request) =>
            {
                if (request.DistinguishedName == DomainRoot)
                    return Task.FromResult<DirectoryResponse>(LdapTestResponses.SearchResponseWith(DomainRoot, ("minPwdLength", "8")));

                if (string.IsNullOrEmpty(request.DistinguishedName))
                    return Task.FromResult<DirectoryResponse>(LdapTestResponses.SearchResponseWith("", ("domainFunctionality", "7")));

                throw new LdapException((int)ResultCode.InsufficientAccessRights, "insufficient access rights");
            });

        var policy = await CreateReader(LdapDirectoryType.ActiveDirectory).GetPasswordPolicyAsync(DomainRoot);

        Assert.That(policy!.FineGrainedPolicySignal, Is.EqualTo(FineGrainedPolicySignal.CouldNotDetermine));
    }

    #endregion

    #region helpers

    private LdapConnectorPasswordPolicy CreateReader(LdapDirectoryType directoryType) =>
        new(_executor.Object, Log.Logger, directoryType);

    /// <summary>
    /// Routes the three reads the policy discovery makes: the domain root, the rootDSE functional level, and the
    /// Password Settings Container.
    /// </summary>
    private void SetupDirectory(SearchResponse domainPolicy, SearchResponse fineGrained, string domainFunctionality = "7")
    {
        _executor.Setup(e => e.SendRequestAsync(It.IsAny<SearchRequest>()))
            .Returns((SearchRequest request) => Task.FromResult<DirectoryResponse>(
                request.DistinguishedName == DomainRoot
                    ? domainPolicy
                    : string.IsNullOrEmpty(request.DistinguishedName)
                        ? LdapTestResponses.SearchResponseWith("", ("domainFunctionality", domainFunctionality))
                        : fineGrained));
    }

    #endregion
}
