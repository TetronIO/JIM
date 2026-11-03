// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Connectors.LDAP;
using JIM.Models.Exceptions;
using JIM.Models.Staging;
using Moq;
using NUnit.Framework;
using Serilog;
using System.DirectoryServices.Protocols;
using System.Text.Json;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// An OpenLDAP import takes its next watermark from cn=accesslog on its first page, and on a directory without the
/// sssvlv overlay that means walking the log. These tests drive the whole import against a scripted directory to show
/// that the walk starts from the watermark the last import left, on a Delta Import and on a Full Import after the
/// first (#2704); that it is confined to the partitions the run imports from, as the change read is (#2712); and that
/// the checks which read that watermark back behave as they always did.
/// </summary>
[TestFixture]
public class LdapConnectorImportAccesslogWatermarkTests
{
    private const string AccesslogDn = "cn=accesslog";
    private const string PartitionDn = "dc=corp,dc=local";
    private const string SecondPartitionDn = "dc=partners,dc=local";
    private const string WithinThePartition = "uid=alice,ou=people," + PartitionDn;
    private const string UnderAnotherSuffix = "uid=bob,ou=people,dc=other,dc=local";
    private const string EarlierWrite = "20260921070000.000001Z";
    private const string Watermark = "20260921080000.000000Z";
    private const string LaterWrite = "20260921090000.000001Z";
    private const string OtherSuffixesLaterWrite = "20260921090500.000002Z";
    private const string WholeLog = "(&(objectClass=auditWriteObject)(reqResult=0))";
    private static readonly ILogger Logger = Serilog.Core.Logger.None;

    /// <summary>Each write the directory's accesslog holds, in the order it logged them: its reqStart and the entry it was made to.</summary>
    private List<(string ReqStart, string ReqDn)> _logged = null!;

    /// <summary>The filters of the watermark walk's searches, in the order the import sent them.</summary>
    private List<string> _walk = null!;

    /// <summary>The filter of the change read, the paged search a Delta Import reads its changes with.</summary>
    private string? _changeRead;

    private Mock<ILdapOperationExecutor> _executor = null!;

    [SetUp]
    public void SetUp()
    {
        _logged = [];
        _walk = [];
        _changeRead = null;

        _executor = new Mock<ILdapOperationExecutor>();
        _executor.Setup(x => x.SendRequest(It.IsAny<DirectoryRequest>()))
            .Returns((DirectoryRequest request) => Answer(request));
        _executor.Setup(x => x.SendRequest(It.IsAny<DirectoryRequest>(), It.IsAny<TimeSpan>()))
            .Returns((DirectoryRequest request, TimeSpan _) => Answer(request));
    }

