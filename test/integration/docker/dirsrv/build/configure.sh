#!/bin/bash
# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.
#
# Build-time configuration of the JIM 389 Directory Server lab image.
#
# Runs once, inside `docker build` (see ../Dockerfile), following upstream's
# ds-container sample: start dscontainer in the background, wait for it,
# configure over LDAP as cn=Directory Manager (dsconf uses LDAPI as root), stop
# it cleanly. What it leaves under /data is the image's state; the Dockerfile
# header lists it. Every step that matters is then checked, as the JIM service
# account where that is the point, and a failed check fails the build: a lab
# image that looks fine but silently lacks its changelog or its ACIs is worse
# than no image.
#
# Three things here contradict what the 389 documentation might lead you to
# expect, all verified against 389-Directory/3.1.2 (the 389ds/dirsrv:3.1 image):
#   - DS_SUFFIX_NAME does not create a backend. dscontainer only records it as
#     the default basedn in /data/config/container.inf, so both suffixes are
#     created here explicitly with `dsconf backend create --create-suffix`.
#   - cn=schema refuses dITContentRules over LDAP ("Only object classes and
#     attribute types may be added"), so the jimPersonContent rule the OpenLDAP
#     lab defines is not loaded here; schema/jim-extensions.ldif says so too.
#   - The RFC 3062 Password Modify operation is refused over plain LDAP with
#     "Confidentiality required (13)" and there is no switch to allow it, so
#     the image carries a certificate for LDAPS (create_lab_tls below).
#
# *** CHANGING ANY FILE UNDER docker/dirsrv/ CHANGES THE IMAGE HASH. ***
# Get-DirsrvBuildHash.ps1 hashes every file here (except the two .ps1 scripts)
# into the jim.dirsrv.build-hash label, which is how the runner knows to rebuild.

set -euo pipefail

DSCONTAINER="/usr/lib/dirsrv/dscontainer"
LDAP_URI="ldap://localhost:3389"
LDAPS_URI="ldaps://localhost:3636"
DM_DN="cn=Directory Manager"
DM_PW="${DS_DM_PASSWORD:?DS_DM_PASSWORD must be set for the build}"

BOOTSTRAP_DIR="/bootstrap"
SCHEMA_DIR="/schema"
ACI_DIR="/aci"
DATA_DIR="/data"
PROVISIONED_DIR="/data.provisioned"
TLS_DIR="$DATA_DIR/tls"
LAB_CA_CERT="$TLS_DIR/ca/jim-dirsrv-lab-ca.crt"
SLAPD_PID_FILE="$DATA_DIR/run/slapd-localhost.pid"

YELLOWSTONE_SUFFIX="dc=yellowstone,dc=local"
GLITTERBAND_SUFFIX="dc=glitterband,dc=local"
YELLOWSTONE_GROUP_DN="cn=jim,ou=Services,$YELLOWSTONE_SUFFIX"
GLITTERBAND_GROUP_DN="cn=jim,ou=Services,$GLITTERBAND_SUFFIX"
YELLOWSTONE_SVC_DN="cn=svc-jim,ou=Services,$YELLOWSTONE_SUFFIX"
GLITTERBAND_SVC_DN="cn=svc-jim,ou=Services,$GLITTERBAND_SUFFIX"
PARTITIONS_SVC_DN="cn=svc-jim-partitions,ou=Services,$YELLOWSTONE_SUFFIX"
# Lab-only passwords, published in test/integration/utils/Test-Helpers.ps1.
SVC_JIM_PW="Svc-Jim@123!"
SVC_JIM_PARTITIONS_PW="Svc-Jim-Partitions@123!"
RETROCL_DN="cn=Retro Changelog Plugin,cn=plugins,cn=config"

log()  { echo "[dirsrv-build] $*"; }
fail() { echo "[dirsrv-build] ERROR: $*" >&2; exit 1; }

