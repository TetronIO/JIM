# OpenLDAP Integration Testing

- **Status:** Planned
- **Created:** 2026-03-09

## Overview

Introduce OpenLDAP as a second LDAP backend for integration testing. At large scales (XLarge: 100K users), Samba AD cannot handle JIM's I/O demands — tests take excessively long. OpenLDAP is significantly faster for concurrent read/write workloads and represents a real-world deployment target that JIM must support.

This plan covers everything needed to run existing LDAP integration test scenarios against OpenLDAP as an alternative to Samba AD: Docker infrastructure, test data population, test framework parameterisation, and connector code fixes.

## Business Value

- **Performance at scale**: OpenLDAP handles concurrent LDAP operations far better than Samba AD, unblocking XLarge/XXLarge integration tests
- **Broader compatibility**: Customers deploy JIM against OpenLDAP, 389DS, and other RFC-compliant directories — not just Active Directory
- **Earlier bug detection**: Exercising the non-AD code paths (changelog delta import, RFC schema discovery, `entryUUID` handling) that exist in the connector but have never been tested against a real directory
- **Confidence**: Proves JIM works correctly with pure LDAP directories, not just AD-compatible ones

## Approach: Parameterised Tests (Not Cloned)

**Recommendation: Add a `-DirectoryType` parameter to the test runner**, not duplicate test scripts.

```powershell
./Run-IntegrationTests.ps1 -Scenario Scenario1-HRToIdentityDirectory -Template Small -DirectoryType OpenLDAP
```

Rationale:
- Cloning every LDAP scenario doubles maintenance — bug fixes need applying in two places
- The differences between Samba AD and OpenLDAP are well-bounded (connection settings, object classes, attribute names, DN format)
- A directory-type configuration abstraction keeps scenario logic shared while varying only the LDAP-specific details

## Key Differences: Samba AD vs OpenLDAP

| Aspect | Samba AD | OpenLDAP |
|--------|----------|----------|
| **Schema discovery** | AD-style `classSchema`/`attributeSchema` in `CN=Schema,CN=Configuration` | RFC 4512 subschema subentry (`cn=Subschema`) |
| **External ID** | `objectGUID` (binary, Microsoft byte order) | `entryUUID` (string, RFC 4530) |
| **Object classes** | `user`, `group`, `computer` | `inetOrgPerson`, `groupOfNames`/`groupOfUniqueNames`, `posixAccount` |
| **User naming** | `sAMAccountName`, `userPrincipalName`, `CN=DisplayName` | `uid`, `cn`, no UPN equivalent |
| **Group membership** | `member` (DN-valued) / `memberOf` (back-link) | `member` on `groupOfNames`, or `uniqueMember` on `groupOfUniqueNames` |
| **Account disable** | `userAccountControl` bitmask (0x2) | No native concept — typically `pwdAccountLockedTime` (ppolicy overlay) or custom attribute |
| **DN format** | `CN=Name,OU=Users,DC=domain,DC=local` | `uid=username,ou=People,dc=openldap,dc=local` |
| **Delta import** | USN-based (`uSNChanged`, tombstones) | Changelog (`cn=changelog`, `changeNumber`) — already implemented in connector |
| **Paging** | Supported (disabled for Samba due to duplicate results) | Supported via Simple Paged Results control |
| **Protected attributes** | `nTSecurityDescriptor`, `userAccountControl`, etc. (SAM layer) | None |
| **Authentication** | Simple bind or NTLM | Simple bind only (no NTLM) |
| **Bulk population** | `ldbadd` into `sam.ldb` (bypasses LDAP protocol, very fast) | `ldapadd` via LDAP protocol with LDIF |
| **Privileged mode** | Required (Samba DC) | **Not required** |
| **Startup time** | ~30 seconds (pre-built image) | ~5 seconds |

## Docker Image Strategy

### Base Image: `bitnami/openldap`

The existing `docker-compose.integration-tests.yml` references `osixia/openldap:latest` but this image is **unmaintained** (last meaningful update 2022, Debian Stretch EOL, open CVEs). Replace with `bitnami/openldap`:

