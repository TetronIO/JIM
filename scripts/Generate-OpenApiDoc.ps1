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

.EXAMPLE
    ./scripts/Generate-OpenApiDoc.ps1
#>
[CmdletBinding()]
param(
    [string]$OutputPath
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
dotnet run --project $projectPath --no-launch-profile

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
