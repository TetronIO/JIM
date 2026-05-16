#!/bin/bash
# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.
#
# Self-heal stale Docker credential helper references in ~/.docker/config.json.
#
# Background: the VS Code Dev Containers extension forwards the host's Docker
# credentials into the container by writing
#   { "credsStore": "dev-containers-<uuid>" }
# into ~/.docker/config.json and installing a matching shim binary
#   /usr/local/bin/docker-credential-dev-containers-<uuid>
# on PATH. The shim proxies credential requests back to the host extension.
#
# Two failure modes leave Docker unable to pull images:
#
#  1. The shim binary is missing entirely. Symptom: Docker can't exec the
#     helper at all; every pull fails with "error getting credentials -
#     err: exit status 255".
#
#  2. The shim binary is present but its host peer has gone away (host
#     VS Code session restarted, extension reloaded, rebuild brought up a
#     new UUID while the persistent home volume preserved the old config).
#     CLI invocations return exit 1 ("credentials not found"), which Docker
#     tolerates - but BuildKit's image-resolve path treats any non-zero exit
#     as fatal and aborts with "error getting credentials - err: exit status
#     255" even for anonymous public images like docker/dockerfile:1.
#
# Either failure breaks jim-build / jim-stack / integration-test runs.
#
# Heuristic: the dev-containers-<uuid> shim exists *only* to forward host
# credentials and is unreliable from BuildKit even when the binary is on
# PATH. JIM's build path only pulls public images, so the shim is pure
# liability - strip it unconditionally whenever credsStore starts with
# "dev-containers-". For other helper names (docker-credential-desktop,
# -secretservice, -pass, etc.) keep the original behaviour: strip only if
# the binary is missing.
#
# Login-based credentials placed by `docker login` live under the "auths"
# key, not "credsStore", and are not touched.
#
# Idempotent and safe to call from both postCreateCommand and
# postStartCommand.

set -e

DOCKER_CONFIG_JSON="${HOME}/.docker/config.json"

# No config => nothing to heal.
if [ ! -f "$DOCKER_CONFIG_JSON" ]; then
    exit 0
fi

# Can't introspect JSON without jq; warn and bail rather than corrupt the file.
if ! command -v jq >/dev/null 2>&1; then
    echo "heal-docker-creds: jq not installed - skipping credsStore check" >&2
    exit 0
fi

creds_store=$(jq -r '.credsStore // empty' "$DOCKER_CONFIG_JSON" 2>/dev/null || true)

# No credsStore key, or empty value => nothing to do.
if [ -z "$creds_store" ]; then
    exit 0
fi

strip=false
reason=""
case "$creds_store" in
    dev-containers-*)
        strip=true
        reason="VS Code forwarding shim - unreliable from BuildKit even when binary is present"
        ;;
    *)
        if ! command -v "docker-credential-$creds_store" >/dev/null 2>&1; then
            strip=true
            reason="helper binary docker-credential-$creds_store not on PATH"
        fi
        ;;
esac

if [ "$strip" = true ]; then
    echo "heal-docker-creds: removing credsStore '$creds_store' ($reason)"
    tmp=$(mktemp)
    jq 'del(.credsStore)' "$DOCKER_CONFIG_JSON" > "$tmp" && mv "$tmp" "$DOCKER_CONFIG_JSON"
fi