| Criteria | `osixia/openldap` | `bitnami/openldap` |
|----------|--------------------|--------------------|
| Last updated | 2022 | Weekly |
| Maintainer | Individual | VMware/Broadcom |
| Base OS | Debian Stretch (EOL) | Debian Bookworm |
| Non-root | No | Yes (default) |
| Bootstrap LDIF | Via `LDAP_SEED_INTERNAL_LDIF_PATH` | Via `LDAP_CUSTOM_LDIF_DIR` |
| Custom schema | Requires manual slapd.d edits | `LDAP_CUSTOM_SCHEMA_FILE` env var |
| Multi-arch | AMD64 only | AMD64 + ARM64 |
| Default port | 389 | 1389 (non-root) |

### Pre-built Image

Unlike Samba AD (which requires `docker commit` after privileged provisioning), OpenLDAP can use a **standard Dockerfile build**:

```
test/integration/docker/openldap/
  Dockerfile                      # FROM bitnami/openldap, COPY schema + bootstrap LDIF
  schema/
    jim-test-extensions.ldif      # Custom schema (extensionAttributes equivalent if needed)
  bootstrap/
    01-base-ous.ldif              # OU=People, OU=Groups structure
  Build-OpenLdapImage.ps1         # Build script (standard docker build, no privileged commit)
```

The Dockerfile copies bootstrap LDIFs to the auto-load directory. On first start, OpenLDAP loads them automatically. No `docker commit` workflow, no privileged mode.

**Build script** (`Build-OpenLdapImage.ps1`) is simpler than the Samba equivalent — just wraps `docker build` with tagging and optional push to `ghcr.io/tetronio/jim-openldap:primary`.

### Docker Compose Configuration

Replace the existing `osixia/openldap` service in `docker-compose.integration-tests.yml`:

```yaml
# OpenLDAP primary instance
openldap-primary:
  image: ${OPENLDAP_IMAGE_PRIMARY:-ghcr.io/tetronio/jim-openldap:primary}
  build:
    context: ./test/integration/docker/openldap
    dockerfile: Dockerfile
  container_name: openldap-primary
  environment:
    - LDAP_ROOT=dc=openldap,dc=local
    - LDAP_ADMIN_DN=cn=admin,dc=openldap,dc=local
    - LDAP_ADMIN_PASSWORD=Test@123!
    - LDAP_ENABLE_TLS=no
    - LDAP_CUSTOM_LDIF_DIR=/ldifs
  volumes:
    - openldap-primary-data:/bitnami/openldap
    - test-csv-data:/connector-files
  networks:
    - jim-network
  profiles:
    - openldap
  healthcheck:
    test: ["CMD", "ldapsearch", "-x", "-H", "ldap://localhost:1389",
           "-b", "dc=openldap,dc=local",
           "-D", "cn=admin,dc=openldap,dc=local", "-w", "Test@123!"]
    interval: 10s
    timeout: 5s
    retries: 5
    start_period: 10s
  deploy:
    resources:
      limits:
        cpus: ${OPENLDAP_PRIMARY_CPUS:-2.0}
        memory: ${OPENLDAP_PRIMARY_MEMORY:-2G}
      reservations:
        cpus: ${OPENLDAP_PRIMARY_CPUS_RESERVED:-0.5}
        memory: ${OPENLDAP_PRIMARY_MEMORY_RESERVED:-512M}
```

Key points:
- Profile `openldap` — only started when OpenLDAP tests are requested
- Port 1389 (bitnami non-root default) — not exposed to host, internal to `jim-network`
- No privileged mode required
- Remove the existing `osixia/openldap` service definition and its volumes (`openldap-data`, `openldap-config`)

### Bootstrap OU Structure

The bootstrap LDIF (`01-base-ous.ldif`) creates the equivalent of Samba AD's baseline OUs:

```ldif
# OU=People — equivalent to OU=Users,OU=Corp in Samba AD
dn: ou=People,dc=openldap,dc=local
objectClass: organizationalUnit
ou: People

# OU=Groups — equivalent to OU=Groups,OU=Corp in Samba AD
dn: ou=Groups,dc=openldap,dc=local
objectClass: organizationalUnit
ou: Groups

# Department OUs under People (created dynamically by Populate-OpenLDAP.ps1)
```

### OpenLDAP Changelog Overlay (for Delta Import)

The connector's changelog-based delta import queries `cn=changelog` for entries with `changeNumber > lastProcessed`. This requires the **accesslog overlay** (`slapo-accesslog`) to be enabled.

The Dockerfile or bootstrap config must enable this overlay. For `bitnami/openldap`, this can be done via a custom schema/config LDIF or by setting:

```
LDAP_EXTRA_SCHEMAS=accesslog
```

