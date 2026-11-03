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
    private const string CorpDn = "dc=corp,dc=local";
    private const string PartnersDn = "dc=partners,dc=local";
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
    public async Task CaptureWatermarkAsync_RootDseAdvertisesLastChangeNumber_RecordsItWithoutEnumeratingAsync()
    {
        var rootDse = new LdapConnectorRootDse { AdvertisedLastChangeNumber = 4321 };

        var captured = await Source().CaptureWatermarkAsync(rootDse, Timeout);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(captured?.ChangeNumber, Is.EqualTo(4321));
            Assert.That(_sent.Select(s => s.Request.Scope), Is.All.EqualTo(SearchScope.Base), "the rootDSE already said; there is nothing to enumerate");
        }
    }

    [Test]
    public async Task CaptureWatermarkAsync_WatermarkEntryRecordsAChangeTime_RecordsItFromABaseReadOfThatEntryAsync()
    {
        // The next Delta Import reads change 4321 back and compares its changeTime, to prove the number still names
        // the same change rather than one written after a restore reused it (#2008).
        _baseReadAnswer = request => WatermarkEntryAnswer(request, "changeNumber=4321,cn=changelog", "20261007190130Z");
        var rootDse = new LdapConnectorRootDse { AdvertisedLastChangeNumber = 4321 };

        var captured = await Source().CaptureWatermarkAsync(rootDse, Timeout);

        var read = _sent.Single(s => s.Request.DistinguishedName == "changeNumber=4321,cn=changelog");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(captured?.ChangeTime, Is.EqualTo("20261007190130Z"));
            Assert.That(read.Request.Scope, Is.EqualTo(SearchScope.Base));
            Assert.That(read.Request.Attributes.Cast<string>(), Is.EqualTo(new[] { "changeTime" }));
            Assert.That(read.Timeout, Is.EqualTo(Timeout), "the read honours the import's search timeout");
        }
    }

    [Test]
    public async Task CaptureWatermarkAsync_AdvertisedChangelogDn_ReadsTheWatermarkEntryThereAsync()
    {
        _baseReadAnswer = request => WatermarkEntryAnswer(request, $"changeNumber=7,{AdvertisedChangelogDn}", "20261007190130Z");
        var rootDse = new LdapConnectorRootDse { AdvertisedLastChangeNumber = 7, ChangelogDn = AdvertisedChangelogDn };

        var captured = await Source().CaptureWatermarkAsync(rootDse, Timeout);

        Assert.That(captured?.ChangeTime, Is.EqualTo("20261007190130Z"));
    }

    [Test]
    public async Task CaptureWatermarkAsync_WatermarkEntryMissing_RecordsNoChangeTimeAsync()
    {
        // 389 Directory Server after an online restore advertises a lastChangeNumber whose entry the restore removed.
        _baseReadAnswer = _ => throw NoSuchObject();
        var rootDse = new LdapConnectorRootDse { AdvertisedLastChangeNumber = 5 };

        var captured = await Source().CaptureWatermarkAsync(rootDse, Timeout);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(captured?.ChangeNumber, Is.EqualTo(5), "the watermark itself stands");
            Assert.That(captured?.ChangeTime, Is.Null, "with no entry there is nothing for the next Delta Import to compare");
        }
    }

    [Test]
    public async Task CaptureWatermarkAsync_WatermarkEntryReadFails_KeepsTheWatermarkAndRecordsNoChangeTimeAsync()
    {
        _baseReadAnswer = _ => throw new LdapException(81, "The LDAP server is unavailable.");
        var rootDse = new LdapConnectorRootDse { AdvertisedLastChangeNumber = 5 };

        var captured = await Source().CaptureWatermarkAsync(rootDse, Timeout);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(captured?.ChangeNumber, Is.EqualTo(5));
            Assert.That(captured?.ChangeTime, Is.Null, "a check the next Delta Import cannot make is skipped, never failed on");
        }
    }

    [Test]
    public async Task CaptureWatermarkAsync_EmptyChangelog_ReadsNoWatermarkEntryAsync()
    {
        var rootDse = new LdapConnectorRootDse();

        var captured = await Source().CaptureWatermarkAsync(rootDse, Timeout);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(captured?.ChangeTime, Is.Null);
            Assert.That(_sent.Select(s => s.Request.DistinguishedName), Has.None.StartsWith("changeNumber="), "change number zero names no change");
        }
    }

    [Test]
    public async Task CaptureWatermarkAsync_NotAdvertised_RecordsTheHighestChangeNumberRegardlessOfOrderAsync()
    {
        _searchAnswer = _ => LdapTestResponses.SearchResponseWithEntries(ChangelogEntry(7), ChangelogEntry(12), ChangelogEntry(9));
        var rootDse = new LdapConnectorRootDse();

        var captured = await Source().CaptureWatermarkAsync(rootDse, Timeout);

        var enumeration = _sent.Single(s => s.Request.Scope != SearchScope.Base);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(captured?.ChangeNumber, Is.EqualTo(12), "LDAP guarantees no order, so the last entry by position is not the newest");
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

        var captured = await Source().CaptureWatermarkAsync(rootDse, Timeout);

        Assert.That(captured?.ChangeNumber, Is.EqualTo(45), "below the truth is the safe direction: the next Delta Import re-reads a few changes rather than skipping any");
    }

    [Test]
    public async Task CaptureWatermarkAsync_ChangelogEmpty_RecordsZeroAsync()
    {
        var rootDse = new LdapConnectorRootDse();

        var captured = await Source().CaptureWatermarkAsync(rootDse, Timeout);

        Assert.That(captured?.ChangeNumber, Is.Zero, "a readable, empty changelog is a baseline: every change from here on is numbered above zero");
    }

    [Test]
    public async Task CaptureWatermarkAsync_ChangelogNotFound_LeavesTheWatermarkNullAsync()
    {
        _baseReadAnswer = _ => throw NoSuchObject();
        var rootDse = new LdapConnectorRootDse();

        var captured = await Source().CaptureWatermarkAsync(rootDse, Timeout);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(captured, Is.Null, "null makes the next Delta Import run as a Full Import; zero would read as a real baseline");
            Assert.That(_sent.Select(s => s.Request.Scope), Is.EqualTo(new[] { SearchScope.Base }), "with no changelog there is nothing to enumerate");
        }
    }

    [Test]
    public async Task CaptureWatermarkAsync_ChangelogRefused_LeavesTheWatermarkNullAsync()
    {
        _baseReadAnswer = _ => throw Refused("insufficient access rights");
        var rootDse = new LdapConnectorRootDse();

        var captured = await Source().CaptureWatermarkAsync(rootDse, Timeout);

        Assert.That(captured, Is.Null);
    }

    [Test]
    public async Task CaptureWatermarkAsync_ConnectionFailure_LeavesTheWatermarkNullAsync()
    {
        _baseReadAnswer = _ => throw new LdapException(81, "The LDAP server is unavailable.");
        var rootDse = new LdapConnectorRootDse();

        var captured = await Source().CaptureWatermarkAsync(rootDse, Timeout);

        Assert.That(captured, Is.Null);
    }

    [Test]
    public async Task CaptureWatermarkAsync_RootDseAdvertisesAChangelogDn_EnumeratesThatDnAsync()
    {
        _searchAnswer = _ => LdapTestResponses.SearchResponseWithEntries(ChangelogEntry(3));
        var rootDse = new LdapConnectorRootDse { ChangelogDn = AdvertisedChangelogDn };

        var captured = await Source().CaptureWatermarkAsync(rootDse, Timeout);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_sent.Select(s => s.Request.DistinguishedName), Is.All.EndsWith(AdvertisedChangelogDn), "the changelog and its entries, never the default cn=changelog");
            Assert.That(captured?.ChangeNumber, Is.EqualTo(3));
        }
    }

    [Test]
    public async Task CaptureWatermarkAsync_ChangeNumberNotAnInteger_IgnoresThatEntryAsync()
    {
        _searchAnswer = _ => LdapTestResponses.SearchResponseWithEntries(
            ChangelogEntry(8),
            LdapTestResponses.Entry("changeNumber=abc,cn=changelog", ("changeNumber", "abc")));
        var rootDse = new LdapConnectorRootDse();

        var captured = await Source().CaptureWatermarkAsync(rootDse, Timeout);

        Assert.That(captured?.ChangeNumber, Is.EqualTo(8));
    }

    [Test]
    public async Task CaptureWatermarksAsync_AssignsTheAdvertisedLastChangeNumberToEveryTargetPartitionAsync()
    {
        // The changelog is directory-wide and the capture is taken before anything is read, so one capture is safe for
        // every target partition, whatever each one's previous watermark: each partition's change read starts at or
        // before its own watermark, never after the capture.
        _baseReadAnswer = request => WatermarkEntryAnswer(request, "changeNumber=4321,cn=changelog", "20261007190130Z");
        var rootDse = new LdapConnectorRootDse { AdvertisedLastChangeNumber = 4321 };
        var previous = new Dictionary<int, LdapPartitionWatermark?> { [Corp().Id] = ChangelogWatermark(1500), [Partners().Id] = ChangelogWatermark(1200) };

        var captured = await Source().CaptureWatermarksAsync(RootDseEntry(), rootDse, previous, [Corp(), Partners()], Timeout);

        var expected = new LdapPartitionWatermark { Kind = LdapDeltaSourceKind.Changelog, ChangeNumber = 4321, ChangeTime = "20261007190130Z" };
        using (Assert.EnterMultipleScope())
        {
            Assert.That(captured.Keys, Is.EquivalentTo(new[] { Corp().Id, Partners().Id }), "a watermark for every target partition and no other");
            Assert.That(captured[Corp().Id], Is.EqualTo(expected));
            Assert.That(captured[Partners().Id], Is.EqualTo(expected), "the previous watermarks do not narrow a changelog capture");
            Assert.That(_sent.Count(s => s.Request.DistinguishedName == "changeNumber=4321,cn=changelog"), Is.EqualTo(1), "the capture is taken once for the run");
            Assert.That(_sent.Select(s => s.Request.Scope), Is.All.EqualTo(SearchScope.Base), "the rootDSE already said; there is nothing to enumerate");
        }
    }

    #endregion

    #region VerifyContinuity

    [Test]
    public void VerifyContinuity_ChangelogTrimmedPastOnePartitionsWatermark_ThrowsNamingThatPartitionAsync()
    {
        // The changelog now starts at 150. partners.local's last import ended at 200, so every change it has not read is
        // still held; corp.local's ended at 100, so the changes between were trimmed before it read them.
        var current = new LdapConnectorRootDse { FirstChangeNumber = 150 };

        Assert.That(() => Source().VerifyContinuity([Partners()], ChangelogWatermark(200), current), Throws.Nothing);

        var ex = Assert.Throws<CannotPerformDeltaImportException>(() => Source().VerifyContinuity([Corp()], ChangelogWatermark(100), current));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ex!.Message, Does.Contain("the last import of the partition corp.local"), "the refusal names the partition whose watermark was trimmed past");
            Assert.That(ex.Message, Does.Contain("starts at change number 150").And.Contain("ended at 100"));
            Assert.That(ex.Message, Does.Contain("Run a Full Import that reads the partition corp.local"),
                "a Full Import of another partition no longer re-establishes this one's baseline");
        }
    }

    [Test]
    public void VerifyContinuity_OnePartitionsWatermarkChangeNowHasAnotherChangeTime_ThrowsAsync()
    {
        // corp.local's watermark change 5 still names the change it recorded; partners.local's change 7 now names one
        // written after a restore reused its number (#2008).
        _baseReadAnswer = request => request.DistinguishedName switch
        {
            "changeNumber=5,cn=changelog" => WatermarkEntryAnswer(request, "changeNumber=5,cn=changelog", "20261007190130Z"),
            "changeNumber=7,cn=changelog" => WatermarkEntryAnswer(request, "changeNumber=7,cn=changelog", "20261008090000Z"),
            _ => ChangelogEntryAnswer(request)
        };
        var current = new LdapConnectorRootDse { FirstChangeNumber = 1, AdvertisedLastChangeNumber = 9 };

        Assert.That(() => Source().VerifyContinuity([Corp()], ChangelogWatermark(5, "20261007190130Z"), current), Throws.Nothing);

        var ex = Assert.Throws<CannotPerformDeltaImportException>(() => Source().VerifyContinuity([Partners()], ChangelogWatermark(7, "20261007190700Z"), current));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ex!.Message, Does.Contain("the last import of the partition partners.local ended at"), "the refusal names the partition whose watermark no longer holds");
            Assert.That(ex.Message, Does.Contain("change number 7").And.Contain("20261007190700Z").And.Contain("20261008090000Z"));
            Assert.That(ex.Message, Does.Contain("Run a Full Import that reads the partition partners.local"));
        }
    }

    [Test]
    public void VerifyContinuity_PreviousWatermarkBelowTheAdvertisedFirstChangeNumber_ThrowsNamingTheTrim()
    {
        var previous = ChangelogWatermark(100);
        var current = new LdapConnectorRootDse { FirstChangeNumber = 150 };

        Assert.That(() => Source().VerifyContinuity([Partition()], previous, current),
            Throws.TypeOf<CannotPerformDeltaImportException>().With.Message.EqualTo(
                "The directory's changelog no longer holds the changes since the last import of the partition example.com: it starts at change number 150, " +
                "and that import ended at 100, so changes between them were trimmed (389 Directory Server: the Retro Changelog plug-in's maximum age) " +
                "and cannot be imported. Run a Full Import that reads the partition example.com to re-establish the baseline; " +
                "a Full Import also detects deletions by absence."));
    }

    [TestCase(149, 150, Description = "the next change to read is the first one held")]
    [TestCase(150, 150, Description = "the watermark itself is still held")]
    [TestCase(200, 150, Description = "the watermark is well within the changelog")]
    public void VerifyContinuity_PreviousWatermarkAtOrAboveTheFirstChangeNumber_DoesNotThrow(long previousWatermark, long firstChangeNumber)
    {
        var previous = ChangelogWatermark(previousWatermark);
        var current = new LdapConnectorRootDse { FirstChangeNumber = firstChangeNumber };

        Assert.That(() => Source().VerifyContinuity([Partition()], previous, current), Throws.Nothing);
    }

    [Test]
    public void VerifyContinuity_FirstChangeNumberNotAdvertised_DoesNotThrow()
    {
        var previous = ChangelogWatermark(1);
        var current = new LdapConnectorRootDse { FirstChangeNumber = null, AdvertisedLastChangeNumber = 5000 };

        Assert.That(() => Source().VerifyContinuity([Partition()], previous, current), Throws.Nothing);
        Assert.That(_sent, Is.Empty, "continuity is judged from the rootDSE alone");
    }

    [Test]
    public void VerifyContinuity_NoPreviousWatermark_DoesNotThrow()
    {
        Assert.That(() => Source().VerifyContinuity([Partition()], new LdapPartitionWatermark { Kind = LdapDeltaSourceKind.Changelog }, new LdapConnectorRootDse { FirstChangeNumber = 150 }), Throws.Nothing);
    }

    [Test]
    public void VerifyContinuity_AdvertisedLastChangeNumberBelowTheWatermark_ThrowsNamingBothNumbersTheRestoreAndTheRemedy()
    {
        // 389 Directory Server restored from a copy of its files taken at change 3, after an import recorded 8 (#2004).
        var previous = ChangelogWatermark(8);
        var current = new LdapConnectorRootDse { FirstChangeNumber = 1, AdvertisedLastChangeNumber = 3 };

        var ex = Assert.Throws<CannotPerformDeltaImportException>(() => Source().VerifyContinuity([Partition()], previous, current));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ex!.Message, Does.Contain("its newest change number is 3"), "the directory's newest change");
            Assert.That(ex.Message, Does.Contain("since the last import of the partition example.com").And.Contain("that import ended at 8"), "the recorded watermark, and whose it is");
            Assert.That(ex.Message, Does.Contain("restored from a backup or snapshot"), "the usual cause");
            Assert.That(ex.Message, Does.Contain("silently miss"), "why the run must not carry on");
            Assert.That(ex.Message, Does.Contain("Run a Full Import"), "what to do");
        }
    }

    [Test]
    public void VerifyContinuity_EnumeratedLastChangeNumberBelowTheWatermark_DoesNotThrow()
    {
        // Without an advertised lastChangeNumber the watermark comes from enumerating the changelog, which can stop at
        // the directory's size limit short of the newest change, so a lower number there proves nothing.
        var previous = ChangelogWatermark(8);
        var current = new LdapConnectorRootDse { AdvertisedLastChangeNumber = null };

        Assert.That(() => Source().VerifyContinuity([Partition()], previous, current), Throws.Nothing);
    }

    [TestCase(8L, 8L, Description = "nothing has changed since the watermark")]
    [TestCase(8L, 9L, Description = "the changelog has moved on")]
    public void VerifyContinuity_AdvertisedLastChangeNumberAtOrAboveTheWatermark_DoesNotThrow(long previousWatermark, long advertisedLast)
    {
        var previous = ChangelogWatermark(previousWatermark);
        var current = new LdapConnectorRootDse { FirstChangeNumber = 1, AdvertisedLastChangeNumber = advertisedLast };

        Assert.That(() => Source().VerifyContinuity([Partition()], previous, current), Throws.Nothing);
    }

    [Test]
    public void VerifyContinuity_WatermarkNumberNowNamesADifferentChange_ThrowsNamingTheReuseTheRestoreAndTheRemedy()
    {
        // Restored from a backup, then written to until the numbering passed the watermark again: change 5 exists, but
        // it is not the change the last import ended at (#2008).
        _baseReadAnswer = request => WatermarkEntryAnswer(request, "changeNumber=5,cn=changelog", "20261007190500Z");
        var previous = ChangelogWatermark(5, "20261007190130Z");
        var current = new LdapConnectorRootDse { FirstChangeNumber = 1, AdvertisedLastChangeNumber = 9 };

        var ex = Assert.Throws<CannotPerformDeltaImportException>(() => Source().VerifyContinuity([Partition()], previous, current));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ex!.Message, Does.Contain("change number 5"), "the change the last import ended at");
            Assert.That(ex.Message, Does.Contain("20261007190130Z").And.Contain("20261007190500Z"), "both changes, by when they were recorded");
            Assert.That(ex.Message, Does.Contain("restored from a backup or snapshot"), "the usual cause");
            Assert.That(ex.Message, Does.Contain("silently miss"), "why the run must not carry on");
            Assert.That(ex.Message, Does.Contain("Run a Full Import"), "what to do");
            Assert.That(_sent.Single().Request.Scope, Is.EqualTo(SearchScope.Base));
        }
    }

    [Test]
    public void VerifyContinuity_WatermarkEntryGoneWhileOlderChangesRemain_ThrowsNamingTheRestore()
    {
        // 389 Directory Server's online restore removes the changes made after the backup from its changelog and
        // carries on numbering after them, so the newest number never goes back and only the hole shows (#2008).
        _baseReadAnswer = _ => throw NoSuchObject();
        var previous = ChangelogWatermark(5, "20261007190130Z");
        var current = new LdapConnectorRootDse { FirstChangeNumber = 1, AdvertisedLastChangeNumber = 9 };

        var ex = Assert.Throws<CannotPerformDeltaImportException>(() => Source().VerifyContinuity([Partition()], previous, current));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ex!.Message, Does.Contain("change number 5"));
            Assert.That(ex.Message, Does.Contain("restored from a backup or snapshot"));
            Assert.That(ex.Message, Does.Contain("Run a Full Import"));
        }
    }

    [Test]
    public void VerifyContinuity_WatermarkEntryGoneAsLdapException32WhileOlderChangesRemain_Throws()
    {
        _baseReadAnswer = _ => throw new LdapException(32, "no such object");
        var previous = ChangelogWatermark(5, "20261007190130Z");
        var current = new LdapConnectorRootDse { FirstChangeNumber = 1, AdvertisedLastChangeNumber = 9 };

        Assert.That(() => Source().VerifyContinuity([Partition()], previous, current), Throws.TypeOf<CannotPerformDeltaImportException>());
    }

    [Test]
    public void VerifyContinuity_WatermarkEntryTrimmedAndTheNextChangeIsTheOldestHeld_DoesNotThrow()
    {
        // Trimming removed the watermark's entry and nothing after it: every change since is still there to read.
        _baseReadAnswer = _ => throw NoSuchObject();
        var previous = ChangelogWatermark(5, "20261007190130Z");
        var current = new LdapConnectorRootDse { FirstChangeNumber = 6, AdvertisedLastChangeNumber = 9 };

        Assert.That(() => Source().VerifyContinuity([Partition()], previous, current), Throws.Nothing);
    }

    [Test]
    public void VerifyContinuity_WatermarkEntryGoneAndFirstChangeNumberNotAdvertised_DoesNotThrow()
    {
        // Trimmed or restored cannot be told apart without knowing where the changelog now starts.
        _baseReadAnswer = _ => throw NoSuchObject();
        var previous = ChangelogWatermark(5, "20261007190130Z");
        var current = new LdapConnectorRootDse();

        Assert.That(() => Source().VerifyContinuity([Partition()], previous, current), Throws.Nothing);
    }

    [Test]
    public void VerifyContinuity_WatermarkEntryUnchanged_DoesNotThrow()
    {
        _baseReadAnswer = request => WatermarkEntryAnswer(request, "changeNumber=5,cn=changelog", "20261007190130Z");
        var previous = ChangelogWatermark(5, "20261007190130Z");
        var current = new LdapConnectorRootDse { FirstChangeNumber = 1, AdvertisedLastChangeNumber = 9 };

        Assert.That(() => Source().VerifyContinuity([Partition()], previous, current), Throws.Nothing);
    }

    [Test]
    public void VerifyContinuity_NoRecordedChangeTime_ReadsNothing()
    {
        var previous = ChangelogWatermark(5);
        var current = new LdapConnectorRootDse { FirstChangeNumber = 1, AdvertisedLastChangeNumber = 9 };

        Assert.That(() => Source().VerifyContinuity([Partition()], previous, current), Throws.Nothing);
        Assert.That(_sent, Is.Empty, "a record from before JIM kept the changeTime, or one whose entry had none, has nothing to compare");
    }

    [Test]
    public void VerifyContinuity_WatermarkEntryReadRefused_DoesNotThrow()
    {
        _baseReadAnswer = _ => throw Refused("insufficient access rights");
        var previous = ChangelogWatermark(5, "20261007190130Z");
        var current = new LdapConnectorRootDse { FirstChangeNumber = 1, AdvertisedLastChangeNumber = 9 };

        Assert.That(() => Source().VerifyContinuity([Partition()], previous, current), Throws.Nothing, "a refusal says nothing about the entry; the readiness check names it");
    }

    [Test]
    public void VerifyContinuity_ConnectionFailure_Propagates()
    {
        _baseReadAnswer = _ => throw new LdapException(81, "The LDAP server is unavailable.");
        var previous = ChangelogWatermark(5, "20261007190130Z");
        var current = new LdapConnectorRootDse { FirstChangeNumber = 1, AdvertisedLastChangeNumber = 9 };

        Assert.That(() => Source().VerifyContinuity([Partition()], previous, current), Throws.TypeOf<LdapException>());
    }

    #endregion

    #region HasBaseline

    [Test]
    public void HasBaseline_NoChangeNumber_IsFalse()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Source().HasBaseline(null), Is.False);
            Assert.That(Source().HasBaseline(new LdapPartitionWatermark { Kind = LdapDeltaSourceKind.Changelog }), Is.False);
        }
    }

    [Test]
    public void HasBaseline_ChangeNumberPresent_IsTrue() =>
        Assert.That(Source().HasBaseline(ChangelogWatermark(0)), Is.True);

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
    public async Task ReadChangesAsync_Always_RequestsTheChangeItsTargetAndWhatARenameNeedsButNotChangesAsync()
    {
        var host = new Mock<ILdapDeltaImportHost>();

        await Source().ReadChangesAsync(Context(host), new ConnectedSystemImportResult(), CancellationToken.None);

        Assert.That(_sent.Single().Request.Attributes.Cast<string>(), Is.EquivalentTo(new[] { "changeNumber", "changeType", "targetDN", "newRdn", "newSuperior" }),
            "a rename's newRdn and newSuperior name where the object now is; changes holds an add's whole entry, a large group's every member, and only a deletion needs it");
    }

    [Test]
    public async Task ReadChangesAsync_Always_AsksForNoMoreThanTheRunProfilesPageSizeAsync()
    {
        await Source().ReadChangesAsync(Context(new Mock<ILdapDeltaImportHost>()), new ConnectedSystemImportResult(), CancellationToken.None);

        Assert.That(_sent.Single().Request.SizeLimit, Is.EqualTo(500));
    }

    [Test]
    public async Task ReadChangesAsync_Delete_ReadsChangesFromItsOwnRecordAndForNothingElseAsync()
    {
        ChangelogHolds(ChangeEntry("modify", PersonDn("a1"), 1201), DeleteEntry(InScopeDn, DeletedEntryLdif(Uuid), 1202));
        var host = new Mock<ILdapDeltaImportHost>();
        host.Setup(h => h.GetObjectByDn(It.IsAny<string>(), It.IsAny<ObjectChangeType>())).Returns(new ConnectedSystemImportObject { ChangeType = ObjectChangeType.Updated });

        await Source().ReadChangesAsync(Context(host), new ConnectedSystemImportResult(), CancellationToken.None);

        var recordReads = _sent.Where(s => s.Request.Scope == SearchScope.Base).ToList();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(recordReads.Select(s => s.Request.DistinguishedName), Is.EqualTo(new[] { "changeNumber=1202,cn=changelog" }), "the modify needs no changes");
            Assert.That(recordReads.Single().Request.Attributes.Cast<string>(), Does.Contain("changes"));
            Assert.That(recordReads.Single().Timeout, Is.EqualTo(Timeout));
        }
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

    #region ReadChangesAsync: each partition against its own watermark

    [Test]
    public async Task ReadChangesAsync_TwoPartitions_ReadsFromTheOldestWatermarkAsync()
    {
        // One flat changelog serves every partition, so it is read once, from the change after the oldest watermark
        // among the run's partitions; nothing a partition has not read lies before that.
        var host = new Mock<ILdapDeltaImportHost>();

        await Source().ReadChangesAsync(PartitionsContext(host, (Corp(), 1500), (Partners(), 1200)), new ConnectedSystemImportResult(), CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_sent.Single().Request.Filter, Is.EqualTo("(changeNumber>=1201)"));
            host.Verify(h => h.EnterPhaseAsync(LdapConnectorPhases.QueryChanges, "Querying changelog since change number 1,200..."), Times.Once);
        }
    }

    [Test]
    public async Task ReadChangesAsync_EntryAtOrBelowItsPartitionsWatermark_IsNotImportedAsync()
    {
        // corp.local last read up to change 1200, partners.local up to 1500. Reading from 1201 meets partners.local
        // changes its own last import already read; importing them again would re-read, and judging them by corp.local's
        // watermark is what a single watermark did.
        ChangelogHolds(
            ChangeEntry("modify", CorpPerson("cal"), 1201),
            ChangeEntry("modify", PartnersPerson("pat"), 1300),
            DeleteEntry(PartnersPerson("old"), DeletedEntryLdif(Uuid), 1400),
            ChangeEntry("modify", PartnersPerson("max"), 1500),
            ChangeEntry("add", PartnersPerson("new"), 1501));
        var (host, fetched) = FetchingHost();
        var result = new ConnectedSystemImportResult();

        await Source().ReadChangesAsync(PartitionsContext(host, (Corp(), 1200), (Partners(), 1500)), result, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(fetched, Is.EqualTo(new[] { CorpPerson("cal"), PartnersPerson("new") }), "partners.local's changes up to and including 1500 were read by its last import");
            Assert.That(result.ImportObjects, Has.Count.EqualTo(2), "nor is the deletion partners.local already read staged again");
            Assert.That(_sent.Where(s => s.Request.Scope == SearchScope.Base), Is.Empty, "a deletion passed over is not read for its deleted entry");
        }
    }

    [Test]
    public async Task ReadChangesAsync_EntryOutsideEveryTargetPartition_IsSkippedAsync()
    {
        // No Container states anything about scope, so only the partitions confine the read. The changelog is
        // directory-wide: it records writes to partitions this run does not target and to suffixes JIM does not manage.
        ChangelogHolds(
            ChangeEntry("modify", "uid=sam,ou=people,dc=other,dc=local", 1201),
            ChangeEntry("modify", PartnersPerson("pat"), 1202),
            ChangeEntry("modify", "uid=pat,adc=corp,dc=local", 1203),
            DeleteEntry("uid=gone,ou=people,dc=other,dc=local", DeletedEntryLdif(Uuid), 1204),
            ChangeEntry("modify", CorpPerson("cal"), 1205));
        var (host, fetched) = FetchingHost();
        var notes = new LdapDeltaSourceNotes();
        var result = new ConnectedSystemImportResult();

        await Source().ReadChangesAsync(PartitionsContext(host, notes, containersStateScope: false, (Corp(), 1200)), result, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(fetched, Is.EqualTo(new[] { CorpPerson("cal") }),
                "another suffix, a partition the run does not target, and a name ending in the partition's text but not on an RDN boundary are all outside it");
            Assert.That(result.ImportObjects, Has.Count.EqualTo(1));
            Assert.That(_sent.Where(s => s.Request.Scope == SearchScope.Base), Is.Empty, "a deletion outside the run's partitions is not read for its deleted entry");
            Assert.That(notes.Warning, Is.Null);
        }
    }

    [Test]
    public async Task ReadChangesAsync_RenameIntoAPartition_IsJudgedByTheNewDnsPartitionAsync()
    {
        // A rename's targetDN is where the object was. pat moved from partners.local, whose last import read past the
        // move, into corp.local, whose last import did not; cal moved the other way.
        _searchAnswer = _ => LdapTestResponses.SearchResponseWithEntries(
            RenameEntry("modrdn", PartnersPerson("pat"), "uid=pat", "ou=people," + CorpDn, 1300),
            RenameEntry("modrdn", CorpPerson("cal"), "uid=cal", "ou=people," + PartnersDn, 1400));
        var (host, fetched) = FetchingHost();

        await Source().ReadChangesAsync(PartitionsContext(host, (Corp(), 1200), (Partners(), 1500)), new ConnectedSystemImportResult(), CancellationToken.None);

        Assert.That(fetched, Is.EqualTo(new[] { CorpPerson("pat") }),
            "pat now lies in corp.local, which has not read the move; cal now lies in partners.local, which has");
    }

    [Test]
    public async Task ReadChangesAsync_PartitionWithNoDistinguishedName_TakesTheEntriesNoNamedPartitionClaimsAsync()
    {
        // A partition with no distinguished name cannot be told apart by suffix, so it takes what no named partition
        // claims, as the accesslog source does; dropping those entries would have the run read nothing for it, silently.
        // Of two such, the one with the older watermark judges them, so neither has a change passed over it has yet to read.
        ChangelogHolds(
            ChangeEntry("modify", CorpPerson("cal"), 1250),
            ChangeEntry("modify", "uid=sam,ou=people,dc=other,dc=local", 1300),
            ChangeEntry("modify", "uid=old,ou=people,dc=other,dc=local", 1150));
        var (host, fetched) = FetchingHost();
        var unnamed = new ConnectedSystemPartition { Id = 3, ExternalId = "", Name = "unnamed", Selected = true };
        var alsoUnnamed = new ConnectedSystemPartition { Id = 4, ExternalId = "", Name = "also unnamed", Selected = true };

        await Source().ReadChangesAsync(
            PartitionsContext(host, new LdapDeltaSourceNotes(), containersStateScope: false, (Corp(), 1200), (alsoUnnamed, 1400), (unnamed, 1100)),
            new ConnectedSystemImportResult(), CancellationToken.None);

        Assert.That(fetched, Is.EqualTo(new[] { CorpPerson("cal"), "uid=sam,ou=people,dc=other,dc=local", "uid=old,ou=people,dc=other,dc=local" }),
            "cal lies in corp.local; sam and old lie in no named partition, so the unnamed partition with the older watermark, 1100, judges them");
    }

    [Test]
    public async Task ReadChangesAsync_EntryWhoseChangeNumberCannotBeRead_IsStillImportedAsync()
    {
        // The directory answered it for a search from 1201, so it is newer than the oldest watermark, but whether
        // partners.local's last import read it cannot be told. Importing it again costs a re-read; passing it over could
        // lose a change.
        _searchAnswer = _ => LdapTestResponses.SearchResponseWithEntries(LdapTestResponses.Entry("changeNumber=x,cn=changelog",
            ("changeNumber", "not-a-number"), ("changeType", "modify"), ("targetDN", PartnersPerson("pat"))));
        var (host, fetched) = FetchingHost();

        await Source().ReadChangesAsync(PartitionsContext(host, (Corp(), 1200), (Partners(), 1500)), new ConnectedSystemImportResult(), CancellationToken.None);

        Assert.That(fetched, Is.EqualTo(new[] { PartnersPerson("pat") }));
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
        ChangelogHolds(DeleteEntry(InScopeDn, DeletedEntryLdif(Uuid)));
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
        ChangelogHolds(DeleteEntry(InScopeDn, DeletedEntryLdif(Uuid).Replace("\n", "\r\n")));
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
        ChangelogHolds(DeleteEntryWithBinaryChanges(InScopeDn, ldif));
        var result = new ConnectedSystemImportResult();

        await Source().ReadChangesAsync(Context(new Mock<ILdapDeltaImportHost>()), result, CancellationToken.None);

        Assert.That(result.ImportObjects.Single().Attributes.Single(a => a.Name == "entryUUID").StringValues, Is.EqualTo(new[] { Uuid }));
    }

    [Test]
    public async Task ReadChangesAsync_DeleteWithoutChanges_EmitsNothingAndNotesWhyAsync()
    {
        ChangelogHolds(ChangeEntry("delete", InScopeDn));
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
        ChangelogHolds(DeleteEntry(InScopeDn, "changetype: delete\n"));
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
        ChangelogHolds(DeleteEntry(InScopeDn, $"objectClass: top\nobjectClass: organizationalUnit\nentryUUID: {Uuid}\n"));
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
        ChangelogHolds(
            ChangeEntry("add", InScopeDn, 1201),
            DeleteEntry(InScopeDn, DeletedEntryLdif(Uuid), 1202),
            ChangeEntry("modify", OutOfScopeDn, 1203));
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
    public async Task ReadChangesAsync_TwoModifiesOfOneDnOnTwoPages_FetchesTheObjectOnEachPageAsync()
    {
        // Each page is its own call and the source keeps nothing between calls. The import lets a later page's copy of
        // an object take an earlier page's place in a Delta Import, so the second fetch, of the newer state, is what
        // stands.
        _searchAnswer = request => request.Filter switch
        {
            "(changeNumber>=1201)" => throw SizeLimitExceeded(ChangeEntry("modify", InScopeDn, 1201)),
            "(changeNumber>=1202)" => LdapTestResponses.SearchResponseWithEntries(ChangeEntry("modify", InScopeDn, 1202)),
            _ => throw new InvalidOperationException($"Unexpected filter {request.Filter}")
        };
        var host = new Mock<ILdapDeltaImportHost>();
        host.Setup(h => h.GetObjectByDn(InScopeDn, ObjectChangeType.Updated)).Returns(() => new ConnectedSystemImportObject { ChangeType = ObjectChangeType.Updated });
        var firstPage = new ConnectedSystemImportResult();
        var secondPage = new ConnectedSystemImportResult();

        await Source().ReadChangesAsync(Context(host), firstPage, CancellationToken.None);
        await Source().ReadChangesAsync(Context(host, paginationTokens: firstPage.PaginationTokens), secondPage, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(firstPage.ImportObjects, Has.Count.EqualTo(1));
            Assert.That(secondPage.ImportObjects, Has.Count.EqualTo(1));
        }
    }

    [Test]
    public async Task ReadChangesAsync_AddThenDeleteOfOneDn_YieldsTheFetchedObjectAndTheDeletionAsync()
    {
        // A deletion is never skipped for a DN already seen: an object added then deleted in the same window must
        // still be deleted.
        ChangelogHolds(ChangeEntry("add", InScopeDn, 1201), DeleteEntry(InScopeDn, DeletedEntryLdif(Uuid), 1202));
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
    public async Task ReadChangesAsync_PageCut_LeavesAPaginationTokenThatResumesFromTheHighestChangeNumberSeenPlusOneAsync()
    {
        // The read stops at the page size (or the directory's own size limit, if lower) and the import calls again,
        // so a page's changes are processed and released before the next page is read.
        _searchAnswer = request => request.Filter switch
        {
            "(changeNumber>=1201)" => throw SizeLimitExceeded(ChangeEntry("add", PersonDn("a1"), 1201), ChangeEntry("add", PersonDn("a3"), 1203), ChangeEntry("add", PersonDn("a2"), 1202)),
            "(changeNumber>=1204)" => LdapTestResponses.SearchResponseWithEntries(ChangeEntry("add", PersonDn("a4"), 1204)),
            _ => throw new InvalidOperationException($"Unexpected filter {request.Filter}")
        };
        var host = new Mock<ILdapDeltaImportHost>();
        host.Setup(h => h.GetObjectByDn(It.IsAny<string>(), ObjectChangeType.Added)).Returns(() => new ConnectedSystemImportObject { ChangeType = ObjectChangeType.Added });
        var firstPage = new ConnectedSystemImportResult();
        var secondPage = new ConnectedSystemImportResult();

        await Source().ReadChangesAsync(Context(host), firstPage, CancellationToken.None);
        await Source().ReadChangesAsync(Context(host, paginationTokens: firstPage.PaginationTokens), secondPage, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_sent.Select(s => s.Request.Filter), Is.EqualTo(new[] { "(changeNumber>=1201)", "(changeNumber>=1204)" }),
                "the change number is an exact cursor, so the next page resumes from the highest seen plus one");
            Assert.That(firstPage.ImportObjects, Has.Count.EqualTo(3), "the cut page is imported, not discarded");
            Assert.That(firstPage.PaginationTokens, Has.Count.EqualTo(1), "the import calls again while a token is left");
            Assert.That(secondPage.ImportObjects, Has.Count.EqualTo(1));
            Assert.That(secondPage.PaginationTokens, Is.Empty, "a page the directory answered in full is the last");
            host.Verify(h => h.ReportObjectsReadAsync(3), Times.Once);
            host.Verify(h => h.ReportObjectsReadAsync(1), Times.Once);
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

    /// <summary>
    /// The changelog holding these records: the change read answers them without <c>changes</c>, as the directory
    /// does when the read does not ask for it, and a read of one record by its own name answers it whole.
    /// </summary>
    private void ChangelogHolds(params SearchResultEntry[] records)
    {
        _searchAnswer = _ => LdapTestResponses.SearchResponseWithEntries(records.Select(WithoutChanges).ToArray());
        var otherBaseReads = _baseReadAnswer;
        _baseReadAnswer = request => records.FirstOrDefault(r => r.DistinguishedName == request.DistinguishedName) is { } record
            ? LdapTestResponses.SearchResponseWithEntries(record)
            : otherBaseReads(request);
    }

    private static SearchResultEntry WithoutChanges(SearchResultEntry record)
    {
        const BindingFlags nonPublicInstance = BindingFlags.NonPublic | BindingFlags.Instance;
        var attributeCollection = (SearchResultAttributeCollection)Activator.CreateInstance(typeof(SearchResultAttributeCollection), nonPublic: true)!;
        var add = typeof(SearchResultAttributeCollection).GetMethod("Add", nonPublicInstance, [typeof(string), typeof(DirectoryAttribute)])!;
        foreach (var name in record.Attributes.AttributeNames.Cast<string>().Where(n => n != "changes"))
            add.Invoke(attributeCollection, [name, record.Attributes[name]]);

        return (SearchResultEntry)Activator.CreateInstance(typeof(SearchResultEntry), nonPublicInstance, binder: null,
            args: [record.DistinguishedName, attributeCollection], culture: null)!;
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
    /// <summary>A base read answering the watermark's changelog entry with a changeTime, and any other entry as the default does.</summary>
    private static SearchResponse WatermarkEntryAnswer(SearchRequest request, string watermarkEntryDn, string changeTime) =>
        string.Equals(request.DistinguishedName, watermarkEntryDn, StringComparison.OrdinalIgnoreCase)
            ? LdapTestResponses.SearchResponseWithEntries(LdapTestResponses.Entry(watermarkEntryDn, ("changeTime", changeTime)))
            : ChangelogEntryAnswer(request);

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

    private static LdapPartitionWatermark ChangelogWatermark(long changeNumber, string? changeTime = null) =>
        new() { Kind = LdapDeltaSourceKind.Changelog, ChangeNumber = changeNumber, ChangeTime = changeTime };

    /// <summary>The partition a change read targets, and a continuity check is told holds the watermark it checks.</summary>
    private static ConnectedSystemPartition Partition() => new() { Id = 1, ExternalId = "dc=example,dc=com", Name = "example.com", Selected = true };

    private static LdapDeltaReadContext Context(Mock<ILdapDeltaImportHost> host, long previousChangeNumber = PreviousChangeNumber, LdapConnectorRootDse? currentRootDse = null, LdapDeltaSourceNotes? notes = null,
        IReadOnlyList<ConnectedSystemPaginationToken>? paginationTokens = null) => new()
    {
        PreviousRootDse = new LdapConnectorRootDse(),
        Watermarks = new Dictionary<int, LdapPartitionWatermark> { [Partition().Id] = ChangelogWatermark(previousChangeNumber) },
        CurrentRootDse = currentRootDse ?? new LdapConnectorRootDse(),
        TargetPartitions = [Partition()],
        ScopeDecidingContainers = [new ConnectedSystemContainer { ExternalId = ContainerDn, Name = "People", Selected = true }],
        ObjectTypes = [InetOrgPerson()],
        PaginationTokens = paginationTokens ?? [],
        PageSize = 500,
        SearchTimeout = Timeout,
        Notes = notes ?? new LdapDeltaSourceNotes(),
        Host = host.Object
    };

    private static ConnectedSystemPartition Corp() => new() { Id = 1, ExternalId = CorpDn, Name = "corp.local", Selected = true };

    private static ConnectedSystemPartition Partners() => new() { Id = 2, ExternalId = PartnersDn, Name = "partners.local", Selected = true };

    private static string CorpPerson(string uid) => $"uid={uid},ou=people,{CorpDn}";

    private static string PartnersPerson(string uid) => $"uid={uid},ou=people,{PartnersDn}";

    /// <summary>A host that fetches every object asked for, recording the DNs it was asked for in order.</summary>
    private static (Mock<ILdapDeltaImportHost> Host, List<string> Fetched) FetchingHost()
    {
        var fetched = new List<string>();
        var host = new Mock<ILdapDeltaImportHost>();
        host.Setup(h => h.GetObjectByDn(It.IsAny<string>(), It.IsAny<ObjectChangeType>()))
            .Returns((string dn, ObjectChangeType changeType) =>
            {
                fetched.Add(dn);
                return new ConnectedSystemImportObject { ChangeType = changeType };
            });
        return (host, fetched);
    }

    /// <summary>
    /// A change read of the given partitions, each with its own watermark, and a selected people Container in each
    /// unless no Container is to state anything about scope.
    /// </summary>
    private static LdapDeltaReadContext PartitionsContext(Mock<ILdapDeltaImportHost> host, params (ConnectedSystemPartition Partition, long ChangeNumber)[] watermarks) =>
        PartitionsContext(host, new LdapDeltaSourceNotes(), true, watermarks);

    private static LdapDeltaReadContext PartitionsContext(Mock<ILdapDeltaImportHost> host, LdapDeltaSourceNotes notes, bool containersStateScope,
        params (ConnectedSystemPartition Partition, long ChangeNumber)[] watermarks) => new()
    {
        PreviousRootDse = new LdapConnectorRootDse(),
        Watermarks = watermarks.ToDictionary(watermark => watermark.Partition.Id, watermark => ChangelogWatermark(watermark.ChangeNumber)),
        CurrentRootDse = new LdapConnectorRootDse(),
        TargetPartitions = watermarks.Select(watermark => watermark.Partition).ToList(),
        ScopeDecidingContainers = containersStateScope
            ? watermarks.Select(watermark => new ConnectedSystemContainer { ExternalId = "ou=people," + watermark.Partition.ExternalId, Name = "people", Selected = true }).ToList()
            : [],
        ObjectTypes = [InetOrgPerson()],
        PaginationTokens = [],
        PageSize = 500,
        SearchTimeout = Timeout,
        Notes = notes,
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
