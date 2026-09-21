// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Connectors.LDAP;
using JIM.Models.Exceptions;
using Moq;
using NUnit.Framework;
using Serilog;
using System.DirectoryServices.Protocols;
using static JIM.Worker.Tests.Connectors.Security.SecurityDescriptorTestData;

namespace JIM.Worker.Tests.Connectors.Security;

/// <summary>
/// Covers reading a directory to answer whether the bound account can list a partition's Deleted Objects
/// container, which is what a Delta Import needs in order to see deletions at all.
/// <para>
/// A search of that container by an account without rights over it is not refused; it succeeds with no rows, so
/// the import would report nothing was deleted. The check reads the container's own permissions instead and
/// evaluates them. As with the reset-password check, every silence the directory offers has to surface as an
/// unknown, never as a denial.
/// </para>
/// </summary>
[TestFixture]
public class LdapConnectorDeletedObjectsAccessTests
{
    private const string NamingContext = "DC=testdomain,DC=local";
    private const string DeletedObjectsDn = "CN=Deleted Objects,DC=testdomain,DC=local";

    private const string ServiceAccount = "S-1-5-21-1111111111-2222222222-3333333333-1104";
    private const string SyncGroup = "S-1-5-21-1111111111-2222222222-3333333333-1105";
    private const string SomebodyElse = "S-1-5-21-1111111111-2222222222-3333333333-9999";

    private Mock<ILdapOperationExecutor> _executor = null!;

    [SetUp]
    public void SetUp()
    {
        _executor = new Mock<ILdapOperationExecutor>();
        GivenTheBoundAccountIs(ServiceAccount, SyncGroup);
    }

    /// <summary>
    /// The rootDSE read that establishes who JIM is bound as, and every group it belongs to.
    /// </summary>
    private void GivenTheBoundAccountIs(params string[] sids) =>
        GivenTheRootDseReturns(LdapTestResponses.SearchResponseWithBinary("",
            (LdapCallerSecurityContext.AttributeTokenGroups, sids.Select(Sid).ToArray()),
            (LdapCallerSecurityContext.AttributePrincipalName, [System.Text.Encoding.UTF8.GetBytes("TESTDOMAIN\\jim-svc")])));

    private void GivenTheRootDseReturns(SearchResponse response) =>
        _executor.Setup(x => x.SendRequestAsync(It.Is<SearchRequest>(r => string.IsNullOrEmpty(r.DistinguishedName))))
            .ReturnsAsync(response);

    private void GivenTheContainerHas(byte[]? securityDescriptor)
    {
        var response = securityDescriptor == null
            ? LdapTestResponses.SearchResponseWithBinary(DeletedObjectsDn)
            : LdapTestResponses.SearchResponseWithBinary(DeletedObjectsDn,
                (LdapConnectorResetRights.AttributeSecurityDescriptor, [securityDescriptor]));

        _executor.Setup(x => x.SendRequestAsync(It.Is<SearchRequest>(r => r.DistinguishedName == DeletedObjectsDn)))
            .ReturnsAsync(response);
    }

    private void GivenTheContainerSearchThrows(Exception exception) =>
        _executor.Setup(x => x.SendRequestAsync(It.Is<SearchRequest>(r => r.DistinguishedName == DeletedObjectsDn)))
            .ThrowsAsync(exception);

    private async Task<DeletedObjectsAccessFinding> CheckAsync()
    {
        var checker = new LdapConnectorDeletedObjectsAccess(_executor.Object, Log.Logger);
        return await checker.CheckAsync(NamingContext, CancellationToken.None);
    }

    private static DeletedObjectsAccessFinding Finding(DeletedObjectsAccessOutcome outcome, string detail = "some detail") =>
        Finding(outcome, NamingContext, detail);

    private static DeletedObjectsAccessFinding Finding(DeletedObjectsAccessOutcome outcome, string namingContext, string detail) => new()
    {
        NamingContext = namingContext,
        ContainerDn = LdapConnectorDeletedObjectsAccess.ContainerDnFor(namingContext),
        Outcome = outcome,
        Detail = detail
    };

    #region how the directory is asked

    [Test]
    public void ContainerDnFor_ANamingContext_PrependsTheDeletedObjectsRdn() =>
        Assert.That(LdapConnectorDeletedObjectsAccess.ContainerDnFor(NamingContext), Is.EqualTo(DeletedObjectsDn));