If the accesslog overlay is not straightforward to configure via environment variables, it can be configured via a bootstrap LDIF that modifies `cn=config`. This needs investigation during implementation — if too complex, delta import testing can be deferred (full import is the priority).

## Test Data Population: `Populate-OpenLDAP.ps1`

New script parallel to `Populate-SambaAD.ps1`. Generates LDIF and loads via `ldapadd`.

### Object Mapping

| Samba AD | OpenLDAP | Notes |
|----------|----------|-------|
| `user` object class | `inetOrgPerson` + `organizationalPerson` + `person` | Standard RFC 2798 person entry |
| `group` object class | `groupOfNames` | Requires at least one `member` (MUST attribute) |
| `CN=FirstName LastName` RDN | `uid=firstname.lastname` RDN | Different naming convention |
| `sAMAccountName` | `uid` | Username attribute |
| `userPrincipalName` | Not applicable | No UPN concept in OpenLDAP |
| `displayName` | `displayName` | Same attribute, available on `inetOrgPerson` |
| `givenName` | `givenName` | Same |
| `sn` | `sn` | Same |
| `mail` | `mail` | Same |
| `title` | `title` | Same |
| `department` | `departmentNumber` or `ou` | `department` not in RFC schema; use `departmentNumber` |
| `accountExpires` (Windows FILETIME) | Not applicable | No native equivalent; skip or use custom attribute |
| `objectGUID` (auto-assigned binary) | `entryUUID` (auto-assigned string) | Different format, already documented in connector |
| `unicodePwd` (binary) | `userPassword` (SSHA hash) | Different password storage |
| `member` (on group) | `member` (on `groupOfNames`) | Same attribute name, same DN-valued semantics |
| `memberOf` (back-link, auto-computed) | `memberOf` (memberof overlay, if enabled) | Requires `slapo-memberof` overlay |

### LDIF Generation

The script generates standard LDIF entries. Example user:

```ldif
dn: uid=john.smith,ou=People,dc=openldap,dc=local
objectClass: inetOrgPerson
objectClass: organizationalPerson
objectClass: person
objectClass: top
uid: john.smith
cn: John Smith
sn: Smith
givenName: John
displayName: John Smith
mail: john.smith@openldap.local
title: Software Engineer
departmentNumber: Engineering
userPassword: {SSHA}base64encodedpassword
```

Example group:

```ldif
dn: cn=Engineering,ou=Groups,dc=openldap,dc=local
objectClass: groupOfNames
cn: Engineering
description: Engineering department group
member: uid=john.smith,ou=People,dc=openldap,dc=local
```

### Bulk Loading

- Use `docker exec openldap-primary ldapadd -x -D "cn=admin,dc=openldap,dc=local" -w "Test@123!" -c -f /path/to/users.ldif`
- The `-c` flag continues on errors (useful for idempotent re-runs)
- Batch into chunks of ~10,000 entries per LDIF file for large templates
- LDIF files can be written to the shared `test-csv-data` volume (mounted at `/connector-files`)

### Performance Considerations

Population at scale is extremely slow. For reference, populating the Samba AD XLarge image (100K users + 50 groups with varied memberships) via `ldbadd` (direct backend write bypassing the LDAP protocol) takes several hours. Group counts were capped from the original 2,000 to 50 to keep total memberships under ~500K (samba-tool holds an LDB write lock per call, making millions of membership writes impractical). OpenLDAP's `ldapadd` goes through the full LDAP protocol stack and will be at least as slow, potentially slower.

**Pre-populated snapshot images are essential for Medium and above.** The same approach used for Samba AD (`Build-SambaSnapshots.ps1` / `docker commit`) must be used for OpenLDAP:

1. Build a base OpenLDAP image with OUs and schema
2. Start container, populate with data (see loading strategies below)
3. `docker commit` the populated container as a snapshot image per template size
4. Push to `ghcr.io/tetronio/jim-openldap:{template}` for reuse

For Nano/Micro/Small templates, live population via `ldapadd` is fast enough (seconds to a few minutes). For Medium and above, snapshot images avoid repeating the population cost on every test run.

**Loading strategies for building snapshot images:**

| Strategy | Method | Speed | Availability |
|----------|--------|-------|--------------|
| `ldapadd` | LDAP protocol, entries processed one by one | Slowest — potentially days at XLarge | Always available |
| `slapadd` | Offline backend load, bypasses LDAP stack | Much faster — similar to Samba's `ldbadd` | Requires `slapd` to be stopped; may not be in bitnami image |
| MDB bulk import | Direct LMDB database write | Fastest | Would require custom tooling |

