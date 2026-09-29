# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Starts a test-only Keycloak, with JIM's development realm, for the deployment-boot check.

.DESCRIPTION
    JIM does not start without an OIDC identity provider to read its discovery document from, so each
    deployment the check boots needs one. This starts Keycloak in Docker on the host's own network and prints
    the realm's authority URL, at the host's own address: every deployment reaches it there, whether it runs
    on Docker, rootful Podman or rootless Podman, so one identity provider serves them all.

    The image is the one docker-compose.override.yml names for development, and the realm is the development
    realm, so the check needs nothing of its own to keep up to date. Neither is part of a JIM installation.

.PARAMETER Port
    The port Keycloak listens on. Defaults to 8180, as in development.

.PARAMETER Address
    The address deployments reach Keycloak at. Defaults to the host's primary IPv4 address.

.OUTPUTS
    The authority URL to install JIM with, for example http://10.1.0.4:8180/realms/jim.

.EXAMPLE
    $authority = ./test/ci/deployment/Start-TestIdentityProvider.ps1
#>
[CmdletBinding()]
param(
    [int]$Port = 8180,

    [string]$Address
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..' '..' '..')).Path
$containerName = 'jim-ci-keycloak'

$override = Get-Content (Join-Path $repoRoot 'docker-compose.override.yml') -Raw
if ($override -notmatch 'image:\s+(quay\.io/keycloak/keycloak:\S+)') {
    throw 'Could not find the Keycloak image in docker-compose.override.yml'
}
$image = $Matches[1]
$realm = Join-Path $repoRoot '.devcontainer/keycloak/jim-realm.json'

if (-not $Address) {
    # The source address of the host's default route: the address other machines, and containers, reach it at.
    $route = (ip -4 route get 1.1.1.1) -join ' '
    if ($route -notmatch '\bsrc\s+(\d+\.\d+\.\d+\.\d+)') {
        throw "Could not find the host's address from: $route"
    }
    $Address = $Matches[1]
}

docker rm -f $containerName *> $null
Write-Host "Starting $image on port $Port..."
docker run -d --name $containerName --network host `
    -v "${realm}:/opt/keycloak/data/import/jim-realm.json:ro" `
    $image start-dev --import-realm --http-port=$Port | Out-Null
if ($LASTEXITCODE -ne 0) {
    throw "Failed to start $image"
}

$authority = "http://${Address}:${Port}/realms/jim"
$deadline = (Get-Date).AddMinutes(5)
while ($true) {
    $code = curl -s -o /dev/null -w '%{http_code}' --max-time 5 --noproxy $Address "$authority/.well-known/openid-configuration"
    if ($code -eq '200') {
        break
    }
    if ((Get-Date) -gt $deadline) {
        docker logs --tail 50 $containerName
        throw "Keycloak did not serve $authority within 5 minutes"
    }
    Start-Sleep -Seconds 3
}
Write-Host "Keycloak is serving $authority"
$authority
