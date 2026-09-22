# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Build the 389 Directory Server Docker image for JIM integration testing

.DESCRIPTION
    Builds a Docker image of 389 Directory Server configured with two suffixes
    (dc=yellowstone,dc=local and dc=glitterband,dc=local), the same tree, service
    accounts and schema extensions as the OpenLDAP lab image, the Retro Changelog
    plug-in for Delta Import, and the JIM access-control set. Everything is baked
    in at build time by build/configure.sh (see the Dockerfile header).

    Like the OpenLDAP image, and unlike the Samba AD images, this is a standard
    docker build: no privileged mode and no docker-commit workflow.

    Image built:
    - ghcr.io/tetronio/jim-dirsrv:primary

    The image is labelled jim.dirsrv.build-hash with the content hash from
    Get-DirsrvBuildHash.ps1 (every fixture file except the two scripts), which the
    integration test runner recomputes to detect a stale image. The same hash is
    passed to the build as JIM_DIRSRV_BUILD_HASH, which build/configure.sh writes
    into the instance as /data/.jim-provisioned-id: the provenance stamp that
    start-dirsrv.sh compares to decide whether a mounted volume holds this build's
    instance or must be replaced from the provisioned copy.

.PARAMETER Push
    Push the image to GitHub Container Registry after building

.PARAMETER Registry
    Container registry to use (default: ghcr.io/tetronio)

.EXAMPLE
    ./Build-DirsrvImage.ps1

.EXAMPLE
    ./Build-DirsrvImage.ps1 -Push
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
$fullTag = "$Registry/jim-dirsrv:primary"

. (Join-Path $scriptDir "Get-DirsrvBuildHash.ps1")
$buildContentHash = Get-DirsrvBuildHash -FixtureDirectory $scriptDir
Write-Host "Build content hash: $buildContentHash" -ForegroundColor DarkGray

Write-Host "=============================================" -ForegroundColor Cyan
Write-Host "Building 389 Directory Server Integration Test Image" -ForegroundColor Cyan
Write-Host "=============================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "Image: $fullTag" -ForegroundColor Gray
Write-Host "Suffixes: dc=yellowstone,dc=local, dc=glitterband,dc=local" -ForegroundColor Gray
Write-Host ""

$startTime = Get-Date

docker build `
    --label "jim.dirsrv.build-hash=$buildContentHash" `
    --build-arg "JIM_DIRSRV_BUILD_HASH=$buildContentHash" `
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
    Write-Host "  ./Build-DirsrvImage.ps1 -Push" -ForegroundColor Gray
    Write-Host ""
    Write-Host "Or push manually:" -ForegroundColor Yellow
    Write-Host "  docker push $fullTag" -ForegroundColor Gray
}

Write-Host ""
Write-Host "To test the image:" -ForegroundColor Yellow
Write-Host "  docker run --rm --name dirsrv-primary -e DS_DM_PASSWORD='Test@123!' -p 3389:3389 -p 3636:3636 $fullTag" -ForegroundColor Gray
Write-Host ""
Write-Host "Then verify both suffixes as the JIM service accounts:" -ForegroundColor Yellow
Write-Host "  ldapsearch -x -H ldap://localhost:3389 -b 'dc=yellowstone,dc=local' -D 'cn=svc-jim,ou=Services,dc=yellowstone,dc=local' -w 'Svc-Jim@123!'" -ForegroundColor Gray
Write-Host "  ldapsearch -x -H ldap://localhost:3389 -b 'dc=glitterband,dc=local' -D 'cn=svc-jim,ou=Services,dc=glitterband,dc=local' -w 'Svc-Jim@123!'" -ForegroundColor Gray
Write-Host "  ldapsearch -x -H ldap://localhost:3389 -b 'cn=changelog' -s one -D 'cn=svc-jim,ou=Services,dc=yellowstone,dc=local' -w 'Svc-Jim@123!' changenumber changetype targetdn" -ForegroundColor Gray
Write-Host ""
