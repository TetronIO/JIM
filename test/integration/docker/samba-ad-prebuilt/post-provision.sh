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

# Install Exchange extensionAttribute1-15 schema (for integration testing)
# In real AD these come from the Exchange schema extension. We add them to Samba AD
# so integration tests can use extensionAttribute1-15 on user objects (e.g., for
# storing pronouns, custom HR data, etc.)
#
# OIDs and schemaIdGUIDs sourced from Microsoft Exchange Server SDK documentation:
# https://learn.microsoft.com/en-us/previous-versions/office/developer/exchange-server-2003/
echo "Checking extensionAttribute schema..."
extattr_check=$(${SAMBA_BIN}/ldbsearch -H ${SAMBA_PRIVATE}/sam.ldb "cn=ms-Exch-Extension-Attribute-1" 2>/dev/null || true)
if ! echo "$extattr_check" | grep -q "ms-Exch-Extension-Attribute-1" 2>/dev/null; then
    echo "Installing extensionAttribute1-15 schema..."

    # Attribute definitions (1-10: rangeUpper 1024, 11-15: rangeUpper 2048)
    # All are single-valued Unicode strings (attributeSyntax 2.5.5.12, oMSyntax 64)
    cat > /tmp/extattr.ldif << EXTATTR_EOF
dn: CN=ms-Exch-Extension-Attribute-1,CN=Schema,CN=Configuration,${DOMAIN_DC}
changetype: add
objectClass: top
objectClass: attributeSchema
attributeID: 1.2.840.113556.1.2.423
cn: ms-Exch-Extension-Attribute-1
name: ms-Exch-Extension-Attribute-1
lDAPDisplayName: extensionAttribute1
attributeSyntax: 2.5.5.12
oMSyntax: 64
isSingleValued: TRUE
rangeLower: 1
rangeUpper: 1024
objectCategory: CN=Attribute-Schema,CN=Schema,CN=Configuration,${DOMAIN_DC}
searchFlags: 16
schemaIDGUID:: Z3mWv+YN0BGihQCqADBJ4g==

dn: CN=ms-Exch-Extension-Attribute-2,CN=Schema,CN=Configuration,${DOMAIN_DC}
changetype: add
objectClass: top
objectClass: attributeSchema
attributeID: 1.2.840.113556.1.2.424
cn: ms-Exch-Extension-Attribute-2
name: ms-Exch-Extension-Attribute-2
lDAPDisplayName: extensionAttribute2
attributeSyntax: 2.5.5.12
oMSyntax: 64
isSingleValued: TRUE
rangeLower: 1
rangeUpper: 1024
objectCategory: CN=Attribute-Schema,CN=Schema,CN=Configuration,${DOMAIN_DC}
searchFlags: 16
schemaIDGUID:: aXmWv+YN0BGihQCqADBJ4g==

dn: CN=ms-Exch-Extension-Attribute-3,CN=Schema,CN=Configuration,${DOMAIN_DC}
changetype: add
objectClass: top
objectClass: attributeSchema
attributeID: 1.2.840.113556.1.2.425
cn: ms-Exch-Extension-Attribute-3
name: ms-Exch-Extension-Attribute-3
lDAPDisplayName: extensionAttribute3
attributeSyntax: 2.5.5.12
oMSyntax: 64
isSingleValued: TRUE
rangeLower: 1
rangeUpper: 1024
objectCategory: CN=Attribute-Schema,CN=Schema,CN=Configuration,${DOMAIN_DC}
searchFlags: 16
schemaIDGUID:: anmWv+YN0BGihQCqADBJ4g==

dn: CN=ms-Exch-Extension-Attribute-4,CN=Schema,CN=Configuration,${DOMAIN_DC}
changetype: add
objectClass: top
objectClass: attributeSchema
attributeID: 1.2.840.113556.1.2.426
cn: ms-Exch-Extension-Attribute-4
name: ms-Exch-Extension-Attribute-4
lDAPDisplayName: extensionAttribute4
attributeSyntax: 2.5.5.12
oMSyntax: 64
isSingleValued: TRUE
rangeLower: 1
rangeUpper: 1024
objectCategory: CN=Attribute-Schema,CN=Schema,CN=Configuration,${DOMAIN_DC}
searchFlags: 16
schemaIDGUID:: a3mWv+YN0BGihQCqADBJ4g==

dn: CN=ms-Exch-Extension-Attribute-5,CN=Schema,CN=Configuration,${DOMAIN_DC}
changetype: add
objectClass: top
objectClass: attributeSchema
attributeID: 1.2.840.113556.1.2.427
cn: ms-Exch-Extension-Attribute-5
name: ms-Exch-Extension-Attribute-5
lDAPDisplayName: extensionAttribute5
attributeSyntax: 2.5.5.12
oMSyntax: 64
isSingleValued: TRUE
rangeLower: 1
rangeUpper: 1024
objectCategory: CN=Attribute-Schema,CN=Schema,CN=Configuration,${DOMAIN_DC}
searchFlags: 16
schemaIDGUID:: bHmWv+YN0BGihQCqADBJ4g==