**Recommendation**: Investigate whether the `bitnami/openldap` image includes `slapadd`. If so, the snapshot build script should: stop `slapd` -> run `slapadd` with the LDIF -> restart `slapd` -> `docker commit`. This could reduce XLarge population from days to hours. If `slapadd` is not available, fall back to `ldapadd` and accept that XLarge snapshot builds will be a multi-day operation (run once, commit, reuse).

## Test Framework Changes

### Runner Parameter: `-DirectoryType`

Add to `Run-IntegrationTests.ps1`:

```powershell
[Parameter(Mandatory=$false)]
[ValidateSet("SambaAD", "OpenLDAP")]
[string]$DirectoryType = "SambaAD"
```

This parameter controls:
1. Which Docker Compose profile to start (`scenario1` vs `openldap`)
2. Which population script to run (`Populate-SambaAD.ps1` vs `Populate-OpenLDAP.ps1`)
3. Which directory config to pass to setup/scenario scripts
4. Which LDAP helper functions to use for validation

### Directory Configuration Abstraction

Create a `Get-DirectoryConfig` function in `Test-Helpers.ps1` that returns a hashtable with all directory-specific values:

```powershell
function Get-DirectoryConfig {
    param(
        [ValidateSet("SambaAD", "OpenLDAP")]
        [string]$DirectoryType,
        [string]$Instance = "Primary"
    )

    switch ($DirectoryType) {
        "SambaAD" {
            return @{
                ContainerName    = "samba-ad-primary"
                Host             = "samba-ad-primary"
                Port             = 636
                UseSSL           = $true
                CertValidation   = "Skip Validation (Not Recommended)"
                BindDN           = "CN=Administrator,CN=Users,DC=subatomic,DC=local"
                BindPassword     = "Test@123!"
                AuthType         = "Simple"
                BaseDN           = "DC=subatomic,DC=local"
                UserContainer    = "OU=Users,OU=Corp,DC=subatomic,DC=local"
                GroupContainer   = "OU=Groups,OU=Corp,DC=subatomic,DC=local"
                UserObjectClass  = "user"
                GroupObjectClass = "group"
                UserRdnAttr      = "CN"
                UserNameAttr     = "sAMAccountName"
                DepartmentAttr   = "department"
                DeleteBehaviour  = "Disable"
                DisableAttribute = "userAccountControl"
                DnTemplate       = 'CN={displayName},OU=Users,OU=Corp,DC=subatomic,DC=local'
            }
        }
        "OpenLDAP" {
            return @{
                ContainerName    = "openldap-primary"
                Host             = "openldap-primary"
                Port             = 1389
                UseSSL           = $false
                CertValidation   = $null
                BindDN           = "cn=admin,dc=openldap,dc=local"
                BindPassword     = "Test@123!"
                AuthType         = "Simple"
                BaseDN           = "dc=openldap,dc=local"
                UserContainer    = "ou=People,dc=openldap,dc=local"
                GroupContainer   = "ou=Groups,dc=openldap,dc=local"
                UserObjectClass  = "inetOrgPerson"
                GroupObjectClass = "groupOfNames"
                UserRdnAttr      = "uid"
                UserNameAttr     = "uid"
                DepartmentAttr   = "departmentNumber"
                DeleteBehaviour  = "Delete"
                DisableAttribute = $null
                DnTemplate       = 'uid={uid},ou=People,dc=openldap,dc=local'
            }
        }
    }
}
```

This config is passed through to setup scripts, scenario scripts, and LDAP helper functions.

### LDAP Helper Refactoring

Refactor `test/integration/utils/LDAP-Helpers.ps1` to accept a directory config instead of hardcoding Samba AD:

```powershell
function Get-LDAPUser {
    param(
        [Parameter(Mandatory=$true)]
        [string]$UserIdentifier,
        [Parameter(Mandatory=$true)]
        [hashtable]$DirectoryConfig
    )

    $container = $DirectoryConfig.ContainerName
    $filter = "($($DirectoryConfig.UserNameAttr)=$UserIdentifier)"
    $baseDN = $DirectoryConfig.BaseDN
    $bindDN = $DirectoryConfig.BindDN
    $bindPassword = $DirectoryConfig.BindPassword
    $port = $DirectoryConfig.Port

    $result = docker exec $container ldapsearch `
        -x -H "ldap://localhost:$port" `
        -D $bindDN -w $bindPassword `
        -b $baseDN $filter 2>&1

    # ... parse LDIF output (same logic)
}
```

### Setup Script Parameterisation

Each `Setup-Scenario*.ps1` receives the directory config and uses it for LDAP Connected System configuration:

```powershell
# In Setup-Scenario1.ps1 (parameterised)
param(
    # ... existing params ...
    [Parameter(Mandatory=$false)]
    [hashtable]$DirectoryConfig
)