    [Test]
    public async Task GetDeltaImportObjectsAsync_PreviousWatermark_TakesTheNextWatermarkFromItAsync()
    {
        Log(EarlierWrite, Watermark, LaterWrite);

        var result = await NewImport(ConnectedSystemRunType.DeltaImport, PersistedState(Watermark)).GetDeltaImportObjectsAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_walk, Is.EqualTo(new[] { WalkFrom(Watermark) }), "the last import's watermark reaches the capture, so the log before it is not read");
            Assert.That(Read(result.PersistedConnectorData).LastAccesslogTimestamp, Is.EqualTo(LaterWrite));
        }
    }

    [Test]
    public async Task GetFullImportObjectsAsync_PreviousWatermark_TakesTheNextWatermarkFromItAsync()
    {
        // A Full Import is handed the record the last import left as well, and takes its baseline the same way.
        Log(EarlierWrite, Watermark, LaterWrite);

        var result = await NewImport(ConnectedSystemRunType.FullImport, PersistedState(Watermark)).GetFullImportObjectsAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_walk, Is.EqualTo(new[] { WalkFrom(Watermark) }));
            Assert.That(Read(result.PersistedConnectorData).LastAccesslogTimestamp, Is.EqualTo(LaterWrite));
        }
    }

    [Test]
    public async Task GetFullImportObjectsAsync_FirstImport_WalksTheWholeLogAsync()
    {
        Log(EarlierWrite, Watermark);

        var result = await NewImport(ConnectedSystemRunType.FullImport, persistedState: null).GetFullImportObjectsAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_walk, Is.EqualTo(new[] { WholeLog }));
            Assert.That(Read(result.PersistedConnectorData).LastAccesslogTimestamp, Is.EqualTo(Watermark));
        }
    }

    [Test]
    public async Task GetFullImportObjectsAsync_PreviousRecordUnreadable_WalksTheWholeLogRatherThanFailingAsync()
    {
        // A Full Import needs nothing from the last import's record; an unreadable one costs only the narrower search,
        // and the Full Import replaces it with a record of its own.
        Log(EarlierWrite, Watermark);

        var result = await NewImport(ConnectedSystemRunType.FullImport, persistedState: "{ not a record").GetFullImportObjectsAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_walk, Is.EqualTo(new[] { WholeLog }));
            Assert.That(Read(result.PersistedConnectorData).LastAccesslogTimestamp, Is.EqualTo(Watermark));
        }
    }

    [Test]
    public void GetDeltaImportObjectsAsync_DirectoryRestoredToBeforeThePreviousWatermark_StillRefusesAsync()
    {
        // The restored log holds only writes older than the watermark, so the walk from it finds nothing and the whole
        // log is read, as before; the continuity check then finds the watermark's entry gone and refuses, as before.
        Log(EarlierWrite);

        var failure = Assert.ThrowsAsync<CannotPerformDeltaImportException>(
            () => NewImport(ConnectedSystemRunType.DeltaImport, PersistedState(Watermark)).GetDeltaImportObjectsAsync());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(failure!.Message, Does.Contain(Watermark).And.Contain("restored from a backup or snapshot"));
            Assert.That(_walk, Is.EqualTo(new[] { WalkFrom(Watermark), WholeLog }), "nothing from the watermark, so the whole log was read");
        }
    }

    [Test]
    public async Task GetDeltaImportObjectsAsync_SelectedPartition_TakesTheNextWatermarkWithinItAsync()
    {
        // cn=accesslog is shared by every suffix on the server; the partitions the run imports from reach the capture,
        // so another suffix's writes are left on the server (#2712).
        Log(EarlierWrite, Watermark, LaterWrite);
        LogUnder(UnderAnotherSuffix, OtherSuffixesLaterWrite);

        var result = await NewImport(ConnectedSystemRunType.DeltaImport, PersistedState(Watermark), [Partition(1, PartitionDn)]).GetDeltaImportObjectsAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_walk, Is.EqualTo(new[] { WalkWithinFrom(Watermark, PartitionDn) }));
            Assert.That(Read(result.PersistedConnectorData).LastAccesslogTimestamp, Is.EqualTo(LaterWrite), "the partition's latest write, not the other suffix's");
        }
    }

    [Test]
    public async Task GetFullImportObjectsAsync_SelectedPartition_TakesTheNextWatermarkWithinItAsync()
    {
        Log(EarlierWrite, Watermark, LaterWrite);
        LogUnder(UnderAnotherSuffix, OtherSuffixesLaterWrite);

        var result = await NewImport(ConnectedSystemRunType.FullImport, PersistedState(Watermark), [Partition(1, PartitionDn)]).GetFullImportObjectsAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_walk, Is.EqualTo(new[] { WalkWithinFrom(Watermark, PartitionDn) }));
            Assert.That(Read(result.PersistedConnectorData).LastAccesslogTimestamp, Is.EqualTo(LaterWrite));
        }
    }

    [Test]
    public async Task GetDeltaImportObjectsAsync_RunProfileTargetsOnePartition_ConfinesTheWatermarkToItAsTheChangeReadIsAsync()
    {
        // The capture is confined to the partitions the change read is: the one the Run Profile targets, not every
        // partition selected on the Connected System.
        Log(EarlierWrite, Watermark, LaterWrite);
        LogUnder("uid=carol,ou=people," + SecondPartitionDn, OtherSuffixesLaterWrite);
        var targeted = Partition(1, PartitionDn);

        var result = await NewImport(ConnectedSystemRunType.DeltaImport, PersistedState(Watermark), [targeted, Partition(2, SecondPartitionDn)], targeted)
            .GetDeltaImportObjectsAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_walk, Is.EqualTo(new[] { WalkWithinFrom(Watermark, PartitionDn) }));
            Assert.That(_changeRead, Does.EndWith($"(|(reqDN:dnSubtreeMatch:={PartitionDn})))"), "the change read is confined the same way");
            Assert.That(Read(result.PersistedConnectorData).LastAccesslogTimestamp, Is.EqualTo(LaterWrite));
        }
    }

    [Test]
    public void GetDeltaImportObjectsAsync_SelectedPartitionAndDirectoryRestoredToBeforeThePreviousWatermark_StillRefusesAsync()
    {
        // Nothing from the watermark within the partition or anywhere else, so the whole log is read; the continuity
        // check reads the previous watermark's own entry, as it always did, finds it gone and refuses.
        Log(EarlierWrite);

        var failure = Assert.ThrowsAsync<CannotPerformDeltaImportException>(
            () => NewImport(ConnectedSystemRunType.DeltaImport, PersistedState(Watermark), [Partition(1, PartitionDn)]).GetDeltaImportObjectsAsync());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(failure!.Message, Does.Contain(Watermark).And.Contain("restored from a backup or snapshot"));
            Assert.That(_walk, Is.EqualTo(new[] { WalkWithinFrom(Watermark, PartitionDn), WalkFrom(Watermark), WholeLog }));
        }
    }

    #region Helpers

    /// <summary>Logs writes to an entry within the partition, in the order given.</summary>
    private void Log(params string[] reqStarts) => LogUnder(WithinThePartition, reqStarts);

    /// <summary>Logs writes to the given entry, in the order given.</summary>
    private void LogUnder(string reqDn, params string[] reqStarts) => _logged.AddRange(reqStarts.Select(reqStart => (reqStart, reqDn)));

    /// <summary>
    /// The scripted OpenLDAP server: its rootDSE, cn=accesslog and the entries in it, no sssvlv overlay, and a change
    /// read that finds nothing.
    /// </summary>
    private DirectoryResponse Answer(DirectoryRequest directoryRequest)
    {
        var request = (SearchRequest)directoryRequest;

        if (string.IsNullOrEmpty(request.DistinguishedName))
            return LdapTestResponses.SearchResponseWithEntries(LdapTestResponses.Entry(string.Empty, ("vendorName", "OpenLDAP")));

        if (request.Controls.OfType<SortRequestControl>().Any())
            throw new DirectoryOperationException(LdapTestResponses.Create<SearchResponse>(ResultCode.UnavailableCriticalExtension), "critical extension is unavailable");

        // The readiness check reads cn=accesslog itself, and the continuity check the watermark's own entry.
        if (request.Scope == SearchScope.Base)
        {
            var held = string.Equals(request.DistinguishedName, AccesslogDn, StringComparison.OrdinalIgnoreCase)
                || _logged.Any(write => string.Equals(request.DistinguishedName, $"reqStart={write.ReqStart},{AccesslogDn}", StringComparison.OrdinalIgnoreCase));
            return held
                ? LdapTestResponses.SearchResponseWith(request.DistinguishedName)
                : throw new DirectoryOperationException(LdapTestResponses.Create<SearchResponse>(ResultCode.NoSuchObject), "no such object");
        }

        var filter = request.Filter.ToString()!;

        // The change read is the paged search; this directory has no changes to give it.
        if (request.Controls.OfType<PageResultRequestControl>().Any())
        {
            _changeRead = filter;
            return LdapTestResponses.EmptySearchResponse();
        }

        _walk.Add(filter);

        return LdapTestResponses.SearchResponseWithEntries(_logged
            .Where(write => ScriptedAccesslogFilter.Matches(filter, write.ReqStart, write.ReqDn))
            .Select(write => LdapTestResponses.Entry($"reqStart={write.ReqStart},{AccesslogDn}", ("reqStart", write.ReqStart)))
            .ToArray());
    }

    private static string WalkFrom(string reqStart) => $"(&(objectClass=auditWriteObject)(reqResult=0)(reqStart>={reqStart}))";

    private static string WalkWithinFrom(string reqStart, string partitionDn) =>
        $"(&(objectClass=auditWriteObject)(reqResult=0)(reqStart>={reqStart})(|(reqDN:dnSubtreeMatch:={partitionDn})))";

    private static ConnectedSystemPartition Partition(int id, string dn) =>
        new() { Id = id, ExternalId = dn, Name = dn, Selected = true, Containers = [] };

    private LdapConnectorImport NewImport(ConnectedSystemRunType runType, string? persistedState,
        List<ConnectedSystemPartition>? partitions = null, ConnectedSystemPartition? targetedPartition = null)
    {
        // No Object Type and no Container selected: a Full Import stops once it has recorded the watermark, and a
        // Delta Import reads its changes without fetching any object.
        var connectedSystem = new ConnectedSystem { Name = "OpenLDAP", ObjectTypes = [], Partitions = partitions ?? [] };
        var import = new LdapConnectorImport(connectedSystem,
            new ConnectedSystemRunProfile { Name = runType.ToString(), RunType = runType, PageSize = 500, Partition = targetedPartition },
            new LdapConnection("localhost"), null, 1, [], persistedState, null, "openldap.corp.local", _ => true, Logger,
            CancellationToken.None, new RecordingConnectorProgress());
        import.Executor = _executor.Object;
        return import;
    }

    private static string PersistedState(string lastAccesslogTimestamp) =>
        JsonSerializer.Serialize(new LdapConnectorRootDse
        {
            DirectoryType = LdapDirectoryType.OpenLDAP,
            VendorName = "OpenLDAP",
            LastAccesslogTimestamp = lastAccesslogTimestamp,
            LastAccesslogTimestampIsLogged = true
        });

    private static LdapConnectorRootDse Read(string? json)
    {
        Assert.That(json, Is.Not.Null, "the import returned no persisted connector data");
        return JsonSerializer.Deserialize<LdapConnectorRootDse>(json!)!;
    }

    #endregion
}
