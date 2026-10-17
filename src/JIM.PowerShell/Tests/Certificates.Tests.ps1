# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

#Requires -Modules Pester

<#
.SYNOPSIS
    Pester tests for Certificate cmdlets.
#>

BeforeAll {
    $ModulePath = Join-Path $PSScriptRoot '..' 'JIM.psd1'
    Get-Module JIM -ErrorAction SilentlyContinue | Remove-Module -Force
    Import-Module $ModulePath -Force
}

AfterAll {
    Get-Module JIM -ErrorAction SilentlyContinue | Remove-Module -Force
}

Describe 'Get-JIMCertificate' {

    Context 'Parameter Sets' {

        BeforeAll {
            $command = Get-Command Get-JIMCertificate
        }

        It 'Should have a List parameter set as default' {
            $command.DefaultParameterSet | Should -Be 'List'
        }

        It 'Should have a ById parameter set' {
            $command.ParameterSets.Name | Should -Contain 'ById'
        }

        It 'Should have an Enabled parameter set' {
            $command.ParameterSets.Name | Should -Contain 'Enabled'
        }
    }

    Context 'Parameter Validation' {

        BeforeAll {
            $command = Get-Command Get-JIMCertificate
        }

        It 'Should have Id parameter that accepts GUID' {
            $param = $command.Parameters['Id']
            $param.ParameterType.Name | Should -Be 'Guid'
        }

        It 'Should have EnabledOnly switch parameter' {
            $command.Parameters['EnabledOnly'].SwitchParameter | Should -BeTrue
        }
    }

    Context 'Requires Connection' {

        BeforeEach {
            Disconnect-JIM
        }

        It 'Should throw when not connected' {
            { Get-JIMCertificate -ErrorAction Stop } | Should -Throw '*Connect-JIM*'
        }
    }

    Context 'List behaviour' {

        It 'Emits nothing when the store is empty (does not leak the pagination envelope)' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi { [PSCustomObject]@{ items = @(); totalCount = 0; page = 1; pageSize = 100 } }

                $result = @(Get-JIMCertificate)

                $result.Count | Should -Be 0
            }
        }

        It 'Emits each certificate from the response envelope, unwrapped' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                $cert1 = [PSCustomObject]@{ id = [guid]::NewGuid(); name = 'Contoso Root CA' }
                $cert2 = [PSCustomObject]@{ id = [guid]::NewGuid(); name = 'Fabrikam Issuing CA' }
                Mock Invoke-JIMApi { [PSCustomObject]@{ items = @($cert1, $cert2); totalCount = 2; page = 1; pageSize = 100 } }

                $result = @(Get-JIMCertificate)

                $result.Count | Should -Be 2
                $result[0].name | Should -Be 'Contoso Root CA'
                $result[1].name | Should -Be 'Fabrikam Issuing CA'
            }
        }
    }

    Context 'Help Documentation' {

        BeforeAll {
            $help = Get-Help Get-JIMCertificate -Full
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

Describe 'Add-JIMCertificate' {

    Context 'Parameter Sets' {

        BeforeAll {
            $command = Get-Command Add-JIMCertificate
        }

        It 'Should have FromFile as default parameter set' {
            $command.DefaultParameterSet | Should -Be 'FromFile'
        }

        It 'Should have FromData parameter set' {
            $command.ParameterSets.Name | Should -Contain 'FromData'
        }

        It 'Should have FromBase64 parameter set' {
            $command.ParameterSets.Name | Should -Contain 'FromBase64'
        }
    }

    Context 'Parameter Validation' {

        BeforeAll {
            $command = Get-Command Add-JIMCertificate
        }

        It 'Should have a mandatory Name parameter' {
            $param = $command.Parameters['Name']
            $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] -and $_.Mandatory } | Should -Not -BeNullOrEmpty
        }

        It 'Should have a PassThru switch parameter' {
            $command.Parameters['PassThru'].SwitchParameter | Should -BeTrue
        }

        It 'Should support ShouldProcess' {
            $command.Parameters['WhatIf'] | Should -Not -BeNullOrEmpty
        }
    }

    Context 'Requires Connection' {

        BeforeEach {
            Disconnect-JIM
        }

        It 'Should throw when not connected' {
            { Add-JIMCertificate -Name "Test" -Path "/test.pem" -ErrorAction Stop } | Should -Throw '*Connect-JIM*'
        }
    }

    Context 'Help Documentation' {

        BeforeAll {
            $help = Get-Help Add-JIMCertificate -Full
        }

        It 'Should have a synopsis' {
            $help.Synopsis | Should -Not -BeNullOrEmpty
        }

        It 'Should have examples' {
            $help.Examples.Example.Count | Should -BeGreaterThan 0
        }
    }
}

