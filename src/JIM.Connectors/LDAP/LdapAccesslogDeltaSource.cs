// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Enums;
using JIM.Models.Exceptions;
using JIM.Models.Staging;
using JIM.Utilities;
using Serilog;
using System.DirectoryServices.Protocols;
namespace JIM.Connectors.LDAP;

/// <summary>
/// The change source for OpenLDAP: the accesslog overlay's cn=accesslog database (slapo-accesslog), where every
/// successful write operation is logged with its reqStart timestamp. The watermark is the latest reqStart seen;
/// a Delta Import reads the write operations at or after it and fetches each affected object's current state.
/// <para>
/// OpenLDAP enforces olcSizeLimit (default 500) as a hard cap for non-rootDN clients, even with paging controls.
/// The bind account used by the connector is typically not the rootDN of the cn=accesslog database, so paging
/// alone cannot bypass the limit. The watermark capture works within it: when the size limit is exceeded, the
/// latest timestamp from the partial results narrows the next query, walking forward through the accesslog from the
/// previous watermark (from the start of the log on a first import) until every entry since has been scanned, first
/// within the run's partitions and across the whole log only when nothing new was written within them. The
/// change read does not: it pages under the directory's own cursor, and a directory that stops it at the limit fails
/// the Delta Import, since resuming from a timestamp could miss an operation that started earlier and was logged
/// later.
/// </para>
/// <para>
/// An accesslog that cannot be read is never taken for one that is empty. The readiness check reads the
/// cn=accesslog entry and reports what it found; the watermark is left empty rather than invented when the search
/// is refused or finds nothing to search; and a Delta Import whose search is refused stops with the reason rather
/// than importing nothing and reporting "no changes".
/// </para>
/// </summary>
internal sealed class LdapAccesslogDeltaSource : ILdapDeltaSource
{
    private const string AccesslogDn = "cn=accesslog";
    private const string WriteOperationsFilter = "(&(objectClass=auditWriteObject)(reqResult=0))";

    private readonly ILdapOperationExecutor _executor;
    private readonly ILogger _logger;

    internal LdapAccesslogDeltaSource(ILdapOperationExecutor executor, ILogger logger)
    {
        _executor = executor;
        _logger = logger;
    }

    #region Watermark

    /// <inheritdoc />
    /// <remarks>
    /// Until each Partition's watermark is taken on its own (#2715, phase 4), one is taken for the run, exactly as the
    /// single Connected System-level watermark was, from the oldest of the target Partitions' watermarks (from the
    /// start of the log when any has none), and every target Partition is given it. That is safe: the change read
    /// starts at the oldest target watermark too, so every write to any target Partition up to the one taken is read
    /// by this run or was read by an earlier run of that Partition; a Partition whose own watermark was later re-reads
    /// what it had read already, and misses nothing. See <see cref="CaptureWatermarkAsync"/> for how the one is taken.
    /// </remarks>
    public async Task<IReadOnlyDictionary<int, LdapPartitionWatermark?>> CaptureWatermarksAsync(SearchResultEntry rootDseEntry, LdapConnectorRootDse rootDse,
        IReadOnlyDictionary<int, LdapPartitionWatermark?> previous, IReadOnlyList<ConnectedSystemPartition> targetPartitions, TimeSpan searchTimeout)
    {
        if (targetPartitions.Count == 0)
            return new Dictionary<int, LdapPartitionWatermark?>();

        var targetWatermarks = targetPartitions.Select(partition => previous.GetValueOrDefault(partition.Id)).ToList();
        var oldest = targetWatermarks.All(HasBaseline)
            ? targetWatermarks.MinBy(watermark => watermark!.AccesslogTimestamp, StringComparer.Ordinal)
            : null;

        var watermark = await CaptureWatermarkAsync(oldest, targetPartitions.Select(partition => partition.ExternalId).ToList(), searchTimeout);
        return targetPartitions.ToDictionary(partition => partition.Id, _ => watermark);
    }

