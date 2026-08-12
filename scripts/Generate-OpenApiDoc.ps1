# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Generates the OpenAPI document for JIM.Web in lightweight mode.

.DESCRIPTION
    Runs JIM.Web with JIM_OPENAPI_GENERATE=true, which skips database,
    SSO, and authentication initialisation. The app generates the OpenAPI
    JSON document and exits immediately.

    No external services (database, Keycloak) are required.

.PARAMETER OutputPath
    Where to write the OpenAPI JSON. Defaults to
    src/JIM.Web/wwwroot/api/openapi/v1.json.

.PARAMETER NoBuild
    Run the already-built JIM.Web rather than building it first. For callers that
    have just built the solution, such as the openapi-document CI job; the build
    would otherwise be repeated for no benefit.

.EXAMPLE
    ./scripts/Generate-OpenApiDoc.ps1

.EXAMPLE
    ./scripts/Generate-OpenApiDoc.ps1 -NoBuild -OutputPath /tmp/openapi-v1.json
#>
[CmdletBinding()]
param(
    [string]$OutputPath,
    [switch]$NoBuild
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path $PSScriptRoot
$projectPath = Join-Path $repoRoot "src/JIM.Web/JIM.Web.csproj"

if (-not $OutputPath) {
    $OutputPath = Join-Path $repoRoot "src/JIM.Web/wwwroot/api/openapi/v1.json"
}

# Set lightweight generation mode
$env:JIM_OPENAPI_GENERATE = "true"
$env:JIM_OPENAPI_OUTPUT_PATH = $OutputPath

# Workstation GC, for this run only. JIM.Web is a web application, so Server GC is on by
# default: one heap per core, collected only under memory pressure. That is right for a
# server holding a working set for hours, and wrong for a process that allocates hard for
# five minutes, writes a 1 MB file and exits. Schema generation walks every route and every
# response type, allocating transiently the whole way, and with nothing pushing back the
# heaps simply grew: measured 11.3 GB peak resident on main, and 15.25 GB on a branch that
# added three fields, at which point the process is killed by the OOM killer on a 16 GB
# GitHub-hosted runner. The same run under Workstation GC peaks at 1.28 GB and produces a
# byte-identical document.
#
# Set here rather than in JIM.Web.csproj so it applies only to generation: the shipped
# application keeps Server GC, which is the right choice for it.
$env:DOTNET_gcServer = "0"

# Provide minimal placeholder env vars for any that are not already set
if (-not $env:JIM_LOG_LEVEL)         { $env:JIM_LOG_LEVEL = "Warning" }
if (-not $env:JIM_LOG_PATH)          { $env:JIM_LOG_PATH = "/tmp/jim-openapi-gen" }
if (-not $env:JIM_SSO_AUTHORITY)     { $env:JIM_SSO_AUTHORITY = "http://localhost:8181/realms/jim" }
if (-not $env:JIM_SSO_CLIENT_ID)     { $env:JIM_SSO_CLIENT_ID = "jim-web" }
if (-not $env:JIM_SSO_SECRET)        { $env:JIM_SSO_SECRET = "placeholder" }
if (-not $env:JIM_SSO_API_SCOPE)     { $env:JIM_SSO_API_SCOPE = "jim-api" }
if (-not $env:JIM_SSO_CLAIM_TYPE)    { $env:JIM_SSO_CLAIM_TYPE = "sub" }
if (-not $env:JIM_SSO_MV_ATTRIBUTE)  { $env:JIM_SSO_MV_ATTRIBUTE = "Subject Identifier" }
if (-not $env:JIM_SSO_INITIAL_ADMIN) { $env:JIM_SSO_INITIAL_ADMIN = "placeholder" }
if (-not $env:JIM_DB_HOSTNAME)       { $env:JIM_DB_HOSTNAME = "localhost" }
if (-not $env:JIM_DB_NAME)           { $env:JIM_DB_NAME = "jim" }
if (-not $env:JIM_DB_USERNAME)       { $env:JIM_DB_USERNAME = "jim" }
if (-not $env:JIM_DB_PASSWORD)       { $env:JIM_DB_PASSWORD = "placeholder" }

Write-Host "Generating OpenAPI document..." -ForegroundColor Cyan
$runArgs = @("run", "--project", $projectPath, "--no-launch-profile")
if ($NoBuild) { $runArgs += "--no-build" }
dotnet @runArgs

if ($LASTEXITCODE -ne 0) {
    Write-Error "OpenAPI generation failed with exit code $LASTEXITCODE"
    exit $LASTEXITCODE
}

if (Test-Path $OutputPath) {
    $size = (Get-Item $OutputPath).Length
    Write-Host "OpenAPI document generated: $OutputPath ($size bytes)" -ForegroundColor Green
} else {
    Write-Error "Expected output file not found: $OutputPath"
    exit 1
}
