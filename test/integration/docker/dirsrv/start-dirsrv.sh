#!/bin/bash
# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.
#
# Container entry point for the JIM 389 Directory Server lab image and the
# snapshot images built on it (test/integration/Build-DirsrvSnapshots.ps1).
#
# The whole configured instance lives under /data, which the base image declares
# as a VOLUME, and the build keeps a copy of the finished instance at
# /data.provisioned. Docker seeds a fresh NAMED volume from the image layer's
# /data on first mount, and that layer is not empty: BuildKit keeps the
# build-time instance under the declared volume path. For the lab image that is
# harmless (the layer and the provisioned copy are the same instance), but for a
# snapshot image it is the trap this script exists to avoid: the layer still
# holds the BASE image's unpopulated instance, so a snapshot started on a fresh
# volume with an "is /data empty?" rule would find an instance there, start it,
# and serve no test data (verified: a probe entry baked into /data.provisioned
# was absent). Emptiness cannot be the test; provenance can.
#
# Both copies therefore carry a provenance stamp, .jim-provisioned-id: the build
# writes its image hash into /data before copying it to /data.provisioned, and a
# snapshot build overwrites the provisioned copy's id with its own. The rule:
#
#   - /data holds an instance (config/container.inf) AND its id equals the
#     provisioned copy's id: start it. A restarted container keeps its data and
#     its changelog.
#   - Otherwise, when /data.provisioned exists: wipe /data and restore the
#     provisioned copy. This replaces a volume seeded from a snapshot's base
#     layer, a volume left over from an older image build, and an empty volume
#     or bind mount alike; a disposable lab wants exactly that, and the log line
#     names which id replaced which.
#   - No instance and no provisioned copy: hand over to dscontainer, which
#     creates a bare instance.
#
# In every case the script ends by exec'ing dscontainer exactly as the base
# image's CMD would.

set -euo pipefail

DATA_DIR="/data"
PROVISIONED_DIR="/data.provisioned"
MARKER="$DATA_DIR/config/container.inf"
PROVISIONED_ID_FILE=".jim-provisioned-id"

data_id=""
provisioned_id=""
[ -f "$DATA_DIR/$PROVISIONED_ID_FILE" ] && data_id="$(cat "$DATA_DIR/$PROVISIONED_ID_FILE")"
[ -f "$PROVISIONED_DIR/$PROVISIONED_ID_FILE" ] && provisioned_id="$(cat "$PROVISIONED_DIR/$PROVISIONED_ID_FILE")"

if [ -f "$MARKER" ] && [ "$data_id" = "$provisioned_id" ]; then
    echo "[dirsrv-start] Existing instance '${data_id:-none}' found under $DATA_DIR; starting it"
elif [ -d "$PROVISIONED_DIR" ]; then
    echo "[dirsrv-start] Replacing instance '${data_id:-none}' with provisioned '${provisioned_id:-none}' from $PROVISIONED_DIR..."
    shopt -s dotglob nullglob
    rm -rf "${DATA_DIR:?}"/*
    shopt -u dotglob nullglob
    cp -a "$PROVISIONED_DIR"/. "$DATA_DIR"/
    echo "[dirsrv-start] Restored"
else
    echo "[dirsrv-start] No instance under $DATA_DIR and no provisioned copy; dscontainer will create a bare instance"
fi

exec /usr/lib/dirsrv/dscontainer -r
