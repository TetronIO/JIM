# Release Process Hardening

- **Status:** Planned
- **Priority:** High
- **Effort:** Large (9 phases, most individually small)

## Overview

Harden JIM's release pipeline and deployment process to match industry best practices for top-tier containerised services. This plan addresses gaps identified by reviewing the current release workflow against projects like GitLab, Grafana, Traefik, Keycloak, and Bitwarden, and against requirements from CISA Secure by Design, the UK Software Security Code of Practice, and SLSA.

The current release process is solid — cosign signing, air-gapped bundles, digest-pinned base images, and SHA256 checksums are ahead of many projects. This plan closes the remaining gaps without over-engineering what already works.

## Business Value

- **Supply chain trust**: SBOM and provenance attestations are increasingly hard requirements for government, defence, and critical infrastructure buyers
- **Reliability**: Version validation and smoke tests catch silent failures that currently could ship broken releases
- **Operational confidence**: Rollback documentation reduces deployment risk for air-gapped customers with no internet access to troubleshoot
- **Cost reduction**: Docker layer caching and multi-arch builds save CI minutes and broaden deployment reach

## Current State

### What's Working Well

- Cosign image signing on every release
- Air-gapped deployment bundle with SHA256 checksums
- Digest-pinned Docker base images with version-pinned apt packages
- Automated release workflow: validate -> build -> publish -> bundle -> release
- Idempotent PSGallery publish (checks for existing version)
- `/release` skill with changelog validation and pre-flight checks

### Identified Gaps

| # | Gap | Risk | Effort |
|---|-----|------|--------|
| 1 | No version validation gate — tag and VERSION file can diverge | High | Trivial |
| 2 | `latest` tag applied unconditionally, including prereleases | Medium | Trivial |
| 3 | No container vulnerability scanning before push | High | Small |
| 4 | No smoke test after image build | Medium | Small |
| 5 | PostgreSQL image unpinned in bundle script (`postgres:18`) | Medium | Trivial |
| 6 | No SBOM or SLSA provenance attestation | High (for target market) | Small |
| 7 | No multi-architecture image builds (deferred — amd64-only) | Low | Medium |
| 8 | No Docker layer caching in CI | Low (cost only) | Small |
| 9 | No rollback documentation | Medium (operational) | Small |
| 10 | Changelog extraction fails silently | Low | Trivial |
| 11 | Air-gapped deployment docs include stale manual migration step | Low | Trivial |

## Implementation Phases

Phases are ordered by risk reduction per unit of effort. Each phase is independently shippable.

---

### Phase 1: Version Validation Gate

**Problem**: If someone pushes tag `v0.4.0` but the VERSION file still says `0.3.0`, Docker images get tagged `0.4.0` (from the git tag) but the .NET assemblies report `0.3.0` (from `Directory.Build.props`). There is no CI gate to catch this.

**Solution**: Add a step to the `validate` job that compares the tag against the VERSION file.

**File**: `.github/workflows/release.yml`

Add after the checkout step in the `validate` job:

```yaml
- name: Validate version consistency
  run: |
    TAG_VERSION="${GITHUB_REF#refs/tags/v}"
    FILE_VERSION=$(cat VERSION | tr -d '[:space:]')
    if [ "$TAG_VERSION" != "$FILE_VERSION" ]; then
      echo "::error::Version mismatch: tag is v${TAG_VERSION} but VERSION file contains ${FILE_VERSION}"
      exit 1
    fi
    echo "Version validated: ${TAG_VERSION}"
```

Also validate the PowerShell manifest `ModuleVersion` matches (numeric part):

```yaml
- name: Validate PowerShell manifest version
  shell: pwsh
  run: |
    $tagVersion = '${{ github.ref_name }}'.TrimStart('v')
    $numericVersion = ($tagVersion -split '-')[0]  # Strip prerelease suffix
    $manifest = Import-PowerShellDataFile './src/JIM.PowerShell/JIM.psd1'
    if ($manifest.ModuleVersion -ne $numericVersion) {
      Write-Error "PowerShell manifest version ($($manifest.ModuleVersion)) does not match tag ($numericVersion)"
      exit 1
    }
```

