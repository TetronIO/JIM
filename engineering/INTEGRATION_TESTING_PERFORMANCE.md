# Integration Testing Performance Optimisation

## Overview

Integration tests generate realistic AD environments with users, groups, and memberships. Performance optimisations are critical for large-scale templates (Medium through XXLarge) to keep test execution times reasonable.

## Template Scales

| Template    | Users      | Groups  | Est. Memberships | Target Time |
|-------------|------------|---------|------------------|-------------|
| Nano        | 3          | 6       | ~20              | <10s        |
| Micro       | 10         | 12      | ~100             | <30s        |
| Small       | 100        | 31      | ~1,000           | <2min       |
| Medium      | 1,000      | 118     | ~23,000          | <5min       |
| MediumLarge | 5,000      | 273     | ~150,000         | <15min      |
| Large       | 10,000     | 530     | ~500,000         | <30min      |
| XLarge      | 100,000    | 2,040   | ~5,000,000       | <2hr        |
| XXLarge     | 1,000,000  | 10,055  | ~50,000,000      | <8hr        |

## Current Performance Issues

### Issue 1: Individual Membership Additions (CRITICAL)

**Problem**: Group memberships are added one-at-a-time with individual `samba-tool group addmembers` calls.

**Current code** (`Populate-SambaAD-Scenario8.ps1:409-411`):
```powershell
foreach ($user in $uniqueCandidates) {
    $result = docker exec $container samba-tool group addmembers `
        $group.SAMAccountName `
        $user.SamAccountName 2>&1
    # ... check result
}
```

**Impact**:
- Medium template: 23,300 individual Docker exec calls = 26 minutes
- Large template: ~500,000 calls = **estimated 9 hours**
- XXLarge template: ~50 million calls = **estimated 38 days**

**Root cause**: `samba-tool group addmembers` accepts **multiple members in a single call**, but the script adds them one-by-one.

**Solution**: Batch membership additions per group.

### Issue 2: Docker Exec Overhead

**Problem**: Each `docker exec` call has overhead:
- Process spawn in container
- samba-tool initialization
- LDAP connection establishment
- Authentication

**Impact**: For 23,300 memberships:
- Docker exec overhead: ~1-2 seconds per call = 6-13 hours of pure overhead
- Actual LDAP operation: milliseconds

**Solution**: Batch operations to minimize Docker exec calls.

### Issue 3: No Resource Limits on Samba Containers

**Problem**: Samba AD containers have no CPU/memory limits in `docker-compose.integration-tests.yml`.

**Impact**:
- Containers compete for resources with JIM stack
- No guaranteed performance baseline
- Inconsistent test execution times

**Solution**: Add appropriate resource limits based on template size.

### Issue 4: LDAP Modify Operations on Large Groups

**Problem**: Adding a member to a group with 1,000+ existing members requires:
- Reading the entire member list
- Adding the new member
- Writing the entire member list back

**Impact**: Performance degrades as groups grow larger (O(n²) complexity).

**Solution**: Pre-calculate memberships and add in batches at group creation time.

## Optimisation Strategies

### Strategy 1: Batch Membership Additions (IMMEDIATE - HIGH IMPACT)

Modify `Populate-SambaAD-Scenario8.ps1` to batch member additions:

```powershell
# Current: One member at a time
foreach ($user in $uniqueCandidates) {
    docker exec $container samba-tool group addmembers $group $user
}

# Optimised: All members at once
$memberList = $uniqueCandidates.SamAccountName -join ' '
docker exec $container samba-tool group addmembers $group $memberList
```

**Expected improvement**:
- Medium: 26 minutes → **2-3 minutes** (8-10x faster)
- Large: 9 hours → **30 minutes** (18x faster)
- XXLarge: 38 days → **8 hours** (114x faster)

**Trade-off**: None. This is a pure optimisation with no downsides.

### Strategy 2: LDIF Bulk Import (ADVANCED - HIGHEST IMPACT)

Instead of using samba-tool, generate LDIF files and import with `ldbmodify`:

```powershell
# Generate LDIF file with all memberships
$ldif = @"
dn: CN=GroupName,OU=Groups,DC=domain,DC=local
changetype: modify
add: member
member: CN=User1,OU=Users,DC=domain,DC=local
member: CN=User2,OU=Users,DC=domain,DC=local
member: CN=User3,OU=Users,DC=domain,DC=local
"@

# Import in one operation
$ldif | docker exec -i $container ldbmodify -H /usr/local/samba/private/sam.ldb
```

**Expected improvement**:
- Medium: 26 minutes → **30-60 seconds** (26-52x faster)
- Large: 9 hours → **5-10 minutes** (54-108x faster)
- XXLarge: 38 days → **2-4 hours** (228-456x faster)

**Trade-off**: More complex code, requires DN construction.

### Strategy 3: Parallel Population (LOW PRIORITY)

For multiple Samba AD instances (Scenario 8 has Source and Target), populate in parallel:

```powershell
$sourceJob = Start-Job -ScriptBlock { & $script -Template $Template -Instance Source }
$targetJob = Start-Job -ScriptBlock { & $script -Template $Template -Instance Target }
$sourceJob, $targetJob | Wait-Job | Out-Null
```

**Expected improvement**: 2x faster for multi-domain scenarios (Source and Target populate simultaneously).

**Trade-off**:
- Higher resource usage during population (but still within container limits)
- `Start-Job` requires permissions that may not be available in all environments (containers, restricted shells)
- **Actual benefit is minimal**: Target population is <1 minute (OU structure only), Source takes 30+ minutes

