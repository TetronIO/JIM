# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

#Requires -Modules Pester

<#
.SYNOPSIS
    Pester tests for Get-LabControlCommand and Invoke-LabControl in Invoke-LabControl.ps1.

.DESCRIPTION
    Invoke-LabControl is how the runner drives the Hyper-V host that carries the Active Directory lab:
    it runs one of the scripts in test/integration/ad-lab/host/ over OpenSSH. The argument list is
    built by a pure function so the quoting, which is where such a call goes wrong, is tested without
    ssh or a host. Every JIM_AD_LAB_CONTROL_* variable set here is cleared again after each test.
#>

BeforeAll {
    . "$PSScriptRoot/Invoke-LabControl.ps1"

    $script:controlVariables = @(
        'JIM_AD_LAB_CONTROL_HOST', 'JIM_AD_LAB_CONTROL_USER', 'JIM_AD_LAB_CONTROL_KEY',
        'JIM_AD_LAB_CONTROL_PORT', 'JIM_AD_LAB_SCRIPT_ROOT'
    )
    function Clear-ControlEnvironment {
        foreach ($name in $script:controlVariables) {
            [System.Environment]::SetEnvironmentVariable($name, $null)
        }
    }
}

Describe 'Get-LabControlCommand' {
    BeforeEach {
        Clear-ControlEnvironment
        $env:JIM_AD_LAB_CONTROL_HOST = 'hv01.example.test'
    }

    AfterEach {
        Clear-ControlEnvironment
    }

    It 'builds an ssh command with the contract defaults' {
        $argv = Get-LabControlCommand -Script 'Get-LabDomainController.ps1' -Arguments @('-Name', 'dc-primary', '-AsJson')

        $argv | Should -Contain '-p'
        $argv[($argv.IndexOf('-p') + 1)] | Should -Be '22'
        $argv | Should -Contain 'jim-lab@hv01.example.test'
        $argv | Should -Not -Contain '-i'
        $argv[-1] | Should -Be 'pwsh -NoProfile -NonInteractive -File "C:\jim-ad-lab\Get-LabDomainController.ps1" -Name dc-primary -AsJson'
    }

    It 'never lets ssh prompt, which would hang an unattended run' {
        $argv = Get-LabControlCommand -Script 'Get-LabDomainController.ps1' -Arguments @()

        $argv | Should -Contain 'BatchMode=yes'
    }

    It 'takes the user, key, port and script root from the environment' {
        $env:JIM_AD_LAB_CONTROL_USER = 'labadmin'
        $env:JIM_AD_LAB_CONTROL_KEY = '/home/runner/.ssh/lab_ed25519'
        $env:JIM_AD_LAB_CONTROL_PORT = '2222'
        $env:JIM_AD_LAB_SCRIPT_ROOT = 'D:\labs\jim'

        $argv = Get-LabControlCommand -Script 'Restore-LabDomainController.ps1' -Arguments @('-Name', 'dc-source')

        $argv[($argv.IndexOf('-p') + 1)] | Should -Be '2222'
        $argv[($argv.IndexOf('-i') + 1)] | Should -Be '/home/runner/.ssh/lab_ed25519'
        $argv | Should -Contain 'labadmin@hv01.example.test'
        $argv[-1] | Should -Be 'pwsh -NoProfile -NonInteractive -File "D:\labs\jim\Restore-LabDomainController.ps1" -Name dc-source'
    }

    It 'does not double the separator when the script root ends in a backslash' {
        $env:JIM_AD_LAB_SCRIPT_ROOT = 'D:\labs\jim\'

        (Get-LabControlCommand -Script 'x.ps1' -Arguments @())[-1] | Should -Match ([regex]::Escape('"D:\labs\jim\x.ps1"'))
    }

    It 'appends .ps1 when the script name has no extension' {
        (Get-LabControlCommand -Script 'Get-LabDomainController' -Arguments @())[-1] | Should -Match ([regex]::Escape('"C:\jim-ad-lab\Get-LabDomainController.ps1"'))
    }

    It 'puts the destination last among the ssh options and the remote command last of all' {
        $argv = Get-LabControlCommand -Script 'x.ps1' -Arguments @('-A')

        $argv.IndexOf('jim-lab@hv01.example.test') | Should -Be ($argv.Count - 2)
    }

    It 'quotes an argument that contains a space' {
        $argv = Get-LabControlCommand -Script 'x.ps1' -Arguments @('-Note', 'a value with spaces')

        $argv[-1] | Should -Match ([regex]::Escape('-Note "a value with spaces"'))
    }

    It 'quotes an empty argument so it is not lost' {
        (Get-LabControlCommand -Script 'x.ps1' -Arguments @('-Note', ''))[-1] | Should -Match ([regex]::Escape('-Note ""'))
    }

    It 'leaves a comma-separated list of names as one argument' {
        (Get-LabControlCommand -Script 'x.ps1' -Arguments @('-ExtraCertificateNames', 'dc1,dc1.panoply.local'))[-1] |
            Should -Match ([regex]::Escape('-ExtraCertificateNames dc1,dc1.panoply.local'))
    }

    It 'refuses an argument the remote shell could interpret' -ForEach @(
        @{ Bad = 'a$b' }, @{ Bad = 'a`b' }, @{ Bad = 'a%b%' }, @{ Bad = "a`nb" }, @{ Bad = 'a;b' }, @{ Bad = 'a&b' }, @{ Bad = 'a|b' }, @{ Bad = 'say "hi"' }
    ) {
        { Get-LabControlCommand -Script 'x.ps1' -Arguments @('-Note', $Bad) } | Should -Throw '*unsafe*'
    }

    It 'refuses a script name that is a path' -ForEach @(
        @{ Name = '..\evil.ps1' }, @{ Name = 'sub/evil.ps1' }, @{ Name = 'C:\x.ps1' }, @{ Name = 'a b.ps1' }, @{ Name = '' }
    ) {
        { Get-LabControlCommand -Script $Name -Arguments @() } | Should -Throw
    }

    It 'refuses a script root containing a double quote' {
        $env:JIM_AD_LAB_SCRIPT_ROOT = 'C:\x" ; calc ; "'

        { Get-LabControlCommand -Script 'x.ps1' -Arguments @() } | Should -Throw '*JIM_AD_LAB_SCRIPT_ROOT*'
    }

    It 'throws naming the variable when the control host is not set' {
        Clear-ControlEnvironment

        { Get-LabControlCommand -Script 'x.ps1' -Arguments @() } | Should -Throw '*JIM_AD_LAB_CONTROL_HOST*'
    }

    It 'never carries a password: there is no parameter for one and no password variable is read' {
        $env:JIM_AD_LAB_ADMIN_PASSWORD = 'must-not-appear'
        try {
            (Get-LabControlCommand -Script 'x.ps1' -Arguments @('-Name', 'dc-primary')) -join ' ' | Should -Not -Match 'must-not-appear'
        }
        finally {
            [System.Environment]::SetEnvironmentVariable('JIM_AD_LAB_ADMIN_PASSWORD', $null)
        }
    }
}