**Effort**: ~15 minutes. Zero risk.

---

### Phase 2: Conditional `latest` Tag

**Problem**: Every tag push — including prereleases like `v0.4.0-alpha` — applies the `latest` Docker tag. Prerelease images should never be `latest`.

**Solution**: Change the `docker/metadata-action` tags configuration to only apply `latest` on stable (non-prerelease) tags.

**File**: `.github/workflows/release.yml`

Replace the current tags block in the `build-containers` job:

```yaml
tags: |
  type=semver,pattern={{version}}
  type=semver,pattern={{major}}.{{minor}}
  type=raw,value=latest,enable=${{ !contains(github.ref, '-') }}
```

The `enable` condition checks whether the tag contains a hyphen (SemVer prerelease indicator). Tags like `v0.4.0-alpha` will get `0.4.0-alpha` and `0.4` tags but not `latest`.

**Effort**: ~5 minutes. Zero risk.

---

### Phase 3: Container Vulnerability Scanning

**Problem**: Images are pushed without checking for known CVEs. A base image update might introduce vulnerabilities that slip through undetected.

**Solution**: Add a Trivy scan step after each image build, before push. Fail the build on CRITICAL/HIGH CVEs.

**File**: `.github/workflows/release.yml`

Add after the build step in `build-containers`, before the push:

```yaml
- name: Scan image for vulnerabilities
  uses: aquasecurity/trivy-action@0.28.0
  with:
    image-ref: ${{ env.IMAGE_PREFIX }}/${{ matrix.image }}:${{ steps.version.outputs.VERSION }}
    format: 'table'
    exit-code: '1'
    severity: 'CRITICAL,HIGH'
    ignore-unfixed: true
```

**Note**: This requires changing the build-push step to build locally first (`load: true`), scan, then push in a separate step. Alternatively, scan after push and fail the downstream jobs if vulnerabilities are found — less ideal but simpler.

**Considerations**:
- `ignore-unfixed: true` avoids failing on CVEs with no available fix (common in Debian base images)
- Pin the Trivy action version (not `@master`)
- May need a `.trivyignore` file for accepted risks that have no upstream fix
- Trivy is MIT-licensed, widely adopted, and maintained by Aqua Security

**Effort**: ~30 minutes including testing.

---

### Phase 4: Smoke Test After Build

**Problem**: The `validate` job tests .NET code, but never tests whether the containerised application actually starts and responds to health checks. A Dockerfile misconfiguration or missing runtime dependency could produce images that pass unit tests but fail to boot.

**Solution**: Add a smoke test step that starts each container and hits its health endpoint.

**File**: `.github/workflows/release.yml`

Add a step after image build in `build-containers` (or as a separate job):

```yaml
- name: Smoke test container
  run: |
    IMAGE="${{ env.IMAGE_PREFIX }}/${{ matrix.image }}:${{ steps.version.outputs.VERSION }}"

    # Start container with minimal config (just needs to boot, not fully function)
    docker run -d --name smoke-test \
      -e ASPNETCORE_ENVIRONMENT=Production \
      -e ConnectionStrings__DefaultConnection="Host=localhost;Database=test" \
      -p 8080:80 \
      "$IMAGE"

    # Wait for container to start (up to 30 seconds)
    for i in $(seq 1 30); do
      if curl -sf http://localhost:8080/health 2>/dev/null; then
        echo "Health check passed"
        docker rm -f smoke-test
        exit 0
      fi
      sleep 1
    done

    echo "::error::Container failed to pass health check within 30 seconds"
    docker logs smoke-test
    docker rm -f smoke-test
    exit 1
```

**Prerequisite**: JIM must expose a `/health` endpoint that returns 200 even without a database connection (or returns a degraded status). If the health endpoint requires a live database, this test needs a PostgreSQL service container — which adds complexity. Consider a `/health/live` (liveness) endpoint that just confirms the process is up, separate from `/health/ready` (readiness) that checks dependencies.

