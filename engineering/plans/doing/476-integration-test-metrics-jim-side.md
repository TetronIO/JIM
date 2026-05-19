# Plan: Integration Test Metrics - JIM Repo Scope (#476)

- **Status:** Doing (JIM-side phases 1–4 merged; end-to-end validation pending)
- **Issue:** [#476](https://github.com/TetronIO/JIM/issues/476)
- **Note:** Implementation is complete on the JIM side, but no devcontainer has yet been verified to put data on the JIM-Bench dashboard. Move to `done/` once a run from a devcontainer with `JIM_BENCH_API_KEY` set is confirmed to appear on https://bench.junctional.io. Outstanding work on the metrics platform itself (Grafana trends/regression dashboards, cross-repo dispatch receiver, devcontainer docs) is tracked in `TetronIO/JIM-Bench`.

## Design Decisions (from planning discussion)

1. **Span enrichment** (Phase 1) adds `cumulativeObjectCount` and `wallClockOffsetMs` tags to existing spans. These enrich the local performance tree at Debug level and flow to the API when streamed.
2. **MetricsCheckpoints** (Phase 1b) are lightweight structured log lines emitted at `Information` level, guaranteeing throughput profile data regardless of log level. Emitted every ~1000 objects or at natural phase boundaries.
3. **Log streaming** (Phase 3) uses a background PowerShell job that follows the worker container's stdout via `docker logs -f` during the test run. Streams DiagnosticListener lines + MetricsCheckpoint lines to the Metrics API in batches. The devcontainer never holds or processes the full log volume. (Note: initial implementation tailed the Serilog file sink via a bind mount, but the file sink uses `RenderedCompactJsonFormatter` (CLEF JSON) while the bench-side parser expects the same plaintext format the runner's Step 6 metrics extraction already parses. Switched to `docker logs -f` so both local Step 6 and bench streaming consume identical plaintext output.)
4. **Architectural separation**: all reporting/submission logic lives in `test/integration/`. Product code changes are limited to span tags + MetricsCheckpoint log lines. No Serilog sink, no product dependency on the Metrics API.
5. **Two-tier data model**: Tier 1 (always available at any log level) = MetricsCheckpoints + runner wall-clock + pass/fail. Tier 2 (Debug level) = full DiagnosticListener span tree. The API and Grafana handle both tiers.

## Data Flow

```
+---------------------------+    docker logs -f       +-------------------+
| jim.worker container      | --------------------->  | Stream-WorkerLogs |
| (Serilog console sink,    |    (stdout/stderr,      | .ps1 background   |
|  plaintext output)        |     plaintext)          | job               |
+---------------------------+                         +--------+----------+
                                                               |
                                            filters DiagnosticListener + MetricsCheckpoint
                                            enriches with run context (runId, scenario, template, host)
                                            batches every 200 lines or 5 seconds
                                                               |
                                                               v
+---------------------------+       HTTPS POST        +-------------------+
| Submit-TestResults.ps1    | ---------------------> | Metrics API (VPS) |
| (end-of-run summary)      |                         +--------+----------+
+---------------------------+                                  |
                                                               v
+---------------------------+                         +-------------------+
| Get-HostFingerprint.ps1   | --- included in ------> | PostgreSQL        |
+---------------------------+    submission payload   +--------+----------+
                                                               |
                                                               v
                                                      +-------------------+
                                                      | Grafana           |
                                                      +-------------------+
```

## Phase 1: Span Enrichment + MetricsCheckpoints

### 1a. Span Tag Enrichment (~10 lines across 3 files)

Tags automatically flow through Activity.Tags -> DiagnosticListener -> Serilog -> log file -> stream to API. No parsing changes needed.

**SyncFullSyncTaskProcessor.cs** - `ProcessCsoLoop` span (line ~143):
- Already has: `csoCount`
- Add: `cumulativeObjectCount` = `_activity.ObjectsProcessed` (cumulative across all pages)
- Add: `wallClockOffsetMs` = elapsed ms since CSO processing phase started (add a Stopwatch at the start of the `ProcessConnectedSystemObjects` span)

**SyncImportTaskProcessor.cs** - `ImportPage` span (line ~236):
- Already has: `pageNumber`
- Add: `cumulativeObjectCount` = `totalObjectsImported` (already tracked, incremented per page at line ~240)
- Add: `wallClockOffsetMs` = elapsed ms from `importPhaseSw` stopwatch (already exists at line ~178)

**ExportExecutionServer.cs** - `ExportBatch` span (line ~464):
- Already has: `batchSize`
- Add: `cumulativeObjectCount` = `processedCount + immediateExports.Count` (processedCount is incremented at line ~478)
- Add: `wallClockOffsetMs` = elapsed ms from a Stopwatch started before the batch loop begins