# ---- LDAP helpers -----------------------------------------------------------------------
dm_modify()   { ldapmodify -x -H "$LDAP_URI" -D "$DM_DN" -w "$DM_PW" "$@"; }
dm_search()   { ldapsearch -x -LLL -o ldif-wrap=no -H "$LDAP_URI" -D "$DM_DN" -w "$DM_PW" "$@"; }
anon_search() { ldapsearch -x -LLL -o ldif-wrap=no -H "$LDAP_URI" "$@"; }
as_search()   { local dn="$1" pw="$2"; shift 2; ldapsearch -x -LLL -o ldif-wrap=no -H "$LDAP_URI" -D "$dn" -w "$pw" "$@"; }
as_modify()   { local dn="$1" pw="$2"; shift 2; ldapmodify -x -H "$LDAP_URI" -D "$dn" -w "$pw" "$@"; }

# Applies a templated LDIF as Directory Manager, substituting placeholders with
# sed exactly as the OpenLDAP lab's apply_ldif_template does. With no
# expressions the file is applied as it is.
apply_ldif_template() {
    local template="$1"; shift
    if [ "$#" -eq 0 ]; then
        dm_modify -f "$template"
        return
    fi
    local sed_args=()
    local expr
    for expr in "$@"; do sed_args+=(-e "$expr"); done
    sed "${sed_args[@]}" "$template" | dm_modify
}

# ---- Server lifecycle -------------------------------------------------------------------
DS_PID=""

start_server() {
    log "Starting dscontainer..."
    "$DSCONTAINER" -r &
    DS_PID=$!
    local i
    for i in $(seq 1 120); do
        if ! kill -0 "$DS_PID" 2>/dev/null; then
            fail "dscontainer exited during start-up"
        fi
        # -H is the image's own healthcheck (LDAPI as root). The bind on top of
        # it proves the Directory Manager password has been applied, which
        # dscontainer does a moment after the healthcheck first passes.
        if "$DSCONTAINER" -H >/dev/null 2>&1 \
            && ldapwhoami -x -H "$LDAP_URI" -D "$DM_DN" -w "$DM_PW" >/dev/null 2>&1; then
            log "Server ready after $((i * 2)) s"
            return 0
        fi
        sleep 2
    done
    fail "Server did not become ready within 240 s"
}

stop_server() {
    log "Stopping dscontainer (SIGTERM)..."
    kill -15 "$DS_PID"
    local rc=0
    wait "$DS_PID" || rc=$?
    log "dscontainer exited with status $rc"
    # ns-slapd removes its pid file on a clean shutdown; wait for that rather
    # than trusting dscontainer's exit alone.
    local i
    for i in $(seq 1 30); do
        if [ ! -f "$SLAPD_PID_FILE" ] || ! kill -0 "$(cat "$SLAPD_PID_FILE")" 2>/dev/null; then
            DS_PID=""
            return 0
        fi
        sleep 1
    done
    fail "ns-slapd is still running 30 s after dscontainer exited"
}

# ---- Configuration steps ----------------------------------------------------------------
create_lab_tls() {
    # A lab CA and a server certificate naming the compose service (dirsrv-primary)
    # and localhost. dscontainer imports /data/tls/server.{key,crt} and every
    # /data/tls/ca/*.crt into its NSS database on every start (its PEM TLS hook),
    # in place of the self-signed certificate it would otherwise mint for the
    # build container's random hostname. JIM validates directory certificates
    # for real, so a Connected System on LDAPS needs a name it can verify; the
    # CA certificate stays at $LAB_CA_CERT for a harness to copy out and trust.
    # The CA's private key is discarded: a different hostname means a rebuild.
    log "Creating the lab CA and a server certificate for dirsrv-primary under $TLS_DIR..."
    mkdir -p "$TLS_DIR/ca"
    local ca_key="/tmp/jim-dirsrv-lab-ca.key" csr="/tmp/server.csr" ext="/tmp/server.ext"
    openssl req -x509 -newkey rsa:3072 -nodes -sha256 -days 3650 \
        -keyout "$ca_key" -out "$LAB_CA_CERT" \
        -subj "/O=JIM Integration Tests/CN=JIM 389 Directory Server Lab CA" >/dev/null 2>&1
    openssl req -newkey rsa:3072 -nodes -sha256 \
        -keyout "$TLS_DIR/server.key" -out "$csr" \
        -subj "/O=JIM Integration Tests/CN=dirsrv-primary" >/dev/null 2>&1
    printf 'subjectAltName=DNS:dirsrv-primary,DNS:localhost\nextendedKeyUsage=serverAuth\n' > "$ext"
    openssl x509 -req -sha256 -days 3650 -in "$csr" \
        -CA "$LAB_CA_CERT" -CAkey "$ca_key" -CAcreateserial \
        -out "$TLS_DIR/server.crt" -extfile "$ext" >/dev/null 2>&1
    rm -f "$ca_key" "$csr" "$ext" "$TLS_DIR/ca/"*.srl
    chmod 600 "$TLS_DIR/server.key"
}

