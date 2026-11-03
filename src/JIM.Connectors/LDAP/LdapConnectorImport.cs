// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Exceptions;
using JIM.Models.Interfaces;
using JIM.Models.Staging;
using JIM.Utilities;
using Serilog;
using System.Diagnostics;
using System.DirectoryServices.Protocols;
using System.Globalization;
using System.Text.Json;
namespace JIM.Connectors.LDAP;

internal class LdapConnectorImport : ILdapDeltaImportHost
{
    private const int DefaultSearchTimeoutSeconds = 300; // 5 minutes
    private const string SearchTimeoutSettingName = "Search Timeout";

    private readonly CancellationToken _cancellationToken;
    private readonly ConnectedSystem _connectedSystem;
    private readonly ConnectedSystemRunProfile _connectedSystemRunProfile;
    private readonly ILogger _logger;
    private readonly LdapConnection _connection;
    private readonly Func<LdapConnection>? _connectionFactory;
    private readonly int _importConcurrency;
    private readonly List<ConnectedSystemPaginationToken> _paginationTokens;
    private readonly string? _persistedConnectorData;

    /// <summary>
    /// The Delta Import watermark JIM holds for each Partition the run targets, by Partition id, null where it holds
    /// none (#2715). Handed over once for the run and the same on every page, so every page decides alike whether to
    /// read changes or every object. Null when JIM never handed any, which reads as no watermark for any Partition.
    /// </summary>
    private readonly IReadOnlyDictionary<int, string?>? _partitionWatermarks;

    private readonly string? _preferredDomainController;

    /// <summary>
    /// The server this import session's connection was opened against, and a probe for any other, so a
    /// domain controller discovered from the rootDSE is proven reachable before being pinned (#230 Phase 2).
    /// </summary>
    private readonly string _connectedServer;

    private readonly Func<string, bool> _canConnectTo;

    /// <summary>
    /// Set when a discovered domain controller was rejected as unreachable and so not pinned. Read by
    /// <see cref="LdapConnector"/> onto the import result, which surfaces it on the Activity; an existing
    /// warning on the result always wins, being about the import itself rather than about its plumbing.
    /// </summary>
    internal string? PinValidationWarning { get; private set; }

    /// <summary>
    /// Where the reads the import makes through the <see cref="ILdapOperationExecutor"/> seam go: the root DSE read,
    /// every read a change source makes, a full read's searches on the import's own connection, and the follow-up
    /// reads of an attribute the directory answered in ranges (<see cref="LdapRangedAttribute"/>). The import's own
    /// connection; settable so a test can script a directory and drive the whole import, in the order the import runs
    /// it, without a server.
    /// </summary>
    internal ILdapOperationExecutor Executor { get; set; }

    /// <summary>
    /// Set when this session's change source may have missed something: JIM could not confirm that the account it
    /// connects as may read where the directory keeps its changes (for Active Directory, a partition's Deleted
    /// Objects container), or a search there was refused or found no container (#1723). One note per subject, the
    /// later and more definite evidence about a subject replacing the earlier. Read by <see cref="LdapConnector"/>
    /// onto the import result, which surfaces it on the Activity; a warning about how the import itself was
    /// performed still wins, but this beats the pinning note, because a change JIM may not have seen matters more
    /// than plumbing. A proven unavailability is never a warning: the Delta Import refuses to run instead.
    /// </summary>
    internal string? DeltaSourceWarning => _deltaSourceNotes.Warning;
    private LdapDeltaSourceNotes _deltaSourceNotes = new();
    private readonly TimeSpan _searchTimeout;

    /// <summary>
    /// The identity JIM binds as, from the Connected System's Username setting, for the message an import gives when
    /// the directory stops a search at a limit that applies to that identity. Null only if the setting is unset.
    /// </summary>
    private readonly string? _bindIdentity;
    private readonly string _placeholderMemberDn;
    private readonly IConnectorProgress _progress;

    /// <summary>
    /// Objects handed over so far within the call currently being served. A Full Import against a
    /// directory that pages per connection drains every page inside one call, so without this the
    /// Activity's counters would not move until the whole directory had been read. Written from
    /// parallel combos, hence the interlocked increments.
    /// </summary>
    private int _objectsReadThisCall;
    private LdapConnectorRootDse? _currentRootDse;

    /// <summary>
    /// The watermark each target Partition's next Delta Import reads from, as the first page's rootDSE read took them.
    /// Null until then. Read once per call: a Delta Import that reads every object instead hands its read on to the
    /// full read rather than reading the directory, and taking the watermarks, a second time.
    /// </summary>
    private IReadOnlyDictionary<int, LdapPartitionWatermark?>? _watermarksReached;

    /// <summary>
    /// Every Container stating something about scope for this run, held once because every entry the searches
    /// return is tested against it. Lazy because it depends on the Run Profile's target partitions, and
    /// thread-safe because the parallel combos convert their entries concurrently.
    /// </summary>
    private readonly Lazy<List<ConnectedSystemContainer>> _scopeDecidingContainers;

    /// <summary>
    /// How many entries each excluded Container caused to be discarded on the way in. Client-side filtering means
    /// these entries were read from the directory and thrown away, and the design accepted that cost on condition
    /// it is reported rather than hidden (#1255).
    /// </summary>
    private readonly ExclusionDiscardTally _entriesDiscardedByExclusion = new();

    internal LdapConnectorImport(
        ConnectedSystem connectedSystem,
        ConnectedSystemRunProfile runProfile,
        LdapConnection connection,
        Func<LdapConnection>? connectionFactory,
        int importConcurrency,
        List<ConnectedSystemPaginationToken> paginationTokens,
        string? persistedConnectorData,
        IReadOnlyDictionary<int, string?>? partitionWatermarks,
        string? preferredDomainController,
        string connectedServer,
        Func<string, bool> canConnectTo,
        ILogger logger,
        CancellationToken cancellationToken,
        IConnectorProgress progress)
    {
        _connectedSystem = connectedSystem;
        _connectedSystemRunProfile = runProfile;
        _connection = connection;
        Executor = new LdapOperationExecutor(connection);
        _connectionFactory = connectionFactory;
        _importConcurrency = Math.Clamp(importConcurrency, 1, LdapConnectorConstants.MAX_IMPORT_CONCURRENCY);
        _paginationTokens = paginationTokens;
        _persistedConnectorData = persistedConnectorData;
        _partitionWatermarks = partitionWatermarks;
        _preferredDomainController = preferredDomainController;
        _connectedServer = connectedServer;
        _canConnectTo = canConnectTo;
        _logger = logger;
        _cancellationToken = cancellationToken;
        _progress = progress;
        _scopeDecidingContainers = new Lazy<List<ConnectedSystemContainer>>(() => GetScopeDecidingContainers(GetTargetPartitions()));

        // Get search timeout from settings, defaulting to 5 minutes
        _bindIdentity = connectedSystem.SettingValues
            .SingleOrDefault(s => s.Setting.Name == LdapConnectorConstants.SETTING_USERNAME)?.StringValue;

        var searchTimeoutSetting = connectedSystem.SettingValues
            .SingleOrDefault(s => s.Setting.Name == SearchTimeoutSettingName);
        var searchTimeoutSeconds = searchTimeoutSetting?.IntValue ?? DefaultSearchTimeoutSeconds;
        _searchTimeout = TimeSpan.FromSeconds(searchTimeoutSeconds);

        // Get placeholder member DN for filtering during import
        var placeholderSetting = connectedSystem.SettingValues
            .SingleOrDefault(s => s.Setting.Name == LdapConnectorConstants.SETTING_GROUP_PLACEHOLDER_MEMBER_DN);
        _placeholderMemberDn = placeholderSetting?.StringValue ?? LdapConnectorConstants.DEFAULT_GROUP_PLACEHOLDER_MEMBER_DN;

        // If we have persisted connector data from a previous page, deserialise it to get capabilities
        // This allows subsequent pages to know the directory capabilities without re-querying
        if (!string.IsNullOrEmpty(persistedConnectorData) && paginationTokens.Count > 0)
        {
            try
            {
                _currentRootDse = JsonSerializer.Deserialize<LdapConnectorRootDse>(persistedConnectorData);
            }
            catch (JsonException ex)
            {
                _logger.Warning(ex, "LdapConnectorImport: Failed to deserialise persisted connector data for capability detection. Will re-query directory.");
            }
        }
    }

