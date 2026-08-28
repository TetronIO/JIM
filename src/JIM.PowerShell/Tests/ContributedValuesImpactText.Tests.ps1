# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

#Requires -Modules Pester

<#
.SYNOPSIS
    Pester tests for the private Get-JIMContributedValuesImpactText helper (#1537).

.DESCRIPTION
    The Remove-JIMSyncRule and Remove-JIMSyncRuleMapping cmdlets include this helper's sentence in their
    ShouldProcess confirmation text, so an administrator sees the impact on contributed Metaverse attribute
    values before agreeing to a deletion. ShouldProcess prompt text cannot be captured from a test, so the
    composition is pinned here and the cmdlets' own tests prove the helper is invoked with the right inputs.
#>

BeforeAll {
    $ModulePath = Join-Path $PSScriptRoot '..' 'JIM.psd1'
    Get-Module JIM -ErrorAction SilentlyContinue | Remove-Module -Force
    Import-Module $ModulePath -Force
}

AfterAll {
    Get-Module JIM -ErrorAction SilentlyContinue | Remove-Module -Force
}

Describe 'Get-JIMContributedValuesImpactText' {

    It 'Returns nothing when the summary reports no contributed values' {
        InModuleScope JIM {
            $valueSummary = [PSCustomObject]@{ Attributes = @(); TotalValues = 0; TotalObjects = 0 }
            Get-JIMContributedValuesImpactText -Summary $valueSummary | Should -BeNullOrEmpty
        }
    }

    It 'Returns nothing when there is no summary at all (the lookup failed)' {
        InModuleScope JIM {
            Get-JIMContributedValuesImpactText -Summary $null | Should -BeNullOrEmpty
        }
    }

    It 'Describes the recall with attribute and Metaverse Object counts' {
        InModuleScope JIM {
            $valueSummary = [PSCustomObject]@{
                Attributes   = @(
                    [PSCustomObject]@{ AttributeId = 1; AttributeName = 'Display Name'; ValueCount = 1204; ObjectCount = 1204 },
                    [PSCustomObject]@{ AttributeId = 2; AttributeName = 'Department'; ValueCount = 1100; ObjectCount = 1100 },
                    [PSCustomObject]@{ AttributeId = 3; AttributeName = 'Job Title'; ValueCount = 900; ObjectCount = 900 }
                )
                TotalValues  = 3204
                TotalObjects = 1204
            }

            $text = Get-JIMContributedValuesImpactText -Summary $valueSummary

            $text | Should -Be '3 attribute(s) across 1,204 Metaverse Object(s) will be recalled'
        }
    }

    It 'Warns that kept values lose their provenance when -KeepContributedValues is supplied' {
        InModuleScope JIM {
            $valueSummary = [PSCustomObject]@{
                Attributes   = @(
                    [PSCustomObject]@{ AttributeId = 1; AttributeName = 'Display Name'; ValueCount = 1204; ObjectCount = 1204 }
                )
                TotalValues  = 1204
                TotalObjects = 1204
            }

            $text = Get-JIMContributedValuesImpactText -Summary $valueSummary -KeepContributedValues

            $text | Should -Be '1 attribute(s) across 1,204 Metaverse Object(s) will be KEPT with no provenance; nothing will ever recall these values'
        }
    }

    It 'Says a deferred recall happens at the next Full Synchronisation of the contributing system' {
        InModuleScope JIM {
            $valueSummary = [PSCustomObject]@{
                Attributes   = @(
                    [PSCustomObject]@{ AttributeId = 5; AttributeName = 'Department'; ValueCount = 96; ObjectCount = 96 }
                )
                TotalValues  = 96
                TotalObjects = 96
            }

            $text = Get-JIMContributedValuesImpactText -Summary $valueSummary -DeferredRecall

            $text | Should -Be '1 attribute(s) across 96 Metaverse Object(s) will be recalled at the next Full Synchronisation of the contributing system'
        }
    }

    It 'The keep warning wins over the deferred wording when both apply' {
        # A mapping deletion with -KeepContributedValues never recalls anything, deferred or otherwise.
        InModuleScope JIM {
            $valueSummary = [PSCustomObject]@{
                Attributes   = @(
                    [PSCustomObject]@{ AttributeId = 5; AttributeName = 'Department'; ValueCount = 96; ObjectCount = 96 }
                )
                TotalValues  = 96
                TotalObjects = 96
            }

            $text = Get-JIMContributedValuesImpactText -Summary $valueSummary -KeepContributedValues -DeferredRecall

            $text | Should -Be '1 attribute(s) across 96 Metaverse Object(s) will be KEPT with no provenance; nothing will ever recall these values'
        }
    }
}