    /// <summary>
    /// Takes one accesslog watermark: the reqStart the next Delta Import of the given partitions reads writes from.
    /// The rootDSE carries nothing this source needs, so only cn=accesslog is read.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The watermark is null when cn=accesslog cannot be read (missing, refused, or unreachable), so that the next
    /// Delta Import finds no baseline and reads every object rather than reading nothing from a log it cannot see and
    /// reporting "no changes". Only a readable, empty accesslog gets a generated timestamp: after a snapshot restore
    /// clears it there is genuinely nothing before now, and a baseline saves the next Delta Import an unnecessary
    /// full read.
    /// </para>
    /// <para>
    /// The previous watermark, when there is one, is where a directory that cannot sort starts looking, so only what
    /// was written since is read rather than the whole log (#2704); and that log is shared by every suffix on the
    /// server, so the search is confined to the run's partitions as well, exactly as the change read is (#2712). The
    /// latest write within them is a correct place for the next change read to start, since it reads nothing outside
    /// them. It is never later than the latest write in the log, so it can cost the next Delta Import a re-read, never
    /// a change. When nothing new was written within them, the watermark is the latest write anywhere from the
    /// previous one on, as it always was, so it keeps advancing with the log rather than standing still until the
    /// purge removes it. When nothing at all is logged from there on, the whole log is read.
    /// </para>
    /// <para>
    /// Whichever way it was found, the watermark is a logged write's reqStart, at or after the previous watermark
    /// whenever anything is logged there; <see cref="VerifyContinuity"/> reads only a previous watermark's own entry,
    /// so what it makes of a purge or a restore is unchanged.
    /// </para>
    /// </remarks>
    /// <param name="previous">The watermark to search on from: the oldest of the target Partitions'. Null on a first
    /// import, or when any target Partition has none, and the whole log is then walked.</param>
    /// <param name="namingContexts">The partitions the search is confined to; empty when none is known, and the search
    /// is then not confined.</param>
    internal Task<LdapPartitionWatermark?> CaptureWatermarkAsync(LdapPartitionWatermark? previous, IReadOnlyCollection<string> namingContexts, TimeSpan searchTimeout)
    {
        string? timestamp;
        try
        {
            timestamp = QueryAccesslogForLatestTimestamp(previous?.AccesslogTimestamp, PartitionSuffixes(namingContexts), searchTimeout);
        }
        catch (DirectoryOperationException ex) when (ex.Response?.ResultCode == ResultCode.NoSuchObject)
        {
            return Task.FromResult(LeaveWatermarkEmpty("it was not found"));
        }
        catch (DirectoryOperationException ex)
        {
            return Task.FromResult(LeaveWatermarkEmpty($"the directory refused the search: {DirectorysWords(ex)}"));
        }
        catch (LdapException ex) when (ex.ErrorCode == 32) // noSuchObject, the legacy shape
        {
            return Task.FromResult(LeaveWatermarkEmpty("it was not found"));
        }
        catch (LdapException ex)
        {
            return Task.FromResult(LeaveWatermarkEmpty($"the directory could not be reached: {DirectorysWords(ex)}"));
        }

        if (string.IsNullOrEmpty(timestamp))
        {
            var generated = LdapConnectorUtilities.GenerateAccesslogFallbackTimestamp();
            _logger.Information("LdapAccesslogDeltaSource: The accesslog at {AccesslogDn} is readable but empty; using the generated timestamp {Timestamp} as the watermark so the next Delta Import has a baseline",
                AccesslogDn, generated);
            return Task.FromResult<LdapPartitionWatermark?>(new LdapPartitionWatermark { Kind = LdapDeltaSourceKind.Accesslog, AccesslogTimestamp = generated, AccesslogTimestampIsLogged = false });
        }

        return Task.FromResult<LdapPartitionWatermark?>(new LdapPartitionWatermark { Kind = LdapDeltaSourceKind.Accesslog, AccesslogTimestamp = timestamp, AccesslogTimestampIsLogged = true });
    }

    /// <summary>
    /// Logs that no watermark could be taken, and why, and returns none. A generated timestamp would give the next
    /// Delta Import a baseline it has no right to; none makes it read every object and say so.
    /// </summary>
    private LdapPartitionWatermark? LeaveWatermarkEmpty(string reason)
    {
        _logger.Warning("LdapAccesslogDeltaSource: No watermark was taken from {AccesslogDn}: {Reason}. The next Delta Import will have no baseline and will read every object in its partitions.",
            AccesslogDn, reason);
        return null;
    }

    /// <summary>
    /// Queries OpenLDAP's cn=accesslog for the most recent reqStart timestamp, to be used as the
    /// delta import watermark. Uses a multi-strategy approach to handle server-side size limits:
    /// 1. Server-side sort (reverse by reqStart) with SizeLimit=1 to get only the latest entry.
    ///    This requires the sssvlv overlay to be enabled on the server. It is not confined to the partitions: within
    ///    a quiet partition it would return that partition's last write however old, the watermark the purge would
    ///    then remove.
    /// 2. If sorting is not supported, walks forward through the log within the size limit (see
    ///    <see cref="QueryAccesslogWithSizeLimitHandling"/>), starting from the previous watermark when there is one,
    ///    and first within the run's partitions (#2712). A write within them newer than the previous watermark is
    ///    the watermark. Otherwise every write from the previous watermark on is walked (#2704): the latest write is
    ///    there whenever anything is logged there, so this finds what a walk of the whole log would while reading
    ///    only what was written since. When nothing is logged there (the watermark's entry was purged with nothing
    ///    newer, or the directory was restored to before it), the whole log is walked, exactly as on a first import.
    /// Null when the accesslog is readable and holds no write operations. A missing, refused or unreachable
    /// accesslog is left to the caller as the exception the directory raised.
    /// </summary>
    private string? QueryAccesslogForLatestTimestamp(string? previousTimestamp, IReadOnlyList<string> partitionSuffixes, TimeSpan searchTimeout)
    {
        // Strategy 1: Server-side sort (reverse) with SizeLimit=1
        // This gets only the single latest entry, avoiding size limit issues entirely.
        var result = QueryAccesslogWithServerSideSort(searchTimeout);
        if (result != null)
            return result;

        // Strategy 2: a walk within the size limit, from the previous watermark when there is one.
        if (string.IsNullOrEmpty(previousTimestamp))
        {
            _logger.Debug("QueryAccesslogForLatestTimestamp: No previous watermark to start from; walking the whole accesslog");
            return QueryAccesslogWithSizeLimitHandling(fromTimestamp: null, [], searchTimeout);
        }

        if (partitionSuffixes.Count > 0)
        {
            // The walk from the previous watermark finds that watermark's own entry again when it was a write within
            // these partitions, so only a later write is evidence that anything new was written to them.
            result = QueryAccesslogWithSizeLimitHandling(previousTimestamp, partitionSuffixes, searchTimeout);
            if (result != null && string.CompareOrdinal(result, previousTimestamp) > 0)
            {
                _logger.Debug("QueryAccesslogForLatestTimestamp: Watermark {Timestamp} taken from the writes within the partitions {Partitions} after the previous watermark {PreviousTimestamp}",
                    LogSanitiser.Sanitise(result), LogSanitiser.Sanitise(string.Join("; ", partitionSuffixes)), LogSanitiser.Sanitise(previousTimestamp));
                return result;
            }

            _logger.Debug("QueryAccesslogForLatestTimestamp: Nothing new was written within the partitions {Partitions} after the previous watermark {PreviousTimestamp}; " +
                "walking every write from the previous watermark on, so the watermark still advances with the log",
                LogSanitiser.Sanitise(string.Join("; ", partitionSuffixes)), LogSanitiser.Sanitise(previousTimestamp));
        }

        result = QueryAccesslogWithSizeLimitHandling(previousTimestamp, [], searchTimeout);
        if (result != null)
        {
            _logger.Debug("QueryAccesslogForLatestTimestamp: Watermark {Timestamp} taken from every write at or after the previous watermark {PreviousTimestamp}",
                LogSanitiser.Sanitise(result), LogSanitiser.Sanitise(previousTimestamp));
            return result;
        }

        _logger.Debug("QueryAccesslogForLatestTimestamp: Nothing is logged at or after the previous watermark {PreviousTimestamp} (its entry was purged with nothing newer, " +
            "or the directory was restored to before it); walking the whole accesslog", LogSanitiser.Sanitise(previousTimestamp));
        return QueryAccesslogWithSizeLimitHandling(fromTimestamp: null, [], searchTimeout);
    }

