# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

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
            { Get-JIMMetaverseObject -ErrorAction Stop } | Should -Throw '*Connect-JIM*'
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

    Context 'Pagination safety (-All bounding)' {

        It 'Should expose a Force switch in the ListAll parameter set' {
            $param = (Get-Command Get-JIMMetaverseObject).Parameters['Force']
            $param | Should -Not -BeNullOrEmpty
            $param.SwitchParameter | Should -BeTrue
            $paramAttr = $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] -and $_.ParameterSetName -eq 'ListAll' }
            $paramAttr | Should -Not -BeNullOrEmpty
        }

        It '-All stops at the page cap and warns when the cap is reached without -Force' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                $original = $script:JIMMaxAllPages
                try {
                    $script:JIMMaxAllPages = 3
                    # Always report another page so, without a cap, this would page forever.
                    Mock Invoke-JIMApi { [PSCustomObject]@{ items = @([PSCustomObject]@{ id = [guid]::NewGuid() }); hasNextPage = $true; totalPages = 999999; totalCount = 100 } }

                    Get-JIMMetaverseObject -All -WarningVariable warnings -WarningAction SilentlyContinue | Out-Null

                    Should -Invoke Invoke-JIMApi -Times 3 -Exactly
                    ($warnings -join ' ') | Should -Match 'stopped after 3 pages'
                    ($warnings -join ' ') | Should -Match '-Force'
                }
                finally {
                    $script:JIMMaxAllPages = $original
                }
            }
        }

        It '-Force overrides the page cap and fetches every page' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                $original = $script:JIMMaxAllPages
                try {
                    $script:JIMMaxAllPages = 2
                    $script:allPollCount = 0
                    # Five pages of data; the cap is 2, so only -Force should reach page 5.
                    Mock Invoke-JIMApi {
                        $script:allPollCount++
                        [PSCustomObject]@{ items = @([PSCustomObject]@{ id = [guid]::NewGuid() }); hasNextPage = ($script:allPollCount -lt 5); totalPages = 5; totalCount = 100 }
                    }

                    Get-JIMMetaverseObject -All -Force -WarningVariable warnings -WarningAction SilentlyContinue | Out-Null

                    Should -Invoke Invoke-JIMApi -Times 5 -Exactly
                    ($warnings -join ' ') | Should -Not -Match 'stopped after'
                }
                finally {
                    $script:JIMMaxAllPages = $original
                }
            }
        }

        It '-All warns up front when the result set is large' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                # Single page, but a large totalCount should trigger the up-front warning.
                Mock Invoke-JIMApi { [PSCustomObject]@{ items = @(); hasNextPage = $false; totalPages = 500; totalCount = 50000 } }

                Get-JIMMetaverseObject -All -WarningVariable warnings -WarningAction SilentlyContinue | Out-Null

                ($warnings -join ' ') | Should -Match 'large result set'
            }
        }
    }
}

Describe 'Search-JIMMetaverseObject' {

    Context 'Endpoint routing' {

        It 'Forwards HasAttribute as a hasAttribute query parameter' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi { [PSCustomObject]@{ items = @(); hasNextPage = $false; totalPages = 1 } }

                Search-JIMMetaverseObject -PredefinedSearchUri "users" -HasAttribute "costCentre" | Out-Null

                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                    $Endpoint -like '*hasAttribute=costCentre*'
                }
            }
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
            { Get-JIMMetaverseObjectType -ErrorAction Stop } | Should -Throw '*Connect-JIM*'
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
            { Get-JIMMetaverseAttribute -ErrorAction Stop } | Should -Throw '*Connect-JIM*'
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

