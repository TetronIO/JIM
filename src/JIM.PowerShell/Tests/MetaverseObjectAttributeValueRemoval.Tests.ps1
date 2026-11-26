# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

#Requires -Modules Pester

<#
.SYNOPSIS
    Pester tests for Remove-JIMMetaverseObjectAttributeValue (#3059, Phase 3).

.DESCRIPTION
    The cmdlet names the values to remove by text, by value id or both, resolves an attribute name to its id, and
    sends one DELETE carrying exactly what was named. -WhatIf previews even with -Force, and nothing is sent when no
    value is named.
#>

BeforeAll {
    $ModulePath = Join-Path $PSScriptRoot '..' 'JIM.psd1'
    Get-Module JIM -ErrorAction SilentlyContinue | Remove-Module -Force
    Import-Module $ModulePath -Force
}

AfterAll {
    Get-Module JIM -ErrorAction SilentlyContinue | Remove-Module -Force
}

Describe 'Remove-JIMMetaverseObjectAttributeValue' {

    It 'Should confirm before removing' {
        (Get-Command Remove-JIMMetaverseObjectAttributeValue).ScriptBlock.Attributes |
            Where-Object { $_ -is [System.Management.Automation.CmdletBindingAttribute] -and $_.SupportsShouldProcess -and $_.ConfirmImpact -eq 'High' } |
            Should -Not -BeNullOrEmpty
    }

    It 'Should resolve the attribute by name and send the named values in one DELETE' {
        InModuleScope JIM {
            $script:JIMConnection = @{ Url = 'https://jim.example.test' }
            Mock Resolve-JIMMetaverseAttribute { [PSCustomObject]@{ id = 10; name = 'Account Audit Log' } }
            Mock Invoke-JIMApi -MockWith { [PSCustomObject]@{ activityId = [guid]::NewGuid(); removedValues = @('J Smith') } }

            Remove-JIMMetaverseObjectAttributeValue -MetaverseObjectId ([guid]'8f1c2d3e-4a5b-6c7d-8e9f-0a1b2c3d4e5f') `
                -AttributeName 'Account Audit Log' -Value 'J Smith' -Force | Out-Null

            Should -Invoke Invoke-JIMApi -Times 1 -ParameterFilter {
                $Method -eq 'DELETE' -and
                $Endpoint -eq '/api/v1/metaverse/objects/8f1c2d3e-4a5b-6c7d-8e9f-0a1b2c3d4e5f/attributes/10/values' -and
                @($Body.values) -join '|' -eq 'J Smith' -and
                -not $Body.ContainsKey('valueIds')
            }
        }
    }

    It 'Should send value ids when named by -ValueId' {
        InModuleScope JIM {
            $script:JIMConnection = @{ Url = 'https://jim.example.test' }
            Mock Invoke-JIMApi -MockWith { [PSCustomObject]@{ activityId = [guid]::NewGuid(); removedValues = @('J Smith') } }

            Remove-JIMMetaverseObjectAttributeValue -MetaverseObjectId ([guid]'8f1c2d3e-4a5b-6c7d-8e9f-0a1b2c3d4e5f') `
                -AttributeId 10 -ValueId ([guid]'11111111-2222-3333-4444-555555555555') -Force | Out-Null

            Should -Invoke Invoke-JIMApi -Times 1 -ParameterFilter {
                @($Body.valueIds) -join '|' -eq '11111111-2222-3333-4444-555555555555' -and -not $Body.ContainsKey('values')
            }
        }
    }

    It 'Should not remove anything under -WhatIf, even with -Force' {
        InModuleScope JIM {
            $script:JIMConnection = @{ Url = 'https://jim.example.test' }
            Mock Invoke-JIMApi { throw 'the API must not be called' }

            Remove-JIMMetaverseObjectAttributeValue -MetaverseObjectId ([guid]'8f1c2d3e-4a5b-6c7d-8e9f-0a1b2c3d4e5f') `
                -AttributeId 10 -Value 'J Smith' -Force -WhatIf

            Should -Invoke Invoke-JIMApi -Times 0
        }
    }

    It 'Should refuse without calling the API when no value is named' {
        InModuleScope JIM {
            $script:JIMConnection = @{ Url = 'https://jim.example.test' }
            Mock Invoke-JIMApi { throw 'the API must not be called' }

            Remove-JIMMetaverseObjectAttributeValue -MetaverseObjectId ([guid]'8f1c2d3e-4a5b-6c7d-8e9f-0a1b2c3d4e5f') `
                -AttributeId 10 -Force -ErrorAction SilentlyContinue -ErrorVariable failure

            $failure | Should -Not -BeNullOrEmpty
            Should -Invoke Invoke-JIMApi -Times 0
        }
    }
}
