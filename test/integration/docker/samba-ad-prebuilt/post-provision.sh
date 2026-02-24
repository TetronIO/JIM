#!/bin/bash
# Post-provisioning setup for Samba AD
# This script runs after the domain is provisioned to add TLS and test OUs
# Called by Build-SambaImages.ps1 after the container is running and healthy
#
# ARCHITECTURE: Supports both AMD64 and ARM64 via diegogslomp/samba-ad-dc base image

set -e

echo "=============================================="
echo "Post-provisioning Samba AD Configuration"
echo "=============================================="

# Parse domain components from environment
# diegogslomp/samba-ad-dc uses REALM for full domain (e.g., TESTDOMAIN.LOCAL)
# and DOMAIN for short domain (e.g., TESTDOMAIN)
FULL_DOMAIN=${REALM:-${DOMAIN}}
LDOMAIN=${FULL_DOMAIN,,}
UDOMAIN=${FULL_DOMAIN^^}
URDOMAIN=${UDOMAIN%%.*}

# Build the DC string (e.g., SUBATOMIC.LOCAL -> DC=subatomic,DC=local)
DOMAIN_DC=$(echo "$LDOMAIN" | sed 's/\./,DC=/g' | sed 's/^/DC=/')

echo "Domain: ${FULL_DOMAIN}"
echo "Domain DN: ${DOMAIN_DC}"

# diegogslomp/samba-ad-dc paths (different from nowsci/samba-domain)
SAMBA_BASE="/usr/local/samba"
SAMBA_PRIVATE="${SAMBA_BASE}/private"
SAMBA_ETC="${SAMBA_BASE}/etc"
SAMBA_BIN="${SAMBA_BASE}/bin"

echo "Samba base path: ${SAMBA_BASE}"

# Create symlinks for common samba commands so they work without full paths
# This makes scripts compatible with both old (nowsci) and new (diegogslomp) images
echo "Creating symlinks for samba commands..."
for cmd in samba-tool smbclient ldbsearch ldbadd ldbmodify ldbedit; do
    if [ -f "${SAMBA_BIN}/${cmd}" ] && [ ! -f "/usr/bin/${cmd}" ]; then
        ln -sf "${SAMBA_BIN}/${cmd}" "/usr/bin/${cmd}" 2>/dev/null || true
    fi
done
echo "  Symlinks created"

# Generate TLS certificates for LDAPS
echo "Generating TLS certificates for LDAPS..."
mkdir -p ${SAMBA_PRIVATE}/tls

if [ ! -f ${SAMBA_PRIVATE}/tls/cert.pem ]; then
    openssl req -x509 -nodes -days 3650 \
        -newkey rsa:2048 \
        -keyout ${SAMBA_PRIVATE}/tls/key.pem \
        -out ${SAMBA_PRIVATE}/tls/cert.pem \
        -subj "/CN=${LDOMAIN}/O=JIM Integration Testing" \
        2>/dev/null

    cp ${SAMBA_PRIVATE}/tls/cert.pem ${SAMBA_PRIVATE}/tls/ca.pem
    chmod 600 ${SAMBA_PRIVATE}/tls/key.pem
    echo "  TLS certificates generated"
else
    echo "  TLS certificates already exist"
fi

# Add TLS configuration to smb.conf if not present
if ! grep -q "tls enabled" ${SAMBA_ETC}/smb.conf; then
    echo "Adding TLS configuration to smb.conf..."
    sed -i "/\[global\]/a \\
tls enabled = yes\\n\\
tls keyfile = ${SAMBA_PRIVATE}/tls/key.pem\\n\\
tls certfile = ${SAMBA_PRIVATE}/tls/cert.pem\\n\\
tls cafile = ${SAMBA_PRIVATE}/tls/ca.pem\\n\\
ldap server require strong auth = no\\
" ${SAMBA_ETC}/smb.conf
    echo "  TLS configuration added"
else
    echo "  TLS already configured"
fi

# Disable password complexity if NOCOMPLEXITY is set
if [ "${NOCOMPLEXITY}" = "true" ]; then
    echo "Disabling password complexity..."
    ${SAMBA_BIN}/samba-tool domain passwordsettings set --complexity=off 2>/dev/null || true
    ${SAMBA_BIN}/samba-tool domain passwordsettings set --history-length=0 2>/dev/null || true
    ${SAMBA_BIN}/samba-tool domain passwordsettings set --min-pwd-age=0 2>/dev/null || true
    ${SAMBA_BIN}/samba-tool domain passwordsettings set --max-pwd-age=0 2>/dev/null || true
    echo "  Password complexity disabled"
