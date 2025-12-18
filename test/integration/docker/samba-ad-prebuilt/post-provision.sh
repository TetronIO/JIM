#!/bin/bash
# Post-provisioning setup for Samba AD
# This script runs after the domain is provisioned to add TLS and test OUs
# Called by Build-SambaImages.ps1 after the container is running and healthy

set -e

echo "=============================================="
echo "Post-provisioning Samba AD Configuration"
echo "=============================================="

# Parse domain components from environment
LDOMAIN=${DOMAIN,,}
UDOMAIN=${DOMAIN^^}
URDOMAIN=${UDOMAIN%%.*}

# Build the DC string (e.g., TESTDOMAIN.LOCAL -> DC=testdomain,DC=local)
DOMAIN_DC=$(echo "$LDOMAIN" | sed 's/\./,DC=/g' | sed 's/^/DC=/')

echo "Domain: ${DOMAIN}"
echo "Domain DN: ${DOMAIN_DC}"

# Generate TLS certificates for LDAPS
echo "Generating TLS certificates for LDAPS..."
mkdir -p /var/lib/samba/private/tls

if [ ! -f /var/lib/samba/private/tls/cert.pem ]; then
    openssl req -x509 -nodes -days 3650 \
        -newkey rsa:2048 \
        -keyout /var/lib/samba/private/tls/key.pem \
        -out /var/lib/samba/private/tls/cert.pem \
        -subj "/CN=${LDOMAIN}/O=JIM Integration Testing" \
        2>/dev/null

    cp /var/lib/samba/private/tls/cert.pem /var/lib/samba/private/tls/ca.pem
    chmod 600 /var/lib/samba/private/tls/key.pem
    echo "  TLS certificates generated"
else
    echo "  TLS certificates already exist"
fi

# Add TLS configuration to smb.conf if not present
if ! grep -q "tls enabled" /etc/samba/smb.conf; then
    echo "Adding TLS configuration to smb.conf..."
    sed -i "/\[global\]/a \\
tls enabled = yes\\n\\
tls keyfile = /var/lib/samba/private/tls/key.pem\\n\\
tls certfile = /var/lib/samba/private/tls/cert.pem\\n\\
tls cafile = /var/lib/samba/private/tls/ca.pem\\
" /etc/samba/smb.conf
    echo "  TLS configuration added"
else
    echo "  TLS already configured"
fi

# Save smb.conf to external location
cp /etc/samba/smb.conf /etc/samba/external/smb.conf

# NOTE: Test OUs (OU=TestUsers, OU=TestGroups) are NOT created here because
# /var/lib/samba is declared as a VOLUME in the base image, and docker commit
# does not include volume data. The OUs should be created at runtime by
# Populate-SambaAD.ps1 or similar scripts. Creating OUs is fast (<1s).

# Install SSH public key schema (optional, may already exist from init.sh)
echo "Checking SSH public key schema..."
if ! ldbedit -H /var/lib/samba/private/sam.ldb -e cat "cn=sshPublicKey,cn=Schema,cn=Configuration,${DOMAIN_DC}" 2>/dev/null | grep -q sshPublicKey; then
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

    ldbadd -H /var/lib/samba/private/sam.ldb /tmp/sshpubkey.attr.ldif --option="dsdb:schema update allowed"=true 2>/dev/null || true
    ldbadd -H /var/lib/samba/private/sam.ldb /tmp/sshpubkey.class.ldif --option="dsdb:schema update allowed"=true 2>/dev/null || true
    rm -f /tmp/sshpubkey.*.ldif
    echo "  SSH public key schema installed"
else
    echo "  SSH public key schema already exists"
fi

echo "=============================================="
echo "Post-provisioning complete!"
echo "=============================================="