# LDAP Connected System settings use $DirectoryConfig values
$ldapSettings = @{
    "Host"                       = $DirectoryConfig.Host
    "Port"                       = $DirectoryConfig.Port
    "Use Secure Connection"      = $DirectoryConfig.UseSSL
    "Certificate Validation"     = $DirectoryConfig.CertValidation
    "Username"                   = $DirectoryConfig.BindDN
    "Password"                   = $DirectoryConfig.BindPassword
    "Authentication Type"        = $DirectoryConfig.AuthType
    "Delete Behaviour"           = $DirectoryConfig.DeleteBehaviour
    "Disable Attribute"          = $DirectoryConfig.DisableAttribute
}
```

Sync rule attribute mappings also vary by directory type — the setup script branches on `$DirectoryConfig.UserObjectClass` to select the appropriate attribute names.

## Connector Code Gaps

These are areas in the LDAP connector that need fixing before OpenLDAP integration tests can pass. Listed in order of criticality.

### Gap 1: Schema Discovery (Critical)

**File**: `src/JIM.Connectors/LDAP/LdapConnectorSchema.cs`

**Problem**: Schema discovery is entirely AD-specific. It queries `classSchema`/`attributeSchema` objects in the AD schema partition using AD-only filters (`objectClassCategory=1`, `defaultHidingValue`, `isDefunct`, `ldapdisplayname`, `omsyntax`, etc.). OpenLDAP uses the RFC 4512 subschema subentry mechanism — a completely different query approach.

**Required**: Detect directory type (already available via `LdapConnectorRootDse.IsActiveDirectory`) and branch:
- **AD path** (existing): Current `classSchema`/`attributeSchema` queries
- **RFC path** (new): Query `cn=Subschema` (or the value from `subschemaSubentry` on rootDSE) for `objectClasses` and `attributeTypes` attributes, then parse RFC 4512 syntax descriptions

**Complexity**: High — this is the biggest piece of work.

### Gap 2: External ID Recommendation (Critical)

**File**: `src/JIM.Connectors/LDAP/LdapConnectorSchema.cs` (line 55)

**Problem**: Hardcoded assumption that `objectGUID` exists:
```csharp
var objectGuidSchemaAttribute = objectType.Attributes.Single(
    a => a.Name.Equals("objectguid", StringComparison.OrdinalIgnoreCase));