    internal async Task<ConnectedSystemImportResult> GetFullImportObjectsAsync()
    {
        _logger.Verbose("GetFullImportObjects: Started");

        if (_connectedSystem.Partitions == null)
            throw new ArgumentException("_connectedSystem.Partitions is null. Cannot continue.");
        if (_connectedSystem.ObjectTypes == null)
            throw new ArgumentException("_connectedSystem.ObjectTypes is null. Cannot continue.");

        // Every page of a full read reads every object in the Run Profile's scope, whether a Full Import asked for it or a
        // Delta Import fell back to it, and says so: that is what has JIM detect deletions by absence on the fallback (#2720).
        var result = new ConnectedSystemImportResult { CompleteRead = true };

        if (_cancellationToken.IsCancellationRequested)
        {
            _logger.Debug("GetFullImportObjects: O1 Cancellation requested. Stopping");
            return result;
        }

        if (_paginationTokens.Count == 0)
        {
            // initial-page call. we have no paging tokens to use (yet) to resume a query

            // get information about the directory we're connected to, and take the watermark each target Partition's
            // next Delta Import reads from. A Delta Import that reads every object instead has already done both.
            var targetPartitions = GetTargetPartitions().ToList();
            var currentRootDse = _currentRootDse != null && _watermarksReached != null
                ? _currentRootDse
                : await ReadTheDirectoryAsync(targetPartitions);
            ReturnWhatThisRunReached(result);

            // Guard against silently importing zero objects from a Partition the connected domain
            // controller does not host (see #230). AD-family only; a no-op for other directory types.
            LdapConnectorUtilities.VerifyPartitionsAreHostedByConnectedServer(currentRootDse, targetPartitions, _logger);
        }

        // OpenLDAP's RFC 2696 paging cookies are connection-scoped: any new search on the same
        // connection invalidates all outstanding paging cursors. To work around this, we give each
        // container+objectType combo its own dedicated LdapConnection and run combos in parallel,
        // capped by the Import Concurrency setting. Each connection fully drains its paged search
        // before being disposed, so there are no cross-call pagination tokens for this path — all
        // data is fetched within this single call.
        //
        // When the connection factory is unavailable or concurrency is 1, we fall back to the
        // original serialised approach using the primary connection (one combo at a time).
        //
        // For AD directories, this block is skipped entirely — AD supports multiple concurrent
        // paged searches on a single connection, so the original multi-combo-per-page logic below
        // is used instead.
        var isConnectionScopedPaging = _currentRootDse?.DirectoryType is LdapDirectoryType.OpenLDAP or LdapDirectoryType.Generic or LdapDirectoryType.DirectoryServer389;


        if (isConnectionScopedPaging)
        {
            // Build the ordered list of all container+objectType combos
            var combos = new List<(ConnectedSystemContainer Container, ConnectedSystemObjectType ObjectType)>();
            foreach (var selectedPartition in GetTargetPartitions())
            {
                foreach (var selectedContainer in ConnectedSystemUtilities.GetTopLevelSelectedContainers(selectedPartition))
                {
                    foreach (var selectedObjectType in _connectedSystem.ObjectTypes.Where(ot => ot.Selected))
                    {
                        combos.Add((selectedContainer, selectedObjectType));
                    }
                }
            }

            if (combos.Count == 0)
                return result;

            _logger.Debug("GetFullImportObjects: OpenLDAP/Generic directory detected. Processing {ComboCount} container+objectType combos with concurrency {Concurrency}",
                combos.Count, _connectionFactory != null ? _importConcurrency : 1);

            if (_connectionFactory != null && _importConcurrency > 1)
            {
                // Parallel path: one dedicated connection per combo, capped by semaphore.
                // Each combo fully drains all pages on its own connection, so no pagination
                // tokens are returned — the import processor sees this as a single-page result.
                await GetFullImportObjectsParallelAsync(result, combos);
            }
            else
            {
                // Sequential fallback: use the primary connection, one combo at a time.
                // Each combo is fully drained before moving to the next.
                await GetFullImportObjectsSequentialAsync(result, combos);
            }

            return result;
        }

        // Non-OpenLDAP: original behaviour — query all combos on every page
        foreach (var selectedPartition in GetTargetPartitions())
        {
            foreach (var selectedContainer in ConnectedSystemUtilities.GetTopLevelSelectedContainers(selectedPartition))
            {
                foreach (var selectedObjectType in _connectedSystem.ObjectTypes.Where(ot => ot.Selected))
                {
                    var paginationTokenName = LdapConnectorUtilities.GetPaginationTokenName(selectedContainer, selectedObjectType);
                    var paginationToken = _paginationTokens.SingleOrDefault(pt => pt.Name == paginationTokenName);
                    var lastRunsCookie = paginationToken?.ByteValue;

                    if (_paginationTokens.Count > 0 && paginationToken == null)
                        continue;

                    if (_cancellationToken.IsCancellationRequested)
                    {
                        _logger.Debug("GetFullImportObjects: Cancellation requested. Stopping");
                        return result;
                    }

                    await _progress.EnterPhaseAsync(LdapConnectorPhases.Fetch, $"Fetching {selectedObjectType.Name} objects from {selectedContainer.Name}...");
                    await ReportObjectsReadByAsync(result,
                        () => GetFisoResults(result, selectedContainer, selectedObjectType, lastRunsCookie));
                }
            }
        }

        // closing notes:
        // this implementation ends up performing paging per selected container, which might confuse the user if they have a lot of selected containers and end up
        // getting more results back per pass than they expect. Consider refactoring the interface between JIM.Service and the connector, so it is executed once per selected container
        // so the connector always returns a page of results to the JIM.Service (a page of results per container).

        return result;
    }

    internal async Task<ConnectedSystemImportResult> GetDeltaImportObjectsAsync()
    {
        _logger.Verbose("GetDeltaImportObjects: Started");

        if (_connectedSystem.Partitions == null)
            throw new ArgumentException("_connectedSystem.Partitions is null. Cannot continue.");
        if (_connectedSystem.ObjectTypes == null)
            throw new ArgumentException("_connectedSystem.ObjectTypes is null. Cannot continue.");

        var result = new ConnectedSystemImportResult();

        if (_cancellationToken.IsCancellationRequested)
        {
            _logger.Debug("GetDeltaImportObjects: Cancellation requested. Stopping");
            return result;
        }

        // The record the last import left for the Connected System, as JIM handed it at the start of the run, says
        // what the directory is and so which source reads its changes. Every page is handed the same record and the
        // same Partition watermarks, so whether to read changes or every object is decided from them alone and every
        // page decides alike; the rootDSE this run reads would let page 1 and page 2 decide differently.
        var record = ReadConnectedSystemRecord();
        var targetPartitions = GetTargetPartitions().ToList();
        var currentRootDse = _paginationTokens.Count == 0 ? await ReadTheDirectoryAsync(targetPartitions) : null;
        if (currentRootDse != null)
            ReturnWhatThisRunReached(result);

        // No partition to read: none is selected, or the hierarchy was never retrieved. Reading every object would read
        // nothing while claiming to have read everything, so the run reads nothing, as a Full Import of the same Run
        // Profile does, and says so.
        if (targetPartitions.Count == 0)
            return ReadNothing(result);

        // With nothing recorded about the directory there is nothing to read a watermark against, so every object is
        // read, as for a partition without a watermark (#2715 Decision 2); the readiness check still runs first.
        if (record == null)
        {
            if (currentRootDse != null)
                await VerifyBeforeReadingAsync(DeltaSourceFor(currentRootDse), currentRootDse, targetPartitions, watermarks: null);

            _logger.Warning("GetDeltaImportObjects: JIM holds no readable record of this directory from an earlier import, so no partition's watermark can be read against it");
            return await ReadEveryObjectInsteadAsync(targetPartitions);
        }

        var source = DeltaSourceFor(record);
        var previous = PreviousWatermarksOf(targetPartitions);
        var withoutWatermark = targetPartitions.Where(partition => !source.HasBaseline(previous[partition.Id])).ToList();
        var watermarks = withoutWatermark.Count == 0
            ? targetPartitions.ToDictionary(partition => partition.Id, partition => previous[partition.Id]!)
            : null;

        if (currentRootDse != null)
            await VerifyBeforeReadingAsync(source, currentRootDse, targetPartitions, watermarks);

        if (watermarks == null)
            return await ReadEveryObjectInsteadAsync(withoutWatermark);

        var context = new LdapDeltaReadContext
        {
            PreviousRootDse = record,
            Watermarks = watermarks,
            CurrentRootDse = _currentRootDse,
            TargetPartitions = targetPartitions,
            ScopeDecidingContainers = _scopeDecidingContainers.Value,
            ObjectTypes = _connectedSystem.ObjectTypes,
            PaginationTokens = _paginationTokens,
            PageSize = _connectedSystemRunProfile.PageSize,
            SearchTimeout = _searchTimeout,
            Notes = _deltaSourceNotes,
            Host = this
        };
        await source.ReadChangesAsync(context, result, _cancellationToken);

        return result;
    }