create_backends() {
    log "Creating the yellowstone and glitterband backends (with their suffix entries)..."
    dsconf localhost backend create --suffix "$YELLOWSTONE_SUFFIX" --be-name yellowstone --create-suffix
    dsconf localhost backend create --suffix "$GLITTERBAND_SUFFIX" --be-name glitterband --create-suffix
}

load_schema() {
    log "Loading the jim-extensions schema into cn=schema..."
    dm_modify -f "$SCHEMA_DIR/jim-extensions.ldif"
}

bootstrap_suffix() {
    local suffix="$1"
    log "Bootstrapping $suffix (ou=People, ou=Groups, ou=Services, cn=svc-jim, cn=jim)..."
    apply_ldif_template "$BOOTSTRAP_DIR/01-suffix-tree.ldif" "s#__SUFFIX__#$suffix#g"
    # --create-suffix left an ACI granting anyone read of the suffix root's dc,
    # description and objectClass. The OpenLDAP lab withdraws anonymous read of
    # everything but the rootDSE and subschema, and so does this one: only what
    # a JIM service account has been granted is readable. Removing it here,
    # before the JIM rules are added, leaves the suffix with exactly the rules
    # in aci/jim-suffix-access.ldif (lab hardening; not part of the recipe).
    dm_modify <<LDIF
dn: $suffix
changetype: modify
delete: aci
LDIF
}

apply_suffix_acis() {
    local suffix="$1"
    log "Applying JIM access control to $suffix..."
    apply_ldif_template "$ACI_DIR/jim-suffix-access.ldif" \
        "s#__SUFFIX__#$suffix#g" \
        "s#__JIM_GROUP_DN__#cn=jim,ou=Services,$suffix#g"
}

apply_changelog_and_config_acis() {
    log "Applying JIM access control to cn=changelog and cn=config..."
    apply_ldif_template "$ACI_DIR/jim-changelog-access.ldif" \
        "s#__JIM_GROUP_DN__#$YELLOWSTONE_GROUP_DN#g" \
        "s#__JIM_GROUP_DN_2__#$GLITTERBAND_GROUP_DN#g"
    apply_ldif_template "$ACI_DIR/jim-config-access.ldif" \
        "s#__JIM_GROUP_DN__#$YELLOWSTONE_GROUP_DN#g" \
        "s#__JIM_GROUP_DN_2__#$GLITTERBAND_GROUP_DN#g"
}

ensure_eq_index() {
    # dscontainer's new backends already index cn, uid and entryUUID for
    # equality; this makes the build fail loudly (or repair) if a base image
    # change ever stops doing so, since JIM's imports search on all three.
    local backend="$1" attr="$2"
    local index_dn="cn=$attr,cn=index,cn=$backend,cn=ldbm database,cn=plugins,cn=config"
    if dm_search -b "$index_dn" -s base nsIndexType 2>/dev/null | grep -qi '^nsIndexType: eq$'; then
        log "Index present: $backend/$attr (eq)"
        return 0
    fi
    log "Index missing: $backend/$attr (eq); adding and reindexing..."
    dsconf localhost backend index add --attr "$attr" --index-type eq --reindex "$backend"
}

enable_retro_changelog() {
    log "Enabling the Retro Changelog plug-in (7 day retention, userPassword excluded, deleted entries logged)..."
    dsconf localhost plugin retro-changelog enable
    dsconf localhost plugin retro-changelog set --max-age 7d --exclude-attrs userPassword
    # nsslapd-log-deleted is not exposed by dsconf, so it is set directly. With
    # it on, a delete record's "changes" value carries the deleted entry's
    # attributes (entryUUID and objectClass included), so a reader of the
    # changelog can tell which object a delete was, not just which DN.
    dm_modify <<LDIF
dn: $RETROCL_DN
changetype: modify
replace: nsslapd-log-deleted
nsslapd-log-deleted: on
LDIF
}

