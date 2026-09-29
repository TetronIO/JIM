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
#     relative-date scoping criteria can target it - Scenario 008 LeaverCohort step, #908)
#   - jimLeaverCohort (Boolean marker identifying the Scenario 008 leaver-cohort users; read
#     by the test harness over LDAP, never selected into JIM)
# Defines jimBadgeHolder (SUP top AUXILIARY), a JIM-owned auxiliary class so auxiliary class
# testing (issue #492, Scenario 019) does not depend on whichever schemas the base image loads:
#   - jimBadgeNumber (MUST; exercises the export-side required-attribute enforcement path)
#   - jimBadgeColour, jimBadgeIssued (MAYs; the ordinary merge/flow path)
#   - uid (MAY, from cosine): lets a jimBadgeHolder-typed object satisfy the 'account' carrier
#     class's MUST (userid) and its own uid= RDN when JIM provisions it, the same shape as the
#     classic account + posixAccount pairing
# A DIT Content Rule on jimPerson names jimBadgeHolder as permitted, giving the schema
# discovery's "suggested by DIT Content Rule" path something real to read; this is the only
# fixture in the estate exercising the parser's dITContentRules support against a live
# directory. NOTE: with the rule in place slapd enforces it, so jimPerson entries may only
# carry auxiliary classes the rule permits.
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
olcAttributeTypes: ( 1.3.6.1.4.1.99999.1.1.4 NAME 'jimLeaverCohort' DESC 'Scenario 008 leaver-cohort marker' EQUALITY booleanMatch SYNTAX 1.3.6.1.4.1.1466.115.121.1.7 SINGLE-VALUE )
olcAttributeTypes: ( 1.3.6.1.4.1.99999.1.1.5 NAME 'jimBadgeNumber' DESC 'Badge number (required by jimBadgeHolder)' EQUALITY caseIgnoreMatch SUBSTR caseIgnoreSubstringsMatch SYNTAX 1.3.6.1.4.1.1466.115.121.1.15 SINGLE-VALUE )
olcAttributeTypes: ( 1.3.6.1.4.1.99999.1.1.6 NAME 'jimBadgeColour' DESC 'Badge colour' EQUALITY caseIgnoreMatch SUBSTR caseIgnoreSubstringsMatch SYNTAX 1.3.6.1.4.1.1466.115.121.1.15 SINGLE-VALUE )
olcAttributeTypes: ( 1.3.6.1.4.1.99999.1.1.7 NAME 'jimBadgeIssued' DESC 'Badge issue date' EQUALITY generalizedTimeMatch ORDERING generalizedTimeOrderingMatch SYNTAX 1.3.6.1.4.1.1466.115.121.1.24 SINGLE-VALUE )
olcObjectClasses: ( 1.3.6.1.4.1.99999.1.2.1 NAME 'jimGroup' DESC 'Extended group with type, status, and mail' SUP groupOfNames STRUCTURAL MAY ( mail $ jimGroupType $ jimGroupStatus ) )
olcObjectClasses: ( 1.3.6.1.4.1.99999.1.2.2 NAME 'jimPerson' DESC 'Extended person with employment end date' SUP inetOrgPerson STRUCTURAL MAY ( jimEmployeeEndDate $ jimLeaverCohort ) )
olcObjectClasses: ( 1.3.6.1.4.1.99999.1.2.3 NAME 'jimBadgeHolder' DESC 'JIM-owned auxiliary class for badge details' SUP top AUXILIARY MUST jimBadgeNumber MAY ( jimBadgeColour $ jimBadgeIssued $ uid ) )
olcDitContentRules: ( 1.3.6.1.4.1.99999.1.2.2 NAME 'jimPersonContent' DESC 'Permits jimBadgeHolder on jimPerson entries' AUX jimBadgeHolder )
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
LDIF
# No olcAccess set here deliberately: the database falls back to slapd's
# hardcoded default ("to * by * read") only for the few minutes it takes this
# script to finish provisioning it. The real access-control set (issue
# #1715) is applied further down, once the service account exists, replacing
# this database's rules with the JIM-scoped set from acl/jim-service-account-access.ldif.

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

# Delegated JIM service account for Glitterband (issue #1715), mirroring the
# one Bitnami's bootstrap LDIF adds for Yellowstone. Password is
# "Svc-Jim@123!", deliberately different from the admin password so an
# accidental rootDN bind by a Connected System does not pass unnoticed.
# Hashed here (rather than a hardcoded hash as in the Yellowstone LDIF)
# because this script already has a live slapd and slappasswd to hand.
SVC_JIM_PW="Svc-Jim@123!"
HASHED_SVC_JIM_PW=$($SLAPPASSWD -s "$SVC_JIM_PW")

