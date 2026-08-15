// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Connectors.LDAP;
using Moq;
using NUnit.Framework;
using Serilog;
using System.DirectoryServices.Protocols;
using static JIM.Worker.Tests.Connectors.Security.SecurityDescriptorTestData;

namespace JIM.Worker.Tests.Connectors.Security;

/// <summary>
/// Covers reading a directory to answer whether the bound account can reset passwords where JIM provisions.
/// <para>
/// The recurring theme is that Active Directory answers "you may not see that" and "there is nothing there" the
/// same way: a successful search returning nothing, or an attribute quietly missing from an entry. Every one of
/// those paths has to surface as an unknown. A denial claimed on the strength of a silence is the failure this
/// whole check was rewritten to avoid.
/// </para>
/// </summary>
[TestFixture]
public class LdapConnectorResetRightsTests
{
    private const string StaffOu = "OU=Staff,DC=testdomain,DC=local";
    private const string ContractorsOu = "OU=Contractors,DC=testdomain,DC=local";
    private const string SampleUser = "CN=Sample User,OU=Staff,DC=testdomain,DC=local";

    private const string ServiceAccount = "S-1-5-21-1111111111-2222222222-3333333333-1104";
    private const string HelpDeskGroup = "S-1-5-21-1111111111-2222222222-3333333333-1105";
    private const string SomebodyElse = "S-1-5-21-1111111111-2222222222-3333333333-9999";

    private Mock<ILdapOperationExecutor> _executor = null!;

    [SetUp]
    public void SetUp() => _executor = new Mock<ILdapOperationExecutor>();

    /// <summary>
    /// The rootDSE read that establishes who JIM is bound as, and every group it belongs to.
    /// </summary>
    private void GivenTheBoundAccountIs(params string[] sids)
    {
        _executor.Setup(x => x.SendRequestAsync(It.Is<SearchRequest>(r => string.IsNullOrEmpty(r.DistinguishedName))))
            .ReturnsAsync(LdapTestResponses.SearchResponseWithBinary("",
                (LdapConnectorResetRights.AttributeTokenGroups, sids.Select(Sid).ToArray()),
                (LdapConnectorResetRights.AttributePrincipalName, [System.Text.Encoding.UTF8.GetBytes("TESTDOMAIN\\jim-svc")])));
    }

    private void GivenTheRootDseReturns(SearchResponse response) =>
        _executor.Setup(x => x.SendRequestAsync(It.Is<SearchRequest>(r => string.IsNullOrEmpty(r.DistinguishedName))))
            .ReturnsAsync(response);

    private void GivenSampleUserIn(string containerDn, byte[]? securityDescriptor)
    {
        var response = securityDescriptor == null
            ? LdapTestResponses.SearchResponseWithBinary(SampleUser)
            : LdapTestResponses.SearchResponseWithBinary(SampleUser,
                (LdapConnectorResetRights.AttributeSecurityDescriptor, [securityDescriptor]));

        _executor.Setup(x => x.SendRequestAsync(It.Is<SearchRequest>(r => r.DistinguishedName == containerDn)))
            .ReturnsAsync(response);
    }

    private void GivenNoUsersIn(string containerDn) =>
        _executor.Setup(x => x.SendRequestAsync(It.Is<SearchRequest>(r => r.DistinguishedName == containerDn)))
            .ReturnsAsync(LdapTestResponses.EmptySearchResponse());

    private async Task<IReadOnlyList<ResetRightsFinding>> CheckAsync(params string[] containers)
    {
        var checker = new LdapConnectorResetRights(_executor.Object, Log.Logger);
        return await checker.CheckAsync(containers, CancellationToken.None);
    }

    private static byte[] GrantingDescriptor(string sid) =>
        SecurityDescriptor(ObjectAce(AccessAllowedObjectAceType, ControlAccess, sid, objectType: ResetPassword));

    #region the answer JIM is after

    [Test]
    public async Task CheckAsync_WhereTheAccountHoldsTheRight_ReportsGrantedAsync()
    {
        GivenTheBoundAccountIs(ServiceAccount, HelpDeskGroup);
        GivenSampleUserIn(StaffOu, GrantingDescriptor(ServiceAccount));

        var findings = await CheckAsync(StaffOu);

        Assert.That(findings, Has.Count.EqualTo(1));
        Assert.That(findings[0].Outcome, Is.EqualTo(ResetRightsOutcome.Granted));
        Assert.That(findings[0].ContainerDn, Is.EqualTo(StaffOu));
    }