### 1b. MetricsCheckpoint Log Lines (~15 lines across 3 files)

Structured log lines emitted at `Information` level. These survive any log level setting and provide guaranteed throughput data for the Metrics API.

Format:
```
MetricsCheckpoint: {Operation} processed={CumulativeCount} elapsed={ElapsedMs}ms total={TotalExpected}
```

**SyncFullSyncTaskProcessor.cs** - inside `ProcessCsoLoop`, after page processing:
```
MetricsCheckpoint: FullSync processed=5000 elapsed=12345ms total=10000
```
- Emit after each page completes (page boundaries are natural checkpoints)

**SyncImportTaskProcessor.cs** - inside the import page loop, after each page:
```
MetricsCheckpoint: Import processed=3000 elapsed=8765ms total=10000
```
- Emit after each connector page is processed

**ExportExecutionServer.cs** - after each batch completes (after `processedCount` is incremented):
```
MetricsCheckpoint: Export processed=500 elapsed=4321ms total=2000
```
- Emit after each batch (batch size = 100, so every 100 exports)

**Implementation detail**: Use `_logger.LogInformation(...)` with the exact prefix `MetricsCheckpoint:` so the streaming script can filter for it alongside `DiagnosticListener:` lines. Include the connected system name for context.

### Files changed (Phase 1)

| File | Change | Lines |
|------|--------|-------|
| `src/JIM.Worker/Processors/SyncFullSyncTaskProcessor.cs` | Add span tags + checkpoint | ~5 |
| `src/JIM.Worker/Processors/SyncImportTaskProcessor.cs` | Add span tags + checkpoint | ~5 |
| `src/JIM.Application/Servers/ExportExecutionServer.cs` | Add span tags + checkpoint | ~8 |

**Build + test required**: Yes (product code changes)

## Phase 2: Host Fingerprinting

**New file**: `test/integration/Get-HostFingerprint.ps1`

Captures hardware profile and derives a `host_class` label for cross-host comparison grouping in Grafana.

**Data captured:**
- CPU model string
- CPU core count
- RAM in GB
- Disk type: SSD or HDD
- Disk size in GB
- Free disk space in GB
- Swap size in GB (0 if no swap configured)
- Swap free in GB (0 if no swap configured)
- RAM free in GB
- CPU utilisation % at capture time (baseline; if not near idle, the run may not be a fair comparison)
- GitHub username (from `gh api user --jq .login`, null if `gh` CLI not available or not authenticated)

**Host class derivation:** `"{cores}c-{ramGb}g-{diskType}"` (e.g. `4c-8g-ssd`, `8c-16g-ssd`)

**Cross-platform detection (PowerShell):**

| Data | Linux (`$IsLinux`) | macOS (`$IsMacOS`) | Windows (else) |
|------|------|-------|---------|
| CPU model | `/proc/cpuinfo` | `sysctl -n machdep.cpu.brand_string` | `Get-CimInstance Win32_Processor` |
| Core count | `nproc` | `sysctl -n hw.ncpu` | `$env:NUMBER_OF_PROCESSORS` |
| RAM GB | `/proc/meminfo` (MemTotal) | `sysctl -n hw.memsize` | `Get-CimInstance Win32_ComputerSystem` |
| Disk type | `lsblk -d -o NAME,ROTA` (ROTA=0 means SSD) | `system_profiler SPStorageDataType` | `Get-PhysicalDisk` |
| Disk size GB | `df -BG /` | `df -g /` | `Get-CimInstance Win32_LogicalDisk` |
| Disk free GB | `df -BG /` | `df -g /` | `Get-CimInstance Win32_LogicalDisk` |
| Swap size GB | `/proc/meminfo` (SwapTotal) | `sysctl -n vm.swapusage` | `Get-CimInstance Win32_PageFileUsage` |
| Swap free GB | `/proc/meminfo` (SwapFree) | `sysctl -n vm.swapusage` | `Get-CimInstance Win32_PageFileUsage` |
| RAM free GB | `/proc/meminfo` (MemAvailable) | `sysctl -n vm.page_free_count` * page size | `Get-CimInstance Win32_OperatingSystem` |
| CPU util % | `top -bn2 -d0.5` (second sample) | `top -l2 -s1` (second sample) | `Get-CimInstance Win32_Processor` |
| GitHub user | `gh api user --jq .login` | `gh api user --jq .login` | `gh api user --jq .login` |

**No caching.** The script runs at the start of every integration test run. It completes in under a second (reading `/proc/`, `df`, and one `gh api` call) so there's no reason to cache and risk stale resource availability data.

