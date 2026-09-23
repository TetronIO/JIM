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
# header lists it. The instance is also stamped with the image's build hash
# (/data/.jim-provisioned-id, from the JIM_DIRSRV_BUILD_HASH build argument)
# before it is copied to /data.provisioned, so both copies carry the same id
# and start-dirsrv.sh can tell a volume holding this build's instance from one
# holding an older build's or a snapshot's. Every step that matters is then
# checked, as the JIM service account where that is the point, and a failed
# check fails the build: a lab image that looks fine but silently lacks its
# changelog or its ACIs is worse than no image.
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
# Checked up front so a missing hash fails the build in its first second, not
# after the whole configuration has run; finalise writes it.
BUILD_HASH="${JIM_DIRSRV_BUILD_HASH:?JIM_DIRSRV_BUILD_HASH must be set for the build (Build-DirsrvImage.ps1 passes it as a build argument)}"

BOOTSTRAP_DIR="/bootstrap"
SCHEMA_DIR="/schema"
ACI_DIR="/aci"
DATA_DIR="/data"
PROVISIONED_DIR="/data.provisioned"
PROVISIONED_ID_FILE=".jim-provisioned-id"
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

set_password_policy() {
    # Mirrors the OpenLDAP lab's pwdMinLength 7 ppolicy: a password shorter than
    # seven characters is refused, which the scenarios that need a change parked
    # rely on. 389 ships passwordMinLength 8 and passwordMinCategories 3 but with
    # passwordCheckSyntax off, so nothing is enforced until the switch is on;
    # JIM's 389 password policy reader honours the same switch. Categories stay
    # at the default so the figures JIM reads are the ones the server applies.
    # With syntax checking on, 389 also applies its trivial-words rule: a
    # password is refused ("password based off of user entry") if it contains
    # any run of passwordMinTokenLength characters (case-insensitive,
    # leet-normalised) of the entry's own uid, cn, sn, givenName, ou or mail,
    # and the rule applies to a password set by the service account too. Three
    # is 389's default, and at three a shared static lab password fails
    # non-deterministically per name: Chalkstream-7-Vault! shares "Cha" with a
    # user called Charlie, so the lab refused one account of a batch and
    # accepted its neighbours. Five keeps the rule alive (customers have it on,
    # and run_checks proves it still catches "Charl") while letting the lab's
    # fixed passwords through. (--pwdmincatagories is dsconf's own spelling.)
    log "Setting the global password policy (passwordCheckSyntax on, passwordMinLength 7, passwordMinCategories 3, passwordMinTokenLength 5)..."
    dsconf localhost pwpolicy set --pwdchecksyntax on --pwdminlen 7 --pwdmincatagories 3 --pwdmintokenlen 5
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

    out=$(as_search "$GLITTERBAND_SVC_DN" "$SVC_JIM_PW" -b cn=config -s base passwordCheckSyntax passwordMinLength passwordMinCategories passwordExp)
    grep -qi '^passwordCheckSyntax: on$' <<<"$out"   || fail "svc-jim cannot read passwordCheckSyntax on cn=config, or it is not on"
    grep -qi '^passwordMinLength: 7$' <<<"$out"      || fail "svc-jim cannot read passwordMinLength on cn=config, or it is not 7"
    grep -qi '^passwordMinCategories: 3$' <<<"$out"  || fail "svc-jim cannot read passwordMinCategories on cn=config, or it is not 3"
    out=$(as_search "$GLITTERBAND_SVC_DN" "$SVC_JIM_PW" -b cn=config -s base nsslapd-rootpw nsslapd-port nsslapd-localhost)
    if grep -qi '^nsslapd-' <<<"$out"; then fail "svc-jim can read cn=config attributes outside the password policy"; fi
    # All three plug-in settings JIM reads must be readable as the service
    # account, nsslapd-log-deleted included: JIM's Delta Import readiness check
    # reads it as the account it connects as and refuses to run when it is off.
    out=$(as_search "$YELLOWSTONE_SVC_DN" "$SVC_JIM_PW" -b "$RETROCL_DN" -s base nsslapd-pluginEnabled nsslapd-changelogmaxage nsslapd-log-deleted)
    grep -qi '^nsslapd-pluginEnabled: on$' <<<"$out" || fail "svc-jim cannot read the Retro Changelog plug-in settings"
    grep -qi '^nsslapd-changelogmaxage: 7d$' <<<"$out" || fail "svc-jim cannot read nsslapd-changelogmaxage (or it is not 7d)"
    grep -qi '^nsslapd-log-deleted: on$' <<<"$out"   || fail "svc-jim cannot read nsslapd-log-deleted on the Retro Changelog plug-in (or it is not on)"
    log "OK: cn=config password policy (syntax checking on, minimum length 7) and all three plug-in settings readable as svc-jim, nothing else"

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
    # The global password policy must bite for a password set by the service
    # account: six characters is under passwordMinLength 7. The accepted probe
    # password avoids the entry's own words (see set_password_policy) and
    # carries four character classes against passwordMinCategories 3.
    out=$(LDAPTLS_CACERT="$LAB_CA_CERT" ldappasswd -x -H "$LDAPS_URI" -D "$YELLOWSTONE_SVC_DN" -w "$SVC_JIM_PW" -s 'Ab1!xy' "$renamed_dn" 2>&1 || true)
    grep -qi 'Constraint violation' <<<"$out" \
        || fail "a six-character password was not refused by the global password policy (passwordMinLength 7 not enforced); got: $out"
    log "OK: the global password policy refuses a six-character password set by svc-jim over LDAPS"
    LDAPTLS_CACERT="$LAB_CA_CERT" ldappasswd -x -H "$LDAPS_URI" -D "$YELLOWSTONE_SVC_DN" -w "$SVC_JIM_PW" -s 'Lab-Check@1' "$renamed_dn" >/dev/null \
        || fail "svc-jim cannot set a policy-compliant password with Password Modify over LDAPS (validated against the lab CA)"
    ldapwhoami -x -H "$LDAP_URI" -D "$renamed_dn" -w 'Lab-Check@1' >/dev/null \
        || fail "the password set by Password Modify does not bind"
    if ldappasswd -x -H "$LDAP_URI" -D "$YELLOWSTONE_SVC_DN" -w "$SVC_JIM_PW" -s 'Lab-Check@2' "$renamed_dn" >/dev/null 2>&1; then
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

    # The trivial-words rule at passwordMinTokenLength 5 (see set_password_policy):
    # the scenarios' shared static password must pass for an entry named like
    # the account the default of 3 refused in the lab, and a password carrying
    # the entry's own name must still be refused. Both as svc-jim over LDAPS,
    # which is how JIM sets them.
    out=$(dm_search -b cn=config -s base passwordMinTokenLength)
    grep -qi '^passwordMinTokenLength: 5$' <<<"$out" || fail "passwordMinTokenLength on cn=config is not 5 (got: $out)"
    local words_dn="uid=charlie.check,ou=People,$YELLOWSTONE_SUFFIX"
    ldapadd -x -H "$LDAP_URI" -D "$YELLOWSTONE_SVC_DN" -w "$SVC_JIM_PW" >/dev/null <<LDIF || fail "svc-jim cannot add the trivial-words check entry under ou=People"
dn: $words_dn
objectClass: top
objectClass: jimPerson
uid: charlie.check
cn: Charlie Mathews
sn: Mathews
givenName: Charlie
mail: charlie.check@panoply.local
LDIF
    out=$(LDAPTLS_CACERT="$LAB_CA_CERT" ldappasswd -x -H "$LDAPS_URI" -D "$YELLOWSTONE_SVC_DN" -w "$SVC_JIM_PW" -s 'Chalkstream-7-Vault!' "$words_dn" 2>&1) \
        || fail "the scenarios' static password was refused for an entry with givenName Charlie (passwordMinTokenLength 5 not effective); got: $out"
    log "OK: the scenarios' static password is accepted for an entry named Charlie (passwordMinTokenLength 5, so \"Cha\" no longer trips the trivial-words rule)"
    out=$(LDAPTLS_CACERT="$LAB_CA_CERT" ldappasswd -x -H "$LDAPS_URI" -D "$YELLOWSTONE_SVC_DN" -w "$SVC_JIM_PW" -s 'Charlie-7-Vault!' "$words_dn" 2>&1 || true)
    grep -qi 'Constraint violation' <<<"$out" \
        || fail "a password carrying the entry's own givenName was not refused (the trivial-words rule is no longer enforced); got: $out"
    log "OK: a password carrying the entry's own name is still refused (the five-character run \"Charl\" is caught)"
    ldapdelete -x -H "$LDAP_URI" -D "$YELLOWSTONE_SVC_DN" -w "$SVC_JIM_PW" "$words_dn" || fail "svc-jim cannot delete the trivial-words check entry"

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
    # restores when a container's /data does not hold this build's instance.
    rm -rf "${DATA_DIR:?}/run/"* "${DATA_DIR:?}/logs/"*
    # The provenance stamp, written BEFORE the copy so /data (which seeds a fresh
    # named volume from the image layer) and /data.provisioned carry the same id:
    # start-dirsrv.sh starts the volume's instance only when the two ids agree.
    # A snapshot built on this image overwrites the provisioned copy's id with
    # its own, which is what makes a volume seeded from the base layer restore.
    printf '%s' "$BUILD_HASH" > "$DATA_DIR/$PROVISIONED_ID_FILE"
    rm -rf "$PROVISIONED_DIR"
    cp -a "$DATA_DIR" "$PROVISIONED_DIR"
    [ "$(cat "$PROVISIONED_DIR/$PROVISIONED_ID_FILE")" = "$BUILD_HASH" ] || fail "the provisioned copy does not carry the build hash"
    log "Provisioned copy written to $PROVISIONED_DIR ($(du -sh "$PROVISIONED_DIR" | cut -f1)), stamped $BUILD_HASH"
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
    set_password_policy
    # The plug-in (and its cn=changelog backend) only comes to life on restart;
    # the changelog ACI needs the cn=changelog entry to exist. The password
    # policy is set before the restart so run_checks proves it survives one.
    stop_server
    start_server
    apply_changelog_and_config_acis
    run_checks
    stop_server
    finalise
    log "389 Directory Server lab: build-time configuration complete"
}

main "$@"