    /// <summary>
    /// The delegation is normally made to a group rather than the account, so the group memberships read from the
    /// directory have to actually be used.
    /// </summary>
    [Test]
    public async Task CheckAsync_WhereTheRightComesViaAGroup_ReportsGrantedAsync()
    {
        GivenTheBoundAccountIs(ServiceAccount, HelpDeskGroup);
        GivenSampleUserIn(StaffOu, GrantingDescriptor(HelpDeskGroup));

        var findings = await CheckAsync(StaffOu);

        Assert.That(findings[0].Outcome, Is.EqualTo(ResetRightsOutcome.Granted));
    }

    [Test]
    public async Task CheckAsync_WhereTheAccountDoesNotHoldTheRight_ReportsDeniedAsync()
    {
        GivenTheBoundAccountIs(ServiceAccount);
        GivenSampleUserIn(StaffOu, GrantingDescriptor(SomebodyElse));

        var findings = await CheckAsync(StaffOu);

        Assert.That(findings[0].Outcome, Is.EqualTo(ResetRightsOutcome.Denied));
    }

    /// <summary>
    /// Rights are granted per part of a directory, so the containers are reported separately rather than reduced
    /// to one verdict. "It works in one place and not the other" is the answer an administrator most needs.
    /// </summary>
    [Test]
    public async Task CheckAsync_WithRightsInOneContainerButNotAnother_ReportsEachSeparatelyAsync()
    {
        GivenTheBoundAccountIs(ServiceAccount);
        GivenSampleUserIn(StaffOu, GrantingDescriptor(ServiceAccount));
        _executor.Setup(x => x.SendRequestAsync(It.Is<SearchRequest>(r => r.DistinguishedName == ContractorsOu)))
            .ReturnsAsync(LdapTestResponses.SearchResponseWithBinary("CN=Other,OU=Contractors,DC=testdomain,DC=local",
                (LdapConnectorResetRights.AttributeSecurityDescriptor, [GrantingDescriptor(SomebodyElse)])));

        var findings = await CheckAsync(StaffOu, ContractorsOu);

        Assert.That(findings.Single(f => f.ContainerDn == StaffOu).Outcome, Is.EqualTo(ResetRightsOutcome.Granted));
        Assert.That(findings.Single(f => f.ContainerDn == ContractorsOu).Outcome, Is.EqualTo(ResetRightsOutcome.Denied));
    }

    #endregion

    #region silences that must not become denials

    /// <summary>
    /// Without the group memberships, no denial can be justified: the account might hold the right through a
    /// group JIM never saw.
    /// </summary>
    [Test]
    public async Task CheckAsync_WhenTheGroupMembershipsCannotBeRead_ReportsUndeterminedRatherThanDeniedAsync()
    {
        GivenTheRootDseReturns(LdapTestResponses.SearchResponseWithBinary(""));
        GivenSampleUserIn(StaffOu, GrantingDescriptor(SomebodyElse));

        var findings = await CheckAsync(StaffOu);

        Assert.That(findings[0].Outcome, Is.EqualTo(ResetRightsOutcome.CouldNotDetermine));
    }

    /// <summary>
    /// Active Directory omits tokenGroups entirely when no Global Catalog is reachable, with no error. Reading
    /// that absence as "belongs to no groups" would deny a correctly delegated account.
    /// </summary>
    [Test]
    public async Task CheckAsync_WhenTheRootDseReturnsNothing_ReportsUndeterminedAsync()
    {
        GivenTheRootDseReturns(LdapTestResponses.EmptySearchResponse());
        GivenSampleUserIn(StaffOu, GrantingDescriptor(ServiceAccount));

        var findings = await CheckAsync(StaffOu);

        Assert.That(findings[0].Outcome, Is.EqualTo(ResetRightsOutcome.CouldNotDetermine));
    }

