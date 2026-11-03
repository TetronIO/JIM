// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Connectors.LDAP;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Exceptions;
using JIM.Models.Staging;
using Moq;
using NUnit.Framework;
using Serilog;
using System.DirectoryServices.Protocols;
using System.Text.Json;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// Each LDAP Partition keeps its own Delta Import watermark, which JIM holds and hands back at the start of each run
/// (#2715). These tests drive the whole import against a scripted directory to show what the import does with them,
/// whichever change source reads the directory: when it reads changes and when it reads every object instead, that
/// every page of a run decides alike, and that a run returns a watermark for the Partitions it read and no other.
/// </summary>
[TestFixture]
public class LdapConnectorImportPartitionWatermarkTests
{
    private const string AccesslogDn = "cn=accesslog";
    private const string CorpDn = "dc=corp,dc=local";
    private const string PartnersDn = "dc=partners,dc=local";
    private const string CorpPeopleDn = "ou=people," + CorpDn;
    private const string PartnersPeopleDn = "ou=people," + PartnersDn;
    private const string NtdsSettingsDn = "CN=NTDS Settings,CN=DC1,CN=Servers,CN=Default-First-Site-Name,CN=Sites,CN=Configuration,DC=corp,DC=local";
    private const string Dc1 = "dc1.corp.local";
    private const string Watermark = "20260921080000.000000Z";
    private static readonly Guid Dc1InvocationId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Dc2InvocationId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly byte[] Cookie = [1, 2, 3, 4];
    private static readonly ILogger Logger = Serilog.Core.Logger.None;

    private LdapDirectoryType _directoryType;
    private List<SearchRequest> _sent = null!;
    private Mock<ILdapOperationExecutor> _executor = null!;

    [SetUp]
    public void SetUp()
    {
        _directoryType = LdapDirectoryType.OpenLDAP;
        _sent = [];

        _executor = new Mock<ILdapOperationExecutor>();
        _executor.Setup(x => x.SendRequest(It.IsAny<DirectoryRequest>()))
            .Returns((DirectoryRequest request) => Answer(request));
        _executor.Setup(x => x.SendRequest(It.IsAny<DirectoryRequest>(), It.IsAny<TimeSpan>()))
            .Returns((DirectoryRequest request, TimeSpan _) => Answer(request));
        _executor.Setup(x => x.SendRequestAsync(It.IsAny<DirectoryRequest>()))
            .ReturnsAsync((DirectoryRequest request) => Answer(request));
    }

    #region Reading every object instead of changes

