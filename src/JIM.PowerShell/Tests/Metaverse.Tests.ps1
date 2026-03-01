#Requires -Modules Pester

<#
.SYNOPSIS
    Pester tests for Metaverse cmdlets.
#>

BeforeAll {
    $ModulePath = Join-Path $PSScriptRoot '..' 'JIM.psd1'
    Get-Module JIM -ErrorAction SilentlyContinue | Remove-Module -Force
    Import-Module $ModulePath -Force
}

AfterAll {
    Get-Module JIM -ErrorAction SilentlyContinue | Remove-Module -Force
}

Describe 'Get-JIMMetaverseObject' {

    Context 'Parameter Sets' {

        BeforeAll {
            $command = Get-Command Get-JIMMetaverseObject
        }

        It 'Should have a List parameter set as default' {
            $command.DefaultParameterSet | Should -Be 'List'
        }

        It 'Should have a ById parameter set' {
            $command.ParameterSets.Name | Should -Contain 'ById'
        }

        It 'Should have a ListAll parameter set' {
            $command.ParameterSets.Name | Should -Contain 'ListAll'
        }
    }

    Context 'Parameter Validation' {

        BeforeAll {
            $command = Get-Command Get-JIMMetaverseObject
        }

        It 'Should have Id parameter that accepts GUID' {
            $param = $command.Parameters['Id']
            $param.ParameterType.Name | Should -Be 'Guid'
        }

        It 'Should have Id parameter that accepts pipeline by property name' {
            $param = $command.Parameters['Id']
            $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] -and $_.ValueFromPipelineByPropertyName } | Should -Not -BeNullOrEmpty
        }

        It 'Should have ObjectTypeId parameter' {
            $command.Parameters['ObjectTypeId'] | Should -Not -BeNullOrEmpty
        }

        It 'Should have ObjectTypeName parameter' {
            $command.Parameters['ObjectTypeName'] | Should -Not -BeNullOrEmpty
        }

        It 'Should have ObjectTypeName parameter with validation' {
            $param = $command.Parameters['ObjectTypeName']
            $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ValidateNotNullOrEmptyAttribute] } | Should -Not -BeNullOrEmpty
        }

        It 'Should have Search parameter that supports wildcards' {
            $param = $command.Parameters['Search']
            $param.Attributes | Where-Object { $_ -is [System.Management.Automation.SupportsWildcardsAttribute] } | Should -Not -BeNullOrEmpty
        }

        It 'Should have Attributes parameter as string array' {
            $param = $command.Parameters['Attributes']
            $param.ParameterType.Name | Should -Be 'String[]'
        }

        It 'Should have Page parameter with validation' {
            $param = $command.Parameters['Page']
            $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ValidateRangeAttribute] } | Should -Not -BeNullOrEmpty
        }

        It 'Should have PageSize parameter with validation' {
            $param = $command.Parameters['PageSize']
            $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ValidateRangeAttribute] } | Should -Not -BeNullOrEmpty
        }

        It 'Should have PageSize max of 100' {
            $param = $command.Parameters['PageSize']
            $rangeAttr = $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ValidateRangeAttribute] }
            $rangeAttr.MaxRange | Should -Be 100
        }

        It 'Should have All switch parameter' {
            $param = $command.Parameters['All']
            $param | Should -Not -BeNullOrEmpty
            $param.SwitchParameter | Should -BeTrue
        }

        It 'Should have All parameter in ListAll parameter set' {
            $param = $command.Parameters['All']
            $paramAttr = $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] -and $_.ParameterSetName -eq 'ListAll' }
            $paramAttr | Should -Not -BeNullOrEmpty
            $paramAttr.Mandatory | Should -BeTrue
        }

        It 'Should not allow Page and All together' {
            $pageParam = $command.Parameters['Page']
            $pageParamSets = $pageParam.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] } | ForEach-Object { $_.ParameterSetName }
            $pageParamSets | Should -Not -Contain 'ListAll'
        }
    }

    Context 'Requires Connection' {

        BeforeEach {
            Disconnect-JIM
        }

        It 'Should throw when not connected' {
            { Get-JIMMetaverseObject } | Should -Throw '*Connect-JIM*'
        }
    }

    Context 'Help Documentation' {

        BeforeAll {
            $help = Get-Help Get-JIMMetaverseObject -Full
        }

        It 'Should have a synopsis' {
            $help.Synopsis | Should -Not -BeNullOrEmpty
        }

        It 'Should have examples' {
            $help.Examples.Example.Count | Should -BeGreaterThan 0
        }

        It 'Should have related links' {
            $help.RelatedLinks | Should -Not -BeNullOrEmpty
        }
    }
}

