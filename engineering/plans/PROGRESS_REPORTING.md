# Event-Based Progress Reporting

- **Status:** Planned
> **Milestone**: Post-MVP
> **Priority**: Medium
> **Effort**: Medium (3 phases)

## Overview

Replace polling-based progress reporting in JIM's Blazor UI with an event-driven model using PostgreSQL `LISTEN/NOTIFY` and SignalR. PowerShell and API consumers already have working progress reporting and require no changes.

**Key Change**: The Worker already writes live progress to the Activity table during execution. Rather than having every UI component poll the database on a timer, PostgreSQL notifies JIM.Web when progress changes, and SignalR pushes updates to connected Blazor components instantly.

## Current State (What's Already Working)

The original version of this plan predates significant progress reporting work that has since been completed. The following are **already implemented**:

### Worker Progress Updates
All processor types update `Activity.ObjectsToProcess`, `ObjectsProcessed`, and `Message` in real-time during execution via `UpdateActivityMessageAsync()` (single-row UPDATE, negligible load):
- `SyncImportTaskProcessor` — updates throughout import phases (importing, deletions, references, saving)
- `SyncFullSyncTaskProcessor` — sets totals at start, increments per-object and at page boundaries
- `SyncDeltaSyncTaskProcessor` — same pattern for modified CSOs only
- `SyncExportTaskProcessor` — uses `ExportProgressInfo` callback with phase tracking (`ExportPhase` enum)

### API Endpoints
- `GET /api/v1/activities/{id}` — returns `ObjectsToProcess`, `ObjectsProcessed`, `Message` during execution
- `GET /api/v1/activities/{id}/stats` — detailed RPEI-based execution statistics
- `GET /api/v1/schedule-executions/{id}` — step-level progress for schedule executions

### PowerShell Progress
- `Start-JIMRunProfile -Wait` — uses `Write-Progress` with determinate/indeterminate modes, polling every 2 seconds
- `Start-JIMSchedule -Wait` — uses `Write-Progress` with step-level progress, polling every 5 seconds
- Both cmdlets handle timeout, authentication refresh, and terminal status detection

### Blazor UI Polling
- `OperationsQueueTab` — 1-second `Task.Run` polling loop with change detection, `MudProgressLinear` bars
- `OperationsHistoryTab` — 5-second polling with fingerprint-based change detection
- `ExampleDataTemplateDetail` — 2-second `System.Threading.Timer` (tagged `POLLING_TO_REPLACE`)
- `Logs` — 5-second auto-refresh timer

## What's Outstanding

The single remaining gap is **replacing UI polling with event-based push updates**. All data, APIs, and external consumers (PowerShell) are already working.

## Technical Approach: PostgreSQL LISTEN/NOTIFY + SignalR

### Why PostgreSQL LISTEN/NOTIFY (Not Redis)

The original plan proposed Redis as an ephemeral progress store. After review, PostgreSQL `LISTEN/NOTIFY` is the better fit:

| Aspect | PostgreSQL LISTEN/NOTIFY | Redis Pub/Sub |
|--------|--------------------------|---------------|
| **Infrastructure** | Already deployed | New container, new dependency |
| **Air-gap compliance** | No change | Another image to distribute |
| **NuGet dependency** | None (Npgsql already included) | StackExchange.Redis (new) |
| **Latency** | Sub-millisecond on same host | Sub-millisecond on same host |
| **Durability** | Fire-and-forget | Fire-and-forget |
| **Operational complexity** | Zero | Memory tuning, monitoring, TTL config |

**Key reasons for this decision:**
- JIM is single-worker architecture — Redis scaling arguments don't apply
- Progress data is already written to PostgreSQL via `UpdateActivityMessageAsync()` — we just need to notify listeners that it changed
- The 8KB NOTIFY payload limit is irrelevant — we send only the activity ID; clients fetch fresh state
- No new Docker container, NuGet package, or environment variables needed
- Aligns with JIM's principle of maximising use of existing infrastructure before adding new components
- The original concern about "database load from progress writes" was addressed by the Phase 5 worker performance work (bulk RPEI inserts, change tracker clearing); the Activity UPDATEs are single-row and negligible

