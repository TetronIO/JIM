# OpenLDAP Integration Testing

- **Status:** Done (Phases 1-6 complete — all scenarios pass on both SambaAD and OpenLDAP at Medium; S3 deferred)
- **Created:** 2026-03-09
- **Issue:** [#72](https://github.com/TetronIO/JIM/issues/72)

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
| **DN format** | `CN=Name,OU=Users,DC=domain,DC=local` | `uid=username,ou=People,dc=yellowstone,dc=local` |
| **Delta import** | USN-based (`uSNChanged`, tombstones) | Changelog (`cn=changelog`, `changeNumber`) — already implemented in connector |
| **Paging** | Supported (disabled for Samba due to duplicate results) | Supported via Simple Paged Results control |
| **Protected attributes** | `nTSecurityDescriptor`, `userAccountControl`, etc. (SAM layer) | None |
| **Authentication** | Simple bind or NTLM | Simple bind only (no NTLM) |
| **Bulk population** | `ldbadd` into `sam.ldb` (bypasses LDAP protocol, very fast) | `ldapadd` via LDAP protocol with LDIF |
| **Privileged mode** | Required (Samba DC) | **Not required** |
| **Startup time** | ~30 seconds (pre-built image) | ~5 seconds |

## Docker Image Strategy

### Base Image: `bitnamilegacy/openldap` ✅

Bitnami migrated their images off Docker Hub in August 2025. The `bitnamilegacy/openldap:latest` image (OpenLDAP 2.6.10) is the successor, replacing the unmaintained `osixia/openldap:latest`:

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

### Docker Compose Configuration ✅

The `osixia/openldap` service has been replaced in `docker-compose.integration-tests.yml`. See the actual compose file for the current configuration. Key points:

- Profile `openldap` — only started when OpenLDAP tests are requested
- Port 1389 (bitnami non-root default) — not exposed to host, internal to `jim-network`
- No privileged mode required
- Two suffixes: `dc=yellowstone,dc=local` (primary via `LDAP_ROOT`) and `dc=glitterband,dc=local` (added by init script)
- Accesslog overlay enabled for future delta import testing
- Config admin enabled for `cn=config` modifications by the init script

### Bootstrap OU Structure ✅

The bootstrap LDIF (`01-base-ous-yellowstone.ldif`) creates the equivalent of Samba AD's baseline OUs for the primary suffix. The second suffix (`dc=glitterband,dc=local`) is created by the init script `01-add-second-suffix.sh`:

```ldif
# OU=People — equivalent to OU=Users,OU=Corp in Samba AD
dn: ou=People,dc=yellowstone,dc=local
objectClass: organizationalUnit
ou: People

# OU=Groups — equivalent to OU=Groups,OU=Corp in Samba AD
dn: ou=Groups,dc=yellowstone,dc=local
objectClass: organizationalUnit
ou: Groups

# Department OUs under People (created dynamically by Populate-OpenLDAP.ps1)
```

### OpenLDAP Changelog Overlay (for Delta Import) ✅

The connector's changelog-based delta import queries `cn=changelog` for entries with `changeNumber > lastProcessed`. This requires the **accesslog overlay** (`slapo-accesslog`) to be enabled.

The `bitnamilegacy/openldap` image supports this natively via the `LDAP_ENABLE_ACCESSLOG=yes` environment variable, which is set in the Docker Compose configuration. The accesslog database is visible as `cn=accesslog` in the RootDSE naming contexts.

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
dn: uid=john.smith,ou=People,dc=yellowstone,dc=local
objectClass: inetOrgPerson
objectClass: organizationalPerson
objectClass: person
objectClass: top
uid: john.smith
cn: John Smith
sn: Smith
givenName: John
displayName: John Smith
mail: john.smith@yellowstone.local
title: Software Engineer
departmentNumber: Engineering
userPassword: {SSHA}base64encodedpassword
```

Example group:

```ldif
dn: cn=Engineering,ou=Groups,dc=yellowstone,dc=local
objectClass: groupOfNames
cn: Engineering
description: Engineering department group
member: uid=john.smith,ou=People,dc=yellowstone,dc=local
```

### Bulk Loading

- Use `docker exec openldap-primary ldapadd -x -D "cn=admin,dc=yellowstone,dc=local" -w "Test@123!" -c -f /path/to/users.ldif`
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
                BindDN           = "CN=Administrator,CN=Users,DC=panoply,DC=local"
                BindPassword     = "Test@123!"
                AuthType         = "Simple"
                BaseDN           = "DC=panoply,DC=local"
                UserContainer    = "OU=Users,OU=Corp,DC=panoply,DC=local"
                GroupContainer   = "OU=Groups,OU=Corp,DC=panoply,DC=local"
                UserObjectClass  = "user"
                GroupObjectClass = "group"
                UserRdnAttr      = "CN"
                UserNameAttr     = "sAMAccountName"
                DepartmentAttr   = "department"
                DeleteBehaviour  = "Disable"
                DisableAttribute = "userAccountControl"
                DnTemplate       = 'CN={displayName},OU=Users,OU=Corp,DC=panoply,DC=local'
            }
        }
        "OpenLDAP" {
            return @{
                ContainerName    = "openldap-primary"
                Host             = "openldap-primary"
                Port             = 1389
                UseSSL           = $false
                CertValidation   = $null
                BindDN           = "cn=admin,dc=yellowstone,dc=local"
                BindPassword     = "Test@123!"
                AuthType         = "Simple"
                BaseDN           = "dc=yellowstone,dc=local"
                UserContainer    = "ou=People,dc=yellowstone,dc=local"
                GroupContainer   = "ou=Groups,dc=yellowstone,dc=local"
                UserObjectClass  = "inetOrgPerson"
                GroupObjectClass = "groupOfNames"
                UserRdnAttr      = "uid"
                UserNameAttr     = "uid"
                DepartmentAttr   = "departmentNumber"
                DeleteBehaviour  = "Delete"
                DisableAttribute = $null
                DnTemplate       = 'uid={uid},ou=People,dc=yellowstone,dc=local'
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

### Phase 1: Docker Infrastructure ✅

**Deliverables:**
- `test/integration/docker/openldap/Dockerfile` — based on `bitnamilegacy/openldap` (OpenLDAP 2.6.10)
- `test/integration/docker/openldap/bootstrap/01-base-ous-yellowstone.ldif` — root entry and OUs for primary suffix
- `test/integration/docker/openldap/scripts/01-add-second-suffix.sh` — creates second MDB database (`dc=glitterband,dc=local`) via `cn=config` at startup
- `test/integration/docker/openldap/Build-OpenLdapImage.ps1` — build script with content-hash labelling
- Updated `docker-compose.integration-tests.yml` (replaced `osixia/openldap` with `bitnamilegacy/openldap`, `openldap` profile)
- Two naming contexts verified: `dc=yellowstone,dc=local` and `dc=glitterband,dc=local`
- Accesslog overlay enabled (`LDAP_ENABLE_ACCESSLOG=yes`) for future delta import testing
- Health check passes, both suffixes queryable with admin bind

### Phase 2: Test Data Population ✅

**Deliverables:**
- `test/integration/Populate-OpenLDAP.ps1` — generates `inetOrgPerson` users and `groupOfNames` groups across both suffixes, loads via `ldapadd` piped through stdin
- Users split between suffixes: odd indices to Yellowstone, even to Glitterband — distinct users per partition for Scenario 9 assertions
- `groupOfNames` MUST constraint handled: initial member assigned during group creation
- Additional memberships added via `ldapmodify`
- Verified at Nano (3 users) and Micro (10 users) scales

### Phase 3: Test Framework Parameterisation ✅

**Deliverables:**
- `Get-DirectoryConfig` function in `Test-Helpers.ps1` — returns directory-specific config (container, host, port, bind DN, object classes, etc.) for SambaAD or OpenLDAP
- `-DirectoryType` parameter on `Run-IntegrationTests.ps1` — controls Docker profile, health checks, population, and OU preparation
- Refactored `LDAP-Helpers.ps1` — all functions accept `$DirectoryConfig` hashtable alongside individual params for backward compatibility
- Parameterised `Setup-Scenario1.ps1` — LDAP connected system name, host, port, bind DN, SSL, and auth type all driven by `$DirectoryConfig`
- Parameterised `Invoke-Scenario1.ps1` — container name for docker exec calls driven by `$DirectoryConfig`
- SambaAD remains the default — all existing behaviour preserved when `-DirectoryType` is not specified

### Phase 4: Connector Fixes ✅

**Deliverables:**
- `LdapDirectoryType` enum with `ActiveDirectory`, `SambaAD`, `OpenLDAP`, `Generic` — all directory-specific behaviour centralised in computed properties on `LdapConnectorRootDse`
- RFC 4512 schema discovery via `cn=Subschema` (`Rfc4512SchemaParser.cs`) with 37 unit tests — tokeniser, objectClass/attributeType parsing, SYNTAX OID mapping, writability from USAGE field
- Partition discovery via `namingContexts` rootDSE attribute for non-AD directories
- `entryUUID` and `distinguishedName` synthesised as attributes on all RFC schema object types (operational attributes not in any class's MUST/MAY)
- `distinguishedName` synthesised during import from `entry.DistinguishedName` (OpenLDAP doesn't return it as an attribute)
- Accesslog-based delta import (`GetDeltaResultsUsingAccesslog`) — queries `cn=accesslog` using `reqStart` timestamp watermarks
- OpenLDAP detection via `structuralObjectClass: OpenLDAProotDSE` (fallback when `vendorName` not set)
- DN-aware RDN attribute detection in export (`IsRdnAttribute` parses RDN from DN, not hardcoded `cn`)
- Changelog query gated behind delta import only (not full import)
- "Include Auxiliary Classes" connected system setting (both AD and RFC paths)
- Related issues created: #433 (AD schema batch optimisation), #434 (filter internal object classes from UI)

### Phase 5: End-to-End Validation ✅

**Deliverables:**
- Scenario 1 (HR → OpenLDAP) passing at Nano scale — all 8 test steps
- Accesslog-based delta import confirmed working for export confirmation
- Integration test parameterisation: all `samba-tool`/`ldbsearch` verifications replaced with `Get-LDAPUser`/`Test-LDAPUserExists`; all hardcoded container names, partitions, attributes, and mappings driven by `$DirectoryConfig`
- AD-specific tests (Disable/Enable via `userAccountControl`) gracefully skipped for OpenLDAP
- Mover Rename verifies `cn` update (not DN change) for OpenLDAP (uid-based RDN)
- Mover Move verifies `departmentNumber` update (not OU move) for OpenLDAP (flat OU structure)

**Scenario 1 test results (Nano scale):**

| Step | Status | Notes |
|------|--------|-------|
| Joiner | ✅ Pass | Full lifecycle incl. accesslog delta import confirmation |
| Mover (Attribute Change) | ✅ Pass | Title update exported and confirmed |
| Mover Rename | ✅ Pass | cn/displayName updated (DN unchanged) |
| Mover Move | ✅ Pass | departmentNumber updated (no OU move) |
| Disable | ⏭ Skipped | No userAccountControl on OpenLDAP |
| Enable | ⏭ Skipped | No userAccountControl on OpenLDAP |
| Leaver | ✅ Pass | Grace period deprovisioning |
| Reconnection | ✅ Pass | Delete → restore within grace period |

### Phase 6: Remaining Scenarios (In Progress)

**Goal:** Parameterise all remaining integration test scenarios for OpenLDAP.

**Recommended order** (value vs effort):

| Priority | Scenario | AD-specific refs | Effort | Status | Notes |
|----------|----------|-----------------|--------|--------|-------|
| 1 | **S9: Partition-Scoped Imports** | 5 | Low | ✅ Done | True multi-partition filtering with Yellowstone + Glitterband suffixes |
| 2 | **S7: Clear Connected System Objects** | 0 | Low | ✅ Done | DirectoryConfig threading only — scenario is entirely CSV-based |
| 3 | **S6: Scheduler Service** | 2 | Low | ✅ Done | DirectoryConfig, system name parameterised, docker cp replaced with bind mount |
| 4 | **S2: Cross-Domain Sync** | 11 | Medium | ✅ Done | Two LDAP connected systems (Yellowstone→Glitterband), all 4 tests passing. Unblocked by #435. |
| 5 | **S5: Matching Rules** | 17 | Medium | ✅ Done | DirectoryConfig threading, docker cp removed, user cleanup parameterised |
| 6 | **S3: GAL Sync** | 0 | N/A | ⏭ Deferred | Not yet implemented — placeholder script only. Out of scope for this phase. |
| 7 | **S4: Deletion Rules** | 26 | High | ✅ Done | All 7 tests passing — LDAP-Helpers replace samba-tool, .ContainsKey() for missing attrs |
| 8 | **S8: Cross-Domain Entitlement Sync** | 50 | High | ✅ Done | All 6 tests passing at MediumLarge. Accesslog mapsize/sizelimit configured. Delta import fallback prevented (null watermark fix). Connector warnings moved to Activity.WarningMessage. |

**Implementation advice for each scenario:**

**S9 (Partition-Scoped Imports):** ✅ Complete. Both `Setup-Scenario9.ps1` and `Invoke-Scenario9-PartitionScopedImports.ps1` parameterised with `$DirectoryConfig`. For OpenLDAP: selects both partitions (Yellowstone + Glitterband), creates four run profiles (scoped primary, scoped second, unscoped, full sync), and asserts true partition isolation — scoped imports to each partition return only that partition's users, and combined counts match total. `Run-IntegrationTests.ps1` updated with Step 4c to call `Populate-OpenLDAP.ps1` before scenarios when using OpenLDAP.

**S2 (Cross-Domain Sync):** Requires two LDAP connected systems pointing at the same OpenLDAP instance but different suffixes. The `Get-DirectoryConfig` function already has `SecondSuffix` and `SecondBindDN` for this purpose. Setup script needs a second connected system creation block.

**S5 (Matching Rules):** Primarily attribute name substitution (`sAMAccountName`→`uid`, `employeeID`→`employeeNumber`, etc.) and object type substitution (`user`→`inetOrgPerson`). Follow the same parameterisation pattern used in S1's `Setup-Scenario1.ps1`.

**S8 (Cross-Domain Entitlement Sync):** ✅ Complete. All 6 tests passing at MediumLarge scale (InitialSync, ForwardSync, DetectDrift, ReassertState, NewGroup, DeleteGroup). Key fixes applied: (1) OpenLDAP accesslog database configured with 1GB mapsize and unlimited sizelimit to prevent `MDB_MAP_FULL` at scale. (2) Delta import null watermark prevention — when accesslog is empty (e.g., after snapshot restore), full import generates a fallback timestamp so the next delta import doesn't fall back unnecessarily. (3) Connector-level warnings (e.g., `DeltaImportFallbackToFullImport`) moved from phantom RPEIs to `Activity.WarningMessage` — eliminates misleading RPEI rows with no CSO association. (4) `SplitOnCapitalLetters` fixed for camelCase LDAP object types (`groupOfNames` → `Group Of Names`). (5) External Object Type displayed as-is on Activity detail page (no word splitting).

**S4 (Deletion Rules):** OpenLDAP has no account disable mechanism. The deletion rule tests that use `Disable` behaviour and verify `userAccountControl` will need to be skipped or adapted. The `Delete` behaviour (actual LDAP delete) should work unchanged.

**Scale testing:** After all scenarios pass at Nano, scale up through Micro→Small→Medium. The connector code is scale-independent so this should be straightforward — any failures will be performance/timeout related, not logic bugs.

## Risks and Mitigations

| Risk | Impact | Likelihood | Status | Mitigation |
|------|--------|------------|--------|------------|
| Schema discovery rewrite is complex | High | High | ✅ Resolved | RFC 4512 parser implemented with 37 unit tests. Handles all standard object classes and attribute types. |
| OpenLDAP changelog overlay hard to configure | Medium | Medium | ✅ Resolved | Implemented accesslog-based delta import using `cn=accesslog` with `reqStart` timestamps. Works with Bitnami's `LDAP_ENABLE_ACCESSLOG=yes`. |
| `bitnami/openldap` doesn't support `slapadd` for bulk loading | Medium | Medium | ✅ Resolved | Using `ldapadd` via stdin piping. Works at Nano/Micro/Small scales. Pre-built images needed for XLarge. |
| `groupOfNames` empty group constraint breaks export | Medium | High | ✅ Resolved | Connector handles placeholder member transparently (configurable DN, default `cn=placeholder`). 21 unit tests. Refint error handling for directories with referential integrity overlay. |
| Paged results cookie invalid on multi-type imports | Medium | High | ✅ Resolved | OpenLDAP's RFC 2696 cursor is connection-scoped — unrelated searches between paged calls invalidate it. Fix: skip completed container+objectType combos on subsequent pages. |
| Performance regression at XLarge if OpenLDAP population is slow | Low | Medium | ✅ Resolved | Pre-populated snapshot images implemented (`Build-OpenLDAPSnapshots.ps1`) with content-hash staleness detection, matching Samba AD pattern |
| Samba AD regression from connector changes | Medium | Low | ✅ Verified | Full regression (8/8 scenarios, Small template) passed on Samba AD. All connector changes gated behind `LdapDirectoryType` checks. |

## Success Criteria

- [x] OpenLDAP container starts and is healthy in integration test environment
- [x] `Populate-OpenLDAP.ps1` successfully creates users and groups at Small template scale
- [x] `Run-IntegrationTests.ps1 -DirectoryType OpenLDAP` parameter works and selects correct infrastructure
- [x] JIM LDAP connector can connect to OpenLDAP, discover schema, and refresh partitions
- [x] Scenario 1 Joiner step passes against OpenLDAP (CSV → JIM → OpenLDAP provisioning)
- [x] Scenario 1 Mover and Leaver steps pass against OpenLDAP
- [x] Delta import works against OpenLDAP (accesslog-based with reqStart timestamps)
- [x] All existing Samba AD tests continue to pass unchanged (8/8 scenarios, Small template — 2026-04-01)
- [x] All scenarios (S1-S9, excluding S3 deferred) parameterised for OpenLDAP
- [x] All OpenLDAP scenarios pass (8/8 scenarios, Small template — 2026-04-01)
- [x] Scale testing through Micro → Small → Medium (full regression at Medium on both SambaAD and OpenLDAP — 2026-04-01)