    /// <summary>
    /// The container's entry is hidden from an ordinary search, so the request needs the Show Deleted Objects
    /// control, marked critical so a directory that does not honour it refuses rather than quietly answering for
    /// the wrong object. Without the security descriptor flags control, the directory reads the request as also
    /// asking for the audit list and omits the whole attribute rather than refusing.
    /// </summary>
    [Test]
    public async Task CheckAsync_AsksForTheContainersSecurityDescriptorWithBothControlsAsync()
    {
        GivenTheContainerHas(SecurityDescriptor(Ace(AccessAllowedAceType, ListContents, SyncGroup)));

        await CheckAsync();

        _executor.Verify(x => x.SendRequestAsync(It.Is<SearchRequest>(r =>
            r.DistinguishedName == DeletedObjectsDn &&
            r.Scope == SearchScope.Base &&
            r.Attributes.Contains(LdapConnectorResetRights.AttributeSecurityDescriptor) &&
            r.Controls.OfType<DirectoryControl>().Any(c =>
                c.Type == LdapConnectorConstants.LDAP_SERVER_SHOW_DELETED_OID && c.IsCritical) &&
            r.Controls.OfType<SecurityDescriptorFlagControl>().Any(c =>
                c.SecurityMasks == (SecurityMasks.Owner | SecurityMasks.Group | SecurityMasks.Dacl)))), Times.Once);
    }

    #endregion

    #region the answer JIM is after

    [Test]
    public async Task CheckAsync_WhereTheGroupIsAllowedListContents_ReportsGrantedAsync()
    {
        GivenTheContainerHas(SecurityDescriptor(Ace(AccessAllowedAceType, ListContents, SyncGroup)));

        var finding = await CheckAsync();

        Assert.That(finding.Outcome, Is.EqualTo(DeletedObjectsAccessOutcome.Granted));
        Assert.That(finding.NamingContext, Is.EqualTo(NamingContext));
        Assert.That(finding.ContainerDn, Is.EqualTo(DeletedObjectsDn));
    }

    [Test]
    public async Task CheckAsync_WhereListContentsAndReadPropertyAreBothGranted_SaysSoInTheDetailAsync()
    {
        GivenTheContainerHas(SecurityDescriptor(Ace(AccessAllowedAceType, ListContents | ReadProperty, SyncGroup)));

        var finding = await CheckAsync();

        Assert.That(finding.Outcome, Is.EqualTo(DeletedObjectsAccessOutcome.Granted));
        Assert.That(finding.Detail, Is.EqualTo("List Contents and Read Property are both granted"));
    }

    /// <summary>
    /// List Contents alone finds the tombstones; without Read Property their attributes may come back empty, so
    /// the detail says so rather than letting a partial grant read as a full one.
    /// </summary>
    [Test]
    public async Task CheckAsync_WhereOnlyListContentsIsGranted_SaysReadPropertyIsMissingInTheDetailAsync()
    {
        GivenTheContainerHas(SecurityDescriptor(Ace(AccessAllowedAceType, ListContents, SyncGroup)));

        var finding = await CheckAsync();

        Assert.That(finding.Outcome, Is.EqualTo(DeletedObjectsAccessOutcome.Granted));
        Assert.That(finding.Detail, Is.EqualTo("List Contents is granted; Read Property is not, so tombstone attributes may not be readable"));
    }

    [Test]
    public async Task CheckAsync_WhereListContentsIsExplicitlyDenied_ReportsDeniedAsync()
    {
        GivenTheContainerHas(SecurityDescriptor(
            Ace(AccessDeniedAceType, ListContents, SyncGroup),
            Ace(AccessAllowedAceType, ListContents | ReadProperty, SyncGroup)));

        var finding = await CheckAsync();

        Assert.That(finding.Outcome, Is.EqualTo(DeletedObjectsAccessOutcome.Denied));
        Assert.That(finding.Detail, Is.EqualTo("List Contents is not granted"));
    }

    [Test]
    public async Task CheckAsync_WhereOnlyUnrelatedRightsAreGranted_ReportsDeniedAsync()
    {
        GivenTheContainerHas(SecurityDescriptor(
            Ace(AccessAllowedAceType, ReadProperty | WriteProperty, SyncGroup),
            Ace(AccessAllowedAceType, ListContents, SomebodyElse)));

        var finding = await CheckAsync();

        Assert.That(finding.Outcome, Is.EqualTo(DeletedObjectsAccessOutcome.Denied));
    }

