#!/bin/bash
# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.
#
# Container entry point for the JIM 389 Directory Server lab image.
#
# The whole configured instance lives under /data, which the base image declares
# as a VOLUME. Docker seeds a fresh NAMED volume from the image's /data on first
# mount, so the ordinary compose path (dirsrv-primary-data:/data) starts with the
# baked-in instance and keeps it across restarts. Two cases do not seed: a volume
# that already exists but is empty, and a bind mount. For those, the build copied
# the finished instance to /data.provisioned, and this script restores it whenever
# /data holds no instance (no container.inf marker), then hands over to
# dscontainer exactly as the base image's CMD would. A volume that already holds
# an instance is left alone, so a restarted container keeps its data and its
# changelog.

set -euo pipefail

DATA_DIR="/data"
PROVISIONED_DIR="/data.provisioned"
MARKER="$DATA_DIR/config/container.inf"

if [ -f "$MARKER" ]; then
    echo "[dirsrv-start] Existing instance found under $DATA_DIR; starting it"
elif [ -d "$PROVISIONED_DIR" ]; then
    echo "[dirsrv-start] No instance under $DATA_DIR; restoring the provisioned lab instance from $PROVISIONED_DIR..."
    shopt -s dotglob nullglob
    rm -rf "${DATA_DIR:?}"/*
    shopt -u dotglob nullglob
    cp -a "$PROVISIONED_DIR"/. "$DATA_DIR"/
    echo "[dirsrv-start] Restored"
else
    echo "[dirsrv-start] No instance under $DATA_DIR and no provisioned copy; dscontainer will create a bare instance"
fi

exec /usr/lib/dirsrv/dscontainer -r