Describe 'Get-JIMMetaverseAttributePriority' {

    Context 'Parameter Validation' {

        BeforeAll {
            $command = Get-Command Get-JIMMetaverseAttributePriority
        }

        It 'Should have a mandatory AttributeId parameter' {
            $param = $command.Parameters['AttributeId']
            $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] -and $_.Mandatory } | Should -Not -BeNullOrEmpty
        }

        It 'Should have a mandatory ObjectTypeId parameter' {
            $param = $command.Parameters['ObjectTypeId']
            $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] -and $_.Mandatory } | Should -Not -BeNullOrEmpty
        }
    }

    Context 'Requires Connection' {

        BeforeEach {
            Disconnect-JIM
        }

        It 'Should throw when not connected' {
            { Get-JIMMetaverseAttributePriority -AttributeId 1 -ObjectTypeId 1 -ErrorAction Stop } | Should -Throw '*Connect-JIM*'
        }
    }

    Context 'Help Documentation' {

        BeforeAll {
            $help = Get-Help Get-JIMMetaverseAttributePriority -Full
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

Describe 'Set-JIMMetaverseAttributePriority' {

    Context 'Parameter Validation' {

        BeforeAll {
            $command = Get-Command Set-JIMMetaverseAttributePriority
        }

        It 'Should have a mandatory MappingId array parameter' {
            $param = $command.Parameters['MappingId']
            $param.ParameterType.Name | Should -Be 'Int32[]'
            $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] -and $_.Mandatory } | Should -Not -BeNullOrEmpty
        }

        It 'Should have PassThru switch parameter' {
            $command.Parameters['PassThru'].SwitchParameter | Should -BeTrue
        }

        It 'Should support ShouldProcess' {
            $command.Parameters['WhatIf'] | Should -Not -BeNullOrEmpty
            $command.Parameters['Confirm'] | Should -Not -BeNullOrEmpty
        }
    }

    Context 'Requires Connection' {

        BeforeEach {
            Disconnect-JIM
        }

        It 'Should throw when not connected' {
            { Set-JIMMetaverseAttributePriority -AttributeId 1 -ObjectTypeId 1 -MappingId 1, 2 -ErrorAction Stop } | Should -Throw '*Connect-JIM*'
        }
    }

    Context 'Help Documentation' {

        BeforeAll {
            $help = Get-Help Set-JIMMetaverseAttributePriority -Full
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

Describe 'Move-JIMMetaverseAttributePriority' {

    Context 'Parameter Validation' {

        BeforeAll {
            $command = Get-Command Move-JIMMetaverseAttributePriority
        }

        It 'Should have a mandatory MappingId parameter' {
            $param = $command.Parameters['MappingId']
            $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] -and $_.Mandatory } | Should -Not -BeNullOrEmpty
        }

        It 'Should have a mandatory Position parameter' {
            $param = $command.Parameters['Position']
            $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] -and $_.Mandatory } | Should -Not -BeNullOrEmpty
        }

        It 'Should have NullIsValue as a switch parameter' {
            $command.Parameters['NullIsValue'].SwitchParameter | Should -BeTrue
        }

        It 'Should support ShouldProcess' {
            $command.Parameters['WhatIf'] | Should -Not -BeNullOrEmpty
            $command.Parameters['Confirm'] | Should -Not -BeNullOrEmpty
        }
    }

    Context 'Requires Connection' {

        BeforeEach {
            Disconnect-JIM
        }

        It 'Should throw when not connected' {
            { Move-JIMMetaverseAttributePriority -AttributeId 1 -ObjectTypeId 1 -MappingId 1 -Position 1 -ErrorAction Stop } | Should -Throw '*Connect-JIM*'
        }
    }

    Context 'Help Documentation' {

        BeforeAll {
            $help = Get-Help Move-JIMMetaverseAttributePriority -Full
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

Describe 'New-JIMMetaverseAttribute' {

    Context 'Parameter Validation' {

        BeforeAll {
            $command = Get-Command New-JIMMetaverseAttribute
        }

        It 'Should have a Type parameter whose ValidateSet includes Decimal after LongNumber' {
            $set = $command.Parameters['Type'].Attributes | Where-Object { $_ -is [System.Management.Automation.ValidateSetAttribute] }
            $set.ValidValues | Should -Contain 'LongNumber'
            $set.ValidValues | Should -Contain 'Decimal'
        }
    }
}

Describe 'Set-JIMMetaverseAttribute' {

    Context 'Parameter Validation' {

        BeforeAll {
            $command = Get-Command Set-JIMMetaverseAttribute
        }

        It 'Should have a Type parameter whose ValidateSet includes Decimal after LongNumber' {
            $set = $command.Parameters['Type'].Attributes | Where-Object { $_ -is [System.Management.Automation.ValidateSetAttribute] }
            $set.ValidValues | Should -Contain 'LongNumber'
            $set.ValidValues | Should -Contain 'Decimal'
        }

        It 'Should have a RenderingHint parameter with the expected values' {
            $set = $command.Parameters['RenderingHint'].Attributes | Where-Object { $_ -is [System.Management.Automation.ValidateSetAttribute] }
            $set.ValidValues | Should -Contain 'Default'
            $set.ValidValues | Should -Contain 'Table'
            $set.ValidValues | Should -Contain 'ChipSet'
            $set.ValidValues | Should -Contain 'List'
        }

        It 'Should no longer expose an ObjectTypeIds parameter (bindings are managed separately)' {
            $command.Parameters.Keys | Should -Not -Contain 'ObjectTypeIds'
        }

        It 'Should support ShouldProcess' {
            $command.Parameters['WhatIf'] | Should -Not -BeNullOrEmpty
            $command.Parameters['Confirm'] | Should -Not -BeNullOrEmpty
        }
    }

    Context 'Requires Connection' {

        BeforeEach { Disconnect-JIM }

        It 'Should throw when not connected' {
            { Set-JIMMetaverseAttribute -Id 1 -Name 'X' -ErrorAction Stop } | Should -Throw '*Connect-JIM*'
        }
    }

    Context 'Endpoint routing' {

        It 'Routes a rename to a PATCH on the attribute endpoint' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi { [PSCustomObject]@{ id = 1 } }

                Set-JIMMetaverseAttribute -Id 1 -Name 'Renamed' -Confirm:$false | Out-Null

                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                    $Method -eq 'PATCH' -and $Endpoint -eq '/api/v1/metaverse/attributes/1' -and $Body.name -eq 'Renamed'
                }
            }
        }

        It 'Routes a type change to the schema endpoint, filling plurality from the current schema' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi -ParameterFilter { $Method -ne 'PATCH' } { [PSCustomObject]@{ id = 1; type = 'Text'; attributePlurality = 'MultiValued' } }
                Mock Invoke-JIMApi -ParameterFilter { $Method -eq 'PATCH' } { $null }

                Set-JIMMetaverseAttribute -Id 1 -Type Integer -Confirm:$false | Out-Null

                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                    $Method -eq 'PATCH' -and $Endpoint -eq '/api/v1/metaverse/attributes/1/schema' -and
                    $Body.type -eq 'Number' -and $Body.attributePlurality -eq 'MultiValued'
                }
            }
        }

        It 'Sends -Type Decimal to the schema endpoint verbatim (no alias normalisation)' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi -ParameterFilter { $Method -ne 'PATCH' } { [PSCustomObject]@{ id = 1; type = 'Text'; attributePlurality = 'SingleValued' } }
                Mock Invoke-JIMApi -ParameterFilter { $Method -eq 'PATCH' } { $null }

                Set-JIMMetaverseAttribute -Id 1 -Type Decimal -Confirm:$false | Out-Null

                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                    $Method -eq 'PATCH' -and $Endpoint -eq '/api/v1/metaverse/attributes/1/schema' -and
                    $Body.type -is [string] -and $Body.type -eq 'Decimal' -and $Body.attributePlurality -eq 'SingleValued'
                }
            }
        }
    }

    Context 'Help Documentation' {

        BeforeAll { $help = Get-Help Set-JIMMetaverseAttribute -Full }

        It 'Should have a synopsis' { $help.Synopsis | Should -Not -BeNullOrEmpty }
        It 'Should have examples' { $help.Examples.Example.Count | Should -BeGreaterThan 0 }
        It 'Should have related links' { $help.RelatedLinks | Should -Not -BeNullOrEmpty }
    }
}

