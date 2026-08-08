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
/// Covers the non-destructive checks JIM runs before relying on a password channel.
/// <para>
/// The behaviour worth protecting here is what the checks say when they cannot see an answer. A preflight exists
/// to be trusted, so a check that reports a pass it did not establish is worse than one that reports nothing:
/// the administrator stops looking. Several of these tests exist only to hold that line.
/// </para>
/// </summary>
[TestFixture]
public class LdapConnectorPreflightTests
{
    private const string DomainRoot = "DC=testdomain,DC=local";

    private Mock<ILdapOperationExecutor> _executor = null!;

    [SetUp]
    public void SetUp()
    {
        _executor = new Mock<ILdapOperationExecutor>();

        // A directory that answers everything with an empty result. Individual tests override what they care
        // about; the default keeps every other check from throwing and obscuring the one under test.
        _executor.Setup(x => x.SendRequestAsync(It.IsAny<DirectoryRequest>()))
            .ReturnsAsync(LdapTestResponses.EmptySearchResponse());
    }

    private LdapConnectorPreflight CreatePreflight(
        LdapDirectoryType directoryType = LdapDirectoryType.ActiveDirectory,
        bool supportsPasswordModifyExtension = false,
        bool isConnectionEncrypted = true) =>
        new(_executor.Object, Log.Logger, directoryType, supportsPasswordModifyExtension, isConnectionEncrypted);

    private async Task<PasswordPreflightCheckResult> RunAndGetAsync(
        PasswordPreflightCheck check,
        LdapDirectoryType directoryType = LdapDirectoryType.ActiveDirectory,
        bool supportsPasswordModifyExtension = false,
        bool isConnectionEncrypted = true,
        IReadOnlyList<string>? containerExternalIds = null)
    {
        var preflight = CreatePreflight(directoryType, supportsPasswordModifyExtension, isConnectionEncrypted);
        var results = await preflight.RunAsync(containerExternalIds ?? [], DomainRoot, CancellationToken.None);
        return results.Single(r => r.Check == check);
    }

    #region encryption

    [Test]
    public async Task RunAsync_WithAnEncryptedConnection_PassesTheEncryptionCheckAsync()
    {
        var check = await RunAndGetAsync(PasswordPreflightCheck.Encryption, isConnectionEncrypted: true);

        Assert.That(check.State, Is.EqualTo(PasswordPreflightState.Passed));
    }

    /// <summary>
    /// A warning, not a failure. JIM allows an unencrypted password channel because some directories genuinely
    /// cannot serve TLS, and refusing those deployments password management entirely helps nobody. The
    /// administrator makes that call, having been told what it costs.
    /// </summary>
    [Test]
    public async Task RunAsync_WithAnUnencryptedConnection_WarnsRatherThanFailingAsync()
    {
        var check = await RunAndGetAsync(PasswordPreflightCheck.Encryption, isConnectionEncrypted: false);

        Assert.That(check.State, Is.EqualTo(PasswordPreflightState.Warning));
        Assert.That(check.Message, Does.Contain("not encrypted"));
    }

    /// <summary>
    /// Active Directory refuses a password write over a connection that is neither encrypted nor signed and
    /// sealed, so on those targets an unencrypted channel is very likely to be the thing that stops a password
    /// set. It is still not a failure: a signed and sealed bind satisfies Active Directory without TLS, and JIM
    /// cannot tell from here whether one is in use.
    /// </summary>
    [Test]
    public async Task RunAsync_WithAnUnencryptedConnectionToActiveDirectory_SaysItWillLikelyBeRejectedAsync()
    {
        var check = await RunAndGetAsync(PasswordPreflightCheck.Encryption,
            directoryType: LdapDirectoryType.ActiveDirectory, isConnectionEncrypted: false);

        Assert.That(check.State, Is.EqualTo(PasswordPreflightState.Warning));
        Assert.That(check.Details, Has.Some.Contains("Active Directory refuses"));
    }

