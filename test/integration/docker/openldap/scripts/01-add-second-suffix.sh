#!/bin/bash
# Add a second MDB database (dc=regionB,dc=test) to the OpenLDAP instance.
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
REGION_B_DB_DIR="/bitnami/openldap/data/regionB"
LDAP_PORT="${LDAP_PORT_NUMBER:-1389}"
LDAP_URI="ldap://localhost:${LDAP_PORT}"

# Config admin credentials (set in docker-compose environment)
CONFIG_ADMIN_DN="cn=${LDAP_CONFIG_ADMIN_USERNAME:-admin},cn=config"
CONFIG_ADMIN_PW="${LDAP_CONFIG_ADMIN_PASSWORD:-configpassword}"

# Data admin credentials for the new suffix (same as primary)
DATA_ADMIN_PW="${LDAP_ADMIN_PASSWORD:-adminpassword}"

echo "[openldap-init] Adding second suffix: dc=regionB,dc=test"

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
olcSuffix: dc=regionB,dc=test
olcDbDirectory: ${REGION_B_DB_DIR}
olcRootDN: cn=admin,dc=regionB,dc=test
olcRootPW: ${HASHED_PW}
olcDbMaxSize: 1073741824
olcDbIndex: objectClass eq
olcDbIndex: uid eq
olcDbIndex: cn eq
olcDbIndex: entryUUID eq
olcAccess: {0}to * by dn.exact="cn=admin,dc=regionB,dc=test" manage by * read
LDIF

echo "[openldap-init] Second MDB database added to cn=config"

# Populate the second database with root entry and base OUs
echo "[openldap-init] Loading Region B base entries..."
ldapadd -x -H "$LDAP_URI" -D "cn=admin,dc=regionB,dc=test" -w "$DATA_ADMIN_PW" <<LDIF
dn: dc=regionB,dc=test
objectClass: dcObject
objectClass: organization
dc: regionB
o: Region B Test Organisation

dn: ou=People,dc=regionB,dc=test
objectClass: organizationalUnit
ou: People

dn: ou=Groups,dc=regionB,dc=test
objectClass: organizationalUnit
ou: Groups
LDIF

echo "[openldap-init] Region B base entries loaded"

# Stop slapd (Bitnami will restart it after all init scripts)
echo "[openldap-init] Stopping slapd..."
kill "$SLAPD_PID" 2>/dev/null || true
wait "$SLAPD_PID" 2>/dev/null || true

echo "[openldap-init] Multi-suffix setup complete"
echo "[openldap-init]   Suffix 1: dc=regionA,dc=test (primary)"
echo "[openldap-init]   Suffix 2: dc=regionB,dc=test (added)"