**Effort**: ~1 hour including health endpoint review.

---

### Phase 5: Pin PostgreSQL Digest in Bundle Script

**Problem**: `Build-ReleaseBundle.ps1` does `docker pull postgres:18` — an unpinned floating tag. Two bundle builds on different days could ship different PostgreSQL binaries. This contradicts the project's own dependency pinning policy.

**Solution**: Pin the PostgreSQL image to a specific digest in the bundle script, matching the approach used for JIM's own base images.

**File**: `scripts/Build-ReleaseBundle.ps1`

Replace:
```powershell
docker pull postgres:18
```

With a digest-pinned reference. Maintain the digest as a variable at the top of the script for easy updates:

```powershell
# Pin PostgreSQL image digest for reproducible air-gapped bundles
# Update this digest when upgrading PostgreSQL versions
$PostgresImage = "postgres:18@sha256:<current-digest>"
```

Also update the `docker save` command to use `$PostgresImage`.

**Maintenance**: Update the digest when Dependabot proposes PostgreSQL updates, or add a comment in the Dependabot config to include this file.

**Effort**: ~15 minutes.

---

### Phase 6: SBOM and SLSA Provenance Attestation

**Problem**: Images are signed with cosign but lack SBOM (Software Bill of Materials) and SLSA provenance attestations. For government and defence customers, these are increasingly mandatory. CISA's Secure by Design guidance and the UK Software Security Code of Practice both require supply chain transparency.

**Solution**: Add SBOM generation and SLSA provenance attestation to the release workflow.

**File**: `.github/workflows/release.yml`

#### 6a: SBOM Generation

Use Syft (by Anchore, Apache 2.0 licence) to generate an SPDX SBOM for each image, then attach it via cosign:

```yaml
- name: Generate SBOM
  uses: anchore/sbom-action@v0
  with:
    image: ${{ env.IMAGE_PREFIX }}/${{ matrix.image }}@${{ steps.build.outputs.digest }}
    format: spdx-json
    output-file: sbom.spdx.json

- name: Attach SBOM to image
  run: |
    IMAGE_REF=$(echo "${{ env.IMAGE_PREFIX }}/${{ matrix.image }}" | tr '[:upper:]' '[:lower:]')
    cosign attest --yes --type spdxjson \
      --predicate sbom.spdx.json \
      "${IMAGE_REF}@${{ steps.build.outputs.digest }}"
```

#### 6b: SLSA Provenance

Use GitHub's built-in attestation action for SLSA L1 provenance:

```yaml
- name: Attest build provenance
  uses: actions/attest-build-provenance@v2
  with:
    subject-name: ${{ env.IMAGE_PREFIX }}/${{ matrix.image }}
    subject-digest: ${{ steps.build.outputs.digest }}
    push-to-registry: true
```

This requires `id-token: write` and `attestations: write` permissions (already have `id-token`; add `attestations`).

#### 6c: Attach SBOM to GitHub Release

Upload the SBOM files as release assets alongside the bundle and checksums.

**Considerations**:
- Syft is Apache 2.0, widely adopted, maintained by Anchore
- `actions/attest-build-provenance` is GitHub-maintained, first-party
- Air-gapped customers can verify attestations offline using `cosign verify-attestation`
- Add SBOM verification instructions to `docs/RELEASE_PROCESS.md`

**Effort**: ~1 hour.

---

### Phase 7: Multi-Architecture Image Builds (Deferred)

