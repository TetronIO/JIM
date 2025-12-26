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

# Start Samba
exec /usr/local/samba/sbin/samba -F