    /// <summary>
    /// A directory refuses to hand over a security descriptor by leaving the attribute off the entry, with a
    /// success result code. That is the single most likely silent failure on this path.
    /// </summary>
    [Test]
    public async Task CheckAsync_WhenTheSecurityDescriptorIsWithheld_ReportsUndeterminedRatherThanDeniedAsync()
    {
        GivenTheBoundAccountIs(ServiceAccount);
        GivenSampleUserIn(StaffOu, securityDescriptor: null);

        var findings = await CheckAsync(StaffOu);

        Assert.That(findings[0].Outcome, Is.EqualTo(ResetRightsOutcome.CouldNotDetermine));
        Assert.That(findings[0].Detail, Does.Contain("did not return"));
    }

    /// <summary>
    /// An empty container, or one JIM has no rights to search, both look like this. Neither says anything about
    /// whether the account could reset a password on an object that was there.
    /// </summary>
    [Test]
    public async Task CheckAsync_WhenNoSampleObjectIsFound_ReportsUndeterminedAsync()
    {
        GivenTheBoundAccountIs(ServiceAccount);
        GivenNoUsersIn(StaffOu);

        var findings = await CheckAsync(StaffOu);

        Assert.That(findings[0].Outcome, Is.EqualTo(ResetRightsOutcome.CouldNotDetermine));
    }

    [Test]
    public async Task CheckAsync_WhenTheSecurityDescriptorCannotBeParsed_ReportsUndeterminedAsync()
    {
        GivenTheBoundAccountIs(ServiceAccount);
        GivenSampleUserIn(StaffOu, [1, 2, 3]);

        var findings = await CheckAsync(StaffOu);

        Assert.That(findings[0].Outcome, Is.EqualTo(ResetRightsOutcome.CouldNotDetermine));
    }

    [Test]
    public async Task CheckAsync_WhenTheDirectoryRefusesTheSearch_ReportsUndeterminedRatherThanThrowingAsync()
    {
        GivenTheBoundAccountIs(ServiceAccount);
        _executor.Setup(x => x.SendRequestAsync(It.Is<SearchRequest>(r => r.DistinguishedName == StaffOu)))
            .ThrowsAsync(new DirectoryOperationException("Insufficient access rights."));

        IReadOnlyList<ResetRightsFinding> findings = [];
        Assert.That(async () => findings = await CheckAsync(StaffOu), Throws.Nothing);

        Assert.That(findings[0].Outcome, Is.EqualTo(ResetRightsOutcome.CouldNotDetermine));
    }

    #endregion

    #region how the directory is asked

    /// <summary>
    /// Without the security descriptor flags control, Active Directory reads the request as also asking for the
    /// audit list, which needs a privilege a least-privileged service account has no reason to hold. It then
    /// omits the whole attribute rather than refusing, so the check would silently see nothing on every object.
    /// </summary>
    [Test]
    public async Task CheckAsync_AsksForTheSecurityDescriptorWithoutTheAuditPortionAsync()
    {
        GivenTheBoundAccountIs(ServiceAccount);
        GivenSampleUserIn(StaffOu, GrantingDescriptor(ServiceAccount));

        await CheckAsync(StaffOu);

        _executor.Verify(x => x.SendRequestAsync(It.Is<SearchRequest>(r =>
            r.DistinguishedName == StaffOu &&
            r.Controls.OfType<SecurityDescriptorFlagControl>().Any(c =>
                c.SecurityMasks == (SecurityMasks.Owner | SecurityMasks.Group | SecurityMasks.Dacl)))), Times.Once);
    }

    /// <summary>
    /// Accounts protected by AdminSDHolder have their access control list overwritten on a timer and inheritance
    /// switched off, so a delegation made on the container does not apply to them. Sampling one would report the
    /// container as denied when every ordinary user in it is fine.
    /// </summary>
    [Test]
    public async Task CheckAsync_DoesNotSampleAccountsProtectedByAdminSdHolderAsync()
    {
        GivenTheBoundAccountIs(ServiceAccount);
        GivenSampleUserIn(StaffOu, GrantingDescriptor(ServiceAccount));

        await CheckAsync(StaffOu);

        _executor.Verify(x => x.SendRequestAsync(It.Is<SearchRequest>(r =>
            r.DistinguishedName == StaffOu && r.Filter.ToString()!.Contains("adminCount"))), Times.Once);
    }

    #endregion
}
