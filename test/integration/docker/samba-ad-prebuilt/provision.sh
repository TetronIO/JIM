#!/bin/bash
# Pre-provision Samba AD domain at image build time
# This script runs once during docker build to create a ready-to-use domain controller

set -e

echo "=============================================="
echo "Pre-provisioning Samba AD Domain Controller"
echo "Domain: ${DOMAIN}"
echo "=============================================="

# Parse domain components
LDOMAIN=${DOMAIN,,}
UDOMAIN=${DOMAIN^^}
URDOMAIN=${UDOMAIN%%.*}

# Build the DC string (e.g., TESTDOMAIN.LOCAL -> DC=testdomain,DC=local)
DOMAIN_DC=$(echo "$LDOMAIN" | sed 's/\./,DC=/g' | sed 's/^/DC=/')

echo "Domain DN: ${DOMAIN_DC}"

# Remove existing smb.conf - provisioning needs to create its own
echo "Removing existing smb.conf (provisioning will create new one)..."
rm -f /etc/samba/smb.conf

# Set up Kerberos configuration
echo "Configuring Kerberos..."
cat > /etc/krb5.conf << EOF
[libdefaults]
    dns_lookup_realm = false
    dns_lookup_kdc = true
    default_realm = ${UDOMAIN}
EOF

# Provision the domain (this is the slow part - ~2-3 minutes)
echo "Provisioning domain (this takes a while)..."
samba-tool domain provision \
    --use-rfc2307 \
    --domain=${URDOMAIN} \
    --realm=${UDOMAIN} \
    --server-role=dc \
    --dns-backend=SAMBA_INTERNAL \
    --adminpass="${DOMAINPASS}"

echo "Domain provisioned successfully"

# Disable password complexity
echo "Configuring password policy..."
samba-tool domain passwordsettings set --complexity=off
samba-tool domain passwordsettings set --history-length=0
samba-tool domain passwordsettings set --min-pwd-age=0
samba-tool domain passwordsettings set --max-pwd-age=0

# Update smb.conf with additional settings
echo "Updating smb.conf..."
sed -i "/\[global\]/a \\
\\tidmap_ldb:use rfc2307 = yes\\n\\
wins support = yes\\n\\
template shell = /bin/bash\\n\\
template homedir = /home/%U\\n\\
idmap config ${URDOMAIN} : schema_mode = rfc2307\\n\\
idmap config ${URDOMAIN} : unix_nss_info = yes\\n\\
idmap config ${URDOMAIN} : backend = ad\\n\\
rpc server dynamic port range = 49152-49172\\n\\
ldap server require strong auth = no\\
" /etc/samba/smb.conf

# Fix the DC name in smb.conf
sed -i "s/LOCALDC/${URDOMAIN}DC/g" /etc/samba/smb.conf

# Generate TLS certificates for LDAPS
echo "Generating TLS certificates for LDAPS..."
mkdir -p /var/lib/samba/private/tls

openssl req -x509 -nodes -days 3650 \
    -newkey rsa:2048 \
    -keyout /var/lib/samba/private/tls/key.pem \
    -out /var/lib/samba/private/tls/cert.pem \
    -subj "/CN=${LDOMAIN}/O=JIM Integration Testing" \
    2>/dev/null

cp /var/lib/samba/private/tls/cert.pem /var/lib/samba/private/tls/ca.pem
chmod 600 /var/lib/samba/private/tls/key.pem

# Add TLS configuration to smb.conf
sed -i "/\[global\]/a \\
tls enabled = yes\\n\\
tls keyfile = /var/lib/samba/private/tls/key.pem\\n\\
tls certfile = /var/lib/samba/private/tls/cert.pem\\n\\
tls cafile = /var/lib/samba/private/tls/ca.pem\\
" /etc/samba/smb.conf

# Save smb.conf to external location (this is the marker that tells init.sh to skip provisioning)
mkdir -p /etc/samba/external
cp /etc/samba/smb.conf /etc/samba/external/smb.conf

# Start Samba temporarily to create OUs and configure schema
echo "Starting Samba temporarily for post-provisioning setup..."
/usr/sbin/samba &
SAMBA_PID=$!
sleep 10

# Create test OUs
echo "Creating test OUs..."
samba-tool ou create "OU=TestUsers,${DOMAIN_DC}" 2>/dev/null || echo "OU=TestUsers may already exist"
samba-tool ou create "OU=TestGroups,${DOMAIN_DC}" 2>/dev/null || echo "OU=TestGroups may already exist"

# Fix Domain Users group gidNumber for RFC2307
echo "Configuring Domain Users gidNumber..."
GIDNUMBER=$(ldbedit -H /var/lib/samba/private/sam.ldb -e cat "samaccountname=domain users" 2>/dev/null | grep ^gidNumber: || true)
if [ -z "${GIDNUMBER}" ]; then
    echo "dn: CN=Domain Users,CN=Users,${DOMAIN_DC}
changetype: modify
add: gidNumber
gidNumber: 3000000" | ldbmodify -H /var/lib/samba/private/sam.ldb
    echo "Set Domain Users gidNumber to 3000000"
fi

# Install SSH public key schema
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

ldbadd -H /var/lib/samba/private/sam.ldb /tmp/sshpubkey.attr.ldif --option="dsdb:schema update allowed"=true 2>/dev/null || echo "SSH attribute schema may already exist"
ldbadd -H /var/lib/samba/private/sam.ldb /tmp/sshpubkey.class.ldif --option="dsdb:schema update allowed"=true 2>/dev/null || echo "SSH class schema may already exist"

# Stop Samba
echo "Stopping Samba..."
kill $SAMBA_PID 2>/dev/null || true
sleep 5

# Clean up
rm -f /tmp/sshpubkey.*.ldif

echo "=============================================="
echo "Pre-provisioning complete!"
echo "Domain: ${DOMAIN}"
echo "Admin password: ${DOMAINPASS}"
echo "LDAPS: Enabled (port 636)"
echo "Test OUs: OU=TestUsers, OU=TestGroups"
echo "=============================================="
