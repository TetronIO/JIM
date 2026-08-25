# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

#Requires -Modules Pester

<#
.SYNOPSIS
    Pester tests for the auxiliary class cmdlets.

.DESCRIPTION
    Concentrates on the requests these cmdlets build, because each one is where a wrong shape is silent: setting
    auxiliary classes replaces the whole set rather than adding to it, clearing has to be expressible, a full scan
    must not carry a sample size, and never having run discovery is an ordinary state rather than an error.
#>

BeforeAll {
    $ModulePath = Join-Path $PSScriptRoot '..' 'JIM.psd1'
    Get-Module JIM -ErrorAction SilentlyContinue | Remove-Module -Force
    Import-Module $ModulePath -Force
}

AfterAll {
    Get-Module JIM -ErrorAction SilentlyContinue | Remove-Module -Force
}

Describe 'Get-JIMConnectedSystemAuxiliaryClass' {

    Context 'Requires Connection' {

        BeforeEach {
            Disconnect-JIM
        }

        It 'Should throw when not connected' {
            { Get-JIMConnectedSystemAuxiliaryClass -ConnectedSystemId 1 -ObjectTypeId 5 -ErrorAction Stop } | Should -Throw '*Connect-JIM*'
        }
    }

    Context 'Filtering' {

        It 'Returns every offered class by default' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi {
                    @(
                        [PSCustomObject]@{ objectTypeId = 2; name = 'posixAccount'; merged = $true; isSuggested = $true },
                        [PSCustomObject]@{ objectTypeId = 3; name = 'shadowAccount'; merged = $false; isSuggested = $true },
                        [PSCustomObject]@{ objectTypeId = 4; name = 'sambaSamAccount'; merged = $false; isSuggested = $false }
                    )
                }

                $result = @(Get-JIMConnectedSystemAuxiliaryClass -ConnectedSystemId 1 -ObjectTypeId 5)

                $result.Count | Should -Be 3
            }
        }

        It 'Returns only the merged classes when asked for them' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi {
                    @(
                        [PSCustomObject]@{ objectTypeId = 2; name = 'posixAccount'; merged = $true; isSuggested = $true },
                        [PSCustomObject]@{ objectTypeId = 4; name = 'sambaSamAccount'; merged = $false; isSuggested = $false }
                    )
                }

                $result = @(Get-JIMConnectedSystemAuxiliaryClass -ConnectedSystemId 1 -ObjectTypeId 5 -MergedOnly)

                $result.Count | Should -Be 1
                $result[0].name | Should -Be 'posixAccount'
            }
        }

        It 'Returns only the suggested classes when asked for them' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi {
                    @(
                        [PSCustomObject]@{ objectTypeId = 3; name = 'shadowAccount'; merged = $false; isSuggested = $true },
                        [PSCustomObject]@{ objectTypeId = 4; name = 'sambaSamAccount'; merged = $false; isSuggested = $false }
                    )
                }

                $result = @(Get-JIMConnectedSystemAuxiliaryClass -ConnectedSystemId 1 -ObjectTypeId 5 -SuggestedOnly)

                $result.Count | Should -Be 1
                $result[0].name | Should -Be 'shadowAccount'
            }
        }
    }
}

