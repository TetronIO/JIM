// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Connectors.LDAP;
using JIM.Models.Activities;
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
/// that the walk starts from the partition's watermark, on a Delta Import and on a Full Import after the first
/// (#2704); that it is confined to the partitions the run imports from, as the change read is (#2712), and given only
/// to them (#2715); that the checks which read that watermark back behave as they always did; and that a Delta Import
/// with no watermark to read changes after reads every object instead, and says that it did (#2720).
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

        var result = await NewImport(ConnectedSystemRunType.DeltaImport, [Partition(1, PartitionDn)], WatermarksOf((1, Watermark))).GetDeltaImportObjectsAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_walk, Is.EqualTo(new[] { WalkWithinFrom(Watermark, PartitionDn) }), "the partition's watermark reaches the capture, so the log before it is not read");
            Assert.That(Reached(result, 1).AccesslogTimestamp, Is.EqualTo(LaterWrite));
        }
    }

    [Test]
    public async Task GetFullImportObjectsAsync_PreviousWatermark_TakesTheNextWatermarkFromItAsync()
    {
        // A Full Import is handed each partition's watermark as well, and takes its baseline the same way.
        Log(EarlierWrite, Watermark, LaterWrite);

        var result = await NewImport(ConnectedSystemRunType.FullImport, [Partition(1, PartitionDn)], WatermarksOf((1, Watermark))).GetFullImportObjectsAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_walk, Is.EqualTo(new[] { WalkWithinFrom(Watermark, PartitionDn) }));
            Assert.That(Reached(result, 1).AccesslogTimestamp, Is.EqualTo(LaterWrite));
        }
    }

    [Test]
    public async Task GetFullImportObjectsAsync_FirstImport_WalksTheWholeLogAsync()
    {
        Log(EarlierWrite, Watermark);

        var result = await NewImport(ConnectedSystemRunType.FullImport, [Partition(1, PartitionDn)], WatermarksOf((1, null))).GetFullImportObjectsAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_walk, Is.EqualTo(new[] { WholeLog }));
            Assert.That(Reached(result, 1).AccesslogTimestamp, Is.EqualTo(Watermark));
        }
    }

    [Test]
    public async Task GetFullImportObjectsAsync_PreviousWatermarkUnreadable_WalksTheWholeLogRatherThanFailingAsync()
    {
        // A Full Import needs nothing from the partition's watermark; an unreadable one costs only the narrower search,
        // and the Full Import replaces it with one of its own.
        Log(EarlierWrite, Watermark);

        var result = await NewImport(ConnectedSystemRunType.FullImport, [Partition(1, PartitionDn)], new Dictionary<int, string?> { [1] = "{ not a watermark" })
            .GetFullImportObjectsAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_walk, Is.EqualTo(new[] { WholeLog }));
            Assert.That(Reached(result, 1).AccesslogTimestamp, Is.EqualTo(Watermark));
        }
    }

    [Test]
    public void GetDeltaImportObjectsAsync_DirectoryRestoredToBeforeThePreviousWatermark_StillRefusesAsync()
    {
        // The restored log holds only writes older than the watermark, so the walk from it finds nothing within the
        // partition or anywhere else and the whole log is read, as before; the continuity check then reads the
        // watermark's own entry, as it always did, finds it gone and refuses.
        Log(EarlierWrite);

        var failure = Assert.ThrowsAsync<CannotPerformDeltaImportException>(
            () => NewImport(ConnectedSystemRunType.DeltaImport, [Partition(1, PartitionDn)], WatermarksOf((1, Watermark))).GetDeltaImportObjectsAsync());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(failure!.Message, Does.Contain(Watermark).And.Contain("restored from a backup or snapshot"));
            Assert.That(_walk, Is.EqualTo(new[] { WalkWithinFrom(Watermark, PartitionDn), WalkFrom(Watermark), WholeLog }), "nothing from the watermark, so the whole log was read");
        }
    }

    [Test]
    public async Task GetDeltaImportObjectsAsync_SelectedPartition_TakesTheNextWatermarkWithinItAsync()
    {
        // cn=accesslog is shared by every suffix on the server; the partitions the run imports from reach the capture,
        // so another suffix's writes are left on the server (#2712).
        Log(EarlierWrite, Watermark, LaterWrite);
        LogUnder(UnderAnotherSuffix, OtherSuffixesLaterWrite);

        var result = await NewImport(ConnectedSystemRunType.DeltaImport, [Partition(1, PartitionDn)], WatermarksOf((1, Watermark))).GetDeltaImportObjectsAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_walk, Is.EqualTo(new[] { WalkWithinFrom(Watermark, PartitionDn) }));
            Assert.That(Reached(result, 1).AccesslogTimestamp, Is.EqualTo(LaterWrite), "the partition's latest write, not the other suffix's");
        }
    }

    [Test]
    public async Task GetFullImportObjectsAsync_SelectedPartition_TakesTheNextWatermarkWithinItAsync()
    {
        Log(EarlierWrite, Watermark, LaterWrite);
        LogUnder(UnderAnotherSuffix, OtherSuffixesLaterWrite);

        var result = await NewImport(ConnectedSystemRunType.FullImport, [Partition(1, PartitionDn)], WatermarksOf((1, Watermark))).GetFullImportObjectsAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_walk, Is.EqualTo(new[] { WalkWithinFrom(Watermark, PartitionDn) }));
            Assert.That(Reached(result, 1).AccesslogTimestamp, Is.EqualTo(LaterWrite));
        }
    }

    [Test]
    public async Task GetDeltaImportObjectsAsync_RunProfileTargetsOnePartition_ConfinesTheWatermarkToItAsTheChangeReadIsAsync()
    {
        // The capture is confined to the partitions the change read is: the one the Run Profile targets, not every
        // partition selected on the Connected System; and only that partition is given the watermark (#2715).
        Log(EarlierWrite, Watermark, LaterWrite);
        LogUnder("uid=carol,ou=people," + SecondPartitionDn, OtherSuffixesLaterWrite);
        var targeted = Partition(1, PartitionDn);

        var result = await NewImport(ConnectedSystemRunType.DeltaImport, [targeted, Partition(2, SecondPartitionDn)], WatermarksOf((1, Watermark)), targeted)
            .GetDeltaImportObjectsAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_walk, Is.EqualTo(new[] { WalkWithinFrom(Watermark, PartitionDn) }));
            Assert.That(_changeRead, Does.EndWith($"(|(&(reqDN:dnSubtreeMatch:={PartitionDn})(reqStart>={Watermark}))))"), "the change read is confined the same way");
            Assert.That(Reached(result, 1).AccesslogTimestamp, Is.EqualTo(LaterWrite));
            Assert.That(result.PartitionWatermarks!.Keys, Is.EquivalentTo(new[] { 1 }), "the partition the run did not read keeps the watermark it has");
        }
    }

    [Test]
    public void GetDeltaImportObjectsAsync_SelectedPartitionAndDirectoryRestoredToBeforeThePreviousWatermark_StillRefusesAsync()
    {
        // Nothing from the watermark within the partition or anywhere else, so the whole log is read; the continuity
        // check reads the previous watermark's own entry, as it always did, finds it gone and refuses.
        Log(EarlierWrite);

        var failure = Assert.ThrowsAsync<CannotPerformDeltaImportException>(
            () => NewImport(ConnectedSystemRunType.DeltaImport, [Partition(1, PartitionDn), Partition(2, SecondPartitionDn)], WatermarksOf((1, Watermark), (2, Watermark)))
                .GetDeltaImportObjectsAsync());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(failure!.Message, Does.Contain(Watermark).And.Contain("restored from a backup or snapshot"));
            Assert.That(_walk, Is.EqualTo(new[] { WalkWithinFrom(Watermark, PartitionDn, SecondPartitionDn), WalkFrom(Watermark), WholeLog }));
        }
    }

    [Test]
    public async Task GetDeltaImportObjectsAsync_PreviousWatermark_ReadsOnlyChangesAsync()
    {
        Log(EarlierWrite, Watermark, LaterWrite);

        var result = await NewImport(ConnectedSystemRunType.DeltaImport, [Partition(1, PartitionDn)], WatermarksOf((1, Watermark))).GetDeltaImportObjectsAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.CompleteRead, Is.False, "a Delta Import that reads changes has not read every object, so absence says nothing");
            Assert.That(result.WarningErrorType, Is.Null);
        }
    }

    [Test]
    public async Task GetDeltaImportObjectsAsync_PreviousRecordHoldsNoWatermark_ReadsEveryObjectInsteadAndSaysSoAsync()
    {
        // JIM holds no accesslog position for the partition, so there is nothing to read changes after: the Delta
        // Import reads everything, and tells JIM so, which is what has JIM detect deletions by absence.
        Log(EarlierWrite, Watermark);

        var result = await NewImport(ConnectedSystemRunType.DeltaImport, [Partition(1, PartitionDn)], WatermarksOf((1, null))).GetDeltaImportObjectsAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.CompleteRead, Is.True);
            Assert.That(result.WarningErrorType, Is.EqualTo(ActivityRunProfileExecutionItemErrorType.DeltaImportFallbackToFullImport));
            Assert.That(result.WarningMessage, Does.Contain("was read instead").And.Contain("marked as deleted"));
            Assert.That(_changeRead, Is.Null, "no changes were read: the run read every object instead");
        }
    }

    [Test]
    public async Task GetFullImportObjectsAsync_ReadsEveryObjectAsync()
    {
        Log(EarlierWrite, Watermark);

        var result = await NewImport(ConnectedSystemRunType.FullImport, [Partition(1, PartitionDn)], WatermarksOf((1, Watermark))).GetFullImportObjectsAsync();

        Assert.That(result.CompleteRead, Is.True);
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

    private static string WalkWithinFrom(string reqStart, params string[] partitionDns) =>
        $"(&(objectClass=auditWriteObject)(reqResult=0)(reqStart>={reqStart})(|{string.Concat(partitionDns.Select(dn => $"(reqDN:dnSubtreeMatch:={dn})"))}))";

    private static ConnectedSystemPartition Partition(int id, string dn) =>
        new() { Id = id, ExternalId = dn, Name = dn, Selected = true, Containers = [] };

    private LdapConnectorImport NewImport(ConnectedSystemRunType runType, List<ConnectedSystemPartition> partitions,
        IReadOnlyDictionary<int, string?> partitionWatermarks, ConnectedSystemPartition? targetedPartition = null)
    {
        // No Object Type and no Container selected: a Full Import stops once it has recorded the watermark, and a
        // Delta Import reads its changes without fetching any object.
        var connectedSystem = new ConnectedSystem { Name = "OpenLDAP", ObjectTypes = [], Partitions = partitions };
        var import = new LdapConnectorImport(connectedSystem,
            new ConnectedSystemRunProfile { Name = runType.ToString(), RunType = runType, PageSize = 500, Partition = targetedPartition },
            new LdapConnection("localhost"), null, 1, [], PersistedState(), partitionWatermarks, null, "openldap.corp.local", _ => true, Logger,
            CancellationToken.None, new RecordingConnectorProgress());
        import.Executor = _executor.Object;
        return import;
    }

    /// <summary>The Connected System's record of the directory: OpenLDAP, which reads its changes from the accesslog.</summary>
    private static string PersistedState() =>
        JsonSerializer.Serialize(new LdapConnectorRootDse { DirectoryType = LdapDirectoryType.OpenLDAP, VendorName = "OpenLDAP" });

    /// <summary>The watermark JIM holds for each partition: the reqStart of a logged write, or none.</summary>
    private static Dictionary<int, string?> WatermarksOf(params (int PartitionId, string? AccesslogTimestamp)[] watermarks) =>
        watermarks.ToDictionary(w => w.PartitionId, w => w.AccesslogTimestamp == null
            ? null
            : new LdapPartitionWatermark { Kind = LdapDeltaSourceKind.Accesslog, AccesslogTimestamp = w.AccesslogTimestamp, AccesslogTimestampIsLogged = true }.Serialise());

    /// <summary>The watermark the run reached for a partition.</summary>
    private static LdapPartitionWatermark Reached(ConnectedSystemImportResult result, int partitionId)
    {
        Assert.That(result.PartitionWatermarks, Is.Not.Null, "the import returned no partition watermarks");
        Assert.That(result.PartitionWatermarks!.ContainsKey(partitionId), Is.True, $"the import returned no watermark for partition {partitionId}");
        var watermark = LdapPartitionWatermark.Parse(result.PartitionWatermarks![partitionId], Logger);
        Assert.That(watermark, Is.Not.Null, $"partition {partitionId}'s watermark is none");
        return watermark!;
    }

    #endregion
}
