#!/bin/bash
# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.
# Add a second MDB database (dc=glitterband,dc=local) to the OpenLDAP instance.
#
# Bitnami runs scripts in /docker-entrypoint-initdb.d/ AFTER ldap_initialize()
# completes and stops slapd. We start slapd temporarily, use ldapadd to create
# the second database via cn=config, populate it, then stop slapd. The Bitnami
# entrypoint restarts slapd for real after all init scripts complete.
#
# This gives us two naming contexts (partitions) for testing partition-scoped
# import run profiles (Issue #72, Phase 1b).

set -euo pipefail

SLAPD="/opt/bitnami/openldap/sbin/slapd"
SLAPPASSWD="/opt/bitnami/openldap/sbin/slappasswd"
SLAPD_CONF_DIR="/opt/bitnami/openldap/etc/slapd.d"
SLAPD_PID_FILE="/opt/bitnami/openldap/var/run/slapd.pid"
REGION_B_DB_DIR="/bitnami/openldap/data/glitterband"
LDAP_PORT="${LDAP_PORT_NUMBER:-1389}"
LDAP_URI="ldap://localhost:${LDAP_PORT}"

# Config admin credentials (set in docker-compose environment)
CONFIG_ADMIN_DN="cn=${LDAP_CONFIG_ADMIN_USERNAME:-admin},cn=config"
CONFIG_ADMIN_PW="${LDAP_CONFIG_ADMIN_PASSWORD:-configpassword}"

# Data admin credentials for the new suffix (same as primary)
DATA_ADMIN_PW="${LDAP_ADMIN_PASSWORD:-adminpassword}"

echo "[openldap-init] Adding second suffix: dc=glitterband,dc=local"

# Create the data directory for the second database
mkdir -p "$REGION_B_DB_DIR"

# Start slapd in background (it was stopped by ldap_initialize)
echo "[openldap-init] Starting slapd temporarily..."
$SLAPD -h "ldap://:${LDAP_PORT}/ ldapi:///" -F "$SLAPD_CONF_DIR" -d 0 &
SLAPD_PID=$!

# Wait for slapd to be ready
for i in $(seq 1 30); do
    if ldapsearch -x -H "$LDAP_URI" -b "" -s base 'objectclass=*' namingContexts >/dev/null 2>&1; then
        echo "[openldap-init] slapd is ready"
        break
    fi
    if [ "$i" -eq 30 ]; then
        echo "[openldap-init] ERROR: slapd failed to start within 30 seconds"
        exit 1
    fi
    sleep 1
done

# Load custom JIM schema extensions.
# Defines jimGroup (SUP groupOfNames STRUCTURAL) with additional MAY attributes:
#   - mail (from cosine schema, already loaded by Bitnami)
#   - jimGroupType (custom: group classification, e.g. "Managed", "Self-Service")
#   - jimGroupStatus (custom: lifecycle status, e.g. "Active", "Archived")
# Defines jimPerson (SUP inetOrgPerson STRUCTURAL) with additional MAY attributes:
#   - jimEmployeeEndDate (Generalized Time; typed DateTime by the JIM LDAP connector so
#     relative-date scoping criteria can target it - Scenario 8 LeaverCohort step, #908)
#   - jimLeaverCohort (Boolean marker identifying the Scenario 8 leaver-cohort users; read
#     by the test harness over LDAP, never selected into JIM)
# OIDs use the 1.3.6.1.4.1.99999 test arc (integration tests only).
# Schema is global (cn=config), so both Yellowstone and Glitterband suffixes can use these classes.
echo "[openldap-init] Loading JIM schema extensions..."
ldapadd -x -H "$LDAP_URI" -D "$CONFIG_ADMIN_DN" -w "$CONFIG_ADMIN_PW" <<SCHEMA
dn: cn=jim-extensions,cn=schema,cn=config
objectClass: olcSchemaConfig
cn: jim-extensions
olcAttributeTypes: ( 1.3.6.1.4.1.99999.1.1.1 NAME 'jimGroupType' DESC 'Group type classification' EQUALITY caseIgnoreMatch SUBSTR caseIgnoreSubstringsMatch SYNTAX 1.3.6.1.4.1.1466.115.121.1.15 SINGLE-VALUE )
olcAttributeTypes: ( 1.3.6.1.4.1.99999.1.1.2 NAME 'jimGroupStatus' DESC 'Group lifecycle status' EQUALITY caseIgnoreMatch SUBSTR caseIgnoreSubstringsMatch SYNTAX 1.3.6.1.4.1.1466.115.121.1.15 SINGLE-VALUE )
olcAttributeTypes: ( 1.3.6.1.4.1.99999.1.1.3 NAME 'jimEmployeeEndDate' DESC 'Employment end date' EQUALITY generalizedTimeMatch ORDERING generalizedTimeOrderingMatch SYNTAX 1.3.6.1.4.1.1466.115.121.1.24 SINGLE-VALUE )
olcAttributeTypes: ( 1.3.6.1.4.1.99999.1.1.4 NAME 'jimLeaverCohort' DESC 'Scenario 8 leaver-cohort marker' EQUALITY booleanMatch SYNTAX 1.3.6.1.4.1.1466.115.121.1.7 SINGLE-VALUE )
olcObjectClasses: ( 1.3.6.1.4.1.99999.1.2.1 NAME 'jimGroup' DESC 'Extended group with type, status, and mail' SUP groupOfNames STRUCTURAL MAY ( mail $ jimGroupType $ jimGroupStatus ) )
olcObjectClasses: ( 1.3.6.1.4.1.99999.1.2.2 NAME 'jimPerson' DESC 'Extended person with employment end date' SUP inetOrgPerson STRUCTURAL MAY ( jimEmployeeEndDate $ jimLeaverCohort ) )
SCHEMA
echo "[openldap-init] JIM schema extensions loaded"

