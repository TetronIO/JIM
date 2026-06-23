# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

#Requires -Modules Pester

<#
.SYNOPSIS
    Pester tests for Predefined Search criteria-group and criterion cmdlets.
#>

BeforeAll {
    $ModulePath = Join-Path $PSScriptRoot '..' 'JIM.psd1'
    Get-Module JIM -ErrorAction SilentlyContinue | Remove-Module -Force
    Import-Module $ModulePath -Force
}

AfterAll {
    Get-Module JIM -ErrorAction SilentlyContinue | Remove-Module -Force
}

Describe 'Predefined Search criteria cmdlets are exported' {
    It 'Exports <_>' -ForEach @(
        'Get-JIMPredefinedSearchCriteriaGroup'
        'New-JIMPredefinedSearchCriteriaGroup'
        'Set-JIMPredefinedSearchCriteriaGroup'
        'Remove-JIMPredefinedSearchCriteriaGroup'
        'New-JIMPredefinedSearchCriterion'
        'Set-JIMPredefinedSearchCriterion'
        'Remove-JIMPredefinedSearchCriterion'
    ) {
        Get-Command $_ -ErrorAction SilentlyContinue | Should -Not -BeNullOrEmpty
    }
}

Describe 'New-JIMPredefinedSearchCriteriaGroup' {

    BeforeAll { $command = Get-Command New-JIMPredefinedSearchCriteriaGroup }

    It 'Has a mandatory PredefinedSearchId parameter' {
        $param = $command.Parameters['PredefinedSearchId']
        $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] -and $_.Mandatory } | Should -Not -BeNullOrEmpty
    }

    It 'Restricts Type to All or Any' {
        $vs = $command.Parameters['Type'].Attributes | Where-Object { $_ -is [System.Management.Automation.ValidateSetAttribute] }
        $vs.ValidValues | Should -Contain 'All'
        $vs.ValidValues | Should -Contain 'Any'
    }

    It 'Supports ShouldProcess' {
        $binding = $command.ScriptBlock.Attributes | Where-Object { $_ -is [System.Management.Automation.CmdletBindingAttribute] }
        $binding.SupportsShouldProcess | Should -BeTrue
    }

    It 'Throws when not connected' {
        Disconnect-JIM
        { New-JIMPredefinedSearchCriteriaGroup -PredefinedSearchId 1 -ErrorAction Stop } | Should -Throw '*Connect-JIM*'
    }

    It 'Has help with examples and links' {
        $help = Get-Help New-JIMPredefinedSearchCriteriaGroup -Full
        $help.Synopsis | Should -Not -BeNullOrEmpty
        $help.Examples.Example.Count | Should -BeGreaterThan 0
        $help.RelatedLinks | Should -Not -BeNullOrEmpty
    }
}

Describe 'New-JIMPredefinedSearchCriterion' {

    BeforeAll { $command = Get-Command New-JIMPredefinedSearchCriterion }

    It 'Has ById and ByName parameter sets' {
        $command.ParameterSets.Name | Should -Contain 'ById'
        $command.ParameterSets.Name | Should -Contain 'ByName'
    }

    It 'Has a mandatory ComparisonType restricted to the supported operators' {
        $vs = $command.Parameters['ComparisonType'].Attributes | Where-Object { $_ -is [System.Management.Automation.ValidateSetAttribute] }
        foreach ($op in 'Equals','NotEquals','GreaterThan','GreaterThanOrEquals','LessThan','LessThanOrEquals','Contains','StartsWith') {
            $vs.ValidValues | Should -Contain $op
        }
    }

    It 'Has typed value parameters' {
        foreach ($p in 'StringValue','IntValue','LongValue','DateTimeValue','BoolValue','GuidValue','CaseSensitive') {
            $command.Parameters[$p] | Should -Not -BeNullOrEmpty
        }
    }

    It 'Types DateTimeValue as datetime and GuidValue as guid' {
        $command.Parameters['DateTimeValue'].ParameterType.FullName | Should -Be 'System.DateTime'
        $command.Parameters['GuidValue'].ParameterType.FullName | Should -Be 'System.Guid'
    }

    It 'Throws when not connected' {
        Disconnect-JIM
        { New-JIMPredefinedSearchCriterion -PredefinedSearchId 1 -GroupId 1 -MetaverseAttributeId 1 -ComparisonType Equals -StringValue 'x' -ErrorAction Stop } | Should -Throw '*Connect-JIM*'
    }

    It 'Has help with examples and links' {
        $help = Get-Help New-JIMPredefinedSearchCriterion -Full
        $help.Synopsis | Should -Not -BeNullOrEmpty
        $help.Examples.Example.Count | Should -BeGreaterThan 0
        $help.RelatedLinks | Should -Not -BeNullOrEmpty
    }
}

Describe 'Set-JIMPredefinedSearchCriterion' {

    BeforeAll { $command = Get-Command Set-JIMPredefinedSearchCriterion }

    It 'Has a mandatory CriterionId parameter aliased Id' {
        $param = $command.Parameters['CriterionId']
        $param | Should -Not -BeNullOrEmpty
        $param.Aliases | Should -Contain 'Id'
    }

    It 'Throws when not connected' {
        Disconnect-JIM
        { Set-JIMPredefinedSearchCriterion -PredefinedSearchId 1 -GroupId 1 -CriterionId 1 -MetaverseAttributeId 1 -ComparisonType Equals -StringValue 'x' -ErrorAction Stop } | Should -Throw '*Connect-JIM*'
    }
}

Describe 'Remove-JIMPredefinedSearchCriterion' {

    BeforeAll { $command = Get-Command Remove-JIMPredefinedSearchCriterion }

    It 'Supports ShouldProcess with High confirm impact' {
        $binding = $command.ScriptBlock.Attributes | Where-Object { $_ -is [System.Management.Automation.CmdletBindingAttribute] }
        $binding.SupportsShouldProcess | Should -BeTrue
        $binding.ConfirmImpact | Should -Be 'High'
    }

    It 'Throws when not connected' {
        Disconnect-JIM
        { Remove-JIMPredefinedSearchCriterion -PredefinedSearchId 1 -GroupId 1 -CriterionId 1 -Confirm:$false -ErrorAction Stop } | Should -Throw '*Connect-JIM*'
    }
}