    /// <summary>
    /// Reads the directory on a run's first page, and takes the watermark each target Partition's next Delta Import
    /// reads from, the change source handed the watermarks JIM holds so it can narrow its search.
    /// </summary>
    private async Task<LdapConnectorRootDse> ReadTheDirectoryAsync(IReadOnlyList<ConnectedSystemPartition> targetPartitions)
    {
        await _progress.EnterPhaseAsync(LdapConnectorPhases.RootDse, "Querying root DSE...");
        var (rootDse, watermarksReached) = await GetRootDseInformationAsync(PreviousWatermarksOf(targetPartitions), targetPartitions);
        _currentRootDse = rootDse;
        _watermarksReached = watermarksReached;
        return rootDse;
    }

    /// <summary>
    /// What the first page returns besides its objects: the record of the directory the next run is handed, and the
    /// watermark this run reached for each target Partition, null for one whose watermark could not be taken, so its
    /// next Delta Import reads every object in it. A Partition the run does not target is never named.
    /// </summary>
    private void ReturnWhatThisRunReached(ConnectedSystemImportResult result)
    {
        result.PersistedConnectorData = JsonSerializer.Serialize(_currentRootDse);
        result.PartitionWatermarks = (_watermarksReached ?? new Dictionary<int, LdapPartitionWatermark?>())
            .ToDictionary(watermark => watermark.Key, watermark => watermark.Value?.Serialise());
    }

    /// <summary>
    /// The checks a Delta Import makes on its first page before it reads anything: that each watermark still means
    /// something against the directory reached (when it is going to read from them), that the domain controller
    /// hosts the target Partitions, and that the account JIM connects as can read where the changes are kept.
    /// </summary>
    /// <param name="watermarks">The watermark of every target Partition, or null when the run reads every object
    /// instead, which needs no watermark to still apply.</param>
    private async Task VerifyBeforeReadingAsync(ILdapDeltaSource source, LdapConnectorRootDse currentRootDse,
        List<ConnectedSystemPartition> targetPartitions, IReadOnlyDictionary<int, LdapPartitionWatermark>? watermarks)
    {
        // Guard against a delta import silently reading a watermark against a directory it no longer applies to, such
        // as a USN watermark read back from a different domain controller than the one that issued it (see #230). This
        // must run before any delta querying below, on the same connection that just answered the rootDSE query. Each
        // distinct watermark is checked once, naming every Partition that holds it.
        if (watermarks != null)
        {
            foreach (var holders in targetPartitions.GroupBy(partition => watermarks[partition.Id]))
                source.VerifyContinuity(holders.ToList(), holders.Key, currentRootDse);
        }

        // Guard against silently importing zero objects from a Partition the connected domain
        // controller does not host (see #230). AD-family only; a no-op for other directory types.
        LdapConnectorUtilities.VerifyPartitionsAreHostedByConnectedServer(currentRootDse, targetPartitions, _logger);

        // Guard against silently importing zero changes (see #1723): a source the account JIM connects as
        // cannot read is not always refused, its search can succeed with no rows, so continuing would import
        // what it could see and provably miss the rest. Runs before any change is queried. An unavailability
        // JIM could prove stops the run; one it could not confirm becomes a warning.
        var findings = await source.VerifyReadinessAsync(currentRootDse, NamingContextsOf(targetPartitions), _cancellationToken);
        _deltaSourceNotes = LdapDeltaSourceFindings.ThrowOnUnavailableOrNote(findings, _logger);
    }

    /// <summary>
    /// Reads every object in the run's Partitions instead of their changes, because a target Partition has no
    /// watermark to read changes after. The full read reports itself complete, so JIM detects deletions by absence as
    /// on a Full Import (#2720), and the run records a watermark for each Partition, so its next Delta Import reads
    /// only the changes after it. Falling back rather than failing is what the SQL and SCIM Connectors already do; the
    /// run says so in its warning.
    /// </summary>
    private async Task<ConnectedSystemImportResult> ReadEveryObjectInsteadAsync(IReadOnlyList<ConnectedSystemPartition> withoutWatermark)
    {
        var partitions = LdapConnectorUtilities.DescribePartitions(withoutWatermark);
        _logger.Warning("GetDeltaImportObjects: JIM holds no change watermark for {Partitions}. Reading every object in the run's partitions instead, " +
            "which records a watermark for each that the next Delta Import reads from.", LogSanitiser.Sanitise(partitions));

        var result = await GetFullImportObjectsAsync();
        result.WarningMessage = $"A Delta Import was requested, but JIM holds no Delta Import watermark for {partitions} to read changes from. " +
            "Every object in this Run Profile's partitions was read instead, as a Full Import does, and an object JIM holds that the directory no longer has " +
            "is marked as deleted, within this Run Profile's deletion limits. Once a run like this has finished that check, " +
            "it records a watermark for each partition it read, and the next Delta Import reads only the changes after it.";
        result.WarningErrorType = ActivityRunProfileExecutionItemErrorType.DeltaImportFallbackToFullImport;
        return result;
    }

    /// <summary>
    /// What a Delta Import whose Run Profile targets no selected partition returns: no object, the record of the
    /// directory the first page read, and a warning saying why nothing was read. It is not a complete read, so JIM
    /// judges nothing it holds deleted.
    /// </summary>
    private ConnectedSystemImportResult ReadNothing(ConnectedSystemImportResult result)
    {
        _logger.Warning("GetDeltaImportObjects: The Run Profile targets no selected partition, so there is nothing to read changes in; nothing was read");
        result.WarningMessage = "This Delta Import read nothing: its Run Profile targets no selected partition, so there is nowhere to read changes from. " +
            "Select the partitions JIM manages on the Connected System's Scope tab.";
        return result;
    }

    /// <summary>
    /// The watermark JIM handed back for each target Partition, read: null where it holds none, or none this
    /// Connector can read.
    /// </summary>
    private Dictionary<int, LdapPartitionWatermark?> PreviousWatermarksOf(IEnumerable<ConnectedSystemPartition> targetPartitions) =>
        targetPartitions.ToDictionary(partition => partition.Id, partition => LdapPartitionWatermark.Parse(_partitionWatermarks?.GetValueOrDefault(partition.Id), _logger));

    /// <summary>
    /// Returns the partitions to import from: the partition the Run Profile targets when it targets one, otherwise
    /// every selected partition. Only selected partitions are ever returned.
    /// </summary>
    /// <remarks>
    /// The decision itself lives in <see cref="ConnectedSystemExtensions.GetTargetPartitions"/> so that the Connector,
    /// the Run Profile validation in JIM.Application and the portal cannot answer "what does this Run Profile read?"
    /// three different ways. This Connector previously returned a targeted partition without consulting its Selected
    /// flag, which made deselecting a partition a no-op for any Run Profile that named it.
    /// </remarks>
    private IEnumerable<ConnectedSystemPartition> GetTargetPartitions()
    {
        var targets = _connectedSystem.GetTargetPartitions(_connectedSystemRunProfile).ToList();

        if (_connectedSystemRunProfile.Partition != null)
        {
            _logger.Debug("GetTargetPartitions: Run Profile targets partition {PartitionName}; {Count} partition(s) in scope after applying selection",
                LogSanitiser.Sanitise(_connectedSystemRunProfile.Partition.Name), targets.Count);
        }
        else
        {
            _logger.Debug("GetTargetPartitions: No partition specified on Run Profile, importing from all {Count} selected partition(s)", targets.Count);
        }

        return targets;
    }

    /// <summary>
    /// The distinguished names of the partitions this run imports from, which is how a change source is told which
    /// partitions to check, and to confine its searches to.
    /// </summary>
    private static List<string> NamingContextsOf(IEnumerable<ConnectedSystemPartition> targetPartitions) =>
        targetPartitions.Select(p => p.ExternalId).ToList();

