# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

#Requires -Modules Pester

<#
.SYNOPSIS
    Pester tests for Confirm-JIMAction: -Force skips a cmdlet's confirmation prompt, and never its -WhatIf.

.DESCRIPTION
    Twenty-two cmdlets wrote "if ($Force -or $PSCmdlet.ShouldProcess(...))", so -Force short-circuited
    ShouldProcess and "-WhatIf -Force" carried the action out instead of previewing it: Reset-JIMSystem
    -WhatIf -Force wiped the system. Confirm-JIMAction is now the one place a cmdlet combines the two.
#>

BeforeAll {
    $ModulePath = Join-Path $PSScriptRoot '..' 'JIM.psd1'
    Get-Module JIM -ErrorAction SilentlyContinue | Remove-Module -Force
    Import-Module $ModulePath -Force
}

AfterAll {
    Get-Module JIM -ErrorAction SilentlyContinue | Remove-Module -Force
}

Describe 'Confirm-JIMAction' {

    BeforeAll {
        # A cmdlet whose confirmation impact is High, so without -Force or -Confirm:$false it would prompt.
        InModuleScope JIM {
            function script:Invoke-JIMProbe {
                [CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
                param([switch]$Force)
                Confirm-JIMAction -Cmdlet $PSCmdlet -Target 'the probe' -Action 'Probe' -Force:$Force
            }
        }
    }

    It 'proceeds without prompting when -Force is given' {
        InModuleScope JIM { Invoke-JIMProbe -Force } | Should -BeTrue
    }

    It 'does not proceed when -WhatIf is given with -Force' {
        InModuleScope JIM { Invoke-JIMProbe -WhatIf -Force } | Should -BeFalse
    }

    It 'does not proceed when -WhatIf is given' {
        InModuleScope JIM { Invoke-JIMProbe -WhatIf } | Should -BeFalse
    }

    It 'proceeds when the prompt is declined in advance with -Confirm:$false' {
        InModuleScope JIM { Invoke-JIMProbe -Confirm:$false } | Should -BeTrue
    }
}

Describe '-WhatIf -Force previews and changes nothing' {

    BeforeAll {
        InModuleScope JIM {
            $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey'; BaseUri = 'https://jim.example.com' }
        }
    }

    AfterAll {
        InModuleScope JIM { $script:JIMConnection = $null }
    }

    It '<Cmdlet> sends nothing that changes JIM' -ForEach @(
        @{ Cmdlet = 'Reset-JIMSystem'; Parameters = @{} }
        @{ Cmdlet = 'Remove-JIMApiKey'; Parameters = @{ Id = [guid]'7f3e0000-0000-0000-0000-000000000001' } }
        @{ Cmdlet = 'Remove-JIMCertificate'; Parameters = @{ Id = [guid]'7f3e0000-0000-0000-0000-000000000002' } }
        @{ Cmdlet = 'Stop-JIMWorkerTask'; Parameters = @{ Id = [guid]'7f3e0000-0000-0000-0000-000000000003' } }
        @{ Cmdlet = 'Stop-JIMPendingPasswordChange'; Parameters = @{ ConnectedSystemId = 7 } }
        @{ Cmdlet = 'Remove-JIMRoleMember'; Parameters = @{ RoleId = 1; MetaverseObjectId = [guid]'7f3e0000-0000-0000-0000-000000000004' } }
    ) {
        InModuleScope JIM -Parameters @{ Cmdlet = $Cmdlet; Parameters = $Parameters } {
            param($Cmdlet, $Parameters)
            $script:changes = [System.Collections.Generic.List[string]]::new()
            Mock Invoke-JIMApi {
                if ($Method -and $Method -ne 'GET') { $script:changes.Add("$Method $Endpoint") }
                [PSCustomObject]@{ id = 1; name = 'Example'; items = @() }
            }
            & $Cmdlet @Parameters -WhatIf -Force | Out-Null
            $script:changes | Should -BeNullOrEmpty -Because "-WhatIf must only preview, whatever else is given"
        }
    }
}

Describe 'No cmdlet lets -Force bypass -WhatIf' {

    It 'combines -Force with ShouldProcess only through Confirm-JIMAction' {
        $public = Join-Path $PSScriptRoot '..' 'Public'
        $offenders = foreach ($file in Get-ChildItem -LiteralPath $public -Filter '*.ps1' -Recurse) {
            $lineNumber = 0
            foreach ($line in Get-Content -LiteralPath $file.FullName) {
                $lineNumber++
                # Case-sensitive, and the call itself: a variable named $shouldProcess holding Confirm-JIMAction's answer is fine.
                if ($line -cmatch '\$Force\b.*\.ShouldProcess\(|\.ShouldProcess\(.*\$Force\b') { "$($file.Name):$lineNumber" }
            }
        }
        $offenders | Should -BeNullOrEmpty -Because 'a cmdlet that ORs $Force with ShouldProcess carries out the action under -WhatIf -Force; call Confirm-JIMAction -Force:$Force instead'
    }
}
