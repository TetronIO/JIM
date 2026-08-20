# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

#Requires -Modules Pester

<#
.SYNOPSIS
    Tests for the module's private name-to-id resolvers and the shared paging helper behind them.

.DESCRIPTION
    The resolvers list an endpoint and match on name, but the list endpoints are paginated with a
    server-side default page size, so a resolver that reads only the first response page cannot see
    entities beyond it: Resolve-JIMMetaverseAttribute failed for 'Last Name' on a stock deployment
    (97 attributes, default page of 25). These tests pin that every resolver reads every page, and
    that the paging helper handles both paginated envelopes and bare-array responses.
#>

BeforeAll {
    Get-Module JIM | Remove-Module -Force -ErrorAction SilentlyContinue
    Import-Module (Join-Path $PSScriptRoot '..' 'JIM.psd1') -Force
}

AfterAll {
    Get-Module JIM | Remove-Module -Force -ErrorAction SilentlyContinue
}

Describe 'Get-JIMPagedItems' {

    It 'Reads every page until totalCount items are collected' {
        InModuleScope JIM {
            $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
            Mock Invoke-JIMApi {
                if ($Endpoint -like '*page=1*') {
                    [PSCustomObject]@{
                        items      = @([PSCustomObject]@{ id = 1; name = 'First' }, [PSCustomObject]@{ id = 2; name = 'Second' })
                        totalCount = 3
                    }
                }
                else {
                    [PSCustomObject]@{
                        items      = @([PSCustomObject]@{ id = 3; name = 'Third' })
                        totalCount = 3
                    }
                }
            }

            $items = Get-JIMPagedItems -Endpoint '/api/v1/metaverse/attributes'

            $items.Count | Should -Be 3
            $items[2].name | Should -BeExactly 'Third'
            Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter { $Endpoint -like '*page=2*' }
        }
    }

    It 'Requests the maximum page size the API allows' {
        InModuleScope JIM {
            $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
            Mock Invoke-JIMApi {
                [PSCustomObject]@{ items = @([PSCustomObject]@{ id = 1; name = 'Only' }); totalCount = 1 }
            }

            Get-JIMPagedItems -Endpoint '/api/v1/metaverse/attributes' | Out-Null

            Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter { $Endpoint -like '*pageSize=100*' }
        }
    }

    It 'Appends paging to an endpoint that already carries a query string' {
        InModuleScope JIM {
            $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
            Mock Invoke-JIMApi {
                [PSCustomObject]@{ items = @([PSCustomObject]@{ id = 1; name = 'Only' }); totalCount = 1 }
            }

            Get-JIMPagedItems -Endpoint '/api/v1/things?filter=x' | Out-Null

            Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter { $Endpoint -like '/api/v1/things?filter=x&page=1*' }
        }
    }

    It 'Handles a bare-array response without an items envelope' {
        InModuleScope JIM {
            $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
            # a bare array of 2+ objects is exactly the shape where `$response.items` member enumeration
            # returns a truthy array of nulls; the helper must not fall into that trap.
            Mock Invoke-JIMApi {
                @([PSCustomObject]@{ id = 1; name = 'One' }, [PSCustomObject]@{ id = 2; name = 'Two' })
            }

            $items = Get-JIMPagedItems -Endpoint '/api/v1/things'

            $items.Count | Should -Be 2
            $items[0].name | Should -BeExactly 'One'
            Should -Invoke Invoke-JIMApi -Times 1 -Exactly
        }
    }

    It 'Stops when a page comes back empty even if totalCount says otherwise' {
        InModuleScope JIM {
            $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
            Mock Invoke-JIMApi {
                if ($Endpoint -like '*page=1*') {
                    [PSCustomObject]@{ items = @([PSCustomObject]@{ id = 1; name = 'Only' }); totalCount = 5 }
                }
                else {
                    [PSCustomObject]@{ items = @(); totalCount = 5 }
                }
            }

            $items = Get-JIMPagedItems -Endpoint '/api/v1/things'

            $items.Count | Should -Be 1
            Should -Invoke Invoke-JIMApi -Times 2 -Exactly
        }
    }
}

