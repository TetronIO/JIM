# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

#Requires -Modules Pester

<#
.SYNOPSIS
    Pester tests for Set-JIMConnectedSystemObjectPassword (#1121, #1635).

.DESCRIPTION
    The rules worth guarding are the ones that decide what reaches the Connected System and what comes back:
    the password must be taken as a SecureString and unwrapped only to be sent, an omitted -EnableAccount must
    not ask the target to change an account's enabled state, a password reset must confirm before it happens,
    and the outcome is a state on the target (the same shape Set-JIMMetaverseObjectPassword returns) rather than
    a thrown error, with a refusal surfaced as an error as well so a script that stops on errors stops on it.
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

        It 'Should take -Wait as a number of seconds between 0 and 30' {
            $param = $command.Parameters['Wait']
            $param | Should -Not -BeNullOrEmpty
            $param.ParameterType | Should -Be ([int])
            $range = $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ValidateRangeAttribute] }
            $range.MinRange | Should -Be 0
            $range.MaxRange | Should -Be 30
        }

        It 'Should no longer offer -PassThru, because the outcome is always returned' {
            # The outcome is the point of the call now that a refusal is a Parked target rather than a thrown
            # error; a switch to withhold it would hide the one thing a script has to check.
            $command.Parameters['PassThru'] | Should -BeNullOrEmpty
        }
    }

    Context 'Behaviour' {

        BeforeEach {
            InModuleScope JIM {
                $script:JIMConnection = @{ Url = 'https://jim.example.test' }
                Mock Invoke-JIMApi {
                    [PSCustomObject]@{
                        ActivityId = [guid]::NewGuid()
                        Origin     = 'Explicit'
                        Settled    = $true
                        Targets    = @([PSCustomObject]@{ ConnectedSystemId = 1; ConnectedSystemName = 'Contoso AD'; Enabled = $true; State = 'Set'; NextAttemptAt = $null; Message = 'Password set'; AttemptCount = 1 })
                    }
                }
            }
        }

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
                $objectId = [guid]'3f2a91c4-5b6d-4e7f-8a90-1b2c3d4e5f60'

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
                Set-JIMConnectedSystemObjectPassword -ConnectedSystemId 1 -Id ([guid]::NewGuid()) `
                    -Password (ConvertTo-SecureString 'Correct-Horse-42' -AsPlainText -Force) -Force | Out-Null

                Should -Invoke Invoke-JIMApi -Times 1 -ParameterFilter { -not $Body.ContainsKey('enableAccount') }
            }
        }

        It 'Should send enableAccount when it was asked for' {
            InModuleScope JIM {
                Set-JIMConnectedSystemObjectPassword -ConnectedSystemId 1 -Id ([guid]::NewGuid()) `
                    -Password (ConvertTo-SecureString 'Correct-Horse-42' -AsPlainText -Force) -EnableAccount -Force | Out-Null

                Should -Invoke Invoke-JIMApi -Times 1 -ParameterFilter { $Body.enableAccount -eq $true }
            }
        }

        # Omitted means "whatever the API's default is", which is documented as requiring a change at the next
        # sign-in and waiting ten seconds. Sending a value the caller never chose would silently override a
        # future change to either.
        It 'Should not send an expiry behaviour or a wait that was not asked for' {
            InModuleScope JIM {
                Set-JIMConnectedSystemObjectPassword -ConnectedSystemId 1 -Id ([guid]::NewGuid()) `
                    -Password (ConvertTo-SecureString 'Correct-Horse-42' -AsPlainText -Force) -Force | Out-Null

                Should -Invoke Invoke-JIMApi -Times 1 -ParameterFilter {
                    -not $Body.ContainsKey('expiryBehaviour') -and -not $Body.ContainsKey('wait')
                }
            }
        }

        It 'Should send the chosen expiry behaviour and wait' {
            InModuleScope JIM {
                Set-JIMConnectedSystemObjectPassword -ConnectedSystemId 1 -Id ([guid]::NewGuid()) `
                    -Password (ConvertTo-SecureString 'Correct-Horse-42' -AsPlainText -Force) -ExpiryBehaviour NeverExpires -Wait 0 -Force | Out-Null

                Should -Invoke Invoke-JIMApi -Times 1 -ParameterFilter {
                    $Body.expiryBehaviour -eq 'NeverExpires' -and $Body.wait -eq 0
                }
            }
        }

        It 'Should refuse a whitespace-only password without calling the API' {
            InModuleScope JIM {
                Mock Invoke-JIMApi { throw 'the API must not be called' }

                { Set-JIMConnectedSystemObjectPassword -ConnectedSystemId 1 -Id ([guid]::NewGuid()) `
                        -Password (ConvertTo-SecureString '   ' -AsPlainText -Force) -Force -ErrorAction Stop } | Should -Throw
                Should -Invoke Invoke-JIMApi -Times 0
            }
        }

        It 'Should bind a piped Connected System Object' {
            InModuleScope JIM {
                $objectId = [guid]'3f2a91c4-5b6d-4e7f-8a90-1b2c3d4e5f60'

                [PSCustomObject]@{ ConnectedSystemId = 7; Id = $objectId } |
                    Set-JIMConnectedSystemObjectPassword -Password (ConvertTo-SecureString 'Correct-Horse-42' -AsPlainText -Force) -Force | Out-Null

                Should -Invoke Invoke-JIMApi -Times 1 -ParameterFilter {
                    $Endpoint -eq "/api/v1/synchronisation/connected-systems/7/connector-space/$objectId/password"
                }
            }
        }

        It 'Should not call the API when WhatIf is specified' {
            InModuleScope JIM {
                Mock Invoke-JIMApi { throw 'the API must not be called' }

                Set-JIMConnectedSystemObjectPassword -ConnectedSystemId 1 -Id ([guid]::NewGuid()) `
                    -Password (ConvertTo-SecureString 'Correct-Horse-42' -AsPlainText -Force) -WhatIf | Out-Null

                Should -Invoke Invoke-JIMApi -Times 0
            }
        }
    }

    Context 'Output' {

        It 'Should always return the outcome, with the target''s State' {
            InModuleScope JIM {
                $script:JIMConnection = @{ Url = 'https://jim.example.test' }
                Mock Invoke-JIMApi {
                    [PSCustomObject]@{
                        ActivityId = [guid]'0a1b2c3d-4e5f-6a7b-8c9d-0e1f2a3b4c5d'
                        Origin     = 'Explicit'
                        Settled    = $true
                        Targets    = @([PSCustomObject]@{ ConnectedSystemId = 1; ConnectedSystemName = 'Contoso AD'; Enabled = $true; State = 'Set'; NextAttemptAt = $null; Message = 'Password set'; AttemptCount = 1 })
                    }
                }

                $result = Set-JIMConnectedSystemObjectPassword -ConnectedSystemId 1 -Id ([guid]::NewGuid()) `
                    -Password (ConvertTo-SecureString 'Correct-Horse-42' -AsPlainText -Force) -Force

                $result.ActivityId | Should -Be ([guid]'0a1b2c3d-4e5f-6a7b-8c9d-0e1f2a3b4c5d')
                $result.Origin | Should -Be 'Explicit'
                $result.Settled | Should -BeTrue
                @($result.Targets).Count | Should -Be 1
                $result.Targets[0].State | Should -Be 'Set'
                $result.PSObject.Properties['GeneratedPassword'] | Should -BeNullOrEmpty
            }
        }

        It 'Should carry a generated password on the result, once' {
            InModuleScope JIM {
                $script:JIMConnection = @{ Url = 'https://jim.example.test' }
                Mock Invoke-JIMApi -ParameterFilter { $Endpoint -like '*/generate-password' } -MockWith { @{ password = 'Generated-Horse-99' } }
                Mock Invoke-JIMApi -ParameterFilter { $Endpoint -like '*/connector-space/*/password' } -MockWith {
                    [PSCustomObject]@{ ActivityId = [guid]::NewGuid(); Origin = 'Explicit'; Settled = $true; Targets = @([PSCustomObject]@{ ConnectedSystemId = 3; ConnectedSystemName = 'Contoso AD'; Enabled = $true; State = 'Set'; NextAttemptAt = $null; Message = 'Password set'; AttemptCount = 1 }) }
                }

                $result = Set-JIMConnectedSystemObjectPassword -ConnectedSystemId 3 -Id ([guid]::NewGuid()) -Generate -Force

                Should -Invoke Invoke-JIMApi -Times 1 -ParameterFilter { $Endpoint -like '*/connector-space/*/password' -and $Body.password -eq 'Generated-Horse-99' }
                ConvertFrom-SecureString -SecureString $result.GeneratedPassword -AsPlainText | Should -Be 'Generated-Horse-99'
            }
        }

        It 'Should report a refusal as a Parked target and as an error carrying the result' {
            InModuleScope JIM {
                $script:JIMConnection = @{ Url = 'https://jim.example.test' }
                Mock Invoke-JIMApi {
                    [PSCustomObject]@{
                        ActivityId = [guid]::NewGuid()
                        Origin     = 'Explicit'
                        Settled    = $true
                        Targets    = @([PSCustomObject]@{ ConnectedSystemId = 1; ConnectedSystemName = 'Contoso AD'; Enabled = $true; State = 'Parked'; NextAttemptAt = $null; Message = 'History requirements of the domain.'; AttemptCount = 1 })
                    }
                }

                $result = Set-JIMConnectedSystemObjectPassword -ConnectedSystemId 1 -Id ([guid]::NewGuid()) `
                    -Password (ConvertTo-SecureString 'Correct-Horse-42' -AsPlainText -Force) -Force -ErrorVariable refusals -ErrorAction SilentlyContinue

                $result.Targets[0].State | Should -Be 'Parked'
                @($refusals).Count | Should -Be 1
                $refusals[0].Exception.Message | Should -BeLike '*History requirements of the domain*'
                $refusals[0].TargetObject.ActivityId | Should -Be $result.ActivityId
            }
        }

        It 'Should warn, not error, when the wait ran out with the account still in flight' {
            InModuleScope JIM {
                $script:JIMConnection = @{ Url = 'https://jim.example.test' }
                Mock Invoke-JIMApi {
                    [PSCustomObject]@{
                        ActivityId = [guid]::NewGuid()
                        Origin     = 'Explicit'
                        Settled    = $false
                        Targets    = @([PSCustomObject]@{ ConnectedSystemId = 1; ConnectedSystemName = 'Contoso AD'; Enabled = $true; State = 'Delivering'; NextAttemptAt = $null; Message = $null; AttemptCount = 0 })
                    }
                }

                $result = Set-JIMConnectedSystemObjectPassword -ConnectedSystemId 1 -Id ([guid]::NewGuid()) `
                    -Password (ConvertTo-SecureString 'Correct-Horse-42' -AsPlainText -Force) -Force -WarningVariable warnings -WarningAction SilentlyContinue -ErrorVariable errors -ErrorAction SilentlyContinue

                $result.Settled | Should -BeFalse
                $result.Targets[0].State | Should -Be 'Delivering'
                @($warnings).Count | Should -Be 1
                @($errors).Count | Should -Be 0
            }
        }
    }
}
