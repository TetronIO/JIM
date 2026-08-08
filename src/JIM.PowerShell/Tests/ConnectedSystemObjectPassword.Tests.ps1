# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

#Requires -Modules Pester

<#
.SYNOPSIS
    Pester tests for Set-JIMConnectedSystemObjectPassword (#1121).

.DESCRIPTION
    The rules worth guarding are the ones that decide what reaches the Connected System: the password must be
    taken as a SecureString and unwrapped only to be sent, an omitted -EnableAccount must not ask the target to
    change an account's enabled state, and a password reset must confirm before it happens.
#>

BeforeAll {
    $ModulePath = Join-Path $PSScriptRoot '..' 'JIM.psd1'
    Get-Module JIM -ErrorAction SilentlyContinue | Remove-Module -Force
    Import-Module $ModulePath -Force
}

AfterAll {
    Get-Module JIM -ErrorAction SilentlyContinue | Remove-Module -Force
}

Describe 'Set-JIMConnectedSystemObjectPassword' {

    Context 'Parameter Validation' {

        BeforeAll {
            $command = Get-Command Set-JIMConnectedSystemObjectPassword
        }

        It 'Should require the Connected System, the object and a password' {
            $command.Parameters['ConnectedSystemId'].Attributes.Mandatory | Should -Contain $true
            $command.Parameters['Id'].Attributes.Mandatory | Should -Contain $true
            $command.Parameters['Password'].Attributes.Mandatory | Should -Contain $true
        }

        # A plain [string] would leave the password in the session's command history and in any transcript.
        It 'Should take the password as a SecureString' {
            $command.Parameters['Password'].ParameterType | Should -Be ([securestring])
        }

        It 'Should accept a Connected System Object from the pipeline by property name' {
            foreach ($name in @('ConnectedSystemId', 'Id')) {
                $command.Parameters[$name].Attributes |
                    Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] -and $_.ValueFromPipelineByPropertyName } |
                    Should -Not -BeNullOrEmpty
            }
        }

        # Resetting somebody's password is not something to do by accident on a mistyped identifier.
        It 'Should confirm before setting a password' {
            $command.ScriptBlock.Attributes |
                Where-Object { $_ -is [System.Management.Automation.CmdletBindingAttribute] -and $_.SupportsShouldProcess -and $_.ConfirmImpact -eq 'High' } |
                Should -Not -BeNullOrEmpty
        }

        It 'Should only offer the expiry behaviours the API accepts' {
            $validateSet = $command.Parameters['ExpiryBehaviour'].Attributes |
                Where-Object { $_ -is [System.Management.Automation.ValidateSetAttribute] }
            $validateSet.ValidValues | Should -Be @('RequireChangeAtNextSignIn', 'ExpiresAccordingToTargetPolicy', 'NeverExpires')
        }
    }

    Context 'Behaviour' {

        It 'Should error rather than call the API when not connected' {
            InModuleScope JIM {
                $script:JIMConnection = $null
                Mock Invoke-JIMApi { throw 'the API must not be called' }

                { Set-JIMConnectedSystemObjectPassword -ConnectedSystemId 1 -Id ([guid]::NewGuid()) `
                        -Password (ConvertTo-SecureString 'Correct-Horse-42' -AsPlainText -Force) -Force -ErrorAction Stop } | Should -Throw
                Should -Invoke Invoke-JIMApi -Times 0
            }
        }

        It 'Should post the password to the object password endpoint' {
            InModuleScope JIM {
                $script:JIMConnection = @{ Url = 'https://jim.example.test' }
                $objectId = [guid]'3f2a91c4-5b6d-4e7f-8a90-1b2c3d4e5f60'
                Mock Invoke-JIMApi { @{ appliedExpiryBehaviour = 'RequireChangeAtNextSignIn' } }

                Set-JIMConnectedSystemObjectPassword -ConnectedSystemId 1 -Id $objectId `
                    -Password (ConvertTo-SecureString 'Correct-Horse-42' -AsPlainText -Force) -Force | Out-Null

                Should -Invoke Invoke-JIMApi -Times 1 -ParameterFilter {
                    $Endpoint -eq "/api/v1/synchronisation/connected-systems/1/connector-space/$objectId/password" -and
                    $Method -eq 'POST' -and
                    $Body.password -eq 'Correct-Horse-42'
                }
            }
        }

        # Omitted, not false. False would ask the Connected System to disable an account nobody asked it to
        # touch, which on a reset for a working account would lock its owner out.
        It 'Should not send enableAccount when it was not asked for' {
            InModuleScope JIM {
                $script:JIMConnection = @{ Url = 'https://jim.example.test' }
                Mock Invoke-JIMApi { @{} }

                Set-JIMConnectedSystemObjectPassword -ConnectedSystemId 1 -Id ([guid]::NewGuid()) `
                    -Password (ConvertTo-SecureString 'Correct-Horse-42' -AsPlainText -Force) -Force | Out-Null

                Should -Invoke Invoke-JIMApi -Times 1 -ParameterFilter {
                    -not $Body.ContainsKey('enableAccount')
                }
            }
        }

        It 'Should send enableAccount when it was asked for' {
            InModuleScope JIM {
                $script:JIMConnection = @{ Url = 'https://jim.example.test' }
                Mock Invoke-JIMApi { @{} }

                Set-JIMConnectedSystemObjectPassword -ConnectedSystemId 1 -Id ([guid]::NewGuid()) `
                    -Password (ConvertTo-SecureString 'Correct-Horse-42' -AsPlainText -Force) -EnableAccount -Force | Out-Null

                Should -Invoke Invoke-JIMApi -Times 1 -ParameterFilter {
                    $Body.enableAccount -eq $true
                }
            }
        }

        # Omitted means "whatever the API's default is", which is documented as requiring a change at the next
        # sign-in. Sending a value the caller never chose would silently override a future change to that.
        It 'Should not send an expiry behaviour that was not asked for' {
            InModuleScope JIM {
                $script:JIMConnection = @{ Url = 'https://jim.example.test' }
                Mock Invoke-JIMApi { @{} }

                Set-JIMConnectedSystemObjectPassword -ConnectedSystemId 1 -Id ([guid]::NewGuid()) `
                    -Password (ConvertTo-SecureString 'Correct-Horse-42' -AsPlainText -Force) -Force | Out-Null

                Should -Invoke Invoke-JIMApi -Times 1 -ParameterFilter {
                    -not $Body.ContainsKey('expiryBehaviour')
                }
            }
        }

        It 'Should send the chosen expiry behaviour' {
            InModuleScope JIM {
                $script:JIMConnection = @{ Url = 'https://jim.example.test' }
                Mock Invoke-JIMApi { @{} }

                Set-JIMConnectedSystemObjectPassword -ConnectedSystemId 1 -Id ([guid]::NewGuid()) `
                    -Password (ConvertTo-SecureString 'Correct-Horse-42' -AsPlainText -Force) -ExpiryBehaviour NeverExpires -Force | Out-Null

                Should -Invoke Invoke-JIMApi -Times 1 -ParameterFilter {
                    $Body.expiryBehaviour -eq 'NeverExpires'
                }
            }
        }

        It 'Should refuse a whitespace-only password without calling the API' {
            InModuleScope JIM {
                $script:JIMConnection = @{ Url = 'https://jim.example.test' }
                Mock Invoke-JIMApi { throw 'the API must not be called' }

                { Set-JIMConnectedSystemObjectPassword -ConnectedSystemId 1 -Id ([guid]::NewGuid()) `
                        -Password (ConvertTo-SecureString '   ' -AsPlainText -Force) -Force -ErrorAction Stop } | Should -Throw
                Should -Invoke Invoke-JIMApi -Times 0
            }
        }

        It 'Should bind a piped Connected System Object' {
            InModuleScope JIM {
                $script:JIMConnection = @{ Url = 'https://jim.example.test' }
                $objectId = [guid]'3f2a91c4-5b6d-4e7f-8a90-1b2c3d4e5f60'
                Mock Invoke-JIMApi { @{} }

                [PSCustomObject]@{ ConnectedSystemId = 7; Id = $objectId } |
                    Set-JIMConnectedSystemObjectPassword -Password (ConvertTo-SecureString 'Correct-Horse-42' -AsPlainText -Force) -Force | Out-Null

                Should -Invoke Invoke-JIMApi -Times 1 -ParameterFilter {
                    $Endpoint -eq "/api/v1/synchronisation/connected-systems/7/connector-space/$objectId/password"
                }
            }
        }

        It 'Should return the outcome only when PassThru is specified' {
            InModuleScope JIM {
                $script:JIMConnection = @{ Url = 'https://jim.example.test' }
                Mock Invoke-JIMApi { @{ appliedExpiryBehaviour = 'ExpiresAccordingToTargetPolicy' } }

                $password = ConvertTo-SecureString 'Correct-Horse-42' -AsPlainText -Force
                $withoutPassThru = Set-JIMConnectedSystemObjectPassword -ConnectedSystemId 1 -Id ([guid]::NewGuid()) -Password $password -Force
                $withPassThru = Set-JIMConnectedSystemObjectPassword -ConnectedSystemId 1 -Id ([guid]::NewGuid()) -Password $password -Force -PassThru

                $withoutPassThru | Should -BeNullOrEmpty
                $withPassThru.appliedExpiryBehaviour | Should -Be 'ExpiresAccordingToTargetPolicy'
            }
        }

        It 'Should not call the API when WhatIf is specified' {
            InModuleScope JIM {
                $script:JIMConnection = @{ Url = 'https://jim.example.test' }
                Mock Invoke-JIMApi { throw 'the API must not be called' }

                Set-JIMConnectedSystemObjectPassword -ConnectedSystemId 1 -Id ([guid]::NewGuid()) `
                    -Password (ConvertTo-SecureString 'Correct-Horse-42' -AsPlainText -Force) -WhatIf | Out-Null

                Should -Invoke Invoke-JIMApi -Times 0
            }
        }
    }
}
