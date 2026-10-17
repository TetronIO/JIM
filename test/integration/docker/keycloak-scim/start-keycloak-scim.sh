#!/bin/bash
# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

# Starts Keycloak for Scenario 028 (the SCIM 2.0 Client Connector against Keycloak's SCIM API, #2349).
#
# The connector refuses plain HTTP to anything but a loopback address, and Keycloak checks that a token was issued
# for its SCIM API at the address it knows itself by, so the container serves HTTPS under the name JIM calls it by
# (KEYCLOAK_SCIM_HOSTNAME, its name on the Docker network). The certificate is self-signed and minted here with the
# image's own keytool, so no further image is needed; the scenario copies the PEM out and adds it to JIM's Trusted
# Certificates. Plain HTTP stays on inside the container only, for kcadm.sh to configure the realm over localhost.
set -euo pipefail

hostname="${KEYCLOAK_SCIM_HOSTNAME:-keycloak-scim}"
tls_dir=/tmp/tls
mkdir -p "$tls_dir"

keytool -genkeypair -alias scim -keyalg RSA -keysize 2048 -validity 30 \
  -dname "CN=$hostname" -ext "SAN=dns:$hostname,dns:localhost" \
  -keystore "$tls_dir/keystore.p12" -storetype PKCS12 -storepass changeit -keypass changeit -noprompt
keytool -exportcert -rfc -alias scim -keystore "$tls_dir/keystore.p12" -storepass changeit \
  -file "$tls_dir/keycloak-scim.pem"

exec /opt/keycloak/bin/kc.sh start-dev \
  --https-port=8443 \
  --https-key-store-file="$tls_dir/keystore.p12" \
  --https-key-store-password=changeit \
  --hostname="https://$hostname:8443" \
  --hostname-backchannel-dynamic=true \
  --http-enabled=true \
  --health-enabled=true \
  --http-management-scheme=http