    [Test]
    public async Task GetDeltaImportObjectsAsync_APartitionWithoutAWatermark_ReadsEveryObjectInTheRunsPartitionsAndNamesItAsync()
    {
        // corp.local has a watermark; partners.local, newly selected, has none. The run cannot read changes in one
        // partition and every object in the other (#2720 decides deletions for the whole run), so it reads every
        // object in both, and says which partition had none.
        var import = NewImport(ConnectedSystemRunType.DeltaImport, RecordOf(LdapDirectoryType.OpenLDAP),
            new Dictionary<int, string?> { [1] = AccesslogWatermark(Watermark), [2] = null });

        var result = await import.GetDeltaImportObjectsAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.CompleteRead, Is.True, "JIM detects deletions by absence across the run's partitions");
            Assert.That(result.WarningErrorType, Is.EqualTo(ActivityRunProfileExecutionItemErrorType.DeltaImportFallbackToFullImport));
            Assert.That(result.WarningMessage, Does.Contain("JIM holds no Delta Import watermark for the partition partners.local"));
            Assert.That(result.WarningMessage, Does.Not.Contain("corp.local"), "only the partition that had none is named");
            Assert.That(FullReadSearchBases(), Is.EquivalentTo(new[] { CorpPeopleDn, PartnersPeopleDn }), "every object in both of the run's partitions is read");
            Assert.That(ChangeReads(), Is.Empty, "no changes are read");
            Assert.That(_sent.Count(r => string.IsNullOrEmpty(r.DistinguishedName)), Is.EqualTo(1),
                "the full read carries on from the first page's read of the directory, rather than reading it and taking the watermarks again");
            Assert.That(result.PartitionWatermarks?.Keys, Is.EquivalentTo(new[] { 1, 2 }), "both partitions are given a watermark to read changes after next time");
            Assert.That(result.PartitionWatermarks!.Values, Has.None.Null);
        }
    }

    [Test]
    public async Task GetDeltaImportObjectsAsync_NoConnectedSystemRecord_ReadsEveryObjectInsteadOfFailingAsync()
    {
        // A Delta Import that is the Connected System's first run has nothing recorded about the directory at all. It
        // used to stop with "Run a full import first"; it now does what a Partition without a watermark does.
        var import = NewImport(ConnectedSystemRunType.DeltaImport, persistedState: null,
            new Dictionary<int, string?> { [1] = null, [2] = null });

        var result = await import.GetDeltaImportObjectsAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.CompleteRead, Is.True);
            Assert.That(result.WarningErrorType, Is.EqualTo(ActivityRunProfileExecutionItemErrorType.DeltaImportFallbackToFullImport));
            Assert.That(result.WarningMessage, Does.Contain("the partitions corp.local and partners.local"));
            Assert.That(FullReadSearchBases(), Is.EquivalentTo(new[] { CorpPeopleDn, PartnersPeopleDn }));
            Assert.That(Read(result.PersistedConnectorData).DirectoryType, Is.EqualTo(LdapDirectoryType.OpenLDAP), "the run records what the directory is");
            Assert.That(result.PartitionWatermarks?.Keys, Is.EquivalentTo(new[] { 1, 2 }));
        }
    }

    [Test]
    public async Task GetDeltaImportObjectsAsync_UnreadableConnectedSystemRecord_ReadsEveryObjectInsteadOfFailingAsync()
    {
        var import = NewImport(ConnectedSystemRunType.DeltaImport, persistedState: "{ not a record",
            new Dictionary<int, string?> { [1] = AccesslogWatermark(Watermark), [2] = AccesslogWatermark(Watermark) });

        var result = await import.GetDeltaImportObjectsAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.CompleteRead, Is.True, "with nothing to say what the directory was, no watermark can be read against it");
            Assert.That(result.WarningErrorType, Is.EqualTo(ActivityRunProfileExecutionItemErrorType.DeltaImportFallbackToFullImport));
            Assert.That(ChangeReads(), Is.Empty);
        }
    }

    [Test]
    public async Task GetDeltaImportObjectsAsync_WatermarkOfAnotherKind_ReadsEveryObjectAsync()
    {
        // The directory is OpenLDAP, read through its accesslog. corp.local's watermark was taken by another change
        // source (the Connected System once reached a directory that keeps a changelog): whatever positions it carries,
        // it is not one this directory's changes can be read after.
        var otherKind = new LdapPartitionWatermark
        {
            Kind = LdapDeltaSourceKind.Changelog,
            ChangeNumber = 1207,
            AccesslogTimestamp = Watermark,
            AccesslogTimestampIsLogged = true
        }.Serialise();
        var import = NewImport(ConnectedSystemRunType.DeltaImport, RecordOf(LdapDirectoryType.OpenLDAP),
            new Dictionary<int, string?> { [1] = otherKind, [2] = AccesslogWatermark(Watermark) });

        var result = await import.GetDeltaImportObjectsAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.CompleteRead, Is.True);
            Assert.That(result.WarningMessage, Does.Contain("the partition corp.local"));
            Assert.That(ChangeReads(), Is.Empty);
        }
    }

    [Test]
    public async Task GetDeltaImportObjectsAsync_FallbackOnALaterPage_StaysAFullReadAsync()
    {
        // Active Directory pages a full read across calls. Page 2 is handed the same record and the same watermarks as
        // page 1 (JIM fixes both for the run), and corp.local still has none, so page 2 carries on the full read from
        // the directory's cookie rather than reading changes: a page that answered differently would fail the run.
        _directoryType = LdapDirectoryType.ActiveDirectory;
        var connectedSystem = ConnectedSystemWith(Partition(1, CorpDn, "corp.local", CorpPeopleDn));
        var objectType = connectedSystem.ObjectTypes!.Single();
        var container = connectedSystem.Partitions!.Single().Containers!.Single();
        var import = NewImport(ConnectedSystemRunType.DeltaImport, RecordOf(LdapDirectoryType.ActiveDirectory),
            new Dictionary<int, string?> { [1] = null }, connectedSystem,
            paginationTokens: [new ConnectedSystemPaginationToken(LdapConnectorUtilities.GetPaginationTokenName(container, objectType), Cookie)]);

        var result = await import.GetDeltaImportObjectsAsync();

        var resumed = _sent.SingleOrDefault(r => r.DistinguishedName == CorpPeopleDn);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.CompleteRead, Is.True, "every page of the fallback says it read every object");
            Assert.That(result.WarningErrorType, Is.EqualTo(ActivityRunProfileExecutionItemErrorType.DeltaImportFallbackToFullImport));
            Assert.That(resumed?.Filter, Is.EqualTo("(objectClass=user)"), "the container's full read resumes, not a uSNChanged search");
            Assert.That(resumed?.Controls.OfType<PageResultRequestControl>().Single().Cookie, Is.EqualTo(Cookie));
            Assert.That(_sent.Any(r => string.IsNullOrEmpty(r.DistinguishedName)), Is.False, "the directory is read on the first page only");
            Assert.That(result.PartitionWatermarks, Is.Null, "the first page returned the watermarks; later pages return none");
        }
    }

    #endregion

    #region A run that targets no Partition

    [Test]
    public async Task GetDeltaImportObjectsAsync_RunTargetsNoPartition_ReadsNothingAndSaysSoWithoutFallingBackAsync()
    {
        // No partition is selected (or the hierarchy was never retrieved). There is nothing to read changes in, and
        // reading "every object" would read nothing while claiming a complete read; the run reads nothing, as a Full
        // Import of the same Run Profile does, and says why.
        var import = NewImport(ConnectedSystemRunType.DeltaImport, RecordOf(LdapDirectoryType.OpenLDAP),
            new Dictionary<int, string?>(), ConnectedSystemWith());

        var result = await import.GetDeltaImportObjectsAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.CompleteRead, Is.False, "nothing was read, so nothing JIM holds can be judged missing");
            Assert.That(result.WarningErrorType, Is.Not.EqualTo(ActivityRunProfileExecutionItemErrorType.DeltaImportFallbackToFullImport));
            Assert.That(result.WarningMessage, Does.Contain("no selected partition"));
            Assert.That(result.ImportObjects, Is.Empty);
            Assert.That(ChangeReads(), Is.Empty);
            Assert.That(FullReadSearchBases(), Is.Empty);
            Assert.That(result.PersistedConnectorData, Is.Not.Null, "the directory is still read and recorded, as on any run");
            Assert.That(result.PartitionWatermarks, Is.Empty, "no partition is given a watermark");
        }
    }

    [Test]
    public async Task GetDeltaImportObjectsAsync_RunTargetsNoPartitionAndNoConnectedSystemRecord_ReadsNothingRatherThanFallingBackAsync()
    {
        var import = NewImport(ConnectedSystemRunType.DeltaImport, persistedState: null, new Dictionary<int, string?>(), ConnectedSystemWith());

        var result = await import.GetDeltaImportObjectsAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.CompleteRead, Is.False);
            Assert.That(result.WarningErrorType, Is.Not.EqualTo(ActivityRunProfileExecutionItemErrorType.DeltaImportFallbackToFullImport));
            Assert.That(result.WarningMessage, Does.Contain("no selected partition"));
        }
    }

    #endregion

    #region What a run returns

    [Test]
    public async Task GetFullImportObjectsAsync_ScopedToOnePartition_ReturnsAWatermarkForThatPartitionOnlyAsync()
    {
        // A Full Import of corp.local reads nothing in partners.local, so it must not move partners.local's starting
        // point: the scoped Full Import half of #2715.
        var connectedSystem = ConnectedSystemWith(Partition(1, CorpDn, "corp.local", CorpPeopleDn), Partition(2, PartnersDn, "partners.local", PartnersPeopleDn));
        var import = NewImport(ConnectedSystemRunType.FullImport, RecordOf(LdapDirectoryType.OpenLDAP),
            new Dictionary<int, string?> { [1] = AccesslogWatermark(Watermark) }, connectedSystem, targetedPartition: connectedSystem.Partitions![0]);

        var result = await import.GetFullImportObjectsAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.PartitionWatermarks?.Keys, Is.EquivalentTo(new[] { 1 }));
            Assert.That(FullReadSearchBases(), Is.EqualTo(new[] { CorpPeopleDn }));
        }
    }

    #endregion

    #region Continuity of each Partition's watermark (Active Directory)

    [Test]
    public void GetDeltaImportObjectsAsync_OnePartitionReadLastThroughAnotherDomainController_RefusesNamingThatPartitionAsync()
    {
        // corp.local was last read through dc1, which this run reached; partners.local was last read through dc2. A
        // single Connected System-level watermark recorded only the last run's domain controller, so partners.local's
        // USN would have been read against dc1 unnoticed.
        _directoryType = LdapDirectoryType.ActiveDirectory;
        var import = NewImport(ConnectedSystemRunType.DeltaImport, RecordOf(LdapDirectoryType.ActiveDirectory),
            new Dictionary<int, string?> { [1] = UsnWatermark(100, Dc1InvocationId), [2] = UsnWatermark(100, Dc2InvocationId) });

        var failure = Assert.ThrowsAsync<CannotPerformDeltaImportException>(() => import.GetDeltaImportObjectsAsync());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(failure!.Message, Does.Contain("invocationId has changed").And.Contain(Dc2InvocationId.ToString()).And.Contain(Dc1InvocationId.ToString()));
            Assert.That(failure.Message, Does.Contain("the partition partners.local").And.Not.Contain("corp.local"));
            Assert.That(ChangeReads(), Is.Empty, "nothing is read while a watermark is untrustworthy");
        }
    }

    [Test]
    public void GetDeltaImportObjectsAsync_PartitionsHoldingOneWatermark_AreRefusedTogetherAsync()
    {
        _directoryType = LdapDirectoryType.ActiveDirectory;
        var import = NewImport(ConnectedSystemRunType.DeltaImport, RecordOf(LdapDirectoryType.ActiveDirectory),
            new Dictionary<int, string?> { [1] = UsnWatermark(100, Dc2InvocationId), [2] = UsnWatermark(100, Dc2InvocationId) });

        var failure = Assert.ThrowsAsync<CannotPerformDeltaImportException>(() => import.GetDeltaImportObjectsAsync());

        Assert.That(failure!.Message, Does.Contain("the partitions corp.local and partners.local"));
    }

    [Test]
    public async Task GetDeltaImportObjectsAsync_EveryPartitionHasAWatermarkFromThisDomainController_ReadsEachFromItsOwnAsync()
    {
        _directoryType = LdapDirectoryType.ActiveDirectory;
        var import = NewImport(ConnectedSystemRunType.DeltaImport, RecordOf(LdapDirectoryType.ActiveDirectory),
            new Dictionary<int, string?> { [1] = UsnWatermark(100, Dc1InvocationId), [2] = UsnWatermark(300, Dc1InvocationId) });

        var result = await import.GetDeltaImportObjectsAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.CompleteRead, Is.False);
            Assert.That(_sent.Single(r => r.DistinguishedName == CorpPeopleDn).Filter, Is.EqualTo("(&(objectClass=user)(uSNChanged>=101))"));
            Assert.That(_sent.Single(r => r.DistinguishedName == PartnersPeopleDn).Filter, Is.EqualTo("(&(objectClass=user)(uSNChanged>=301))"));
            Assert.That(result.PartitionWatermarks?.Keys, Is.EquivalentTo(new[] { 1, 2 }));
        }
    }

    #endregion

    #region Helpers

    /// <summary>
    /// The scripted directory: OpenLDAP with an empty accesslog and no sssvlv overlay, or Active Directory reached at
    /// dc1; either way every search of a container finds nothing.
    /// </summary>
    private DirectoryResponse Answer(DirectoryRequest directoryRequest)
    {
        var request = (SearchRequest)directoryRequest;
        _sent.Add(request);

        if (string.IsNullOrEmpty(request.DistinguishedName))
            return _directoryType == LdapDirectoryType.ActiveDirectory ? ActiveDirectoryRootDse() : OpenLdapRootDse();

        if (string.Equals(request.DistinguishedName, NtdsSettingsDn, StringComparison.OrdinalIgnoreCase))
            return LdapTestResponses.SearchResponseWithBinary(NtdsSettingsDn, ("invocationId", [Dc1InvocationId.ToByteArray()]));

        if (request.Controls.OfType<SortRequestControl>().Any())
            throw new DirectoryOperationException(LdapTestResponses.Create<SearchResponse>(ResultCode.UnavailableCriticalExtension), "critical extension is unavailable");

        // The accesslog readiness check, and the continuity check of a watermark's own entry: both are logged.
        if (request.Scope == SearchScope.Base && request.DistinguishedName.EndsWith(AccesslogDn, StringComparison.OrdinalIgnoreCase))
            return LdapTestResponses.SearchResponseWith(request.DistinguishedName);

        return LdapTestResponses.EmptySearchResponse();
    }

    private static SearchResponse OpenLdapRootDse() =>
        LdapTestResponses.SearchResponseWithEntries(LdapTestResponses.Entry(string.Empty, ("vendorName", "OpenLDAP")));

    private static SearchResponse ActiveDirectoryRootDse() =>
        LdapTestResponses.SearchResponseWithEntries(LdapTestResponses.EntryWithValues(string.Empty,
            ("DNSHostName", [Dc1]),
            ("HighestCommittedUSN", ["500"]),
            ("supportedCapabilities", [LdapConnectorConstants.LDAP_CAP_ACTIVE_DIRECTORY_OID]),
            ("dsServiceName", [NtdsSettingsDn]),
            ("namingContexts", [CorpDn, PartnersDn])));

    /// <summary>The containers a full read searched, in the order it searched them.</summary>
    private List<string> FullReadSearchBases() =>
        _sent.Where(r => r.Filter?.ToString()?.StartsWith("(objectClass=", StringComparison.Ordinal) == true && r.Scope != SearchScope.Base)
            .Select(r => r.DistinguishedName)
            .ToList();

    /// <summary>The searches that read changes: the accesslog's write operations, or Active Directory's uSNChanged.</summary>
    private List<SearchRequest> ChangeReads() =>
        _sent.Where(r => r.Filter?.ToString() is { } filter
                && (filter.Contains("uSNChanged", StringComparison.Ordinal) || (filter.Contains("auditWriteObject", StringComparison.Ordinal) && r.Controls.OfType<PageResultRequestControl>().Any())))
            .ToList();

    private static ConnectedSystemPartition Partition(int id, string dn, string name, string containerDn) => new()
    {
        Id = id,
        ExternalId = dn,
        Name = name,
        Selected = true,
        Containers = [new ConnectedSystemContainer { Id = id * 10, ExternalId = containerDn, Name = "people", Selected = true }]
    };

    /// <summary>A Connected System with one selected Object Type and the given partitions, each with a selected Container.</summary>
    private ConnectedSystem ConnectedSystemWith(params ConnectedSystemPartition[] partitions)
    {
        var objectType = _directoryType == LdapDirectoryType.ActiveDirectory
            ? new ConnectedSystemObjectType
            {
                Id = 1,
                Name = "user",
                Selected = true,
                Attributes = [new ConnectedSystemObjectTypeAttribute { Id = 1, Name = "objectGUID", Type = AttributeDataType.Guid, IsExternalId = true, Selected = true }]
            }
            : new ConnectedSystemObjectType
            {
                Id = 1,
                Name = "inetOrgPerson",
                Selected = true,
                Attributes = [new ConnectedSystemObjectTypeAttribute { Id = 1, Name = "entryUUID", Type = AttributeDataType.Text, IsExternalId = true, Selected = true }]
            };

        return new ConnectedSystem { Name = "Directory", ObjectTypes = [objectType], Partitions = [.. partitions] };
    }

    private LdapConnectorImport NewImport(ConnectedSystemRunType runType, string? persistedState, IReadOnlyDictionary<int, string?> partitionWatermarks,
        ConnectedSystem? connectedSystem = null, ConnectedSystemPartition? targetedPartition = null, List<ConnectedSystemPaginationToken>? paginationTokens = null)
    {
        connectedSystem ??= ConnectedSystemWith(Partition(1, CorpDn, "corp.local", CorpPeopleDn), Partition(2, PartnersDn, "partners.local", PartnersPeopleDn));
        var import = new LdapConnectorImport(connectedSystem,
            new ConnectedSystemRunProfile { Name = runType.ToString(), RunType = runType, PageSize = 500, Partition = targetedPartition },
            new LdapConnection("localhost"), null, 1, paginationTokens ?? [], persistedState, partitionWatermarks, null, Dc1, _ => true, Logger,
            CancellationToken.None, new RecordingConnectorProgress());
        import.Executor = _executor.Object;
        return import;
    }

    /// <summary>The Connected System's record of the directory the last import reached.</summary>
    private static string RecordOf(LdapDirectoryType directoryType) =>
        JsonSerializer.Serialize(new LdapConnectorRootDse { DirectoryType = directoryType, DnsHostName = Dc1, InvocationId = Dc1InvocationId });

    private static string AccesslogWatermark(string reqStart) =>
        new LdapPartitionWatermark { Kind = LdapDeltaSourceKind.Accesslog, AccesslogTimestamp = reqStart, AccesslogTimestampIsLogged = true }.Serialise();

    private static string UsnWatermark(long usn, Guid invocationId) =>
        new LdapPartitionWatermark { Kind = LdapDeltaSourceKind.Usn, Usn = usn, InvocationId = invocationId, DnsHostName = Dc1 }.Serialise();

    private static LdapConnectorRootDse Read(string? json)
    {
        Assert.That(json, Is.Not.Null, "the import returned no persisted connector data");
        return JsonSerializer.Deserialize<LdapConnectorRootDse>(json!)!;
    }

    #endregion
}
