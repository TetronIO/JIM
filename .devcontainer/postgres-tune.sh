#!/bin/bash
#
# PostgreSQL Auto-Tuning Script for JIM
#
# Detects the devcontainer's CPU and RAM, then generates two gitignored
# compose overlay files with machine-optimal PostgreSQL settings:
#
#   docker-compose.local.yml  — overrides for the full Docker stack
#   db.local.yml              — overrides for the standalone database (jim-db)
#
# These files are automatically picked up by the jim-* shell aliases.
#
# Usage:
#   .devcontainer/postgres-tune.sh [--cpu N] [--memory N]
#
# Tuning formulas based on https://pgtune.leopard.in.ua/ for OLTP workloads.

set -e

# Resolve repo root (script may be called from anywhere)
REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"

# Color codes
GREEN='\033[0;32m'
BLUE='\033[0;34m'
YELLOW='\033[1;33m'
RED='\033[0;31m'
NC='\033[0m'

print_step()    { echo -e "${BLUE}▶${NC} $1"; }
print_success() { echo -e "${GREEN}✓${NC} $1"; }
print_warning() { echo -e "${YELLOW}⚠${NC} $1"; }
print_error()   { echo -e "${RED}✗${NC} $1"; }

# ============================================================================
# Parse arguments
# ============================================================================

OVERRIDE_CPU=""
OVERRIDE_MEMORY=""

while [[ $# -gt 0 ]]; do
    case $1 in
        --cpu)    OVERRIDE_CPU=$2;    shift 2 ;;
        --memory) OVERRIDE_MEMORY=$2; shift 2 ;;
        *)        print_error "Unknown option: $1"; exit 1 ;;
    esac
done

# ============================================================================
# Detect system specs
# ============================================================================

print_step "Detecting system specifications..."

# CPU cores
if [ -n "$OVERRIDE_CPU" ]; then
    CPU_CORES=$OVERRIDE_CPU
    print_success "CPU cores (override): $CPU_CORES"
else
    CPU_CORES=""
    if command -v nproc &>/dev/null; then
        CPU_CORES=$(nproc 2>/dev/null || true)
    elif [ -f /proc/cpuinfo ]; then
        CPU_CORES=$(grep -c "^processor" /proc/cpuinfo 2>/dev/null || true)
    elif command -v sysctl &>/dev/null; then
        CPU_CORES=$(sysctl -n hw.ncpu 2>/dev/null || true)
    fi

    if [ -z "$CPU_CORES" ] || [ "$CPU_CORES" -lt 1 ] 2>/dev/null; then
        print_error "Could not detect CPU cores. Use --cpu N"
        exit 1
    fi
    print_success "CPU cores (detected): $CPU_CORES"
fi

# Memory
if [ -n "$OVERRIDE_MEMORY" ]; then
    MEMORY_GB=$OVERRIDE_MEMORY
    print_success "Memory (override): ${MEMORY_GB}GB"
else
    MEMORY_GB=""
    if [ -f /proc/meminfo ]; then
        MEMORY_KB=$(grep "^MemTotal:" /proc/meminfo | awk '{print $2}')
        MEMORY_GB=$((MEMORY_KB / 1024 / 1024))
    elif command -v sysctl &>/dev/null; then
        MEMORY_BYTES=$(sysctl -n hw.memsize 2>/dev/null || true)
        if [ -n "$MEMORY_BYTES" ]; then
            MEMORY_GB=$((MEMORY_BYTES / 1024 / 1024 / 1024))
        fi
    fi

    if [ -z "$MEMORY_GB" ] || [ "$MEMORY_GB" -lt 1 ] 2>/dev/null; then
        print_error "Could not detect system memory. Use --memory N"
        exit 1
    fi
    print_success "Memory (detected): ${MEMORY_GB}GB"
fi

# ============================================================================
# Calculate pgtune settings (OLTP workload)
# ============================================================================
# Reference: https://pgtune.leopard.in.ua/
# Workload: OLTP — many short queries, not data-warehouse analytics