    /// <summary>
    /// Attempts to query the accesslog using server-side sorting (reverse by reqStart) with
    /// SizeLimit=1 to retrieve only the latest entry. This requires the sssvlv overlay.
    /// Returns null if server-side sorting is not supported.
    /// </summary>
    private string? QueryAccesslogWithServerSideSort(TimeSpan searchTimeout)
    {
        try
        {
            var request = new SearchRequest(AccesslogDn,
                WriteOperationsFilter,
                SearchScope.OneLevel,
                "reqStart");

            // Request reverse sort by reqStart so the latest entry comes first
            var sortControl = new SortRequestControl(new SortKey("reqStart", "caseIgnoreOrderingMatch", true));
            sortControl.IsCritical = true;
            request.Controls.Add(sortControl);
            request.SizeLimit = 1;

            var response = (SearchResponse)_executor.SendRequest(request, searchTimeout);
            if (response?.Entries.Count > 0)
            {
                var timestamp = LdapConnectorUtilities.GetEntryAttributeStringValue(response.Entries[0], "reqStart");
                if (timestamp != null)
                {
                    _logger.Debug("QueryAccesslogWithServerSideSort: Latest accesslog timestamp: {Timestamp} (via server-side sort)", timestamp);
                    return timestamp;
                }
            }

            _logger.Debug("QueryAccesslogWithServerSideSort: No accesslog entries found via server-side sort");
            return null;
        }
        catch (DirectoryOperationException ex) when (ex.Response is SearchResponse { ResultCode: ResultCode.UnavailableCriticalExtension or ResultCode.UnwillingToPerform })
        {
            _logger.Debug("QueryAccesslogWithServerSideSort: Server-side sorting not supported (sssvlv overlay not enabled). Falling back to size-limit-aware query.");
            return null;
        }
        catch (DirectoryOperationException ex) when (ex.Response is SearchResponse { ResultCode: ResultCode.InappropriateMatching })
        {
            // Matching rule not supported for this attribute; fall back
            _logger.Debug("QueryAccesslogWithServerSideSort: Sort matching rule not supported for reqStart. Falling back to size-limit-aware query.");
            return null;
        }
    }

    /// <summary>
    /// Queries the accesslog using an iterative approach that works within the server's size limit, from
    /// <paramref name="fromTimestamp"/> on, or from the start of the log when it is null, and confined to the writes
    /// within <paramref name="partitionSuffixes"/> when it names any.
    /// When the size limit is exceeded, extracts the latest timestamp from partial results and
    /// re-queries with a narrower filter (reqStart >= latest_seen) to walk forward through the
    /// accesslog until all entries have been scanned. This effectively implements manual paging
    /// without requiring paging controls to bypass the size limit.
    /// </summary>
    private string? QueryAccesslogWithSizeLimitHandling(string? fromTimestamp, IReadOnlyList<string> partitionSuffixes, TimeSpan searchTimeout)
    {
        string? latestTimestamp = null;
        var totalEntries = 0;
        var iterations = 0;
        var cutShort = false;
        const int maxIterations = 100; // Safety limit to prevent infinite loops

        // Start from the given timestamp, or from the start of the log when there is none
        var currentFilter = WriteOperations(fromTimestamp, partitionSuffixes);

        while (iterations < maxIterations)
        {
            iterations++;
            string? batchLatest;
            int batchCount;
            var hitSizeLimit = false;

            try
            {
                var request = new SearchRequest(AccesslogDn, currentFilter,
                    SearchScope.OneLevel, "reqStart");

                var response = (SearchResponse)_executor.SendRequest(request, searchTimeout);
                (batchLatest, batchCount) = ExtractLatestTimestamp(response);
            }
            catch (DirectoryOperationException ex) when (ex.Response is SearchResponse partialResponse
                && partialResponse.ResultCode == ResultCode.SizeLimitExceeded)
            {
                // The server hit its size limit but returned partial results.
                (batchLatest, batchCount) = ExtractLatestTimestamp(partialResponse);
                hitSizeLimit = true;
            }

            totalEntries += batchCount;
            cutShort = hitSizeLimit;

            if (batchCount == 0 || batchLatest == null)
                break;

            // Update the overall latest timestamp
            if (latestTimestamp == null || string.Compare(batchLatest, latestTimestamp, StringComparison.Ordinal) > 0)
                latestTimestamp = batchLatest;

            if (!hitSizeLimit)
                break; // Got all results without hitting the limit: done

            // Size limit was hit. The partial results contain the earliest entries (OpenLDAP
            // returns in insertion order). Re-query starting after the latest timestamp we've
            // seen to walk forward through the remaining entries.
            _logger.Debug("QueryAccesslogWithSizeLimitHandling: Size limit exceeded on iteration {Iteration}. " +
                "Latest timestamp so far: {Timestamp}. Re-querying from that point.",
                iterations, latestTimestamp);

            currentFilter = WriteOperations(latestTimestamp, partitionSuffixes);
        }

        var startingPoint = fromTimestamp == null ? "the start of the log" : LogSanitiser.Sanitise(fromTimestamp);
        if (totalEntries == 0)
        {
            _logger.Debug("QueryAccesslogWithSizeLimitHandling: No accesslog entries found from {StartingPoint}", startingPoint);
            return null;
        }

        // The last search was cut short too, so the walk ended before the end of the log. The watermark is still a
        // logged write, so nothing is missed: the next Delta Import reads from an earlier point than it needs to.
        if (cutShort)
        {
            _logger.Warning("QueryAccesslogWithSizeLimitHandling: Stopped walking {AccesslogDn} after {Iterations} searches with the directory's size limit still cutting them short, " +
                "so the watermark {Timestamp} may not be the latest write it logged. The next Delta Import starts from there, so it reads some changes twice rather than missing any; " +
                "exempting the account JIM connects as from the size limit on {AccesslogDn} (olcLimits) avoids this.",
                AccesslogDn, iterations, LogSanitiser.Sanitise(latestTimestamp), AccesslogDn);
        }

        _logger.Debug("QueryAccesslogWithSizeLimitHandling: Latest accesslog timestamp: {Timestamp} " +
            "(scanned {Count} entries in {Iterations} iterations from {StartingPoint})",
            latestTimestamp, totalEntries, iterations, startingPoint);
        return latestTimestamp;
    }

