# Pre-initialised Samba AD Domain Controller
# This image has domain provisioning already complete, reducing startup from ~5 minutes to ~30 seconds
#
# IMPORTANT: This image must be built using the Build-SambaImages.ps1 script, not direct docker build.
# Domain provisioning requires privileged mode which is not available during docker build.
#
# Build with:
#   pwsh ./Build-SambaImages.ps1 -Images All
#
# The image includes:
#   - Domain fully provisioned
#   - TLS/LDAPS configured with self-signed certs
#   - Password complexity disabled
#   - Test OUs created (OU=TestUsers, OU=TestGroups)
#   - SSH public key schema installed
#   - NO test users/groups (added by Populate-SambaAD.ps1)
#
# This Dockerfile is a placeholder that provides the base image and configuration.
# The actual provisioning is done by Build-SambaImages.ps1 which:
#   1. Runs a container from the base image in privileged mode
#   2. Waits for provisioning to complete
#   3. Configures TLS and test OUs
#   4. Commits the container as a new image
#
# ARCHITECTURE SUPPORT:
#   This image uses diegogslomp/samba-ad-dc as the base, which provides native
#   multi-architecture support for both AMD64 (x86_64) and ARM64 (Apple Silicon).
#   Docker will automatically pull the correct architecture for your platform.

FROM diegogslomp/samba-ad-dc:latest

# Build arguments (used by docker-compose build context)
ARG SAMBA_DOMAIN=TESTDOMAIN.LOCAL
ARG SAMBA_PASSWORD=Test@123!

# Store domain info for runtime use
# Note: diegogslomp/samba-ad-dc uses different env vars than nowsci/samba-domain:
#   - REALM = full domain (e.g., TESTDOMAIN.LOCAL)
#   - DOMAIN = short domain (e.g., TESTDOMAIN)
#   - ADMIN_PASS = administrator password
#   - DNS_FORWARDER = DNS forwarder IP
ENV REALM=${SAMBA_DOMAIN}
ENV DOMAIN=${SAMBA_DOMAIN%%.*}
ENV ADMIN_PASS=${SAMBA_PASSWORD}
ENV DNS_FORWARDER=8.8.8.8

# Also set legacy env vars for compatibility with existing scripts
ENV DOMAINPASS=${SAMBA_PASSWORD}
ENV NOCOMPLEXITY=true
ENV DNSFORWARDER=8.8.8.8

# Copy helper scripts (used during commit-based build)
COPY provision.sh /provision.sh
COPY post-provision.sh /post-provision.sh
RUN chmod +x /provision.sh /post-provision.sh

# Faster healthcheck for pre-provisioned image
# When built with Build-SambaImages.ps1, startup is much faster
# Note: samba binaries are in /usr/local/samba/bin/ for this base image
HEALTHCHECK --interval=10s --timeout=5s --start-period=30s --retries=5 \
    CMD /usr/local/samba/bin/smbclient -L localhost -U% -N || exit 1

# Default command - start Samba in foreground
# The base image uses: /bin/bash -c samba-domain-provision && samba -F
# For pre-provisioned images, we skip provisioning and just start samba
CMD ["/usr/local/samba/sbin/samba", "-F"]