print_step "Calculating PostgreSQL settings..."

# -- Reserve memory for non-PostgreSQL workloads --
# The devcontainer also runs JIM services, VS Code Server, .NET build tools,
# and the Docker engine. Current baseline memory usage (March 2026):
#   JIM.Web:        ~300MB    JIM.Scheduler:  ~100MB
#   JIM.Worker:     ~300MB    VS Code/tools:  ~1-2GB
#
# These figures will grow as JIM scales — in particular:
#   - JIM.Worker may consume significantly more RAM with large datasets
#     or if in-memory caching is added in future
#   - .NET builds can spike during parallel compilation
#
# If you see OOM kills on JIM services (not PostgreSQL), increase RESERVED_GB.
# If PostgreSQL is the bottleneck, decrease it. Monitor with: docker stats
RESERVED_GB=4
if [ $MEMORY_GB -le 8 ]; then
    RESERVED_GB=2
fi
PG_MEMORY_GB=$((MEMORY_GB - RESERVED_GB))
if [ $PG_MEMORY_GB -lt 1 ]; then
    PG_MEMORY_GB=1
fi
print_success "Reserving ${RESERVED_GB}GB for devcontainer/JIM services, ${PG_MEMORY_GB}GB for PostgreSQL"

# -- Connections --
MAX_CONNECTIONS=100

# -- shared_buffers: 25% of PostgreSQL-available RAM, minimum 256MB --
SHARED_BUFFERS_MB=$((PG_MEMORY_GB * 1024 / 4))
if [ $SHARED_BUFFERS_MB -lt 256 ]; then
    SHARED_BUFFERS_MB=256
fi

# -- effective_cache_size: 75% of PostgreSQL-available RAM --
# This tells the query planner how much RAM is realistically available
# for OS page cache + shared_buffers combined.
EFFECTIVE_CACHE_MB=$((PG_MEMORY_GB * 1024 * 3 / 4))

# -- maintenance_work_mem: PG RAM / 16, capped at 2GB --
MAINT_WORK_MEM_MB=$((PG_MEMORY_GB * 1024 / 16))
if [ $MAINT_WORK_MEM_MB -gt 2048 ]; then
    MAINT_WORK_MEM_MB=2048
fi
if [ $MAINT_WORK_MEM_MB -lt 64 ]; then
    MAINT_WORK_MEM_MB=64
fi

# -- work_mem: (PG RAM / max_connections) / 4 --
# Each connection may have ~4 concurrent sort/hash operations.
WORK_MEM_KB=$((PG_MEMORY_GB * 1024 * 1024 / MAX_CONNECTIONS / 4))
# Cap at 64MB to avoid OOM under load
if [ $WORK_MEM_KB -gt 65536 ]; then
    WORK_MEM_KB=65536
fi
# Floor at 4MB
if [ $WORK_MEM_KB -lt 4096 ]; then
    WORK_MEM_KB=4096
fi

# -- wal_buffers: 3% of shared_buffers, capped at 64MB, min 1MB --
WAL_BUFFERS_MB=$((SHARED_BUFFERS_MB * 3 / 100))
if [ $WAL_BUFFERS_MB -gt 64 ]; then
    WAL_BUFFERS_MB=64
fi
if [ $WAL_BUFFERS_MB -lt 1 ]; then
    WAL_BUFFERS_MB=1
fi

# -- WAL sizes: conservative for dev --
MIN_WAL_SIZE_MB=$((MEMORY_GB * 64))
if [ $MIN_WAL_SIZE_MB -lt 80 ]; then
    MIN_WAL_SIZE_MB=80
fi
if [ $MIN_WAL_SIZE_MB -gt 1024 ]; then
    MIN_WAL_SIZE_MB=1024
fi
MAX_WAL_SIZE_MB=$((MIN_WAL_SIZE_MB * 4))
if [ $MAX_WAL_SIZE_MB -gt 4096 ]; then
    MAX_WAL_SIZE_MB=4096
fi