**Output (JSON):**
```json
{
  "hostname": "codespaces-abc123",
  "cpuModel": "AMD EPYC 7763",
  "cores": 4,
  "ramGb": 8,
  "diskType": "ssd",
  "diskSizeGb": 128,
  "diskFreeGb": 76,
  "swapSizeGb": 4,
  "swapFreeGb": 4,
  "ramFreeGb": 5.2,
  "cpuUtilisationPct": 3.2,
  "githubUsername": "jayv",
  "hostClass": "4c-8g-ssd",
  "capturedAt": "2026-04-09T12:00:00Z"
}
```

**Build + test required**: No (script only)

## Phase 3: Log Streaming + Submission

### 3a. Docker Compose Bind Mount

Add a bind mount so the worker log file is readable from the host filesystem.

**File**: `docker-compose.override.yml` (dev/devcontainer only; never touches `docker-compose.yml` or `docker-compose.production.yml`)

Add to `jim.worker` service volumes (line ~29):
```yaml
volumes:
  - ./test/integration/results/logs/worker:/var/log/jim    # Expose worker logs for metrics streaming
```

This is the correct file because:
- `docker-compose.override.yml` is already dev-only (contains Keycloak, debug ports, test-data mounts)
- The integration test runner already uses `-f docker-compose.yml -f docker-compose.override.yml`
- `docker-compose.production.yml` is the customer-facing override and is not touched
- `docker-compose.yml` (base) is shared by all environments and is not touched

### 3b. Log Streaming Script

**New file**: `test/integration/Stream-WorkerLogs.ps1`

A script that runs as a background job during the test, tailing the worker log file and streaming filtered lines to the Metrics API.

**Parameters:**
- `-LogFilePath` - path to the worker log file (from bind mount)
- `-ApiUrl` - Metrics API base URL (from `$env:JIM_BENCH_API_URL`)
- `-ApiKey` - API key (from `$env:JIM_BENCH_API_KEY`)
- `-RunId` - unique run identifier (generated by the test runner)
- `-Scenario` - scenario name
- `-Template` - template size
- `-HostClass` - from host fingerprint

**Behaviour:**
- `Get-Content -Wait -Tail 0` on the log file (lightweight file tail)
- Filters lines matching `DiagnosticListener:` or `MetricsCheckpoint:`
- Buffers into batches of 200 lines or 5-second flush interval (whichever comes first)
- Each batch POSTed to `{ApiUrl}/api/v1/runs/{RunId}/logs` with headers:
  - `X-API-Key: {ApiKey}`
  - `Content-Type: application/json`
- Payload per batch:
  ```json
  {
    "runId": "uuid",
    "scenario": "Scenario1-HRToIdentityDirectory",
    "template": "Scale100k50Groups",
    "hostClass": "4c-8g-ssd",
    "lines": [
      "DiagnosticListener: FullSync > ProcessCsoLoop completed in 234.5ms [csoCount=50, cumulativeObjectCount=5000, wallClockOffsetMs=12345]",
      "MetricsCheckpoint: FullSync processed=5000 elapsed=12345ms total=10000"
    ]
  }
  ```
- Graceful failure: if POST fails, log warning, buffer the lines for next attempt (up to a cap), never crash
- Exits cleanly when the log file stops growing and a stop signal is received

### 3c. Submission Script (End-of-Run Summary)

**New file**: `test/integration/Submit-TestResults.ps1`

Called at the end of the test run to submit the final summary and signal run completion.

**Parameters:**
- `-RunId` - same run ID used by the streaming job
- `-ResultFile` - path to the performance result JSON (existing format)
- `-HostFingerprintFile` - path to host fingerprint JSON
- `-ApiUrl` / `-ApiKey` - Metrics API credentials

**Payload:**
```json
{
  "runId": "uuid",
  "scenario": "Scenario1-HRToIdentityDirectory",
  "template": "Scale100k50Groups",
  "step": "All",
  "directoryType": "OpenLDAP",
  "hostFingerprint": { "hostname": "...", "hostClass": "4c-8g-ssd", ... },
  "success": true,
  "exitCode": 0,
  "testDurationMs": 267113.5,
  "wallClockTimings": { ... },
  "completedAt": "2026-04-09T14:30:00Z"
}
```

**Behaviour:**
- Reads existing result JSON and host fingerprint
- POSTs to `{ApiUrl}/api/v1/runs/{RunId}/complete`
- Prints Grafana dashboard URL for this run: `{GrafanaUrl}/d/run-detail?var-runId={RunId}`
- Graceful failure: warn, don't fail the test run

### 3d. Interactive Menu: Metrics Streaming Prompt

The integration test runner's interactive menu should include a step before execution asking whether metrics streaming is enabled. This reminds the user to set the env vars and makes the feature discoverable.

