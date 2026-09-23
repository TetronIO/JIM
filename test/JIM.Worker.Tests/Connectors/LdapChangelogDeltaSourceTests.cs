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
using Serilog.Core;
using Serilog.Events;
using System.DirectoryServices.Protocols;
using System.Reflection;
using System.Text;

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
    private const string PluginDn = "cn=Retro Changelog Plugin,cn=plugins,cn=config";
    private const string LogDeletedAttribute = "nsslapd-log-deleted";
    private const string Uuid = "1c0d8f2e-4d1a-4b2b-9c3e-1e2f3a4b5c6d";
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

        // By default the changelog is there and readable, and empty, and the Retro Changelog plug-in records
        // deleted entries: a base-scope read answers the entry asked for (the plug-in's with nsslapd-log-deleted on)
        // and a search under the changelog answers nothing. Tests that need otherwise replace one or the other.
        _baseReadAnswer = PluginEntryAnswering("on");
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

    private LdapChangelogDeltaSource Source(CapturingSink? sink = null) =>
        new(_executor.Object, sink == null ? Log.Logger : new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(sink).CreateLogger());

    #region VerifyReadinessAsync

    [Test]
    public async Task VerifyReadinessAsync_ChangelogEntryReadable_ReportsAvailableAsync()
    {
        var findings = await Source().VerifyReadinessAsync(new LdapConnectorRootDse(), ["dc=example,dc=com"], CancellationToken.None);

        var finding = findings[0];
        var probe = _sent[0];
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
            Assert.That(_sent[0].Request.DistinguishedName, Is.EqualTo(AdvertisedChangelogDn));
            Assert.That(findings[0].Subject, Is.EqualTo(AdvertisedChangelogDn));
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

    #region VerifyReadinessAsync: deleted-entry recording

    [Test]
    public async Task VerifyReadinessAsync_LogDeletedOn_ReportsTheRetroChangelogPlugInAvailableAsync()
    {
        var findings = await Source().VerifyReadinessAsync(new LdapConnectorRootDse(), [], CancellationToken.None);

        Assert.That(findings, Has.Count.EqualTo(2), "one finding about the changelog, one about the plug-in recording deleted entries");
        var finding = findings[1];
        var probe = _sent[1];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(finding.Subject, Is.EqualTo(PluginDn));
            Assert.That(finding.Outcome, Is.EqualTo(LdapDeltaSourceOutcome.Available));
            Assert.That(finding.Detail, Is.EqualTo($"the Retro Changelog plug-in ({PluginDn}) records deleted entries ({LogDeletedAttribute} is on)"));
            Assert.That(finding.DeltaImportText, Is.Null);
            Assert.That(finding.SchemaDiscoveryText, Is.Null);
            Assert.That(probe.Request.DistinguishedName, Is.EqualTo(PluginDn));
            Assert.That(probe.Request.Scope, Is.EqualTo(SearchScope.Base));
            Assert.That(probe.Request.Filter, Is.EqualTo("(objectClass=*)"));
            Assert.That(probe.Request.Attributes.Cast<string>(), Is.EqualTo(new[] { LogDeletedAttribute }));
            Assert.That(probe.Timeout, Is.Null, "the same connection-level timeout as the changelog probe");
        }
    }

    [Test]
    public async Task VerifyReadinessAsync_LogDeletedOnInAnyCase_ReportsAvailableAsync()
    {
        _baseReadAnswer = PluginEntryAnswering("ON");

        var findings = await Source().VerifyReadinessAsync(new LdapConnectorRootDse(), [], CancellationToken.None);

        Assert.That(findings[1].Outcome, Is.EqualTo(LdapDeltaSourceOutcome.Available));
    }

    [Test]
    public async Task VerifyReadinessAsync_LogDeletedOff_ReportsUnavailableWithBothTextsAsync()
    {
        _baseReadAnswer = PluginEntryAnswering("off");

        var findings = await Source().VerifyReadinessAsync(new LdapConnectorRootDse(), [], CancellationToken.None);

        var finding = findings[1];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(finding.Subject, Is.EqualTo(PluginDn));
            Assert.That(finding.Outcome, Is.EqualTo(LdapDeltaSourceOutcome.Unavailable), "read in full: the plug-in provably does not record deleted entries");
            Assert.That(finding.Detail, Is.EqualTo($"the Retro Changelog plug-in ({PluginDn}) is not recording deleted entries ({LogDeletedAttribute} is off)"));
            Assert.That(finding.DeltaImportText, Is.EqualTo(
                $"Deletions cannot be detected: the Retro Changelog plug-in ({PluginDn}) is not recording deleted entries ({LogDeletedAttribute} is off), " +
                "so objects deleted in the directory would stay in JIM. " +
                $"Set {LogDeletedAttribute} to on on that entry and restart the directory, or run a Full Import, which detects deletions by absence; " +
                "the LDAP Connector documentation, under Service Account Permissions, gives the detail."));
            Assert.That(finding.SchemaDiscoveryText, Is.EqualTo(
                $"The Retro Changelog plug-in ({PluginDn}) is not recording deleted entries ({LogDeletedAttribute} is off), " +
                "so Delta Import is not available until it does; Full Import works as normal and also detects deletions by absence. " +
                $"To use Delta Import, set {LogDeletedAttribute} to on on that entry and restart the directory; " +
                "see the LDAP Connector documentation, Service Account Permissions."));
        }
    }

    [Test]
    public async Task VerifyReadinessAsync_LogDeletedAbsent_ReportsUnavailableAsync()
    {
        _baseReadAnswer = PluginEntryAnswering(null);

        var findings = await Source().VerifyReadinessAsync(new LdapConnectorRootDse(), [], CancellationToken.None);

        var finding = findings[1];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(finding.Outcome, Is.EqualTo(LdapDeltaSourceOutcome.Unavailable), "the entry was read in full and says nothing about recording deleted entries, which is off");
            Assert.That(finding.Detail, Is.EqualTo($"the Retro Changelog plug-in ({PluginDn}) is not recording deleted entries ({LogDeletedAttribute} is not set)"));
            Assert.That(finding.DeltaImportText, Does.StartWith("Deletions cannot be detected:"));
        }
    }

    [Test]
    public async Task VerifyReadinessAsync_PlugInNotFoundAsDirectoryOperationException_ReportsCouldNotDetermineAsync()
    {
        _baseReadAnswer = request => request.DistinguishedName == PluginDn ? throw NoSuchObject() : ChangelogEntryAnswer(request);

        var findings = await Source().VerifyReadinessAsync(new LdapConnectorRootDse(), [], CancellationToken.None);

        var finding = findings[1];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(finding.Outcome, Is.EqualTo(LdapDeltaSourceOutcome.CouldNotDetermine), "389 Directory Server answers noSuchObject for an entry the account may not read, so absence is not proven");
            Assert.That(finding.Detail, Is.EqualTo("the entry was not found, or the account JIM connects as may not read it"));
            Assert.That(finding.DeltaImportText, Is.EqualTo(
                $"JIM could not confirm that the Retro Changelog plug-in ({PluginDn}) records deleted entries: the entry was not found, or the account JIM connects as may not read it. " +
                "Grant the account it connects as read access to that entry (the LDAP Connector documentation, under Service Account Permissions, asks for read on cn=config for 389 Directory Server); " +
                "if the plug-in is not recording deleted entries, Delta Imports would miss deletions."));
            Assert.That(finding.SchemaDiscoveryText, Is.EqualTo(finding.DeltaImportText));
        }
    }

    [Test]
    public async Task VerifyReadinessAsync_PlugInNotFoundAsLdapException32_ReportsCouldNotDetermineAsync()
    {
        _baseReadAnswer = request => request.DistinguishedName == PluginDn ? throw new LdapException(32, "no such object") : ChangelogEntryAnswer(request);

        var findings = await Source().VerifyReadinessAsync(new LdapConnectorRootDse(), [], CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(findings[1].Outcome, Is.EqualTo(LdapDeltaSourceOutcome.CouldNotDetermine));
            Assert.That(findings[1].Detail, Is.EqualTo("the entry was not found, or the account JIM connects as may not read it"));
        }
    }

    [Test]
    public async Task VerifyReadinessAsync_PlugInReadAnsweredWithNoEntry_ReportsCouldNotDetermineAsync()
    {
        _baseReadAnswer = request => request.DistinguishedName == PluginDn ? LdapTestResponses.EmptySearchResponse() : ChangelogEntryAnswer(request);

        var findings = await Source().VerifyReadinessAsync(new LdapConnectorRootDse(), [], CancellationToken.None);

        Assert.That(findings[1].Outcome, Is.EqualTo(LdapDeltaSourceOutcome.CouldNotDetermine));
    }

    [Test]
    public async Task VerifyReadinessAsync_PlugInReadRefused_ReportsCouldNotDetermineWithTheDirectorysReasonAsync()
    {
        _baseReadAnswer = request => request.DistinguishedName == PluginDn ? throw Refused("insufficient access rights") : ChangelogEntryAnswer(request);

        var findings = await Source().VerifyReadinessAsync(new LdapConnectorRootDse(), [], CancellationToken.None);

        var finding = findings[1];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(finding.Outcome, Is.EqualTo(LdapDeltaSourceOutcome.CouldNotDetermine), "a refusal to read the plug-in's entry says nothing about what the entry holds");
            Assert.That(finding.Detail, Is.EqualTo("the directory refused to read it (insufficient access rights)"));
            Assert.That(finding.DeltaImportText, Does.StartWith($"JIM could not confirm that the Retro Changelog plug-in ({PluginDn}) records deleted entries: the directory refused to read it (insufficient access rights)."));
        }
    }

    [Test]
    public async Task VerifyReadinessAsync_PlugInReadConnectionFailure_ReportsCouldNotDetermineAsync()
    {
        _baseReadAnswer = request => request.DistinguishedName == PluginDn ? throw new LdapException(81, "The LDAP server is unavailable.") : ChangelogEntryAnswer(request);

        var findings = await Source().VerifyReadinessAsync(new LdapConnectorRootDse(), [], CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(findings[1].Outcome, Is.EqualTo(LdapDeltaSourceOutcome.CouldNotDetermine));
            Assert.That(findings[1].Detail, Is.EqualTo("the directory answered: The LDAP server is unavailable."));
        }
    }

    [TestCase("not found", Description = "no changelog at all: there is nothing to record deletions in")]
    [TestCase("refused", Description = "the changelog is unavailable: the plug-in's recording is moot")]
    [TestCase("connection failure", Description = "nothing is known about the changelog: a second probe would only fail the same way")]
    public async Task VerifyReadinessAsync_ChangelogNotAvailable_DoesNotProbeThePlugInAsync(string changelogOutcome)
    {
        _baseReadAnswer = _ => changelogOutcome switch
        {
            "not found" => throw NoSuchObject(),
            "refused" => throw Refused("insufficient access rights"),
            _ => throw new LdapException(81, "The LDAP server is unavailable.")
        };

        var findings = await Source().VerifyReadinessAsync(new LdapConnectorRootDse(), [], CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(findings, Has.Count.EqualTo(1), "the plug-in is only worth asking about when there is a changelog to read");
            Assert.That(_sent.Select(s => s.Request.DistinguishedName), Is.EqualTo(new[] { DefaultChangelogDn }), "no second search");
        }
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
    public async Task ReadChangesAsync_Always_RequestsTheChangeItsTargetAndWhatARenameOrDeleteNeedsAsync()
    {
        var host = new Mock<ILdapDeltaImportHost>();

        await Source().ReadChangesAsync(Context(host), new ConnectedSystemImportResult(), CancellationToken.None);

        Assert.That(_sent.Single().Request.Attributes.Cast<string>(), Is.EquivalentTo(new[] { "changeNumber", "changeType", "targetDN", "changes", "newRdn", "newSuperior" }),
            "a delete record's changes carry the deleted entry, and a rename's newRdn and newSuperior name where the object now is");
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
    public async Task ReadChangesAsync_Delete_YieldsADeletedImportObjectWithObjectTypeExternalIdAndDnAsync()
    {
        _searchAnswer = _ => LdapTestResponses.SearchResponseWithEntries(DeleteEntry(InScopeDn, DeletedEntryLdif(Uuid)));
        var host = new Mock<ILdapDeltaImportHost>();
        var result = new ConnectedSystemImportResult();

        await Source().ReadChangesAsync(Context(host), result, CancellationToken.None);

        var deleted = result.ImportObjects.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(deleted.ChangeType, Is.EqualTo(ObjectChangeType.Deleted));
            Assert.That(deleted.ObjectType, Is.EqualTo("inetOrgPerson"), "resolved from the objectClass values in the record's changes, as a live entry's would be");
            Assert.That(deleted.Attributes.Single(a => a.Name == "entryUUID").StringValues, Is.EqualTo(new[] { Uuid }), "the external id the import matches the deletion on");
            Assert.That(deleted.Attributes.Single(a => a.Name == "distinguishedName").StringValues, Is.EqualTo(new[] { InScopeDn }));
            host.Verify(h => h.GetObjectByDn(It.IsAny<string>(), It.IsAny<ObjectChangeType>()), Times.Never, "a deleted object cannot be fetched");
        }
    }

    [Test]
    public async Task ReadChangesAsync_DeleteWithCrLfChanges_IsStillIdentifiedAsync()
    {
        _searchAnswer = _ => LdapTestResponses.SearchResponseWithEntries(DeleteEntry(InScopeDn, DeletedEntryLdif(Uuid).Replace("\n", "\r\n")));
        var result = new ConnectedSystemImportResult();

        await Source().ReadChangesAsync(Context(new Mock<ILdapDeltaImportHost>()), result, CancellationToken.None);

        Assert.That(result.ImportObjects.Single().Attributes.Single(a => a.Name == "entryUUID").StringValues, Is.EqualTo(new[] { Uuid }));
    }

    [Test]
    public async Task ReadChangesAsync_DeleteWithChangesAnsweredAsBytes_IsStillIdentifiedAsync()
    {
        // 389 Directory Server declares changes with Octet String syntax, and System.DirectoryServices.Protocols hands a
        // search-result value back as bytes rather than a string when it is not valid UTF-8 throughout.
        var ldif = Encoding.UTF8.GetBytes(DeletedEntryLdif(Uuid)).Concat(new byte[] { 0xFF }).ToArray();
        _searchAnswer = _ => LdapTestResponses.SearchResponseWithEntries(DeleteEntryWithBinaryChanges(InScopeDn, ldif));
        var result = new ConnectedSystemImportResult();

        await Source().ReadChangesAsync(Context(new Mock<ILdapDeltaImportHost>()), result, CancellationToken.None);

        Assert.That(result.ImportObjects.Single().Attributes.Single(a => a.Name == "entryUUID").StringValues, Is.EqualTo(new[] { Uuid }));
    }

    [Test]
    public async Task ReadChangesAsync_DeleteWithoutChanges_EmitsNothingAndNotesWhyAsync()
    {
        _searchAnswer = _ => LdapTestResponses.SearchResponseWithEntries(ChangeEntry("delete", InScopeDn));
        var notes = new LdapDeltaSourceNotes();
        var log = new CapturingSink();
        var result = new ConnectedSystemImportResult();

        await Source(log).ReadChangesAsync(Context(new Mock<ILdapDeltaImportHost>(), notes: notes), result, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.ImportObjects, Is.Empty, "a delete with no identity would be discarded by the import anyway; better to say so than to stage it");
            Assert.That(notes.Warning, Is.EqualTo(LdapChangelogDeltaSource.DescribeUnidentifiedDeletion(InScopeDn)));
            Assert.That(notes.Warning, Does.Contain(InScopeDn).And.Contain("carries no deleted entry").And.Contain("nsslapd-log-deleted").And.Contain("restart"));
            Assert.That(log.Events.Any(e => e.Level == LogEventLevel.Warning && e.RenderMessage().Contains(InScopeDn)), Is.True);
        }
    }

    [Test]
    public async Task ReadChangesAsync_DeleteWhoseChangesDescribeNoEntry_EmitsNothingAndNotesWhyAsync()
    {
        _searchAnswer = _ => LdapTestResponses.SearchResponseWithEntries(DeleteEntry(InScopeDn, "changetype: delete\n"));
        var notes = new LdapDeltaSourceNotes();
        var result = new ConnectedSystemImportResult();

        await Source().ReadChangesAsync(Context(new Mock<ILdapDeltaImportHost>(), notes: notes), result, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.ImportObjects, Is.Empty);
            Assert.That(notes.Warning, Is.EqualTo(LdapChangelogDeltaSource.DescribeUnidentifiedDeletion(InScopeDn)), "no objectClass and no entryUUID is a record without the deleted entry");
        }
    }

    [Test]
    public async Task ReadChangesAsync_DeleteOfAnUnselectedObjectType_EmitsNothingWithoutANoteAsync()
    {
        _searchAnswer = _ => LdapTestResponses.SearchResponseWithEntries(DeleteEntry(InScopeDn, $"objectClass: top\nobjectClass: organizationalUnit\nentryUUID: {Uuid}\n"));
        var notes = new LdapDeltaSourceNotes();
        var result = new ConnectedSystemImportResult();

        await Source().ReadChangesAsync(Context(new Mock<ILdapDeltaImportHost>(), notes: notes), result, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.ImportObjects, Is.Empty);
            Assert.That(notes.Warning, Is.Null, "the record did carry the deleted entry; it is one this Connected System does not import, which is not a fault");
        }
    }

    [Test]
    public async Task ReadChangesAsync_DeleteOutsideSelectedContainers_IsSkippedBeforeAnyParsingAsync()
    {
        _searchAnswer = _ => LdapTestResponses.SearchResponseWithEntries(ChangeEntry("delete", OutOfScopeDn));
        var notes = new LdapDeltaSourceNotes();
        var log = new CapturingSink();
        var result = new ConnectedSystemImportResult();

        await Source(log).ReadChangesAsync(Context(new Mock<ILdapDeltaImportHost>(), notes: notes), result, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.ImportObjects, Is.Empty);
            Assert.That(notes.Warning, Is.Null, "an out-of-scope deletion, with or without changes, is nothing to warn about");
            Assert.That(log.Events.Any(e => e.Level == LogEventLevel.Warning), Is.False);
        }
    }

    [TestCase("modrdn")]
    [TestCase("moddn")]
    public async Task ReadChangesAsync_RenameWithinTheParent_FetchesTheNewDnAsync(string changeType)
    {
        const string newDn = "uid=jsmith2," + ContainerDn;
        _searchAnswer = _ => LdapTestResponses.SearchResponseWithEntries(RenameEntry(changeType, InScopeDn, "uid=jsmith2", newSuperior: null));
        var fetched = new ConnectedSystemImportObject { ChangeType = ObjectChangeType.Updated };
        var host = new Mock<ILdapDeltaImportHost>();
        host.Setup(h => h.GetObjectByDn(newDn, ObjectChangeType.Updated)).Returns(fetched);
        var result = new ConnectedSystemImportResult();

        await Source().ReadChangesAsync(Context(host), result, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            host.Verify(h => h.GetObjectByDn(newDn, ObjectChangeType.Updated), Times.Once, "targetDN is the DN before the rename; the object now lives at the new RDN under the same parent");
            host.Verify(h => h.GetObjectByDn(InScopeDn, It.IsAny<ObjectChangeType>()), Times.Never, "the old DN answers nothing");
            Assert.That(result.ImportObjects, Is.EqualTo(new[] { fetched }));
        }
    }

    [Test]
    public async Task ReadChangesAsync_RenameWithNewSuperior_FetchesTheNewRdnUnderTheNewSuperiorAsync()
    {
        const string newSuperior = "ou=Staff," + ContainerDn;
        const string newDn = "uid=jsmith," + newSuperior;
        _searchAnswer = _ => LdapTestResponses.SearchResponseWithEntries(RenameEntry("modrdn", InScopeDn, "uid=jsmith", newSuperior));
        var host = new Mock<ILdapDeltaImportHost>();
        host.Setup(h => h.GetObjectByDn(newDn, ObjectChangeType.Updated)).Returns(new ConnectedSystemImportObject { ChangeType = ObjectChangeType.Updated });
        var result = new ConnectedSystemImportResult();

        await Source().ReadChangesAsync(Context(host), result, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            host.Verify(h => h.GetObjectByDn(newDn, ObjectChangeType.Updated), Times.Once);
            host.Verify(h => h.GetObjectByDn(InScopeDn, It.IsAny<ObjectChangeType>()), Times.Never);
            Assert.That(result.ImportObjects, Has.Count.EqualTo(1));
        }
    }

    [Test]
    public async Task ReadChangesAsync_RenameWithoutNewRdn_FallsBackToFetchingTheTargetDnAsync()
    {
        _searchAnswer = _ => LdapTestResponses.SearchResponseWithEntries(ChangeEntry("modrdn", InScopeDn));
        var host = new Mock<ILdapDeltaImportHost>();

        await Source().ReadChangesAsync(Context(host), new ConnectedSystemImportResult(), CancellationToken.None);

        host.Verify(h => h.GetObjectByDn(InScopeDn, ObjectChangeType.Updated), Times.Once, "with no newRdn there is nothing better to fetch than the DN the record names");
    }

    [Test]
    public async Task ReadChangesAsync_MoveOutOfScope_IsSkippedWithoutFetchingAsync()
    {
        _searchAnswer = _ => LdapTestResponses.SearchResponseWithEntries(RenameEntry("modrdn", InScopeDn, "uid=jsmith", "ou=Elsewhere,dc=example,dc=com"));
        var host = new Mock<ILdapDeltaImportHost>();
        var result = new ConnectedSystemImportResult();

        await Source().ReadChangesAsync(Context(host), result, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.ImportObjects, Is.Empty, "the object now lives where a Full Import would not look, so it is not an update");
            host.Verify(h => h.GetObjectByDn(It.IsAny<string>(), It.IsAny<ObjectChangeType>()), Times.Never);
        }
    }

    [Test]
    public async Task ReadChangesAsync_MoveIntoScope_FetchesTheNewDnAsync()
    {
        const string newDn = "uid=jsmith," + ContainerDn;
        _searchAnswer = _ => LdapTestResponses.SearchResponseWithEntries(RenameEntry("modrdn", OutOfScopeDn, "uid=jsmith", ContainerDn));
        var host = new Mock<ILdapDeltaImportHost>();
        host.Setup(h => h.GetObjectByDn(newDn, ObjectChangeType.Updated)).Returns(new ConnectedSystemImportObject { ChangeType = ObjectChangeType.Updated });
        var result = new ConnectedSystemImportResult();

        await Source().ReadChangesAsync(Context(host), result, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            host.Verify(h => h.GetObjectByDn(newDn, ObjectChangeType.Updated), Times.Once, "scope is judged where the object now is, not where it came from");
            Assert.That(result.ImportObjects, Has.Count.EqualTo(1));
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
            DeleteEntry(InScopeDn, DeletedEntryLdif(Uuid)),
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

    #region ReadChangesAsync: repeated changes to one object

    [Test]
    public async Task ReadChangesAsync_TwoModifiesOfOneDnInOnePage_FetchesAndImportsTheObjectOnceAsync()
    {
        // The fetch reads the object's current state, so the second record has nothing more to say; importing it
        // again would have the import report a duplicate external id across pages.
        _searchAnswer = _ => LdapTestResponses.SearchResponseWithEntries(ChangeEntry("modify", InScopeDn, 1201), ChangeEntry("modify", InScopeDn, 1202));
        var host = new Mock<ILdapDeltaImportHost>();
        host.Setup(h => h.GetObjectByDn(InScopeDn, ObjectChangeType.Updated)).Returns(() => new ConnectedSystemImportObject { ChangeType = ObjectChangeType.Updated });
        var log = new CapturingSink();
        var result = new ConnectedSystemImportResult();

        await Source(log).ReadChangesAsync(Context(host), result, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            host.Verify(h => h.GetObjectByDn(InScopeDn, ObjectChangeType.Updated), Times.Once);
            Assert.That(result.ImportObjects, Has.Count.EqualTo(1));
            host.Verify(h => h.ReportObjectsReadAsync(1), Times.Once);
            Assert.That(log.Events.Any(e => e.RenderMessage().Contains("Skipped 1 changelog entries for objects already fetched")), Is.True, "the skipped records are counted, as out-of-scope skips are");
        }
    }

    [Test]
    public async Task ReadChangesAsync_TwoModifiesOfOneDnAcrossTwoPages_FetchesAndImportsTheObjectOnceAsync()
    {
        _searchAnswer = request => request.Filter switch
        {
            "(changeNumber>=1201)" => throw SizeLimitExceeded(ChangeEntry("modify", InScopeDn, 1201)),
            "(changeNumber>=1202)" => LdapTestResponses.SearchResponseWithEntries(ChangeEntry("modify", InScopeDn, 1202)),
            _ => throw new InvalidOperationException($"Unexpected filter {request.Filter}")
        };
        var host = new Mock<ILdapDeltaImportHost>();
        host.Setup(h => h.GetObjectByDn(InScopeDn, ObjectChangeType.Updated)).Returns(() => new ConnectedSystemImportObject { ChangeType = ObjectChangeType.Updated });
        var result = new ConnectedSystemImportResult();

        await Source().ReadChangesAsync(Context(host), result, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_sent.Select(s => s.Request.Filter), Is.EqualTo(new[] { "(changeNumber>=1201)", "(changeNumber>=1202)" }), "both pages were read");
            host.Verify(h => h.GetObjectByDn(InScopeDn, ObjectChangeType.Updated), Times.Once, "the DNs already fetched are remembered across the whole read, not per page");
            Assert.That(result.ImportObjects, Has.Count.EqualTo(1));
        }
    }

    [Test]
    public async Task ReadChangesAsync_AddThenDeleteOfOneDn_YieldsTheFetchedObjectAndTheDeletionAsync()
    {
        // A deletion is never skipped for a DN already seen: an object added then deleted in the same window must
        // still be deleted.
        _searchAnswer = _ => LdapTestResponses.SearchResponseWithEntries(ChangeEntry("add", InScopeDn, 1201), DeleteEntry(InScopeDn, DeletedEntryLdif(Uuid), 1202));
        var host = new Mock<ILdapDeltaImportHost>();
        host.Setup(h => h.GetObjectByDn(InScopeDn, ObjectChangeType.Added)).Returns(new ConnectedSystemImportObject { ChangeType = ObjectChangeType.Added });
        var result = new ConnectedSystemImportResult();

        await Source().ReadChangesAsync(Context(host), result, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            host.Verify(h => h.GetObjectByDn(InScopeDn, ObjectChangeType.Added), Times.Once);
            Assert.That(result.ImportObjects.Select(o => o.ChangeType), Is.EqualTo(new[] { ObjectChangeType.Added, ObjectChangeType.Deleted }));
        }
    }

    [Test]
    public async Task ReadChangesAsync_TwoModifiesOfOneDnDifferingOnlyInCase_FetchesTheObjectOnceAsync()
    {
        const string upperCasedDn = "UID=JSMITH,OU=People,DC=example,DC=com";
        _searchAnswer = _ => LdapTestResponses.SearchResponseWithEntries(ChangeEntry("modify", InScopeDn, 1201), ChangeEntry("modify", upperCasedDn, 1202));
        var host = new Mock<ILdapDeltaImportHost>();
        host.Setup(h => h.GetObjectByDn(It.IsAny<string>(), ObjectChangeType.Updated)).Returns(() => new ConnectedSystemImportObject { ChangeType = ObjectChangeType.Updated });
        var result = new ConnectedSystemImportResult();

        await Source().ReadChangesAsync(Context(host), result, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            host.Verify(h => h.GetObjectByDn(It.IsAny<string>(), It.IsAny<ObjectChangeType>()), Times.Once, "a DN is case-insensitive, so the two records name one object");
            Assert.That(result.ImportObjects, Has.Count.EqualTo(1));
        }
    }

    [Test]
    public async Task ReadChangesAsync_ModifyThenRenameOfOneDn_FetchesTheOldDnOnceAndTheNewDnOnceAsync()
    {
        const string newDn = "uid=jsmith2," + ContainerDn;
        _searchAnswer = _ => LdapTestResponses.SearchResponseWithEntries(ChangeEntry("modify", InScopeDn, 1201), RenameEntry("modrdn", InScopeDn, "uid=jsmith2", newSuperior: null, 1202));
        var host = new Mock<ILdapDeltaImportHost>();
        host.Setup(h => h.GetObjectByDn(It.IsAny<string>(), ObjectChangeType.Updated)).Returns(() => new ConnectedSystemImportObject { ChangeType = ObjectChangeType.Updated });
        var result = new ConnectedSystemImportResult();

        await Source().ReadChangesAsync(Context(host), result, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            host.Verify(h => h.GetObjectByDn(InScopeDn, ObjectChangeType.Updated), Times.Once);
            host.Verify(h => h.GetObjectByDn(newDn, ObjectChangeType.Updated), Times.Once, "a rename's subject is the new DN, which no earlier record named");
            Assert.That(result.ImportObjects, Has.Count.EqualTo(2));
        }
    }

    #endregion

    #region ReadChangesAsync: size limit and failures

    [Test]
    public async Task ReadChangesAsync_SizeLimitExceeded_ContinuesFromTheHighestChangeNumberSeenPlusOneAsync()
    {
        // Four distinct objects: a repeated DN is fetched once per read, which is the previous region's concern.
        _searchAnswer = request => request.Filter switch
        {
            "(changeNumber>=1201)" => throw SizeLimitExceeded(ChangeEntry("add", PersonDn("a1"), 1201), ChangeEntry("add", PersonDn("a3"), 1203), ChangeEntry("add", PersonDn("a2"), 1202)),
            "(changeNumber>=1204)" => LdapTestResponses.SearchResponseWithEntries(ChangeEntry("add", PersonDn("a4"), 1204)),
            _ => throw new InvalidOperationException($"Unexpected filter {request.Filter}")
        };
        var host = new Mock<ILdapDeltaImportHost>();
        host.Setup(h => h.GetObjectByDn(It.IsAny<string>(), ObjectChangeType.Added)).Returns(() => new ConnectedSystemImportObject { ChangeType = ObjectChangeType.Added });
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

    private static string PersonDn(string uid) => $"uid={uid},{ContainerDn}";

    private static SearchResultEntry ChangelogEntry(long changeNumber) =>
        LdapTestResponses.Entry($"changeNumber={changeNumber},cn=changelog", ("changeNumber", changeNumber.ToString()));

    private static SearchResultEntry ChangeEntry(string changeType, string targetDn, long changeNumber = PreviousChangeNumber + 1) =>
        LdapTestResponses.Entry($"changeNumber={changeNumber},cn=changelog",
            ("changeNumber", changeNumber.ToString()),
            ("changeType", changeType),
            ("targetDN", targetDn));

    /// <summary>
    /// A delete record as 389 Directory Server's Retro Changelog writes it once nsslapd-log-deleted is on: the
    /// changes attribute holds the deleted entry as LDIF text, without its dn line.
    /// </summary>
    private static SearchResultEntry DeleteEntry(string targetDn, string changes, long changeNumber = PreviousChangeNumber + 1) =>
        LdapTestResponses.Entry($"changeNumber={changeNumber},cn=changelog",
            ("changeNumber", changeNumber.ToString()),
            ("changeType", "delete"),
            ("targetDN", targetDn),
            ("changes", changes));

    /// <summary>A delete record whose changes value arrives as bytes, as a value that is not valid UTF-8 does.</summary>
    private static SearchResultEntry DeleteEntryWithBinaryChanges(string targetDn, byte[] changes, long changeNumber = PreviousChangeNumber + 1)
    {
        const BindingFlags nonPublicInstance = BindingFlags.NonPublic | BindingFlags.Instance;
        var attributeCollection = (SearchResultAttributeCollection)Activator.CreateInstance(typeof(SearchResultAttributeCollection), nonPublic: true)!;
        var add = typeof(SearchResultAttributeCollection).GetMethod("Add", nonPublicInstance, [typeof(string), typeof(DirectoryAttribute)])!;

        add.Invoke(attributeCollection, ["changeNumber", new DirectoryAttribute("changeNumber", changeNumber.ToString())]);
        add.Invoke(attributeCollection, ["changeType", new DirectoryAttribute("changeType", "delete")]);
        add.Invoke(attributeCollection, ["targetDN", new DirectoryAttribute("targetDN", targetDn)]);
        add.Invoke(attributeCollection, ["changes", new DirectoryAttribute("changes", changes)]);

        return (SearchResultEntry)Activator.CreateInstance(typeof(SearchResultEntry), nonPublicInstance, binder: null,
            args: [$"changeNumber={changeNumber},cn=changelog", attributeCollection], culture: null)!;
    }

    private static string DeletedEntryLdif(string entryUuid) =>
        $"objectClass: top\nobjectClass: person\nobjectClass: inetOrgPerson\nuid: jsmith\nentryUUID: {entryUuid}\n";

    /// <summary>A modrdn record: targetDN is the DN before the rename, newRdn and newSuperior say where the entry went.</summary>
    private static SearchResultEntry RenameEntry(string changeType, string targetDn, string newRdn, string? newSuperior, long changeNumber = PreviousChangeNumber + 1)
    {
        var attributes = new List<(string Name, string Value)>
        {
            ("changeNumber", changeNumber.ToString()),
            ("changeType", changeType),
            ("targetDN", targetDn),
            ("newRdn", newRdn),
            ("deleteOldRdn", "TRUE")
        };
        if (newSuperior != null)
            attributes.Add(("newSuperior", newSuperior));

        return LdapTestResponses.Entry($"changeNumber={changeNumber},cn=changelog", [.. attributes]);
    }

    private static SearchResultEntry RootDseEntry() => LdapTestResponses.Entry("", ("vendorName", "389 Project"));

    /// <summary>The changelog's own entry, as the default base-scope read answers it.</summary>
    private static SearchResponse ChangelogEntryAnswer(SearchRequest request) =>
        LdapTestResponses.SearchResponseWithEntries(LdapTestResponses.Entry(request.DistinguishedName, ("objectClass", "top")));

    /// <summary>
    /// A base-scope read answering the Retro Changelog plug-in's entry with the given nsslapd-log-deleted value
    /// (none when null), and any other entry (the changelog's) as readable.
    /// </summary>
    private static Func<SearchRequest, SearchResponse> PluginEntryAnswering(string? logDeleted) => request =>
        request.DistinguishedName != PluginDn ? ChangelogEntryAnswer(request)
        : logDeleted == null ? LdapTestResponses.SearchResponseWithEntries(LdapTestResponses.Entry(PluginDn, ("objectClass", "top")))
        : LdapTestResponses.SearchResponseWithEntries(LdapTestResponses.Entry(PluginDn, ("objectClass", "top"), (LogDeletedAttribute, logDeleted)));

    private static LdapDeltaReadContext Context(Mock<ILdapDeltaImportHost> host, long previousChangeNumber = PreviousChangeNumber, LdapConnectorRootDse? currentRootDse = null, LdapDeltaSourceNotes? notes = null) => new()
    {
        PreviousRootDse = new LdapConnectorRootDse { LastChangeNumber = previousChangeNumber },
        CurrentRootDse = currentRootDse ?? new LdapConnectorRootDse { LastChangeNumber = previousChangeNumber + 10 },
        TargetPartitions = [],
        ScopeDecidingContainers = [new ConnectedSystemContainer { ExternalId = ContainerDn, Name = "People", Selected = true }],
        ObjectTypes = [InetOrgPerson()],
        PaginationTokens = [],
        PageSize = 500,
        SearchTimeout = Timeout,
        Notes = notes ?? new LdapDeltaSourceNotes(),
        Host = host.Object
    };

    private static ConnectedSystemObjectType InetOrgPerson()
    {
        var objectType = new ConnectedSystemObjectType { Id = 1, Name = "inetOrgPerson", Selected = true };
        objectType.Attributes.Add(new ConnectedSystemObjectTypeAttribute { Id = 1, Name = "entryUUID", Type = AttributeDataType.Text, Selected = true, IsExternalId = true });
        objectType.Attributes.Add(new ConnectedSystemObjectTypeAttribute { Id = 2, Name = "distinguishedName", Type = AttributeDataType.Text, Selected = true });
        return objectType;
    }

    /// <summary>Collects what the source logs, so a test can assert on the warning it raises.</summary>
    private sealed class CapturingSink : ILogEventSink
    {
        internal List<LogEvent> Events { get; } = [];

        public void Emit(LogEvent logEvent) => Events.Add(logEvent);
    }

    #endregion
}
