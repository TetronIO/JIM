// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Connectors.LDAP;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Exceptions;
using JIM.Models.Staging;
using Moq;
using NUnit.Framework;
using Serilog;
using System.DirectoryServices.Protocols;
using System.Text;
using static JIM.Worker.Tests.Connectors.Security.SecurityDescriptorTestData;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// The change source a Delta Import reads through against Active Directory and Samba AD: uSNChanged for what
/// changed, the Deleted Objects container for what was deleted, and the domain controller's invocationId for
/// whether the watermark still means anything. Extracted from the import shell as a pure move, so what these
/// prove is that the source asks the directory the same questions the shell used to and hands the same answers
/// back through <see cref="ILdapDeltaImportHost"/>.
/// </summary>
[TestFixture]
public class LdapUsnDeltaSourceTests
{
    private const string PartitionDn = "DC=corp,DC=local";
    private const string ContainerDn = "OU=Users,DC=corp,DC=local";
    private const string DeletedObjectsDn = "CN=Deleted Objects,DC=corp,DC=local";
    private const string NtdsSettingsDn = "CN=NTDS Settings,CN=DC1,CN=Servers,CN=Default-First-Site-Name,CN=Sites,CN=Configuration,DC=corp,DC=local";
    private const string ServiceAccount = "S-1-5-21-1111111111-2222222222-3333333333-1104";
    private const long Watermark = 42;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private static readonly byte[] Cookie = [1, 2, 3, 4];
    private static readonly byte[] DeletedObjectsCookie = [9, 8, 7];

    private Mock<ILdapOperationExecutor> _executor = null!;
    private Mock<ILdapDeltaImportHost> _host = null!;
    private LdapDeltaSourceNotes _notes = null!;
    private List<SearchRequest> _sent = null!;
    private List<(string Phase, string Message)> _phases = null!;
    private List<int> _reported = null!;
    private List<(int EntryCount, ObjectChangeType ChangeType, string? ObjectType)> _conversions = null!;
    private Dictionary<string, Func<SearchRequest, SearchResponse>> _answers = null!;

    [SetUp]
    public void SetUp()
    {
        _sent = [];
        _phases = [];
        _reported = [];
        _conversions = [];
        _notes = new LdapDeltaSourceNotes();
        _answers = new Dictionary<string, Func<SearchRequest, SearchResponse>>(StringComparer.OrdinalIgnoreCase);

        _executor = new Mock<ILdapOperationExecutor>();
        _executor.Setup(x => x.SendRequest(It.IsAny<DirectoryRequest>(), It.IsAny<TimeSpan>()))
            .Returns((DirectoryRequest request, TimeSpan _) =>
            {
                var search = (SearchRequest)request;
                _sent.Add(search);
                return _answers.TryGetValue(search.DistinguishedName, out var answer)
                    ? answer(search)
                    : LdapTestResponses.EmptySearchResponse();
            });

        _host = new Mock<ILdapDeltaImportHost>();
        _host.Setup(h => h.EnterPhaseAsync(It.IsAny<string>(), It.IsAny<string>()))
            .Callback<string, string>((phase, message) => _phases.Add((phase, message)))
            .Returns(Task.CompletedTask);
        _host.Setup(h => h.ReportObjectsReadAsync(It.IsAny<int>()))
            .Callback<int>(count => _reported.Add(count))
            .Returns(Task.CompletedTask);
        _host.Setup(h => h.ConvertEntries(It.IsAny<SearchResultEntryCollection>(), It.IsAny<ObjectChangeType>(), It.IsAny<ConnectedSystemObjectType?>()))
            .Returns((SearchResultEntryCollection entries, ObjectChangeType changeType, ConnectedSystemObjectType? objectType) =>
            {
                _conversions.Add((entries.Count, changeType, objectType?.Name));
                return entries.Cast<SearchResultEntry>()
                    .Select(_ => new ConnectedSystemImportObject { ObjectType = objectType?.Name ?? "unknown", ChangeType = changeType })
                    .ToList();
            });
    }

    private LdapUsnDeltaSource Source() => new(_executor.Object, Log.Logger);

    #region CaptureWatermarkAsync

    [Test]
    public async Task CaptureWatermarkAsync_RootDseCarriesHighestCommittedUsn_RecordsItAsync()
    {
        var rootDse = new LdapConnectorRootDse { DirectoryType = LdapDirectoryType.ActiveDirectory };

        await Source().CaptureWatermarkAsync(LdapTestResponses.Entry("", ("HighestCommittedUSN", "123456")), rootDse, Timeout);

        Assert.That(rootDse.HighestCommittedUsn, Is.EqualTo(123456));
    }

