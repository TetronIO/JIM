# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.
#
# LDAP toolbox for the Active Directory integration lab
#
# The Samba AD, OpenLDAP and 389 Directory Server labs run the directory in a container, and the
# harness runs ldapsearch, ldapmodify and friends inside it. The Active Directory lab is real Windows
# Server domain controllers running as Hyper-V virtual machines, so there is no container to run the
# tools in. This image is that container: the OpenLDAP client tools, and openssl to read a domain
# controller's certificate, on the Docker network the rest of the lab uses. The harness runs
# "docker exec jim-ldap-toolbox ldapsearch -H ldaps://<dc fqdn>:636 ..." (see Invoke-LdapTool in
# test/integration/utils/LDAP-Helpers.ps1).
#
# Windows Server enforces LDAP signing, so every connection is LDAPS, and it is verified: the compose
# service sets LDAPTLS_CACERT to /certs/ad-lab-ca.pem, a bundle of the domain controllers' own
# certificates that the harness writes into ./test/integration/ad-lab-certs (bind-mounted at /certs)
# once it has read each one off the wire and checked its Subject Alternative Names.
#
# Why this base image rather than a smaller one (alpine, debian-slim, ubuntu): it is the aspnet image
# JIM itself is built on (src/JIM.Web/Dockerfile), at the same digest, so this adds no new base image
# to review, approve or track (engineering/DEPENDENCY_PINNING.md; root CLAUDE.md, Third-Party
# Dependency Governance). It is also noble, so ldap-utils is OpenLDAP 2.6, the same client library
# generation JIM's LDAP Connector talks through. The cost, a few hundred megabytes of .NET runtime this
# container never uses, is paid once per machine and does not matter for a test tool.
#
# Keep the digest below in step with the aspnet digest in src/JIM.Web/Dockerfile. Dependabot does not
# watch test/integration/docker, so it will not bump this line for you. The apt packages float, as
# every integration test image's do (DEPENDENCY_PINNING.md, integration test images); nothing here is
# shipped.

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble@sha256:a4556ed033fa96f984bb7a8d348851cb2d36b1281dd2420070045f664fbb5f94

ENV DEBIAN_FRONTEND=noninteractive

RUN apt-get update \
    && apt-get install -y --no-install-recommends \
        ldap-utils \
        openssl \
        ca-certificates \
    && rm -rf /var/lib/apt/lists/*

# The base image's built-in unprivileged user (UID 1654), as the JIM images run as. The tools only read
# the mounted certificates.
USER app

CMD ["sleep", "infinity"]
