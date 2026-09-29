# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Returns the digest-pinned PostgreSQL image reference from docker-compose.yml.

.DESCRIPTION
    docker-compose.yml is the single source of truth for the bundled database's image: Dependabot maintains its
    digest there, and the release bundle and the Podman files read it from there, so every runtime runs the same
    image. The reference is the default of JIM_DB_IMAGE, which an air-gapped installation on Docker's classic
    image store sets to the loaded image's ID; this returns the reference itself, without the variable syntax.

.PARAMETER ComposePath
    The compose file to read. Defaults to the repository's docker-compose.yml.

.EXAMPLE
    ./scripts/Get-PostgresImageReference.ps1
#>
[CmdletBinding()]
param(
    [Parameter()]
    [string]$ComposePath = (Join-Path (Split-Path -Parent $PSScriptRoot) 'docker-compose.yml')
)

$ErrorActionPreference = 'Stop'

$compose = Get-Content $ComposePath -Raw
$match = [regex]::Match($compose, '(?m)^\s*image:\s*(?:\$\{JIM_DB_IMAGE:-)?((?:docker\.io/library/)?postgres:[^\s}]+)\}?\s*$')
if (-not $match.Success) {
    throw "Could not find the PostgreSQL image reference in $ComposePath"
}

$reference = $match.Groups[1].Value
if ($reference -notmatch '@sha256:[0-9a-f]{64}$') {
    throw "The PostgreSQL image in $ComposePath ($reference) is not pinned by digest"
}

$reference
