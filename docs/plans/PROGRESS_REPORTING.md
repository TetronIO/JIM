# Run Profile Progress Reporting (Post-MVP)

> **Status**: Planned
> **Milestone**: Post-MVP
> **Priority**: Medium
> **Effort**: Large (8+ phases)

## Overview

Implement comprehensive real-time progress reporting for all JIM run profile execution types (Import, Export, Sync) using Redis for zero database load.

**Key Features**:
- **Live Progress**: Real-time visibility into executing run profiles (currently blind until completion)
- **MIM-Style Display**: Phase + object counts + operation breakdown (Creates/Updates/Deletes)
- **Zero DB Load**: Redis-based tracking eliminates EF thread-safety performance issues
- **PowerShell Integration**: Live progress in `Get-JIMActivity -Follow` and enhanced `Start-JIMRunProfile -Wait`
- **Dual API Endpoints**: Lightweight polling endpoint + enhanced activity details

## Business Value

**Current Pain Points**:
- No visibility into long-running imports/syncs until completion
- Users cannot monitor progress or estimate completion time
- No way to detect issues early (e.g., high error rates during execution)
- Database performance degradation from frequent progress updates

**Solution Benefits**:
- Real-time monitoring of run profile execution
- Early detection of problems (error rates, performance issues)
- Better user experience (progress bars, ETAs, phase information)
- Improved system performance (Redis eliminates database contention)
- Foundation for future features (WebSocket notifications, caching infrastructure)

## Technical Architecture

### Current State

- `ActivityRunProfileExecutionStats`: Final summary calculated **after** completion by counting `ActivityRunProfileExecutionItems` in PostgreSQL
- Export has basic `ExportProgressInfo` with callback-based reporting
- Import/Sync have no structured progress tracking

### Proposed Solution

**Redis-Based Progress Tracking**:
- Worker writes progress to Redis during execution (ephemeral, 1-hour TTL)
- API reads from Redis for real-time queries (microsecond latency)
- Stats remain unchanged (permanent database records for historical analysis)
- **No conflicts**: Progress is transient (Redis), Stats are permanent (PostgreSQL)

### Data Flow

```
1. User executes run profile
   └─> Activity created with Status=InProgress

2. Worker processes objects (DURING EXECUTION)
   ├─> Updates Redis progress every N objects (fast, no DB hit)
   │   └─> Clients poll GET /activities/{id}/progress (read from Redis)
   └─> Writes ActivityRunProfileExecutionItem to database (detailed record)

3. Worker completes
   ├─> Activity.Status = Complete
   ├─> Final progress update to Redis
   └─> Redis auto-expires progress after 1 hour (TTL)

4. User views completed activity
   ├─> GET /activities/{id} returns Activity + Stats (from database)
   └─> GET /activities/{id}/progress returns 404 (Redis expired)
```

### Stats vs Progress Comparison

| Aspect | ActivityRunProfileExecutionStats | RunProfileProgressInfo |
|--------|----------------------------------|------------------------|
| **Timing** | After completion | During execution |
| **Storage** | PostgreSQL (permanent) | Redis (ephemeral, 1-hour TTL) |
| **Detail** | Final summary counts | Live phase + incremental counts |
| **Performance** | 5 DB COUNT queries | Single Redis GET (microseconds) |
| **Use Case** | Historical analysis, auditing | Real-time monitoring |
| **Phase Info** | No | Yes (Importing, Syncing, Preparing, etc.) |

## Implementation Phases

### Phase 1: Redis Infrastructure
- Add Redis service to docker-compose.yml (redis:7-alpine)
- Add StackExchange.Redis NuGet package to Application/Web/Worker
- Configure Redis connection in Program.cs (both Web and Worker)
- Environment variables: `JIM_REDIS_HOST`, `JIM_REDIS_PORT`, `JIM_REDIS_PROGRESS_TTL_SECONDS`

### Phase 2: Progress Models
- Create `RunProfileProgressInfo` base class
- Create `ImportProgressInfo` with `ImportPhase` enum
- Create `SyncProgressInfo` with `SyncPhase` enum
- Enhance existing `ExportProgressInfo` with operation counts
- Add operation breakdown fields: CreatedCount, UpdatedCount, DeletedCount

