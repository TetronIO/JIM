# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

#Requires -Modules Pester

<#
.SYNOPSIS
    Pester tests for host/Install-LabScripts.ps1, the script that deploys the lab's host scripts to the Hyper-V host.

.DESCRIPTION
    The path mapping and the settings example are pure functions and are tested directly (the script returns
    early when dot-sourced). The rest runs the script end to end against the real checkout and a temporary
    destination, which also proves the result is a layout the module's own Resolve-LabLayout accepts. Nothing here
    needs Windows or Hyper-V.
#>

BeforeAll {
    $script:ScriptPath = Join-Path $PSScriptRoot 'host' 'Install-LabScripts.ps1'
    $script:RepositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..' '..' '..'))
    $script:ModulePath = Join-Path $PSScriptRoot 'guest' 'LabDomainController.psm1'
    . $script:ScriptPath

    function Get-TestDestinationPath {
        return (Join-Path ([System.IO.Path]::GetTempPath()) "jim-install-lab-$([guid]::NewGuid().ToString('N'))")
    }

    # Runs the script in a fresh pwsh so that a throw is an exit code and nothing leaks into the test scope.
    function Invoke-InstallScript {
        param([string[]]$Argument)
        $output = & pwsh -NoProfile -NonInteractive -File $script:ScriptPath @Argument 2>&1
        return [pscustomobject]@{ ExitCode = $LASTEXITCODE; Text = ($output | Out-String) }
    }
}

Describe 'Get-LabDeploymentMap' {
    BeforeAll {
        $script:Map = Get-LabDeploymentMap -RepositoryRoot '/repo' -Destination '/lab' `
            -HostScript @('New-LabDomainController.ps1', 'Get-LabDomainController.ps1', 'Install-LabScripts.ps1', 'notes.txt', 'Helpers.psm1') `
            -GuestFile @('LabDomainController.psm1', 'autounattend.xml', 'nested/extra.ps1')
    }

    It 'places the host scripts flat in the destination' {
        $entry = $script:Map | Where-Object { $_.Source -eq (Join-Path '/repo' 'test' 'integration' 'ad-lab' 'host' 'New-LabDomainController.ps1') }
        $entry.Area | Should -Be 'host'
        $entry.Destination | Should -Be (Join-Path '/lab' 'New-LabDomainController.ps1')
    }

    It 'leaves out the deployment script itself and anything that is not a script or module' {
        $names = @($script:Map | Where-Object Area -eq 'host' | ForEach-Object { Split-Path -Leaf $_.Destination })
        $names | Should -Contain 'Helpers.psm1'
        $names | Should -Not -Contain 'Install-LabScripts.ps1'
        $names | Should -Not -Contain 'notes.txt'
    }

    It 'keeps the guest files under guest, with their sub-folders' {
        $guest = @($script:Map | Where-Object Area -eq 'guest')
        $guest.Count | Should -Be 3
        ($guest | Where-Object { $_.Source -like '*extra.ps1' }).Destination | Should -Be (Join-Path '/lab' 'guest' 'nested' 'extra.ps1')
        ($guest | Where-Object { $_.Source -like '*autounattend.xml' }).Destination | Should -Be (Join-Path '/lab' 'guest' 'autounattend.xml')
    }

    It 'always maps the delegation file, from the Samba image folder to delegation' {
        $entry = $script:Map | Where-Object Area -eq 'delegation'
        $entry.Source | Should -Be (Join-Path '/repo' 'test' 'integration' 'docker' 'samba-ad-prebuilt' 'delegation' 'jim-ad-delegation.acl')
        $entry.Destination | Should -Be (Join-Path '/lab' 'delegation' 'jim-ad-delegation.acl')
    }

    It 'still maps the delegation file when there is nothing else' {
        $map = @(Get-LabDeploymentMap -RepositoryRoot '/repo' -Destination '/lab')
        $map.Count | Should -Be 1
        $map[0].Area | Should -Be 'delegation'
    }
}