    /// <summary>
    /// The accesslog search both the watermark walk and the change read make: the successful write operations at or
    /// after a reqStart (all of them when there is none to start from), to entries within the given partitions (any
    /// entry when none is given). dnSubtreeMatch keeps the other databases' writes on the server.
    /// </summary>
    private static string WriteOperations(string? fromTimestamp, IReadOnlyList<string> partitionSuffixes)
    {
        if (fromTimestamp == null && partitionSuffixes.Count == 0)
            return WriteOperationsFilter;

        var from = fromTimestamp == null
            ? string.Empty
            : $"(reqStart>={LdapConnectorUtilities.EscapeLdapFilterValue(fromTimestamp)})";
        var withinPartitions = partitionSuffixes.Count == 0
            ? string.Empty
            : $"(|{string.Concat(partitionSuffixes.Select(s => $"(reqDN:dnSubtreeMatch:={LdapConnectorUtilities.EscapeLdapFilterValue(s)})"))})";
        return $"(&(objectClass=auditWriteObject)(reqResult=0){from}{withinPartitions})";
    }

    /// <summary>
    /// The partitions an accesslog search is confined to, by distinguished name: those named, less any without one.
    /// </summary>
    private static List<string> PartitionSuffixes(IEnumerable<string> namingContexts) =>
        namingContexts.Where(dn => !string.IsNullOrEmpty(dn)).ToList();

    /// <summary>
    /// Extracts the latest reqStart timestamp from a search response containing accesslog entries.
    /// </summary>
    private static (string? latestTimestamp, int entryCount) ExtractLatestTimestamp(SearchResponse response)
    {
        string? latestTimestamp = null;
        var count = 0;

        foreach (SearchResultEntry entry in response.Entries)
        {
            var reqStart = LdapConnectorUtilities.GetEntryAttributeStringValue(entry, "reqStart");
            if (reqStart != null && (latestTimestamp == null || string.Compare(reqStart, latestTimestamp, StringComparison.Ordinal) > 0))
                latestTimestamp = reqStart;
            count++;
        }

        return (latestTimestamp, count);
    }

    #endregion

    #region Continuity, readiness and baseline

    /// <inheritdoc />
    /// <remarks>
    /// The watermark is the reqStart of the last write the last import read, which is also the RDN of that write's
    /// accesslog entry, so whether the accesslog still reaches back to it is one base read away (#2008). When the
    /// entry is gone, the accesslog was purged past it (olcAccessLogPurge removes entries older than its maximum age,
    /// so this happens when Delta Imports stop for longer than that) or the directory was restored to before it;
    /// either way the writes between it and the oldest entry still held may be gone too, and a timestamp watermark
    /// cannot otherwise tell. When nothing was written in that time nothing was lost, but no read can tell that
    /// apart, so the run is refused all the same. A generated watermark names no entry and is not checked; nor is a
    /// record from before JIM kept which kind it is. Where cn=accesslog itself is missing or refused, the readiness
    /// check that runs next names that, so this one says nothing.
    /// </remarks>
    public void VerifyContinuity(IReadOnlyList<ConnectedSystemPartition> partitions, LdapPartitionWatermark previous, LdapConnectorRootDse current)
    {
        if (previous.AccesslogTimestampIsLogged != true || string.IsNullOrEmpty(previous.AccesslogTimestamp))
            return;

        var watermark = previous.AccesslogTimestamp;
        if (IsStillLogged(watermark) != false || ProbeAccesslog().Outcome != LdapDeltaSourceOutcome.Available)
            return;

        _logger.Warning("LdapAccesslogDeltaSource: Refusing the Delta Import; the accesslog no longer holds the entry the last import ended at ({Watermark})",
            LogSanitiser.Sanitise(watermark));

        throw new CannotPerformDeltaImportException(
            $"The directory's accesslog no longer holds the change the last import ended at (reqStart {watermark}), so the changes logged after it " +
            "may be gone too. The usual cause is that the accesslog's purge (olcAccessLogPurge) removed entries older than its maximum age because " +
            "Delta Imports did not run for longer than that; it also happens when the directory is restored from a backup or snapshot. If nothing " +
            "was written in that time nothing was lost, but JIM cannot tell, and a Delta Import would silently miss any changes that were. Run a " +
            "Full Import, which also detects deletions by absence, to re-establish the baseline, and keep the purge age longer than twice the " +
            "longest gap between Delta Imports.");
    }