Describe 'Get-JIMMetaverseObjectType' {

    Context 'Parameter Sets' {

        BeforeAll {
            $command = Get-Command Get-JIMMetaverseObjectType
        }

        It 'Should have a List parameter set as default' {
            $command.DefaultParameterSet | Should -Be 'List'
        }

        It 'Should have a ById parameter set' {
            $command.ParameterSets.Name | Should -Contain 'ById'
        }

        It 'Should have a ByName parameter set' {
            $command.ParameterSets.Name | Should -Contain 'ByName'
        }
    }

    Context 'Parameter Validation' {

        BeforeAll {
            $command = Get-Command Get-JIMMetaverseObjectType
        }

        It 'Should have Id parameter' {
            $command.Parameters['Id'] | Should -Not -BeNullOrEmpty
        }

        It 'Should have Name parameter' {
            $command.Parameters['Name'] | Should -Not -BeNullOrEmpty
        }

        It 'Should have Name parameter with validation' {
            $param = $command.Parameters['Name']
            $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ValidateNotNullOrEmptyAttribute] } | Should -Not -BeNullOrEmpty
        }

        It 'Should have IncludeChildObjects switch parameter' {
            $command.Parameters['IncludeChildObjects'].SwitchParameter | Should -BeTrue
        }
    }

    Context 'Requires Connection' {

        BeforeEach {
            Disconnect-JIM
        }

        It 'Should throw when not connected' {
            { Get-JIMMetaverseObjectType } | Should -Throw '*Connect-JIM*'
        }
    }

    Context 'Help Documentation' {

        BeforeAll {
            $help = Get-Help Get-JIMMetaverseObjectType -Full
        }

        It 'Should have a synopsis' {
            $help.Synopsis | Should -Not -BeNullOrEmpty
        }

        It 'Should have examples' {
            $help.Examples.Example.Count | Should -BeGreaterThan 0
        }
    }
}

Describe 'Get-JIMMetaverseAttribute' {

    Context 'Parameter Sets' {

        BeforeAll {
            $command = Get-Command Get-JIMMetaverseAttribute
        }

        It 'Should have a List parameter set as default' {
            $command.DefaultParameterSet | Should -Be 'List'
        }

        It 'Should have a ById parameter set' {
            $command.ParameterSets.Name | Should -Contain 'ById'
        }

        It 'Should have a ByName parameter set' {
            $command.ParameterSets.Name | Should -Contain 'ByName'
        }
    }

    Context 'Parameter Validation' {

        BeforeAll {
            $command = Get-Command Get-JIMMetaverseAttribute
        }

        It 'Should have Id parameter' {
            $command.Parameters['Id'] | Should -Not -BeNullOrEmpty
        }

        It 'Should have Name parameter' {
            $command.Parameters['Name'] | Should -Not -BeNullOrEmpty
        }

        It 'Should have Name parameter with validation' {
            $param = $command.Parameters['Name']
            $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ValidateNotNullOrEmptyAttribute] } | Should -Not -BeNullOrEmpty
        }

        It 'Should have Id parameter that accepts pipeline by property name' {
            $param = $command.Parameters['Id']
            $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] -and $_.ValueFromPipelineByPropertyName } | Should -Not -BeNullOrEmpty
        }
    }

    Context 'Requires Connection' {

        BeforeEach {
            Disconnect-JIM
        }

        It 'Should throw when not connected' {
            { Get-JIMMetaverseAttribute } | Should -Throw '*Connect-JIM*'
        }
    }

    Context 'Help Documentation' {

        BeforeAll {
            $help = Get-Help Get-JIMMetaverseAttribute -Full
        }

        It 'Should have a synopsis' {
            $help.Synopsis | Should -Not -BeNullOrEmpty
        }

        It 'Should have examples' {
            $help.Examples.Example.Count | Should -BeGreaterThan 0
        }
    }
}