### Phase 3: Progress Tracking Service
- Create `ProgressTrackingService` in JIM.Application
- Methods: `UpdateProgressAsync`, `GetProgressAsync<T>`, `ClearProgressAsync`
- Redis key pattern: `progress:{activityId}`
- JSON serialisation with polymorphic type handling
- TTL management (auto-expire after completion)

### Phase 4: Worker Integration
- Update `SyncImportTaskProcessor.cs` with progress tracking
- Update `SyncFullSyncTaskProcessor.cs` with progress tracking
- Enhance `SyncExportTaskProcessor.cs` callback to write to Redis
- Progress updates at: phase transitions, batch completions, final summary

### Phase 5: API Layer
- Add `GET /api/v1/activities/{id}/progress` endpoint (lightweight polling)
- Enhance `GET /api/v1/activities/{id}` to include progress when available
- Create `RunProfileProgressDto` with polymorphic type support
- Update `ActivityDetailDto` to optionally include progress

### Phase 6: PowerShell Integration
- Add `-Follow` parameter to `Get-JIMActivity` (continuous polling with progress display)
- Enhance `Start-JIMRunProfile -Wait` with live progress updates
- Reuse progress bar functions from `Test-Helpers.ps1`
- MIM-style display: `"Importing | Processed: 457/1000 | Creates: 234, Updates: 198, Deletes: 25"`
- Handle indeterminate progress (delta imports where total is unknown)

### Phase 7: Testing
- Unit tests: `ProgressTrackingServiceTests` with Testcontainers for Redis
- Integration tests: Full run profile execution with progress verification
- API tests: Progress endpoint + 404 handling + embedded progress
- PowerShell tests: Polling and display logic (mocked API responses)
- Performance tests: Memory usage + Redis latency under concurrent operations

### Phase 8: Documentation
- Update `CLAUDE.md` with Redis architecture details
- Update `DEVELOPER_GUIDE.md` with progress tracking patterns
- Update `.devcontainer/README.md` with Docker stack changes
- Document Redis configuration and troubleshooting

## Success Criteria

✅ Redis service running in Docker stack with health checks
✅ Zero database writes for progress (all via Redis)
✅ Phase + object-level progress for Import, Export, Sync with operation breakdown
✅ Dual API endpoints working (enhanced /activities/{id} + dedicated /progress)
✅ PowerShell live progress via 2s polling in both cmdlets
✅ Indeterminate progress support for delta imports
✅ Auto-cleanup via TTL (1-hour default)
✅ Progress survives worker restarts
✅ 90%+ test coverage for new components
✅ No API or Redis bottlenecks under load

## Benefits

**Performance**:
- Eliminates database load from progress updates (addresses EF thread-safety concerns)
- Microsecond read/write times via Redis (vs milliseconds for database)
- Auto-cleanup via TTL (no manual maintenance)

**User Experience**:
- Real-time progress visibility during execution
- MIM-style operation breakdown (creates/updates/deletes)
- Progress available to all users (not just initiator)
- Early problem detection (error rates, stalls)

**Architecture**:
- Foundation for future caching (EF query cache, connector metadata)
- Enables WebSocket pub/sub for JIM.Web real-time UI
- Clean separation: ephemeral data in Redis, persistent data in PostgreSQL
- Scalable pattern for future features

## Dependencies

- Redis 7-alpine Docker image (~10MB)
- StackExchange.Redis NuGet package (v2.8.16+)
- No breaking changes to existing Stats functionality
- No database schema changes required

## Risks & Mitigations

**Risk**: Redis unavailable during run profile execution
**Mitigation**: Progress tracking is optional; execution continues, Stats still calculated from database

**Risk**: Memory usage with many concurrent run profiles
**Mitigation**: 256MB Redis maxmemory with LRU eviction policy, 1-hour TTL for auto-cleanup

**Risk**: Polling overhead for API/PowerShell
**Mitigation**: 2-second polling interval, lightweight Redis queries (microseconds), dedicated progress endpoint

## Related Features

- **Schedules** (future): Overall progress across multiple run profile executions
- **JIM.Web Progress UI** (future): Real-time progress bars in web interface using WebSocket pub/sub
- **Performance Caching** (future): Use Redis for EF query caching, connector metadata caching

## Notes

- Post-MVP feature (not required for initial release)
- Complements existing Stats system (doesn't replace it)
- Single worker architecture assumption (no horizontal scaling planned)
- Air-gapped compatible (self-hosted Redis container)