    #endregion

    #region password mechanism

    [Test]
    public async Task RunAsync_AgainstActiveDirectory_ReportsTheUnicodePwdMechanismAsAvailableAsync()
    {
        var check = await RunAndGetAsync(PasswordPreflightCheck.PasswordMechanism,
            directoryType: LdapDirectoryType.ActiveDirectory);

        Assert.That(check.State, Is.EqualTo(PasswordPreflightState.Passed));
        Assert.That(check.Message, Does.Contain("unicodePwd"));
    }

    /// <summary>
    /// Active Directory never advertises the RFC 3062 extended operation, so its absence must not be read as a
    /// problem there. Getting this the wrong way round would fail the check on every Active Directory in
    /// existence.
    /// </summary>
    [Test]
    public async Task RunAsync_AgainstActiveDirectoryWithoutTheExtendedOperation_StillPassesAsync()
    {
        var check = await RunAndGetAsync(PasswordPreflightCheck.PasswordMechanism,
            directoryType: LdapDirectoryType.ActiveDirectory, supportsPasswordModifyExtension: false);

        Assert.That(check.State, Is.EqualTo(PasswordPreflightState.Passed));
    }

    [Test]
    public async Task RunAsync_AgainstADirectoryAdvertisingTheExtendedOperation_PassesAsync()
    {
        var check = await RunAndGetAsync(PasswordPreflightCheck.PasswordMechanism,
            directoryType: LdapDirectoryType.OpenLDAP, supportsPasswordModifyExtension: true);

        Assert.That(check.State, Is.EqualTo(PasswordPreflightState.Passed));
        Assert.That(check.Message, Does.Contain("Password Modify"));
    }

    /// <summary>
    /// The one check here that fails outright. JIM will not fall back to writing a password attribute directly,
    /// because a directory stores a directly written value exactly as given rather than applying its configured
    /// hashing, which would leave cleartext passwords in the directory.
    /// </summary>
    [Test]
    public async Task RunAsync_AgainstADirectoryWithoutTheExtendedOperation_FailsAndSaysWhyThereIsNoFallbackAsync()
    {
        var check = await RunAndGetAsync(PasswordPreflightCheck.PasswordMechanism,
            directoryType: LdapDirectoryType.OpenLDAP, supportsPasswordModifyExtension: false);

        Assert.That(check.State, Is.EqualTo(PasswordPreflightState.Failed));
        Assert.That(check.Details, Has.Some.Contains("cleartext"));
    }

    #endregion

    #region reset rights

    /// <summary>
    /// A denial is only ever claimed on evidence. The default executor here answers every read with an empty
    /// result, which is exactly how a directory refuses one, so nothing has been established and the check must
    /// say so.
    /// <para>
    /// This matters more than it looks. The first implementation of this check read Active Directory's
    /// allowedAttributesEffective and looked for unicodePwd, which is computed from a write-permission check
    /// ([MS-ADTS] 3.1.1.4.5.7) while a reset is granted by a control access right ([MS-ADTS] 3.1.1.3.1.5.1). It
    /// therefore denied exactly the least-privileged delegations JIM recommends and passed for Domain Admins.
    /// </para>
    /// </summary>
    [Test]
    public async Task RunAsync_WhenTheDirectoryAnswersNothing_ReportsUndeterminedRatherThanDeniedAsync()
    {
        foreach (var directoryType in Enum.GetValues<LdapDirectoryType>())
        {
            var check = await RunAndGetAsync(PasswordPreflightCheck.ResetRights, directoryType: directoryType,
                supportsPasswordModifyExtension: true, containerExternalIds: ["OU=Staff,DC=testdomain,DC=local"]);

            Assert.That(check.State, Is.EqualTo(PasswordPreflightState.CouldNotDetermine),
                $"Nothing was established for {directoryType}, so the check must report an unknown rather than a verdict.");
        }
    }