Describe 'Set-JIMCertificate' {

    Context 'Parameter Validation' {

        BeforeAll {
            $command = Get-Command Set-JIMCertificate
        }

        It 'Should have a mandatory Id parameter' {
            $param = $command.Parameters['Id']
            $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] -and $_.Mandatory } | Should -Not -BeNullOrEmpty
        }

        It 'Should have Enable switch parameter' {
            $command.Parameters['Enable'].SwitchParameter | Should -BeTrue
        }

        It 'Should have Disable switch parameter' {
            $command.Parameters['Disable'].SwitchParameter | Should -BeTrue
        }

        It 'Should have a PassThru switch parameter' {
            $command.Parameters['PassThru'].SwitchParameter | Should -BeTrue
        }

        It 'Should support ShouldProcess' {
            $command.Parameters['WhatIf'] | Should -Not -BeNullOrEmpty
        }
    }

    Context 'Requires Connection' {

        BeforeEach {
            Disconnect-JIM
        }

        It 'Should throw when not connected' {
            { Set-JIMCertificate -Id ([Guid]::NewGuid()) -Name "Test" -ErrorAction Stop } | Should -Throw '*Connect-JIM*'
        }
    }

    Context 'Help Documentation' {

        BeforeAll {
            $help = Get-Help Set-JIMCertificate -Full
        }

        It 'Should have a synopsis' {
            $help.Synopsis | Should -Not -BeNullOrEmpty
        }

        It 'Should have examples' {
            $help.Examples.Example.Count | Should -BeGreaterThan 0
        }
    }
}

Describe 'Remove-JIMCertificate' {

    Context 'Parameter Validation' {

        BeforeAll {
            $command = Get-Command Remove-JIMCertificate
        }

        It 'Should support ShouldProcess' {
            $command.Parameters['WhatIf'] | Should -Not -BeNullOrEmpty
            $command.Parameters['Confirm'] | Should -Not -BeNullOrEmpty
        }

        It 'Should have a Force switch parameter' {
            $command.Parameters['Force'].SwitchParameter | Should -BeTrue
        }

        It 'Should have a PassThru switch parameter' {
            $command.Parameters['PassThru'].SwitchParameter | Should -BeTrue
        }
    }

    Context 'Requires Connection' {

        BeforeEach {
            Disconnect-JIM
        }

        It 'Should throw when not connected' {
            { Remove-JIMCertificate -Id ([Guid]::NewGuid()) -Force -ErrorAction Stop } | Should -Throw '*Connect-JIM*'
        }
    }

    Context 'Help Documentation' {

        BeforeAll {
            $help = Get-Help Remove-JIMCertificate -Full
        }

        It 'Should have a synopsis' {
            $help.Synopsis | Should -Not -BeNullOrEmpty
        }

        It 'Should have examples' {
            $help.Examples.Example.Count | Should -BeGreaterThan 0
        }
    }
}