**Status**: Not implemented due to permission issues in containerized environments. Sequential population is acceptable since Target is very fast.

### Strategy 4: Resource Allocation

Add resource limits to `docker-compose.integration-tests.yml`:

```yaml
samba-ad-source:
  # ... existing config
  deploy:
    resources:
      limits:
        cpus: '2.0'
        memory: 4G
      reservations:
        cpus: '1.0'
        memory: 2G
```

**Rationale**:
- Samba AD requires CPU for LDAP indexing and replication
- More memory = better caching of LDAP database
- Guaranteed resources = consistent performance

**Recommended allocations**:
- Nano/Micro/Small: 1 CPU, 1GB RAM
- Medium: 2 CPUs, 2GB RAM
- Large: 4 CPUs, 4GB RAM
- XLarge/XXLarge: 8 CPUs, 8GB RAM (requires host with sufficient resources)

### Strategy 5: Pre-Populated Container Images (FUTURE)

Build Samba AD container images with test data already populated:

- `ghcr.io/tetronio/jim-samba-ad:scenario8-source-medium`
- `ghcr.io/tetronio/jim-samba-ad:scenario8-source-large`

**Expected improvement**: Population time → **0 seconds** (instant startup).

**Trade-off**:
- Requires image building and storage
- Images would be large (XLarge/XXLarge could be multi-GB)
- Less flexible for ad-hoc testing

## Common Issues and Fixes

### Issue: Console Flooded with User Names

**Symptom**: Terminal fills with hundreds of user/group names during population.

**Cause**: Using space-separated member lists instead of comma-separated lists for `samba-tool group addmembers`. PowerShell treats each space as a command-line argument boundary, causing names to be echoed.

**Fix**: Use commas to join member lists:

```powershell
# WRONG - causes console spam
$memberList = $members -join ' '

# CORRECT - comma-separated as per samba-tool requirements
$memberList = $members -join ','
```

**Status**: Fixed in `Populate-SambaAD.ps1` and `Populate-SambaAD-Scenario8.ps1`.

### Issue: Low Resource Utilization During Population

**Symptom**: `htop` shows low CPU/memory usage even though population is slow.

**Causes**:
1. **Sequential population**: Source and Target AD populated one after another
2. **Resource limits working as designed**: Containers capped at 2 CPUs / 2GB RAM each
3. **I/O bottleneck**: Samba AD may be disk-bound, not CPU-bound

**Fixes**:
1. ❌ Parallel population (Strategy 3) - Not feasible due to PowerShell job permission issues in containers
2. Increase resource limits for large templates:
   ```bash
   SAMBA_SOURCE_CPUS=4.0 SAMBA_SOURCE_MEMORY=4G \
   SAMBA_TARGET_CPUS=4.0 SAMBA_TARGET_MEMORY=4G \
   ./Run-IntegrationTests.ps1 -Scenario Scenario8 -Template Large
   ```
3. Consider SSD storage for Docker volumes if using HDD

## Recommended Implementation Plan

### Phase 1: Immediate Wins - ✅ COMPLETE

1. ✅ Remove hardcoded `$WaitSeconds = 20` delays from test scenarios → default to 0
2. ✅ Implement Strategy 1 (batch membership additions) with correct comma separation
3. ✅ Add resource limits to docker-compose.integration-tests.yml
4. ❌ Parallel population (Strategy 3) - Skipped due to permission issues in containers

**Expected result**: Medium scenario 8 test time: 1h 45m → **15-20 minutes**.

### Phase 2: Major Performance Gains (Week 2)

1. Implement Strategy 2 (LDIF bulk import) for group memberships
2. Add Strategy 4 (resource limits) to `docker-compose.integration-tests.yml`
3. Test with Medium and Large templates

**Expected result**:
- Medium scenario 8: 15-20 minutes → **5 minutes**
- Large scenario 8: Untested currently → **30-45 minutes**

### Phase 3: Advanced Optimisations (Future)

1. Implement Strategy 3 (parallel population) for multi-domain scenarios
2. Evaluate Strategy 5 (pre-populated images) for XLarge/XXLarge templates
3. Add progress reporting and ETA improvements to population scripts

**Expected result**:
- XLarge scenarios become feasible (<2 hours)
- XXLarge scenarios become feasible for overnight CI runs (<8 hours)

## Testing and Validation

After each optimisation:

1. **Functional validation**: Run all scenario tests (Nano, Small, Medium) to ensure correctness
2. **Performance measurement**: Record execution times and compare to baseline
3. **Resource monitoring**: Check CPU/memory usage during population
4. **Scalability testing**: Validate improvements hold for larger templates

## Metrics to Track

| Metric                      | Baseline (Medium) | Target (Medium) | Target (Large) |
|-----------------------------|-------------------|-----------------|----------------|
| AD Population Time          | 31 minutes        | 3 minutes       | 10 minutes     |
| Test Execution Time (S8)    | 1h 45m            | 20 minutes      | 45 minutes     |
| Docker Exec Calls           | ~23,300           | ~118            | ~530           |
| Peak Memory (Samba)         | ~500MB            | ~2GB            | ~4GB           |

## Related Documentation

- [Integration Testing Guide](INTEGRATION_TESTING.md)
- [Scenario 8: Cross-Domain Entitlement Sync](../test/integration/scenarios/Invoke-Scenario8-CrossDomainEntitlementSync.ps1)
- [Samba AD Population Scripts](../test/integration/Populate-SambaAD-Scenario8.ps1)

---

**Document Status**: Draft
**Last Updated**: 2026-02-17
**Author**: Claude Code (Sonnet 4.5)