    /// <summary>
    /// Whether the accesslog entry named by a watermark is still there: true when the directory answers it, false
    /// when it answers that there is no such entry (or answers with none), and null when it refuses the read, which
    /// says nothing about the entry. A fault on the way to the directory propagates: it is an error, not evidence.
    /// </summary>
    private bool? IsStillLogged(string watermark)
    {
        var request = new SearchRequest($"reqStart={watermark},{AccesslogDn}", "(objectClass=*)", SearchScope.Base, "1.1");

        try
        {
            return ((SearchResponse)_executor.SendRequest(request)).Entries.Count > 0;
        }
        catch (DirectoryOperationException ex) when (ex.Response?.ResultCode == ResultCode.NoSuchObject)
        {
            return false;
        }
        catch (DirectoryOperationException)
        {
            return null;
        }
        catch (LdapException ex) when (ex.ErrorCode == 32) // noSuchObject, the legacy shape
        {
            return false;
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Reads the cn=accesslog entry itself, base scope and no attributes, which is the least a directory will answer
    /// about it. OpenLDAP shares one accesslog across every database it serves, so there is a single subject here
    /// whatever the partitions. An entry answered is an availability. No entry, noSuchObject (in either shape the
    /// client library gives it) and a refusal are all unavailabilities, because each was read in full: a directory
    /// that hides what the account may not see answers success with nothing, and some answer noSuchObject for a
    /// base the account may not read, so a missing entry is never claimed as absence outright. Only a fault on the
    /// way to the directory is an unknown.
    /// </remarks>
    public Task<IReadOnlyList<LdapDeltaSourceFinding>> VerifyReadinessAsync(LdapConnectorRootDse rootDse, IReadOnlyCollection<string> namingContexts, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<LdapDeltaSourceFinding>>([ProbeAccesslog()]);
    }

    /// <summary>What reading the cn=accesslog entry itself says about the accesslog; see <see cref="VerifyReadinessAsync"/>.</summary>
    private LdapDeltaSourceFinding ProbeAccesslog()
    {
        var request = new SearchRequest(AccesslogDn, "(objectClass=*)", SearchScope.Base, "1.1");

        try
        {
            var response = (SearchResponse)_executor.SendRequest(request);
            return response.Entries.Count > 0
                ? Available()
                : NotFound("the directory answered the read with no entry");
        }
        catch (DirectoryOperationException ex) when (ex.Response?.ResultCode == ResultCode.NoSuchObject)
        {
            return NotFound("the directory answered noSuchObject");
        }
        catch (DirectoryOperationException ex)
        {
            return Refused(DirectorysWords(ex));
        }
        catch (LdapException ex) when (ex.ErrorCode == 32) // noSuchObject, the legacy shape
        {
            return NotFound("the directory answered noSuchObject");
        }
        catch (LdapException ex)
        {
            return Undetermined($"the directory could not be reached ({DirectorysWords(ex)})");
        }
    }

    /// <inheritdoc />
    public bool HasBaseline(LdapPartitionWatermark? watermark) =>
        watermark is { Kind: LdapDeltaSourceKind.Accesslog } && !string.IsNullOrEmpty(watermark.AccesslogTimestamp);

    #endregion

    #region Findings and their texts

    private LdapDeltaSourceFinding Available()
    {
        _logger.Debug("LdapAccesslogDeltaSource: The account JIM connects as can read {AccesslogDn}.", AccesslogDn);
        return new LdapDeltaSourceFinding
        {
            Subject = AccesslogDn,
            Outcome = LdapDeltaSourceOutcome.Available,
            Detail = "the accesslog entry is readable"
        };
    }

    private LdapDeltaSourceFinding NotFound(string detail)
    {
        _logger.Warning("LdapAccesslogDeltaSource: No accesslog was found at {AccesslogDn}, or none the account JIM connects as may read: {Detail}. A Delta Import from this directory would detect no changes.",
            AccesslogDn, detail);
        return new LdapDeltaSourceFinding
        {
            Subject = AccesslogDn,
            Outcome = LdapDeltaSourceOutcome.Unavailable,
            Detail = detail,
            DeltaImportText = DescribeNotFoundForDeltaImport(),
            SchemaDiscoveryText = DescribeNotFoundForSchemaDiscovery()
        };
    }

    private LdapDeltaSourceFinding Refused(string reason)
    {
        _logger.Warning("LdapAccesslogDeltaSource: The directory refused to read {AccesslogDn}: {Reason}. A Delta Import from this directory would detect no changes.",
            AccesslogDn, reason);
        return new LdapDeltaSourceFinding
        {
            Subject = AccesslogDn,
            Outcome = LdapDeltaSourceOutcome.Unavailable,
            Detail = $"the directory refused the read: {reason}",
            DeltaImportText = DescribeRefusedForDeltaImport(reason),
            SchemaDiscoveryText = DescribeRefusedForSchemaDiscovery(reason)
        };
    }

    private LdapDeltaSourceFinding Undetermined(string detail)
    {
        _logger.Warning("LdapAccesslogDeltaSource: Could not establish whether the account JIM connects as can read {AccesslogDn}: {Detail}", AccesslogDn, detail);
        var text = DescribeUndetermined(detail);
        return new LdapDeltaSourceFinding
        {
            Subject = AccesslogDn,
            Outcome = LdapDeltaSourceOutcome.CouldNotDetermine,
            Detail = detail,
            DeltaImportText = text,
            SchemaDiscoveryText = text
        };
    }

    /// <summary>The directory's own words for a refusal or a fault, made safe to log and to quote in a finding.</summary>
    private static string DirectorysWords(Exception ex) => LogSanitiser.Sanitise(ex.Message) ?? string.Empty;

    /// <summary>What every Delta Import text ends with: the two ways out, and where the access-control rule is.</summary>
    private const string DeltaImportRemedy =
        "Run a Full Import, which also detects deletions by absence, or enable the accesslog overlay and grant the account read access to " + AccesslogDn + "; " +
        "the LDAP Connector documentation, under Service Account Permissions, gives the access-control rule.";

    /// <summary>The failure that stops a Delta Import when there is no accesslog to read, or none the account may see.</summary>
    private static string DescribeNotFoundForDeltaImport() =>
        $"Changes cannot be detected: the directory provides no accesslog at {AccesslogDn}, or none the account JIM connects as may read, " +
        "so additions, updates and deletions since the last import would go unnoticed. " + DeltaImportRemedy;

    /// <summary>The failure that stops a Delta Import when the directory refused the accesslog search, in its own words.</summary>
    private static string DescribeRefusedForDeltaImport(string reason) =>
        $"Changes cannot be detected: the directory refused to read {AccesslogDn} ({reason}), " +
        "so additions, updates and deletions since the last import would go unnoticed. " + DeltaImportRemedy;

    /// <summary>What Schema Discovery warns when there is no accesslog, so the administrator learns of it while setting the Connected System up.</summary>
    private static string DescribeNotFoundForSchemaDiscovery() =>
        $"This directory publishes no accesslog at {AccesslogDn} that the account JIM connects as may read, so Delta Import is not available; " +
        "Full Import works as normal and also detects deletions by absence. " +
        $"To use Delta Import, enable the accesslog overlay and grant the account read access to {AccesslogDn}; see the LDAP Connector documentation, Service Account Permissions.";

    /// <summary>What Schema Discovery warns when the directory refused to read the accesslog.</summary>
    private static string DescribeRefusedForSchemaDiscovery(string reason) =>
        $"The directory refused to read {AccesslogDn} ({reason}), so Delta Import is not available; " +
        "Full Import works as normal and also detects deletions by absence. " +
        $"To use Delta Import, grant the account JIM connects as read access to {AccesslogDn}; see the LDAP Connector documentation, Service Account Permissions.";

    /// <summary>The one text for an unknown, in both places: what could not be confirmed, why, and what it would mean.</summary>
    private static string DescribeUndetermined(string detail) =>
        $"JIM could not confirm that the account it connects as can read {AccesslogDn}: {detail}. " +
        "If it cannot, Delta Imports from this directory detect no changes.";

    #endregion

    #region Reading changes

    /// <summary>The pagination token a page leaves when the directory has more of the change read to give.</summary>
    private const string PaginationTokenName = "accesslog";

    /// <summary>
    /// What the change read asks for: when, what kind of write, to which entry, and that entry's entryUUID, which is
    /// how a deletion is matched to the object JIM holds. Never reqOld: it holds a modified attribute's previous values
    /// (a large group's whole member list on every membership change), and JIM needs none of it (#1066).
    /// </summary>
    private static readonly string[] ChangeAttributes = ["reqStart", "reqType", "reqDN", "reqEntryUUID"];

    /// <inheritdoc />
    /// <remarks>
    /// Queries cn=accesslog for write operations at or after the previous watermark timestamp, one page per call. For
    /// each change, fetches the current state of the affected object through the host; a deletion is built from its
    /// own accesslog entry, because the object is no longer there to fetch.
    /// <para>
    /// cn=accesslog is shared by every database on the server, so the read is confined to the target partitions on
    /// the server rather than read whole and filtered here. The pages come from RFC 2696 Simple Paged Results: the
    /// directory's cursor carries the read from one page to the next, so nothing falls between pages however the
    /// log's entries are ordered. A directory that stops the read at its size limit fails it, because an account the
    /// limit applies to cannot read the whole log.
    /// </para>
    /// </remarks>
    /// <exception cref="CannotPerformDeltaImportException">The directory refused the accesslog search, has no accesslog to search, or stopped the search at its size limit.</exception>
    public async Task ReadChangesAsync(LdapDeltaReadContext context, ConnectedSystemImportResult result, CancellationToken cancellationToken)
    {
        // The import establishes HasBaseline for every target Partition before calling, so each has a timestamp. Until
        // each Partition is read from its own (#2715, phase 4), every one is read from the oldest, which re-reads what a
        // Partition with a later watermark already read and misses nothing.
        var previousTimestamp = context.OldestWatermark(watermark => watermark.AccesslogTimestamp!, StringComparer.Ordinal);

        await context.Host.EnterPhaseAsync(LdapConnectorPhases.QueryChanges, $"Querying changes since {previousTimestamp}...");

        _logger.Debug("GetDeltaResultsUsingAccesslog: Querying for changes since {PreviousTimestamp}", previousTimestamp);

        // Build partition suffix list for filtering: OpenLDAP shares cn=accesslog across
        // all databases, so we must exclude entries from other suffixes.
        var partitionSuffixes = PartitionSuffixes(context.TargetPartitions.Select(p => p.ExternalId));
        // Partition filtering alone is not the same selection a full import makes: within a partition, only the
        // selected Containers are imported from, and each carries its own scope. Applying that here keeps a delta
        // import to the objects a full import would have returned.
        var targetContainers = context.ScopeDecidingContainers;

        var cookie = context.PaginationTokens.SingleOrDefault(t => t.Name == PaginationTokenName)?.ByteValue;

        // LDAP only supports >= (not >), so we use >= and skip the watermark's own entry in code. Confined to the
        // target partitions on the server; with no partition to name, the read is not narrowed.
        var request = new SearchRequest(AccesslogDn, WriteOperations(previousTimestamp, partitionSuffixes), SearchScope.OneLevel, ChangeAttributes);
        var pageControl = new PageResultRequestControl(context.PageSize) { IsCritical = false };
        if (cookie is { Length: > 0 })
            pageControl.Cookie = cookie;
        request.Controls.Add(pageControl);

        SearchResponse response;

        // A search the directory will not answer stops the run rather than ending the read: returning here
        // would import nothing, which the run would report as "no changes". A fault on the way to the directory
        // (any other LdapException) propagates as the run's failure, in the directory's own words.
        try
        {
            response = (SearchResponse)_executor.SendRequest(request, context.SearchTimeout);

            if (response == null || response.ResultCode != ResultCode.Success)
                throw RefusedSearch($"result code {response?.ResultCode.ToString() ?? "none"}");
        }
        catch (DirectoryOperationException ex) when (ex.Response?.ResultCode == ResultCode.NoSuchObject)
        {
            throw MissingAccesslog();
        }
        catch (DirectoryOperationException ex) when (ex.Response?.ResultCode == ResultCode.SizeLimitExceeded)
        {
            throw SizeLimited();
        }
        catch (DirectoryOperationException ex)
        {
            throw RefusedSearch(DirectorysWords(ex));
        }
        catch (LdapException ex) when (ex.ErrorCode == 32) // noSuchObject, the legacy shape
        {
            throw MissingAccesslog();
        }

        _logger.Debug("GetDeltaResultsUsingAccesslog: Processing {Count} accesslog entries", response.Entries.Count);

        var objectsReadBefore = result.ImportObjects.Count;
        var totalEntries = 0;
        var skippedOutOfScope = 0;
        // Track processed DNs to avoid importing the same object multiple times when the
        // accesslog has multiple changes for the same DN (e.g., 3 member modifications to
        // the same group). Since we fetch current state rather than replaying individual
        // changes, only the first occurrence needs to be processed. A later page's copy of an
        // object takes an earlier page's place in the import, so this only needs to span the page.
        var processedDns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (SearchResultEntry entry in response.Entries)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                _logger.Debug("GetDeltaResultsUsingAccesslog: Cancellation requested. Stopping");
                return;
            }

            var reqStart = LdapConnectorUtilities.GetEntryAttributeStringValue(entry, "reqStart");
            var reqType = LdapConnectorUtilities.GetEntryAttributeStringValue(entry, "reqType");
            var reqDn = LdapConnectorUtilities.GetEntryAttributeStringValue(entry, "reqDN");

            if (string.IsNullOrEmpty(reqDn) || string.IsNullOrEmpty(reqStart))
                continue;

            // The search is confined to the target partitions on the server; this keeps the selection exact
            // whatever the directory made of the filter, at the cost of a string comparison.
            if (partitionSuffixes.Count > 0 &&
                !partitionSuffixes.Any(suffix => reqDn.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)))
            {
                skippedOutOfScope++;
                continue;
            }

            // Filter by Container scope: the entry is within a selected partition, but the administrator
            // selects Containers within it, and a OneLevel Container excludes everything below its own level.
            if (!LdapConnectorUtilities.IsDnInScope(reqDn, targetContainers))
            {
                skippedOutOfScope++;
                continue;
            }

            // Skip the exact timestamp match from the previous watermark
            if (reqStart == previousTimestamp)
                continue;

            totalEntries++;

            // Map accesslog reqType to ObjectChangeType
            var objectChangeType = reqType?.ToLowerInvariant() switch
            {
                "add" => ObjectChangeType.Added,
                "modify" => ObjectChangeType.Updated,
                "delete" => ObjectChangeType.Deleted,
                "modrdn" => ObjectChangeType.Updated,
                _ => ObjectChangeType.NotSet
            };

            // Deduplicate by DN for non-delete operations. Multiple add/modify entries for
            // the same DN would produce identical import objects (since we fetch current state),
            // triggering duplicate detection errors. However, delete entries must ALWAYS be
            // processed even if the DN was already seen: a group that was added then deleted
            // in the same delta window must be processed as a delete (the final state).
            if (objectChangeType != ObjectChangeType.Deleted && !processedDns.Add(reqDn))
                continue;

            if (objectChangeType == ObjectChangeType.Deleted)
            {
                // A deleted object cannot be fetched, so the deletion is built from its log entry: the entryUUID it
                // is matched on, and its DN.
                var deleteObject = BuildDeletion(entry, reqDn, context);
                if (deleteObject != null)
                    result.ImportObjects.Add(deleteObject);
            }
            else
            {
                // For add/modify/modrdn, fetch the current state of the object
                var currentObject = context.Host.GetObjectByDn(reqDn, objectChangeType);
                if (currentObject != null)
                {
                    result.ImportObjects.Add(currentObject);
                }
                else
                {
                    _logger.Warning("GetDeltaResultsUsingAccesslog: GetObjectByDn returned null for DN '{ReqDn}' " +
                        "(change type: {ChangeType}). The object may have been deleted or moved since the accesslog " +
                        "entry was recorded. Entry skipped.", LogSanitiser.Sanitise(reqDn), objectChangeType);
                }
            }
        }

