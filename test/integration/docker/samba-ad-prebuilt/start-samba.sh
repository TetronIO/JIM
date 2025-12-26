#!/bin/bash
# Restore provisioned data if volumes are empty
if [ ! -f /usr/local/samba/private/secrets.keytab ]; then
    echo "Restoring provisioned Samba data..."
    cp -a /usr/local/samba/etc.provisioned/* /usr/local/samba/etc/ 2>/dev/null || true
    cp -a /usr/local/samba/private.provisioned/* /usr/local/samba/private/ 2>/dev/null || true
    cp -a /usr/local/samba/var.provisioned/* /usr/local/samba/var/ 2>/dev/null || true
fi

# Update network-dependent files
/usr/local/sbin/update-etc-files 2>/dev/null || true

# Ensure LDAP/LDAPS bind to all interfaces (not just localhost)
# This allows other containers to connect via Docker network
SMB_CONF="/usr/local/samba/etc/smb.conf"
if [ -f "$SMB_CONF" ]; then
    # Replace existing interfaces line to include eth0
    sed -i 's/interfaces = .*/interfaces = lo eth0/' "$SMB_CONF"
    # Set bind interfaces only to No so we accept connections on all interfaces
    sed -i 's/bind interfaces only = Yes/bind interfaces only = No/' "$SMB_CONF"
fi

# Start Samba
exec /usr/local/samba/sbin/samba -F