**Prompt (shown after scenario/template/step selection, before execution begins):**
```
Metrics streaming: JIM_BENCH_API_URL and JIM_BENCH_API_KEY are [SET / NOT SET]

  Metrics streaming is [ENABLED / DISABLED] for this run.
  Results will [be streamed to {ApiUrl} / only be saved locally].

  Continue? [Y/n]:
```

- Default: `Y` (press Enter to continue)
- If env vars are not set, the message is informational only (not a blocker); the test proceeds without streaming
- If env vars are set, confirms the target URL so the user can verify it's correct
- Skipped in non-interactive mode (when `-NonInteractive` or equivalent flag is used, e.g. CI/automated runs)

### 3e. Integration into Run-IntegrationTests.ps1

Modifications to the main test runner:

**Before Step 5 (Run Tests):**
```powershell
$runId = [Guid]::NewGuid().ToString()
$hostFingerprint = & "$PSScriptRoot/Get-HostFingerprint.ps1"

if ($env:JIM_BENCH_API_URL -and $env:JIM_BENCH_API_KEY) {
    $streamJob = Start-Job -FilePath "$PSScriptRoot/Stream-WorkerLogs.ps1" -ArgumentList @(
        $workerLogPath, $env:JIM_BENCH_API_URL, $env:JIM_BENCH_API_KEY,
        $runId, $Scenario, $Template, $hostFingerprint.hostClass
    )
}
```

**After Step 6 (Capture Metrics) - existing local comparison and display logic unchanged:**
```powershell
if ($env:JIM_BENCH_API_URL -and $env:JIM_BENCH_API_KEY) {
    Stop-Job $streamJob; Remove-Job $streamJob
    & "$PSScriptRoot/Submit-TestResults.ps1" `
        -RunId $runId -ResultFile $metricsFilePath `
        -HostFingerprintFile $hostFingerprintPath `
        -ApiUrl $env:JIM_BENCH_API_URL -ApiKey $env:JIM_BENCH_API_KEY
}
```

**Key point**: The existing local metrics capture and comparison logic is **unchanged**. Small templates still get the local performance tree at Debug level. The streaming + submission is purely additive, gated on the env vars being set.

**Build + test required**: No (scripts only). But needs manual testing with the integration test runner.

## Phase 4: Cross-Repo Sync Workflow

**New file**: `.github/workflows/bench-sync.yml`

```yaml
name: JIM-Bench Schema Sync
on:
  push:
    branches: [main]
    paths:
      - 'src/JIM.Worker/Processors/Sync*Processor.cs'
      - 'src/JIM.Application/Servers/ExportExecutionServer.cs'
      - 'test/integration/Submit-TestResults.ps1'
      - 'test/integration/Get-HostFingerprint.ps1'
      - 'test/integration/Stream-WorkerLogs.ps1'
```

**Action**: Sends `repository_dispatch` to `TetronIO/JIM-Bench` with:
- Changed files list
- Commit SHA and message
- Diff summary of what changed in span tags / payload structure

**Auth**: `JIM_BENCH_DISPATCH_TOKEN` secret (GitHub PAT with `repo` scope on `TetronIO/JIM-Bench`)

**Build + test required**: No (workflow file only)

## Implementation Order

1. **Phase 1** - Span enrichment + MetricsCheckpoints (product code, needs build + test)
2. **Phase 2** - Host fingerprinting script (standalone, no build)
3. **Phase 3** - Bind mount + streaming + submission scripts + runner integration (scripts, no build, needs manual integration test)
4. **Phase 4** - GitHub Actions workflow (last, depends on JIM-Bench repo existing)

## What This Delivers for the JIM-Bench Agent Prompt

After this plan is implemented, the Metrics API contract is fully defined:

1. **Streaming endpoint**: `POST /api/v1/runs/{runId}/logs` - receives batches of raw log lines with run context
2. **Completion endpoint**: `POST /api/v1/runs/{runId}/complete` - receives final summary with host fingerprint, pass/fail, wall-clock timing
3. **Log line formats**: `DiagnosticListener:` (spans) and `MetricsCheckpoint:` (structured progress) - both have documented formats
4. **Auth**: `X-API-Key` header
5. **Host fingerprint schema**: defined in Phase 2

The API is responsible for parsing the log lines server-side (the regex already exists in the test runner and can be ported to C#), storing structured data in PostgreSQL, and making it queryable by Grafana.

## Changelog

Add under `## [Unreleased]` in `CHANGELOG.md`:

### Performance
- ⚡ Enriched diagnostic spans with cumulative object count and wall-clock offset for throughput profiling (#476)
- ⚡ Added MetricsCheckpoint log lines for guaranteed throughput tracking at any log level (#476)

### Added
- ✨ Automated integration test metrics submission to central tracking system with Grafana dashboards (#476)
- ✨ Host fingerprinting for fair cross-environment performance comparison (#476)