    /// <summary>
    /// Only Active Directory publishes what this check needs. Elsewhere the answer is an unknown with a reason,
    /// not a denial.
    /// </summary>
    [Test]
    public async Task RunAsync_AgainstADirectoryThatIsNotActiveDirectory_CannotCheckRightsAtAllAsync()
    {
        var check = await RunAndGetAsync(PasswordPreflightCheck.ResetRights,
            directoryType: LdapDirectoryType.OpenLDAP, supportsPasswordModifyExtension: true,
            containerExternalIds: ["OU=Staff,DC=testdomain,DC=local"]);

        Assert.That(check.State, Is.EqualTo(PasswordPreflightState.CouldNotDetermine));
        Assert.That(check.Details, Has.Some.Contains("no way for a client to ask"));
    }

    [Test]
    public async Task RunAsync_WithSelectedContainers_ReportsEachOneByNameAsync()
    {
        string[] containers = ["OU=Staff,DC=testdomain,DC=local", "OU=Contractors,DC=testdomain,DC=local"];

        var check = await RunAndGetAsync(PasswordPreflightCheck.ResetRights, containerExternalIds: containers);

        Assert.That(check.Details, Has.Some.Contains("OU=Staff,DC=testdomain,DC=local"));
        Assert.That(check.Details, Has.Some.Contains("OU=Contractors,DC=testdomain,DC=local"));
    }

    /// <summary>
    /// With nowhere to check, there is nothing to say. The remedy has to be part of the message, or an unknown is
    /// just a shrug.
    /// </summary>
    [Test]
    public async Task RunAsync_WithNoSelectedContainers_SaysItDoesNotKnowWhereToLookAsync()
    {
        var check = await RunAndGetAsync(PasswordPreflightCheck.ResetRights, containerExternalIds: []);

        Assert.That(check.State, Is.EqualTo(PasswordPreflightState.CouldNotDetermine));
        Assert.That(check.Details, Has.Some.Contains("Partitions and Containers"));
    }



    #endregion

    #region policy discovery

    [Test]
    public async Task RunAsync_WhereTheDomainPolicyCanBeRead_PassesThePolicyCheckAsync()
    {
        _executor.Setup(x => x.SendRequestAsync(It.Is<SearchRequest>(r => r.DistinguishedName == DomainRoot)))
            .ReturnsAsync(LdapTestResponses.SearchResponseWith(DomainRoot,
                (LdapConnectorPasswordPolicy.AttributeMinPwdLength, "12"),
                (LdapConnectorPasswordPolicy.AttributePwdProperties, "1")));

        var check = await RunAndGetAsync(PasswordPreflightCheck.PolicyDiscovery);

        Assert.That(check.State, Is.EqualTo(PasswordPreflightState.Passed));
        Assert.That(check.Details, Has.Some.Contains("Minimum length: 12"));
    }

    /// <summary>
    /// Not a failure: an unreadable policy does not stop a password being set. It means the administrator
    /// configures the generator from what they know, and finds out about a mismatch through a rejection.
    /// </summary>
    [Test]
    public async Task RunAsync_WhereTheDomainPolicyCannotBeRead_ReportsUndeterminedRatherThanFailedAsync()
    {
        var check = await RunAndGetAsync(PasswordPreflightCheck.PolicyDiscovery);

        Assert.That(check.State, Is.EqualTo(PasswordPreflightState.CouldNotDetermine));
    }

    /// <summary>
    /// A directory that publishes no readable policy is not misconfigured, so this must not read as a fault. It
    /// is an unknown with a specific remedy, which the message has to carry.
    /// </summary>
    [Test]
    public async Task RunAsync_AgainstADirectoryThatPublishesNoPolicy_ReportsUndeterminedWithAdviceAsync()
    {
        var check = await RunAndGetAsync(PasswordPreflightCheck.PolicyDiscovery,
            directoryType: LdapDirectoryType.OpenLDAP, supportsPasswordModifyExtension: true);

        Assert.That(check.State, Is.EqualTo(PasswordPreflightState.CouldNotDetermine));
        Assert.That(check.Details, Has.Some.Contains("Synchronisation Rule"));
    }