# Keep large multi-valued attributes sorted for O(log N) value lookups.
# Without sortvals, slapd duplicate-checks each added value against every existing
# value with a linear scan, so appending members to a large group costs
# O(existing members) per modify and a big-group export becomes quadratic in the
# member count (measured on Scale500k25kGroups: ~0.02s per 100-member modify on a
# small entry rising to ~1.3s at 350K members). sortvals stores the values sorted
# so the duplicate check is a binary search.
# olcSortVals lives on the frontend database entry (settings there apply to all
# databases) and only affects entries written while it is active, so it MUST be
# applied before any group data is populated.
# Deliberately NOT reconciled in start-openldap.sh: applying sortvals to snapshot
# data whose entries were stored unsorted breaks value lookups (binary search over
# unsorted values). This script is part of the snapshot content hash, so existing
# snapshots rebuild automatically with sortvals active from creation.
echo "[openldap-init] Enabling sorted storage for multi-valued 'member' (sortvals)..."
ldapmodify -x -H "$LDAP_URI" -D "$CONFIG_ADMIN_DN" -w "$CONFIG_ADMIN_PW" <<SORTVALS
dn: olcDatabase={-1}frontend,cn=config
changetype: modify
add: olcSortVals
olcSortVals: member
SORTVALS
echo "[openldap-init] sortvals enabled for 'member'"

# Hash the password for the new database's rootpw
HASHED_PW=$($SLAPPASSWD -s "$DATA_ADMIN_PW")

# Add the second MDB database via cn=config
echo "[openldap-init] Adding second MDB database to cn=config..."
ldapadd -x -H "$LDAP_URI" -D "$CONFIG_ADMIN_DN" -w "$CONFIG_ADMIN_PW" <<LDIF
dn: olcDatabase=mdb,cn=config
objectClass: olcDatabaseConfig
objectClass: olcMdbConfig
olcDatabase: mdb
olcSuffix: dc=glitterband,dc=local
olcDbDirectory: ${REGION_B_DB_DIR}
olcRootDN: cn=admin,dc=glitterband,dc=local
olcRootPW: ${HASHED_PW}
olcDbMaxSize: 34359738368
olcDbIndex: objectClass eq
olcDbIndex: uid eq
olcDbIndex: cn eq
olcDbIndex: entryUUID eq
olcSizeLimit: unlimited
olcAccess: {0}to * by dn.exact="cn=admin,dc=glitterband,dc=local" manage by * read
LDIF

echo "[openldap-init] Second MDB database added to cn=config"

# Add accesslog overlay to the Glitterband database
# This mirrors the accesslog overlay that Bitnami auto-configures on the primary
# (Yellowstone) database, logging writes to the shared cn=accesslog database.
# Without this, delta imports on the Target system cannot detect changes.
echo "[openldap-init] Adding accesslog overlay to Glitterband database..."