dn: CN=ms-Exch-Extension-Attribute-6,CN=Schema,CN=Configuration,${DOMAIN_DC}
changetype: add
objectClass: top
objectClass: attributeSchema
attributeID: 1.2.840.113556.1.2.428
cn: ms-Exch-Extension-Attribute-6
name: ms-Exch-Extension-Attribute-6
lDAPDisplayName: extensionAttribute6
attributeSyntax: 2.5.5.12
oMSyntax: 64
isSingleValued: TRUE
rangeLower: 1
rangeUpper: 1024
objectCategory: CN=Attribute-Schema,CN=Schema,CN=Configuration,${DOMAIN_DC}
searchFlags: 16
schemaIDGUID:: bXmWv+YN0BGihQCqADBJ4g==

dn: CN=ms-Exch-Extension-Attribute-7,CN=Schema,CN=Configuration,${DOMAIN_DC}
changetype: add
objectClass: top
objectClass: attributeSchema
attributeID: 1.2.840.113556.1.2.429
cn: ms-Exch-Extension-Attribute-7
name: ms-Exch-Extension-Attribute-7
lDAPDisplayName: extensionAttribute7
attributeSyntax: 2.5.5.12
oMSyntax: 64
isSingleValued: TRUE
rangeLower: 1
rangeUpper: 1024
objectCategory: CN=Attribute-Schema,CN=Schema,CN=Configuration,${DOMAIN_DC}
searchFlags: 16
schemaIDGUID:: bnmWv+YN0BGihQCqADBJ4g==

dn: CN=ms-Exch-Extension-Attribute-8,CN=Schema,CN=Configuration,${DOMAIN_DC}
changetype: add
objectClass: top
objectClass: attributeSchema
attributeID: 1.2.840.113556.1.2.430
cn: ms-Exch-Extension-Attribute-8
name: ms-Exch-Extension-Attribute-8
lDAPDisplayName: extensionAttribute8
attributeSyntax: 2.5.5.12
oMSyntax: 64
isSingleValued: TRUE
rangeLower: 1
rangeUpper: 1024
objectCategory: CN=Attribute-Schema,CN=Schema,CN=Configuration,${DOMAIN_DC}
searchFlags: 16
schemaIDGUID:: b3mWv+YN0BGihQCqADBJ4g==

dn: CN=ms-Exch-Extension-Attribute-9,CN=Schema,CN=Configuration,${DOMAIN_DC}
changetype: add
objectClass: top
objectClass: attributeSchema
attributeID: 1.2.840.113556.1.2.431
cn: ms-Exch-Extension-Attribute-9
name: ms-Exch-Extension-Attribute-9
lDAPDisplayName: extensionAttribute9
attributeSyntax: 2.5.5.12
oMSyntax: 64
isSingleValued: TRUE
rangeLower: 1
rangeUpper: 1024
objectCategory: CN=Attribute-Schema,CN=Schema,CN=Configuration,${DOMAIN_DC}
searchFlags: 16
schemaIDGUID:: cHmWv+YN0BGihQCqADBJ4g==

dn: CN=ms-Exch-Extension-Attribute-10,CN=Schema,CN=Configuration,${DOMAIN_DC}
changetype: add
objectClass: top
objectClass: attributeSchema
attributeID: 1.2.840.113556.1.2.432
cn: ms-Exch-Extension-Attribute-10
name: ms-Exch-Extension-Attribute-10
lDAPDisplayName: extensionAttribute10
attributeSyntax: 2.5.5.12
oMSyntax: 64
isSingleValued: TRUE
rangeLower: 1
rangeUpper: 1024
objectCategory: CN=Attribute-Schema,CN=Schema,CN=Configuration,${DOMAIN_DC}
searchFlags: 16
schemaIDGUID:: aHmWv+YN0BGihQCqADBJ4g==

dn: CN=ms-Exch-Extension-Attribute-11,CN=Schema,CN=Configuration,${DOMAIN_DC}
changetype: add
objectClass: top
objectClass: attributeSchema
attributeID: 1.2.840.113556.1.2.599
cn: ms-Exch-Extension-Attribute-11
name: ms-Exch-Extension-Attribute-11
lDAPDisplayName: extensionAttribute11
attributeSyntax: 2.5.5.12
oMSyntax: 64
isSingleValued: TRUE
rangeLower: 1
rangeUpper: 2048
objectCategory: CN=Attribute-Schema,CN=Schema,CN=Configuration,${DOMAIN_DC}
searchFlags: 16
schemaIDGUID:: 9ld3FvNH0RGpwwAA+ANnwQ==

