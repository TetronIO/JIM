#!/bin/bash
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
olcDbMaxSize: 1073741824
olcDbIndex: objectClass eq
olcDbIndex: uid eq
olcDbIndex: cn eq
olcDbIndex: entryUUID eq
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

# Configure the accesslog database for production-like usage:
# 1. Increase MDB mapsize from default (~10MB) to 4GB. Without this, the
#    accesslog silently stops logging once the MDB map is full (MDB_MAP_FULL),
#    causing delta imports to miss changes. At XLarge scale (100K+ objects),
#    the initial population alone generates ~100K accesslog entries that exhaust
#    smaller limits. 4GB accommodates XLarge with multiple sync cycles.
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
olcDbMaxSize: 4294967296
-
add: olcSizeLimit
olcSizeLimit: unlimited
ALMODIFY
    echo "[openldap-init] Accesslog database configured (mapsize=4GB, sizeLimit=unlimited)"
fi

# Stop slapd (Bitnami will restart it after all init scripts)
echo "[openldap-init] Stopping slapd..."
kill "$SLAPD_PID" 2>/dev/null || true
wait "$SLAPD_PID" 2>/dev/null || true

echo "[openldap-init] Multi-suffix setup complete"
echo "[openldap-init]   Suffix 1: dc=yellowstone,dc=local (primary)"
echo "[openldap-init]   Suffix 2: dc=glitterband,dc=local (added)"