# Determine the Glitterband database number in cn=config.
# It's the database we just added — query cn=config to find it.
GLITTERBAND_DB_DN=$(ldapsearch -x -H "$LDAP_URI" -D "$CONFIG_ADMIN_DN" -w "$CONFIG_ADMIN_PW" \
    -b "cn=config" "(olcSuffix=dc=glitterband,dc=local)" dn -LLL 2>/dev/null | grep "^dn:" | head -1 | sed 's/^dn: //')

if [ -z "$GLITTERBAND_DB_DN" ]; then
    echo "[openldap-init] WARNING: Could not find Glitterband database DN in cn=config. Skipping accesslog overlay."
else
    echo "[openldap-init] Glitterband database DN: $GLITTERBAND_DB_DN"
    ldapadd -x -H "$LDAP_URI" -D "$CONFIG_ADMIN_DN" -w "$CONFIG_ADMIN_PW" <<ALDIF
dn: olcOverlay=accesslog,$GLITTERBAND_DB_DN
objectClass: olcOverlayConfig
objectClass: olcAccessLogConfig
olcOverlay: accesslog
olcAccessLogDB: cn=accesslog
olcAccessLogOps: writes
olcAccessLogPurge: 07+00:00 01+00:00
olcAccessLogSuccess: TRUE
olcAccessLogOld: (objectClass=*)
olcAccessLogOldAttr: objectClass
ALDIF
    echo "[openldap-init] Accesslog overlay added to Glitterband database"
fi

# Populate the second database with root entry and base OUs
echo "[openldap-init] Loading Glitterband base entries..."
ldapadd -x -H "$LDAP_URI" -D "cn=admin,dc=glitterband,dc=local" -w "$DATA_ADMIN_PW" <<LDIF
dn: dc=glitterband,dc=local
objectClass: dcObject
objectClass: organization
dc: glitterband
o: Glitterband Test Organisation

dn: ou=People,dc=glitterband,dc=local
objectClass: organizationalUnit
ou: People

dn: ou=Groups,dc=glitterband,dc=local
objectClass: organizationalUnit
ou: Groups
LDIF

echo "[openldap-init] Glitterband base entries loaded"

# Raise the primary (Yellowstone) database's MDB mapsize. Bitnami's default is
# 1GB, which the large-scale templates exceed: 200K users + 10K long-tail groups
# already consume ~634MB, so Scale500k and above would hit MDB_MAP_FULL during
# population. The Glitterband database created above gets the same 32GB via its
# creation LDIF; both must stay in sync with MAINDB_MAXSIZE in start-openldap.sh,
# which re-applies the value to snapshot-restored containers. MDB maps are
# sparse, so the larger mapsize consumes no disk up front.
echo "[openldap-init] Raising primary (Yellowstone) database mapsize..."

YELLOWSTONE_DB_DN=$(ldapsearch -x -H "$LDAP_URI" -D "$CONFIG_ADMIN_DN" -w "$CONFIG_ADMIN_PW" \
    -b "cn=config" "(olcSuffix=dc=yellowstone,dc=local)" dn -LLL 2>/dev/null | grep "^dn:" | head -1 | sed 's/^dn: //')

if [ -z "$YELLOWSTONE_DB_DN" ]; then
    echo "[openldap-init] WARNING: Could not find Yellowstone database DN in cn=config. Skipping mapsize increase."
else
    ldapmodify -x -H "$LDAP_URI" -D "$CONFIG_ADMIN_DN" -w "$CONFIG_ADMIN_PW" <<YSMODIFY
dn: $YELLOWSTONE_DB_DN
changetype: modify
replace: olcDbMaxSize
olcDbMaxSize: 34359738368
YSMODIFY
    echo "[openldap-init] Yellowstone database mapsize raised to 32GB"
fi