    /// <summary>
    /// Returns every Container stating something about scope across the partitions this run targets: the
    /// selections and the exclusions alike.
    /// </summary>
    /// <remarks>
    /// This is what decides an object's fate, and it is deliberately a different list from the search roots
    /// (<see cref="ConnectedSystemUtilities.GetTopLevelSelectedContainers"/>), which say only where to search
    /// from. A Subtree search of a selected Container returns everything beneath it, including any branch the
    /// administrator has excluded (#1255), so an exclusion adds no search root of its own and the entries a
    /// search returns are filtered against this list on the way through. Decomposing the searches to avoid
    /// excluded branches server-side was rejected in the design: it would make import scope depend on how
    /// recently the hierarchy was refreshed, so a new Container beneath a selected parent would be silently
    /// skipped and its objects obsoleted.
    ///
    /// Four paths ask this question and all must reach the same answer, or a delta import sees objects a full
    /// import would not: the full import and the AD USN delta, filtering the entries their searches return; and
    /// the changelog and accesslog deltas, which read one directory-wide log and have to decide per entry whether
    /// the changed object is one this Connected System imports.
    /// </remarks>
    private List<ConnectedSystemContainer> GetScopeDecidingContainers(IEnumerable<ConnectedSystemPartition> targetPartitions)
    {
        return targetPartitions.SelectMany(partition => partition.GetScopeDecidingContainers()).ToList();
    }

    /// <summary>
    /// Reports how many entries each excluded Container caused this import call to read and discard, onto the
    /// result and into the log.
    /// </summary>
    /// <remarks>
    /// Client-side filtering is the deliberate choice (#1255): a directory cannot express "this subtree except
    /// that branch" in one search, and decomposing the searches instead would make import scope depend on how
    /// recently the hierarchy was refreshed. The cost is entries transferred only to be thrown away, and the
    /// design accepted that cost on condition it is visible: these counts are the evidence for revisiting the
    /// decision if an exclusion turns out to sit in front of a large branch.
    ///
    /// Both channels, not one. The log is where an engineer reading a run's output finds it; the result is what
    /// carries it to the Activity, which is where the administrator who configured the exclusion will look.
    /// </remarks>
    internal void ReportEntriesDiscardedByExclusion(ConnectedSystemImportResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (_entriesDiscardedByExclusion.IsEmpty)
            return;

        var counts = _entriesDiscardedByExclusion.ToCounts();
        result.EntriesDiscardedByExclusion = counts;

        _logger.Information("LdapConnectorImport: Discarded {DiscardedCount} entries read from excluded Containers across {ContainerCount} exclusion(s)",
            _entriesDiscardedByExclusion.Total, counts.Count);

        // Named by Distinguished Name in the log even though the count travels by id: the log is read by a person,
        // and a Container id says nothing to one.
        var containersById = _scopeDecidingContainers.Value.ToDictionary(container => container.Id);
        var named = counts.Select(count => (
            ExternalId: containersById.TryGetValue(count.ContainerId, out var container)
                ? container.ExternalId
                : $"Container {count.ContainerId}",
            count.EntriesDiscarded));

        foreach (var (externalId, discarded) in named)
            _logger.Information("LdapConnectorImport: Excluded Container '{Container}' discarded {DiscardedCount} entries",
                LogSanitiser.Sanitise(externalId), discarded);
    }

    /// <summary>
    /// Whether an entry a search returned is one this Connected System imports, counting it against the excluded
    /// Container that carved it out where it is not.
    /// </summary>
    /// <remarks>
    /// Applied to every entry converted, on the full import and both delta paths alike, because a directory
    /// cannot express "this subtree except that branch" in a single search. Where no Container has been excluded
    /// this is the same answer the search itself gave, at the cost of one containment test per entry against a
    /// list bounded by the number of selected Containers.
    /// </remarks>
    private bool IsWithinImportScope(string? distinguishedName)
    {
        var scopeDecidingContainers = _scopeDecidingContainers.Value;

        // No Container-level opinion to apply: the partition selection alone governs, as it did before Containers
        // could be excluded.
        if (scopeDecidingContainers.Count == 0)
            return true;

        if (LdapConnectorUtilities.IsDnInScope(distinguishedName, scopeDecidingContainers))
            return true;

        var excludedBy = LdapConnectorUtilities.ResolveMostSpecificContainerScope(distinguishedName, scopeDecidingContainers);
        if (excludedBy is { Excluded: true })
            _entriesDiscardedByExclusion.RecordDiscard(excludedBy);

        return false;
    }

    /// <summary>
    /// Processes all container+objectType combos in parallel using a dedicated LdapConnection per combo.
    /// Each combo fully drains all pages on its own connection, avoiding the RFC 2696 connection-scoped
    /// paging cookie limitation. Concurrency is capped by <see cref="_importConcurrency"/>.
    /// </summary>
    private async Task GetFullImportObjectsParallelAsync(
        ConnectedSystemImportResult result,
        List<(ConnectedSystemContainer Container, ConnectedSystemObjectType ObjectType)> combos)
    {
        var stopwatch = Stopwatch.StartNew();

        // Each combo gets its own result to avoid contention on shared collections.
        // Results are merged after all combos complete.
        var comboResults = new ConnectedSystemImportResult[combos.Count];
        for (var i = 0; i < comboResults.Length; i++)
            comboResults[i] = new ConnectedSystemImportResult();

        using var semaphore = new SemaphoreSlim(_importConcurrency);
        var tasks = new Task[combos.Count];

        for (var i = 0; i < combos.Count; i++)
        {
            _cancellationToken.ThrowIfCancellationRequested();

            var index = i;
            var (container, objectType) = combos[i];

            tasks[i] = Task.Run(async () =>
            {
                await semaphore.WaitAsync(_cancellationToken);
                LdapConnection? comboConnection = null;
                try
                {
                    comboConnection = _connectionFactory!();
                    _logger.Debug("GetFullImportObjectsParallel: Started combo {Index}/{Total} — container={Container}, objectType={ObjectType}",
                        index + 1, combos.Count, container.Name, objectType.Name);

                    // Fully drain all pages for this combo on its dedicated connection
                    await DrainAllPagesAsync(comboResults[index], comboConnection, container, objectType);
                }
                catch (OperationCanceledException)
                {
                    _logger.Debug("GetFullImportObjectsParallel: Combo {Index} cancelled", index + 1);
                }
                catch (OperationalException ex)
                {
                    // A refusal the import chose (for example a search stopped at the directory's limit): the
                    // message says everything, and the Activity reports it without a stack trace.
                    _logger.Warning("GetFullImportObjectsParallel: Combo {Index} refused — container={Container}, objectType={ObjectType}: {Message}",
                        index + 1, container.Name, objectType.Name, LogSanitiser.Sanitise(ex.Message));
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "GetFullImportObjectsParallel: Combo {Index} failed — container={Container}, objectType={ObjectType}",
                        index + 1, container.Name, objectType.Name);
                    throw;
                }
                finally
                {
                    comboConnection?.Dispose();
                    semaphore.Release();
                }
            }, _cancellationToken);
        }

        var allCombos = Task.WhenAll(tasks);
        try
        {
            await allCombos;
        }
        catch (OperationCanceledException)
        {
            _logger.Debug("GetFullImportObjectsParallel: Cancelled while waiting for combos to complete");
            return;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Awaiting rethrows only the first exception; unwrap and rethrow the first real
            // (non-cancellation) failure so the import processor sees it
            var inner = allCombos.Exception?.Flatten().InnerExceptions.FirstOrDefault(e => e is not OperationCanceledException);
            if (inner != null)
                throw inner;

            _logger.Warning(ex, "GetFullImportObjectsParallel: Combos faulted but no failure could be unwrapped to report");
            return;
        }

        // Merge results from all combos
        foreach (var comboResult in comboResults)
        {
            result.ImportObjects.AddRange(comboResult.ImportObjects);
        }