dn: CN=ms-Exch-Extension-Attribute-12,CN=Schema,CN=Configuration,${DOMAIN_DC}
changetype: add
objectClass: top
objectClass: attributeSchema
attributeID: 1.2.840.113556.1.2.600
cn: ms-Exch-Extension-Attribute-12
name: ms-Exch-Extension-Attribute-12
lDAPDisplayName: extensionAttribute12
attributeSyntax: 2.5.5.12
oMSyntax: 64
isSingleValued: TRUE
rangeLower: 1
rangeUpper: 2048
objectCategory: CN=Attribute-Schema,CN=Schema,CN=Configuration,${DOMAIN_DC}
searchFlags: 16
schemaIDGUID:: 91d3FvNH0RGpwwAA+ANnwQ==

dn: CN=ms-Exch-Extension-Attribute-13,CN=Schema,CN=Configuration,${DOMAIN_DC}
changetype: add
objectClass: top
objectClass: attributeSchema
attributeID: 1.2.840.113556.1.2.601
cn: ms-Exch-Extension-Attribute-13
name: ms-Exch-Extension-Attribute-13
lDAPDisplayName: extensionAttribute13
attributeSyntax: 2.5.5.12
oMSyntax: 64
isSingleValued: TRUE
rangeLower: 1
rangeUpper: 2048
objectCategory: CN=Attribute-Schema,CN=Schema,CN=Configuration,${DOMAIN_DC}
searchFlags: 16
schemaIDGUID:: +Fd3FvNH0RGpwwAA+ANnwQ==

dn: CN=ms-Exch-Extension-Attribute-14,CN=Schema,CN=Configuration,${DOMAIN_DC}
changetype: add
objectClass: top
objectClass: attributeSchema
attributeID: 1.2.840.113556.1.2.602
cn: ms-Exch-Extension-Attribute-14
name: ms-Exch-Extension-Attribute-14
lDAPDisplayName: extensionAttribute14
attributeSyntax: 2.5.5.12
oMSyntax: 64
isSingleValued: TRUE
rangeLower: 1
rangeUpper: 2048
objectCategory: CN=Attribute-Schema,CN=Schema,CN=Configuration,${DOMAIN_DC}
searchFlags: 16
schemaIDGUID:: +Vd3FvNH0RGpwwAA+ANnwQ==

dn: CN=ms-Exch-Extension-Attribute-15,CN=Schema,CN=Configuration,${DOMAIN_DC}
changetype: add
objectClass: top
objectClass: attributeSchema
attributeID: 1.2.840.113556.1.2.603
cn: ms-Exch-Extension-Attribute-15
name: ms-Exch-Extension-Attribute-15
lDAPDisplayName: extensionAttribute15
attributeSyntax: 2.5.5.12
oMSyntax: 64
isSingleValued: TRUE
rangeLower: 1
rangeUpper: 2048
objectCategory: CN=Attribute-Schema,CN=Schema,CN=Configuration,${DOMAIN_DC}
searchFlags: 16
schemaIDGUID:: +ld3FvNH0RGpwwAA+ANnwQ==
EXTATTR_EOF

    # Load all 15 attribute definitions into the schema
    ${SAMBA_BIN}/ldbadd -H ${SAMBA_PRIVATE}/sam.ldb /tmp/extattr.ldif \
        --option="dsdb:schema update allowed"=true 2>/dev/null || true

    # Add extensionAttribute1-15 as mayContain on the user class
    # This allows user objects to carry these attributes (mimics Exchange schema extension)
    cat > /tmp/extattr-user.ldif << EXTATTR_USER_EOF
dn: CN=User,CN=Schema,CN=Configuration,${DOMAIN_DC}
changetype: modify
add: mayContain
mayContain: extensionAttribute1
mayContain: extensionAttribute2
mayContain: extensionAttribute3
mayContain: extensionAttribute4
mayContain: extensionAttribute5
mayContain: extensionAttribute6
mayContain: extensionAttribute7
mayContain: extensionAttribute8
mayContain: extensionAttribute9
mayContain: extensionAttribute10
mayContain: extensionAttribute11
mayContain: extensionAttribute12
mayContain: extensionAttribute13
mayContain: extensionAttribute14
mayContain: extensionAttribute15
EXTATTR_USER_EOF

    ${SAMBA_BIN}/ldbmodify -H ${SAMBA_PRIVATE}/sam.ldb /tmp/extattr-user.ldif \
        --option="dsdb:schema update allowed"=true 2>/dev/null || true

    rm -f /tmp/extattr.ldif /tmp/extattr-user.ldif
    echo "  extensionAttribute1-15 schema installed"
else
    echo "  extensionAttribute schema already exists"
fi

echo "=============================================="
echo "Post-provisioning complete!"
echo "=============================================="