Describe 'Invoke-LabControlProcess' {
    It 'returns the exit code, stdout and stderr of a process' {
        $result = Invoke-LabControlProcess -FilePath 'pwsh' -ArgumentList @('-NoProfile', '-Command', '[Console]::Out.Write("out"); [Console]::Error.Write("err"); exit 3') -TimeoutSeconds 60

        $result.ExitCode | Should -Be 3
        $result.StdOut | Should -Be 'out'
        $result.StdErr | Should -Be 'err'
    }

    It 'kills a process that outlives the timeout and says so' {
        { Invoke-LabControlProcess -FilePath 'pwsh' -ArgumentList @('-NoProfile', '-Command', 'Start-Sleep -Seconds 60') -TimeoutSeconds 1 } |
            Should -Throw '*timed out*'
    }

    It 'throws a clear message when the executable is missing' {
        { Invoke-LabControlProcess -FilePath 'jim-no-such-executable' -ArgumentList @() -TimeoutSeconds 5 } | Should -Throw '*jim-no-such-executable*'
    }
}

Describe 'Invoke-LabControl' {
    BeforeEach {
        Clear-ControlEnvironment
        $env:JIM_AD_LAB_CONTROL_HOST = 'hv01.example.test'
    }

    AfterEach {
        Clear-ControlEnvironment
    }

    It 'returns the script''s standard output' {
        Mock Invoke-LabControlProcess { @{ ExitCode = 0; StdOut = '{"State":"Running"}'; StdErr = '' } }

        Invoke-LabControl -Script 'Get-LabDomainController.ps1' -Arguments @('-Name', 'dc-primary', '-AsJson') | Should -Be '{"State":"Running"}'
    }

    It 'runs ssh with the argument list Get-LabControlCommand builds' {
        Mock Invoke-LabControlProcess { @{ ExitCode = 0; StdOut = ''; StdErr = '' } }
        $expected = Get-LabControlCommand -Script 'Get-LabDomainController.ps1' -Arguments @('-Name', 'dc-primary')

        Invoke-LabControl -Script 'Get-LabDomainController.ps1' -Arguments @('-Name', 'dc-primary') | Out-Null

        Should -Invoke Invoke-LabControlProcess -Times 1 -Exactly -ParameterFilter {
            $FilePath -eq 'ssh' -and (($ArgumentList -join "`n") -eq ($expected -join "`n"))
        }
    }

    It 'passes the timeout through' {
        Mock Invoke-LabControlProcess { @{ ExitCode = 0; StdOut = ''; StdErr = '' } }

        Invoke-LabControl -Script 'x.ps1' -Arguments @() -TimeoutSeconds 90 | Out-Null

        Should -Invoke Invoke-LabControlProcess -Times 1 -Exactly -ParameterFilter { $TimeoutSeconds -eq 90 }
    }

    It 'throws on a non-zero exit code with stderr and the exit code in the message' {
        Mock Invoke-LabControlProcess { @{ ExitCode = 1; StdOut = ''; StdErr = 'Checkpoint ''baseline'' does not exist' } }

        { Invoke-LabControl -Script 'Restore-LabDomainController.ps1' -Arguments @('-Name', 'dc-primary') } |
            Should -Throw "*Restore-LabDomainController.ps1*exit code 1*Checkpoint 'baseline' does not exist*"
    }

    It 'never puts a secret in the error: only the script name, exit code and stderr' {
        Mock Invoke-LabControlProcess { @{ ExitCode = 255; StdOut = ''; StdErr = 'Permission denied (publickey).' } }

        { Invoke-LabControl -Script 'x.ps1' -Arguments @('-Name', 'dc-primary') } | Should -Throw '*Permission denied*'
    }

    It 'returns an empty string when the script printed nothing' {
        Mock Invoke-LabControlProcess { @{ ExitCode = 0; StdOut = ''; StdErr = '' } }

        Invoke-LabControl -Script 'x.ps1' -Arguments @() | Should -Be ''
    }
}