```

This will throw `InvalidOperationException` for OpenLDAP (which has `entryUUID`, not `objectGUID`).

**Required**: Check `IsActiveDirectory` and recommend `entryUUID` (string type) for non-AD directories. The connector utility code already documents this distinction (see `GetEntryAttributeGuidValues` XML docs).

### Gap 3: Partition Discovery

**File**: `src/JIM.Connectors/LDAP/LdapConnectorPartitions.cs`

**Problem**: AD-specific logic uses `systemFlags` to filter domain partitions vs configuration/schema partitions. OpenLDAP exposes `namingContexts` on rootDSE but has no `systemFlags`.

**Required**: For non-AD directories, simply return all `namingContexts` as partitions without `systemFlags` filtering.

### Gap 4: Changelog Delta Import (Testing Required)

**File**: `src/JIM.Connectors/LDAP/LdapConnectorImport.cs` (`GetDeltaResultsUsingChangelog`)

**Status**: Code exists and compiles but has **never been tested** against a real directory. The implementation queries `cn=changelog` for entries with `changeNumber > lastProcessed` and maps `changeType` to `ObjectChangeType`.

**Required**: Test against OpenLDAP with accesslog overlay enabled. Likely needs bug fixes once exercised with real data.

### Gap 5: Protected Attribute Handling

**File**: `src/JIM.Connectors/LDAP/LdapConnectorExport.cs`

**Status**: The export code has AD-specific protected attribute lists and `userAccountControl` handling. These should already be gated behind `IsActiveDirectory` checks in many places, but needs verification that no AD-specific logic runs unconditionally.

### Gap 6: `groupOfNames` Empty Group Constraint

**Problem**: The `groupOfNames` object class requires at least one `member` value (it is a MUST attribute in the schema). AD's `group` class has no such constraint — groups can be empty.

**Impact**: When JIM exports a new group with no members, or removes the last member from a group, the export will fail against OpenLDAP.

**Required**: For non-AD directories using `groupOfNames`, either:
- Add a placeholder member (e.g., the admin DN) when the group would otherwise be empty
- Use `groupOfUniqueNames` instead (same constraint, but using `uniqueMember`)
- Document this as a known limitation

## Implementation Phases

### Phase 1: Docker Infrastructure

**Deliverables:**
- `test/integration/docker/openldap/Dockerfile`
- `test/integration/docker/openldap/bootstrap/01-base-ous.ldif`
- `test/integration/docker/openldap/Build-OpenLdapImage.ps1`
- Updated `docker-compose.integration-tests.yml` (replace `osixia/openldap` with `bitnami/openldap`, `openldap` profile)
- Manual verification: container starts, health check passes, admin can bind and search

### Phase 2: Test Data Population

**Deliverables:**
- `test/integration/Populate-OpenLDAP.ps1` — generates LDIF for `inetOrgPerson` users and `groupOfNames` groups, loads via `ldapadd`
- Manual verification: population works at Nano/Micro/Small scales, users and groups queryable

### Phase 3: Test Framework Parameterisation

**Deliverables:**
- `Get-DirectoryConfig` function in `Test-Helpers.ps1`
- `-DirectoryType` parameter on `Run-IntegrationTests.ps1`
- Refactored `LDAP-Helpers.ps1` to accept directory config
- Parameterised `Setup-Scenario1.ps1` (start with Scenario 1 as the pilot)
- Parameterised `Invoke-Scenario1.ps1` validation assertions

### Phase 4: Connector Fixes

**Deliverables:**
- RFC 4512 schema discovery path in `LdapConnectorSchema.cs`
- `entryUUID` external ID recommendation for non-AD directories
- Partition discovery fix for non-AD directories
- Export handling for directory-specific differences
- Unit tests for all new/modified connector code

### Phase 5: End-to-End Validation

**Deliverables:**
- Scenario 1 (HR → OpenLDAP) passing at Nano scale
- Fix issues found during E2E testing
- Scale up through Micro → Small → Medium
- Changelog-based delta import testing (if accesslog overlay configured)
- Document any remaining limitations

### Phase 6: Remaining Scenarios (Future)

**Deliverables:**
- Parameterise Scenarios 2, 4, 5, 8 for OpenLDAP
- OpenLDAP-to-OpenLDAP cross-directory sync (Scenario 2 equivalent)
- XLarge-scale performance benchmarking against OpenLDAP

## Risks and Mitigations

| Risk | Impact | Likelihood | Mitigation |
|------|--------|------------|------------|
| Schema discovery rewrite is complex | High | High | Phase 4 is the largest work item; consider starting with a minimal RFC schema parser that handles `inetOrgPerson` and `groupOfNames` only, expanding later |
| OpenLDAP changelog overlay hard to configure | Medium | Medium | If accesslog overlay is too complex to enable in Docker, defer delta import testing — full import covers the critical path |
| `bitnami/openldap` doesn't support `slapadd` for bulk loading | Medium | Medium | Fall back to `ldapadd` (slower but functional); consider pre-built populated images for XLarge |
| `groupOfNames` empty group constraint breaks export | Medium | High | Needs connector code change; temporary mitigation is to always ensure groups have at least one member in test data |
| Performance regression at XLarge if OpenLDAP population is slow | Low | Medium | Build pre-populated snapshot images (like Samba approach) for Large/XLarge templates |

## Success Criteria

- [ ] OpenLDAP container starts and is healthy in integration test environment
- [ ] `Populate-OpenLDAP.ps1` successfully creates users and groups at Small template scale
- [ ] `Run-IntegrationTests.ps1 -DirectoryType OpenLDAP` parameter works and selects correct infrastructure
- [ ] JIM LDAP connector can connect to OpenLDAP, discover schema, and refresh partitions
- [ ] Scenario 1 Joiner step passes against OpenLDAP (CSV → JIM → OpenLDAP provisioning)
- [ ] Scenario 1 Mover and Leaver steps pass against OpenLDAP
- [ ] Delta import works against OpenLDAP (changelog-based or full import fallback documented)
- [ ] All existing Samba AD tests continue to pass unchanged (no regressions)