        stopwatch.Stop();
        _logger.Information("GetFullImportObjectsParallel: Completed {ComboCount} combos in {Elapsed}. Total objects: {ObjectCount}",
            combos.Count, stopwatch.Elapsed, result.ImportObjects.Count);
    }

    /// <summary>
    /// Processes all container+objectType combos sequentially on the primary connection.
    /// Each combo is fully drained (all pages) before moving to the next.
    /// Used as a fallback when the connection factory is unavailable or concurrency is 1.
    /// </summary>
    private async Task GetFullImportObjectsSequentialAsync(
        ConnectedSystemImportResult result,
        List<(ConnectedSystemContainer Container, ConnectedSystemObjectType ObjectType)> combos)
    {
        foreach (var (container, objectType) in combos)
        {
            if (_cancellationToken.IsCancellationRequested)
            {
                _logger.Debug("GetFullImportObjectsSequential: Cancellation requested. Stopping");
                return;
            }

            await DrainAllPagesAsync(result, _connection, container, objectType);
        }
    }

    /// <summary>
    /// Fully drains all pages for a single container+objectType combination on the given connection.
    /// Keeps issuing paged search requests until the server returns an empty paging cookie.
    /// </summary>
    private async Task DrainAllPagesAsync(
        ConnectedSystemImportResult result,
        LdapConnection connection,
        ConnectedSystemContainer container,
        ConnectedSystemObjectType objectType)
    {
        byte[]? pagingCookie = null;
        var page = 0;

        while (true)
        {
            if (_cancellationToken.IsCancellationRequested)
                return;

            page++;
            await _progress.EnterPhaseAsync(LdapConnectorPhases.Fetch, $"Fetching {objectType.Name} objects from {container.Name} (page {page:N0})...");

            var comboResult = new ConnectedSystemImportResult();
            GetFisoResults(comboResult, connection, container, objectType, pagingCookie);

            result.ImportObjects.AddRange(comboResult.ImportObjects);
            await ReportObjectsReadAsync(comboResult.ImportObjects.Count);

            // Check if there are more pages
            if (comboResult.PaginationTokens.Count > 0)
            {
                pagingCookie = comboResult.PaginationTokens[0].ByteValue;
            }
            else
            {
                // No more pages — this combo is fully drained
                break;
            }
        }
    }

    #region private methods
    /// <summary>
    /// Tells JIM how many objects this call has read so far, so the Activity's counters move
    /// while a call that drains many directory pages is still in flight.
    /// </summary>
    /// <remarks>
    /// The directory cannot be asked how many objects a search will return without running it, so
    /// this Connector states no expected total: a percentage would have to be invented, and the
    /// count and rate on their own are honest.
    /// </remarks>
    private Task ReportObjectsReadAsync(int objectsJustRead)
    {
        if (objectsJustRead <= 0)
            return Task.CompletedTask;

        return _progress.ReportObjectsReadAsync(Interlocked.Add(ref _objectsReadThisCall, objectsJustRead));
    }

    /// <summary>
    /// Runs a piece of work that appends to the import result, and reports whatever it read.
    /// </summary>
    private async Task ReportObjectsReadByAsync(ConnectedSystemImportResult result, Action read)
    {
        var readBefore = result.ImportObjects.Count;
        read();
        await ReportObjectsReadAsync(result.ImportObjects.Count - readBefore);
    }

    /// <summary>
    /// The record the last import left for the Connected System, as JIM handed it at the start of the run: what the
    /// directory was. Null when there is none or it cannot be read.
    /// </summary>
    private LdapConnectorRootDse? ReadConnectedSystemRecord()
    {
        if (string.IsNullOrEmpty(_persistedConnectorData))
            return null;

        try
        {
            return JsonSerializer.Deserialize<LdapConnectorRootDse>(_persistedConnectorData);
        }
        catch (JsonException ex)
        {
            _logger.Warning(ex, "GetDeltaImportObjects: The record the last import left for this Connected System could not be read");
            return null;
        }
    }

    /// <summary>
    /// Reads the rootDSE of the directory reached: its type and capabilities, and the watermark each target Partition's
    /// next Delta Import will read from.
    /// </summary>
    /// <param name="previous">The watermark JIM holds for each target Partition, which the change source may use to
    /// narrow its search for the new ones; null for a Partition that has none.</param>
    /// <param name="targetPartitions">The partitions this run imports from, which a change source whose log every
    /// partition on the server shares may confine that search to, as its change read is (#2712).</param>
    private async Task<(LdapConnectorRootDse RootDse, IReadOnlyDictionary<int, LdapPartitionWatermark?> Watermarks)> GetRootDseInformationAsync(
        IReadOnlyDictionary<int, LdapPartitionWatermark?> previous, IReadOnlyList<ConnectedSystemPartition> targetPartitions)
    {
        var request = new SearchRequest()
        {
            Scope = SearchScope.Base,
        };

        request.Attributes.AddRange(new[] {
            "DNSHostName",
            "HighestCommittedUSN",
            "supportedCapabilities",
            "vendorName",
            "structuralObjectClass",
            "dsServiceName"
        });
        request.Attributes.AddRange(LdapConnectorUtilities.RootDseDiscoveryAttributes);


        var response = (SearchResponse)Executor.SendRequest(request);

        if (response == null)
            throw new LdapCommunicationException("LDAP response was null when querying directory information.");

        if (response.ResultCode != ResultCode.Success)
            throw new LdapCommunicationException($"LDAP request failed with result code {response.ResultCode} when querying directory information.");

        if (response.Entries.Count == 0)
            throw new LdapCommunicationException("No entries returned from rootDSE query. Verify the LDAP server is reachable and correctly configured.");

        var rootDseEntry = response.Entries[0];

        // Detect directory type from rootDSE capabilities
        var capabilities = LdapConnectorUtilities.GetEntryAttributeStringValues(rootDseEntry, "supportedCapabilities");
        var vendorName = LdapConnectorUtilities.GetEntryAttributeStringValue(rootDseEntry, "vendorName");
        var structuralObjectClass = LdapConnectorUtilities.GetEntryAttributeStringValue(rootDseEntry, "structuralObjectClass");
        var vendorVersion = LdapConnectorUtilities.GetEntryAttributeStringValue(rootDseEntry, "vendorVersion");
        var directoryType = LdapConnectorUtilities.DetectDirectoryType(capabilities, vendorName, structuralObjectClass, vendorVersion);

        var rootDse = new LdapConnectorRootDse
        {
            DnsHostName = LdapConnectorUtilities.GetEntryAttributeStringValue(rootDseEntry, "DNSHostName"),
            DirectoryType = directoryType,
            VendorName = vendorName
        };
        LdapConnectorUtilities.ApplyRootDseDiscoveryAttributes(rootDseEntry, rootDse);

        // The watermark each target Partition's later delta import reads from, captured by the source that will read
        // it: the highest committed USN and the domain controller's invocationId for Active Directory, the latest
        // accesslog timestamp for OpenLDAP, the last change number for a changelog. This must run during BOTH full and
        // delta imports: a full import establishes the baseline for each Partition's first delta import, and a delta
        // import captures the position the next one starts from. Only the run's own Partitions are given one (#2715).
        // The watermarks JIM holds let the source search from them rather than from the start of its change log
        // (#2704), and the run's partitions let it leave out the writes to the others where every partition shares
        // one log (#2712).
        var watermarks = await DeltaSourceFor(rootDse).CaptureWatermarksAsync(rootDseEntry, rootDse, previous, targetPartitions, _searchTimeout);

        // Pin creation and self-healing (issue #230 Phase 2): the connection that answered this rootDSE
        // query was itself opened via the resolved pin (or Host, on a first connection or after a pin was
        // just invalidated), so setting the pin to the domain controller reached here both establishes the
        // pin on first-ever connection and re-affirms/self-heals it on every later import. When a Preferred
        // Domain Controller is configured, the setting owns selection, so any pin from a previous
        // configuration is cleared rather than carried forward into the new baseline.
        var pinDecision = LdapConnectorUtilities.ResolvePinnedDirectoryServerForImport(
            rootDse.IsActiveDirectoryFamily, _preferredDomainController, rootDse.DnsHostName,
            _connectedServer, _canConnectTo, _logger);
        rootDse.PinnedDirectoryServer = pinDecision.PinnedServer;
        PinValidationWarning = pinDecision.WarningMessage;

        _logger.Information("GetRootDseInformation: Directory capabilities detected. DirectoryType={DirectoryType}, VendorName={VendorName}, SupportsPaging={SupportsPaging}, InvocationId={InvocationId}, PinnedDirectoryServer={PinnedDirectoryServer}. " +
            "Watermarks taken for {WatermarkCount} target partition(s), {WithoutCount} of them none",
            rootDse.DirectoryType, rootDse.VendorName ?? "(not set)", rootDse.SupportsPaging, rootDse.InvocationId, LogSanitiser.Sanitise(rootDse.PinnedDirectoryServer) ?? "(not set)",
            watermarks.Count, watermarks.Count(watermark => watermark.Value == null));
        foreach (var (partitionId, watermark) in watermarks)
            _logger.Debug("GetRootDseInformation: Partition {PartitionId} watermark {Watermark}", partitionId, LogSanitiser.Sanitise(watermark?.Serialise()) ?? "(none)");

        return (rootDse, watermarks);
    }

    /// <summary>
    /// Overload that uses the primary connection. Called by the AD (non-connection-scoped) path and delta imports.
    /// </summary>
    /// <summary>
    /// Whether a search result says the directory stopped at one of its own limits (size, time or an
    /// administrative limit) rather than answering the search in full.
    /// </summary>
    internal static bool IsSearchLimitExceeded(ResultCode? resultCode) =>
        resultCode is ResultCode.SizeLimitExceeded or ResultCode.TimeLimitExceeded or ResultCode.AdminLimitExceeded;

    /// <summary>
    /// The message an import refuses with when the directory stopped a container's search at its limit: which
    /// container and object type, that nothing from it was imported, which account the limit applied to, and
    /// the exemption to ask for. Names OpenLDAP's mechanism because that is the directory family that limits a
    /// paged search as a whole; Active Directory pages within its limits on its own.
    /// </summary>
    internal static string DescribeSearchLimitStoppedImport(string containerName, string objectTypeName, string? bindIdentity, string directoryMessage)
    {
        var account = string.IsNullOrWhiteSpace(bindIdentity)
            ? "The account JIM connects as is subject to"
            : $"The account JIM connects as, {bindIdentity}, is subject to";
        return $"The directory stopped the import of {objectTypeName} objects from {containerName} at its search limit ({directoryMessage}), " +
            $"so nothing from {containerName} was imported. {account} the directory's search limits, which its rootDN is not, " +
            "and a smaller page size does not help: OpenLDAP applies the limit across a paged search as a whole. " +
            "Ask the directory administrator to exempt the account (on OpenLDAP, an olcLimits entry for the JIM group on each suffix; " +
            "see Service Account Permissions in the JIM LDAP Connector documentation) rather than raising the limit for every client.";
    }

    /// <summary>
    /// The attributes a Full Import search asks the directory for on <paramref name="connectedSystemObjectType"/>:
    /// every selected attribute by name, plus the identifiers and objectClass JIM always needs.
    /// </summary>
    /// <remarks>
    /// Naming each attribute is what makes a selected operational attribute (<c>pwdFailureTime</c>,
    /// <c>createTimestamp</c>) come back at all: a directory returns operational attributes only when they are
    /// requested by name (RFC 4511 4.5.1.8), and it is why unselected ones cost nothing (#2153, D8).
    /// </remarks>
    internal static string[] GetRequestedAttributeNames(ConnectedSystemObjectType connectedSystemObjectType)
    {
        // add user selected attributes. A connector-defined one (an Exchange attribute, #2021) is not in the directory,
        // and its name is not a valid one to search for: what it is derived from is asked for instead
        var attributes = connectedSystemObjectType.Attributes.Where(a => a.Selected && !a.IsConnectorDefined).Select(a => a.Name).ToList();
        attributes.AddRange(LdapExchangeAttributes.SourceAttributes(SelectedConnectorDefinedAttributeNames(connectedSystemObjectType)));

        // ensure we are also retrieving the unique identifier attribute(s)
        attributes.AddRange(connectedSystemObjectType.Attributes.Where(a => a.IsExternalId).Select(a => a.Name));

        // ensure we also retrieve the secondary external ID attribute (e.g., distinguishedName) so that
        // export confirmation can verify DN changes (moves/renames) were applied successfully
        attributes.AddRange(connectedSystemObjectType.Attributes.Where(a => a.IsSecondaryExternalId).Select(a => a.Name));

        // we also need the objectClass for type matching purposes
        attributes.Add("objectClass");

        // remove any duplicates we might have added and change to a simple array for use with the search request
        return attributes.Distinct().ToArray();
    }

    /// <summary>The connector-defined attributes selected on an Object Type, which the import derives rather than reads.</summary>
    internal static List<string> SelectedConnectorDefinedAttributeNames(ConnectedSystemObjectType connectedSystemObjectType) =>
        connectedSystemObjectType.Attributes.Where(a => a.Selected && a.IsConnectorDefined).Select(a => a.Name).ToList();

    private void GetFisoResults(ConnectedSystemImportResult connectedSystemImportResult, ConnectedSystemContainer connectedSystemContainer, ConnectedSystemObjectType connectedSystemObjectType, byte[]? lastRunsCookie)
        => GetFisoResults(connectedSystemImportResult, _connection, connectedSystemContainer, connectedSystemObjectType, lastRunsCookie);

    private void GetFisoResults(ConnectedSystemImportResult connectedSystemImportResult, LdapConnection connection, ConnectedSystemContainer connectedSystemContainer, ConnectedSystemObjectType connectedSystemObjectType, byte[]? lastRunsCookie)
    {
        if (_cancellationToken.IsCancellationRequested)
        {
            _logger.Debug("GetFisoResults: O1 Cancellation requested. Stopping");
            return;
        }

        var stopwatch = Stopwatch.StartNew();
        var ldapFilter = $"(objectClass={connectedSystemObjectType.Name})";

        var queryAttributes = GetRequestedAttributeNames(connectedSystemObjectType);

        var searchRequest = new SearchRequest(connectedSystemContainer.ExternalId, ldapFilter,
            LdapConnectorUtilities.GetSearchScope(connectedSystemContainer), queryAttributes);

        // Only add paging control if the directory supports it
        // Samba AD claims AD compatibility but returns duplicate results when using paging cookies
        var supportsPaging = _currentRootDse?.SupportsPaging ?? true; // Default to true for backwards compatibility
        if (supportsPaging)
        {
            var pageResultRequestControl = new PageResultRequestControl(_connectedSystemRunProfile.PageSize)
            {
                // Make paging non-critical so servers that don't support paging can ignore it
                IsCritical = false
            };
            if (lastRunsCookie is { Length: > 0 })
                pageResultRequestControl.Cookie = lastRunsCookie;

            searchRequest.Controls.Add(pageResultRequestControl);
        }
        else
        {
            _logger.Debug("GetFisoResults: Paging disabled for this directory (VendorName={VendorName}). Retrieving all results in single request.",
                _currentRootDse?.VendorName ?? "unknown");
        }

        SearchResponse searchResponse;
        try
        {
            // The import's own connection is reached through the executor seam like every other read it makes, so a
            // test can script the whole run; a parallel combo's dedicated connection is reached directly.
            searchResponse = ReferenceEquals(connection, _connection)
                ? (SearchResponse)Executor.SendRequest(searchRequest, _searchTimeout)
                : (SearchResponse)connection.SendRequest(searchRequest, _searchTimeout);
        }
        catch (DirectoryOperationException ex) when (lastRunsCookie is { Length: > 0 } &&
            ex.Message.Contains("does not support the control", StringComparison.OrdinalIgnoreCase))
        {
            // Server returned a cookie on first page but doesn't actually support paging (e.g., Samba AD)
            // Retry without paging control - results should have already been returned on first page
            _logger.Warning("GetFisoResults: Server rejected paging cookie, assuming all results were returned on first page. Error: {Message}", LogSanitiser.Sanitise(ex.Message));
            return;
        }
        catch (DirectoryOperationException ex) when (IsSearchLimitExceeded(ex.Response?.ResultCode))
        {
            // The directory stopped the search at its own limit, so the rest of this container is unread.
            // Continuing would import a truncated container and, on a Full Import, treat every object past the
            // limit as gone. Refuse instead, and say whose limit it was: OpenLDAP applies olcSizeLimit across a
            // paged search as a whole to every client but the rootDN, so this is what a move from the rootDN to
            // a delegated service account looks like (#1718), and the page size cannot change it.
            throw new CannotPerformImportException(DescribeSearchLimitStoppedImport(
                connectedSystemContainer.Name, connectedSystemObjectType.Name, _bindIdentity, ex.Message));
        }

        // Only track pagination tokens if paging is supported
        // For directories without paging support, all results are returned in a single request
        if (supportsPaging && searchResponse.Controls != null && searchResponse.Controls.SingleOrDefault(c => c is PageResultResponseControl) is PageResultResponseControl pageResultResponseControl && pageResultResponseControl.Cookie.Length > 0)
        {
            var tokenName = LdapConnectorUtilities.GetPaginationTokenName(connectedSystemContainer, connectedSystemObjectType);
            connectedSystemImportResult.PaginationTokens.Add(new ConnectedSystemPaginationToken(tokenName, pageResultResponseControl.Cookie));
        }

        if (_cancellationToken.IsCancellationRequested)
        {
            _logger.Debug("GetFisoResults: O2 Cancellation requested. Stopping");
            return;
        }

        // Use NotSet for Full Imports - JIM will determine Create vs Update based on CSO existence.
        // Only delta imports with change tracking should specify explicit Create/Update/Delete.
        connectedSystemImportResult.ImportObjects.AddRange(
            ConvertLdapResults(searchResponse.Entries, ObjectChangeType.NotSet, connectedSystemObjectType));
        stopwatch.Stop();
        _logger.Debug($"GetFisoResults: Executed for object type '{connectedSystemObjectType.Name}' within container '{connectedSystemContainer.Name}' in {stopwatch.Elapsed}");
    }

    /// <summary>
    /// The change source for a directory: how it reports what changed, chosen once from its detected type. Sources
    /// hold no state, so one is made wherever it is needed rather than kept.
    /// </summary>
    private ILdapDeltaSource DeltaSourceFor(LdapConnectorRootDse rootDse) =>
        LdapDeltaSources.Create(rootDse.DeltaSourceKind, Executor, _logger);

    #region ILdapDeltaImportHost members
    // The way a change source reaches the parts of the import that are not about any one directory type.

    IEnumerable<ConnectedSystemImportObject> ILdapDeltaImportHost.ConvertEntries(SearchResultEntryCollection entries, ObjectChangeType changeType, ConnectedSystemObjectType? searchedObjectType) =>
        ConvertLdapResults(entries, changeType, searchedObjectType);

    ConnectedSystemImportObject? ILdapDeltaImportHost.GetObjectByDn(string dn, ObjectChangeType changeType) =>
        GetObjectByDn(dn, changeType);

    Task ILdapDeltaImportHost.EnterPhaseAsync(string phase, string message) =>
        _progress.EnterPhaseAsync(phase, message);

    Task ILdapDeltaImportHost.ReportObjectsReadAsync(int objectsJustRead) =>
        ReportObjectsReadAsync(objectsJustRead);
    #endregion

    private ConnectedSystemImportObject? GetObjectByDn(string dn, ObjectChangeType changeType)
    {
        if (_connectedSystem.ObjectTypes == null)
            return null;

        try
        {
            // Every attribute a Full Import reads, across all selected object types, since the entry's type is not yet known
            var allAttributes = _connectedSystem.ObjectTypes
                .Where(ot => ot.Selected)
                .SelectMany(GetRequestedAttributeNames)
                .Distinct()
                .ToList();

            // Through the executor seam, like every other read a change source makes, so a test can script it
            var searchRequest = new SearchRequest(dn, "(objectClass=*)", SearchScope.Base, allAttributes.ToArray());
            var searchResponse = (SearchResponse)Executor.SendRequest(searchRequest, _searchTimeout);

            if (searchResponse.Entries.Count == 0)
            {
                _logger.Verbose("GetObjectByDn: Object not found at DN {Dn}", LogSanitiser.Sanitise(dn));
                return null;
            }

            var results = ConvertLdapResults(searchResponse.Entries, changeType).ToList();
            return results.FirstOrDefault();
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "GetObjectByDn: Error fetching object at DN {Dn}", LogSanitiser.Sanitise(dn));
            return null;
        }
    }

    /// <param name="searchedObjectType">The Object Type whose search produced these results, so that an entry
    /// carrying more than one selected class is emitted by one search only. Null when the caller is not searching
    /// per Object Type, i.e. fetching a single object by its DN.</param>
    private IEnumerable<ConnectedSystemImportObject> ConvertLdapResults(
        SearchResultEntryCollection searchResults,
        ObjectChangeType changeType,
        ConnectedSystemObjectType? searchedObjectType = null)
    {
        if (_connectedSystem.ObjectTypes == null)
            throw new InvalidDataException("_connectedSystem.ObjectTypes is null. Cannot continue.");

        var importObjects = new List<ConnectedSystemImportObject>();

        // todo (#497): experiment with parallel foreach to see if we can speed up processing
        foreach (SearchResultEntry searchResult in searchResults)
        {
            if (_cancellationToken.IsCancellationRequested)
            {
                _logger.Information("ConvertLdapResults: Cancellation requested. Stopping.");
                break;
            }

            // Discard entries an excluded Container carves out. A Subtree search returns everything beneath its
            // base, so the directory cannot apply an exclusion for us and every path that converts entries has to
            // apply it here instead (#1255). Nothing beyond this point knows about Containers, so a discarded
            // entry never becomes an import object, is never staged, and cannot be exported to.
            if (!IsWithinImportScope(searchResult.DistinguishedName))
                continue;

            // start to build the object that will represent the object in the Connected System. we will pass this back to JIM
            var importObject = new ConnectedSystemImportObject
            {
                ChangeType = changeType
            };

            // work out what JIM object type this result is
            var objectClasses = (string[])searchResult.Attributes["objectclass"].GetValues(typeof(string));
            var objectType = LdapObjectTypeMatcher.Match(objectClasses, _connectedSystem.ObjectTypes);
            if (objectType == null)
            {
                importObject.ErrorType = ConnectedSystemImportObjectError.CouldNotDetermineObjectType;
                importObject.ErrorMessage = $"ConvertLdapResults: Couldn't match object type to object classes received: {string.Join(',', objectClasses)}";
                importObjects.Add(importObject);
                continue;
            }

            // Leave an entry that resolved to another Object Type to that type's own search, which returns it too.
            // Emitting it here as well would stage one directory entry as two Connected System Objects.
            if (!LdapObjectTypeMatcher.OwnsEntry(objectType, searchedObjectType))
                continue;

            importObject.ObjectType = objectType.Name;

            // Connector-defined attributes (Exchange's, #2021) are derived from directory attributes read for them, which
            // are not themselves imported unless they are selected too
            var connectorDefinedAttributes = SelectedConnectorDefinedAttributeNames(objectType);
            var derivationOnlySources = LdapExchangeAttributes.SourceAttributes(connectorDefinedAttributes)
                .Where(source => !objectType.Attributes.Any(a => a.Selected && a.Name.Equals(source, StringComparison.OrdinalIgnoreCase)))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            // start populating import object attribute values from the search result
            foreach (var attributeDescription in LdapRangedAttribute.DescriptionsToRead(searchResult))
            {
                if (derivationOnlySources.Contains(LdapRangedAttribute.TryParse(attributeDescription, out var sourceName, out _, out _) ? sourceName : attributeDescription))
                    continue;

                // Active Directory answers a multi-valued attribute over its MaxValRange (1,500 by default) as
                // "member;range=0-1499", with the plain attribute absent or empty beside it (an empty one is left out
                // of what is read, #2041). The range option is not part of the attribute's name, so it is stripped for
                // the schema lookup, and the values past the first range are read before the attribute is (#1853).
                var attributeName = attributeDescription;
                var attribute = searchResult.Attributes[attributeDescription];
                if (LdapRangedAttribute.TryParse(attributeDescription, out var rangedAttributeName, out _, out _))
                {
                    attributeName = rangedAttributeName;
                    attribute = LdapRangedAttribute.ReadAll(Executor, searchResult, attributeDescription, _searchTimeout, _logger);
                }

                // get the schema attribute for this search result attribute, so we can work out what type it is
                var schemaAttribute = objectType.Attributes.SingleOrDefault(a => a.Name.Equals(attributeName, StringComparison.OrdinalIgnoreCase));
                if (schemaAttribute == null)
                {
                    // The configuration error is what the object reports. Unreadable values found on its earlier
                    // attributes would otherwise stand in for it, since JIM words an object's message from those.
                    importObject.AttributeValueErrors = null;
                    importObject.ErrorType = ConnectedSystemImportObjectError.ConfigurationError;
                    importObject.ErrorMessage = $"Search result attribute '{attributeName}' not found in schema!";
                    break;
                }

                var importObjectAttribute = new ConnectedSystemImportObjectAttribute
                {
                    Name = attributeName,
                    Type = schemaAttribute.Type
                };

                // assign the right type of value(s), keeping any value that is not imported to report below: one that
                // cannot be read as the attribute's type (#2128), or a date JIM cannot keep apart from another (#2153)
                IReadOnlyList<LdapUnreadableValue> unreadableValues = [];
                switch (importObjectAttribute.Type)
                {
                    case AttributeDataType.Text:
                        var stringValues = LdapConnectorUtilities.GetAttributeStringValues(attribute, attributeName, searchResult.DistinguishedName);
                        if (stringValues is { Count: > 0 })
                            importObjectAttribute.StringValues.AddRange(stringValues);
                        break;

                    case AttributeDataType.Number:
                        var numbers = LdapImportValueReader.ReadNumbers(attribute, attributeName, searchResult.DistinguishedName);
                        importObjectAttribute.IntValues.AddRange(numbers.Values);
                        unreadableValues = numbers.Unreadable;
                        break;

                    case AttributeDataType.LongNumber:
                        var longNumbers = LdapImportValueReader.ReadLongNumbers(attribute, attributeName, searchResult.DistinguishedName);
                        unreadableValues = longNumbers.Unreadable;

                        // Filter out protected attribute default values. AD has "protected" attributes that cannot be
                        // cleared; they store a sentinel value instead (accountExpires uses 9223372036854775807 for
                        // "never expires"). On export, JIM writes the sentinel for no value; on import, the sentinel
                        // is not imported, so JIM consistently sees no value and drift detection doesn't produce
                        // false positives. Only a value that was read can be the sentinel: an unreadable one is
                        // reported below, never taken for "never expires".
                        var protectedDefault = LdapConnectorExport.GetProtectedAttributeDefault(attributeName);
                        if (protectedDefault != null && long.TryParse(protectedDefault, NumberStyles.Integer, CultureInfo.InvariantCulture, out var defaultLongValue))
                            longNumbers.Values.RemoveAll(v => v == defaultLongValue);

                        importObjectAttribute.LongValues.AddRange(longNumbers.Values);
                        break;

                    case AttributeDataType.Boolean:
                        var booleans = LdapImportValueReader.ReadBoolean(attribute);
                        importObjectAttribute.BoolValues.AddRange(booleans.Values);
                        unreadableValues = booleans.Unreadable;
                        break;

                    case AttributeDataType.DateTime:
                        var dateTimes = LdapImportValueReader.ReadDateTime(attribute);
                        importObjectAttribute.DateTimeValues.AddRange(dateTimes.Values);
                        unreadableValues = dateTimes.Unreadable;
                        break;

                    case AttributeDataType.Guid:
                        var guids = LdapImportValueReader.ReadGuids(attribute, attributeName, searchResult.DistinguishedName);
                        importObjectAttribute.GuidValues.AddRange(guids.Values);
                        unreadableValues = guids.Unreadable;
                        break;

                    case AttributeDataType.Binary:
                        var binaryValues = LdapConnectorUtilities.GetAttributeBinaryValues(attribute, attributeName, searchResult.DistinguishedName);
                        if (binaryValues is { Count: > 0 })
                            importObjectAttribute.ByteValues.AddRange(binaryValues);
                        break;

                    case AttributeDataType.Reference:
                        var referenceValues = LdapConnectorUtilities.GetAttributeStringValues(attribute, attributeName, searchResult.DistinguishedName);
                        if (referenceValues is { Count: > 0 })
                        {
                            // Filter out the placeholder member DN so it never enters the metaverse.
                            // The placeholder is injected by the connector during export to satisfy the
                            // groupOfNames MUST member constraint — it should be invisible to JIM.
                            var filteredValues = referenceValues.Where(v =>
                                !_placeholderMemberDn.Equals(v, StringComparison.OrdinalIgnoreCase)).ToList();
                            if (filteredValues.Count > 0)
                                importObjectAttribute.ReferenceValues.AddRange(filteredValues);
                            else if (referenceValues.Count > filteredValues.Count)
                                _logger.Debug("LdapConnectorImport: Filtered placeholder member '{Placeholder}' from attribute '{Attr}' on '{Dn}'",
                                    LogSanitiser.Sanitise(_placeholderMemberDn), attributeName, LogSanitiser.Sanitise(searchResult.DistinguishedName));
                        }
                        break;
                    case AttributeDataType.NotSet:
                    default:
                        throw new ArgumentOutOfRangeException();
                }

                if (unreadableValues.Count > 0)
                {
                    // Report every value that is not imported, and leave the attribute off: what was read of a
                    // multi-valued attribute is only part of what the directory holds, and JIM leaves an attribute
                    // with a value it could not import as it already held it rather than reading it as the whole set
                    // (#2066). Two dates that differ only below the 100 nanoseconds JIM can read arrive here too, both
                    // reported rather than merged (#2153). The object is still returned, so
                    // every other attribute imports.
                    ReportUnreadableValues(importObject, searchResult.DistinguishedName, schemaAttribute, unreadableValues);
                    continue;
                }

                importObject.Attributes.Add(importObjectAttribute);
            }

            if (connectorDefinedAttributes.Count > 0 && importObject.ErrorType != ConnectedSystemImportObjectError.ConfigurationError)
                importObject.Attributes.AddRange(LdapExchangeAttributes.Derive(name => ReadDerivationSource(searchResult, name), connectorDefinedAttributes));

            // Synthesise distinguishedName for directories that don't return it as an attribute.
            // OpenLDAP (and most RFC-compliant directories) expose the DN as the entry's DistinguishedName
            // property, not as a searchable/importable attribute. The connector schema synthesises
            // distinguishedName as an attribute (for DN-based provisioning), so we need to populate it
            // from the entry's DN during import for export confirmation to match correctly.
            if (!importObject.Attributes.Any(a => a.Name.Equals("distinguishedName", StringComparison.OrdinalIgnoreCase))
                && objectType.Attributes.Any(a => a.Name.Equals("distinguishedName", StringComparison.OrdinalIgnoreCase) && a.Selected))
            {
                importObject.Attributes.Add(new ConnectedSystemImportObjectAttribute
                {
                    Name = "distinguishedName",
                    Type = AttributeDataType.Text,
                    StringValues = { searchResult.DistinguishedName }
                });
            }

            importObjects.Add(importObject);
        }

        LogUnreadableValueSummary(importObjects);
        return importObjects;
    }

    /// <summary>A directory attribute's values as text, for deriving a connector-defined attribute; empty when it has none.</summary>
    private static IReadOnlyList<string> ReadDerivationSource(SearchResultEntry entry, string attributeName) =>
        LdapConnectorUtilities.GetAttributeStringValues(entry.Attributes[attributeName], attributeName, entry.DistinguishedName) ?? [];

    /// <summary>
    /// Records the values of one attribute that are not imported on the object (one that could not be read as its type,
    /// #2128, or a date JIM cannot keep apart from another, #2153), which keeps JIM from changing the attribute, and gives the
    /// entry's distinguished name as where the object was read from: an administrator can go straight to it, where its
    /// External ID (an objectGUID or entryUUID) means nothing to them.
    /// </summary>
    private void ReportUnreadableValues(ConnectedSystemImportObject importObject, string distinguishedName, ConnectedSystemObjectTypeAttribute schemaAttribute, IReadOnlyList<LdapUnreadableValue> unreadableValues)
    {
        importObject.SourceLocation ??= distinguishedName;

        // The schema's name rather than the one on the entry, which the directory client lower-cases: it is the name
        // the administrator knows the attribute by.
        foreach (var unreadable in unreadableValues)
            importObject.AddAttributeValueError(schemaAttribute.Name, unreadable.Value, unreadable.Reason ?? UnreadableValueReasons.For(schemaAttribute.Type, unreadable.Value));

        // Per object at Debug only: a schema mapping gap makes this true of every object in the directory, so the
        // Warning is the one summary per batch below. Never the values themselves, which can be personal data; the
        // execution item records them.
        _logger.Debug("ConvertLdapResults: {Count} value(s) of {AttributeType} attribute '{AttributeName}' on '{Dn}' were not imported; JIM leaves the attribute as it already held it",
            unreadableValues.Count, schemaAttribute.Type, LogSanitiser.Sanitise(schemaAttribute.Name), LogSanitiser.Sanitise(distinguishedName));
    }

    /// <summary>
    /// Logs one Warning for a batch of converted entries that carried values JIM could not import, counting them per
    /// attribute, so a gap that touches every object is one line per batch rather than one per object.
    /// </summary>
    private void LogUnreadableValueSummary(List<ConnectedSystemImportObject> importObjects)
    {
        var flaggedObjects = importObjects.Where(importObject => importObject.AttributeValueErrors is { Count: > 0 }).ToList();
        if (flaggedObjects.Count == 0)
            return;

        var countsByAttribute = flaggedObjects
            .SelectMany(importObject => importObject.AttributeValueErrors!)
            .GroupBy(error => error.AttributeName, StringComparer.OrdinalIgnoreCase)
            .Select(group => $"{group.Key}: {group.Count()}");

        _logger.Warning("ConvertLdapResults: {ObjectCount} of {TotalCount} object(s) carried values JIM could not import (unreadable as their attribute's type, " +
            "or more than one for a Boolean or DateTime attribute), each reported on its execution item and its attribute left as JIM already held it. " +
            "Values per attribute: {UnreadableValueCounts}",
            flaggedObjects.Count, importObjects.Count, LogSanitiser.Sanitise(string.Join(", ", countsByAttribute)));
    }
    #endregion
}