Describe 'Remove-JIMMetaverseAttribute' {

    Context 'Requires Connection' {

        BeforeEach { Disconnect-JIM }

        It 'Should throw when not connected' {
            { Remove-JIMMetaverseAttribute -Id 1 -ErrorAction Stop } | Should -Throw '*Connect-JIM*'
        }
    }

    Context 'Safeguards' {

        It 'Refuses deletion and does not call DELETE when stored values exist' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi -ParameterFilter { $Endpoint -like '*deletion-preview*' } {
                    [PSCustomObject]@{ attributeId = 1; attributeName = 'CostCentre'; builtIn = $false; blockedByValues = $true; totalObjectsWithValues = 3; references = @() }
                }
                Mock Invoke-JIMApi -ParameterFilter { $Method -eq 'DELETE' } { $null }

                Remove-JIMMetaverseAttribute -Id 1 -Force -ErrorAction SilentlyContinue

                Should -Invoke Invoke-JIMApi -Times 0 -Exactly -ParameterFilter { $Method -eq 'DELETE' }
            }
        }

        It 'Sends the attribute name as confirmationName when references cascade' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi -ParameterFilter { $Endpoint -like '*deletion-preview*' } {
                    [PSCustomObject]@{ attributeId = 1; attributeName = 'CostCentre'; builtIn = $false; blockedByValues = $false; totalObjectsWithValues = 0; references = @(1, 2, 3) }
                }
                Mock Invoke-JIMApi -ParameterFilter { $Method -eq 'DELETE' } { $null }

                Remove-JIMMetaverseAttribute -Id 1 -Force

                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                    $Method -eq 'DELETE' -and $Endpoint -like '*confirmationName=CostCentre*'
                }
            }
        }
    }

    Context 'Help Documentation' {

        BeforeAll { $help = Get-Help Remove-JIMMetaverseAttribute -Full }

        It 'Should have a synopsis' { $help.Synopsis | Should -Not -BeNullOrEmpty }
        It 'Should have examples' { $help.Examples.Example.Count | Should -BeGreaterThan 0 }
    }
}