# Configure the accesslog database for production-like usage:
# 1. Increase MDB mapsize from default (~10MB) to 128GB. Without this, the
#    accesslog silently stops logging once the MDB map is full (MDB_MAP_FULL),
#    causing delta imports to miss changes: writes to the main databases still
#    succeed, but the overlay drops their log entries, so JIM's delta import
#    finds nothing and the miss only surfaces as a test assertion much later.
#    Empirical sizing: one full write cycle of ~210K objects (the initial
#    export at Scale200k10kGroups, long-tail group memberships included)
#    consumes ~8GB. Live population plus export in the same slapd lifetime
#    doubles that, and Scale1m60kGroups roughly quintuples it; 128GB covers
#    the worst case with headroom. MDB maps are sparse: disk is only consumed
#    as entries are actually written, so a large mapsize costs nothing up front.
#    This value must stay in sync with ACCESSLOG_MAXSIZE in start-openldap.sh,
#    which re-applies it to snapshot-restored containers whose baked config
#    predates the current value.
# 2. Set unlimited size limit so delta import queries are not truncated by the
#    default olcSizeLimit (500). OpenLDAP enforces olcSizeLimit as a hard cap
#    even with paging controls for non-rootDN clients.
echo "[openldap-init] Configuring accesslog database (mapsize + size limit)..."

ACCESSLOG_DB_DN=$(ldapsearch -x -H "$LDAP_URI" -D "$CONFIG_ADMIN_DN" -w "$CONFIG_ADMIN_PW" \
    -b "cn=config" "(olcSuffix=cn=accesslog)" dn -LLL 2>/dev/null | grep "^dn:" | head -1 | sed 's/^dn: //')

if [ -z "$ACCESSLOG_DB_DN" ]; then
    echo "[openldap-init] WARNING: Could not find accesslog database DN in cn=config. Skipping accesslog configuration."
else
    echo "[openldap-init] Accesslog database DN: $ACCESSLOG_DB_DN"
    ldapmodify -x -H "$LDAP_URI" -D "$CONFIG_ADMIN_DN" -w "$CONFIG_ADMIN_PW" <<ALMODIFY
dn: $ACCESSLOG_DB_DN
changetype: modify
replace: olcDbMaxSize
olcDbMaxSize: 137438953472
-
add: olcSizeLimit
olcSizeLimit: unlimited
ALMODIFY
    echo "[openldap-init] Accesslog database configured (mapsize=128GB, sizeLimit=unlimited)"
fi

# Relax MDB write durability for test speed unless explicitly disabled.
# 'olcDbEnvFlags: nosync' skips the per-transaction fsync that otherwise caps
# LDAP write throughput at ~70 adds/sec (single-writer MDB, two fsyncs per
# logged write via the accesslog overlay). Test data is disposable, so fast
# writes are the default; snapshot population in particular benefits hugely.
#
# *** TEST-ONLY SPEED-UP; NOT THE CUSTOMER EXPERIENCE. *** Customer directories
# fsync their writes. start-openldap.sh re-reconciles this flag on every
# container start from LDAP_TEST_FAST_WRITES, and the integration test runner
# exposes -DurableDirectoryWrites to run customer-representative tests.
if [ "${LDAP_TEST_FAST_WRITES:-yes}" = "yes" ]; then
    echo "[openldap-init] Enabling fast (nosync) writes on all MDB databases (TEST-ONLY speed-up)..."
    for DB_DN in "$YELLOWSTONE_DB_DN" "$GLITTERBAND_DB_DN" "$ACCESSLOG_DB_DN"; do
        if [ -n "$DB_DN" ]; then
            ldapmodify -x -H "$LDAP_URI" -D "$CONFIG_ADMIN_DN" -w "$CONFIG_ADMIN_PW" <<NOSYNC || echo "[openldap-init] WARNING: failed to enable nosync on $DB_DN"
dn: $DB_DN
changetype: modify
add: olcDbEnvFlags
olcDbEnvFlags: nosync
NOSYNC
        fi
    done
    echo "[openldap-init] Fast writes enabled"
else
    echo "[openldap-init] LDAP_TEST_FAST_WRITES=no — keeping durable (customer-representative) writes"
fi

# Stop slapd (Bitnami will restart it after all init scripts)
echo "[openldap-init] Stopping slapd..."
kill "$SLAPD_PID" 2>/dev/null || true
wait "$SLAPD_PID" 2>/dev/null || true

echo "[openldap-init] Multi-suffix setup complete"
echo "[openldap-init]   Suffix 1: dc=yellowstone,dc=local (primary)"
echo "[openldap-init]   Suffix 2: dc=glitterband,dc=local (added)"