# -- Parallel workers: conservative for OLTP --
if [ $CPU_CORES -ge 4 ]; then
    MAX_WORKER_PROCESSES=$CPU_CORES
    MAX_PARALLEL_WORKERS=$((CPU_CORES / 2))
    MAX_PARALLEL_WORKERS_PER_GATHER=2
    MAX_PARALLEL_MAINT_WORKERS=$((CPU_CORES / 4))
    if [ $MAX_PARALLEL_MAINT_WORKERS -lt 1 ]; then
        MAX_PARALLEL_MAINT_WORKERS=1
    fi
else
    MAX_WORKER_PROCESSES=$CPU_CORES
    MAX_PARALLEL_WORKERS=1
    MAX_PARALLEL_WORKERS_PER_GATHER=0
    MAX_PARALLEL_MAINT_WORKERS=1
fi

# -- Storage: assume SSD/NVMe --
RANDOM_PAGE_COST="1.1"
EFFECTIVE_IO_CONCURRENCY=200

# -- shm_size: shared_buffers + 25% headroom, minimum 256MB --
SHM_SIZE_MB=$((SHARED_BUFFERS_MB + SHARED_BUFFERS_MB / 4))
if [ $SHM_SIZE_MB -lt 256 ]; then
    SHM_SIZE_MB=256
fi

# ============================================================================
# Format values (human-readable units)
# ============================================================================

format_mb() {
    local mb=$1
    if [ $mb -ge 1024 ] && [ $((mb % 1024)) -eq 0 ]; then
        echo "$((mb / 1024))GB"
    else
        echo "${mb}MB"
    fi
}

SHARED_BUFFERS_FMT=$(format_mb $SHARED_BUFFERS_MB)
EFFECTIVE_CACHE_FMT=$(format_mb $EFFECTIVE_CACHE_MB)
MAINT_WORK_MEM_FMT=$(format_mb $MAINT_WORK_MEM_MB)
WAL_BUFFERS_FMT=$(format_mb $WAL_BUFFERS_MB)
MIN_WAL_SIZE_FMT=$(format_mb $MIN_WAL_SIZE_MB)
MAX_WAL_SIZE_FMT=$(format_mb $MAX_WAL_SIZE_MB)
SHM_SIZE_FMT=$(format_mb $SHM_SIZE_MB)

# ============================================================================
# Build the postgres command lines
# ============================================================================

# Common tuning flags (shared between both files)
TUNE_FLAGS="-c shared_preload_libraries=pg_stat_statements -c pg_stat_statements.track=all -c max_connections=${MAX_CONNECTIONS} -c shared_buffers=${SHARED_BUFFERS_FMT} -c effective_cache_size=${EFFECTIVE_CACHE_FMT} -c maintenance_work_mem=${MAINT_WORK_MEM_FMT} -c checkpoint_completion_target=0.9 -c wal_buffers=${WAL_BUFFERS_FMT} -c default_statistics_target=100 -c random_page_cost=${RANDOM_PAGE_COST} -c effective_io_concurrency=${EFFECTIVE_IO_CONCURRENCY} -c work_mem=${WORK_MEM_KB}kB -c min_wal_size=${MIN_WAL_SIZE_FMT} -c max_wal_size=${MAX_WAL_SIZE_FMT} -c max_worker_processes=${MAX_WORKER_PROCESSES} -c max_parallel_workers_per_gather=${MAX_PARALLEL_WORKERS_PER_GATHER} -c max_parallel_workers=${MAX_PARALLEL_WORKERS} -c max_parallel_maintenance_workers=${MAX_PARALLEL_MAINT_WORKERS} -c statement_timeout=300000"

# Full stack: includes logging flags (logging_collector, jsonlog, etc.)
FULL_COMMAND="postgres ${TUNE_FLAGS} -c log_min_duration_statement=\${JIM_DB_LOG_MIN_DURATION:-500} -c logging_collector=on -c log_destination=jsonlog -c log_directory=/var/log/jim/database -c log_filename=jim.database.%Y%m%d.log -c log_rotation_age=1440 -c log_rotation_size=0 -c log_file_mode=0644"