fi

# Create baseline OU structure for integration testing
# These OUs are created here (during post-provisioning) so they're baked into
# the committed image. This runs AFTER the container is running, so the database
# exists and will be persisted by docker commit.
#
# Structure:
#   OU=Corp,DC=...              - Main corporate structure (selected in JIM)
#     OU=Users,OU=Corp,DC=...   - User objects (provisioned here by JIM)
#     OU=Groups,OU=Corp,DC=...  - Group objects
#   OU=TestUsers,DC=...         - Legacy test users (for compatibility)
#   OU=TestGroups,DC=...        - Legacy test groups
echo "Creating baseline OU structure..."

# Corp base OU - this is selected as the partition container in JIM
${SAMBA_BIN}/samba-tool ou create "OU=Corp,${DOMAIN_DC}" 2>/dev/null || echo "  OU=Corp already exists"
${SAMBA_BIN}/samba-tool ou create "OU=Users,OU=Corp,${DOMAIN_DC}" 2>/dev/null || echo "  OU=Users,OU=Corp already exists"
${SAMBA_BIN}/samba-tool ou create "OU=Groups,OU=Corp,${DOMAIN_DC}" 2>/dev/null || echo "  OU=Groups,OU=Corp already exists"

# Legacy test OUs (kept for backward compatibility with Populate-SambaAD.ps1)
${SAMBA_BIN}/samba-tool ou create "OU=TestUsers,${DOMAIN_DC}" 2>/dev/null || echo "  OU=TestUsers already exists"
${SAMBA_BIN}/samba-tool ou create "OU=TestGroups,${DOMAIN_DC}" 2>/dev/null || echo "  OU=TestGroups already exists"

echo "  OU structure created"

# Install SSH public key schema (optional, may already exist)
echo "Checking SSH public key schema..."
sshkey_check=$(${SAMBA_BIN}/ldbsearch -H ${SAMBA_PRIVATE}/sam.ldb "cn=sshPublicKey" 2>/dev/null || true)
if ! echo "$sshkey_check" | grep -q sshPublicKey 2>/dev/null; then
    echo "Installing SSH public key schema..."

    echo "dn: CN=sshPublicKey,CN=Schema,CN=Configuration,${DOMAIN_DC}
changetype: add
objectClass: top
objectClass: attributeSchema
attributeID: 1.3.6.1.4.1.24552.500.1.1.1.13
cn: sshPublicKey
name: sshPublicKey
lDAPDisplayName: sshPublicKey
description: MANDATORY: OpenSSH Public key
attributeSyntax: 2.5.5.10
oMSyntax: 4
isSingleValued: FALSE
objectCategory: CN=Attribute-Schema,CN=Schema,CN=Configuration,${DOMAIN_DC}
searchFlags: 8
schemaIDGUID:: cjDAZyEXzU+/akI0EGDW+g==" > /tmp/sshpubkey.attr.ldif

    echo "dn: CN=ldapPublicKey,CN=Schema,CN=Configuration,${DOMAIN_DC}
changetype: add
objectClass: top
objectClass: classSchema
governsID: 1.3.6.1.4.1.24552.500.1.1.2.0
cn: ldapPublicKey
name: ldapPublicKey
description: MANDATORY: OpenSSH LPK objectclass
lDAPDisplayName: ldapPublicKey
subClassOf: top
objectClassCategory: 3
objectCategory: CN=Class-Schema,CN=Schema,CN=Configuration,${DOMAIN_DC}
defaultObjectCategory: CN=ldapPublicKey,CN=Schema,CN=Configuration,${DOMAIN_DC}
mayContain: sshPublicKey
schemaIDGUID:: +8nFQ43rpkWTOgbCCcSkqA==" > /tmp/sshpubkey.class.ldif

    ${SAMBA_BIN}/ldbadd -H ${SAMBA_PRIVATE}/sam.ldb /tmp/sshpubkey.attr.ldif --option="dsdb:schema update allowed"=true 2>/dev/null || true
    ${SAMBA_BIN}/ldbadd -H ${SAMBA_PRIVATE}/sam.ldb /tmp/sshpubkey.class.ldif --option="dsdb:schema update allowed"=true 2>/dev/null || true
    rm -f /tmp/sshpubkey.*.ldif
    echo "  SSH public key schema installed"
else
    echo "  SSH public key schema already exists"
fi

echo "=============================================="
echo "Post-provisioning complete!"
echo "=============================================="
