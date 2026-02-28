# PostgreSQL Database Configuration Improvements

- **Status:** Done

> Review findings and improvement plan for JIM's PostgreSQL configuration. Operational guidance moved to `docs/DATABASE_GUIDE.md`.

---

## Assessment

### What's Working Well

| Area | Status | Notes |
|------|--------|-------|
| Memory allocation | ✅ | Appropriate for target hardware |
| Parallelism | ✅ | Matches CPU count |
| SSD optimisation | ✅ | `random_page_cost=1.1`, `effective_io_concurrency=200` |
| WAL configuration | ✅ | Good checkpoint settings |
| Connection limits | ✅ | 200 connections sufficient for 4 services |

### Issues Found

| # | Severity | Finding |
|---|----------|---------|
| 1 | Medium | Limited custom indexes — only 2 explicit indexes defined |
| 2 | Low | No explicit connection pooling configuration (Npgsql defaults used) |
| 3 | Low | No query timeout protection (`statement_timeout` not set) |
| 4 | Low | No container health check on PostgreSQL container |

---

## Improvement Plan

### Phase 1: Index Optimisation ✅

- [x] **1.1** Analyse slow query patterns using `pg_stat_statements`
- [x] **1.2** Add composite index on `ConnectedSystemObject (ConnectedSystemId, TypeId)`
- [x] **1.3** Add composite index on `PendingExport (ConnectedSystemId, Status)`
- [x] **1.4** Add index on `MetaverseObjectAttributeValue (AttributeId, StringValue)`
- [x] **1.5** Add index on `ConnectedSystemObjectAttributeValue (ConnectedSystemObjectId, AttributeId)`
- [x] **1.6** Create EF migration for new indexes (`20251208094620_AddPerformanceIndexes`)
- [x] **1.7** Add database connectivity check to health endpoint (`/api/v1/health/ready`)
- [x] **1.8** Add unit tests for health controller

### Phase 2: Operational Improvements ✅

- [x] **2.1** Add `statement_timeout` (5 minutes) to prevent runaway queries
- [x] **2.2** Add container health checks using `pg_isready`
- [x] **2.3** Add `log_min_duration_statement` (1s prod, 0.5s dev) for slow query logging
- [x] **2.4** Document backup/restore procedures (see `docs/DATABASE_GUIDE.md`)

### Phase 3: Connection Management ✅

- [x] **3.1** Add explicit connection pooling parameters to connection string
- [x] **3.2** Document connection pool settings (monitoring is via Npgsql logging)
- [x] **3.3** Document recommended pool sizes for different deployment sizes (see `docs/DATABASE_GUIDE.md`)
