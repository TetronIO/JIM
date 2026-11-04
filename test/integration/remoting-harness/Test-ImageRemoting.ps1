# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Opens a PowerShell remoting session to the harness from inside a built JIM image, run as that image is deployed.

.DESCRIPTION
    Proves what a JIM image contributes to signing in to Exchange (#2021): MIT Kerberos (libgssapi-krb5-2), the
    environment System.Management.Automation needs (XDG_CACHE_HOME on a read-only root filesystem), and the non-root
    user. It publishes Tetron.PowerShellRemoting.ImageProbe, layers it onto -Image, and runs it the way
    docker-compose.yml runs JIM: read-only, as UID 1654, with /tmp as the only writable path, every capability dropped
    and no privilege escalation. The probe opens one session to the stand-in WinRM server and runs
    one command with a bound parameter.

    The probe brings its own copy of the remoting library, at the version the lock files pin, so it proves the image's
    native libraries and environment, not the copy of the library under the image's /app.

    Needs the harness running (Start-RemotingHarness.ps1) and its JIM_REMOTING_HARNESS_KDC and
    JIM_REMOTING_HARNESS_WINRM variables. Changes nothing outside the throwaway probe image it builds and removes.

.PARAMETER Image
    The JIM image to test, already built, for example jim-worker:probe or jim-web:probe.

.EXAMPLE
    ./test/integration/remoting-harness/Test-ImageRemoting.ps1 -Image jim-worker:probe
    Runs the probe inside the Worker image against the harness and fails if no session opens.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Image
)

$ErrorActionPreference = 'Stop'

foreach ($name in 'JIM_REMOTING_HARNESS_KDC', 'JIM_REMOTING_HARNESS_WINRM') {
    if (-not [Environment]::GetEnvironmentVariable($name)) {
        throw "$name is not set: start the harness with Start-RemotingHarness.ps1 first."
    }
}

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '../../..')).Path
$probeProject = Join-Path $repositoryRoot 'test/Tetron.PowerShellRemoting.ImageProbe/Tetron.PowerShellRemoting.ImageProbe.csproj'
$context = Join-Path ([IO.Path]::GetTempPath()) "jim-image-probe-$([guid]::NewGuid().ToString('N'))"
$probeImage = "jim-image-probe:$([guid]::NewGuid().ToString('N').Substring(0, 12))"

try {
    Write-Host "Publishing the probe..."
    dotnet publish $probeProject -c Release -o (Join-Path $context 'probe') --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw 'Could not publish the probe.' }

    # The probe layered on the image under test, and nothing else: the image's own user, environment and libraries.
    Set-Content -Path (Join-Path $context 'Dockerfile') -Value @(
        'ARG JIM_IMAGE'
        'FROM ${JIM_IMAGE}'
        'COPY --chown=app:app probe/ /probe/'
        'ENTRYPOINT ["dotnet", "/probe/Tetron.PowerShellRemoting.ImageProbe.dll"]'
    )
    docker build --quiet --build-arg "JIM_IMAGE=$Image" -t $probeImage $context | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Could not layer the probe onto $Image." }

    Write-Host "Opening a session from inside $Image..."
    # The harness's own test account, which Start-RemotingHarness.ps1 creates: nothing secret.
    docker run --rm `
        --read-only --tmpfs /tmp `
        --user 1654:1654 --cap-drop ALL --security-opt no-new-privileges `
        --network jim-remoting-harness `
        -e "JIM_PROBE_REALM=HARNESS.TEST" `
        -e "JIM_PROBE_KDC=$env:JIM_REMOTING_HARNESS_KDC" `
        -e "JIM_PROBE_URI=http://$($env:JIM_REMOTING_HARNESS_WINRM):5985/wsman" `
        -e "JIM_PROBE_SPN_HOST=winrm-harness.harness.test" `
        -e "JIM_PROBE_UPN=svc-winrm@HARNESS.TEST" `
        -e "JIM_PROBE_PASSWORD=Client@123!" `
        $probeImage
    if ($LASTEXITCODE -ne 0) { throw "No PowerShell remoting session could be opened from inside $Image." }
}
finally {
    docker image rm --force $probeImage 2>$null | Out-Null
    if (Test-Path $context) { Remove-Item -Recurse -Force $context }
}
