# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

#Requires -Modules Pester

<#
.SYNOPSIS
    Pester tests for Set-JIMMetaverseObjectPassword (#1172, #1119, #1635).

.DESCRIPTION
    One cmdlet, two target modes, and the binder is what keeps them honest. Naming Connected Systems resolves
    the person's accounts there and sends their ids; naming none propagates to every Connected System
    configured for Password Synchronisation (decision D5). The defaults the server applies per mode are not
    repeated here: the cmdlet omits what the caller did not say, so the request means what the API says a bare
    request means. -EnableAccount is refused by parameter set for a propagated change, and a named system the
    person has no account in refuses the whole request rather than quietly setting the password in the rest.
#>

BeforeAll {
    $ModulePath = Join-Path $PSScriptRoot '..' 'JIM.psd1'
    Get-Module JIM -ErrorAction SilentlyContinue | Remove-Module -Force
    Import-Module $ModulePath -Force
}

AfterAll {
    Get-Module JIM -ErrorAction SilentlyContinue | Remove-Module -Force
}

Describe 'Set-JIMMetaverseObjectPassword' {

    Context 'Parameter Validation' {

        BeforeAll {
            $command = Get-Command Set-JIMMetaverseObjectPassword
        }

        It 'Should take the password as a SecureString' {
            $command.Parameters['Password'].ParameterType | Should -Be ([securestring])
        }

        It 'Should confirm before setting a password' {
            $command.ScriptBlock.Attributes |
                Where-Object { $_ -is [System.Management.Automation.CmdletBindingAttribute] -and $_.SupportsShouldProcess -and $_.ConfirmImpact -eq 'High' } |
                Should -Not -BeNullOrEmpty
        }

        It 'Should accept the Metaverse Object from the pipeline by property name' {
            $command.Parameters['Id'].Attributes.Where({ $_ -is [System.Management.Automation.ParameterAttribute] }).ValueFromPipelineByPropertyName |
                Should -Contain $true
        }

        It 'Should no longer offer -AllAccounts' {
            # Retired (decision D5). A script wanting every account passes their systems to -ConnectedSystemId.
            $command.Parameters['AllAccounts'] | Should -BeNullOrEmpty
        }

        It 'Should default to propagating with a supplied password when no system is named' {
            # The event case needs no account selection; a bare -Password must bind without prompting.
            $command.DefaultParameterSet | Should -Be 'PropagateSuppliedPassword'
        }

        It 'Should offer -EnableAccount only where systems are named' {
            $sets = $command.Parameters['EnableAccount'].ParameterSets.Keys
            $sets | Should -Contain 'NamedSuppliedPassword'
            $sets | Should -Contain 'NamedGeneratedPassword'
            $sets | Should -Not -Contain 'PropagateSuppliedPassword'
            $sets | Should -Not -Contain 'PropagateGeneratedPassword'
        }

        It 'Should refuse -EnableAccount without -ConnectedSystemId by binding' {
            # A propagated password reaches accounts an administrator may have disabled on purpose. The binder,
            # not a runtime check, is what refuses this: the only sets carrying -EnableAccount require systems.
            InModuleScope JIM {
                $script:JIMConnection = @{ Url = 'https://jim.example.test' }
                Mock Invoke-JIMApi { throw 'the API must not be called' }

                { Set-JIMMetaverseObjectPassword -Id ([guid]::NewGuid()) -Password (ConvertTo-SecureString 'Correct-Horse-42' -AsPlainText -Force) -EnableAccount -Force -ErrorAction Stop } |
                    Should -Throw -ErrorId 'MissingMandatoryParameter*'
                Should -Invoke Invoke-JIMApi -Times 0
            }
        }

        It 'Should take -Wait as a number of seconds between 0 and 30' {
            $param = $command.Parameters['Wait']
            $param | Should -Not -BeNullOrEmpty
            $param.ParameterType | Should -Be ([int])
            $range = $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ValidateRangeAttribute] }
            $range.MinRange | Should -Be 0
            $range.MaxRange | Should -Be 30
        }

        It 'Should refuse a wait longer than the API allows' {
            { Set-JIMMetaverseObjectPassword -Id ([guid]::NewGuid()) -Password (ConvertTo-SecureString 'Correct-Horse-42' -AsPlainText -Force) -Wait 31 -Force -ErrorAction Stop } |
                Should -Throw -ErrorId 'ParameterArgumentValidationError*'
        }

        It 'Should offer only expiry behaviours JIM understands' {
            $validateSet = $command.Parameters['ExpiryBehaviour'].Attributes |
                Where-Object { $_ -is [System.Management.Automation.ValidateSetAttribute] }
            $validateSet.ValidValues | Should -Be @('RequireChangeAtNextSignIn', 'ExpiresAccordingToTargetPolicy', 'NeverExpires')
        }
    }

    Context 'Propagating to every configured system' {

        It 'Should error rather than call the API when not connected' {
            InModuleScope JIM {
                $script:JIMConnection = $null
                Mock Invoke-JIMApi { throw 'the API must not be called' }

                { Set-JIMMetaverseObjectPassword -Id ([guid]::NewGuid()) `
                        -Password (ConvertTo-SecureString 'Correct-Horse-42' -AsPlainText -Force) -Force -ErrorAction Stop } |
                    Should -Throw -ExpectedMessage '*not connected to JIM*'
                Should -Invoke Invoke-JIMApi -Times 0
            }
        }

        It 'Should post the password with no account list and nothing else it was not told' {
            # The server's per-mode defaults (expiry left to each system, return on enqueue) are the contract for
            # a bare request; sending them explicitly would pin them into every script written against this
            # version. And the person's accounts are not read at all: the server resolves its own targets.
            InModuleScope JIM {
                $script:JIMConnection = @{ Url = 'https://jim.example.test' }
                Mock Invoke-JIMApi -MockWith { [PSCustomObject]@{ ActivityId = [guid]::NewGuid(); Origin = 'Propagated'; Settled = $false; Targets = @() } }

                Set-JIMMetaverseObjectPassword -Id ([guid]'8f1c2d3e-4a5b-6c7d-8e9f-0a1b2c3d4e5f') `
                    -Password (ConvertTo-SecureString 'Correct-Horse-42' -AsPlainText -Force) -Force | Out-Null

                Should -Invoke Invoke-JIMApi -Times 1 -ParameterFilter {
                    $Method -eq 'POST' -and
                    $Endpoint -eq '/api/v1/metaverse/objects/8f1c2d3e-4a5b-6c7d-8e9f-0a1b2c3d4e5f/password' -and
                    $Body.password -eq 'Correct-Horse-42' -and
                    -not $Body.ContainsKey('connectedSystemObjectIds') -and
                    -not $Body.ContainsKey('wait') -and
                    -not $Body.ContainsKey('expiryBehaviour') -and
                    -not $Body.ContainsKey('enableAccount')
                }
                Should -Invoke Invoke-JIMApi -Times 0 -ParameterFilter { $Method -eq 'GET' }
            }
        }

        It 'Should send the wait when -Wait is given' {
            InModuleScope JIM {
                $script:JIMConnection = @{ Url = 'https://jim.example.test' }
                Mock Invoke-JIMApi -MockWith { [PSCustomObject]@{ ActivityId = [guid]::NewGuid(); Origin = 'Propagated'; Settled = $true; Targets = @() } }

                Set-JIMMetaverseObjectPassword -Id ([guid]'8f1c2d3e-4a5b-6c7d-8e9f-0a1b2c3d4e5f') `
                    -Password (ConvertTo-SecureString 'Correct-Horse-42' -AsPlainText -Force) -Wait 10 -Force | Out-Null

                Should -Invoke Invoke-JIMApi -Times 1 -ParameterFilter { $Method -eq 'POST' -and $Body.wait -eq 10 }
            }
        }

        It 'Should send the chosen expiry behaviour' {
            InModuleScope JIM {
                $script:JIMConnection = @{ Url = 'https://jim.example.test' }
                Mock Invoke-JIMApi -MockWith { [PSCustomObject]@{ ActivityId = [guid]::NewGuid(); Origin = 'Propagated'; Settled = $false; Targets = @() } }

                Set-JIMMetaverseObjectPassword -Id ([guid]'8f1c2d3e-4a5b-6c7d-8e9f-0a1b2c3d4e5f') `
                    -Password (ConvertTo-SecureString 'Correct-Horse-42' -AsPlainText -Force) -ExpiryBehaviour NeverExpires -Force | Out-Null

                Should -Invoke Invoke-JIMApi -Times 1 -ParameterFilter { $Method -eq 'POST' -and $Body.expiryBehaviour -eq 'NeverExpires' }
            }
        }

        It 'Should generate against every system the person has an account in when -Generate is given' {
            InModuleScope JIM {
                $script:JIMConnection = @{ Url = 'https://jim.example.test' }
                Mock Invoke-JIMApi -ParameterFilter { $Method -eq 'GET' } -MockWith {
                    @{ ConnectedSystemObjects = @(
                        @{ Id = [guid]'11111111-1111-1111-1111-111111111111'; ConnectedSystemId = 1; ConnectedSystemName = 'Contoso AD' },
                        @{ Id = [guid]'22222222-2222-2222-2222-222222222222'; ConnectedSystemId = 2; ConnectedSystemName = 'Fabrikam HR' }) }
                }
                Mock Invoke-JIMApi -ParameterFilter { $Endpoint -like '*/generate-password' } -MockWith {
                    @{ password = 'Generated-Horse-99'; systemsWithNoDiscoveredPolicy = @() }
                }
                Mock Invoke-JIMApi -ParameterFilter { $Endpoint -like '*/objects/*/password' } -MockWith {
                    [PSCustomObject]@{ ActivityId = [guid]::NewGuid(); Origin = 'Propagated'; Settled = $false; Targets = @() }
                }

                $result = Set-JIMMetaverseObjectPassword -Id ([guid]'8f1c2d3e-4a5b-6c7d-8e9f-0a1b2c3d4e5f') -Generate -Force

                Should -Invoke Invoke-JIMApi -Times 1 -ParameterFilter {
                    $Endpoint -like '*/generate-password' -and @($Body.connectedSystemIds).Count -eq 2
                }
                Should -Invoke Invoke-JIMApi -Times 1 -ParameterFilter {
                    $Endpoint -like '*/objects/*/password' -and $Body.password -eq 'Generated-Horse-99' -and -not $Body.ContainsKey('connectedSystemObjectIds')
                }
                ConvertFrom-SecureString -SecureString $result.GeneratedPassword -AsPlainText | Should -Be 'Generated-Horse-99'
            }
        }
    }

    Context 'Naming Connected Systems' {

        BeforeEach {
            InModuleScope JIM {
                $script:JIMConnection = @{ Url = 'https://jim.example.test' }
                Mock Invoke-JIMApi -ParameterFilter { $Method -eq 'GET' } -MockWith {
                    @{ ConnectedSystemObjects = @(
                        @{ Id = [guid]'11111111-1111-1111-1111-111111111111'; ConnectedSystemId = 1; ConnectedSystemName = 'Contoso AD' },
                        @{ Id = [guid]'22222222-2222-2222-2222-222222222222'; ConnectedSystemId = 2; ConnectedSystemName = 'Fabrikam HR' }) }
                }
                Mock Invoke-JIMApi -ParameterFilter { $Method -eq 'POST' } -MockWith {
                    [PSCustomObject]@{ ActivityId = [guid]::NewGuid(); Origin = 'Explicit'; Settled = $true; Targets = @() }
                }
            }
        }

        It 'Should resolve the named systems to the person''s accounts there and send their ids' {
            InModuleScope JIM {
                Set-JIMMetaverseObjectPassword -Id ([guid]'8f1c2d3e-4a5b-6c7d-8e9f-0a1b2c3d4e5f') -ConnectedSystemId 2 `
                    -Password (ConvertTo-SecureString 'Correct-Horse-42' -AsPlainText -Force) -Force | Out-Null

                Should -Invoke Invoke-JIMApi -Times 1 -ParameterFilter {
                    $Method -eq 'POST' -and
                    @($Body.connectedSystemObjectIds).Count -eq 1 -and
                    $Body.connectedSystemObjectIds[0] -eq [guid]'22222222-2222-2222-2222-222222222222'
                }
            }
        }

        It 'Should send every named system''s account in one request' {
            InModuleScope JIM {
                Set-JIMMetaverseObjectPassword -Id ([guid]'8f1c2d3e-4a5b-6c7d-8e9f-0a1b2c3d4e5f') -ConnectedSystemId 1, 2 `
                    -Password (ConvertTo-SecureString 'Correct-Horse-42' -AsPlainText -Force) -Force | Out-Null

                Should -Invoke Invoke-JIMApi -Times 1 -ParameterFilter {
                    $Method -eq 'POST' -and @($Body.connectedSystemObjectIds).Count -eq 2
                }
            }
        }

        # Refused rather than quietly narrowed: a caller who named three systems and had the password set in two
        # would believe all three took it.
        It 'Should refuse the whole request when the person has no account in a named system' {
            InModuleScope JIM {
                { Set-JIMMetaverseObjectPassword -Id ([guid]'8f1c2d3e-4a5b-6c7d-8e9f-0a1b2c3d4e5f') -ConnectedSystemId 1, 99 `
                        -Password (ConvertTo-SecureString 'Correct-Horse-42' -AsPlainText -Force) -Force -ErrorAction Stop } |
                    Should -Throw -ExpectedMessage '*no account in Connected System 99*'

                Should -Invoke Invoke-JIMApi -Times 0 -ParameterFilter { $Method -eq 'POST' }
            }
        }

        It 'Should send enableAccount only when asked for' {
            # Omitted, not false. False would ask each Connected System to disable an account nobody asked it to
            # touch, which on a reset for working accounts would lock their owner out of all of them at once.
            InModuleScope JIM {
                Set-JIMMetaverseObjectPassword -Id ([guid]'8f1c2d3e-4a5b-6c7d-8e9f-0a1b2c3d4e5f') -ConnectedSystemId 1 `
                    -Password (ConvertTo-SecureString 'Correct-Horse-42' -AsPlainText -Force) -Force | Out-Null
                Set-JIMMetaverseObjectPassword -Id ([guid]'8f1c2d3e-4a5b-6c7d-8e9f-0a1b2c3d4e5f') -ConnectedSystemId 1 `
                    -Password (ConvertTo-SecureString 'Correct-Horse-42' -AsPlainText -Force) -EnableAccount -Force | Out-Null

                Should -Invoke Invoke-JIMApi -Times 1 -ParameterFilter { $Method -eq 'POST' -and -not $Body.ContainsKey('enableAccount') }
                Should -Invoke Invoke-JIMApi -Times 1 -ParameterFilter { $Method -eq 'POST' -and $Body.enableAccount -eq $true }
            }
        }

        It 'Should not send a wait when -Wait is not given, leaving the server''s ten-second default in force' {
            InModuleScope JIM {
                Set-JIMMetaverseObjectPassword -Id ([guid]'8f1c2d3e-4a5b-6c7d-8e9f-0a1b2c3d4e5f') -ConnectedSystemId 1 `
                    -Password (ConvertTo-SecureString 'Correct-Horse-42' -AsPlainText -Force) -Force | Out-Null

                Should -Invoke Invoke-JIMApi -Times 1 -ParameterFilter { $Method -eq 'POST' -and -not $Body.ContainsKey('wait') }
            }
        }

        It 'Should generate against the named systems only' {
            InModuleScope JIM {
                Mock Invoke-JIMApi -ParameterFilter { $Endpoint -like '*/generate-password' } -MockWith {
                    @{ password = 'Generated-Horse-99'; systemsWithNoDiscoveredPolicy = @('Fabrikam HR') }
                }

                $result = Set-JIMMetaverseObjectPassword -Id ([guid]'8f1c2d3e-4a5b-6c7d-8e9f-0a1b2c3d4e5f') -ConnectedSystemId 2 -Generate -Force -WarningVariable warnings -WarningAction SilentlyContinue

                Should -Invoke Invoke-JIMApi -Times 1 -ParameterFilter {
                    $Endpoint -like '*/generate-password' -and @($Body.connectedSystemIds).Count -eq 1 -and $Body.connectedSystemIds[0] -eq 2
                }
                Should -Invoke Invoke-JIMApi -Times 1 -ParameterFilter {
                    $Endpoint -like '*/objects/*/password' -and $Body.password -eq 'Generated-Horse-99'
                }
                ConvertFrom-SecureString -SecureString $result.GeneratedPassword -AsPlainText | Should -Be 'Generated-Horse-99'
                $warnings | Should -Not -BeNullOrEmpty
            }
        }

        It 'Should not call the API when WhatIf is specified' {
            InModuleScope JIM {
                Set-JIMMetaverseObjectPassword -Id ([guid]'8f1c2d3e-4a5b-6c7d-8e9f-0a1b2c3d4e5f') -ConnectedSystemId 1 `
                    -Password (ConvertTo-SecureString 'Correct-Horse-42' -AsPlainText -Force) -WhatIf | Out-Null

                Should -Invoke Invoke-JIMApi -Times 0 -ParameterFilter { $Method -eq 'POST' }
            }
        }
    }

    Context 'Output' {

        # The wire shape is what POST /api/v1/metaverse/objects/{id}/password returns once every target has
        # settled (200) or the wait ran out (202): Origin, Settled and one State per target are what a script
        # keys on. Mocked at the level Invoke-JIMApi hands back, which is already PascalCased.
        It 'Should report Origin, Settled and each target''s State when every system answered' {
            InModuleScope JIM {
                $script:JIMConnection = @{ Url = 'https://jim.example.test' }
                Mock Invoke-JIMApi -MockWith {
                    [PSCustomObject]@{
                        ActivityId         = [guid]'0a1b2c3d-4e5f-6a7b-8c9d-0e1f2a3b4c5d'
                        Origin             = 'Propagated'
                        Settled            = $true
                        QueuedForNoSystems = $false
                        Targets            = @(
                            [PSCustomObject]@{ ConnectedSystemId = 3; ConnectedSystemName = 'Corporate AD'; Enabled = $true; State = 'Set'; NextAttemptAt = $null; Message = 'Password set'; AttemptCount = 1 },
                            [PSCustomObject]@{ ConnectedSystemId = 7; ConnectedSystemName = 'Payroll'; Enabled = $false; State = 'Held'; NextAttemptAt = $null; Message = $null; AttemptCount = 0 }
                        )
                    }
                }

                $result = Set-JIMMetaverseObjectPassword -Id ([guid]'8f1c2d3e-4a5b-6c7d-8e9f-0a1b2c3d4e5f') `
                    -Password (ConvertTo-SecureString 'Correct-Horse-42' -AsPlainText -Force) -Wait 10 -Force

                $result.Origin | Should -Be 'Propagated'
                $result.Settled | Should -BeTrue
                $result.ActivityId | Should -Be ([guid]'0a1b2c3d-4e5f-6a7b-8c9d-0e1f2a3b4c5d')
                @($result.Targets).Count | Should -Be 2
                @($result.Targets | Where-Object State -eq 'Set').ConnectedSystemName | Should -Be 'Corporate AD'
                @($result.Targets | Where-Object State -eq 'Held').ConnectedSystemName | Should -Be 'Payroll'
                $result.PSObject.Properties['GeneratedPassword'] | Should -BeNullOrEmpty
            }
        }

        It 'Should report a refused target as Parked, and as an error carrying the result' {
            InModuleScope JIM {
                $script:JIMConnection = @{ Url = 'https://jim.example.test' }
                Mock Invoke-JIMApi -MockWith {
                    [PSCustomObject]@{
                        ActivityId = [guid]::NewGuid()
                        Origin     = 'Propagated'
                        Settled    = $true
                        Targets    = @(
                            [PSCustomObject]@{ ConnectedSystemId = 7; ConnectedSystemName = 'Payroll'; Enabled = $true; State = 'Parked'; NextAttemptAt = $null; Message = 'The password does not meet the length, complexity or history requirement of the domain.'; AttemptCount = 1 }
                        )
                    }
                }

                $result = Set-JIMMetaverseObjectPassword -Id ([guid]'8f1c2d3e-4a5b-6c7d-8e9f-0a1b2c3d4e5f') `
                    -Password (ConvertTo-SecureString 'Correct-Horse-42' -AsPlainText -Force) -Wait 10 -Force -ErrorVariable refusals -ErrorAction SilentlyContinue

                $parked = $result.Targets | Where-Object State -eq 'Parked'
                $parked.ConnectedSystemName | Should -Be 'Payroll'
                $parked.Message | Should -BeLike '*requirement of the domain*'
                @($refusals).Count | Should -Be 1
                $refusals[0].Exception.Message | Should -BeLike 'Payroll refused the password:*'
                $refusals[0].TargetObject.ActivityId | Should -Be $result.ActivityId
            }
        }

        It 'Should report Settled false with what is known when the wait ran out' {
            InModuleScope JIM {
                $script:JIMConnection = @{ Url = 'https://jim.example.test' }
                $nextAttempt = (Get-Date).AddMinutes(1).ToUniversalTime()
                Mock Invoke-JIMApi -MockWith {
                    [PSCustomObject]@{
                        ActivityId = [guid]::NewGuid()
                        Origin     = 'Explicit'
                        Settled    = $false
                        Targets    = @(
                            [PSCustomObject]@{ ConnectedSystemId = 3; ConnectedSystemName = 'Corporate AD'; Enabled = $true; State = 'Delivering'; NextAttemptAt = $null; Message = $null; AttemptCount = 0 },
                            [PSCustomObject]@{ ConnectedSystemId = 7; ConnectedSystemName = 'Payroll'; Enabled = $true; State = 'Retrying'; NextAttemptAt = $nextAttempt; Message = 'The directory is unavailable.'; AttemptCount = 1 }
                        )
                    }
                }

                $result = Set-JIMMetaverseObjectPassword -Id ([guid]'8f1c2d3e-4a5b-6c7d-8e9f-0a1b2c3d4e5f') `
                    -Password (ConvertTo-SecureString 'Correct-Horse-42' -AsPlainText -Force) -Wait 5 -Force -WarningVariable warnings -WarningAction SilentlyContinue

                $result.Settled | Should -BeFalse
                @($result.Targets | Where-Object State -eq 'Delivering').Count | Should -Be 1
                $retrying = $result.Targets | Where-Object State -eq 'Retrying'
                $retrying.NextAttemptAt | Should -Be $nextAttempt
                $retrying.AttemptCount | Should -Be 1
                @($warnings).Count | Should -Be 1
            }
        }
    }
}
