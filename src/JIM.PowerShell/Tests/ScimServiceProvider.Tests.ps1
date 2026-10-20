# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

#Requires -Modules Pester

<#
.SYNOPSIS
    Pester tests for the SCIM 2.0 Service Provider cmdlets: a Connected System's SCIM keys, its store summary, and
    the resource types and schemas it serves.
#>

BeforeAll {
    $ModulePath = Join-Path $PSScriptRoot '..' 'JIM.psd1'
    Get-Module JIM -ErrorAction SilentlyContinue | Remove-Module -Force
    Import-Module $ModulePath -Force
}

AfterAll {
    Get-Module JIM -ErrorAction SilentlyContinue | Remove-Module -Force
}

Describe 'SCIM cmdlets require a connection' {

    BeforeEach {
        Disconnect-JIM
    }

    It '<Command> throws when not connected' -ForEach @(
        @{ Command = 'Get-JIMScimKey'; Arguments = @{ ConnectedSystemId = 7 } }
        @{ Command = 'New-JIMScimKey'; Arguments = @{ ConnectedSystemId = 7; Name = 'Entra' } }
        @{ Command = 'Set-JIMScimKey'; Arguments = @{ ConnectedSystemId = 7; Id = [guid]::NewGuid(); Disable = $true } }
        @{ Command = 'Remove-JIMScimKey'; Arguments = @{ ConnectedSystemId = 7; Id = [guid]::NewGuid(); Force = $true } }
        @{ Command = 'Get-JIMScimStoreSummary'; Arguments = @{ ConnectedSystemId = 7 } }
        @{ Command = 'Get-JIMScimResourceType'; Arguments = @{ ConnectedSystemId = 7 } }
        @{ Command = 'Get-JIMScimSchema'; Arguments = @{ ConnectedSystemId = 7 } }
    ) {
        { & $Command @Arguments -ErrorAction Stop } | Should -Throw '*Connect-JIM*'
    }
}

Describe 'Get-JIMScimKey' {

    It 'Lists the Connected System''s keys' {
        InModuleScope JIM {
            $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
            Mock Invoke-JIMApi { @([PSCustomObject]@{ name = 'Entra 2025' }, [PSCustomObject]@{ name = 'Entra 2026' }) }

            $keys = @(Get-JIMScimKey -ConnectedSystemId 7)

            $keys.Count | Should -Be 2
            Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                $Endpoint -eq '/api/v1/synchronisation/connected-systems/7/scim/keys'
            }
        }
    }

    It 'Filters by name with wildcards' {
        InModuleScope JIM {
            $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
            Mock Invoke-JIMApi { @([PSCustomObject]@{ name = 'Entra 2025' }, [PSCustomObject]@{ name = 'Okta' }) }

            $keys = @(Get-JIMScimKey -ConnectedSystemId 7 -Name 'Entra*')

            $keys.name | Should -Be @('Entra 2025')
        }
    }

    It 'Gets one key by ID' {
        InModuleScope JIM {
            $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
            $id = [guid]'11111111-2222-3333-4444-555555555555'
            Mock Invoke-JIMApi { [PSCustomObject]@{ id = $id } }

            Get-JIMScimKey -ConnectedSystemId 7 -Id $id | Out-Null

            Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                $Endpoint -eq '/api/v1/synchronisation/connected-systems/7/scim/keys/11111111-2222-3333-4444-555555555555'
            }
        }
    }
}

Describe 'New-JIMScimKey' {

    It 'Creates the key under the Connected System and returns it with its secret' {
        InModuleScope JIM {
            $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
            Mock Invoke-JIMApi { [PSCustomObject]@{ id = [guid]::NewGuid(); key = 'jim_ak_secret' } }

            $created = New-JIMScimKey -ConnectedSystemId 7 -Name 'Entra 2026' -Description 'Provisioning'

            $created.key | Should -Be 'jim_ak_secret'
            Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                $Endpoint -eq '/api/v1/synchronisation/connected-systems/7/scim/keys' -and $Method -eq 'POST' -and
                $Body.name -eq 'Entra 2026' -and $Body.description -eq 'Provisioning' -and -not $Body.ContainsKey('expiresAt')
            }
        }
    }

    It 'Sends an expiry in UTC' {
        InModuleScope JIM {
            $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
            Mock Invoke-JIMApi { [PSCustomObject]@{ id = [guid]::NewGuid() } }
            $expiry = [datetime]::new(2027, 6, 1, 12, 0, 0, [DateTimeKind]::Utc)

            New-JIMScimKey -ConnectedSystemId 7 -Name 'Entra' -ExpiresAt $expiry | Out-Null

            Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                $Body.expiresAt -eq $expiry.ToString('o')
            }
        }
    }

    It 'Creates nothing with -WhatIf' {
        InModuleScope JIM {
            $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
            Mock Invoke-JIMApi { }

            New-JIMScimKey -ConnectedSystemId 7 -Name 'Entra' -WhatIf

            Should -Invoke Invoke-JIMApi -Times 0 -Exactly
        }
    }
}