### Architecture

```
+-------------------+     +------------------+     +------------------+
|  JIM.Worker       |     |  PostgreSQL      |     |  JIM.Web         |
|                   |     |                  |     |                  |
| Processors update |---->| Activity table   |     | SignalR Hub      |
| Activity fields   |     |   |              |     |   ^              |
| via SaveChanges() |     |   v              |     |   |              |
|                   |     | TRIGGER fires    |     | IHostedService   |
|                   |     | pg_notify(       |---->| LISTEN on        |
|                   |     |  'activity_      |     | NpgsqlConnection |
|                   |     |   progress',     |     |   |              |
|                   |     |   activity_id)   |     |   v              |
|                   |     |                  |     | Blazor components|
|                   |     |                  |     | subscribe to hub |
+-------------------+     +------------------+     +------------------+
                                                          |
                                                   +------v-----------+
                                                   | PowerShell / API |
                                                   | (keep polling -  |
                                                   |  already works)  |
                                                   +------------------+
```

### Data Flow

```
1. Worker processes objects
   --> SaveChangesAsync() updates Activity row (ObjectsProcessed, Message, Status)

2. PostgreSQL AFTER UPDATE trigger on Activities table
   --> pg_notify('activity_progress', activity_id::text)
   --> Only fires when ObjectsProcessed, Message, or Status actually change

3. JIM.Web NotificationListenerService (IHostedService)
   --> Dedicated NpgsqlConnection with LISTEN activity_progress
   --> WaitAsync() loop receives notification
   --> Pushes to ActivityProgressHub via IHubContext

4. Blazor components subscribed to hub
   --> Receive activity ID
   --> Fetch fresh state via existing JimApplication methods
   --> Update UI (StateHasChanged)

5. PowerShell / external API consumers
   --> Continue polling as today (already working, idiomatic for CLI)
```

### Why PowerShell Keeps Polling

PowerShell is an external HTTP client — it cannot subscribe to SignalR or PostgreSQL notifications. The existing `Write-Progress` with 2-second polling is the idiomatic PowerShell pattern and provides a good user experience. The Worker updates the Activity at regular intervals regardless of the notification mechanism, so polling the Activity API gives accurate, timely progress.

If sub-second CLI updates are ever needed, a Server-Sent Events (SSE) endpoint (`GET /api/v1/activities/{id}/progress/stream`) could be added, backed by the same SignalR infrastructure. This is low priority given the current experience is already good.

## Implementation Phases

### Phase 1: PostgreSQL Trigger + Notification Listener Service

**PostgreSQL trigger** (EF Core migration):
- `AFTER UPDATE` trigger on `Activities` table
- Fires `pg_notify('activity_progress', NEW.id::text)` when `ObjectsProcessed`, `Message`, or `Status` columns change
- Use `WHEN (OLD.* IS DISTINCT FROM NEW.*)` or column-specific checks to avoid spurious notifications

**NotificationListenerService** (`IHostedService` in JIM.Web):
- Opens a dedicated `NpgsqlConnection` (not from the pool — required for LISTEN)
- Executes `LISTEN activity_progress`
- Loops on `WaitAsync()` with cancellation token support
- Reconnects on connection failure with exponential backoff
- Publishes received activity IDs to an in-process event (e.g., `IActivityProgressNotifier` interface)

**Testing:**
- Unit tests for reconnection logic (mocked connection)
- Integration test: UPDATE Activity row, verify notification received

### Phase 2: SignalR Hub + Blazor Component Migration

