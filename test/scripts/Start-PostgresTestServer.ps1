# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Stands up the throwaway PostgreSQL that the RequiresPostgres test category runs against.

.DESCRIPTION
    The database-backed test tier guards provider-specific and raw-SQL behaviour the EF Core in-memory provider
    cannot reproduce, so it needs a real PostgreSQL, and it should be the one customers run. This starts a container
    from the image pinned in docker-compose.yml (read by scripts/Get-PostgresImageReference.ps1), which Dependabot
    keeps current; CI's database-tests job and the devcontainer's jim-test-db both run it, so neither can fall behind
    the shipped pin. They had: by #2075 the two were pinned by hand, a release behind it.

    The database is discarded when the tests end, so durability is switched off (fsync, synchronous_commit and
    full_page_writes). On a slow disk PostgreSQL's checkpoints otherwise took 40 to 50 seconds each and the tier ran
    about 4.5 times slower. The cluster is created with LANG=C.UTF-8, as the bundled database is, so the tests see
    the sorting and case rules production runs with. The superuser's password is generated on every run.

    It then prints the environment variables the RequiresPostgres fixtures read, as `export` lines, and appends them
    to $env:GITHUB_ENV when run in GitHub Actions.

    Requires Docker.

.PARAMETER HostPort
    The loopback port to publish PostgreSQL on. Leave it at 0 to have Docker choose a free one, which CI does: a fixed
    port collides on the self-hosted runner host, where a second runner service can be running another job's
    database at the same moment.

.PARAMETER Stop
    Removes the container. In GitHub Actions the end of the server's log is printed first, in a collapsed group, so a
    failed run can be diagnosed from the job log.

.EXAMPLE
    pwsh ./test/scripts/Start-PostgresTestServer.ps1

.EXAMPLE
    pwsh ./test/scripts/Start-PostgresTestServer.ps1 -HostPort 5433

.EXAMPLE
    pwsh ./test/scripts/Start-PostgresTestServer.ps1 -Stop
#>
[CmdletBinding()]
param(
    [ValidateRange(0, 65535)]
    [int]$HostPort = 0,
    [switch]$Stop
)

$ErrorActionPreference = 'Stop'

# One name per workflow run in CI, for the same reason the port is chosen by Docker; the same fixed name locally, so
# a container left behind by an interrupted run is replaced rather than piling up.
$containerName = if ($env:GITHUB_ACTIONS) { "jim-test-db-$($env:GITHUB_RUN_ID)-$($env:GITHUB_RUN_ATTEMPT)" } else { 'jim-test-db' }
$databaseName = 'jim_test'
$username = 'postgres'
$readyTimeoutSeconds = 120

if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
    throw 'docker is required but was not found on PATH.'
}

if ($Stop) {
    if ($env:GITHUB_ACTIONS -and (docker ps -aq --filter "name=^$containerName$")) {
        Write-Host "::group::PostgreSQL server log ($containerName)"
        docker logs --tail 200 $containerName 2>&1 | Write-Host
        Write-Host '::endgroup::'
    }
    docker rm -f $containerName 2>$null | Out-Null
    Write-Host "Throwaway PostgreSQL ($containerName) removed."
    return
}

$image = & (Join-Path $PSScriptRoot '..' '..' 'scripts' 'Get-PostgresImageReference.ps1')

# Pulled up front with a retry, so a transient registry refusal fails here with a clear message rather than inside
# docker run.
for ($attempt = 1; ; $attempt++) {
    docker pull --quiet $image | Out-Null
    if ($LASTEXITCODE -eq 0) { break }
    if ($attempt -eq 3) { throw "Failed to pull $image after $attempt attempts." }
    Start-Sleep -Seconds (15 * $attempt)
}

$password = 'jim' + [System.Convert]::ToHexString([System.Security.Cryptography.RandomNumberGenerator]::GetBytes(16)).ToLowerInvariant()
if ($env:GITHUB_ACTIONS) {
    # Disposable and loopback-only, but still a credential: keep it out of the Actions log.
    Write-Host "::add-mask::$password"
}

docker rm -f $containerName 2>$null | Out-Null
$publish = if ($HostPort -eq 0) { '127.0.0.1::5432' } else { "127.0.0.1:${HostPort}:5432" }
docker run -d --name $containerName -p $publish `
    -e "POSTGRES_USER=$username" `
    -e "POSTGRES_PASSWORD=$password" `
    -e "POSTGRES_DB=$databaseName" `
    -e 'LANG=C.UTF-8' `
    $image `
    postgres -c fsync=off -c synchronous_commit=off -c full_page_writes=off | Out-Null
if ($LASTEXITCODE -ne 0) {
    throw "docker run failed for $containerName."
}

Write-Host "Waiting for PostgreSQL ($containerName, $image) to accept connections..."
$deadline = (Get-Date).AddSeconds($readyTimeoutSeconds)
while ($true) {
    # Over TCP, not the socket: the image's entrypoint initialises the cluster on a temporary server that listens on
    # the socket alone, and a socket probe answers ready while that server is still about to be stopped.
    docker exec $containerName pg_isready -h 127.0.0.1 -U $username 2>$null | Out-Null
    if ($LASTEXITCODE -eq 0) { break }
    if ((Get-Date) -ge $deadline) {
        throw "PostgreSQL ($containerName) did not accept connections within $readyTimeoutSeconds seconds. Check 'docker logs $containerName'."
    }
    Start-Sleep -Seconds 1
}

$mapping = docker port $containerName '5432/tcp' 2>$null | Select-Object -First 1
if (-not $mapping -or $mapping -notmatch ':(\d+)\s*$') {
    throw "Could not determine the published host port for ${containerName}:5432."
}
$port = $Matches[1]

docker exec $containerName psql -h 127.0.0.1 -U $username -d $databaseName -tA `
    -c "SELECT name || ' = ' || setting FROM pg_settings WHERE name IN ('fsync', 'synchronous_commit', 'full_page_writes', 'server_version') UNION ALL SELECT 'collation = ' || datcollate FROM pg_database WHERE datname = current_database() ORDER BY 1" |
    ForEach-Object { Write-Host "  $_" }
Write-Host "Started $containerName on port $port."
Write-Host ''

$environmentVariables = [ordered]@{
    JIM_TEST_RESET_HOST     = 'localhost'
    JIM_TEST_RESET_PORT     = $port
    JIM_TEST_RESET_USER     = $username
    JIM_TEST_RESET_PASSWORD = $password
    JIM_TEST_RESET_DB       = $databaseName
}

foreach ($variable in $environmentVariables.GetEnumerator()) {
    Write-Host "export $($variable.Key)=$($variable.Value)"
}

# Lets a CI workflow step consume these without a human copying and pasting the printout above.
if ($env:GITHUB_ENV) {
    foreach ($variable in $environmentVariables.GetEnumerator()) {
        Add-Content -Path $env:GITHUB_ENV -Value "$($variable.Key)=$($variable.Value)"
    }
}