    [Test]
    public async Task CaptureWatermarkAsync_RootDseNamesDsServiceName_ReadsInvocationIdFromTheNtdsSettingsObjectAsync()
    {
        var invocationId = Guid.NewGuid();
        _answers[NtdsSettingsDn] = _ => LdapTestResponses.SearchResponseWithBinary(NtdsSettingsDn, ("invocationId", [invocationId.ToByteArray()]));
        var rootDse = new LdapConnectorRootDse { DirectoryType = LdapDirectoryType.ActiveDirectory };

        await Source().CaptureWatermarkAsync(LdapTestResponses.Entry("", ("HighestCommittedUSN", "1"), ("dsServiceName", NtdsSettingsDn)), rootDse, Timeout);

        var ntdsRead = _sent.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(rootDse.InvocationId, Is.EqualTo(invocationId));
            Assert.That(ntdsRead.DistinguishedName, Is.EqualTo(NtdsSettingsDn));
            Assert.That(ntdsRead.Scope, Is.EqualTo(SearchScope.Base));
            Assert.That(ntdsRead.Attributes.Cast<string>(), Is.EqualTo(new[] { "invocationId" }));
        }
        _executor.Verify(x => x.SendRequest(It.IsAny<DirectoryRequest>(), Timeout), Times.Once, "the read honours the import's search timeout");
    }

    [Test]
    public async Task CaptureWatermarkAsync_NtdsSettingsReadRefused_LeavesInvocationIdNullWithoutFailingAsync()
    {
        _answers[NtdsSettingsDn] = _ => throw new DirectoryOperationException(
            LdapTestResponses.Create<SearchResponse>(ResultCode.InsufficientAccessRights), "insufficient access rights");
        var rootDse = new LdapConnectorRootDse { DirectoryType = LdapDirectoryType.ActiveDirectory };

        await Source().CaptureWatermarkAsync(LdapTestResponses.Entry("", ("HighestCommittedUSN", "1"), ("dsServiceName", NtdsSettingsDn)), rootDse, Timeout);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(rootDse.InvocationId, Is.Null, "identity unknown is not a failure; the import goes on without the guard");
            Assert.That(rootDse.HighestCommittedUsn, Is.EqualTo(1));
        }
    }

    [Test]
    public async Task CaptureWatermarkAsync_RootDseLacksDsServiceName_LeavesInvocationIdNullAsync()
    {
        var rootDse = new LdapConnectorRootDse { DirectoryType = LdapDirectoryType.ActiveDirectory };

        await Source().CaptureWatermarkAsync(LdapTestResponses.Entry("", ("HighestCommittedUSN", "1")), rootDse, Timeout);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(rootDse.InvocationId, Is.Null);
            Assert.That(_sent, Is.Empty, "with no NTDS Settings DN there is nothing to read");
        }
    }

    #endregion

    #region VerifyContinuity

    [Test]
    public void VerifyContinuity_InvocationIdChanged_ThrowsCannotPerformDeltaImportException()
    {
        var previous = new LdapConnectorRootDse { InvocationId = Guid.NewGuid(), DnsHostName = "dc1.corp.local" };
        var current = new LdapConnectorRootDse { InvocationId = Guid.NewGuid(), DnsHostName = "dc1.corp.local" };

        Assert.That(() => Source().VerifyContinuity(previous, current),
            Throws.TypeOf<CannotPerformDeltaImportException>().With.Message.Contains("invocationId has changed"));
    }

    [Test]
    public void VerifyContinuity_InvocationIdUnchanged_DoesNotThrow()
    {
        var invocationId = Guid.NewGuid();
        var previous = new LdapConnectorRootDse { InvocationId = invocationId, DnsHostName = "dc1.corp.local" };
        var current = new LdapConnectorRootDse { InvocationId = invocationId, DnsHostName = "dc2.corp.local" };

        Assert.That(() => Source().VerifyContinuity(previous, current), Throws.Nothing);
    }

    #endregion

    #region VerifyReadinessAsync

    [Test]
    public async Task VerifyReadinessAsync_ContainerDenied_ReportsUnavailableWithTheDeltaImportAndSchemaDiscoveryTextsAsync()
    {
        GivenTheBoundAccountIs(ServiceAccount);
        GivenTheDeletedObjectsContainerOf(PartitionDn, SecurityDescriptor(Ace(AccessDeniedAceType, ListContents, ServiceAccount)));

        var findings = await Source().VerifyReadinessAsync(new LdapConnectorRootDse(), [PartitionDn], CancellationToken.None);

        var finding = findings.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(finding.Subject, Is.EqualTo(DeletedObjectsDn));
            Assert.That(finding.Outcome, Is.EqualTo(LdapDeltaSourceOutcome.Unavailable));
            Assert.That(finding.Detail, Is.EqualTo("List Contents is not granted"));
            Assert.That(finding.DeltaImportText, Does.StartWith("Deletions cannot be detected:").And.Contains(DeletedObjectsDn));
            Assert.That(finding.SchemaDiscoveryText, Does.StartWith("The account JIM connects as is not allowed to list the Deleted Objects container").And.Contains(DeletedObjectsDn));
        }
    }

    [Test]
    public async Task VerifyReadinessAsync_ContainerUndetermined_ReportsCouldNotDetermineAsync()
    {
        GivenTheBoundAccountIs(ServiceAccount);
        GivenTheDeletedObjectsContainerOf(PartitionDn, securityDescriptor: null);

        var findings = await Source().VerifyReadinessAsync(new LdapConnectorRootDse(), [PartitionDn], CancellationToken.None);

        var finding = findings.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(finding.Subject, Is.EqualTo(DeletedObjectsDn));
            Assert.That(finding.Outcome, Is.EqualTo(LdapDeltaSourceOutcome.CouldNotDetermine));
            Assert.That(finding.DeltaImportText, Does.StartWith("JIM could not confirm"));
            Assert.That(finding.SchemaDiscoveryText, Does.StartWith("JIM could not confirm"));
        }
    }

    [Test]
    public async Task VerifyReadinessAsync_ContainerGranted_ReportsAvailableWithNoTextAsync()
    {
        GivenTheBoundAccountIs(ServiceAccount);
        GivenTheDeletedObjectsContainerOf(PartitionDn, SecurityDescriptor(Ace(AccessAllowedAceType, ListContents | ReadProperty, ServiceAccount)));

        var findings = await Source().VerifyReadinessAsync(new LdapConnectorRootDse(), [PartitionDn], CancellationToken.None);

        var finding = findings.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(finding.Subject, Is.EqualTo(DeletedObjectsDn));
            Assert.That(finding.Outcome, Is.EqualTo(LdapDeltaSourceOutcome.Available));
            Assert.That(finding.Detail, Is.EqualTo("List Contents and Read Property are both granted"));
            Assert.That(finding.DeltaImportText, Is.Null);
            Assert.That(finding.SchemaDiscoveryText, Is.Null);
        }
    }

    [Test]
    public async Task VerifyReadinessAsync_TwoPartitions_ChecksEachContainerAsync()
    {
        const string otherPartitionDn = "DC=child,DC=corp,DC=local";
        GivenTheBoundAccountIs(ServiceAccount);
        GivenTheDeletedObjectsContainerOf(PartitionDn, SecurityDescriptor(Ace(AccessAllowedAceType, ListContents, ServiceAccount)));
        GivenTheDeletedObjectsContainerOf(otherPartitionDn, SecurityDescriptor(Ace(AccessDeniedAceType, ListContents, ServiceAccount)));

        var findings = await Source().VerifyReadinessAsync(new LdapConnectorRootDse(), [PartitionDn, otherPartitionDn], CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(findings.Select(f => f.Subject), Is.EqualTo(new[] { DeletedObjectsDn, "CN=Deleted Objects," + otherPartitionDn }));
            Assert.That(findings.Select(f => f.Outcome), Is.EqualTo(new[] { LdapDeltaSourceOutcome.Available, LdapDeltaSourceOutcome.Unavailable }));
        }
    }

    [Test]
    public async Task VerifyReadinessAsync_NoNamingContexts_ChecksNothingAsync()
    {
        var findings = await Source().VerifyReadinessAsync(new LdapConnectorRootDse(), [], CancellationToken.None);

        Assert.That(findings, Is.Empty);
        _executor.Verify(x => x.SendRequestAsync(It.IsAny<DirectoryRequest>()), Times.Never);
    }

    #endregion

    #region HasBaseline

    [Test]
    public void HasBaseline_NoHighestCommittedUsn_IsFalse() =>
        Assert.That(Source().HasBaseline(new LdapConnectorRootDse { InvocationId = Guid.NewGuid() }), Is.False);

    [Test]
    public void HasBaseline_WatermarkPresent_IsTrue() =>
        Assert.That(Source().HasBaseline(new LdapConnectorRootDse { HighestCommittedUsn = 0 }), Is.True);

    #endregion

    #region ReadChangesAsync: changed objects

    [Test]
    public async Task ReadChangesAsync_Always_SearchesEachSelectedContainerForObjectsChangedAfterTheWatermarkAsync()
    {
        var context = Context(Partition());

        await Source().ReadChangesAsync(context, new ConnectedSystemImportResult(), CancellationToken.None);

        var search = _sent.Single(r => r.DistinguishedName == ContainerDn);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(search.Filter, Is.EqualTo("(&(objectClass=user)(uSNChanged>=43))"));
            Assert.That(search.Scope, Is.EqualTo(SearchScope.Subtree));
            Assert.That(search.Attributes.Cast<string>(), Is.SupersetOf(new[] { "objectGUID", "displayName", "objectClass", "isDeleted" }));
        }
    }

    [Test]
    public async Task ReadChangesAsync_DirectorySupportsPaging_SendsANonCriticalPagingControlOfTheRunProfilesPageSizeAsync()
    {
        var context = Context(Partition(), supportsPaging: true, pageSize: 250);

        await Source().ReadChangesAsync(context, new ConnectedSystemImportResult(), CancellationToken.None);

        var control = _sent.Single(r => r.DistinguishedName == ContainerDn).Controls.OfType<PageResultRequestControl>().SingleOrDefault();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(control, Is.Not.Null);
            Assert.That(control!.PageSize, Is.EqualTo(250));
            Assert.That(control.IsCritical, Is.False, "a directory that cannot page must answer the search rather than refuse it");
            Assert.That(control.Cookie, Is.Empty);
        }
    }

    [Test]
    public async Task ReadChangesAsync_DirectoryDoesNotSupportPaging_SendsNoPagingControlAsync()
    {
        var context = Context(Partition(), supportsPaging: false);

        await Source().ReadChangesAsync(context, new ConnectedSystemImportResult(), CancellationToken.None);

        Assert.That(_sent.Single(r => r.DistinguishedName == ContainerDn).Controls.OfType<PageResultRequestControl>(), Is.Empty);
    }

    [Test]
    public async Task ReadChangesAsync_DirectoryAnswersWithACookie_AddsATokenNamedForTheContainerAndObjectTypeAsync()
    {
        _answers[ContainerDn] = _ => LdapTestResponses.SearchResponseWithPagingCookie(Cookie, ChangedEntry("CN=a," + ContainerDn));
        var partition = Partition();
        var context = Context(partition);
        var result = new ConnectedSystemImportResult();

        await Source().ReadChangesAsync(context, result, CancellationToken.None);

        var token = result.PaginationTokens.SingleOrDefault(t => t.Name == LdapConnectorUtilities.GetPaginationTokenName(partition.Containers!.Single(), context.ObjectTypes[0]));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(token, Is.Not.Null, "the next page resumes this combo from where the directory stopped");
            Assert.That(token!.ByteValue, Is.EqualTo(Cookie));
        }
    }

    [Test]
    public async Task ReadChangesAsync_OnALaterPage_SkipsCombosWithoutATokenAsync()
    {
        // Another combo is still paging; this container's combo finished on an earlier page.
        var context = Context(Partition(), tokens: [new ConnectedSystemPaginationToken("OU=Elsewhere,DC=corp,DC=local|1", Cookie)]);

        await Source().ReadChangesAsync(context, new ConnectedSystemImportResult(), CancellationToken.None);

        Assert.That(_sent.Select(r => r.DistinguishedName), Does.Not.Contain(ContainerDn));
    }

    [Test]
    public async Task ReadChangesAsync_OnALaterPage_ResumesEachComboFromItsCookieAsync()
    {
        var partition = Partition();
        var container = partition.Containers!.Single();
        var objectType = ObjectType();
        var context = Context(partition, objectType, tokens:
        [
            new ConnectedSystemPaginationToken(LdapConnectorUtilities.GetPaginationTokenName(container, objectType), Cookie),
            new ConnectedSystemPaginationToken(LdapConnectorUtilities.GetDeletedObjectsPaginationTokenName(partition), DeletedObjectsCookie)
        ]);

        await Source().ReadChangesAsync(context, new ConnectedSystemImportResult(), CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_sent.Single(r => r.DistinguishedName == ContainerDn).Controls.OfType<PageResultRequestControl>().Single().Cookie, Is.EqualTo(Cookie));
            Assert.That(_sent.Single(r => r.DistinguishedName == DeletedObjectsDn).Controls.OfType<PageResultRequestControl>().Single().Cookie, Is.EqualTo(DeletedObjectsCookie));
        }
    }

    [Test]
    public async Task ReadChangesAsync_DirectoryRejectsTheCookie_ImportsNothingMoreForThatComboWithoutFailingAsync()
    {
        // Samba AD hands back a cookie on the first page and then refuses it; everything was on that first page.
        _answers[ContainerDn] = _ => throw new DirectoryOperationException("The server does not support the control. The control is critical.");
        var partition = Partition();
        var objectType = ObjectType();
        var context = Context(partition, objectType, tokens:
            [new ConnectedSystemPaginationToken(LdapConnectorUtilities.GetPaginationTokenName(partition.Containers!.Single(), objectType), Cookie)]);
        var result = new ConnectedSystemImportResult();

        await Source().ReadChangesAsync(context, result, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.ImportObjects, Is.Empty);
            Assert.That(result.PaginationTokens, Is.Empty);
            Assert.That(_conversions, Is.Empty, "a refused page has no entries to hand over");
        }
    }

    [Test]
    public async Task ReadChangesAsync_Always_HandsEntriesToTheHostAsNotSetWithTheSearchedObjectTypeAsync()
    {
        _answers[ContainerDn] = _ => LdapTestResponses.SearchResponseWithEntries(ChangedEntry("CN=a," + ContainerDn), ChangedEntry("CN=b," + ContainerDn));
        var result = new ConnectedSystemImportResult();

        await Source().ReadChangesAsync(Context(Partition()), result, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_conversions, Is.EqualTo(new[] { (2, ObjectChangeType.NotSet, (string?)"user") }),
                "a USN search cannot tell a create from an update; the import decides from whether the object is already staged");
            Assert.That(result.ImportObjects, Has.Count.EqualTo(2));
            Assert.That(result.ImportObjects.Select(o => o.ChangeType), Is.All.EqualTo(ObjectChangeType.NotSet));
        }
    }

    #endregion

    #region ReadChangesAsync: deleted objects

    [Test]
    public async Task ReadChangesAsync_Always_SearchesEachPartitionsDeletedObjectsContainerAfterItsCombosAsync()
    {
        const string otherPartitionDn = "DC=child,DC=corp,DC=local";
        const string otherContainerDn = "OU=Staff,DC=child,DC=corp,DC=local";
        var context = Context([Partition(), Partition(otherPartitionDn, otherContainerDn)], ObjectType());

        await Source().ReadChangesAsync(context, new ConnectedSystemImportResult(), CancellationToken.None);

        var tombstoneSearch = _sent.First(r => r.DistinguishedName == DeletedObjectsDn);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(_sent.Select(r => r.DistinguishedName),
                Is.EqualTo(new[] { ContainerDn, DeletedObjectsDn, otherContainerDn, "CN=Deleted Objects," + otherPartitionDn }));
            Assert.That(tombstoneSearch.Filter, Is.EqualTo("(&(isDeleted=TRUE)(uSNChanged>=43))"));
            Assert.That(tombstoneSearch.Controls.Cast<DirectoryControl>().Any(c => c.Type == LdapConnectorConstants.LDAP_SERVER_SHOW_DELETED_OID && c.IsCritical), Is.True);
        }
    }

    [Test]
    public async Task ReadChangesAsync_TombstoneOfASelectedClass_YieldsADeletedImportObjectCarryingTheExternalIdAsync()
    {
        var objectGuid = Guid.NewGuid();
        _answers[DeletedObjectsDn] = _ => LdapTestResponses.SearchResponseWithEntries(Tombstone("CN=a\\0ADEL:1," + DeletedObjectsDn, objectGuid, "top", "person", "user"));
        var result = new ConnectedSystemImportResult();

        await Source().ReadChangesAsync(Context(Partition()), result, CancellationToken.None);

        var deletion = result.ImportObjects.Single();
        var externalId = deletion.Attributes.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(deletion.ChangeType, Is.EqualTo(ObjectChangeType.Deleted));
            Assert.That(deletion.ObjectType, Is.EqualTo("user"));
            Assert.That(externalId.Name, Is.EqualTo("objectGUID"));
            Assert.That(externalId.Type, Is.EqualTo(AttributeDataType.Guid));
            Assert.That(externalId.GuidValues, Is.EqualTo(new[] { objectGuid }));
            Assert.That(_conversions.Select(c => c.EntryCount), Is.All.Zero, "a tombstone is built here, not converted by the host");
        }
    }

    [Test]
    public async Task ReadChangesAsync_TombstoneWithoutObjectGuid_IsSkippedAsync()
    {
        _answers[DeletedObjectsDn] = _ => LdapTestResponses.SearchResponseWithBinary("CN=a\\0ADEL:1," + DeletedObjectsDn,
            ("objectClass", [Encoding.UTF8.GetBytes("top"), Encoding.UTF8.GetBytes("user")]),
            ("isDeleted", [Encoding.UTF8.GetBytes("TRUE")]));
        var result = new ConnectedSystemImportResult();

        await Source().ReadChangesAsync(Context(Partition()), result, CancellationToken.None);

        Assert.That(result.ImportObjects, Is.Empty, "without an objectGUID there is nothing to match the deletion to");
    }

    [Test]
    public async Task ReadChangesAsync_TombstoneOfAnUnselectedClass_IsSkippedAsync()
    {
        _answers[DeletedObjectsDn] = _ => LdapTestResponses.SearchResponseWithEntries(Tombstone("CN=a\\0ADEL:1," + DeletedObjectsDn, Guid.NewGuid(), "top", "group"));
        var result = new ConnectedSystemImportResult();

        await Source().ReadChangesAsync(Context(Partition()), result, CancellationToken.None);

        Assert.That(result.ImportObjects, Is.Empty);
    }

    [Test]
    public async Task ReadChangesAsync_TombstoneSearchRefused_RecordsANoteAndImportsNoDeletionsAsync()
    {
        _answers[DeletedObjectsDn] = _ => throw new DirectoryOperationException(
            LdapTestResponses.Create<SearchResponse>(ResultCode.InsufficientAccessRights), "insufficient access rights");
        var result = new ConnectedSystemImportResult();

        await Source().ReadChangesAsync(Context(Partition()), result, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.ImportObjects, Is.Empty);
            Assert.That(_notes.Warning, Does.StartWith($"Deletions were not detected in {DeletedObjectsDn}: the directory refused the search").And.Contains("insufficient access rights"));
        }
    }

    [Test]
    public async Task ReadChangesAsync_TombstoneSearchLimitExceeded_RecordsANoteAndImportsNoDeletionsAsync()
    {
        _answers[DeletedObjectsDn] = _ => throw new DirectoryOperationException(
            LdapTestResponses.Create<SearchResponse>(ResultCode.SizeLimitExceeded), "The size limit was exceeded");
        var result = new ConnectedSystemImportResult();

        await Source().ReadChangesAsync(Context(Partition()), result, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.ImportObjects, Is.Empty);
            Assert.That(_notes.Warning, Does.StartWith($"Deletions were not detected in {DeletedObjectsDn}: the directory returned more deleted objects than it answers in one search"));
        }
    }

    [Test]
    public async Task ReadChangesAsync_TombstonePageHasACookie_AddsThePartitionsDeletedObjectsTokenAsync()
    {
        _answers[DeletedObjectsDn] = _ => LdapTestResponses.SearchResponseWithPagingCookie(DeletedObjectsCookie,
            Tombstone("CN=a\\0ADEL:1," + DeletedObjectsDn, Guid.NewGuid(), "top", "user"));
        var partition = Partition();
        var result = new ConnectedSystemImportResult();

        await Source().ReadChangesAsync(Context(partition), result, CancellationToken.None);

        var token = result.PaginationTokens.SingleOrDefault(t => t.Name == LdapConnectorUtilities.GetDeletedObjectsPaginationTokenName(partition));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(token, Is.Not.Null, "the next page resumes the tombstone search rather than re-reading it");
            Assert.That(token!.ByteValue, Is.EqualTo(DeletedObjectsCookie));
        }
    }

    [Test]
    public async Task ReadChangesAsync_OnALaterPageWithoutADeletedObjectsToken_SkipsTheTombstoneSearchAsync()
    {
        var partition = Partition();
        var objectType = ObjectType();
        var context = Context(partition, objectType, tokens:
            [new ConnectedSystemPaginationToken(LdapConnectorUtilities.GetPaginationTokenName(partition.Containers!.Single(), objectType), Cookie)]);

        await Source().ReadChangesAsync(context, new ConnectedSystemImportResult(), CancellationToken.None);

        Assert.That(_sent.Select(r => r.DistinguishedName), Is.EqualTo(new[] { ContainerDn }), "the tombstone search finished on an earlier page");
    }

    #endregion

    #region ReadChangesAsync: cancellation and progress

    [Test]
    public async Task ReadChangesAsync_CancellationRequested_StopsBeforeTheNextSearchAsync()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Source().ReadChangesAsync(Context(Partition()), new ConnectedSystemImportResult(), cancellation.Token);

        Assert.That(_sent, Is.Empty);
    }

    [Test]
    public async Task ReadChangesAsync_Always_EntersQueryChangesFetchAndQueryDeletionsPhasesAsync()
    {
        await Source().ReadChangesAsync(Context(Partition()), new ConnectedSystemImportResult(), CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_phases.Select(p => p.Phase), Is.EqualTo(new[] { LdapConnectorPhases.QueryChanges, LdapConnectorPhases.Fetch, LdapConnectorPhases.QueryDeletions }));
            Assert.That(_phases[0].Message, Is.EqualTo("Querying changes since USN 42..."));
            Assert.That(_phases[1].Message, Is.EqualTo("Fetching changed user objects from Users..."));
            Assert.That(_phases[2].Message, Is.EqualTo("Querying deleted objects in corp.local..."));
        }
    }

    [Test]
    public async Task ReadChangesAsync_Always_ReportsObjectsReadAfterEachSearchAsync()
    {
        _answers[ContainerDn] = _ => LdapTestResponses.SearchResponseWithEntries(ChangedEntry("CN=a," + ContainerDn), ChangedEntry("CN=b," + ContainerDn));
        _answers[DeletedObjectsDn] = _ => LdapTestResponses.SearchResponseWithEntries(Tombstone("CN=c\\0ADEL:1," + DeletedObjectsDn, Guid.NewGuid(), "top", "user"));

        await Source().ReadChangesAsync(Context(Partition()), new ConnectedSystemImportResult(), CancellationToken.None);

        Assert.That(_reported, Is.EqualTo(new[] { 2, 1 }), "the Activity's counters move after the changes and again after the deletions");
    }

    #endregion

    #region ToFinding

    [Test]
    public void ToFinding_Denied_MapsToUnavailableWithBothTexts()
    {
        var finding = LdapUsnDeltaSource.ToFinding(new DeletedObjectsAccessFinding
        {
            NamingContext = PartitionDn,
            ContainerDn = DeletedObjectsDn,
            Outcome = DeletedObjectsAccessOutcome.Denied,
            Detail = "List Contents is not granted"
        });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(finding.Subject, Is.EqualTo(DeletedObjectsDn));
            Assert.That(finding.Outcome, Is.EqualTo(LdapDeltaSourceOutcome.Unavailable));
            Assert.That(finding.Detail, Is.EqualTo("List Contents is not granted"));
            Assert.That(finding.DeltaImportText, Does.StartWith("Deletions cannot be detected:"));
            Assert.That(finding.SchemaDiscoveryText, Does.StartWith("The account JIM connects as is not allowed to list the Deleted Objects container"));
        }
    }

    [Test]
    public void ToFinding_Granted_MapsToAvailable()
    {
        var finding = LdapUsnDeltaSource.ToFinding(new DeletedObjectsAccessFinding
        {
            NamingContext = PartitionDn,
            ContainerDn = DeletedObjectsDn,
            Outcome = DeletedObjectsAccessOutcome.Granted,
            Detail = "List Contents and Read Property are both granted"
        });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(finding.Subject, Is.EqualTo(DeletedObjectsDn));
            Assert.That(finding.Outcome, Is.EqualTo(LdapDeltaSourceOutcome.Available));
            Assert.That(finding.Detail, Is.EqualTo("List Contents and Read Property are both granted"));
            Assert.That(finding.DeltaImportText, Is.Null);
            Assert.That(finding.SchemaDiscoveryText, Is.Null);
        }
    }

    #endregion

    #region Helpers

    /// <summary>
    /// The rootDSE read that establishes who JIM is bound as, which the access check makes before it reads any
    /// container's permissions.
    /// </summary>
    private void GivenTheBoundAccountIs(params string[] sids) =>
        _executor.Setup(x => x.SendRequestAsync(It.Is<SearchRequest>(r => string.IsNullOrEmpty(r.DistinguishedName))))
            .ReturnsAsync(LdapTestResponses.SearchResponseWithBinary("",
                (LdapCallerSecurityContext.AttributeTokenGroups, sids.Select(Sid).ToArray()),
                (LdapCallerSecurityContext.AttributePrincipalName, [Encoding.UTF8.GetBytes("CORP\\jim-svc")])));

    private void GivenTheDeletedObjectsContainerOf(string namingContext, byte[]? securityDescriptor)
    {
        var containerDn = LdapConnectorDeletedObjectsAccess.ContainerDnFor(namingContext);
        var response = securityDescriptor == null
            ? LdapTestResponses.SearchResponseWithBinary(containerDn)
            : LdapTestResponses.SearchResponseWithBinary(containerDn, (LdapConnectorResetRights.AttributeSecurityDescriptor, [securityDescriptor]));

        _executor.Setup(x => x.SendRequestAsync(It.Is<SearchRequest>(r => r.DistinguishedName == containerDn)))
            .ReturnsAsync(response);
    }

    private static ConnectedSystemObjectType ObjectType() => new()
    {
        Id = 1,
        Name = "user",
        Selected = true,
        Attributes =
        [
            new ConnectedSystemObjectTypeAttribute { Id = 1, Name = "objectGUID", Type = AttributeDataType.Guid, IsExternalId = true, Selected = true },
            new ConnectedSystemObjectTypeAttribute { Id = 2, Name = "displayName", Type = AttributeDataType.Text, Selected = true }
        ]
    };

    private static ConnectedSystemPartition Partition(string partitionDn = PartitionDn, string containerDn = ContainerDn) => new()
    {
        Id = partitionDn.GetHashCode(),
        ExternalId = partitionDn,
        Name = partitionDn == PartitionDn ? "corp.local" : partitionDn,
        Selected = true,
        Containers = [new ConnectedSystemContainer { Id = containerDn.GetHashCode(), ExternalId = containerDn, Name = containerDn == ContainerDn ? "Users" : containerDn, Selected = true }]
    };

    private LdapDeltaReadContext Context(ConnectedSystemPartition partition, ConnectedSystemObjectType? objectType = null,
        IReadOnlyList<ConnectedSystemPaginationToken>? tokens = null, bool supportsPaging = true, int pageSize = 500) =>
        Context([partition], objectType, tokens, supportsPaging, pageSize);

    private LdapDeltaReadContext Context(IReadOnlyList<ConnectedSystemPartition> partitions, ConnectedSystemObjectType? objectType = null,
        IReadOnlyList<ConnectedSystemPaginationToken>? tokens = null, bool supportsPaging = true, int pageSize = 500) => new()
    {
        PreviousRootDse = new LdapConnectorRootDse { DirectoryType = LdapDirectoryType.ActiveDirectory, HighestCommittedUsn = Watermark },
        // Microsoft AD pages; Samba AD is the AD-family directory that does not.
        CurrentRootDse = new LdapConnectorRootDse { DirectoryType = supportsPaging ? LdapDirectoryType.ActiveDirectory : LdapDirectoryType.SambaAD },
        TargetPartitions = partitions,
        ScopeDecidingContainers = partitions.SelectMany(p => p.Containers!).ToList(),
        ObjectTypes = [objectType ?? ObjectType()],
        PaginationTokens = tokens ?? [],
        PageSize = pageSize,
        SearchTimeout = Timeout,
        Notes = _notes,
        Host = _host.Object
    };

    private static SearchResultEntry ChangedEntry(string dn) =>
        LdapTestResponses.Entry(dn, ("objectClass", "user"), ("displayName", "Someone"));

    private static SearchResultEntry Tombstone(string dn, Guid objectGuid, params string[] objectClasses) =>
        LdapTestResponses.SearchResponseWithBinary(dn,
            ("objectGUID", [objectGuid.ToByteArray()]),
            ("objectClass", objectClasses.Select(Encoding.UTF8.GetBytes).ToArray()),
            ("isDeleted", [Encoding.UTF8.GetBytes("TRUE")])).Entries[0];

    #endregion
    #region what a Delta Import does with the findings for every partition (moved from the access tests)

    private const string OtherNamingContext = "DC=child,DC=testdomain,DC=local";
    private const string OtherDeletedObjectsDn = "CN=Deleted Objects,DC=child,DC=testdomain,DC=local";

    private static DeletedObjectsAccessFinding AccessFinding(DeletedObjectsAccessOutcome outcome, string namingContext, string detail) => new()
    {
        NamingContext = namingContext,
        ContainerDn = LdapConnectorDeletedObjectsAccess.ContainerDnFor(namingContext),
        Outcome = outcome,
        Detail = detail
    };

    private static LdapDeltaSourceNotes Summarise(params DeletedObjectsAccessFinding[] findings) =>
        LdapDeltaSourceFindings.ThrowOnUnavailableOrNote(findings.Select(LdapUsnDeltaSource.ToFinding).ToList(), Log.Logger);

    [Test]
    public void Summarise_OneDeniedAmongGranted_ThrowsNamingThatContainer()
    {
        var findings = new[]
        {
            AccessFinding(DeletedObjectsAccessOutcome.Granted, PartitionDn, "List Contents and Read Property are both granted"),
            AccessFinding(DeletedObjectsAccessOutcome.Denied, OtherNamingContext, "List Contents is not granted")
        };

        Assert.That(() => Summarise(findings), Throws.TypeOf<CannotPerformDeltaImportException>()
            .With.Message.Contains(OtherDeletedObjectsDn)
            .And.Message.Not.Contains(DeletedObjectsDn));
    }

    [Test]
    public void Summarise_TwoDenied_NamesBothContainers()
    {
        var findings = new[]
        {
            AccessFinding(DeletedObjectsAccessOutcome.Denied, PartitionDn, "List Contents is not granted"),
            AccessFinding(DeletedObjectsAccessOutcome.Denied, OtherNamingContext, "List Contents is not granted")
        };

        Assert.That(() => Summarise(findings), Throws.TypeOf<CannotPerformDeltaImportException>()
            .With.Message.Contains(DeletedObjectsDn)
            .And.Message.Contains(OtherDeletedObjectsDn));
    }

    [Test]
    public void Summarise_OneDeniedAndOneUndetermined_ThrowsWithoutTheUndeterminedText()
    {
        var findings = new[]
        {
            AccessFinding(DeletedObjectsAccessOutcome.CouldNotDetermine, PartitionDn, "the directory did not return the container's entry"),
            AccessFinding(DeletedObjectsAccessOutcome.Denied, OtherNamingContext, "List Contents is not granted")
        };

        Assert.That(() => Summarise(findings), Throws.TypeOf<CannotPerformDeltaImportException>()
            .With.Message.Contains(OtherDeletedObjectsDn)
            .And.Message.Not.Contains("could not confirm"));
    }

    [Test]
    public void Summarise_UndeterminedOnly_ReturnsAWarningWithoutThrowing()
    {
        var finding = AccessFinding(DeletedObjectsAccessOutcome.CouldNotDetermine, PartitionDn, "the directory did not return the container's entry");

        LdapDeltaSourceNotes? notes = null;
        Assert.That(() => notes = Summarise(finding), Throws.Nothing);

        Assert.That(notes!.Warning, Is.EqualTo(LdapConnectorDeletedObjectsAccess.DescribeForDeltaImport(finding)));
    }

    [Test]
    public void Summarise_TwoUndetermined_WarnsAboutBothContainers()
    {
        var notes = Summarise(
            AccessFinding(DeletedObjectsAccessOutcome.CouldNotDetermine, PartitionDn, "the directory did not return the container's entry"),
            AccessFinding(DeletedObjectsAccessOutcome.CouldNotDetermine, OtherNamingContext, "the directory answered: Insufficient access rights."));

        Assert.That(notes.Warning, Does.Contain(DeletedObjectsDn).And.Contain("did not return the container's entry")
            .And.Contain(OtherDeletedObjectsDn).And.Contain("Insufficient access rights."));
    }

    [Test]
    public void Summarise_AllGranted_ReturnsNoWarning()
    {
        var notes = Summarise(
            AccessFinding(DeletedObjectsAccessOutcome.Granted, PartitionDn, "List Contents and Read Property are both granted"),
            AccessFinding(DeletedObjectsAccessOutcome.Granted, OtherNamingContext, "List Contents and Read Property are both granted"));

        Assert.That(notes.Warning, Is.Null);
    }

    /// <summary>
    /// The tombstone search is refused after the up-front check could only say it was unsure about the same
    /// container. The refusal is the stronger evidence, and it is what the administrator has to see.
    /// </summary>
    [Test]
    public void Record_ARefusalForAContainerAlreadyNotedAsUndetermined_ReplacesTheUndeterminedNote()
    {
        var notes = Summarise(
            AccessFinding(DeletedObjectsAccessOutcome.CouldNotDetermine, PartitionDn, "the directory did not return the container's permissions, which needs Read Permissions on it"));

        notes.Record(DeletedObjectsDn, LdapConnectorDeletedObjectsAccess.DescribeRefusedSearch(DeletedObjectsDn, "Unavailable critical extension"));

        Assert.That(notes.Warning, Does.Contain("refused the search").And.Contain("Unavailable critical extension")
            .And.Not.Contain("could not confirm"));
    }

    [Test]
    public void Record_ARefusalForAnotherContainerThanTheUndeterminedOne_KeepsBothNotes()
    {
        var notes = Summarise(
            AccessFinding(DeletedObjectsAccessOutcome.CouldNotDetermine, PartitionDn, "the directory did not return the container's entry"));

        notes.Record(OtherDeletedObjectsDn, LdapConnectorDeletedObjectsAccess.DescribeRefusedSearch(OtherDeletedObjectsDn, "Unavailable critical extension"));

        Assert.That(notes.Warning, Does.Contain("could not confirm").And.Contain(DeletedObjectsDn)
            .And.Contain("refused the search").And.Contain(OtherDeletedObjectsDn));
    }

    #endregion
}