# ---- Build-time checks ------------------------------------------------------------------
# Each one fails the build with a message naming what is missing. They run as
# the JIM service accounts wherever the point is what those accounts can do.
run_checks() {
    log "Running build-time checks..."
    local out

    out=$(anon_search -b "" -s base '+' changelog firstchangenumber lastchangenumber vendorVersion)
    grep -qi '^changelog: cn=changelog$' <<<"$out" || fail "the rootDSE does not advertise 'changelog: cn=changelog'"
    grep -qi '^lastchangenumber: ' <<<"$out"      || fail "the rootDSE has no lastchangenumber"
    log "OK: rootDSE advertises the changelog ($(grep -i '^vendorVersion' <<<"$out"))"

    out=$(dm_search -b "$RETROCL_DN" -s base nsslapd-pluginEnabled nsslapd-log-deleted nsslapd-changelogmaxage)
    grep -qi '^nsslapd-pluginEnabled: on$' <<<"$out" || fail "the Retro Changelog plug-in is not enabled"
    grep -qi '^nsslapd-log-deleted: on$' <<<"$out"   || fail "nsslapd-log-deleted is not on"
    log "OK: Retro Changelog plug-in enabled with deleted entries logged"

    ldapwhoami -x -H "$LDAP_URI" -D "$YELLOWSTONE_SVC_DN" -w "$SVC_JIM_PW" >/dev/null            || fail "$YELLOWSTONE_SVC_DN cannot bind"
    ldapwhoami -x -H "$LDAP_URI" -D "$GLITTERBAND_SVC_DN" -w "$SVC_JIM_PW" >/dev/null            || fail "$GLITTERBAND_SVC_DN cannot bind"
    ldapwhoami -x -H "$LDAP_URI" -D "$PARTITIONS_SVC_DN" -w "$SVC_JIM_PARTITIONS_PW" >/dev/null || fail "$PARTITIONS_SVC_DN cannot bind"
    log "OK: all three service accounts bind"

    local a
    out=$(dm_search -b "$YELLOWSTONE_SVC_DN" -s base nsSizeLimit nsLookThroughLimit nsPagedSizeLimit)
    for a in nsSizeLimit nsLookThroughLimit nsPagedSizeLimit; do
        grep -qi "^$a: -1$" <<<"$out" || fail "$a is not -1 on $YELLOWSTONE_SVC_DN"
    done
    log "OK: search limits lifted on the service accounts"

    out=$(as_search "$YELLOWSTONE_SVC_DN" "$SVC_JIM_PW" -b "ou=People,$YELLOWSTONE_SUFFIX" -s base entryUUID)
    grep -qi '^entryUUID: ' <<<"$out" || fail "svc-jim cannot read entryUUID on ou=People,$YELLOWSTONE_SUFFIX"
    out=$(as_search "$PARTITIONS_SVC_DN" "$SVC_JIM_PARTITIONS_PW" -b "ou=People,$GLITTERBAND_SUFFIX" -s base entryUUID)
    grep -qi '^entryUUID: ' <<<"$out" || fail "svc-jim-partitions cannot read ou=People,$GLITTERBAND_SUFFIX"
    out=$(as_search "$GLITTERBAND_SVC_DN" "$SVC_JIM_PW" -b "ou=People,$YELLOWSTONE_SUFFIX" -s base dn 2>&1 || true)
    if grep -qi '^dn: ' <<<"$out"; then fail "Glitterband's svc-jim can read Yellowstone; suffix isolation is broken"; fi
    out=$(anon_search -b "$YELLOWSTONE_SUFFIX" -s base dn 2>&1 || true)
    if grep -qi '^dn: ' <<<"$out"; then fail "anonymous can read the suffix root; the default ACI was not removed"; fi
    log "OK: service accounts read their own suffixes (entryUUID included), nothing else and nobody else"

    out=$(anon_search -b cn=schema -s base objectClasses attributeTypes)
    for a in jimPerson jimGroup jimBadgeHolder jimGroupType jimGroupStatus jimEmployeeEndDate jimLeaverCohort jimBadgeNumber jimBadgeColour jimBadgeIssued; do
        grep -q "NAME '$a'" <<<"$out" || fail "cn=schema lacks $a"
    done
    log "OK: jim-extensions schema present"

    out=$(as_search "$GLITTERBAND_SVC_DN" "$SVC_JIM_PW" -b cn=config -s base passwordCheckSyntax passwordMinLength passwordExp)
    grep -qi '^passwordCheckSyntax: ' <<<"$out" || fail "svc-jim cannot read the global password policy on cn=config"
    out=$(as_search "$GLITTERBAND_SVC_DN" "$SVC_JIM_PW" -b cn=config -s base nsslapd-rootpw nsslapd-port nsslapd-localhost)
    if grep -qi '^nsslapd-' <<<"$out"; then fail "svc-jim can read cn=config attributes outside the password policy"; fi
    # nsslapd-log-deleted has no schema definition, so the ACI cannot name it and
    # only Directory Manager reads it (checked above); the two settings JIM can
    # act on are readable by the service account.
    out=$(as_search "$YELLOWSTONE_SVC_DN" "$SVC_JIM_PW" -b "$RETROCL_DN" -s base nsslapd-pluginEnabled nsslapd-changelogmaxage)
    grep -qi '^nsslapd-pluginEnabled: on$' <<<"$out" || fail "svc-jim cannot read the Retro Changelog plug-in settings"
    grep -qi '^nsslapd-changelogmaxage: 7d$' <<<"$out" || fail "svc-jim cannot read nsslapd-changelogmaxage (or it is not 7d)"
    log "OK: cn=config password policy and plug-in settings readable, nothing else"

    # Export rights, end to end, as svc-jim under ou=People; then the changelog
    # must show the delete, with the deleted entry's attributes.
    local probe_rdn="uid=jim-build-probe" renamed_rdn="uid=jim-build-probe-renamed"
    local probe_dn="$probe_rdn,ou=People,$YELLOWSTONE_SUFFIX" renamed_dn="$renamed_rdn,ou=People,$YELLOWSTONE_SUFFIX"
    ldapadd -x -H "$LDAP_URI" -D "$YELLOWSTONE_SVC_DN" -w "$SVC_JIM_PW" >/dev/null <<LDIF || fail "svc-jim cannot add under ou=People"
dn: $probe_dn
objectClass: top
objectClass: jimPerson
uid: jim-build-probe
cn: Build Probe
sn: Probe
jimEmployeeEndDate: 20991231235959Z
LDIF
    as_modify "$YELLOWSTONE_SVC_DN" "$SVC_JIM_PW" >/dev/null <<LDIF || fail "svc-jim cannot modify under ou=People"
dn: $probe_dn
changetype: modify
replace: description
description: modified by the build check
LDIF
    ldapmodrdn -x -H "$LDAP_URI" -D "$YELLOWSTONE_SVC_DN" -w "$SVC_JIM_PW" -r "$probe_dn" "$renamed_rdn" >/dev/null \
        || fail "svc-jim cannot rename under ou=People"
    LDAPTLS_CACERT="$LAB_CA_CERT" ldappasswd -x -H "$LDAPS_URI" -D "$YELLOWSTONE_SVC_DN" -w "$SVC_JIM_PW" -s 'Build-Probe@1' "$renamed_dn" >/dev/null \
        || fail "svc-jim cannot set a password with Password Modify over LDAPS (validated against the lab CA)"
    ldapwhoami -x -H "$LDAP_URI" -D "$renamed_dn" -w 'Build-Probe@1' >/dev/null \
        || fail "the password set by Password Modify does not bind"
    if ldappasswd -x -H "$LDAP_URI" -D "$YELLOWSTONE_SVC_DN" -w "$SVC_JIM_PW" -s 'Build-Probe@2' "$renamed_dn" >/dev/null 2>&1; then
        log "NOTE: this build accepts Password Modify over plain LDAP; the LDAPS requirement in the Dockerfile header no longer applies"
    else
        log "OK: Password Modify is refused over plain LDAP and works over LDAPS, as documented"
    fi
    if as_modify "$YELLOWSTONE_SVC_DN" "$SVC_JIM_PW" >/dev/null 2>&1 <<LDIF; then fail "svc-jim can write ou=Services; the ACI set is too wide"; fi
dn: ou=Services,$YELLOWSTONE_SUFFIX
changetype: modify
replace: description
description: must be refused
LDIF
    ldapdelete -x -H "$LDAP_URI" -D "$YELLOWSTONE_SVC_DN" -w "$SVC_JIM_PW" "$renamed_dn" || fail "svc-jim cannot delete under ou=People"
    log "OK: svc-jim can add, modify, rename, set a password and delete under ou=People, and cannot write ou=Services"

    out=$(as_search "$YELLOWSTONE_SVC_DN" "$SVC_JIM_PW" -b cn=changelog -s one "(&(changeType=delete)(targetDn=$renamed_dn))" changeNumber changeType targetDn changes)
    grep -qi '^changeType: delete$' <<<"$out" || fail "svc-jim finds no delete record for $renamed_dn in cn=changelog (got: $out)"
    local changes
    changes=$(grep -i '^changes:: ' <<<"$out" | head -1 | sed 's/^[Cc]hanges:: //' | base64 -d 2>/dev/null || true)
    if [ -z "$changes" ]; then
        changes=$(grep -i '^changes: ' <<<"$out" | head -1 | sed 's/^[Cc]hanges: //' || true)
    fi
    grep -qi '^entryUUID: ' <<<"$changes"   || fail "the delete record's changes carry no entryUUID (nsslapd-log-deleted not effective); record: $out"
    grep -qi '^objectClass: ' <<<"$changes" || fail "the delete record's changes carry no objectClass; record: $out"
    log "OK: cn=changelog readable as svc-jim; the delete record carries the deleted entry's entryUUID and objectClass"

    out=$(echo | openssl s_client -connect localhost:3636 -servername dirsrv-primary -CAfile "$LAB_CA_CERT" 2>&1 || true)
    grep -q 'Verify return code: 0 (ok)' <<<"$out" || fail "the LDAPS certificate does not verify against the lab CA"
    openssl x509 -in "$TLS_DIR/server.crt" -noout -ext subjectAltName | grep -q 'DNS:dirsrv-primary' || fail "the server certificate does not name dirsrv-primary"
    log "OK: LDAPS presents the lab certificate for dirsrv-primary"
}

