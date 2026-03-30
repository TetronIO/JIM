#!/bin/bash
# Restore provisioned OpenLDAP data if volumes are empty (snapshot startup).
#
# docker commit does not capture Docker volumes. During snapshot build, the
# populated /bitnami/openldap data is copied to /bitnami/openldap.provisioned
# inside the container filesystem (which IS captured). On startup, if the
# volume is empty (fresh mount), we restore from the provisioned copy.

PROVISIONED_DIR="/bitnami/openldap.provisioned"
DATA_DIR="/bitnami/openldap"

if [ -d "$PROVISIONED_DIR" ] && [ -z "$(ls -A "$DATA_DIR/data" 2>/dev/null)" ]; then
    echo "[openldap-snapshot] Volume is empty — restoring provisioned data from snapshot..."
    # Remove any empty dirs created by Docker volume mount
    rm -rf "${DATA_DIR:?}"/*
    cp -a "$PROVISIONED_DIR"/* "$DATA_DIR/"
    echo "[openldap-snapshot] Data restored successfully"
else
    if [ ! -d "$PROVISIONED_DIR" ]; then
        echo "[openldap-snapshot] No provisioned data found — running as base image"
    else
        echo "[openldap-snapshot] Volume already has data — skipping restore"
    fi
fi

# Hand off to the original Bitnami entrypoint
exec /opt/bitnami/scripts/openldap/entrypoint.sh /opt/bitnami/scripts/openldap/run.sh
