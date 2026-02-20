# PostgreSQL Database Configuration Improvements

> Review findings and improvement plan for JIM's PostgreSQL configuration

## Current Configuration Overview

JIM uses PostgreSQL 18 with environment-specific tuning:

| Environment | File | Purpose |
|-------------|------|---------|
| Full Stack | `docker-compose.yml` | Production-like deployment with PGTune settings |
| Codespaces | `docker-compose.override.yml` | Reduced settings for ~8GB environments |
| Local Migrations | `db.yml` | Simple DB for EF migrations (not for production) |

---

## Current Settings (Full Stack)

The `docker-compose.yml` configuration is optimised for **64GB Windows / 32GB WSL / 16 cores / SSD** using [PGTune](https://pgtune.leopard.in.ua/):

```
max_connections=200
shared_buffers=8GB
effective_cache_size=24GB
maintenance_work_mem=2GB
checkpoint_completion_target=0.9
wal_buffers=16MB
default_statistics_target=100
random_page_cost=1.1
effective_io_concurrency=200
work_mem=10485kB
min_wal_size=1GB
max_wal_size=4GB
max_worker_processes=16
max_parallel_workers_per_gather=4
max_parallel_workers=16
max_parallel_maintenance_workers=4
```

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

### Issues Identified

#### 1. No Explicit Connection Pooling Configuration (LOW)

**Location:** `src/JIM.PostgresData/JimDbContext.cs` line 91

**Current:** Connection string uses Npgsql defaults (Min=0, Max=100 per pool)

```csharp
_connectionString = $"Host={dbHostName};Database={dbName};Username={dbUsername};Password={dbPassword}";
```

**Recommendation:** Consider adding pooling parameters when scaling:
```csharp
_connectionString += ";Minimum Pool Size=5;Maximum Pool Size=50;Connection Idle Lifetime=300";
```

---

#### 2. Limited Custom Indexes (MEDIUM)

**Location:** `src/JIM.PostgresData/JimDbContext.cs` lines 202-209

**Current:** Only 2 explicit indexes defined:
- `DeferredReference (TargetMvoId, TargetSystemId)` - composite
- `TrustedCertificate (Thumbprint)` - unique

**Recommendation:** Add indexes for frequently queried columns:

```csharp
// ConnectedSystemObject - for import lookups
modelBuilder.Entity<ConnectedSystemObject>()
    .HasIndex(cso => new { cso.ConnectedSystemId, cso.ExternalId });

// PendingExport - for export batch queries
modelBuilder.Entity<PendingExport>()
    .HasIndex(pe => new { pe.ConnectedSystemId, pe.ChangeType });

// MetaverseObjectAttributeValue - for search/join operations
modelBuilder.Entity<MetaverseObjectAttributeValue>()
    .HasIndex(moav => new { moav.AttributeId, moav.StringValue });
```

---

#### 3. No Query Timeout Protection (LOW)

**Current:** No statement timeout configured

**Recommendation:** Add to PostgreSQL command in `docker-compose.yml`:
```
-c statement_timeout=300000
```
This prevents runaway queries from consuming resources (5 minute timeout).

---

#### 4. No Container Health Check (LOW)

**Current:** No health check on PostgreSQL container

**Recommendation:** Add to `docker-compose.yml`:
```yaml
jim.database:
  healthcheck:
    test: ["CMD-SHELL", "pg_isready -U ${DB_USERNAME} -d ${DB_NAME}"]
    interval: 10s
    timeout: 5s
    retries: 5
```

---

## Improvement Plan

### Phase 1: Index Optimisation (Priority: Medium) - IMPLEMENTED

- [x] **1.1** Analyse slow query patterns using `pg_stat_statements`
- [x] **1.2** Add composite index on `ConnectedSystemObject (ConnectedSystemId, TypeId)`
- [x] **1.3** Add composite index on `PendingExport (ConnectedSystemId, Status)`
- [x] **1.4** Add index on `MetaverseObjectAttributeValue (AttributeId, StringValue)`
- [x] **1.5** Add index on `ConnectedSystemObjectAttributeValue (ConnectedSystemObjectId, AttributeId)`
- [x] **1.6** Create EF migration for new indexes (`20251208094620_AddPerformanceIndexes`)
- [x] **1.7** Add database connectivity check to health endpoint (`/api/v1/health/ready`)
- [x] **1.8** Add unit tests for health controller

### Phase 2: Operational Improvements (Priority: Low) - IMPLEMENTED

- [x] **2.1** Add `statement_timeout` (5 minutes) to prevent runaway queries
- [x] **2.2** Add container health checks using `pg_isready`
- [x] **2.3** Add `log_min_duration_statement` (1s prod, 0.5s dev) for slow query logging
- [x] **2.4** Document backup/restore procedures (see below)

### Phase 3: Connection Management (Priority: Low) - IMPLEMENTED

- [x] **3.1** Add explicit connection pooling parameters to connection string
- [x] **3.2** Document connection pool settings (monitoring is via Npgsql logging)
- [x] **3.3** Document recommended pool sizes for different deployment sizes (see below)

---

## Connection Pooling Configuration

JIM uses Npgsql connection pooling with the following default settings:

| Parameter | Value | Description |
|-----------|-------|-------------|
| Minimum Pool Size | 5 | Keep connections warm for common operations |
| Maximum Pool Size | 50 | Per-service limit (4 services × 50 = 200 max total) |
| Connection Idle Lifetime | 300s | Recycle idle connections after 5 minutes |
| Connection Pruning Interval | 30s | Check for idle connections every 30 seconds |

### Recommended Pool Sizes by Deployment Size

| Environment | Services | Max Pool/Service | Total Max | PostgreSQL max_connections |
|-------------|----------|------------------|-----------|----------------------------|
| Development | 4 | 25 | 100 | 100 |
| Small (< 10k objects) | 4 | 50 | 200 | 200 |
| Medium (10k-100k objects) | 4 | 75 | 300 | 300 |
| Large (100k+ objects) | 4-8 | 100 | 400-800 | 500-1000 |

### Monitoring Connection Pool

Enable Npgsql logging by setting the logging level to `Debug` for the `Npgsql` logger. Connection pool statistics will appear in logs during high activity.

---

## Backup and Restore Procedures

### Creating a Backup

Using Docker:
```bash
# Backup to a timestamped file
docker exec jim.database pg_dump -U ${DB_USERNAME} -Fc ${DB_NAME} > jim_backup_$(date +%Y%m%d_%H%M%S).dump

# Or use plain SQL format (larger but human-readable)
docker exec jim.database pg_dump -U ${DB_USERNAME} ${DB_NAME} > jim_backup_$(date +%Y%m%d_%H%M%S).sql
```

### Restoring from Backup

```bash
# Restore from custom format (.dump)
docker exec -i jim.database pg_restore -U ${DB_USERNAME} -d ${DB_NAME} --clean < jim_backup.dump

# Restore from SQL format
docker exec -i jim.database psql -U ${DB_USERNAME} -d ${DB_NAME} < jim_backup.sql
```

### Scheduled Backups (Production)

For production deployments, consider:
1. Using a volume-mounted backup directory
2. Setting up cron jobs for automated backups
3. Implementing backup rotation (keep last N backups)
4. Testing restore procedures regularly

---

## Environment-Specific Notes

### Codespaces (~8GB)

Settings are appropriately reduced:
- `shared_buffers=256MB`
- `effective_cache_size=1GB`
- `work_mem=4MB`
- `max_connections=100`

### Local Development (db.yml)

Uses PostgreSQL defaults. This is intentional - the file is for running EF migrations only, not for production use or performance testing.

---

## References

- [PGTune](https://pgtune.leopard.in.ua/) - PostgreSQL configuration calculator
- [Npgsql Connection String Parameters](https://www.npgsql.org/doc/connection-string-parameters.html)
- [PostgreSQL Performance Tuning](https://wiki.postgresql.org/wiki/Performance_Optimization)
- [EF Core Indexing](https://learn.microsoft.com/en-us/ef/core/modeling/indexes)