    /// <summary>
    /// An object entry whose ObjectType is the container class addresses the container as a whole ([MS-ADTS]
    /// 5.1.3.3.3), so the check has to tell the evaluator what class the container is, or a correctly delegated
    /// account would be told to grant a right it already holds.
    /// </summary>
    [Test]
    public async Task CheckAsync_WhereListContentsIsGrantedByAnEntryScopedToTheContainerClass_ReportsGrantedAsync()
    {
        GivenTheContainerHas(SecurityDescriptor(
            ObjectAce(AccessAllowedObjectAceType, ListContents, SyncGroup, objectType: ContainerClass)));

        var finding = await CheckAsync();

        Assert.That(finding.Outcome, Is.EqualTo(DeletedObjectsAccessOutcome.Granted));
    }

    /// <summary>
    /// A Read Property entry scoped to one attribute says nothing about listing the container, so it must not be
    /// read as a grant.
    /// </summary>
    [Test]
    public async Task CheckAsync_WhereOnlyAPropertyScopedReadPropertyEntryExists_ReportsDeniedAsync()
    {
        GivenTheContainerHas(SecurityDescriptor(
            ObjectAce(AccessAllowedObjectAceType, ReadProperty, SyncGroup, objectType: DescriptionProperty)));

        var finding = await CheckAsync();

        Assert.That(finding.Outcome, Is.EqualTo(DeletedObjectsAccessOutcome.Denied));
    }

    #endregion

    #region silences that must not become denials

    [Test]
    public async Task CheckAsync_WhenTheGroupMembershipsCannotBeRead_ReportsUndeterminedRatherThanDeniedAsync()
    {
        GivenTheRootDseReturns(LdapTestResponses.SearchResponseWithBinary(""));
        GivenTheContainerHas(SecurityDescriptor(Ace(AccessAllowedAceType, ListContents, SomebodyElse)));

        var finding = await CheckAsync();

        Assert.That(finding.Outcome, Is.EqualTo(DeletedObjectsAccessOutcome.CouldNotDetermine));
        Assert.That(finding.Detail, Is.EqualTo("JIM could not read which groups the account it connects as belongs to"));
    }

    [Test]
    public async Task CheckAsync_WhenTheSearchReturnsNoEntry_ReportsUndeterminedAsync()
    {
        _executor.Setup(x => x.SendRequestAsync(It.Is<SearchRequest>(r => r.DistinguishedName == DeletedObjectsDn)))
            .ReturnsAsync(LdapTestResponses.EmptySearchResponse());

        var finding = await CheckAsync();

        Assert.That(finding.Outcome, Is.EqualTo(DeletedObjectsAccessOutcome.CouldNotDetermine));
        Assert.That(finding.Detail, Is.EqualTo("the directory did not return the container's entry"));
    }

    /// <summary>
    /// A directory withholds a security descriptor by leaving the attribute off the entry, with a success result
    /// code. Read Permissions on the container is what makes it visible.
    /// </summary>
    [Test]
    public async Task CheckAsync_WhenTheEntryLacksTheSecurityDescriptor_ReportsUndeterminedRatherThanDeniedAsync()
    {
        GivenTheContainerHas(securityDescriptor: null);

        var finding = await CheckAsync();

        Assert.That(finding.Outcome, Is.EqualTo(DeletedObjectsAccessOutcome.CouldNotDetermine));
        Assert.That(finding.Detail, Is.EqualTo("the directory did not return the container's permissions, which needs Read Permissions on it"));
    }

    [Test]
    public async Task CheckAsync_WhenTheSecurityDescriptorCannotBeParsed_ReportsUndeterminedAsync()
    {
        GivenTheContainerHas([1, 2, 3]);

        var finding = await CheckAsync();

        Assert.That(finding.Outcome, Is.EqualTo(DeletedObjectsAccessOutcome.CouldNotDetermine));
        Assert.That(finding.Detail, Does.Contain("permissions"));
    }