Describe 'Set-JIMScimKey' {

    It 'Sends only what was given' {
        InModuleScope JIM {
            $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
            Mock Invoke-JIMApi { }
            $id = [guid]'11111111-2222-3333-4444-555555555555'

            Set-JIMScimKey -ConnectedSystemId 7 -Id $id -Disable -ChangeReason 'Rotated'

            Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                $Endpoint -eq '/api/v1/synchronisation/connected-systems/7/scim/keys/11111111-2222-3333-4444-555555555555' -and
                $Method -eq 'PUT' -and $Body.isEnabled -eq $false -and $Body.changeReason -eq 'Rotated' -and
                -not $Body.ContainsKey('name') -and -not $Body.ContainsKey('description') -and -not $Body.ContainsKey('expiresAt')
            }
        }
    }

    It 'Clears the description with <Label>' -ForEach @(
        @{ Label = '$null'; Value = $null }
        @{ Label = 'an empty string'; Value = '' }
    ) {
        InModuleScope JIM -Parameters @{ Value = $Value } {
            param($Value)
            $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
            Mock Invoke-JIMApi { }

            Set-JIMScimKey -ConnectedSystemId 7 -Id ([guid]::NewGuid()) -Description $Value

            Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                $Body.ContainsKey('description') -and $Body.description -eq ''
            }
        }
    }

    It 'Refuses -Enable with -Disable' {
        InModuleScope JIM {
            $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
            Mock Invoke-JIMApi { }

            { Set-JIMScimKey -ConnectedSystemId 7 -Id ([guid]::NewGuid()) -Enable -Disable -ErrorAction Stop } | Should -Throw

            Should -Invoke Invoke-JIMApi -Times 0 -Exactly
        }
    }

    It 'Binds a key piped from Get-JIMScimKey to its own Connected System' {
        InModuleScope JIM {
            $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
            Mock Invoke-JIMApi { }
            $key = [PSCustomObject]@{ id = [guid]'11111111-2222-3333-4444-555555555555'; scopeConnectedSystemId = 9; name = 'Entra 2025' }

            $key | Set-JIMScimKey -Disable

            Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                $Endpoint -eq '/api/v1/synchronisation/connected-systems/9/scim/keys/11111111-2222-3333-4444-555555555555'
            }
        }
    }
}

Describe 'Remove-JIMScimKey' {

    It 'Deletes the key with -Force, sending the reason' {
        InModuleScope JIM {
            $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
            Mock Invoke-JIMApi { [PSCustomObject]@{ name = 'Entra 2025' } } -ParameterFilter { $Method -ne 'DELETE' }
            Mock Invoke-JIMApi { } -ParameterFilter { $Method -eq 'DELETE' }

            Remove-JIMScimKey -ConnectedSystemId 7 -Id ([guid]'11111111-2222-3333-4444-555555555555') -ChangeReason 'Rotated out' -Force

            Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                $Method -eq 'DELETE' -and
                $Endpoint -eq '/api/v1/synchronisation/connected-systems/7/scim/keys/11111111-2222-3333-4444-555555555555?changeReason=Rotated%20out'
            }
        }
    }

    It 'Deletes nothing with -WhatIf, even with -Force' {
        InModuleScope JIM {
            $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
            Mock Invoke-JIMApi { [PSCustomObject]@{ name = 'Entra 2025' } } -ParameterFilter { $Method -ne 'DELETE' }
            Mock Invoke-JIMApi { } -ParameterFilter { $Method -eq 'DELETE' }

            Remove-JIMScimKey -ConnectedSystemId 7 -Id ([guid]::NewGuid()) -Force -WhatIf

            Should -Invoke Invoke-JIMApi -Times 0 -Exactly -ParameterFilter { $Method -eq 'DELETE' }
        }
    }

    It 'Binds a key piped from Get-JIMScimKey to its own Connected System' {
        InModuleScope JIM {
            $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
            Mock Invoke-JIMApi { [PSCustomObject]@{ name = 'Entra 2025' } } -ParameterFilter { $Method -ne 'DELETE' }
            Mock Invoke-JIMApi { } -ParameterFilter { $Method -eq 'DELETE' }
            $key = [PSCustomObject]@{ id = [guid]'11111111-2222-3333-4444-555555555555'; scopeConnectedSystemId = 9 }

            $key | Remove-JIMScimKey -Force

            Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                $Method -eq 'DELETE' -and $Endpoint -eq '/api/v1/synchronisation/connected-systems/9/scim/keys/11111111-2222-3333-4444-555555555555'
            }
        }
    }
}

Describe 'Get-JIMScimStoreSummary' {

    It 'Gets the summary, binding a Connected System piped in' {
        InModuleScope JIM {
            $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
            Mock Invoke-JIMApi { [PSCustomObject]@{ baseUrl = 'https://jim.example.com/scim/v2/7' } }

            $summary = [PSCustomObject]@{ id = 7; name = 'Entra provisioning' } | Get-JIMScimStoreSummary

            $summary.baseUrl | Should -Be 'https://jim.example.com/scim/v2/7'
            Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                $Endpoint -eq '/api/v1/synchronisation/connected-systems/7/scim/summary'
            }
        }
    }
}

Describe 'Get-JIMScimResourceType' {

    It 'Lists the resource types, filtering by name' {
        InModuleScope JIM {
            $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
            Mock Invoke-JIMApi { @([PSCustomObject]@{ name = 'User' }, [PSCustomObject]@{ name = 'Group' }) }

            $all = @(Get-JIMScimResourceType -ConnectedSystemId 7)
            $users = @(Get-JIMScimResourceType -ConnectedSystemId 7 -Name 'U*')

            $all.Count | Should -Be 2
            $users.name | Should -Be @('User')
            Should -Invoke Invoke-JIMApi -Times 2 -Exactly -ParameterFilter {
                $Endpoint -eq '/api/v1/synchronisation/connected-systems/7/scim/resource-types'
            }
        }
    }
}

Describe 'Get-JIMScimSchema' {

    It 'Lists the schemas' {
        InModuleScope JIM {
            $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
            Mock Invoke-JIMApi { @([PSCustomObject]@{ id = 'urn:ietf:params:scim:schemas:core:2.0:User' }) }

            $schemas = @([PSCustomObject]@{ id = 7 } | Get-JIMScimSchema)

            $schemas.Count | Should -Be 1
            Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                $Endpoint -eq '/api/v1/synchronisation/connected-systems/7/scim/schemas'
            }
        }
    }
}