Describe 'Get-LabSettingsExample' {
    BeforeAll {
        $script:Text = Get-LabSettingsExample
        $script:Settings = $script:Text | ConvertFrom-Json
    }

    It 'is valid JSON with the keys Invoke-LabRebuild.ps1 reads' {
        foreach ($key in 'isoPath', 'cumulativeUpdateDirectory', 'vhdDirectory', 'switchName', 'ntpServer', 'domainControllers') {
            $script:Settings.PSObject.Properties.Name | Should -Contain $key
        }
        $script:Settings.switchName | Should -Be 'Lab'
    }

    It 'extends every forest with the Exchange organisation, in a list a later product can join' {
        $extensions = @($script:Settings.directoryExtensions)
        $extensions.Count | Should -Be 1
        $extensions[0].product | Should -Be 'Exchange'
        $extensions[0].isoPath | Should -Match '\.iso$'
    }

    It 'has no dnsForwarder: the Lab switch has no uplink, so there is nothing to forward to' {
        $script:Settings.PSObject.Properties.Name | Should -Not -Contain 'dnsForwarder'
    }

    It 'points the time source and every gateway at the host address on the Lab switch' {
        $script:Settings.ntpServer | Should -Be '10.99.0.1'
        foreach ($dc in $script:Settings.domainControllers.PSObject.Properties.Value) {
            $dc.gateway | Should -Be $script:Settings.ntpServer
        }
    }

    It 'describes the three domain controllers, Recycle Bin on for the primary only' {
        $dcs = $script:Settings.domainControllers
        @($dcs.PSObject.Properties.Name) | Should -Be @('dc-primary', 'dc-source', 'dc-target')
        $dcs.'dc-primary'.domain | Should -Be 'PANOPLY.LOCAL'
        $dcs.'dc-source'.domain | Should -Be 'RESURGAM.LOCAL'
        $dcs.'dc-target'.domain | Should -Be 'GENTIAN.LOCAL'
        $dcs.'dc-primary'.enableRecycleBin | Should -BeTrue
        $dcs.'dc-source'.enableRecycleBin | Should -BeFalse
        $dcs.'dc-target'.enableRecycleBin | Should -BeFalse
        foreach ($dc in $dcs.PSObject.Properties.Value) {
            $dc.prefixLength | Should -Be 24
            $dc.ipAddress | Should -Match '^\d{1,3}(\.\d{1,3}){3}$'
            $dc.gateway | Should -Match '^\d{1,3}(\.\d{1,3}){3}$'
        }
    }

    It 'holds no password or secret' {
        $script:Text | Should -Not -Match '(?i)password|secret|token|key'
    }
}