Describe 'Add-JIMMetaverseObjectTypeAttribute' {

    Context 'Requires Connection' {

        BeforeEach { Disconnect-JIM }

        It 'Should throw when not connected' {
            { Add-JIMMetaverseObjectTypeAttribute -AttributeId 1 -ObjectTypeId 1 -ErrorAction Stop } | Should -Throw '*Connect-JIM*'
        }
    }

    Context 'Endpoint routing' {

        It 'Posts to the bind endpoint' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi { [PSCustomObject]@{ id = 1 } }

                Add-JIMMetaverseObjectTypeAttribute -AttributeId 42 -ObjectTypeId 7 -Confirm:$false | Out-Null

                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                    $Method -eq 'POST' -and $Endpoint -like '/api/v1/metaverse/attributes/42/object-types/7*'
                }
            }
        }
    }

    Context 'Help Documentation' {

        BeforeAll { $help = Get-Help Add-JIMMetaverseObjectTypeAttribute -Full }

        It 'Should have a synopsis' { $help.Synopsis | Should -Not -BeNullOrEmpty }
        It 'Should have examples' { $help.Examples.Example.Count | Should -BeGreaterThan 0 }
    }
}

Describe 'Remove-JIMMetaverseObjectTypeAttribute' {

    Context 'Requires Connection' {

        BeforeEach { Disconnect-JIM }

        It 'Should throw when not connected' {
            { Remove-JIMMetaverseObjectTypeAttribute -AttributeId 1 -ObjectTypeId 1 -ErrorAction Stop } | Should -Throw '*Connect-JIM*'
        }
    }

    Context 'Safeguards' {

        It 'Does nothing when the attribute is not bound' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi -ParameterFilter { $Endpoint -like '*unassign-preview*' } {
                    [PSCustomObject]@{ attributeName = 'CostCentre'; metaverseObjectTypeName = 'User'; builtIn = $false; wasBound = $false; blockedByValues = $false; objectsWithValues = 0; references = @() }
                }
                Mock Invoke-JIMApi -ParameterFilter { $Method -eq 'DELETE' } { $null }

                Remove-JIMMetaverseObjectTypeAttribute -AttributeId 1 -ObjectTypeId 1 -Force -WarningAction SilentlyContinue

                Should -Invoke Invoke-JIMApi -Times 0 -Exactly -ParameterFilter { $Method -eq 'DELETE' }
            }
        }

        It 'Sends confirmationName when type-scoped references cascade' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi -ParameterFilter { $Endpoint -like '*unassign-preview*' } {
                    [PSCustomObject]@{ attributeName = 'CostCentre'; metaverseObjectTypeName = 'User'; builtIn = $false; wasBound = $true; blockedByValues = $false; objectsWithValues = 0; references = @([PSCustomObject]@{ kind = 'Binding' }, [PSCustomObject]@{ kind = 'AttributeFlow' }) }
                }
                Mock Invoke-JIMApi -ParameterFilter { $Method -eq 'DELETE' } { $null }

                Remove-JIMMetaverseObjectTypeAttribute -AttributeId 1 -ObjectTypeId 1 -Force

                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                    $Method -eq 'DELETE' -and $Endpoint -like '*confirmationName=CostCentre*'
                }
            }
        }
    }

    Context 'Help Documentation' {

        BeforeAll { $help = Get-Help Remove-JIMMetaverseObjectTypeAttribute -Full }

        It 'Should have a synopsis' { $help.Synopsis | Should -Not -BeNullOrEmpty }
        It 'Should have examples' { $help.Examples.Example.Count | Should -BeGreaterThan 0 }
    }
}

