# JIM PostgreSQL Database Guide

> Configuration, connection pooling, backup/restore, and environment-specific notes for JIM's PostgreSQL database.

---

## Configuration Overview

JIM uses PostgreSQL 18 with environment-specific tuning:

| Environment | File | Purpose |
|-------------|------|---------|
| Full Stack | `docker-compose.yml` | Production-like deployment with PGTune settings |
| Codespaces | `docker-compose.override.yml` | Reduced settings for ~8GB environments |
| Local Migrations | `db.yml` | Simple DB for EF migrations (not for production) |

---

## Full Stack Settings

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

## Connection Pooling

JIM uses Npgsql connection pooling with the following default settings:

| Parameter | Value | Description |
|-----------|-------|-------------|
| Minimum Pool Size | 5 | Keep connections warm for common operations |
| Maximum Pool Size | 30 | Per-service limit (3 services × 30 = 90 max, leaving headroom) |
| Connection Idle Lifetime | 300s | Recycle idle connections after 5 minutes |
| Connection Pruning Interval | 30s | Check for idle connections every 30 seconds |

> **Note:** JIM has 3 services (Web, Worker, Scheduler). The Maximum Pool Size must leave headroom within PostgreSQL's `max_connections` for superuser connections, monitoring tools, and ad-hoc sessions. The default 30 per service uses 90 of the 200 available connections, leaving 110 for headroom.

### Recommended Pool Sizes by Deployment Size

| Environment | Services | Max Pool/Service | Total Max | PostgreSQL max_connections |
|-------------|----------|------------------|-----------|----------------------------|
| Development | 3 | 20 | 60 | 100-200 |
| Small (< 10k objects) | 3 | 30 | 90 | 200 |
| Medium (10k-100k objects) | 3 | 50 | 150 | 200-300 |
| Large (100k+ objects) | 3-6 | 50 | 150-300 | 300-500 |

### Monitoring Connection Pool

Enable Npgsql logging by setting the logging level to `Debug` for the `Npgsql` logger. Connection pool statistics will appear in logs during high activity.

---

## Backup and Restore

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

Uses PostgreSQL defaults. This is intentional — the file is for running EF migrations only, not for production use or performance testing.

---

## References

- [PGTune](https://pgtune.leopard.in.ua/) - PostgreSQL configuration calculator
- [Npgsql Connection String Parameters](https://www.npgsql.org/doc/connection-string-parameters.html)
- [PostgreSQL Performance Tuning](https://wiki.postgresql.org/wiki/Performance_Optimization)
- [EF Core Indexing](https://learn.microsoft.com/en-us/ef/core/modeling/indexes)