# ---- Finalisation -----------------------------------------------------------------------
finalise() {
    # Runtime files are recreated on start (and a stale socket breaks cp -a);
    # the build's logs are noise. The provisioned copy is what start-dirsrv.sh
    # restores when a container's /data holds no instance.
    rm -rf "${DATA_DIR:?}/run/"* "${DATA_DIR:?}/logs/"*
    rm -rf "$PROVISIONED_DIR"
    cp -a "$DATA_DIR" "$PROVISIONED_DIR"
    log "Provisioned copy written to $PROVISIONED_DIR ($(du -sh "$PROVISIONED_DIR" | cut -f1))"
}

main() {
    log "389 Directory Server lab: build-time configuration starting"
    create_lab_tls
    start_server
    create_backends
    load_schema
    bootstrap_suffix "$YELLOWSTONE_SUFFIX"
    bootstrap_suffix "$GLITTERBAND_SUFFIX"
    log "Adding cn=svc-jim-partitions and making it a member of both suffixes' cn=jim groups..."
    dm_modify -f "$BOOTSTRAP_DIR/02-multi-partition-account.ldif"
    apply_suffix_acis "$YELLOWSTONE_SUFFIX"
    apply_suffix_acis "$GLITTERBAND_SUFFIX"
    local be attr
    for be in yellowstone glitterband; do
        for attr in uid cn entryUUID; do ensure_eq_index "$be" "$attr"; done
    done
    enable_retro_changelog
    # The plug-in (and its cn=changelog backend) only comes to life on restart;
    # the changelog ACI needs the cn=changelog entry to exist.
    stop_server
    start_server
    apply_changelog_and_config_acis
    run_checks
    stop_server
    finalise
    log "389 Directory Server lab: build-time configuration complete"
}

main "$@"