Describe 'Test-JIMMetaverseAttributeName' {

    Context 'Requires Connection' {

        BeforeEach { Disconnect-JIM }

        It 'Should throw when not connected' {
            { Test-JIMMetaverseAttributeName -Name 'X' -ErrorAction Stop } | Should -Throw '*Connect-JIM*'
        }
    }

    Context 'Behaviour' {

        It 'Returns the boolean availability and forwards excludeId' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi { [PSCustomObject]@{ name = 'CostCentre'; available = $false } }

                $result = Test-JIMMetaverseAttributeName -Name 'CostCentre' -ExcludeId 42

                $result | Should -BeOfType [bool]
                $result | Should -BeFalse
                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                    $Endpoint -like '*name-availability*name=CostCentre*' -and $Endpoint -like '*excludeId=42*'
                }
            }
        }
    }

    Context 'Help Documentation' {

        BeforeAll { $help = Get-Help Test-JIMMetaverseAttributeName -Full }

        It 'Should have a synopsis' { $help.Synopsis | Should -Not -BeNullOrEmpty }
        It 'Should have examples' { $help.Examples.Example.Count | Should -BeGreaterThan 0 }
    }
}

Describe 'Get-JIMMetaverseAttributeDeletionPreview' {

    Context 'Requires Connection' {

        BeforeEach { Disconnect-JIM }

        It 'Should throw when not connected' {
            { Get-JIMMetaverseAttributeDeletionPreview -Id 1 -ErrorAction Stop } | Should -Throw '*Connect-JIM*'
        }
    }

    Context 'Behaviour' {

        It 'Gets the deletion-preview endpoint' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi { [PSCustomObject]@{ attributeId = 1; attributeName = 'CostCentre' } }

                Get-JIMMetaverseAttributeDeletionPreview -Id 1 | Out-Null

                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                    $Endpoint -eq '/api/v1/metaverse/attributes/1/deletion-preview'
                }
            }
        }
    }

    Context 'Help Documentation' {

        BeforeAll { $help = Get-Help Get-JIMMetaverseAttributeDeletionPreview -Full }

        It 'Should have a synopsis' { $help.Synopsis | Should -Not -BeNullOrEmpty }
        It 'Should have examples' { $help.Examples.Example.Count | Should -BeGreaterThan 0 }
    }
}

Describe 'Set-JIMMetaverseObjectType' {

    Context 'Parameter Validation' {

        BeforeAll {
            $command = Get-Command Set-JIMMetaverseObjectType
        }

        It 'Should expose NewName, PluralName and Icon parameters' {
            $command.Parameters.Keys | Should -Contain 'NewName'
            $command.Parameters.Keys | Should -Contain 'PluralName'
            $command.Parameters.Keys | Should -Contain 'Icon'
        }

        It 'Should not put ValidateNotNullOrEmpty on the clearable Icon parameter' {
            $validators = $command.Parameters['Icon'].Attributes | Where-Object { $_ -is [System.Management.Automation.ValidateNotNullOrEmptyAttribute] }
            $validators | Should -BeNullOrEmpty
        }

        It 'Should support ShouldProcess' {
            $command.Parameters['WhatIf'] | Should -Not -BeNullOrEmpty
            $command.Parameters['Confirm'] | Should -Not -BeNullOrEmpty
        }
    }

    Context 'Requires Connection' {

        BeforeEach { Disconnect-JIM }

        It 'Should throw when not connected' {
            { Set-JIMMetaverseObjectType -Id 1 -NewName 'X' -ErrorAction Stop } | Should -Throw '*Connect-JIM*'
        }
    }

    Context 'Endpoint routing' {

        It 'Routes a rename to a PUT on the object type endpoint with the new name' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi { [PSCustomObject]@{ id = 5 } }

                Set-JIMMetaverseObjectType -Id 5 -NewName 'Gadget' -PluralName 'Gadgets' -Confirm:$false | Out-Null

                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                    $Method -eq 'PUT' -and $Endpoint -eq '/api/v1/metaverse/object-types/5' -and
                    $Body.name -eq 'Gadget' -and $Body.pluralName -eq 'Gadgets'
                }
            }
        }

        It 'Sends an empty icon when -Icon $null is passed (clear semantics)' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi { [PSCustomObject]@{ id = 5 } }

                Set-JIMMetaverseObjectType -Id 5 -Icon $null -Confirm:$false | Out-Null

                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                    $Method -eq 'PUT' -and $Body.ContainsKey('icon') -and $Body.icon -eq ''
                }
            }
        }
    }

    Context 'Help Documentation' {

        BeforeAll { $help = Get-Help Set-JIMMetaverseObjectType -Full }

        It 'Should have a synopsis' { $help.Synopsis | Should -Not -BeNullOrEmpty }
        It 'Should have examples' { $help.Examples.Example.Count | Should -BeGreaterThan 0 }
        It 'Should have related links' { $help.RelatedLinks | Should -Not -BeNullOrEmpty }
    }
}