**Status**: Deferred — amd64-only for now. Revisit when customer demand arises or native ARM runners become available. See [#375](https://github.com/TetronIO/JIM/issues/375).

**Problem**: Images are built only for `linux/amd64`. Air-gapped environments may run on ARM64 hardware (AWS Graviton, Apple Silicon for development).

**Decision**: Stay with single-arch (amd64) builds. JIM's target environments (government, defence, healthcare, financial services) are overwhelmingly x86-64. Adding arm64 has significant trade-offs with no current demand:

| Factor | amd64 only (current) | Multi-arch (amd64 + arm64) |
|--------|---------------------|---------------------------|
| CI build time | ~5 min per image | ~15-20 min (QEMU emulation ~3-4x slower) |
| Air-gapped bundle size | ~500MB-1GB | ~1-2GB (roughly doubles) |
| Pipeline complexity | Simple | Buildx + QEMU setup, or native ARM runners |
| Deployment coverage | Vast majority of enterprise servers | Adds Graviton, Ampere, Apple Silicon |
| apt package verification | Known working | `libldap`, `cifs-utils` need arm64 verification |

**When to revisit**: Customer/prospect explicitly requires ARM64 deployment, or GitHub native ARM runners become available on our plan (eliminating the QEMU penalty).

**Implementation approach when needed**: Use Docker Buildx with QEMU emulation (or native ARM runners). For the air-gapped bundle, produce separate platform-specific bundles (`jim-release-X.Y.Z-linux-amd64.tar.gz`, `jim-release-X.Y.Z-linux-arm64.tar.gz`) rather than a single multi-arch tarball — keeps each bundle small and avoids forcing air-gapped deployers to transfer architecture-irrelevant images.

**Effort**: ~2 hours including bundle script updates and testing.

---

### Phase 8: Docker Layer Caching in CI

**Problem**: Every release does a full Docker build from scratch. This wastes CI minutes and slows the release pipeline.

**Solution**: Use GitHub Actions cache for Docker layers.

**File**: `.github/workflows/release.yml`

Update the build-push step in `build-containers`:

```yaml
- name: Build and push Docker image
  uses: docker/build-push-action@v6
  with:
    context: .
    file: ${{ matrix.dockerfile }}
    push: true
    tags: ${{ steps.meta.outputs.tags }}
    labels: ${{ steps.meta.outputs.labels }}
    build-args: |
      VERSION=${{ steps.version.outputs.VERSION }}
    cache-from: type=gha
    cache-to: type=gha,mode=max
```

**Considerations**:
- `type=gha` uses GitHub Actions' built-in cache (10 GB limit per repository)
- `mode=max` caches all layers, not just the final stage — important for multi-stage Dockerfiles
- Can also be applied to the CI build workflow (`dotnet-build-and-test.yml`) for NuGet restore caching
- If cache size becomes an issue, switch to `type=registry` using GHCR as the cache backend

**Effort**: ~15 minutes.

---

### Phase 9: Rollback Documentation, Changelog Validation, and Stale Docs Cleanup

**Problem**: If a release goes wrong in production, there is no documented rollback procedure. Separately, if the changelog extraction regex fails, the release ships with a generic "Release X.Y.Z" note and nobody is alerted.

#### 9a: Rollback Runbook

**File**: `docs/RELEASE_PROCESS.md` (new section)

Add a "Rolling Back a Release" section covering:

1. **Docker rollback**: Point `JIM_VERSION` at the previous version in `.env` and `docker compose up -d` — images are immutable, so this is safe
2. **Database rollback**: If migrations were applied, run the reverse migration:
   ```bash
   docker compose exec jim.web dotnet ef database update <PreviousMigrationName>
   ```
   List how to find the previous migration name. Note: this only works if the migration has a `Down()` method — verify before release.
3. **PowerShell module**: `Install-Module JIM -RequiredVersion <previous>` or restore from the previous air-gapped bundle
4. **GitHub Release**: Can be deleted/re-drafted. Docker images on GHCR can be deleted via the packages UI.
5. **Prevention**: Recommend deploying to a staging environment first and running integration tests before promoting to production.

#### 9b: Changelog Extraction Validation

**File**: `.github/workflows/release.yml`

Strengthen the changelog extraction step to fail loudly if no section is found:

```yaml
- name: Extract release notes from CHANGELOG
  id: changelog
  shell: pwsh
  run: |
    $version = '${{ steps.version.outputs.VERSION }}'
    $changelog = Get-Content ./CHANGELOG.md -Raw
    $pattern = "(?s)## \[$([regex]::Escape($version))\][^\n]*\n(.*?)(?=\n## \[|\z)"
    if ($changelog -match $pattern) {
      $notes = $Matches[1].Trim()
    } else {
      Write-Error "No changelog section found for version $version. Ensure CHANGELOG.md has a ## [$version] section."
      exit 1
    }
    if ($notes.Length -lt 20) {
      Write-Warning "Changelog section for $version is suspiciously short ($($notes.Length) chars). Verify content."
    }
    $notes | Set-Content ./release-notes.md
```

#### 9c: Remove Stale Manual Migration Step from Docs

**Problem**: `docs/RELEASE_PROCESS.md` Step 9 tells air-gapped users to manually run `docker compose exec jim.web dotnet ef database update`. This is unnecessary — JIM already auto-migrates on startup via `PostgresDataRepository.MigrateDatabaseAsync()`, called from `Worker.InitialiseDatabaseAsync()`. The worker checks for pending migrations and applies them before accepting work. JIM.Web waits for readiness via `IsApplicationReadyAsync()`.

**Solution**: Remove Step 9 from the air-gapped deployment instructions. Replace with a note explaining that migrations are applied automatically on first startup. Keep the manual command as a troubleshooting fallback only.

**File**: `docs/RELEASE_PROCESS.md`

**Effort**: ~2 hours for rollback docs, ~15 minutes for changelog validation, ~15 minutes for stale docs cleanup.

---

## Summary of Affected Files

| File | Phases |
|------|--------|
| `.github/workflows/release.yml` | 1, 2, 3, 4, 6, 7, 8, 9b |
| `scripts/Build-ReleaseBundle.ps1` | 5, 7 |
| `docs/RELEASE_PROCESS.md` | 9a, 9c |

## Dependencies

| Dependency | Licence | Phase | Notes |
|------------|---------|-------|-------|
| `aquasecurity/trivy-action` | Apache 2.0 | 3 | GitHub Action for container scanning |
| `anchore/sbom-action` | Apache 2.0 | 6 | GitHub Action for SBOM generation (Syft) |
| `actions/attest-build-provenance` | MIT (GitHub) | 6 | First-party GitHub attestation |
| `docker/setup-qemu-action` | Apache 2.0 | 7 | QEMU for multi-arch builds |
| `docker/setup-buildx-action` | Apache 2.0 | 7 | Docker Buildx setup |

All dependencies are well-maintained, widely adopted, and licence-compatible. No new NuGet packages required.

## Risks & Mitigations

| Risk | Mitigation |
|------|------------|
| Trivy false positives block releases | Use `ignore-unfixed: true` and maintain a `.trivyignore` for accepted risks |
| Multi-arch builds slow down CI | Start with amd64-only in cache; add arm64 when native runners are available |
| QEMU arm64 emulation produces subtly different binaries | Run integration tests on native arm64 hardware before claiming arm64 support |
| GitHub Actions cache eviction invalidates Docker cache | Acceptable — builds still succeed, just slower. Registry cache is an alternative |

## Success Criteria

- [ ] Tag push with VERSION mismatch fails the workflow immediately
- [ ] Prerelease tags do not receive the `latest` Docker tag
- [ ] No CRITICAL/HIGH CVEs in published images (or explicitly accepted)
- [ ] All published images pass health check smoke test
- [ ] PostgreSQL image in air-gapped bundle is digest-pinned
- [ ] SBOM and SLSA provenance attestations are attached to every image
- [ ] `cosign verify-attestation` succeeds for published images
- [ ] ~~Multi-arch images available for amd64 and arm64~~ (deferred — amd64-only for now)
- [ ] Release builds use cached Docker layers
- [ ] `docs/RELEASE_PROCESS.md` includes rollback procedure
- [ ] Stale manual migration step removed from air-gapped deployment docs
- [ ] Changelog extraction failure stops the release workflow