# Standalone db: just tuning, no logging setup
DB_COMMAND="postgres ${TUNE_FLAGS}"

# ============================================================================
# Generate gitignored overlay files
# ============================================================================

print_step "Generating local compose overlays..."

SHM_SIZE_LOWER="${SHM_SIZE_FMT,,}"  # lowercase for YAML

# docker-compose.local.yml — overrides jim.database in the full stack
STACK_LOCAL="$REPO_ROOT/docker-compose.local.yml"
cat > "$STACK_LOCAL" <<COMPOSE_EOF
# Auto-generated by .devcontainer/postgres-tune.sh — do not edit or commit
# Tuned for: ${CPU_CORES} cores / ${MEMORY_GB}GB RAM (${PG_MEMORY_GB}GB for PostgreSQL, ${RESERVED_GB}GB reserved)
# Regenerate: jim-postgres-tune  |  .devcontainer/postgres-tune.sh
services:
  jim.database:
    shm_size: '${SHM_SIZE_LOWER}'
    command: ${FULL_COMMAND}
COMPOSE_EOF
print_success "docker-compose.local.yml"

# db.local.yml — overrides jim.database for standalone db (jim-db)
DB_LOCAL="$REPO_ROOT/db.local.yml"
cat > "$DB_LOCAL" <<COMPOSE_EOF
# Auto-generated by .devcontainer/postgres-tune.sh — do not edit or commit
# Tuned for: ${CPU_CORES} cores / ${MEMORY_GB}GB RAM (${PG_MEMORY_GB}GB for PostgreSQL, ${RESERVED_GB}GB reserved)
# Regenerate: jim-postgres-tune  |  .devcontainer/postgres-tune.sh
services:
  jim.database:
    shm_size: '${SHM_SIZE_LOWER}'
    command: ${DB_COMMAND}
COMPOSE_EOF
print_success "db.local.yml"

# ============================================================================
# Summary
# ============================================================================

echo ""
echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
echo -e "${GREEN}PostgreSQL Auto-Tune Summary${NC}  (${CPU_CORES} cores / ${MEMORY_GB}GB RAM)"
echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
echo ""
echo "  System RAM:              ${MEMORY_GB}GB"
echo "  Reserved (JIM/devtools): ${RESERVED_GB}GB"
echo "  Available for PostgreSQL: ${PG_MEMORY_GB}GB"
echo ""
echo "  shared_buffers:          ${SHARED_BUFFERS_FMT}  (25% of ${PG_MEMORY_GB}GB)"
echo "  effective_cache_size:    ${EFFECTIVE_CACHE_FMT}  (75% of ${PG_MEMORY_GB}GB)"
echo "  work_mem:                ${WORK_MEM_KB}kB  (per sort/hash op)"
echo "  maintenance_work_mem:    ${MAINT_WORK_MEM_FMT}"
echo "  wal_buffers:             ${WAL_BUFFERS_FMT}"
echo "  min_wal_size:            ${MIN_WAL_SIZE_FMT}"
echo "  max_wal_size:            ${MAX_WAL_SIZE_FMT}"
echo ""
echo "  max_worker_processes:              ${MAX_WORKER_PROCESSES}"
echo "  max_parallel_workers:              ${MAX_PARALLEL_WORKERS}"
echo "  max_parallel_workers_per_gather:   ${MAX_PARALLEL_WORKERS_PER_GATHER}"
echo "  max_parallel_maintenance_workers:  ${MAX_PARALLEL_MAINT_WORKERS}"
echo ""
echo "  shm_size (Docker):       ${SHM_SIZE_FMT}"
echo "  random_page_cost:        ${RANDOM_PAGE_COST}  (SSD/NVMe)"
echo "  effective_io_concurrency: ${EFFECTIVE_IO_CONCURRENCY}"
echo ""
echo "Restart the database to apply:"
echo "  jim-db-stop && jim-db          (local development)"
echo "  jim-restart                    (Docker stack)"
echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
echo ""
