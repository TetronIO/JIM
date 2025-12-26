#!/bin/bash
# Pre-provision Samba AD domain at image build time
# This script is retained for compatibility but is typically NOT used with
# diegogslomp/samba-ad-dc as that image handles its own provisioning via init.sh.
#
# The Build-SambaImages.ps1 script uses the base image's automatic provisioning
# and then runs post-provision.sh to add TLS and other customisations.
#
# ARCHITECTURE: Supports both AMD64 and ARM64 via diegogslomp/samba-ad-dc base image

set -e

echo "=============================================="
echo "Pre-provisioning Samba AD Domain Controller"
echo "=============================================="

# Parse domain components from environment
# diegogslomp/samba-ad-dc uses REALM for full domain (e.g., TESTDOMAIN.LOCAL)
# and DOMAIN for short domain (e.g., TESTDOMAIN)
FULL_DOMAIN=${REALM:-${DOMAIN}}
LDOMAIN=${FULL_DOMAIN,,}
UDOMAIN=${FULL_DOMAIN^^}
URDOMAIN=${DOMAIN:-${UDOMAIN%%.*}}
PASSWORD=${ADMIN_PASS:-${DOMAINPASS}}

echo "Domain: ${FULL_DOMAIN}"
echo "Short Domain: ${URDOMAIN}"

# Build the DC string (e.g., TESTDOMAIN.LOCAL -> DC=testdomain,DC=local)
DOMAIN_DC=$(echo "$LDOMAIN" | sed 's/\./,DC=/g' | sed 's/^/DC=/')

echo "Domain DN: ${DOMAIN_DC}"

# diegogslomp/samba-ad-dc paths
SAMBA_BASE="/usr/local/samba"
SAMBA_PRIVATE="${SAMBA_BASE}/private"
SAMBA_ETC="${SAMBA_BASE}/etc"
SAMBA_BIN="${SAMBA_BASE}/bin"

echo "Samba base path: ${SAMBA_BASE}"

# Check if already provisioned
if [ -f "${SAMBA_ETC}/smb.conf" ]; then
    echo "Domain appears to be already provisioned (smb.conf exists)"
    echo "Skipping provisioning step"
    exit 0
fi

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
${SAMBA_BIN}/samba-tool domain provision \
    --use-rfc2307 \
    --domain=${URDOMAIN} \
    --realm=${UDOMAIN} \
    --server-role=dc \
    --dns-backend=SAMBA_INTERNAL \
    --adminpass="${PASSWORD}"

echo "Domain provisioned successfully"

# Disable password complexity
echo "Configuring password policy..."
${SAMBA_BIN}/samba-tool domain passwordsettings set --complexity=off
${SAMBA_BIN}/samba-tool domain passwordsettings set --history-length=0
${SAMBA_BIN}/samba-tool domain passwordsettings set --min-pwd-age=0
${SAMBA_BIN}/samba-tool domain passwordsettings set --max-pwd-age=0

echo "=============================================="
echo "Pre-provisioning complete!"
echo "Domain: ${FULL_DOMAIN}"
echo "Admin password: ${PASSWORD}"
echo "=============================================="
