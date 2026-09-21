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
using System.Reflection;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// The changelog change source (<see cref="LdapChangelogDeltaSource"/>), as fixed for #1725: it reads the changelog
/// the rootDSE names with a filter LDAP accepts, takes its watermark from the rootDSE or from the highest change
/// number rather than the last by position, refuses when the changelog has been trimmed past the baseline, and
/// fails loudly when the changelog cannot be read rather than importing nothing and saying so to nobody.
/// </summary>
[TestFixture]
public class LdapChangelogDeltaSourceTests
{
    private const string DefaultChangelogDn = "cn=changelog";
    private const string AdvertisedChangelogDn = "cn=changelog,cn=replication";
    private const string ContainerDn = "ou=People,dc=example,dc=com";
    private const string InScopeDn = "uid=jsmith,ou=People,dc=example,dc=com";
    private const string OutOfScopeDn = "uid=jsmith,ou=Elsewhere,dc=example,dc=com";
    private const int PreviousChangeNumber = 1200;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private Mock<ILdapOperationExecutor> _executor = null!;
    private List<(SearchRequest Request, TimeSpan? Timeout)> _sent = null!;
    private Func<SearchRequest, SearchResponse> _baseReadAnswer = null!;
    private Func<SearchRequest, SearchResponse> _searchAnswer = null!;

    [SetUp]
    public void SetUp()
    {
        _sent = [];

        // By default the changelog is there and readable, and empty: the base-scope read answers its entry and a
        // search under it answers nothing. Tests that need otherwise replace one or the other.
        _baseReadAnswer = request => LdapTestResponses.SearchResponseWithEntries(LdapTestResponses.Entry(request.DistinguishedName, ("objectClass", "top")));
        _searchAnswer = _ => LdapTestResponses.EmptySearchResponse();

        _executor = new Mock<ILdapOperationExecutor>();
        _executor.Setup(x => x.SendRequest(It.IsAny<DirectoryRequest>()))
            .Returns((DirectoryRequest request) => Answer((SearchRequest)request, null));
        _executor.Setup(x => x.SendRequest(It.IsAny<DirectoryRequest>(), It.IsAny<TimeSpan>()))
            .Returns((DirectoryRequest request, TimeSpan timeout) => Answer((SearchRequest)request, timeout));
    }

    private SearchResponse Answer(SearchRequest request, TimeSpan? timeout)
    {
        _sent.Add((request, timeout));
        return request.Scope == SearchScope.Base ? _baseReadAnswer(request) : _searchAnswer(request);
    }

    private LdapChangelogDeltaSource Source() => new(_executor.Object, Log.Logger);

    #region VerifyReadinessAsync