    /// <summary>
    /// The caveat that matters most on this panel: what JIM read may not be what applies to the account being
    /// provisioned. It has to survive into the preflight, not only the policy display.
    /// </summary>
    [Test]
    public async Task RunAsync_WhereFineGrainedPoliciesCouldNotBeRuledOut_SaysTheDiscoveredPolicyIsAFloorAsync()
    {
        _executor.Setup(x => x.SendRequestAsync(It.Is<SearchRequest>(r => r.DistinguishedName == DomainRoot)))
            .ReturnsAsync(LdapTestResponses.SearchResponseWith(DomainRoot,
                (LdapConnectorPasswordPolicy.AttributeMinPwdLength, "8")));

        var check = await RunAndGetAsync(PasswordPreflightCheck.PolicyDiscovery);

        Assert.That(check.Details, Has.Some.Contains("floor"));
    }

    #endregion

    #region composition

    /// <summary>
    /// Every check the model declares must actually be run, or a gap in the preflight shows up as a silently
    /// shorter list rather than as anything an administrator would notice.
    /// </summary>
    [Test]
    public async Task RunAsync_WhateverTheTarget_ReturnsEveryCheckThatDoesNotDependOnConnectingAsync()
    {
        var preflight = CreatePreflight();

        var results = await preflight.RunAsync([], DomainRoot, CancellationToken.None);

        Assert.That(results.Select(r => r.Check), Is.EquivalentTo(new[]
        {
            PasswordPreflightCheck.Encryption,
            PasswordPreflightCheck.PasswordMechanism,
            PasswordPreflightCheck.ResetRights,
            PasswordPreflightCheck.PolicyDiscovery
        }));
    }

    /// <summary>
    /// An administrator runs a preflight to find out what is wrong, so an unreachable or unconfigured target is
    /// the answer rather than an exception. Throwing here would surface as an unhandled error in the portal and
    /// tell them nothing.
    /// </summary>
    [Test]
    public async Task RunPasswordPreflightAsync_WithSettingsItCannotConnectWith_ReportsRatherThanThrowsAsync()
    {
        using var connector = new LdapConnector();

        var result = await connector.RunPasswordPreflightAsync([], [], Log.Logger, CancellationToken.None);

        Assert.That(result.Outcome, Is.EqualTo(PasswordPreflightOutcome.NotReady));
        Assert.That(result.Checks.Single(c => c.Check == PasswordPreflightCheck.Connection).State,
            Is.EqualTo(PasswordPreflightState.Failed));
    }

    /// <summary>
    /// The checks that were never reached must still appear, as unknowns. Dropping them would leave a result whose
    /// only finding is the connection failure, and an administrator could not tell what else JIM would have looked
    /// at.
    /// </summary>
    [Test]
    public async Task RunPasswordPreflightAsync_WhenItCannotConnect_StillReportsEveryCheckAsUndeterminedAsync()
    {
        using var connector = new LdapConnector();

        var result = await connector.RunPasswordPreflightAsync([], [], Log.Logger, CancellationToken.None);

        Assert.That(result.Checks.Where(c => c.Check != PasswordPreflightCheck.Connection).Select(c => c.State),
            Is.All.EqualTo(PasswordPreflightState.CouldNotDetermine));
        Assert.That(result.Checks, Has.Count.EqualTo(Enum.GetValues<PasswordPreflightCheck>().Length));
    }

    [Test]
    public void RunAsync_WhenCancelled_StopsRatherThanFinishingTheRemainingChecks()
    {
        var preflight = CreatePreflight();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.That(async () => await preflight.RunAsync([], DomainRoot, cancellation.Token),
            Throws.InstanceOf<OperationCanceledException>());
    }

    #endregion
}