    [Test]
    public async Task CheckAsync_WhenTheDirectoryRefusesTheRead_ReportsUndeterminedRatherThanThrowingAsync()
    {
        GivenTheContainerSearchThrows(new DirectoryOperationException("Insufficient access rights."));

        DeletedObjectsAccessFinding? finding = null;
        Assert.That(async () => finding = await CheckAsync(), Throws.Nothing);

        Assert.That(finding!.Outcome, Is.EqualTo(DeletedObjectsAccessOutcome.CouldNotDetermine));
        Assert.That(finding.Detail, Is.EqualTo("the directory answered: Insufficient access rights."));
    }

    [Test]
    public async Task CheckAsync_WhenTheConnectionFails_ReportsUndeterminedRatherThanThrowingAsync()
    {
        GivenTheContainerSearchThrows(new LdapException("The server is unavailable."));

        DeletedObjectsAccessFinding? finding = null;
        Assert.That(async () => finding = await CheckAsync(), Throws.Nothing);

        Assert.That(finding!.Outcome, Is.EqualTo(DeletedObjectsAccessOutcome.CouldNotDetermine));
        Assert.That(finding.Detail, Is.EqualTo("the directory answered: The server is unavailable."));
    }

    #endregion

    #region what the administrator is told

    [Test]
    public void DescribeForDeltaImport_WhenDenied_NamesTheContainerAndTheThreeRights()
    {
        var text = LdapConnectorDeletedObjectsAccess.DescribeForDeltaImport(Finding(DeletedObjectsAccessOutcome.Denied));

        Assert.That(text, Is.EqualTo(
            $"Deletions cannot be detected: the account JIM connects as is not allowed to list the Deleted Objects container ({DeletedObjectsDn}), so objects deleted in the directory would stay in JIM. Grant the account List Contents, Read Property and Read Permissions on that container (see the LDAP Connector documentation, Service Account Permissions), or run a Full Import, which detects deletions by absence."));
    }

    [Test]
    public void DescribeForSchemaDiscovery_WhenDenied_NamesTheContainerAndTheThreeRights()
    {
        var text = LdapConnectorDeletedObjectsAccess.DescribeForSchemaDiscovery(Finding(DeletedObjectsAccessOutcome.Denied));

        Assert.That(text, Is.EqualTo(
            $"The account JIM connects as is not allowed to list the Deleted Objects container ({DeletedObjectsDn}). Delta Imports from this domain will refuse to run until it is granted List Contents, Read Property and Read Permissions on that container; the LDAP Connector documentation, under Service Account Permissions, gives the commands."));
    }

    [Test]
    public void DescribeForDeltaImport_WhenUndetermined_NamesTheContainerTheDetailAndTheThreeRights()
    {
        var text = LdapConnectorDeletedObjectsAccess.DescribeForDeltaImport(Finding(DeletedObjectsAccessOutcome.CouldNotDetermine, "the directory did not return the container's entry"));

        Assert.That(text, Is.EqualTo(
            $"JIM could not confirm that the account it connects as can list the Deleted Objects container ({DeletedObjectsDn}): the directory did not return the container's entry. If it cannot, Delta Imports from this domain import no deletions. Granting Read Permissions on that container alongside List Contents and Read Property lets JIM give a definite answer."));
    }

    [Test]
    public void DescribeForSchemaDiscovery_WhenUndetermined_ReadsTheSameAsTheDeltaImportText()
    {
        var finding = Finding(DeletedObjectsAccessOutcome.CouldNotDetermine, "the directory answered: Insufficient access rights.");

        var text = LdapConnectorDeletedObjectsAccess.DescribeForSchemaDiscovery(finding);

        Assert.That(text, Is.EqualTo(LdapConnectorDeletedObjectsAccess.DescribeForDeltaImport(finding)));
        Assert.That(text, Does.Contain(DeletedObjectsDn).And.Contain(finding.Detail)
            .And.Contain("List Contents").And.Contain("Read Property").And.Contain("Read Permissions"));
    }

    /// <summary>
    /// A grant has nothing to tell the administrator; a caller that asks for text for one has taken a wrong
    /// turn, and should hear so loudly rather than get an empty string to display.
    /// </summary>
    [Test]
    public void DescribeForDeltaImport_WhenGranted_Throws() =>
        Assert.That(() => LdapConnectorDeletedObjectsAccess.DescribeForDeltaImport(Finding(DeletedObjectsAccessOutcome.Granted)),
            Throws.InvalidOperationException);

