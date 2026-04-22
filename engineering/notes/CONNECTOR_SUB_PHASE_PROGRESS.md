# Connector Sub-Phase Progress

> Design note for a standardised sub-phase progress capability across all connector interaction interfaces. Captured ahead of implementation so future PRs have a shared reference.

## Status: Design (2026-04-22)

Feature is designed but not yet implemented. Tracked by [#637](https://github.com/TetronIO/JIM/issues/637).

## Background

Issue #637 originated during the investigation of [#633](https://github.com/TetronIO/JIM/issues/633) (file-based export throughput). A user reported that a 100,000-object Cross-Domain export appeared stuck at "Exporting 100000 changes to file — 0 of 100,000" for 20+ minutes. The AsNoTracking fix in dcb7cf78 resolved the throughput problem, but the underlying observability gap remained: the progress counter only advances **after** the connector returns, so during long-running internal phases (file load, in-memory merge, file write) the UI shows "0 of N" with no signal the system is working.

At 100,000 objects this is no longer a user concern because the phases now complete in seconds. But the same gap exists across every connector's long-running internal operations and will re-emerge at larger scales.

Discussion on #637 established that the right fix is not narrow (plumb a callback into `FileConnectorExport`) but cross-cutting: **every connector should be able to narrate its internal progress to JIM**, and the mechanism should be symmetric across imports, exports, files, and calls.

## Problem

Connectors own long-running, multi-step operations that are opaque to the orchestrator. Today:

- `IConnectorExportUsingFiles.ExportAsync` receives a batch of pending exports and returns when done. The File connector's internal Load → Merge → Write phases take real wall-clock time at scale, but the server has no way to surface them.
- `IConnectorImportUsingFiles.ImportAsync` reads an entire file in a single call. The worker sees one page at the end, with no visibility into read/parse progress.
- `IConnectorImportUsingCalls.ImportAsync` reports per-page (one page per call), but within a page the worker has no visibility into connection setup, root DSE queries, container enumeration, or internal parallel fan-out.
- `IConnectorExportUsingCalls.ExportAsync` naturally iterates per-item and already benefits from per-item progress at the server level, but has no way to emit pre-flight sub-phases ("Creating parent containers…", "Resolving references…").

The result: operators see a frozen activity message during long connector operations and cannot distinguish a healthy long-running operation from a stuck one.

## Design

### Principles

1. **Connector-defined vocabulary, not prescribed.** Sub-phases are free-form strings. A CSV file connector narrates "Merging…"; a future SFTP-with-compression connector narrates "Compressing output…". JIM does not need to know.
2. **Orchestration-level phases stay fixed.** The existing `ExportPhase` enum (`Preparing` / `Executing` / `ResolvingReferences` / `Completed`) is JIM's vocabulary for the orchestrator's view. It does not change. Sub-phase detail rides in the `Message` field.
3. **Symmetric across the four interaction interfaces.** One pattern applies to both directions and both mechanisms. No separate design for imports vs exports, or files vs calls.
4. **Connector stays ignorant of JIM orchestration state.** The connector does not set `ProcessedExports` / `TotalExports` / `ExportPhase`. Those are the server/worker's responsibility. The connector narrates what it is doing.
5. **Optional, backwards-compatible.** Callback parameter is nullable with a default of `null`. Existing connector implementations and tests compile unchanged.
6. **Throttling is the caller's problem.** The callback passed in is already either throttled or cheap. The connector emits on phase transitions; it does not emit per-item unless it naturally iterates, in which case existing per-item patterns apply.

### Interface changes

All four interaction interfaces gain an optional progress callback. The signature is `Func<string, Task>? progressCallback = null` — a plain async string callback.

```csharp
// IConnectorImportUsingCalls.cs
public Task<ConnectedSystemImportResult> ImportAsync(
    ConnectedSystem connectedSystem,
    ConnectedSystemRunProfile runProfile,
    List<ConnectedSystemPaginationToken> paginationTokens,
    string? persistedConnectorData,
    ILogger logger,
    CancellationToken cancellationToken,
    Func<string, Task>? progressCallback = null);  // new

// IConnectorImportUsingFiles.cs
public Task<ConnectedSystemImportResult> ImportAsync(
    ConnectedSystem connectedSystem,
    ConnectedSystemRunProfile runProfile,
    ILogger logger,
    CancellationToken cancellationToken,
    Func<string, Task>? progressCallback = null);  // new

// IConnectorExportUsingCalls.cs
public Task<List<ConnectedSystemExportResult>> ExportAsync(
    IList<PendingExport> pendingExports,
    CancellationToken cancellationToken,
    Func<string, Task>? progressCallback = null);  // new

// IConnectorExportUsingFiles.cs
public Task<List<ConnectedSystemExportResult>> ExportAsync(
    IList<ConnectedSystemSettingValue> settings,
    IList<PendingExport> pendingExports,
    CancellationToken cancellationToken,
    Func<string, Task>? progressCallback = null);  // new
```

#### Why `Func<string, Task>?` and not something else

- **`IProgress<string>`** is .NET idiomatic but fire-and-forget. JIM's activity-message pipeline is async (EF round-trip), and the export path already uses `Func<ExportProgressInfo, Task>`. Consistency wins.
- **Passing a structured `ExportProgressInfo`** (or a new `ImportProgressInfo`) would couple connectors to JIM's orchestration state — fields they cannot meaningfully populate. It would also require a second type for imports. Simpler to have the server fill the struct from its own state and let the connector provide only the human-readable message.
- **A named delegate `ConnectorProgressReporter`** has low added value; `Func<string, Task>?` matches the existing `Func<ExportProgressInfo, Task>?` pattern used throughout `ExportExecutionServer`.

### Server/worker integration

#### Export side (`ExportExecutionServer`)

The server wraps the plain-string callback into an `ExportProgressInfo` that preserves counts and phase:

```csharp
// Inside ExecuteUsingFilesWithBatchingAsync (and wherever a connector export call happens)
Func<string, Task>? connectorProgress = progressCallback == null ? null : async subPhase =>
{
    await ReportProgressAsync(progressCallback, new ExportProgressInfo
    {
        Phase = ExportPhase.Executing,
        TotalExports = result.TotalPendingExports,
        ProcessedExports = result.SuccessCount + result.FailedCount,
        Message = subPhase
    });
};

var exportResults = await connector.ExportAsync(..., connectorProgress);
```

The server owns `Phase`, `TotalExports`, and `ProcessedExports`. Only `Message` is supplied by the connector.

#### Import side (`SyncImportTaskProcessor`)

The worker passes a lambda that updates the activity message directly, re-using the same pathway the page loop already uses:

```csharp
Func<string, Task> connectorProgress = async subPhase =>
    await _syncRepo.UpdateActivityMessageAsync(_activity, subPhase);

result = await callBasedImportConnector.ImportAsync(
    _connectedSystem, _connectedSystemRunProfile, paginationTokens,
    originalPersistedData, Log.Logger, _cancellationTokenSource.Token,
    connectorProgress);
```

No new `ImportProgressInfo` type is introduced. Imports remain worker-driven at the page level; sub-phase callbacks write directly to the activity message.

### Relationship to existing per-item progress

Sub-phase progress and per-item progress coexist cleanly:

- **Per-item progress** (existing): emitted from the server/worker once per batch or per N items, with accurate `ProcessedExports` counts. Appropriate when the connector iterates items.
- **Sub-phase progress** (new): emitted from the connector at phase boundaries, with counts unchanged. Appropriate for unitary operations (bulk load, bulk write, connection setup, schema query) where "X of N" is meaningless.

The UI (activity message) is shared. Each emit replaces the previous message. A connector that has both (e.g. a hypothetical batched file export) can interleave:

```
Loading existing export file...
Merging 50,000 / 100,000
Merging 100,000 / 100,000
Writing 100,000 rows to output file...
```

## Per-Connector Sub-Phase Catalogue

The phrases below are examples showing each connector maintainer's vocabulary. They set expectations and seed the initial implementations. Connector authors are free to refine them.

### FileConnector

**ExportAsync** (the issue's original motivation):
1. `"Loading existing export file..."` — before `LoadExistingFileContent`
2. `"Merging {N:N0} changes into file..."` — before the per-item merge loop
3. `"Writing {N:N0} rows to output file..."` — before `WriteFullStateFile`

**ImportAsync**:
1. `"Reading CSV file..."`
2. `"Parsing {N:N0} rows..."`

### LdapConnector

**ImportAsync (full import)**:
1. `"Querying root DSE..."` — before `GetRootDseInformation`
2. `"Enumerating containers in {partition}..."` — before container loop
3. `"Fetching {objectType} from {container} (page {N})..."` — per page

**ImportAsync (delta import)**:
1. `"Querying root DSE..."`
2. `"Querying changes since USN {N}..."` (AD USN path) or `"Querying changes since {timestamp}..."` (accesslog path) or `"Querying changelog since {changeNumber}..."` (generic path)
3. `"Querying deleted objects in {partition}..."` (AD tombstones)

**ExportAsync**: naturally per-item, already covered by the server's per-item progress. Optional pre-flight sub-phases:
- `"Creating parent containers..."` — when Create-Containers-As-Needed is enabled
- `"Resolving references..."` — if applicable

LDAP export is lower priority because per-item progress already addresses the visible gap there.

## Non-Goals

- **Not** adding new values to `ExportPhase` or any other JIM-level enum.
- **Not** changing the existing per-item or batch-level progress already working in `ExecuteExportsViaConnectorAsync` / `ProcessBatchAsync`.
- **Not** introducing a new `ImportProgressInfo` type. Imports remain worker-driven at the page level.
- **Not** adding progress to non-interaction interfaces (`IConnectorSchema.GetSchemaAsync`, `IConnectorPartitions.GetPartitionsAsync`, etc.). If a future schema import proves slow, the same pattern can be applied there.

## Implementation Phases

Each phase is suggested to be delivered as a separate PR so the change lands incrementally and can be reviewed in context.

1. **Interface changes + server/worker wiring** (no behavioural change yet). Adds the optional callback parameter to all four interfaces and wires the server/worker to pass a lambda. Callback is null from all callers initially; existing tests pass unchanged.
2. **FileConnector export sub-phases** (closes the original #637 observability gap). Emits Load / Merge / Write. Tests: verify the three expected messages fire in order in a non-empty export scenario.
3. **FileConnector import sub-phases**.
4. **LdapConnector import sub-phases** (full + delta paths).
5. **LdapConnector export sub-phases** (optional; lower priority because per-item progress already covers the gap).

## Testing Strategy

- **Unit tests per connector**: verify expected sub-phase messages are emitted in the expected order when a progress callback is supplied. Use a `List<string>`-capturing callback.
- **Callback optionality**: test both `null` and a non-null callback path.
- **Integration coverage**: at 100k+ scale, verify the activity message advances through expected sub-phases. Timestamps in the activity log confirm progression.

## Backwards Compatibility

- New parameter is optional with default `null`. No call-site changes required for existing callers.
- Existing `Func<ExportProgressInfo, Task>?` callbacks throughout `ExportExecutionServer` are untouched. The new `Func<string, Task>?` is a distinct, narrower callback handed specifically to connectors.
- Connectors authored against the current interface continue to compile once the parameter is added with a default value; no existing connector needs to change before the new facility is useful.

## Open Questions

1. **Throttling expectations**: should we document a minimum emit interval (e.g. "no more than one emit per 500 ms")? Or leave it to connector authors on the basis that sub-phase emits are naturally sparse? Current lean: leave it to authors; revisit if we see a connector that emits too frequently in practice.
2. **LdapConnector import sub-phase granularity**: LDAP import has substantially different shapes per directory type (AD USN vs OpenLDAP accesslog vs generic changelog). Should each path have its own documented sub-phase sequence before implementation starts, or is it enough to follow the catalogue above as a starting point?