Describe 'Set-JIMConnectedSystemAuxiliaryClass' {

    Context 'Requires Connection' {

        BeforeEach {
            Disconnect-JIM
        }

        It 'Should throw when not connected' {
            { Set-JIMConnectedSystemAuxiliaryClass -ConnectedSystemId 1 -ObjectTypeId 5 -AuxiliaryClassObjectTypeId 2 -Confirm:$false -ErrorAction Stop } |
                Should -Throw '*Connect-JIM*'
        }
    }

    Context 'The request it builds' {

        It 'Sends the whole set, so what is not named is withdrawn' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                $script:capturedBody = $null
                Mock Invoke-JIMApi { $script:capturedBody = $Body }

                Set-JIMConnectedSystemAuxiliaryClass -ConnectedSystemId 1 -ObjectTypeId 5 -AuxiliaryClassObjectTypeId 2, 3 -Confirm:$false

                $script:capturedBody.objectTypeIds | Should -Be @(2, 3)
            }
        }

        It 'Sends an empty set for -Clear, rather than omitting the field' {
            InModuleScope JIM {
                # An omitted field would read as "change nothing", which is the opposite of what -Clear means.
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                $script:capturedBody = $null
                Mock Invoke-JIMApi { $script:capturedBody = $Body }

                Set-JIMConnectedSystemAuxiliaryClass -ConnectedSystemId 1 -ObjectTypeId 5 -Clear -Confirm:$false

                $script:capturedBody.ContainsKey('objectTypeIds') | Should -BeTrue
                @($script:capturedBody.objectTypeIds).Count | Should -Be 0
            }
        }

        It 'Keeps a single class an array, so it serialises as one and not as a scalar' {
            InModuleScope JIM {
                # Assigning from an if-expression enumerates its output, which collapses a one-element
                # array to a scalar Int32; ConvertTo-Json then sends {"objectTypeIds":16} and the API
                # rejects it with a 400. Merging exactly one class is the cmdlet's own first example,
                # and Scenario 19's Merge step is where this shipped bug surfaced. The value must
                # still be an array at the serialisation boundary.
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                $script:capturedBody = $null
                Mock Invoke-JIMApi { $script:capturedBody = $Body }

                Set-JIMConnectedSystemAuxiliaryClass -ConnectedSystemId 1 -ObjectTypeId 5 -AuxiliaryClassObjectTypeId 16 -Confirm:$false

                $script:capturedBody.objectTypeIds -is [System.Collections.ICollection] | Should -BeTrue
                ($script:capturedBody | ConvertTo-Json -Depth 10 -Compress) | Should -Be '{"objectTypeIds":[16]}'
            }
        }

        It 'Serialises -Clear as an empty JSON array, not null' {
            InModuleScope JIM {
                # The same if-expression enumeration turns @() into $null, which serialises as
                # {"objectTypeIds":null}: "change nothing" instead of "withdraw everything".
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                $script:capturedBody = $null
                Mock Invoke-JIMApi { $script:capturedBody = $Body }

                Set-JIMConnectedSystemAuxiliaryClass -ConnectedSystemId 1 -ObjectTypeId 5 -Clear -Confirm:$false

                ($script:capturedBody | ConvertTo-Json -Depth 10 -Compress) | Should -Be '{"objectTypeIds":[]}'
            }
        }

        It 'Puts to the object type auxiliary classes endpoint' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                $script:capturedEndpoint = $null
                $script:capturedMethod = $null
                Mock Invoke-JIMApi { $script:capturedEndpoint = $Endpoint; $script:capturedMethod = $Method }

                Set-JIMConnectedSystemAuxiliaryClass -ConnectedSystemId 1 -ObjectTypeId 5 -AuxiliaryClassObjectTypeId 2 -Confirm:$false

                $script:capturedEndpoint | Should -Be '/api/v1/synchronisation/connected-systems/1/object-types/5/auxiliary-classes'
                $script:capturedMethod | Should -Be 'PUT'
            }
        }

        It 'Returns nothing without -PassThru' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi { [PSCustomObject]@{ id = 5 } }

                $result = Set-JIMConnectedSystemAuxiliaryClass -ConnectedSystemId 1 -ObjectTypeId 5 -AuxiliaryClassObjectTypeId 2 -Confirm:$false

                $result | Should -BeNullOrEmpty
            }
        }
    }
}

Describe 'Set-JIMConnectedSystemStructuralCarrierClass' {

    Context 'The request it builds' {

        It 'Sends the carrier id' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                $script:capturedBody = $null
                $script:capturedEndpoint = $null
                Mock Invoke-JIMApi { $script:capturedBody = $Body; $script:capturedEndpoint = $Endpoint }

                Set-JIMConnectedSystemStructuralCarrierClass -ConnectedSystemId 1 -ObjectTypeId 12 -StructuralCarrierObjectTypeId 3 -Confirm:$false

                $script:capturedBody.structuralCarrierObjectTypeId | Should -Be 3
                $script:capturedEndpoint | Should -Be '/api/v1/synchronisation/connected-systems/1/object-types/12/structural-carrier'
            }
        }

        It 'Sends a null carrier for -Clear' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                $script:capturedBody = $null
                Mock Invoke-JIMApi { $script:capturedBody = $Body }

                Set-JIMConnectedSystemStructuralCarrierClass -ConnectedSystemId 1 -ObjectTypeId 12 -Clear -Confirm:$false

                $script:capturedBody.ContainsKey('structuralCarrierObjectTypeId') | Should -BeTrue
                $script:capturedBody.structuralCarrierObjectTypeId | Should -BeNullOrEmpty
            }
        }
    }
}