# Populate the second database with root entry, base OUs and the service
# account. ou=Policies and its ppolicy default policy are added further
# down, once the ppolicy overlay is attached to this database.
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

dn: ou=Services,dc=glitterband,dc=local
objectClass: organizationalUnit
ou: Services

dn: cn=svc-jim,ou=Services,dc=glitterband,dc=local
objectClass: organizationalRole
objectClass: simpleSecurityObject
cn: svc-jim
description: Delegated account JIM's Connected Systems bind as (#1715). Not for interactive or administrative use.
userPassword: ${HASHED_SVC_JIM_PW}

dn: cn=jim,ou=Services,dc=glitterband,dc=local
objectClass: groupOfNames
cn: jim
description: JIM service accounts permitted to manage dc=glitterband,dc=local (#1715). Membership, not the ACL, controls which accounts this grants.
member: cn=svc-jim,ou=Services,dc=glitterband,dc=local
member: cn=svc-jim-partitions,ou=Services,dc=yellowstone,dc=local
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

# Load the password policy overlay (slapo-ppolicy) and attach it to BOTH databases.
#
# The overlay is attached with NO default policy (no olcPPolicyDefault) and no policy entries
# exist in the image yet, so it enforces nothing at this point: with no policy in effect it only
# stamps pwdChangedTime on password writes, which no scenario reads. Further down, once the JIM
# service account's password policy entries exist (jim-password-policy.ldif), the overlay on each
# suffix is pointed at that suffix's default policy (minimum length 7, quality checking), matching
# the Samba domain. Scenario 022 (Populate-OpenLDAP-Scenario-022.ps1) later repoints the Yellowstone
# overlay at its own stricter policy entry for its own run; every other scenario keeps the lab
# default. What the image provides here is the ppolicy request control in the rootDSE's
# supportedControl, which is what JIM's OpenLDAP password policy reader looks for before it reads
# anything (#1702).
#
# OpenLDAP 2.5 and later build the ppolicy schema into the overlay; 2.4 shipped it as a separate
# ppolicy.ldif under the schema directory, which must be loaded before the overlay will start.
# The Dockerfile pins bitnamilegacy/openldap:latest (2.6 at the time of writing), so the schema
# file is not expected, but the version is not asserted: the file is loaded if it is there and
# the version is logged either way, so a base image change shows up in the build log.
#
# *** CHANGING THIS FILE CHANGES THE BASE IMAGE HASH. *** Get-OpenLDAPBuildHash.ps1 hashes every
# fixture file into the jim.openldap.build-hash label, Run-IntegrationTests.ps1 rebuilds the base
# image when that label no longer matches, and every OpenLDAP snapshot records the base hash it was
# baked from, so every existing OpenLDAP snapshot is invalidated and rebuilt on its next use
# (Build-OpenLDAPSnapshots.ps1). That is the intended way to roll a base change out.
echo "[openldap-init] Loading the ppolicy overlay module..."
echo "[openldap-init] slapd version: $($SLAPD -VV 2>&1 | head -1)"

PPOLICY_MODULE_PATH="/opt/bitnami/openldap/lib/openldap"
PPOLICY_SCHEMA_LDIF="/opt/bitnami/openldap/etc/schema/ppolicy.ldif"

if [ -f "$PPOLICY_SCHEMA_LDIF" ]; then
    echo "[openldap-init] ppolicy.ldif found (OpenLDAP 2.4 layout); loading the ppolicy schema..."
    ldapadd -x -H "$LDAP_URI" -D "$CONFIG_ADMIN_DN" -w "$CONFIG_ADMIN_PW" -f "$PPOLICY_SCHEMA_LDIF"
    echo "[openldap-init] ppolicy schema loaded"
else
    echo "[openldap-init] No ppolicy.ldif under the schema directory (OpenLDAP 2.5+ builds the ppolicy schema into the overlay)"
fi

if [ -f "$PPOLICY_MODULE_PATH/ppolicy.so" ]; then
    ldapadd -x -H "$LDAP_URI" -D "$CONFIG_ADMIN_DN" -w "$CONFIG_ADMIN_PW" <<PPMODULE
dn: cn=module,cn=config
objectClass: olcModuleList
cn: module
olcModulePath: ${PPOLICY_MODULE_PATH}
olcModuleLoad: ppolicy.so
PPMODULE
    echo "[openldap-init] ppolicy module loaded from ${PPOLICY_MODULE_PATH}/ppolicy.so"
else
    echo "[openldap-init] ${PPOLICY_MODULE_PATH}/ppolicy.so not found; assuming the overlay is compiled into slapd (the overlay add below fails if it is not)"
fi

for DB_DN in "$YELLOWSTONE_DB_DN" "$GLITTERBAND_DB_DN"; do
    if [ -z "$DB_DN" ]; then
        continue
    fi
    echo "[openldap-init] Adding ppolicy overlay (no default policy yet) to $DB_DN..."
    ldapadd -x -H "$LDAP_URI" -D "$CONFIG_ADMIN_DN" -w "$CONFIG_ADMIN_PW" <<PPOVERLAY
dn: olcOverlay=ppolicy,$DB_DN
objectClass: olcOverlayConfig
objectClass: olcPPolicyConfig
olcOverlay: ppolicy
PPOVERLAY
    echo "[openldap-init] ppolicy overlay added to $DB_DN"
done

# slapd assigns the new overlay an ordinal RDN (olcOverlay={0}ppolicy,...) on add; the unindexed
# name used above is only accepted there because slapd is resolving an X-ORDERED RDN for an ADD.
# A later ldapmodify against the same unindexed name gets "No such object (32)", so look each
# overlay's real DN up by its distinguishing objectClass rather than guessing the index.
YELLOWSTONE_PPOLICY_OVERLAY_DN=""
GLITTERBAND_PPOLICY_OVERLAY_DN=""
if [ -n "$YELLOWSTONE_DB_DN" ]; then
    YELLOWSTONE_PPOLICY_OVERLAY_DN=$(ldapsearch -x -H "$LDAP_URI" -D "$CONFIG_ADMIN_DN" -w "$CONFIG_ADMIN_PW" \
        -b "$YELLOWSTONE_DB_DN" "(objectClass=olcPPolicyConfig)" dn -LLL 2>/dev/null | grep "^dn:" | head -1 | sed 's/^dn: //')
fi
if [ -n "$GLITTERBAND_DB_DN" ]; then
    GLITTERBAND_PPOLICY_OVERLAY_DN=$(ldapsearch -x -H "$LDAP_URI" -D "$CONFIG_ADMIN_DN" -w "$CONFIG_ADMIN_PW" \
        -b "$GLITTERBAND_DB_DN" "(objectClass=olcPPolicyConfig)" dn -LLL 2>/dev/null | grep "^dn:" | head -1 | sed 's/^dn: //')
fi
echo "[openldap-init] ppolicy overlay DNs: Yellowstone=$YELLOWSTONE_PPOLICY_OVERLAY_DN Glitterband=$GLITTERBAND_PPOLICY_OVERLAY_DN"

# Apply the JIM service account's access control and password policy (issue
# #1715). Single-sourced from acl/*.ldif (copied into the image by the
# Dockerfile): the same files are published verbatim in
# docs/connectors/jim-ldap-connector.md as the customer recipe, so the lab
# runs exactly what customers are told to set up. Templated with
# placeholders substituted here via sed; applied once both suffixes' cn=jim
# groups (created above, in the bootstrap LDIF and this script's Glitterband
# block) and all three database DNs (Yellowstone, Glitterband, accesslog) are
# known. Access is granted to each suffix's cn=jim group, not to an
# individual service account's DN, so this lives in cn=config, which the
# snapshot images preserve, so there is nothing for start-openldap.sh to
# reconcile at container start.
#
# The ppolicy overlays attached above still have no default policy at this point; the last two
# steps below (jim-ppolicy-overlay.ldif) point each one at the policy entries this section creates.
ACL_DIR="/acl"
YELLOWSTONE_GROUP_DN="cn=jim,ou=Services,dc=yellowstone,dc=local"
GLITTERBAND_GROUP_DN="cn=jim,ou=Services,dc=glitterband,dc=local"

# Applies an acl/*.ldif template, substituting placeholders and binding as
# the given identity. Two different binds are needed across these files:
# cn=config entries (the olcAccess and olcOverlay templates) require the
# configuration rootDN; ordinary suffix data (the pwdPolicy entries) requires
# that suffix's own rootDN. Neither bind can write the other's entries.
apply_ldif_template() {
    local bind_dn="$1" bind_pw="$2" template="$3"
    shift 3
    # Files with no placeholders (e.g. the frontend rules) are called with no
    # substitutions; a bare `sed FILE` with no -e would then treat FILE's
    # path as the script instead of the input, so use cat in that case.
    if [ "$#" -eq 0 ]; then
        cat "$ACL_DIR/$template" | ldapmodify -x -H "$LDAP_URI" -D "$bind_dn" -w "$bind_pw"
        return
    fi
    local sed_args=()
    for expr in "$@"; do
        sed_args+=(-e "$expr")
    done
    sed "${sed_args[@]}" "$ACL_DIR/$template" \
        | ldapmodify -x -H "$LDAP_URI" -D "$bind_dn" -w "$bind_pw"
}

if [ -n "$YELLOWSTONE_DB_DN" ] && [ -n "$GLITTERBAND_DB_DN" ] && [ -n "$ACCESSLOG_DB_DN" ] \
    && [ -n "$YELLOWSTONE_PPOLICY_OVERLAY_DN" ] && [ -n "$GLITTERBAND_PPOLICY_OVERLAY_DN" ]; then
    echo "[openldap-init] Applying JIM service account access control (Yellowstone)..."
    apply_ldif_template "$CONFIG_ADMIN_DN" "$CONFIG_ADMIN_PW" "jim-service-account-access.ldif" \
        "s#__DB_DN__#$YELLOWSTONE_DB_DN#g" \
        "s#__SUFFIX__#dc=yellowstone,dc=local#g"

    echo "[openldap-init] Applying JIM service account access control (Glitterband)..."
    apply_ldif_template "$CONFIG_ADMIN_DN" "$CONFIG_ADMIN_PW" "jim-service-account-access.ldif" \
        "s#__DB_DN__#$GLITTERBAND_DB_DN#g" \
        "s#__SUFFIX__#dc=glitterband,dc=local#g"

    # The service accounts are not rootDNs, so each suffix's default size limit
    # (500) would stop a paged import at the 500th object. Exempt the JIM group
    # on each suffix (acl/jim-service-account-limits.ldif); the accesslog
    # database is handled above by its unlimited olcSizeLimit.
    echo "[openldap-init] Applying JIM service account search limits (Yellowstone)..."
    apply_ldif_template "$CONFIG_ADMIN_DN" "$CONFIG_ADMIN_PW" "jim-service-account-limits.ldif" \
        "s#__DB_DN__#$YELLOWSTONE_DB_DN#g" \
        "s#__SUFFIX__#dc=yellowstone,dc=local#g"

    echo "[openldap-init] Applying JIM service account search limits (Glitterband)..."
    apply_ldif_template "$CONFIG_ADMIN_DN" "$CONFIG_ADMIN_PW" "jim-service-account-limits.ldif" \
        "s#__DB_DN__#$GLITTERBAND_DB_DN#g" \
        "s#__SUFFIX__#dc=glitterband,dc=local#g"

    echo "[openldap-init] Applying JIM frontend (rootDSE/subschema) access control..."
    apply_ldif_template "$CONFIG_ADMIN_DN" "$CONFIG_ADMIN_PW" "jim-frontend-access.ldif"

    echo "[openldap-init] Applying JIM accesslog access control (both suffixes' groups)..."
    apply_ldif_template "$CONFIG_ADMIN_DN" "$CONFIG_ADMIN_PW" "jim-accesslog-access.ldif" \
        "s#__DB_DN__#$ACCESSLOG_DB_DN#g" \
        "s#__JIM_GROUP_DN_2__#$GLITTERBAND_GROUP_DN#g" \
        "s#__JIM_GROUP_DN__#$YELLOWSTONE_GROUP_DN#g"

    echo "[openldap-init] Applying JIM password policy entries (Yellowstone)..."
    apply_ldif_template "cn=admin,dc=yellowstone,dc=local" "$DATA_ADMIN_PW" "jim-password-policy.ldif" \
        "s#__SUFFIX__#dc=yellowstone,dc=local#g"

    echo "[openldap-init] Applying JIM password policy entries (Glitterband)..."
    apply_ldif_template "cn=admin,dc=glitterband,dc=local" "$DATA_ADMIN_PW" "jim-password-policy.ldif" \
        "s#__SUFFIX__#dc=glitterband,dc=local#g"

    echo "[openldap-init] Pointing ppolicy overlay's default policy (Yellowstone)..."
    apply_ldif_template "$CONFIG_ADMIN_DN" "$CONFIG_ADMIN_PW" "jim-ppolicy-overlay.ldif" \
        "s#__OVERLAY_DN__#$YELLOWSTONE_PPOLICY_OVERLAY_DN#g" \
        "s#__SUFFIX__#dc=yellowstone,dc=local#g"

    echo "[openldap-init] Pointing ppolicy overlay's default policy (Glitterband)..."
    apply_ldif_template "$CONFIG_ADMIN_DN" "$CONFIG_ADMIN_PW" "jim-ppolicy-overlay.ldif" \
        "s#__OVERLAY_DN__#$GLITTERBAND_PPOLICY_OVERLAY_DN#g" \
        "s#__SUFFIX__#dc=glitterband,dc=local#g"

    echo "[openldap-init] JIM service account access control and password policy applied"
else
    echo "[openldap-init] ERROR: one or more database or ppolicy overlay DNs (Yellowstone/Glitterband/accesslog database; Yellowstone/Glitterband ppolicy overlay) not found; cannot apply access control or password policy" >&2
    exit 1
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