Describe 'Remove-JIMMetaverseObjectType' {

    Context 'Requires Connection' {

        BeforeEach { Disconnect-JIM }

        It 'Should throw when not connected' {
            { Remove-JIMMetaverseObjectType -Id 1 -ErrorAction Stop } | Should -Throw '*Connect-JIM*'
        }
    }

    Context 'Safeguards' {

        It 'Refuses and does not call DELETE when Metaverse Objects of the type exist' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi -ParameterFilter { $Endpoint -like '*delete-preview*' } {
                    [PSCustomObject]@{ objectTypeId = 5; objectTypeName = 'Device'; builtIn = $false; blockedByObjects = $true; metaverseObjectCount = 12; blockedBySynchronisationRules = $false; synchronisationRules = @(); cascadeReferences = @() }
                }
                Mock Invoke-JIMApi -ParameterFilter { $Method -eq 'DELETE' } { $null }

                Remove-JIMMetaverseObjectType -Id 5 -Force -ErrorAction SilentlyContinue

                Should -Invoke Invoke-JIMApi -Times 0 -Exactly -ParameterFilter { $Method -eq 'DELETE' }
            }
        }

        It 'Refuses and does not call DELETE when Synchronisation Rules target the type' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi -ParameterFilter { $Endpoint -like '*delete-preview*' } {
                    [PSCustomObject]@{ objectTypeId = 5; objectTypeName = 'Device'; builtIn = $false; blockedByObjects = $false; metaverseObjectCount = 0; blockedBySynchronisationRules = $true; synchronisationRules = @([PSCustomObject]@{ description = 'Import Devices' }); cascadeReferences = @() }
                }
                Mock Invoke-JIMApi -ParameterFilter { $Method -eq 'DELETE' } { $null }

                Remove-JIMMetaverseObjectType -Id 5 -Force -ErrorAction SilentlyContinue

                Should -Invoke Invoke-JIMApi -Times 0 -Exactly -ParameterFilter { $Method -eq 'DELETE' }
            }
        }

        It 'Refuses and does not call DELETE for a built-in type' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi -ParameterFilter { $Endpoint -like '*delete-preview*' } {
                    [PSCustomObject]@{ objectTypeId = 1; objectTypeName = 'User'; builtIn = $true; blockedByObjects = $false; metaverseObjectCount = 0; blockedBySynchronisationRules = $false; synchronisationRules = @(); cascadeReferences = @() }
                }
                Mock Invoke-JIMApi -ParameterFilter { $Method -eq 'DELETE' } { $null }

                Remove-JIMMetaverseObjectType -Id 1 -Force -ErrorAction SilentlyContinue

                Should -Invoke Invoke-JIMApi -Times 0 -Exactly -ParameterFilter { $Method -eq 'DELETE' }
            }
        }

        It 'Sends the type name as confirmationName when references cascade' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi -ParameterFilter { $Endpoint -like '*delete-preview*' } {
                    [PSCustomObject]@{ objectTypeId = 5; objectTypeName = 'Device'; builtIn = $false; blockedByObjects = $false; metaverseObjectCount = 0; blockedBySynchronisationRules = $false; synchronisationRules = @(); cascadeReferences = @([PSCustomObject]@{ kind = 'PredefinedSearch'; description = 'All Devices' }) }
                }
                Mock Invoke-JIMApi -ParameterFilter { $Method -eq 'DELETE' } { $null }

                Remove-JIMMetaverseObjectType -Id 5 -Force

                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                    $Method -eq 'DELETE' -and $Endpoint -like '*confirmationName=Device*'
                }
            }
        }
    }

    Context 'Help Documentation' {

        BeforeAll { $help = Get-Help Remove-JIMMetaverseObjectType -Full }

        It 'Should have a synopsis' { $help.Synopsis | Should -Not -BeNullOrEmpty }
        It 'Should have examples' { $help.Examples.Example.Count | Should -BeGreaterThan 0 }
        It 'Should have related links' { $help.RelatedLinks | Should -Not -BeNullOrEmpty }
    }
}