    [Test]
    public void DescribeForSchemaDiscovery_WhenGranted_Throws() =>
        Assert.That(() => LdapConnectorDeletedObjectsAccess.DescribeForSchemaDiscovery(Finding(DeletedObjectsAccessOutcome.Granted)),
            Throws.InvalidOperationException);

    #endregion

    #region what a Delta Import does with the findings for every partition

    private const string OtherNamingContext = "DC=child,DC=testdomain,DC=local";
    private const string OtherDeletedObjectsDn = "CN=Deleted Objects,DC=child,DC=testdomain,DC=local";

    private static DeletionDetectionNotes Summarise(params DeletedObjectsAccessFinding[] findings) =>
        LdapConnectorDeletedObjectsAccess.SummariseForDeltaImport(findings, Log.Logger);

    [Test]
    public void SummariseForDeltaImport_OneDeniedAmongGranted_ThrowsNamingThatContainer()
    {
        var findings = new[]
        {
            Finding(DeletedObjectsAccessOutcome.Granted, NamingContext, "List Contents and Read Property are both granted"),
            Finding(DeletedObjectsAccessOutcome.Denied, OtherNamingContext, "List Contents is not granted")
        };

        Assert.That(() => Summarise(findings), Throws.TypeOf<CannotPerformDeltaImportException>()
            .With.Message.Contains(OtherDeletedObjectsDn)
            .And.Message.Not.Contains(DeletedObjectsDn));
    }

    /// <summary>
    /// Every denied partition is named in the one failure, so the administrator fixes them all in one go rather
    /// than one per failed run.
    /// </summary>
    [Test]
    public void SummariseForDeltaImport_TwoDenied_NamesBothContainers()
    {
        var findings = new[]
        {
            Finding(DeletedObjectsAccessOutcome.Denied, NamingContext, "List Contents is not granted"),
            Finding(DeletedObjectsAccessOutcome.Denied, OtherNamingContext, "List Contents is not granted")
        };

        Assert.That(() => Summarise(findings), Throws.TypeOf<CannotPerformDeltaImportException>()
            .With.Message.Contains(DeletedObjectsDn)
            .And.Message.Contains(OtherDeletedObjectsDn));
    }

    /// <summary>
    /// A proven denial stops the run, and the failure speaks only to what was proven: an unknown for another
    /// partition is not folded into a message about a denial.
    /// </summary>
    [Test]
    public void SummariseForDeltaImport_OneDeniedAndOneUndetermined_ThrowsWithoutTheUndeterminedText()
    {
        var findings = new[]
        {
            Finding(DeletedObjectsAccessOutcome.CouldNotDetermine, NamingContext, "the directory did not return the container's entry"),
            Finding(DeletedObjectsAccessOutcome.Denied, OtherNamingContext, "List Contents is not granted")
        };

        Assert.That(() => Summarise(findings), Throws.TypeOf<CannotPerformDeltaImportException>()
            .With.Message.Contains(OtherDeletedObjectsDn)
            .And.Message.Not.Contains("could not confirm"));
    }

    [Test]
    public void SummariseForDeltaImport_UndeterminedOnly_ReturnsAWarningWithoutThrowing()
    {
        DeletionDetectionNotes? notes = null;
        Assert.That(() => notes = Summarise(
            Finding(DeletedObjectsAccessOutcome.CouldNotDetermine, NamingContext, "the directory did not return the container's entry")), Throws.Nothing);

        Assert.That(notes!.Warning, Is.EqualTo(LdapConnectorDeletedObjectsAccess.DescribeForDeltaImport(
            Finding(DeletedObjectsAccessOutcome.CouldNotDetermine, NamingContext, "the directory did not return the container's entry"))));
    }

    [Test]
    public void SummariseForDeltaImport_TwoUndetermined_WarnsAboutBothContainers()
    {
        var notes = Summarise(
            Finding(DeletedObjectsAccessOutcome.CouldNotDetermine, NamingContext, "the directory did not return the container's entry"),
            Finding(DeletedObjectsAccessOutcome.CouldNotDetermine, OtherNamingContext, "the directory answered: Insufficient access rights."));

        Assert.That(notes.Warning, Does.Contain(DeletedObjectsDn).And.Contain("did not return the container's entry")
            .And.Contain(OtherDeletedObjectsDn).And.Contain("Insufficient access rights."));
    }

