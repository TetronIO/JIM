#!/bin/bash
# Restore provisioned OpenLDAP data if volumes are empty (snapshot startup).
#
# docker commit does not capture Docker volumes. During snapshot build, the
# populated /bitnami/openldap data is copied to /bitnami/openldap.provisioned
# inside the container filesystem (which IS captured). On startup, if the
# volume is empty (fresh mount), we restore from the provisioned copy.
#
# IMPORTANT: The accesslog overlay (slapo-accesslog) does not properly
# reinitialise its write path when slapd starts from pre-existing accesslog
# data that was created during a different slapd lifetime (e.g., snapshot
# build). New modifications are silently not logged. Clearing the accesslog
# data directory forces slapd to create a fresh accesslog database on startup,
# which correctly logs all new write operations. The old accesslog entries
# (from the snapshot build) are not needed — the JIM connector captures a
# fresh watermark from the accesslog during each full import.

PROVISIONED_DIR="/bitnami/openldap.provisioned"
DATA_DIR="/bitnami/openldap"
ACCESSLOG_DIR="$DATA_DIR/data/accesslog"

if [ -d "$PROVISIONED_DIR" ] && [ -z "$(ls -A "$DATA_DIR/data" 2>/dev/null)" ]; then
    echo "[openldap-snapshot] Volume is empty — restoring provisioned data from snapshot..."
    # Remove any empty dirs created by Docker volume mount
    rm -rf "${DATA_DIR:?}"/*
    cp -a "$PROVISIONED_DIR"/* "$DATA_DIR/"
    echo "[openldap-snapshot] Data restored successfully"
else
    if [ ! -d "$PROVISIONED_DIR" ]; then
        echo "[openldap-snapshot] No provisioned data found — running as base image"
    else
        echo "[openldap-snapshot] Volume already has data — skipping restore"
    fi
fi

# Clear stale accesslog data so the overlay creates a fresh database.
# The accesslog overlay silently fails to log new writes when starting from
# an MDB file created during a previous slapd lifetime (snapshot build).
if [ -d "$ACCESSLOG_DIR" ]; then
    echo "[openldap-snapshot] Clearing stale accesslog data to force fresh initialisation..."
    rm -f "$ACCESSLOG_DIR/data.mdb" "$ACCESSLOG_DIR/lock.mdb"
    echo "[openldap-snapshot] Accesslog data cleared — slapd will create a fresh database"
fi

# Hand off to the original Bitnami entrypoint
exec /opt/bitnami/scripts/openldap/entrypoint.sh /opt/bitnami/scripts/openldap/run.sh
