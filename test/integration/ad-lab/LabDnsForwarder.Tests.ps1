# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

#Requires -Modules Pester

<#
.SYNOPSIS
    Pester tests for the optional DNS forwarder of the domain controller build scripts.

.DESCRIPTION
    The lab network has no uplink, so a domain controller has nothing to forward DNS to. -DnsForwarder is therefore
    optional in host/New-LabDomainController.ps1 and guest/Initialize-LabDomainController.ps1, and the shared module
    accepts its absence (see LabDomainController.Tests.ps1). Both scripts are Windows-only and cannot run in CI, so this
    checks their parameter blocks and the host's settings hashtable with the PowerShell parser, and lifts the small pure
    function that decides "no forwarder" out of the guest script text (also with the parser) and tests it.
#>

BeforeAll {
    $script:HostScript = Join-Path $PSScriptRoot 'host' 'New-LabDomainController.ps1'
    $script:GuestScript = Join-Path $PSScriptRoot 'guest' 'Initialize-LabDomainController.ps1'

    function Get-ScriptAst {
        param([string]$Path)
        $errors = $null
        $ast = [System.Management.Automation.Language.Parser]::ParseFile($Path, [ref]$null, [ref]$errors)
        if ($errors) { throw "Parse errors in ${Path}: $($errors -join '; ')" }
        return $ast
    }

    function Get-ParameterAst {
        param([string]$Path, [string]$Name)
        $ast = Get-ScriptAst -Path $Path
        return $ast.ParamBlock.Parameters | Where-Object { $_.Name.VariablePath.UserPath -eq $Name }
    }

    function Test-ParameterMandatory {
        param($Parameter)
        foreach ($attribute in $Parameter.Attributes) {
            if ($attribute.TypeName.Name -eq 'Parameter') {
                foreach ($argument in $attribute.NamedArguments) {
                    if ($argument.ArgumentName -eq 'Mandatory') {
                        return ($argument.ExpressionOmitted -or ($argument.Argument.Extent.Text -eq '$true'))
                    }
                }
            }
        }
        return $false
    }

    function Get-ScriptFunctionText {
        param([string]$Path, [string]$Name)
        $ast = Get-ScriptAst -Path $Path
        $function = $ast.Find({ param($node) ($node -is [System.Management.Automation.Language.FunctionDefinitionAst]) -and ($node.Name -eq $Name) }, $true)
        if ($null -eq $function) { throw "$Path defines no function called $Name." }
        return $function.Extent.Text
    }

    # The functions are lifted out of the scripts so the tests run the code the scripts run.
    . ([scriptblock]::Create((Get-ScriptFunctionText -Path $script:GuestScript -Name 'Resolve-DnsForwarder')))
}

Describe 'The DnsForwarder parameter is optional' {
    It 'is not mandatory in New-LabDomainController.ps1' {
        $parameter = Get-ParameterAst -Path $script:HostScript -Name 'DnsForwarder'
        $parameter | Should -Not -BeNullOrEmpty
        Test-ParameterMandatory -Parameter $parameter | Should -BeFalse
    }

    It 'is not mandatory in Initialize-LabDomainController.ps1' {
        $parameter = Get-ParameterAst -Path $script:GuestScript -Name 'DnsForwarder'
        $parameter | Should -Not -BeNullOrEmpty
        Test-ParameterMandatory -Parameter $parameter | Should -BeFalse
    }

    It 'is still a parameter the other lab parameters rely on staying mandatory' {
        Test-ParameterMandatory -Parameter (Get-ParameterAst -Path $script:HostScript -Name 'Gateway') | Should -BeTrue
        Test-ParameterMandatory -Parameter (Get-ParameterAst -Path $script:HostScript -Name 'NtpServer') | Should -BeTrue
    }

    It 'no longer requires DnsForwarder in the Prepare or Configure phase' {
        $text = Get-Content -LiteralPath $script:GuestScript -Raw
        $text | Should -Not -Match "Assert-Setting -Name [^\r\n]*'DnsForwarder'"
    }
}

Describe 'Resolve-DnsForwarder (guest)' {
    It 'is none when nothing was given: null, empty or blank' {
        Resolve-DnsForwarder -Forwarder $null | Should -BeNullOrEmpty
        Resolve-DnsForwarder -Forwarder '' | Should -BeNullOrEmpty
        Resolve-DnsForwarder -Forwarder '   ' | Should -BeNullOrEmpty
    }

    It 'returns a given forwarder, trimmed' {
        Resolve-DnsForwarder -Forwarder '10.20.30.2' | Should -Be '10.20.30.2'
        Resolve-DnsForwarder -Forwarder ' 10.20.30.2 ' | Should -Be '10.20.30.2'
    }

    It 'has no stand-in value to interpret: empty means none, and nothing else does' {
        $text = Get-Content -LiteralPath $script:GuestScript -Raw
        $text | Should -Not -Match 'OwnAddress'
        (Get-Command -Name Resolve-DnsForwarder).Parameters.Keys | Should -Not -Contain 'OwnAddress'
    }
}

Describe 'New-LabDomainController.ps1 passes no DnsForwarder when none is given' {
    BeforeAll {
        $script:HostAst = Get-ScriptAst -Path $script:HostScript
        $script:Assignments = @($script:HostAst.FindAll({ param($node) $node -is [System.Management.Automation.Language.AssignmentStatementAst] }, $true))
    }

    It 'has no stand-in for the forwarder' {
        $script:HostAst.Find({ param($node) ($node -is [System.Management.Automation.Language.FunctionDefinitionAst]) -and ($node.Name -eq 'Get-DnsForwarderSetting') }, $true) | Should -BeNullOrEmpty
    }

    It 'leaves DnsForwarder out of the settings it builds for the guest phases' {
        $literal = @($script:Assignments | Where-Object { $_.Left.Extent.Text -eq '$settings' -and $_.Right.Extent.Text.TrimStart().StartsWith('@{') })
        $literal.Count | Should -Be 1
        $keys = @($literal[0].Right.Find({ param($node) $node -is [System.Management.Automation.Language.HashtableAst] }, $true).KeyValuePairs | ForEach-Object { $_.Item1.Extent.Text })
        $keys | Should -Contain 'Gateway'
        $keys | Should -Not -Contain 'DnsForwarder'
    }

    It 'adds DnsForwarder only inside a check that one was given' {
        $ifs = @($script:HostAst.FindAll({
                    param($node)
                    ($node -is [System.Management.Automation.Language.IfStatementAst]) -and
                    ($node.Clauses[0].Item1.Extent.Text -match '\$DnsForwarder') -and
                    ($node.Clauses[0].Item2.Extent.Text -match "\`$settings\['DnsForwarder'\]\s*=")
                }, $true))
        $ifs.Count | Should -Be 1
    }
}

Describe 'Carries no em dash' {
    It 'in either script or this file' {
        $emDash = [string][char]0x2014
        foreach ($path in $script:HostScript, $script:GuestScript, $PSCommandPath) {
            (Get-Content -LiteralPath $path -Raw).Contains($emDash) | Should -BeFalse -Because $path
        }
    }
}
