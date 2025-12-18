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

FROM nowsci/samba-domain:latest

# Build arguments (used by docker-compose build context)
ARG SAMBA_DOMAIN=TESTDOMAIN.LOCAL
ARG SAMBA_PASSWORD=Test@123!

# Store domain info for runtime use
ENV DOMAIN=${SAMBA_DOMAIN}
ENV DOMAINPASS=${SAMBA_PASSWORD}
ENV NOCOMPLEXITY=true
ENV DNSFORWARDER=8.8.8.8

# Copy helper scripts (used during commit-based build)
COPY provision.sh /provision.sh
COPY post-provision.sh /post-provision.sh
RUN chmod +x /provision.sh /post-provision.sh

# Faster healthcheck for pre-provisioned image
# When built with Build-SambaImages.ps1, startup is much faster
HEALTHCHECK --interval=10s --timeout=5s --start-period=30s --retries=5 \
    CMD smbclient -L localhost -U% -N || exit 1

# Default command remains the same - starts supervisord with samba
CMD ["/bin/sh", "-c", "/init.sh"]