    /// <summary>
    /// Null rather than empty: the connector chains this warning with the pinning note using null-coalescing, so
    /// an empty string would both hide that note and put a blank warning on the Activity.
    /// </summary>
    [Test]
    public void SummariseForDeltaImport_AllGranted_ReturnsNoWarning()
    {
        var notes = Summarise(
            Finding(DeletedObjectsAccessOutcome.Granted, NamingContext, "List Contents and Read Property are both granted"),
            Finding(DeletedObjectsAccessOutcome.Granted, OtherNamingContext, "List Contents and Read Property are both granted"));

        Assert.That(notes.Warning, Is.Null);
    }

    [Test]
    public void SummariseForDeltaImport_NoFindings_ReturnsNoWarning() =>
        Assert.That(Summarise().Warning, Is.Null);

    #endregion

    #region notes gathered as the import runs

    /// <summary>
    /// The tombstone search is refused after the up-front check could only say it was unsure about the same
    /// container. The refusal is the stronger evidence, and it is what the administrator has to see: the earlier
    /// note asked for a right to be granted, where the run in fact knows no deletions were detected and why.
    /// </summary>
    [Test]
    public void Record_ARefusalForAContainerAlreadyNotedAsUndetermined_ReplacesTheUndeterminedNote()
    {
        var notes = Summarise(
            Finding(DeletedObjectsAccessOutcome.CouldNotDetermine, NamingContext, "the directory did not return the container's permissions, which needs Read Permissions on it"));

        notes.Record(DeletedObjectsDn, LdapConnectorDeletedObjectsAccess.DescribeRefusedSearch(DeletedObjectsDn, "Unavailable critical extension"));

        Assert.That(notes.Warning, Does.Contain("refused the search").And.Contain("Unavailable critical extension")
            .And.Not.Contain("could not confirm"));
    }

    [Test]
    public void Record_ARefusalForEachOfTwoContainers_KeepsBoth()
    {
        var notes = new DeletionDetectionNotes();

        notes.Record(DeletedObjectsDn, LdapConnectorDeletedObjectsAccess.DescribeRefusedSearch(DeletedObjectsDn, "Unavailable critical extension"));
        notes.Record(OtherDeletedObjectsDn, LdapConnectorDeletedObjectsAccess.DescribeMissingContainer(OtherDeletedObjectsDn));

        Assert.That(notes.Warning, Does.Contain(DeletedObjectsDn).And.Contain("refused the search")
            .And.Contain(OtherDeletedObjectsDn).And.Contain("was not found"));
    }

    /// <summary>
    /// An unknown for one partition and a refusal for another are about different containers, so both notes stand.
    /// </summary>
    [Test]
    public void Record_ARefusalForAnotherContainerThanTheUndeterminedOne_KeepsBothNotes()
    {
        var notes = Summarise(
            Finding(DeletedObjectsAccessOutcome.CouldNotDetermine, NamingContext, "the directory did not return the container's entry"));

        notes.Record(OtherDeletedObjectsDn, LdapConnectorDeletedObjectsAccess.DescribeRefusedSearch(OtherDeletedObjectsDn, "Unavailable critical extension"));

        Assert.That(notes.Warning, Does.Contain("could not confirm").And.Contain(DeletedObjectsDn)
            .And.Contain("refused the search").And.Contain(OtherDeletedObjectsDn));
    }

    [Test]
    public void Warning_WithNothingRecorded_IsNull() =>
        Assert.That(new DeletionDetectionNotes().Warning, Is.Null);

    [Test]
    public void DescribeRefusedSearch_NamesTheContainerTheReasonAndTheConsequence() =>
        Assert.That(LdapConnectorDeletedObjectsAccess.DescribeRefusedSearch(DeletedObjectsDn, "Unavailable critical extension"), Is.EqualTo(
            $"Deletions were not detected in {DeletedObjectsDn}: the directory refused the search (Unavailable critical extension). Objects deleted since the last import may still be present in JIM."));

    [Test]
    public void DescribeMissingContainer_NamesTheContainerAndTheConsequence() =>
        Assert.That(LdapConnectorDeletedObjectsAccess.DescribeMissingContainer(DeletedObjectsDn), Is.EqualTo(
            $"Deletions were not detected in {DeletedObjectsDn}: the container was not found. Objects deleted since the last import may still be present in JIM."));

    #endregion
}
