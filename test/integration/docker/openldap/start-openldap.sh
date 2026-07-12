#!/bin/bash
# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.
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

# Reconcile each MDB database's mapsize with the current required values.
# Snapshot images bake cn=config from whenever they were built, so a snapshot
# created before a mapsize was raised silently reintroduces the old cap. A full
# main database rejects further writes (MDB_MAP_FULL errors); a full accesslog
# is worse: writes to the main databases still succeed but the overlay stops
# logging them, so JIM's delta imports silently miss changes. slapd is not
# running yet, so patch cn=config offline with slapmodify. Keep these values in
# sync with the olcDbMaxSize settings in scripts/01-add-second-suffix.sh (the
# authoritative values for fresh builds).
ACCESSLOG_MAXSIZE=137438953472
MAINDB_MAXSIZE=34359738368
CONFIG_DIR="$DATA_DIR/slapd.d"
SLAPMODIFY="/opt/bitnami/openldap/sbin/slapmodify"

set_db_maxsize() {
    suffix="$1"
    maxsize="$2"
    db_ldif=$(grep -l "^olcSuffix: $suffix" "$CONFIG_DIR/cn=config/"olcDatabase=*.ldif 2>/dev/null | head -1)
    if [ -z "$db_ldif" ]; then
        echo "[openldap-snapshot] WARNING: no database with suffix '$suffix' found in cn=config — skipping mapsize reconcile"
        return
    fi
    current=$(grep "^olcDbMaxSize:" "$db_ldif" | head -1 | awk '{print $2}')
    if [ "$current" = "$maxsize" ]; then
        return
    fi
    db_rdn=$(basename "$db_ldif" .ldif)
    echo "[openldap-snapshot] Raising olcDbMaxSize for '$suffix' (${current:-unset} -> $maxsize)..."
    if "$SLAPMODIFY" -F "$CONFIG_DIR" -n 0 <<SLAPMOD
dn: $db_rdn,cn=config
changetype: modify
replace: olcDbMaxSize
olcDbMaxSize: $maxsize
SLAPMOD
    then
        echo "[openldap-snapshot] Mapsize updated for '$suffix'"
    else
        echo "[openldap-snapshot] WARNING: slapmodify failed for '$suffix' — mapsize stays ${current:-unset}; large-scale runs may hit MDB_MAP_FULL"
    fi
}

if [ -d "$CONFIG_DIR" ] && [ -x "$SLAPMODIFY" ]; then
    set_db_maxsize "cn=accesslog" "$ACCESSLOG_MAXSIZE"
    set_db_maxsize "dc=yellowstone,dc=local" "$MAINDB_MAXSIZE"
    set_db_maxsize "dc=glitterband,dc=local" "$MAINDB_MAXSIZE"
fi

# Hand off to the original Bitnami entrypoint
exec /opt/bitnami/scripts/openldap/entrypoint.sh /opt/bitnami/scripts/openldap/run.sh