Describe 'Resolver paging' {

    BeforeEach {
        InModuleScope JIM {
            $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
        }
    }

    It 'Resolve-JIMMetaverseAttribute finds an attribute beyond the first page' {
        InModuleScope JIM {
            Mock Invoke-JIMApi {
                if ($Endpoint -like '*page=2*') {
                    [PSCustomObject]@{ items = @([PSCustomObject]@{ id = 40; name = 'Last Name' }); totalCount = 2 }
                }
                else {
                    [PSCustomObject]@{ items = @([PSCustomObject]@{ id = 1; name = 'First Name' }); totalCount = 2 }
                }
            } -ParameterFilter { $Endpoint -like '/api/v1/metaverse/attributes*' }

            $resolved = Resolve-JIMMetaverseAttribute -Name 'Last Name'

            $resolved.id | Should -Be 40
        }
    }

    It 'Resolve-JIMMetaverseObjectType finds an Object Type beyond the first page' {
        InModuleScope JIM {
            Mock Invoke-JIMApi {
                if ($Endpoint -like '*page=2*') {
                    [PSCustomObject]@{ items = @([PSCustomObject]@{ id = 2; name = 'Group' }); totalCount = 2 }
                }
                else {
                    [PSCustomObject]@{ items = @([PSCustomObject]@{ id = 1; name = 'User' }); totalCount = 2 }
                }
            } -ParameterFilter { $Endpoint -like '/api/v1/metaverse/object-types*' }

            $resolved = Resolve-JIMMetaverseObjectType -Name 'Group'

            $resolved.id | Should -Be 2
        }
    }

    It 'Resolve-JIMConnectedSystem finds a Connected System beyond the first page' {
        InModuleScope JIM {
            Mock Invoke-JIMApi {
                if ($Endpoint -like '*page=2*') {
                    [PSCustomObject]@{ items = @([PSCustomObject]@{ id = 9; name = 'Corporate LDAP' }); totalCount = 2 }
                }
                else {
                    [PSCustomObject]@{ items = @([PSCustomObject]@{ id = 1; name = 'HR CSV' }); totalCount = 2 }
                }
            } -ParameterFilter { $Endpoint -like '/api/v1/synchronisation/connected-systems*' }

            $resolved = Resolve-JIMConnectedSystem -Name 'Corporate LDAP'

            $resolved.id | Should -Be 9
        }
    }

    It 'Resolve-JIMExampleDataSet finds a data set beyond the first page' {
        InModuleScope JIM {
            Mock Invoke-JIMApi {
                if ($Endpoint -like '*page=2*') {
                    [PSCustomObject]@{ items = @([PSCustomObject]@{ id = 13; name = 'Lastnames' }); totalCount = 2 }
                }
                else {
                    [PSCustomObject]@{ items = @([PSCustomObject]@{ id = 1; name = 'Adjectives' }); totalCount = 2 }
                }
            } -ParameterFilter { $Endpoint -like '/api/v1/example-data/example-data-sets*' }

            $resolved = Resolve-JIMExampleDataSet -Name 'Lastnames'

            $resolved.id | Should -Be 13
        }
    }

    It 'Resolve-JIMExampleDataTemplate finds a template beyond the first page' {
        InModuleScope JIM {
            Mock Invoke-JIMApi {
                if ($Endpoint -like '*page=2*') {
                    [PSCustomObject]@{ items = @([PSCustomObject]@{ id = 7; name = 'Contractors' }); totalCount = 2 }
                }
                else {
                    [PSCustomObject]@{ items = @([PSCustomObject]@{ id = 1; name = 'Users & Groups' }); totalCount = 2 }
                }
            } -ParameterFilter { $Endpoint -like '/api/v1/example-data/templates*' }

            $resolved = Resolve-JIMExampleDataTemplate -Name 'Contractors'

            $resolved.id | Should -Be 7
        }
    }
}
