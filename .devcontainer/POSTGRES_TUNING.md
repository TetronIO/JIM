# PostgreSQL Auto-Tuning for JIM

PostgreSQL is automatically tuned during devcontainer setup for optimal performance based on your system's CPU and RAM.

## How It Works

`.devcontainer/postgres-tune.sh` detects your system specs and generates two **gitignored** overlay files:

- **`docker-compose.local.yml`** — overrides `shm_size` and `command` for the full Docker stack
- **`db.local.yml`** — overrides `shm_size` and `command` for the standalone database (`jim-db`)

The `jim-*` shell aliases automatically include these files when present. Docker Compose merges files in order — later files win — so the local overlays override the conservative defaults in the tracked files without modifying them.

```
Tracked (in git):                    Gitignored (per-machine):
  docker-compose.yml                   docker-compose.local.yml
  docker-compose.override.yml          db.local.yml
  db.yml
```

## Re-Tuning After Resource Changes

If you increase your devcontainer's CPU/memory allocation:

```bash
jim-postgres-tune                   # re-detect and regenerate
jim-db-stop && jim-db               # restart (local dev)
jim-restart                         # restart (Docker stack)
```

Override detection if needed:

```bash
jim-postgres-tune --cpu 8 --memory 32
```

## What Gets Tuned

The script first reserves memory for non-PostgreSQL workloads (JIM services, VS Code Server, .NET build tools, Docker engine): **4GB** on systems with >8GB RAM, **2GB** on systems with ≤8GB. All tuning formulas use the remaining "PG memory".

| Setting | Formula | Example (12c/15GB → 11GB PG) |
|---------|---------|-------------------------------|
| `shared_buffers` | 25% of PG memory | 2816MB |
| `effective_cache_size` | 75% of PG memory | 8448MB |
| `work_mem` | PG memory / max_connections / 4, max 64MB | 28835kB |
| `maintenance_work_mem` | PG memory / 16, max 2GB | 704MB |
| `wal_buffers` | 3% of shared_buffers, max 64MB | 64MB |
| `min_wal_size` | 64MB per GB total RAM, 80MB–1GB | 960MB |
| `max_wal_size` | 4x min_wal_size, max 4GB | 3840MB |
| `max_worker_processes` | CPU cores | 12 |
| `max_parallel_workers` | CPU cores / 2 | 6 |
| `max_parallel_workers_per_gather` | 2 (fixed for OLTP) | 2 |
| `max_parallel_maintenance_workers` | CPU cores / 4 | 3 |
| `shm_size` (Docker) | shared_buffers + 25% | 3520MB |

### Fixed values (not tuned)

- `max_connections` — 100 (EF Core connection pooling)
- `random_page_cost` — 1.1 (assumes SSD/NVMe)
- `effective_io_concurrency` — 200 (SSD-optimised)
- `statement_timeout` — 300000ms (5 minutes)

## Memory Balance: PostgreSQL vs JIM Services

The auto-tuning reserves a fixed amount of RAM for non-PostgreSQL workloads (JIM services, VS Code Server, .NET build tools, Docker engine) and allocates the rest to PostgreSQL. This is a reasonable starting default, but **the balance may need adjusting** as JIM's resource profile evolves.

### When to increase the reservation (give JIM more RAM)

- **JIM.Worker processing large datasets** — bulk imports, full syncs of large directories, or complex sync rules with many joins can cause the worker to consume significantly more memory than the ~300MB baseline.
- **In-memory caching is added** — if JIM introduces application-level caches (e.g., metaverse attribute caches, connector schema caches), each service will need more headroom.
- **OOM kills on JIM containers** — if `docker stats` shows a JIM service hitting its memory limit or the container is killed by the kernel, PostgreSQL has too large a share. Edit `RESERVED_GB` in `postgres-tune.sh` and re-tune.

### When to decrease the reservation (give PostgreSQL more RAM)

- **Slow queries or high disk I/O on the database** — if PostgreSQL is thrashing (high `buffers_read` vs `buffers_hit` in `pg_stat_bgwriter`), it needs more `shared_buffers`.
- **JIM services are using well below the reserved amount** — check with `docker stats`. If all three JIM services plus tools are comfortably within 2GB on a 16GB system, reduce the reservation to give PostgreSQL more cache.

### How to adjust

Edit `RESERVED_GB` in `.devcontainer/postgres-tune.sh` (near the top of the calculation section), then re-tune:

```bash
jim-postgres-tune
jim-db-stop && jim-db
```

### How to monitor

```bash
# Live memory usage of all containers
docker stats

# PostgreSQL buffer cache hit ratio (should be >95%)
docker compose exec jim.database psql -U jim -d jim -c "
  SELECT
    ROUND(100.0 * blks_hit / NULLIF(blks_hit + blks_read, 0), 1) AS cache_hit_pct,
    blks_hit, blks_read
  FROM pg_stat_database WHERE datname = 'jim';"
```

A cache hit ratio below 95% suggests PostgreSQL needs more `shared_buffers`. Above 99% with low JIM service memory usage suggests you could safely reduce the PostgreSQL share.

## Troubleshooting

**Settings didn't take effect?** Restart the database container after tuning.

**Want to see current PostgreSQL settings?**
```bash
docker compose exec jim.database psql -U jim -d jim -c "
  SELECT name, setting, unit FROM pg_settings
  WHERE name IN ('shared_buffers','effective_cache_size','work_mem',
    'maintenance_work_mem','max_worker_processes','max_parallel_workers',
    'max_parallel_workers_per_gather')
  ORDER BY name;"
```

**Local files not being picked up?** Check they exist:
```bash
ls -la docker-compose.local.yml db.local.yml
```
If missing, regenerate: `jim-postgres-tune`