        // A delta that follows an outage can run to a very large number of changes, each one costing a round trip
        // to fetch the object's current state, so report every page rather than leaving the Activity's counters
        // still until the whole read ends.
        await context.Host.ReportObjectsReadAsync(result.ImportObjects.Count - objectsReadBefore);

        if (response.Controls.OfType<PageResultResponseControl>().SingleOrDefault() is { Cookie.Length: > 0 } pageResponse)
            result.PaginationTokens.Add(new ConnectedSystemPaginationToken(PaginationTokenName, pageResponse.Cookie));

        _logger.Debug("GetDeltaResultsUsingAccesslog: Processed {TotalEntries} change entries on this page. " +
            "Skipped {SkippedOutOfScope} entries outside the selected scope.",
            totalEntries, skippedOutOfScope);
    }

    /// <summary>
    /// The failure a Delta Import stops with when the directory stopped the change read at its size limit: an account
    /// the limit applies to cannot read the whole log, and resuming from the newest timestamp seen could miss an
    /// operation that started earlier and was logged later.
    /// </summary>
    private CannotPerformDeltaImportException SizeLimited()
    {
        _logger.Warning("GetDeltaResultsUsingAccesslog: The directory stopped reading {AccesslogDn} at its size limit. Refusing the Delta Import rather than risk missing changes.", AccesslogDn);
        return new CannotPerformDeltaImportException(
            $"Changes cannot be detected: the directory stopped reading {AccesslogDn} at its size limit, so the changes logged beyond it would go unnoticed. " +
            $"Exempt the account JIM connects as from the size limit on the {AccesslogDn} database with an olcLimits entry (the LDAP Connector documentation, under Service Account Permissions, gives it), " +
            "or run a Full Import, which also detects deletions by absence.");
    }

    /// <summary>
    /// The failure a Delta Import stops with when the accesslog search found nothing at cn=accesslog to search.
    /// </summary>
    private CannotPerformDeltaImportException MissingAccesslog()
    {
        _logger.Warning("GetDeltaResultsUsingAccesslog: No accesslog was found at {AccesslogDn}, or none the account JIM connects as may read. Refusing the Delta Import rather than reporting no changes.", AccesslogDn);
        return new CannotPerformDeltaImportException(DescribeNotFoundForDeltaImport());
    }

    /// <summary>
    /// The failure a Delta Import stops with when the directory refused the accesslog search, in its own words.
    /// </summary>
    private CannotPerformDeltaImportException RefusedSearch(string reason)
    {
        _logger.Warning("GetDeltaResultsUsingAccesslog: The directory refused the search of {AccesslogDn}: {Reason}. Refusing the Delta Import rather than reporting no changes.", AccesslogDn, reason);
        return new CannotPerformDeltaImportException(DescribeRefusedForDeltaImport(reason));
    }

    /// <summary>
    /// A deletion as an import object, from its accesslog auditDelete entry: the deleted entry's entryUUID
    /// (reqEntryUUID) under each selected Object Type's external id attribute, and its DN as
    /// <c>distinguishedName</c>. It names no Object Type: the accesslog records a deleted entry's object classes only in
    /// reqOld, which costs a large group's whole member list on every membership change (#1066), so JIM types the
    /// deletion from the object it holds with that external id instead. Null, with a note on the run, when the entry
    /// carries no reqEntryUUID, since nothing would then match it.
    /// </summary>
    private ConnectedSystemImportObject? BuildDeletion(SearchResultEntry accesslogEntry, string reqDn, LdapDeltaReadContext context)
    {
        var entryUuid = LdapConnectorUtilities.GetEntryAttributeStringValue(accesslogEntry, "reqEntryUUID");
        if (string.IsNullOrEmpty(entryUuid))
        {
            _logger.Warning("GetDeltaResultsUsingAccesslog: The accesslog's record of the deletion of {Dn} carries no reqEntryUUID, so the deletion cannot be matched to an object and is not staged.",
                LogSanitiser.Sanitise(reqDn));
            context.Notes.Record(reqDn, DescribeUnidentifiedDeletion(reqDn));
            return null;
        }

        var deletion = new ConnectedSystemImportObject { ChangeType = ObjectChangeType.Deleted };

        // Every selected Object Type's external id attribute carries the entryUUID, so whichever type JIM holds the
        // object under, its own external id is there to match on. On a directory that is entryUUID itself.
        var externalIdAttributeNames = context.ObjectTypes
            .Where(t => t.Selected)
            .SelectMany(t => t.Attributes.Where(a => a.IsExternalId).Select(a => a.Name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .DefaultIfEmpty(context.PreviousRootDse.ExternalIdAttributeName);
        deletion.Attributes.AddRange(externalIdAttributeNames.Select(name => new ConnectedSystemImportObjectAttribute { Name = name, StringValues = [entryUuid] }));

        deletion.Attributes.Add(new ConnectedSystemImportObjectAttribute { Name = "distinguishedName", StringValues = [reqDn] });
        return deletion;
    }

    /// <summary>The note a run carries for a deletion the accesslog did not record well enough to match.</summary>
    internal static string DescribeUnidentifiedDeletion(string deletedDn) =>
        $"The deletion of {deletedDn} could not be identified: the accesslog's record of it carries no reqEntryUUID, so the object stays in JIM. " +
        "Run a Full Import, which detects deletions by absence.";

    #endregion
}
