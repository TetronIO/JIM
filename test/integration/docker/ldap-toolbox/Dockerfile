# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.
#
# LDAP toolbox for the Active Directory integration lab
#
# The Samba AD, OpenLDAP and 389 Directory Server labs run the directory in a container, and the
# harness runs ldapsearch, ldapmodify and friends inside it. The Active Directory lab is real Windows
# Server domain controllers running as Hyper-V virtual machines, so there is no container to run the
# tools in. This image is that container: the OpenLDAP client tools, openssl to read a domain
# controller's certificate, kpasswd, and PowerShell for the AD FS sign-in checks, on the Docker network the
# rest of the lab uses. The harness runs
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

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble@sha256:d5633778c754e89a386882eddf8dbee6942447a8a4b8267c19cf1e1158d0b0ac

ENV DEBIAN_FRONTEND=noninteractive

# krb5-user is MIT Kerberos's client tools, for kpasswd: an account an Initial Password leaves needing
# a change cannot bind over LDAP (Active Directory answers data 773), so the account holder's first
# change is made the way Windows makes it, by Kerberos password change (RFC 3244). The harness writes
# a krb5.conf for each forest into /tmp (Set-LDAPUserPasswordAsAccountHolder, #2286). It installs
# without asking for a default realm because DEBIAN_FRONTEND is noninteractive.
RUN apt-get update \
    && apt-get install -y --no-install-recommends \
        ldap-utils \
        openssl \
        ca-certificates \
        krb5-user \
        curl \
    && rm -rf /var/lib/apt/lists/*

# PowerShell, for the AD FS sign-in checks (#2441). They run here because this container is the lab's stand-in for
# an administrator's workstation: it resolves the domain controllers' and the AD FS server's names (extra_hosts) and
# reaches jim.web on the Docker network, which the runner itself cannot. Connect-JIM and the portal sign-in run in it
# with JIM's own PowerShell module. Pinned to a release and its SHA-256 (the digest GitHub publishes for the asset),
# unlike the apt packages above, because it is downloaded rather than taken from the distribution.
ARG POWERSHELL_VERSION=7.6.7
ARG POWERSHELL_SHA256=6896f9e3a6b0a354bf470835a96da76d2f801cd50dcf047ad7c4563bf93bf431
RUN curl -fsSL -o /tmp/powershell.tar.gz \
        "https://github.com/PowerShell/PowerShell/releases/download/v${POWERSHELL_VERSION}/powershell-${POWERSHELL_VERSION}-linux-x64.tar.gz" \
    && echo "${POWERSHELL_SHA256}  /tmp/powershell.tar.gz" | sha256sum -c - \
    && mkdir -p /opt/microsoft/powershell/7 \
    && tar -xzf /tmp/powershell.tar.gz -C /opt/microsoft/powershell/7 \
    && chmod +x /opt/microsoft/powershell/7/pwsh \
    && ln -s /opt/microsoft/powershell/7/pwsh /usr/local/bin/pwsh \
    && rm /tmp/powershell.tar.gz

# The base image's built-in unprivileged user (UID 1654), as the JIM images run as. The tools only read
# the mounted certificates.
USER app

CMD ["sleep", "infinity"]
