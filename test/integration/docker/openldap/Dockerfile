# OpenLDAP for JIM Integration Testing (Multi-Suffix)
#
# This image provides an OpenLDAP instance with TWO suffixes (naming contexts)
# for testing partition-scoped import run profiles (Issue #72, Phase 1b).
#
# Suffixes:
#   - dc=yellowstone,dc=local  (primary, configured via LDAP_ROOT)
#   - dc=glitterband,dc=local  (added by init script during bootstrap)
#
# Build with:
#   pwsh ./Build-OpenLdapImage.ps1
#
# Or directly:
#   docker build -t jim-openldap:primary .

FROM bitnamilegacy/openldap:latest

# Bootstrap LDIFs for the primary suffix (dc=yellowstone,dc=local)
# Bitnami loads these automatically into the LDAP_ROOT database
COPY bootstrap/ /ldifs/

# Init script to create the second MDB database (dc=glitterband,dc=local)
# Bitnami runs .sh scripts in this directory before starting slapd
COPY scripts/01-add-second-suffix.sh /docker-entrypoint-initdb.d/

# Schema extensions (if needed in future)
# COPY schema/ /schemas/