Describe 'Install-LabScripts.ps1 end to end' {
    BeforeEach {
        $script:Destination = Get-TestDestinationPath
    }

    AfterEach {
        Remove-Item -LiteralPath $script:Destination -Recurse -Force -ErrorAction SilentlyContinue
    }

    It 'deploys the real checkout into the layout Resolve-LabLayout accepts' {
        $result = Invoke-InstallScript -Argument @('-Destination', $script:Destination)
        $result.ExitCode | Should -Be 0
        (Test-Path -LiteralPath (Join-Path $script:Destination 'New-LabDomainController.ps1')) | Should -BeTrue
        (Test-Path -LiteralPath (Join-Path $script:Destination 'guest' 'LabDomainController.psm1')) | Should -BeTrue
        (Test-Path -LiteralPath (Join-Path $script:Destination 'guest' 'autounattend.xml')) | Should -BeTrue
        (Test-Path -LiteralPath (Join-Path $script:Destination 'delegation' 'jim-ad-delegation.acl')) | Should -BeTrue
        (Test-Path -LiteralPath (Join-Path $script:Destination 'settings.example.json')) | Should -BeTrue
        (Test-Path -LiteralPath (Join-Path $script:Destination 'Install-LabScripts.ps1')) | Should -BeFalse

        Import-Module $script:ModulePath -Force
        $layout = Resolve-LabLayout -ScriptRoot $script:Destination
        $layout.GuestDirectory | Should -Be (Join-Path $script:Destination 'guest')
        $layout.DelegationAclPath | Should -Be (Join-Path $script:Destination 'delegation' 'jim-ad-delegation.acl')
    }

    It 'copies every host script the checkout has, byte for byte' {
        $null = Invoke-InstallScript -Argument @('-Destination', $script:Destination)
        $hostDirectory = Join-Path $PSScriptRoot 'host'
        $sources = @(Get-ChildItem -LiteralPath $hostDirectory -File | Where-Object { $_.Name -match '\.(ps1|psm1|psd1)$' -and $_.Name -ne 'Install-LabScripts.ps1' })
        $sources.Count | Should -BeGreaterThan 0
        foreach ($source in $sources) {
            $copy = Join-Path $script:Destination $source.Name
            (Get-FileHash -LiteralPath $copy).Hash | Should -Be (Get-FileHash -LiteralPath $source.FullName).Hash
        }
    }

    It 'is idempotent: a second run reports everything unchanged and copies nothing' {
        $null = Invoke-InstallScript -Argument @('-Destination', $script:Destination)
        $before = Get-ChildItem -LiteralPath $script:Destination -File -Recurse | ForEach-Object { "$($_.FullName)|$($_.LastWriteTimeUtc.Ticks)" }
        Start-Sleep -Milliseconds 1100
        $result = Invoke-InstallScript -Argument @('-Destination', $script:Destination)
        $result.ExitCode | Should -Be 0
        $result.Text | Should -Match '0 new, 0 updated, \d+ unchanged'
        $after = Get-ChildItem -LiteralPath $script:Destination -File -Recurse | Where-Object Name -ne 'settings.example.json' | ForEach-Object { "$($_.FullName)|$($_.LastWriteTimeUtc.Ticks)" }
        foreach ($line in $after) { $before | Should -Contain $line }
    }

    It 'replaces a file that differs from its source and says so' {
        $null = Invoke-InstallScript -Argument @('-Destination', $script:Destination)
        $target = Join-Path $script:Destination 'New-LabDomainController.ps1'
        Set-Content -LiteralPath $target -Value '# tampered'
        $result = Invoke-InstallScript -Argument @('-Destination', $script:Destination)
        $result.Text | Should -Match 'updated\s+New-LabDomainController.ps1'
        (Get-FileHash -LiteralPath $target).Hash | Should -Be (Get-FileHash -LiteralPath (Join-Path $PSScriptRoot 'host' 'New-LabDomainController.ps1')).Hash
    }

    It 'never creates settings.json unless asked' {
        $result = Invoke-InstallScript -Argument @('-Destination', $script:Destination)
        (Test-Path -LiteralPath (Join-Path $script:Destination 'settings.json')) | Should -BeFalse
        $result.Text | Should -Match 'settings.json does not exist yet'
    }

    It 'leaves an existing settings.json untouched when run without -CreateSettings' {
        $null = New-Item -ItemType Directory -Path $script:Destination
        $settings = Join-Path $script:Destination 'settings.json'
        Set-Content -LiteralPath $settings -Value '{ "mine": true }'
        $result = Invoke-InstallScript -Argument @('-Destination', $script:Destination)
        $result.ExitCode | Should -Be 0
        (Get-Content -LiteralPath $settings -Raw).Trim() | Should -Be '{ "mine": true }'
    }

    It 'creates settings.json from the example with -CreateSettings when there is none' {
        $result = Invoke-InstallScript -Argument @('-Destination', $script:Destination, '-CreateSettings')
        $result.ExitCode | Should -Be 0
        $settings = Join-Path $script:Destination 'settings.json'
        (Test-Path -LiteralPath $settings) | Should -BeTrue
        (Get-Content -LiteralPath $settings -Raw) | Should -Be (Get-Content -LiteralPath (Join-Path $script:Destination 'settings.example.json') -Raw)
    }

    It 'refuses -CreateSettings over an existing settings.json, and copies nothing' {
        $null = New-Item -ItemType Directory -Path $script:Destination
        $settings = Join-Path $script:Destination 'settings.json'
        Set-Content -LiteralPath $settings -Value '{ "mine": true }'
        $result = Invoke-InstallScript -Argument @('-Destination', $script:Destination, '-CreateSettings')
        $result.ExitCode | Should -Not -Be 0
        $result.Text | Should -Match 'Refusing to overwrite'
        (Get-Content -LiteralPath $settings -Raw).Trim() | Should -Be '{ "mine": true }'
        @(Get-ChildItem -LiteralPath $script:Destination -Recurse -File).Count | Should -Be 1
    }

    It 'refuses a folder that is not a JIM checkout' {
        $notRepository = Get-TestDestinationPath
        $null = New-Item -ItemType Directory -Path $notRepository
        try {
            $result = Invoke-InstallScript -Argument @('-Destination', $script:Destination, '-RepositoryRoot', $notRepository)
            $result.ExitCode | Should -Not -Be 0
            $result.Text | Should -Match 'is not a JIM checkout'
            (Test-Path -LiteralPath $script:Destination) | Should -BeFalse
        }
        finally {
            Remove-Item -LiteralPath $notRepository -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    It 'carries no em dash' {
        $emDash = [string][char]0x2014
        (Get-Content -LiteralPath $script:ScriptPath -Raw).Contains($emDash) | Should -BeFalse
        (Get-Content -LiteralPath $PSCommandPath -Raw).Contains($emDash) | Should -BeFalse
    }
}