    [Test]
    public async Task VerifyReadinessAsync_ChangelogEntryReadable_ReportsAvailableAsync()
    {
        var findings = await Source().VerifyReadinessAsync(new LdapConnectorRootDse(), ["dc=example,dc=com"], CancellationToken.None);

        var finding = findings.Single();
        var probe = _sent.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(finding.Subject, Is.EqualTo(DefaultChangelogDn));
            Assert.That(finding.Outcome, Is.EqualTo(LdapDeltaSourceOutcome.Available));
            Assert.That(finding.Detail, Is.EqualTo($"the changelog at {DefaultChangelogDn} can be read"));
            Assert.That(finding.DeltaImportText, Is.Null);
            Assert.That(finding.SchemaDiscoveryText, Is.Null);
            Assert.That(probe.Request.DistinguishedName, Is.EqualTo(DefaultChangelogDn));
            Assert.That(probe.Request.Scope, Is.EqualTo(SearchScope.Base));
            Assert.That(probe.Request.Filter, Is.EqualTo("(objectClass=*)"));
            Assert.That(probe.Request.Attributes.Cast<string>(), Is.EqualTo(new[] { "1.1" }), "the probe asks whether the entry is there, not what it holds");
        }
    }

    [Test]
    public async Task VerifyReadinessAsync_RootDseAdvertisesAChangelogDn_ProbesThatDnRatherThanTheDefaultAsync()
    {
        var findings = await Source().VerifyReadinessAsync(new LdapConnectorRootDse { ChangelogDn = AdvertisedChangelogDn }, [], CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_sent.Single().Request.DistinguishedName, Is.EqualTo(AdvertisedChangelogDn));
            Assert.That(findings.Single().Subject, Is.EqualTo(AdvertisedChangelogDn));
        }
    }

    [Test]
    public async Task VerifyReadinessAsync_NoSuchObjectAsDirectoryOperationException_ReportsUnavailableNamingBothCausesAsync()
    {
        _baseReadAnswer = _ => throw NoSuchObject();

        var finding = (await Source().VerifyReadinessAsync(new LdapConnectorRootDse(), [], CancellationToken.None)).Single();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(finding.Outcome, Is.EqualTo(LdapDeltaSourceOutcome.Unavailable));
            Assert.That(finding.Detail, Is.EqualTo($"no changelog at {DefaultChangelogDn}, or none the account JIM connects as may read"),
                "389 Directory Server answers noSuchObject for a base the account may not read, so code 32 never proves absence");
            Assert.That(finding.DeltaImportText, Does.Contain("provides no changelog at cn=changelog, or none the account JIM connects as may read"));
            Assert.That(finding.SchemaDiscoveryText, Does.Contain("publishes no changelog at cn=changelog that the account JIM connects as may read"));
        }
    }

    [Test]
    public async Task VerifyReadinessAsync_NoSuchObjectAsLdapException32_ReportsUnavailableAsync()
    {
        _baseReadAnswer = _ => throw new LdapException(32, "no such object");

        var finding = (await Source().VerifyReadinessAsync(new LdapConnectorRootDse(), [], CancellationToken.None)).Single();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(finding.Outcome, Is.EqualTo(LdapDeltaSourceOutcome.Unavailable));
            Assert.That(finding.DeltaImportText, Does.StartWith("Changes cannot be detected: the directory provides no changelog at cn=changelog"));
        }
    }

    [Test]
    public async Task VerifyReadinessAsync_SuccessWithNoEntry_ReportsUnavailableAsync()
    {
        _baseReadAnswer = _ => LdapTestResponses.EmptySearchResponse();

        var finding = (await Source().VerifyReadinessAsync(new LdapConnectorRootDse(), [], CancellationToken.None)).Single();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(finding.Outcome, Is.EqualTo(LdapDeltaSourceOutcome.Unavailable));
            Assert.That(finding.DeltaImportText, Does.StartWith("Changes cannot be detected: the directory provides no changelog at cn=changelog"));
        }
    }

    [Test]
    public async Task VerifyReadinessAsync_Refused_ReportsUnavailableWithTheDirectorysReasonAsync()
    {
        _baseReadAnswer = _ => throw Refused("insufficient access rights");

        var finding = (await Source().VerifyReadinessAsync(new LdapConnectorRootDse(), [], CancellationToken.None)).Single();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(finding.Outcome, Is.EqualTo(LdapDeltaSourceOutcome.Unavailable));
            Assert.That(finding.Detail, Is.EqualTo("the directory refused to read the changelog at cn=changelog (insufficient access rights)"));
            Assert.That(finding.DeltaImportText, Does.StartWith("Changes cannot be detected: the directory refused to read the changelog at cn=changelog (insufficient access rights), so additions, updates and deletions since the last import would go unnoticed."));
            Assert.That(finding.SchemaDiscoveryText, Does.StartWith("The directory refused to read the changelog at cn=changelog (insufficient access rights), so Delta Import is not available; Full Import works as normal and also detects deletions by absence."));
        }
    }

    [Test]
    public async Task VerifyReadinessAsync_ConnectionFailure_ReportsCouldNotDetermineAsync()
    {
        _baseReadAnswer = _ => throw new LdapException(81, "The LDAP server is unavailable.");

        var finding = (await Source().VerifyReadinessAsync(new LdapConnectorRootDse(), [], CancellationToken.None)).Single();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(finding.Outcome, Is.EqualTo(LdapDeltaSourceOutcome.CouldNotDetermine), "a connection fault is an unknown, never a denial");
            Assert.That(finding.Detail, Does.Contain("The LDAP server is unavailable."));
            Assert.That(finding.DeltaImportText, Is.EqualTo(
                "JIM could not confirm that the account it connects as can read the changelog at cn=changelog: the directory answered: The LDAP server is unavailable.. " +
                "If it cannot, Delta Imports from this directory detect no changes."));
            Assert.That(finding.SchemaDiscoveryText, Is.EqualTo(finding.DeltaImportText));
        }
    }

    [Test]
    public async Task VerifyReadinessAsync_Unavailable_DeltaImportTextNamesTheRetroChangelogPlugInTheReadRightAndTheFullImportRemedyAsync()
    {
        _baseReadAnswer = _ => throw NoSuchObject();

        var finding = (await Source().VerifyReadinessAsync(new LdapConnectorRootDse(), [], CancellationToken.None)).Single();

        Assert.That(finding.DeltaImportText, Is.EqualTo(
            "Changes cannot be detected: the directory provides no changelog at cn=changelog, or none the account JIM connects as may read, " +
            "so additions, updates and deletions since the last import would go unnoticed. " +
            "Run a Full Import, which also detects deletions by absence, or enable the directory's changelog (389 Directory Server: the Retro Changelog plug-in) " +
            "and grant the account read access to it; the LDAP Connector documentation, under Service Account Permissions, gives the detail."));
    }

    [Test]
    public async Task VerifyReadinessAsync_Unavailable_SchemaDiscoveryTextSaysDeltaImportIsNotAvailableAndFullImportDetectsDeletionsAsync()
    {
        _baseReadAnswer = _ => throw NoSuchObject();

        var finding = (await Source().VerifyReadinessAsync(new LdapConnectorRootDse(), [], CancellationToken.None)).Single();

        Assert.That(finding.SchemaDiscoveryText, Is.EqualTo(
            "This directory publishes no changelog at cn=changelog that the account JIM connects as may read, so Delta Import is not available; " +
            "Full Import works as normal and also detects deletions by absence. " +
            "To use Delta Import, enable the directory's changelog (389 Directory Server: the Retro Changelog plug-in) and grant the account read access to it; " +
            "see the LDAP Connector documentation, Service Account Permissions."));
    }

    #endregion

    #region CaptureWatermarkAsync

    [Test]
    public async Task CaptureWatermarkAsync_RootDseAdvertisesLastChangeNumber_RecordsItWithoutSearchingAsync()
    {
        var rootDse = new LdapConnectorRootDse { AdvertisedLastChangeNumber = 4321 };

        await Source().CaptureWatermarkAsync(RootDseEntry(), rootDse, Timeout);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(rootDse.LastChangeNumber, Is.EqualTo(4321));
            Assert.That(_sent, Is.Empty, "the rootDSE already said; there is nothing to enumerate");
        }
    }

    [Test]
    public async Task CaptureWatermarkAsync_NotAdvertised_RecordsTheHighestChangeNumberRegardlessOfOrderAsync()
    {
        _searchAnswer = _ => LdapTestResponses.SearchResponseWithEntries(ChangelogEntry(7), ChangelogEntry(12), ChangelogEntry(9));
        var rootDse = new LdapConnectorRootDse();

        await Source().CaptureWatermarkAsync(RootDseEntry(), rootDse, Timeout);

        var enumeration = _sent.Single(s => s.Request.Scope != SearchScope.Base);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(rootDse.LastChangeNumber, Is.EqualTo(12), "LDAP guarantees no order, so the last entry by position is not the newest");
            Assert.That(enumeration.Request.DistinguishedName, Is.EqualTo(DefaultChangelogDn));
            Assert.That(enumeration.Request.Scope, Is.EqualTo(SearchScope.OneLevel));
            Assert.That(enumeration.Request.Filter, Is.EqualTo("(changeNumber=*)"));
            Assert.That(enumeration.Request.Attributes.Cast<string>(), Is.EqualTo(new[] { "changeNumber" }));
            Assert.That(enumeration.Timeout, Is.EqualTo(Timeout), "the enumeration honours the import's search timeout");
        }
    }

    [Test]
    public async Task CaptureWatermarkAsync_NotAdvertisedAndSizeLimitExceeded_RecordsTheHighestOfThePartialAnswerAsync()
    {
        _searchAnswer = _ => throw SizeLimitExceeded(ChangelogEntry(30), ChangelogEntry(45), ChangelogEntry(31));
        var rootDse = new LdapConnectorRootDse();

        await Source().CaptureWatermarkAsync(RootDseEntry(), rootDse, Timeout);

        Assert.That(rootDse.LastChangeNumber, Is.EqualTo(45), "below the truth is the safe direction: the next Delta Import re-reads a few changes rather than skipping any");
    }

    [Test]
    public async Task CaptureWatermarkAsync_ChangelogEmpty_RecordsZeroAsync()
    {
        var rootDse = new LdapConnectorRootDse();

        await Source().CaptureWatermarkAsync(RootDseEntry(), rootDse, Timeout);

        Assert.That(rootDse.LastChangeNumber, Is.Zero, "a readable, empty changelog is a baseline: every change from here on is numbered above zero");
    }

    [Test]
    public async Task CaptureWatermarkAsync_ChangelogNotFound_LeavesTheWatermarkNullAsync()
    {
        _baseReadAnswer = _ => throw NoSuchObject();
        var rootDse = new LdapConnectorRootDse { LastChangeNumber = 5 };

        await Source().CaptureWatermarkAsync(RootDseEntry(), rootDse, Timeout);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(rootDse.LastChangeNumber, Is.Null, "null makes the next Delta Import run as a Full Import; zero would read as a real baseline");
            Assert.That(_sent.Select(s => s.Request.Scope), Is.EqualTo(new[] { SearchScope.Base }), "with no changelog there is nothing to enumerate");
        }
    }

    [Test]
    public async Task CaptureWatermarkAsync_ChangelogRefused_LeavesTheWatermarkNullAsync()
    {
        _baseReadAnswer = _ => throw Refused("insufficient access rights");
        var rootDse = new LdapConnectorRootDse { LastChangeNumber = 5 };

        await Source().CaptureWatermarkAsync(RootDseEntry(), rootDse, Timeout);

        Assert.That(rootDse.LastChangeNumber, Is.Null);
    }

    [Test]
    public async Task CaptureWatermarkAsync_ConnectionFailure_LeavesTheWatermarkNullAsync()
    {
        _baseReadAnswer = _ => throw new LdapException(81, "The LDAP server is unavailable.");
        var rootDse = new LdapConnectorRootDse();

        await Source().CaptureWatermarkAsync(RootDseEntry(), rootDse, Timeout);

        Assert.That(rootDse.LastChangeNumber, Is.Null);
    }

    [Test]
    public async Task CaptureWatermarkAsync_RootDseAdvertisesAChangelogDn_EnumeratesThatDnAsync()
    {
        _searchAnswer = _ => LdapTestResponses.SearchResponseWithEntries(ChangelogEntry(3));
        var rootDse = new LdapConnectorRootDse { ChangelogDn = AdvertisedChangelogDn };

        await Source().CaptureWatermarkAsync(RootDseEntry(), rootDse, Timeout);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_sent.Select(s => s.Request.DistinguishedName), Is.All.EqualTo(AdvertisedChangelogDn));
            Assert.That(rootDse.LastChangeNumber, Is.EqualTo(3));
        }
    }

    [Test]
    public async Task CaptureWatermarkAsync_ChangeNumberNotAnInteger_IgnoresThatEntryAsync()
    {
        _searchAnswer = _ => LdapTestResponses.SearchResponseWithEntries(
            ChangelogEntry(8),
            LdapTestResponses.Entry("changeNumber=abc,cn=changelog", ("changeNumber", "abc")));
        var rootDse = new LdapConnectorRootDse();

        await Source().CaptureWatermarkAsync(RootDseEntry(), rootDse, Timeout);

        Assert.That(rootDse.LastChangeNumber, Is.EqualTo(8));
    }

    #endregion

    #region VerifyContinuity

    [Test]
    public void VerifyContinuity_PreviousWatermarkBelowTheAdvertisedFirstChangeNumber_ThrowsNamingTheTrim()
    {
        var previous = new LdapConnectorRootDse { LastChangeNumber = 100 };
        var current = new LdapConnectorRootDse { FirstChangeNumber = 150 };

        Assert.That(() => Source().VerifyContinuity(previous, current),
            Throws.TypeOf<CannotPerformDeltaImportException>().With.Message.EqualTo(
                "The directory's changelog no longer holds the changes since the last import: it starts at change number 150, and the last import ended at 100, " +
                "so changes between them were trimmed (389 Directory Server: the Retro Changelog plug-in's maximum age) and cannot be imported. " +
                "Run a Full Import, which also detects deletions by absence, to re-establish the baseline."));
    }

    [TestCase(149, 150, Description = "the next change to read is the first one held")]
    [TestCase(150, 150, Description = "the watermark itself is still held")]
    [TestCase(200, 150, Description = "the watermark is well within the changelog")]
    public void VerifyContinuity_PreviousWatermarkAtOrAboveTheFirstChangeNumber_DoesNotThrow(long previousWatermark, long firstChangeNumber)
    {
        var previous = new LdapConnectorRootDse { LastChangeNumber = previousWatermark };
        var current = new LdapConnectorRootDse { FirstChangeNumber = firstChangeNumber };

        Assert.That(() => Source().VerifyContinuity(previous, current), Throws.Nothing);
    }

    [Test]
    public void VerifyContinuity_FirstChangeNumberNotAdvertised_DoesNotThrow()
    {
        var previous = new LdapConnectorRootDse { LastChangeNumber = 1 };
        var current = new LdapConnectorRootDse { FirstChangeNumber = null, AdvertisedLastChangeNumber = 5000 };

        Assert.That(() => Source().VerifyContinuity(previous, current), Throws.Nothing);
        Assert.That(_sent, Is.Empty, "continuity is judged from the rootDSE alone");
    }

    [Test]
    public void VerifyContinuity_NoPreviousWatermark_DoesNotThrow()
    {
        Assert.That(() => Source().VerifyContinuity(new LdapConnectorRootDse(), new LdapConnectorRootDse { FirstChangeNumber = 150 }), Throws.Nothing);
    }

    #endregion

    #region HasBaseline

    [Test]
    public void HasBaseline_NoChangeNumber_IsFalse() =>
        Assert.That(Source().HasBaseline(new LdapConnectorRootDse()), Is.False);

    [Test]
    public void HasBaseline_ChangeNumberPresent_IsTrue() =>
        Assert.That(Source().HasBaseline(new LdapConnectorRootDse { LastChangeNumber = 0 }), Is.True);

    #endregion

    #region ReadChangesAsync: request shape

    [Test]
    public async Task ReadChangesAsync_Always_EntersTheQueryChangesPhaseAsync()
    {
        var host = new Mock<ILdapDeltaImportHost>();

        await Source().ReadChangesAsync(Context(host), new ConnectedSystemImportResult(), CancellationToken.None);

        host.Verify(h => h.EnterPhaseAsync(LdapConnectorPhases.QueryChanges, $"Querying changelog since change number {PreviousChangeNumber:N0}..."), Times.Once);
    }

    [Test]
    public async Task ReadChangesAsync_Always_AsksForChangeNumbersAtOrAboveThePreviousPlusOneAsync()
    {
        var host = new Mock<ILdapDeltaImportHost>();

        await Source().ReadChangesAsync(Context(host, previousChangeNumber: 42), new ConnectedSystemImportResult(), CancellationToken.None);

        var search = _sent.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(search.Request.Filter, Is.EqualTo("(changeNumber>=43)"), "LDAP defines >= but not >, so the old (changeNumber>42) was not a valid filter");
            Assert.That(search.Request.DistinguishedName, Is.EqualTo(DefaultChangelogDn));
            Assert.That(search.Request.Scope, Is.EqualTo(SearchScope.OneLevel));
            Assert.That(search.Timeout, Is.EqualTo(Timeout));
        }
    }

    [Test]
    public async Task ReadChangesAsync_Always_RequestsOnlyChangeNumberChangeTypeAndTargetDnAsync()
    {
        var host = new Mock<ILdapDeltaImportHost>();

        await Source().ReadChangesAsync(Context(host), new ConnectedSystemImportResult(), CancellationToken.None);

        Assert.That(_sent.Single().Request.Attributes.Cast<string>(), Is.EquivalentTo(new[] { "changeNumber", "changeType", "targetDN" }),
            "the target's current state is fetched by DN, so the LDIF of the change is not wanted");
    }

    [Test]
    public async Task ReadChangesAsync_RootDseAdvertisesAChangelogDn_SearchesThatDnAsync()
    {
        var host = new Mock<ILdapDeltaImportHost>();
        var context = Context(host, currentRootDse: new LdapConnectorRootDse { ChangelogDn = AdvertisedChangelogDn });

        await Source().ReadChangesAsync(context, new ConnectedSystemImportResult(), CancellationToken.None);

        Assert.That(_sent.Single().Request.DistinguishedName, Is.EqualTo(AdvertisedChangelogDn));
    }

    #endregion

    #region ReadChangesAsync: entries

    [Test]
    public async Task ReadChangesAsync_TargetOutsideSelectedContainers_IsSkippedAsync()
    {
        _searchAnswer = _ => LdapTestResponses.SearchResponseWithEntries(ChangeEntry("modify", OutOfScopeDn));
        var host = new Mock<ILdapDeltaImportHost>();
        var result = new ConnectedSystemImportResult();

        await Source().ReadChangesAsync(Context(host), result, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.ImportObjects, Is.Empty);
            host.Verify(h => h.GetObjectByDn(It.IsAny<string>(), It.IsAny<ObjectChangeType>()), Times.Never);
        }
    }

    [TestCase("add", ObjectChangeType.Added)]
    [TestCase("modify", ObjectChangeType.Updated)]
    [TestCase("modrdn", ObjectChangeType.Updated)]
    [TestCase("moddn", ObjectChangeType.Updated)]
    public async Task ReadChangesAsync_AddModifyModrdnModdn_FetchTheCurrentObjectWithTheMappedChangeTypeAsync(string changeType, ObjectChangeType expected)
    {
        _searchAnswer = _ => LdapTestResponses.SearchResponseWithEntries(ChangeEntry(changeType, InScopeDn));
        var fetched = new ConnectedSystemImportObject { ChangeType = expected };
        var host = new Mock<ILdapDeltaImportHost>();
        host.Setup(h => h.GetObjectByDn(InScopeDn, expected)).Returns(fetched);
        var result = new ConnectedSystemImportResult();

        await Source().ReadChangesAsync(Context(host), result, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            host.Verify(h => h.GetObjectByDn(InScopeDn, expected), Times.Once);
            Assert.That(result.ImportObjects, Is.EqualTo(new[] { fetched }));
        }
    }

    [Test]
    public async Task ReadChangesAsync_Delete_YieldsADeletedImportObjectAsync()
    {
        _searchAnswer = _ => LdapTestResponses.SearchResponseWithEntries(ChangeEntry("delete", InScopeDn));
        var host = new Mock<ILdapDeltaImportHost>();
        var result = new ConnectedSystemImportResult();

        await Source().ReadChangesAsync(Context(host), result, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.ImportObjects, Has.Count.EqualTo(1));
            Assert.That(result.ImportObjects[0].ChangeType, Is.EqualTo(ObjectChangeType.Deleted));
            host.Verify(h => h.GetObjectByDn(It.IsAny<string>(), It.IsAny<ObjectChangeType>()), Times.Never);
        }
    }

    [Test]
    public async Task ReadChangesAsync_UnknownChangeType_FetchesTheObjectAsNotSetAsync()
    {
        _searchAnswer = _ => LdapTestResponses.SearchResponseWithEntries(ChangeEntry("something-new", InScopeDn));
        var host = new Mock<ILdapDeltaImportHost>();

        await Source().ReadChangesAsync(Context(host), new ConnectedSystemImportResult(), CancellationToken.None);

        host.Verify(h => h.GetObjectByDn(InScopeDn, ObjectChangeType.NotSet), Times.Once);
    }

    [Test]
    public async Task ReadChangesAsync_ObjectNoLongerThere_ImportsNothingForItAsync()
    {
        _searchAnswer = _ => LdapTestResponses.SearchResponseWithEntries(ChangeEntry("add", InScopeDn));
        var host = new Mock<ILdapDeltaImportHost>();
        host.Setup(h => h.GetObjectByDn(InScopeDn, ObjectChangeType.Added)).Returns((ConnectedSystemImportObject?)null);
        var result = new ConnectedSystemImportResult();

        await Source().ReadChangesAsync(Context(host), result, CancellationToken.None);

        Assert.That(result.ImportObjects, Is.Empty);
    }

    [Test]
    public async Task ReadChangesAsync_Always_ReportsObjectsReadAsync()
    {
        _searchAnswer = _ => LdapTestResponses.SearchResponseWithEntries(
            ChangeEntry("add", InScopeDn),
            ChangeEntry("delete", InScopeDn),
            ChangeEntry("modify", OutOfScopeDn));
        var host = new Mock<ILdapDeltaImportHost>();
        host.Setup(h => h.GetObjectByDn(InScopeDn, ObjectChangeType.Added)).Returns(new ConnectedSystemImportObject { ChangeType = ObjectChangeType.Added });

        await Source().ReadChangesAsync(Context(host), new ConnectedSystemImportResult(), CancellationToken.None);

        host.Verify(h => h.ReportObjectsReadAsync(2), Times.Once);
    }

    [Test]
    public async Task ReadChangesAsync_CancellationRequested_StopsWithoutFetchingObjectsAsync()
    {
        _searchAnswer = _ => LdapTestResponses.SearchResponseWithEntries(ChangeEntry("add", InScopeDn));
        var host = new Mock<ILdapDeltaImportHost>();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Source().ReadChangesAsync(Context(host), new ConnectedSystemImportResult(), cancellation.Token);

        host.Verify(h => h.GetObjectByDn(It.IsAny<string>(), It.IsAny<ObjectChangeType>()), Times.Never);
    }

    #endregion

    #region ReadChangesAsync: size limit and failures

    [Test]
    public async Task ReadChangesAsync_SizeLimitExceeded_ContinuesFromTheHighestChangeNumberSeenPlusOneAsync()
    {
        _searchAnswer = request => request.Filter switch
        {
            "(changeNumber>=1201)" => throw SizeLimitExceeded(ChangeEntry("add", InScopeDn, 1201), ChangeEntry("add", InScopeDn, 1203), ChangeEntry("add", InScopeDn, 1202)),
            "(changeNumber>=1204)" => LdapTestResponses.SearchResponseWithEntries(ChangeEntry("add", InScopeDn, 1204)),
            _ => throw new InvalidOperationException($"Unexpected filter {request.Filter}")
        };
        var host = new Mock<ILdapDeltaImportHost>();
        host.Setup(h => h.GetObjectByDn(InScopeDn, ObjectChangeType.Added)).Returns(() => new ConnectedSystemImportObject { ChangeType = ObjectChangeType.Added });
        var result = new ConnectedSystemImportResult();

        await Source().ReadChangesAsync(Context(host), result, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_sent.Select(s => s.Request.Filter), Is.EqualTo(new[] { "(changeNumber>=1201)", "(changeNumber>=1204)" }),
                "the change number is an exact cursor, so the walk resumes from the highest seen plus one");
            Assert.That(result.ImportObjects, Has.Count.EqualTo(4), "the partial answer is imported, not discarded");
            host.Verify(h => h.ReportObjectsReadAsync(4), Times.Once);
        }
    }

    [Test]
    public void ReadChangesAsync_SizeLimitExceededWithNothingAnswered_ThrowsRatherThanLoopingAsync()
    {
        _searchAnswer = _ => throw SizeLimitExceeded();
        var host = new Mock<ILdapDeltaImportHost>();

        Assert.That(() => Source().ReadChangesAsync(Context(host), new ConnectedSystemImportResult(), CancellationToken.None),
            Throws.TypeOf<CannotPerformDeltaImportException>().With.Message.Contains("the directory answered no entries within its size limit"));
        Assert.That(_sent, Has.Count.EqualTo(1), "with no entry to advance from, asking again would ask the same question forever");
    }

    [Test]
    public void ReadChangesAsync_ChangelogRefused_ThrowsCannotPerformDeltaImportExceptionAsync()
    {
        _searchAnswer = _ => throw Refused("insufficient access rights");
        var host = new Mock<ILdapDeltaImportHost>();
        var result = new ConnectedSystemImportResult();

        Assert.That(() => Source().ReadChangesAsync(Context(host), result, CancellationToken.None),
            Throws.TypeOf<CannotPerformDeltaImportException>().With.Message.EqualTo(LdapChangelogDeltaSource.DescribeForDeltaImport(DefaultChangelogDn, "insufficient access rights")));
        Assert.That(result.ImportObjects, Is.Empty);
    }

    [Test]
    public void ReadChangesAsync_ChangelogNotFound_ThrowsCannotPerformDeltaImportExceptionAsync()
    {
        _searchAnswer = _ => throw NoSuchObject();
        var host = new Mock<ILdapDeltaImportHost>();

        Assert.That(() => Source().ReadChangesAsync(Context(host), new ConnectedSystemImportResult(), CancellationToken.None),
            Throws.TypeOf<CannotPerformDeltaImportException>().With.Message.EqualTo(LdapChangelogDeltaSource.DescribeForDeltaImport(DefaultChangelogDn, null)));
    }

    [Test]
    public void ReadChangesAsync_ChangelogNotFoundAsLdapException32_ThrowsCannotPerformDeltaImportExceptionAsync()
    {
        _searchAnswer = _ => throw new LdapException(32, "no such object");
        var host = new Mock<ILdapDeltaImportHost>();

        Assert.That(() => Source().ReadChangesAsync(Context(host), new ConnectedSystemImportResult(), CancellationToken.None),
            Throws.TypeOf<CannotPerformDeltaImportException>().With.Message.StartsWith("Changes cannot be detected: the directory provides no changelog at cn=changelog"));
    }

    [Test]
    public void ReadChangesAsync_ConnectionFailure_PropagatesAsync()
    {
        _searchAnswer = _ => throw new LdapException(81, "The LDAP server is unavailable.");
        var host = new Mock<ILdapDeltaImportHost>();

        Assert.That(() => Source().ReadChangesAsync(Context(host), new ConnectedSystemImportResult(), CancellationToken.None),
            Throws.TypeOf<LdapException>(), "a failed connection is an error, not a Delta Import that found nothing");
    }

    #endregion

    #region Helpers

    private static DirectoryOperationException NoSuchObject() =>
        new(LdapTestResponses.Create<SearchResponse>(ResultCode.NoSuchObject), "The object does not exist.");

    private static DirectoryOperationException Refused(string reason) =>
        new(LdapTestResponses.Create<SearchResponse>(ResultCode.InsufficientAccessRights), reason);

    /// <summary>
    /// The directory stopping at its size limit, carrying the entries it did answer, as System.DirectoryServices.Protocols
    /// surfaces it. The shared helpers only build successful responses, so the partial one is assembled here.
    /// </summary>
    private static DirectoryOperationException SizeLimitExceeded(params SearchResultEntry[] partialEntries)
    {
        const BindingFlags nonPublicInstance = BindingFlags.NonPublic | BindingFlags.Instance;
        var entryCollection = (SearchResultEntryCollection)Activator.CreateInstance(typeof(SearchResultEntryCollection), nonPublic: true)!;
        var add = typeof(SearchResultEntryCollection).GetMethod("Add", nonPublicInstance, [typeof(SearchResultEntry)])!;
        foreach (var entry in partialEntries)
            add.Invoke(entryCollection, [entry]);

        var response = LdapTestResponses.Create<SearchResponse>(ResultCode.SizeLimitExceeded);
        typeof(SearchResponse).GetMethod("set_Entries", nonPublicInstance)!.Invoke(response, [entryCollection]);
        return new DirectoryOperationException(response, "The size limit was exceeded");
    }

    private static SearchResultEntry ChangelogEntry(long changeNumber) =>
        LdapTestResponses.Entry($"changeNumber={changeNumber},cn=changelog", ("changeNumber", changeNumber.ToString()));

    private static SearchResultEntry ChangeEntry(string changeType, string targetDn, long changeNumber = PreviousChangeNumber + 1) =>
        LdapTestResponses.Entry($"changeNumber={changeNumber},cn=changelog",
            ("changeNumber", changeNumber.ToString()),
            ("changeType", changeType),
            ("targetDN", targetDn));

    private static SearchResultEntry RootDseEntry() => LdapTestResponses.Entry("", ("vendorName", "389 Project"));

    private static LdapDeltaReadContext Context(Mock<ILdapDeltaImportHost> host, long previousChangeNumber = PreviousChangeNumber, LdapConnectorRootDse? currentRootDse = null) => new()
    {
        PreviousRootDse = new LdapConnectorRootDse { LastChangeNumber = previousChangeNumber },
        CurrentRootDse = currentRootDse ?? new LdapConnectorRootDse { LastChangeNumber = previousChangeNumber + 10 },
        TargetPartitions = [],
        ScopeDecidingContainers = [new ConnectedSystemContainer { ExternalId = ContainerDn, Name = "People", Selected = true }],
        ObjectTypes = [new ConnectedSystemObjectType { Name = "inetOrgPerson", Selected = true }],
        PaginationTokens = [],
        PageSize = 500,
        SearchTimeout = Timeout,
        Notes = new LdapDeltaSourceNotes(),
        Host = host.Object
    };

    #endregion
}
