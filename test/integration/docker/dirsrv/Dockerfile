# 389 Directory Server for JIM Integration Testing (Multi-Suffix)
#
# The RFC 4512 sibling of the OpenLDAP lab (../openldap): the same two suffixes
# with the same tree, so the OpenLDAP populate scripts and the directory-agnostic
# scenarios run unchanged against a second directory family. Everything below is
# baked into the image by build/configure.sh at build time, so a container is
# ready the moment its healthcheck passes:
#
#   - Suffixes dc=yellowstone,dc=local (backend "yellowstone") and
#     dc=glitterband,dc=local (backend "glitterband"), each with ou=People,
#     ou=Groups and ou=Services (bootstrap/).
#   - Delegated service accounts: cn=svc-jim,ou=Services,<suffix> on each suffix,
#     plus cn=svc-jim-partitions,ou=Services,dc=yellowstone,dc=local for a
#     Connected System that imports both partitions. Access is granted to each
#     suffix's cn=jim,ou=Services,<suffix> group, never to an account's DN; the
#     accounts are members of the group(s) they manage.
#   - The jim-extensions schema (jimPerson, jimGroup, jimBadgeHolder and their
#     attributes) in cn=schema (schema/), so the LDIF the shared populate scripts
#     emit loads here too.
#   - The Retro Changelog plug-in (cn=changelog) with deleted entries logged: what
#     JIM's Delta Import reads on 389 Directory Server.
#   - A global password policy with syntax checking on and a minimum length of 7
#     (passwordCheckSyntax on, passwordMinLength 7, passwordMinCategories 3),
#     mirroring the OpenLDAP lab's pwdMinLength 7: a too-short password is
#     refused, which the scenarios that park a refused change rely on. The
#     trivial-words token length is raised from 389's default of 3 to 5
#     (passwordMinTokenLength 5), so the scenarios' shared static password is
#     not refused for whichever user's name happens to share three letters
#     with it, while a password carrying the user's own name is still refused.
#   - A lab CA and a server certificate for dirsrv-primary and localhost under
#     /data/tls, so LDAPS on 3636 presents a name-valid certificate. 389 accepts
#     the RFC 3062 Password Modify operation only over a secure connection, so
#     the Connected System connects over LDAPS 3636 (the runner adds the lab CA
#     to JIM's certificate store); the harness's own direct ldapsearch checks
#     run inside the container over plain LDAP on 3389.
#
# The access-control files under aci/ are the single source for both this lab and
# the customer recipe: they are published verbatim in
# docs/connectors/jim-ldap-connector.md, so a change here changes what customers
# are told to grant, and vice versa.
#
# All state lives under /data. The base image declares it a VOLUME, so a fresh
# named volume mounted there is seeded from the image's /data by Docker. The
# build also copies the finished instance to /data.provisioned and stamps both
# copies with the image's build hash (/data/.jim-provisioned-id, from the
# JIM_DIRSRV_BUILD_HASH build argument); start-dirsrv.sh compares the stamp in
# the mounted volume with the provisioned copy's and restores /data whenever
# they differ, so a volume left over from an older build, or one seeded from
# this image's layer under a snapshot image, is replaced rather than served.
#
# The base tag is the mutable minor-version tag on purpose: this is a test
# fixture that should follow the current 3.1.x build, not a production image.
#
# Build with (computes the hash and passes it in):
#   pwsh ./Build-DirsrvImage.ps1
#
# Or directly (the build fails without the hash; any non-empty value works for
# a throwaway image, but the runner will treat it as stale):
#   docker build --build-arg JIM_DIRSRV_BUILD_HASH=<hash> -t jim-dirsrv:primary .

FROM 389ds/dirsrv:3.1

COPY build/ /build/
COPY bootstrap/ /bootstrap/
COPY schema/ /schema/
COPY aci/ /aci/
COPY start-dirsrv.sh /usr/local/bin/start-dirsrv.sh

# The content hash of this fixture (Get-DirsrvBuildHash.ps1), passed in by
# Build-DirsrvImage.ps1. configure.sh writes it into the instance as its
# provenance stamp; the same value goes on the image as the
# jim.dirsrv.build-hash label.
ARG JIM_DIRSRV_BUILD_HASH

# The Directory Manager password used while configuring at build time is the
# published lab administrator password (test/integration/utils/Test-Helpers.ps1),
# not a secret: the compose service sets DS_DM_PASSWORD to the same value at run
# time and dscontainer re-applies it on every start. Inlined rather than an ARG so
# buildx's secrets lint does not flag a value that is public by design.
RUN chmod +x /build/configure.sh /usr/local/bin/start-dirsrv.sh \
    && JIM_DIRSRV_BUILD_HASH="$JIM_DIRSRV_BUILD_HASH" DS_DM_PASSWORD='Test@123!' /build/configure.sh

# Restores /data from the provisioned copy unless the mounted volume already
# holds this build's instance (matching provenance stamp), then hands over to
# dscontainer exactly as the base image's CMD does.
CMD ["/usr/local/bin/start-dirsrv.sh"]