Describe 'Test-JIMCertificate' {

    Context 'Parameter Validation' {

        BeforeAll {
            $command = Get-Command Test-JIMCertificate
        }

        It 'Should have a mandatory Id parameter' {
            $param = $command.Parameters['Id']
            $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] -and $_.Mandatory } | Should -Not -BeNullOrEmpty
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
            { Test-JIMCertificate -Id ([Guid]::NewGuid()) -ErrorAction Stop } | Should -Throw '*Connect-JIM*'
        }
    }

    Context 'Help Documentation' {

        BeforeAll {
            $help = Get-Help Test-JIMCertificate -Full
        }

        It 'Should have a synopsis' {
            $help.Synopsis | Should -Not -BeNullOrEmpty
        }

        It 'Should have examples' {
            $help.Examples.Example.Count | Should -BeGreaterThan 0
        }
    }
}

Describe 'Export-JIMCertificate' {

    Context 'Parameter Sets' {

        BeforeAll {
            $command = Get-Command Export-JIMCertificate
        }

        It 'Should have ToFile as default parameter set' {
            $command.DefaultParameterSet | Should -Be 'ToFile'
        }

        It 'Should have PassThru parameter set' {
            $command.ParameterSets.Name | Should -Contain 'PassThru'
        }
    }

    Context 'Parameter Validation' {

        BeforeAll {
            $command = Get-Command Export-JIMCertificate
        }

        It 'Should have a mandatory Id parameter' {
            $param = $command.Parameters['Id']
            $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] -and $_.Mandatory } | Should -Not -BeNullOrEmpty
        }

        It 'Should have Path parameter' {
            $command.Parameters['Path'] | Should -Not -BeNullOrEmpty
        }

        It 'Should have Force switch parameter' {
            $command.Parameters['Force'].SwitchParameter | Should -BeTrue
        }

        It 'Should have PassThru switch parameter' {
            $command.Parameters['PassThru'].SwitchParameter | Should -BeTrue
        }
    }

    Context 'Requires Connection' {

        BeforeEach {
            Disconnect-JIM
        }

        It 'Should write error when not connected' {
            { Export-JIMCertificate -Id ([Guid]::NewGuid()) -PassThru -ErrorAction Stop } | Should -Throw '*Connect-JIM*'
        }
    }

    Context 'Authentication' {

        AfterEach {
            InModuleScope JIM { $script:JIMConnection = $null }
        }

        It 'Should send an API key connection as a Bearer token, never as X-API-Key' {
            InModuleScope JIM {
                $script:JIMConnection = @{
                    Url        = 'https://jim.example.com'
                    AuthMethod = 'ApiKey'
                    ApiKey     = 'jim_ak_certificate_export_key'
                    Connected  = $true
                }
                Mock Invoke-WebRequest { [PSCustomObject]@{ Content = [byte[]](1, 2, 3) } }

                Export-JIMCertificate -Id ([Guid]::NewGuid()) -PassThru | Out-Null

                Should -Invoke Invoke-WebRequest -Times 1 -Exactly -ParameterFilter {
                    $Headers['Authorization'] -eq 'Bearer jim_ak_certificate_export_key' -and
                    -not $Headers.ContainsKey('X-API-Key')
                }
            }
        }

        It 'Should refresh an SSO access token that is about to expire before downloading' {
            InModuleScope JIM {
                $script:JIMConnection = @{
                    Url            = 'https://jim.example.com'
                    AuthMethod     = 'OAuth'
                    ApiKey         = $null
                    AccessToken    = 'eyJhbGciOiJSUzI1NiJ9.expiring.token'
                    TokenExpiresAt = (Get-Date).AddMinutes(1)
                    Connected      = $true
                }
                Mock Invoke-TokenRefresh { $script:JIMConnection.AccessToken = 'eyJhbGciOiJSUzI1NiJ9.refreshed.token' }
                Mock Invoke-WebRequest { [PSCustomObject]@{ Content = [byte[]](1, 2, 3) } }

                Export-JIMCertificate -Id ([Guid]::NewGuid()) -PassThru | Out-Null

                Should -Invoke Invoke-TokenRefresh -Times 1 -Exactly
                Should -Invoke Invoke-WebRequest -Times 1 -Exactly -ParameterFilter {
                    $Headers['Authorization'] -eq 'Bearer eyJhbGciOiJSUzI1NiJ9.refreshed.token'
                }
            }
        }

        It 'Should not refresh an SSO access token that is still valid' {
            InModuleScope JIM {
                $script:JIMConnection = @{
                    Url            = 'https://jim.example.com'
                    AuthMethod     = 'OAuth'
                    ApiKey         = $null
                    AccessToken    = 'eyJhbGciOiJSUzI1NiJ9.access.token'
                    TokenExpiresAt = (Get-Date).AddHours(1)
                    Connected      = $true
                }
                Mock Invoke-TokenRefresh { }
                Mock Invoke-WebRequest { [PSCustomObject]@{ Content = [byte[]](1, 2, 3) } }

                Export-JIMCertificate -Id ([Guid]::NewGuid()) -PassThru | Out-Null

                Should -Invoke Invoke-TokenRefresh -Times 0 -Exactly
            }
        }

        It 'Should report a failed refresh rather than send an expired token' {
            InModuleScope JIM {
                $script:JIMConnection = @{
                    Url            = 'https://jim.example.com'
                    AuthMethod     = 'OAuth'
                    ApiKey         = $null
                    AccessToken    = 'eyJhbGciOiJSUzI1NiJ9.expired.token'
                    TokenExpiresAt = (Get-Date).AddMinutes(-5)
                    Connected      = $true
                }
                Mock Invoke-TokenRefresh { throw 'Access token expired and no refresh token available. Please run Connect-JIM again to re-authenticate.' }
                Mock Invoke-WebRequest { [PSCustomObject]@{ Content = [byte[]](1, 2, 3) } }

                { Export-JIMCertificate -Id ([Guid]::NewGuid()) -PassThru -ErrorAction Stop } | Should -Throw '*Connect-JIM again*'
                Should -Invoke Invoke-WebRequest -Times 0 -Exactly
            }
        }

        It 'Should report the API error message, not its JSON body, when the certificate cannot be found' {
            InModuleScope JIM {
                $script:JIMConnection = @{
                    Url        = 'https://jim.example.com'
                    AuthMethod = 'ApiKey'
                    ApiKey     = 'jim_ak_certificate_export_key'
                    Connected  = $true
                }
                Mock Invoke-WebRequest {
                    $record = [System.Management.Automation.ErrorRecord]::new(
                        [System.Exception]::new('Response status code does not indicate success: 404 (Not Found).'),
                        'WebCmdletWebResponseException', 'InvalidOperation', $null)
                    $record.ErrorDetails = [System.Management.Automation.ErrorDetails]::new('{"code":"NOT_FOUND","message":"Certificate with ID 42 not found."}')
                    throw $record
                }

                { Export-JIMCertificate -Id ([Guid]::NewGuid()) -PassThru -ErrorAction Stop } |
                    Should -Throw 'Failed to export certificate: Certificate with ID 42 not found.'
            }
        }

        It 'Should send an SSO connection access token as a Bearer token' {
            InModuleScope JIM {
                $script:JIMConnection = @{
                    Url            = 'https://jim.example.com'
                    AuthMethod     = 'OAuth'
                    ApiKey         = $null
                    AccessToken    = 'eyJhbGciOiJSUzI1NiJ9.access.token'
                    TokenExpiresAt = (Get-Date).AddHours(1)
                    Connected      = $true
                }
                Mock Invoke-WebRequest { [PSCustomObject]@{ Content = [byte[]](1, 2, 3) } }

                Export-JIMCertificate -Id ([Guid]::NewGuid()) -PassThru | Out-Null

                Should -Invoke Invoke-WebRequest -Times 1 -Exactly -ParameterFilter {
                    $Headers['Authorization'] -eq 'Bearer eyJhbGciOiJSUzI1NiJ9.access.token'
                }
            }
        }
    }

    Context 'Help Documentation' {

        BeforeAll {
            $help = Get-Help Export-JIMCertificate -Full
        }

        It 'Should have a synopsis' {
            $help.Synopsis | Should -Not -BeNullOrEmpty
        }

        It 'Should have examples' {
            $help.Examples.Example.Count | Should -BeGreaterThan 0
        }
    }
}