Describe 'Start-JIMConnectedSystemAuxiliaryClassDiscovery' {

    Context 'The request it builds' {

        It 'Sends the sample size for a quick sample' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                $script:capturedBody = $null
                Mock Invoke-JIMApi { $script:capturedBody = $Body }

                Start-JIMConnectedSystemAuxiliaryClassDiscovery -ConnectedSystemId 1 -Scope QuickSample -SampleSizePerObjectType 20000 -Confirm:$false

                $script:capturedBody.scope | Should -Be 'QuickSample'
                $script:capturedBody.sampleSizePerObjectType | Should -Be 20000
            }
        }

        It 'Omits the sample size for a full scan, which has no per-type limit' {
            InModuleScope JIM {
                # Sending one would be a number that silently did nothing.
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                $script:capturedBody = $null
                Mock Invoke-JIMApi { $script:capturedBody = $Body }

                Start-JIMConnectedSystemAuxiliaryClassDiscovery -ConnectedSystemId 1 -Scope FullScan -Confirm:$false

                $script:capturedBody.scope | Should -Be 'FullScan'
                $script:capturedBody.ContainsKey('sampleSizePerObjectType') | Should -BeFalse
            }
        }

        It 'Posts to the discovery endpoint' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                $script:capturedEndpoint = $null
                $script:capturedMethod = $null
                Mock Invoke-JIMApi { $script:capturedEndpoint = $Endpoint; $script:capturedMethod = $Method }

                Start-JIMConnectedSystemAuxiliaryClassDiscovery -ConnectedSystemId 1 -Scope QuickSample -Confirm:$false

                $script:capturedEndpoint | Should -Be '/api/v1/synchronisation/connected-systems/1/auxiliary-class-discovery'
                $script:capturedMethod | Should -Be 'POST'
            }
        }

        It 'Rejects a scope the API does not have' {
            { Start-JIMConnectedSystemAuxiliaryClassDiscovery -ConnectedSystemId 1 -Scope 'Everything' -Confirm:$false -ErrorAction Stop } |
                Should -Throw
        }
    }
}

Describe 'Get-JIMConnectedSystemAuxiliaryClassDiscovery' {

    Context 'Reading the last run' {

        It 'Returns the run the API reports' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi {
                    [PSCustomObject]@{
                        id = 11
                        scope = 'QuickSample'
                        status = 'Complete'
                        entriesRead = 15000
                        results = @([PSCustomObject]@{ auxiliaryClassName = 'posixAccount'; entryCount = 1204 })
                    }
                }

                $result = Get-JIMConnectedSystemAuxiliaryClassDiscovery -ConnectedSystemId 1

                $result.status | Should -Be 'Complete'
                $result.entriesRead | Should -Be 15000
                $result.results[0].auxiliaryClassName | Should -Be 'posixAccount'
            }
        }

        It 'Gets from the discovery endpoint' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                $script:capturedEndpoint = $null
                Mock Invoke-JIMApi { $script:capturedEndpoint = $Endpoint }

                Get-JIMConnectedSystemAuxiliaryClassDiscovery -ConnectedSystemId 1

                $script:capturedEndpoint | Should -Be '/api/v1/synchronisation/connected-systems/1/auxiliary-class-discovery'
            }
        }
    }

    Context 'Requires Connection' {

        BeforeEach {
            Disconnect-JIM
        }

        It 'Should throw when not connected' {
            { Get-JIMConnectedSystemAuxiliaryClassDiscovery -ConnectedSystemId 1 -ErrorAction Stop } | Should -Throw '*Connect-JIM*'
        }
    }
}

Describe 'Module surface' {

    It 'Exports every auxiliary class cmdlet' {
        $exported = (Get-Module JIM).ExportedFunctions.Keys

        foreach ($name in @(
                'Get-JIMConnectedSystemAuxiliaryClass',
                'Set-JIMConnectedSystemAuxiliaryClass',
                'Set-JIMConnectedSystemStructuralCarrierClass',
                'Start-JIMConnectedSystemAuxiliaryClassDiscovery',
                'Get-JIMConnectedSystemAuxiliaryClassDiscovery')) {
            $exported | Should -Contain $name
        }
    }
}
