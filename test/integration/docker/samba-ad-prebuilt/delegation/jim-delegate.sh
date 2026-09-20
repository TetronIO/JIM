#!/bin/bash
# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.
#
# Applies JIM's delegation to this domain controller, exactly as an Active Directory
# administrator would with dsacls: over LDAP, as the domain administrator, writing the
# access control entries held in jim-ad-delegation.acl (the same file the LDAP Connector
# documentation publishes).
#
# Usage:
#   jim-delegate.sh <container DN>   delegate JIM's access over a container (an OU) and everything below it
#   jim-delegate.sh --tombstones     grant read over the Deleted Objects container, so Delta Import sees deletions
#
# Both forms are idempotent: a container already carrying the delegation is left alone, so
# populate and scenario scripts can call this for every container they create without
# accumulating duplicate entries.

set -e

ACL_FILE="${JIM_DELEGATION_ACL:-/usr/local/share/jim/jim-ad-delegation.acl}"
SAMBA_BIN="/usr/local/samba/bin"
LDAP_URL="ldap://localhost"

FULL_DOMAIN="${REALM:-${DOMAIN}}"
LDOMAIN="${FULL_DOMAIN,,}"
DOMAIN_DC=$(echo "$LDOMAIN" | sed 's/\./,DC=/g' | sed 's/^/DC=/')
ADMIN_CREDS="Administrator%${ADMIN_PASS:-${DOMAINPASS}}"
ADMIN_DN="CN=Administrator,CN=Users,${DOMAIN_DC}"
ADMIN_PASSWORD="${ADMIN_PASS:-${DOMAINPASS}}"

# The group the delegation is granted to. JIM's service account is a member of it; nothing is
# granted to the account directly, so the account can be replaced without redoing any of this.
JIM_GROUP_DN="CN=JIM Connectors,OU=Services,${DOMAIN_DC}"

# Reads one attribute of one entry, unwrapping LDIF's 79-column line folding.
read_attribute() {
    local base="$1" attribute="$2" controls="$3"
    local args=(-H "$LDAP_URL" --simple-bind-dn="$ADMIN_DN" --password="$ADMIN_PASSWORD" -b "$base" -s base "$attribute")
    if [ -n "$controls" ]; then args+=(--controls="$controls"); fi
    ${SAMBA_BIN}/ldbsearch "${args[@]}" 2>/dev/null \
        | sed -n "/^${attribute}: /,/^$/p" \
        | sed ':a;N;$!ba;s/\n //g' \
        | sed -n "s/^${attribute}: //p" \
        | head -1
}

jim_group_sid() {
    local sid
    sid=$(${SAMBA_BIN}/ldbsearch -H "$LDAP_URL" --simple-bind-dn="$ADMIN_DN" --password="$ADMIN_PASSWORD" \
        -b "$JIM_GROUP_DN" -s base objectSid 2>/dev/null | sed -n 's/^objectSid: //p' | head -1)
    if [ -z "$sid" ]; then
        echo "ERROR: the delegation group '${JIM_GROUP_DN}' was not found; the image's post-provisioning should have created it" >&2
        exit 1
    fi
    echo "$sid"
}

# The access control entries, with the trustee filled in: one SDDL string, comments stripped.
delegation_sddl() {
    local sid="$1"
    if [ ! -f "$ACL_FILE" ]; then
        echo "ERROR: the delegation file '${ACL_FILE}' is missing from this image" >&2
        exit 1
    fi
    grep -v '^\s*#' "$ACL_FILE" | grep -v '^\s*$' | tr -d '\n ' | sed "s/__JIM_TRUSTEE_SID__/${sid}/g"
}

delegate_container() {
    local container_dn="$1"
    local sid; sid=$(jim_group_sid)

    local existing; existing=$(read_attribute "$container_dn" "nTSecurityDescriptor" "sd_flags:1:4")
    if [ -z "$existing" ]; then
        echo "ERROR: could not read the permissions of '${container_dn}'; does it exist?" >&2
        exit 1
    fi
    if echo "$existing" | grep -q "$sid"; then
        echo "  Delegation already present on ${container_dn}"
        return 0
    fi

    ${SAMBA_BIN}/samba-tool dsacl set -H "$LDAP_URL" -U "$ADMIN_CREDS" \
        --objectdn="$container_dn" --sddl="$(delegation_sddl "$sid")" > /dev/null
    echo "  Delegated JIM's access over ${container_dn}"
}

# Deletions reach a Delta Import through the Deleted Objects container, which no delegation
# reaches by default: its permissions are protected from inheritance, its owner is SYSTEM, and
# the administrators group is granted only enough to read what is inside, never to change who
# else can.
#
# Withholding this read does not produce an error: the container's contents are simply not
# returned, and a Delta Import that would have seen deletions sees none of them, silently.
delegate_tombstones() {
    local sid; sid=$(jim_group_sid)
    local container_dn="CN=Deleted Objects,${DOMAIN_DC}"
    local controls="show_deleted:1,sd_flags:1:4"

    # Ownership first, because the container's permissions cannot even be READ otherwise: the
    # default entries give the administrators group List Contents and Read Property and nothing
    # else, and reading a security descriptor needs Read Control. Taking ownership is what makes
    # the domain administrator able to grant anything here at all, and is the same step
    # "dsacls /takeOwnership" performs on Windows. Repeating it is harmless.
    cat > /tmp/jim-tombstone-owner.ldif << EOF
dn: ${container_dn}
changetype: modify
replace: nTSecurityDescriptor
nTSecurityDescriptor: O:BA
EOF
    if ! ${SAMBA_BIN}/ldbmodify -H "$LDAP_URL" --simple-bind-dn="$ADMIN_DN" --password="$ADMIN_PASSWORD" \
            --controls="show_deleted:1,sd_flags:1:1" /tmp/jim-tombstone-owner.ldif > /dev/null; then
        echo "ERROR: could not take ownership of '${container_dn}'" >&2
        exit 1
    fi

    local existing; existing=$(read_attribute "$container_dn" "nTSecurityDescriptor" "$controls")
    if [ -z "$existing" ]; then
        echo "ERROR: could not read the permissions of '${container_dn}' even as its owner" >&2
        exit 1
    fi
    if echo "$existing" | grep -q "$sid"; then
        rm -f /tmp/jim-tombstone-owner.ldif
        echo "  Deleted Objects read already granted"
        return 0
    fi

    # List Contents and Read Property is all a Delta Import needs: tombstones are only ever read.
    cat > /tmp/jim-tombstone-dacl.ldif << EOF
dn: ${container_dn}
changetype: modify
replace: nTSecurityDescriptor
nTSecurityDescriptor: ${existing}(A;;LCRP;;;${sid})
EOF
    if ! ${SAMBA_BIN}/ldbmodify -H "$LDAP_URL" --simple-bind-dn="$ADMIN_DN" --password="$ADMIN_PASSWORD" \
            --controls="$controls" /tmp/jim-tombstone-dacl.ldif > /dev/null; then
        echo "ERROR: could not grant read over '${container_dn}'" >&2
        exit 1
    fi
    rm -f /tmp/jim-tombstone-owner.ldif /tmp/jim-tombstone-dacl.ldif
    echo "  Granted read over ${container_dn}"
}

case "${1:-}" in
    "")
        echo "Usage: jim-delegate.sh <container DN> | --tombstones" >&2
        exit 2
        ;;
    --tombstones)
        delegate_tombstones
        ;;
    *)
        delegate_container "$1"
        ;;
esac