**SignalR hub** (`ActivityProgressHub`):
- `AddSignalR()` in `Program.cs`, `MapHub<ActivityProgressHub>("/hubs/activity-progress")`
- Hub method: clients join/leave groups by activity ID or "all activities"
- `NotificationListenerService` uses `IHubContext<ActivityProgressHub>` to push notifications

**Blazor component migration:**
- Create a shared `ActivityProgressSubscription` service/component that wraps SignalR subscription
- Migrate `OperationsQueueTab` — replace 1-second polling loop with hub subscription
- Migrate `OperationsHistoryTab` — replace 5-second polling loop with hub subscription
- Migrate `ExampleDataTemplateDetail` — replace timer (remove `POLLING_TO_REPLACE` tag)
- Migrate `Logs` page — replace auto-refresh timer (if applicable)
- Keep polling as fallback: if SignalR connection drops, revert to timer-based polling until reconnected

**Testing:**
- Unit tests for hub group management
- UI smoke testing for each migrated page

### Phase 3: Cleanup + Documentation

- Remove polling code from migrated components (or retain as documented fallback)
- Update `DEVELOPER_GUIDE.md` with notification architecture
- Update `CHANGELOG.md`
- Document the trigger in migration notes
- Add connection string guidance for the dedicated LISTEN connection if needed

## Design Considerations

### Dedicated Connection for LISTEN
Npgsql requires a non-pooled connection for `LISTEN`/`WaitAsync()`. The `NotificationListenerService` should open its own connection using the same connection string but with `Pooling=false` or a separate `NpgsqlDataSource`. This connection stays open for the lifetime of the service.

### Notification Deduplication
Multiple rapid Activity UPDATEs (e.g., during fast batch processing) may generate many notifications. The listener should debounce — collect notifications over a short window (~200ms) and push unique activity IDs to SignalR. This prevents UI thrashing during high-throughput processing.

### Blazor Server Circuit
Since JIM uses Blazor Server, the SignalR circuit for Blazor is already established. The `ActivityProgressHub` can be a separate hub on the same connection, or notifications can be pushed through the Blazor circuit using `IJSRuntime` or a scoped service. The separate hub approach is cleaner and allows non-Blazor consumers in the future.

### Graceful Degradation
If the LISTEN connection drops or the trigger is missing (e.g., database restored from backup without the trigger), the system should fall back to polling. This means the polling infrastructure should be retained but disabled by default when SignalR is connected.

## Success Criteria

- Blazor UI updates within ~500ms of Worker progress changes (vs current 1-5 second polling delay)
- Zero polling timers in migrated Blazor components during normal operation
- Polling fallback activates automatically on SignalR/LISTEN disconnection
- No new infrastructure dependencies (no Redis, no new Docker containers)
- PowerShell `Write-Progress` continues to work unchanged
- All existing API endpoints continue to work unchanged

## Risks & Mitigations

**Risk**: Dedicated LISTEN connection drops silently
**Mitigation**: `NotificationListenerService` implements health checks and automatic reconnection with exponential backoff. Falls back to polling if reconnection fails.

**Risk**: High-frequency notifications during large imports overwhelm SignalR
**Mitigation**: Debounce notifications (~200ms window). Send only activity IDs, not full payloads — clients fetch state on demand.

**Risk**: PostgreSQL connection limit pressure from dedicated LISTEN connection
**Mitigation**: Single additional connection. JIM's single-worker architecture means at most 1 extra connection.

## Dependencies

- Npgsql (already included via `Npgsql.EntityFrameworkCore.PostgreSQL 9.0.4`)
- ASP.NET Core SignalR (already included in the framework; log config already in `appsettings.json`)
- No new NuGet packages required
- No new Docker containers required
- No database schema changes beyond the trigger migration

## Notes

- Post-MVP feature (not required for initial release)
- Complements existing Stats system (doesn't replace it)
- Single worker architecture assumption (no horizontal scaling planned)
- Air-gapped compatible (no external dependencies)
- The original Redis-based plan is superseded by this approach
