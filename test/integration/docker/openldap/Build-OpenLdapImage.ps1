<#
.SYNOPSIS
    Build OpenLDAP Docker image for JIM integration testing

.DESCRIPTION
    Builds a Docker image with OpenLDAP configured with two suffixes
    (dc=regionA,dc=test and dc=regionB,dc=test) for testing partition-scoped
    import run profiles (Issue #72, Phase 1b).

    Unlike the Samba AD images, OpenLDAP does not require privileged mode
    or a docker-commit workflow. This is a standard docker build.

    Image built:
    - ghcr.io/tetronio/jim-openldap:primary

.PARAMETER Push
    Push image to GitHub Container Registry after building

.PARAMETER Registry
    Container registry to use (default: ghcr.io/tetronio)

.EXAMPLE
    ./Build-OpenLdapImage.ps1

.EXAMPLE
    ./Build-OpenLdapImage.ps1 -Push
#>

param(
    [Parameter(Mandatory = $false)]
    [switch]$Push,

    [Parameter(Mandatory = $false)]
    [string]$Registry = "ghcr.io/tetronio"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$scriptDir = $PSScriptRoot
$fullTag = "$Registry/jim-openldap:primary"

# Compute a content hash of files that affect the image contents.
# This hash is stored as a Docker image label so the test runner can detect stale images.
$filesToHash = @(
    (Join-Path $scriptDir "Dockerfile"),
    (Join-Path $scriptDir "scripts/01-add-second-suffix.sh"),
    (Join-Path $scriptDir "bootstrap/01-base-ous-regionA.ldif")
)
$combinedContent = ($filesToHash | ForEach-Object { Get-Content -Path $_ -Raw }) -join ""
$buildContentHash = [System.BitConverter]::ToString(
    [System.Security.Cryptography.SHA256]::HashData([System.Text.Encoding]::UTF8.GetBytes($combinedContent))
).Replace("-", "").Substring(0, 16).ToLower()
Write-Host "Build content hash: $buildContentHash" -ForegroundColor DarkGray

Write-Host "=============================================" -ForegroundColor Cyan
Write-Host "Building OpenLDAP Integration Test Image" -ForegroundColor Cyan
Write-Host "=============================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "Image: $fullTag" -ForegroundColor Gray
Write-Host "Suffixes: dc=regionA,dc=test, dc=regionB,dc=test" -ForegroundColor Gray
Write-Host ""

$startTime = Get-Date

docker build `
    --label "jim.openldap.build-hash=$buildContentHash" `
    -t $fullTag `
    $scriptDir

if ($LASTEXITCODE -ne 0) {
    Write-Host "ERROR: Failed to build image" -ForegroundColor Red
    exit 1
}

$elapsed = (Get-Date) - $startTime
Write-Host ""
Write-Host "Image built: $fullTag" -ForegroundColor Green
Write-Host "Build time: $($elapsed.TotalSeconds.ToString('F1'))s" -ForegroundColor Green
Write-Host ""

if ($Push) {
    Write-Host "Pushing $fullTag..." -ForegroundColor Cyan
    docker push $fullTag

    if ($LASTEXITCODE -ne 0) {
        Write-Host "ERROR: Failed to push $fullTag" -ForegroundColor Red
        exit 1
    }
    Write-Host "  Pushed successfully" -ForegroundColor Green
    Write-Host ""
}
else {
    Write-Host "To push image to the registry:" -ForegroundColor Yellow
    Write-Host "  ./Build-OpenLdapImage.ps1 -Push" -ForegroundColor Gray
    Write-Host ""
    Write-Host "Or push manually:" -ForegroundColor Yellow
    Write-Host "  docker push $fullTag" -ForegroundColor Gray
}

Write-Host ""
Write-Host "To test the image:" -ForegroundColor Yellow
Write-Host "  docker run --rm -e LDAP_ROOT=dc=regionA,dc=test -e LDAP_ADMIN_USERNAME=admin -e LDAP_ADMIN_PASSWORD='Test@123!' -p 1389:1389 $fullTag" -ForegroundColor Gray
Write-Host ""
Write-Host "Then verify both suffixes:" -ForegroundColor Yellow
Write-Host "  ldapsearch -x -H ldap://localhost:1389 -b 'dc=regionA,dc=test' -D 'cn=admin,dc=regionA,dc=test' -w 'Test@123!'" -ForegroundColor Gray
Write-Host "  ldapsearch -x -H ldap://localhost:1389 -b 'dc=regionB,dc=test' -D 'cn=admin,dc=regionB,dc=test' -w 'Test@123!'" -ForegroundColor Gray
Write-Host ""
