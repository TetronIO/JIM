# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

#Requires -Modules Pester

<#
.SYNOPSIS
    Pester tests for Sync-JIMMetaverseObjectPassword.

.DESCRIPTION
    A synchronised password change has to be scriptable (#1119, requirement 31): the usual caller is a
    self-service portal or a service desk tool telling JIM that somebody's password has changed, and neither
    of those clicks a dialog.

    Deliberately a separate cmdlet from Set-JIMMetaverseObjectPassword, which sets a password you choose on
    whichever accounts you name, immediately, and reports whether each target accepted it. This one records
    that the person's password changed and lets delivery happen on its own clock. Collapsing the two would mean one
    cmdlet whose -AllAccounts and "synchronise" behaviours differ in retry semantics, target selection and
    what the return value means.
#>

BeforeAll {
    $ModulePath = Join-Path $PSScriptRoot '..' 'JIM.psd1'
    Get-Module JIM -ErrorAction SilentlyContinue | Remove-Module -Force
    Import-Module $ModulePath -Force
}

AfterAll {
    Get-Module JIM -ErrorAction SilentlyContinue | Remove-Module -Force
}

Describe 'Sync-JIMMetaverseObjectPassword' {

    Context 'Parameter Validation' {

        BeforeAll {
            $command = Get-Command Sync-JIMMetaverseObjectPassword
        }

        It 'Should exist and be exported by the module' {
            $command | Should -Not -BeNullOrEmpty
        }

        It 'Should have a mandatory Id parameter accepting a Guid' {
            $param = $command.Parameters['Id']
            $param | Should -Not -BeNullOrEmpty
            $param.ParameterType | Should -Be ([guid])
            $param.Attributes.Where({ $_ -is [System.Management.Automation.ParameterAttribute] }).Mandatory |
                Should -Contain $true
        }

        It 'Should take the password as a SecureString' {
            # A plain string would sit in the session history and in memory as a readable value; every other
            # password-taking cmdlet in the module takes a SecureString for the same reason.
            $param = $command.Parameters['Password']
            $param | Should -Not -BeNullOrEmpty
            $param.ParameterType | Should -Be ([securestring])
        }

        It 'Should have a mandatory Password parameter' {
            $command.Parameters['Password'].Attributes.Where({ $_ -is [System.Management.Automation.ParameterAttribute] }).Mandatory |
                Should -Contain $true
        }

        It 'Should support ShouldProcess' {
            # It changes somebody's password in every system they have an account in. That belongs behind
            # -WhatIf and -Confirm.
            $command.Parameters['WhatIf'] | Should -Not -BeNullOrEmpty
        }

        It 'Should be a high-impact operation' {
            $binding = $command.ScriptBlock.Attributes.Where({ $_ -is [System.Management.Automation.CmdletBindingAttribute] })
            $binding.ConfirmImpact | Should -Be 'High'
        }

        It 'Should expose <Name>' -ForEach @(
            @{ Name = 'ExpiryBehaviour' }
            @{ Name = 'Force' }
        ) {
            $command.Parameters[$Name] | Should -Not -BeNullOrEmpty
        }

        It 'Should offer only expiry behaviours JIM understands' {
            $validateSet = $command.Parameters['ExpiryBehaviour'].Attributes |
                Where-Object { $_ -is [System.Management.Automation.ValidateSetAttribute] }
            $validateSet | Should -Not -BeNullOrEmpty
            $validateSet.ValidValues | Should -Contain 'RequireChangeAtNextSignIn'
            $validateSet.ValidValues | Should -Contain 'ExpiresAccordingToTargetPolicy'
            $validateSet.ValidValues | Should -Contain 'NeverExpires'
        }

        It 'Should accept the Metaverse Object from the pipeline by property name' {
            # So Get-JIMMetaverseObject | Sync-JIMMetaverseObjectPassword works, which is how a bulk change is
            # actually driven.
            $param = $command.Parameters['Id']
            $param.Attributes.Where({ $_ -is [System.Management.Automation.ParameterAttribute] }).ValueFromPipelineByPropertyName |
                Should -Contain $true
        }

        It 'Should not offer an account selection' -ForEach @(
            @{ Name = 'ConnectedSystemId' }
            @{ Name = 'AllAccounts' }
        ) {
            # Which systems receive a synchronised password is the Connected Systems' own configuration, not a
            # per-call choice. Offering a selection here would imply the caller can override it; they cannot.
            $command.Parameters[$Name] | Should -BeNullOrEmpty
        }

        It 'Should take -Wait as a number of seconds between 0 and 30' {
            # The Password Delivery Service answers in about a second, so a caller can ask to be told which
            # systems took the password before the call returns. The ceiling is the API's own (#1635); a longer
            # wait belongs in a polling loop over Get-JIMPendingPasswordChange, not in one held request.
            $param = $command.Parameters['Wait']
            $param | Should -Not -BeNullOrEmpty
            $param.ParameterType | Should -Be ([int])
            $range = $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ValidateRangeAttribute] }
            $range | Should -Not -BeNullOrEmpty
            $range.MinRange | Should -Be 0
            $range.MaxRange | Should -Be 30
        }

        It 'Should refuse a wait longer than the API allows' {
            $password = ConvertTo-SecureString 'Correct-Horse-42' -AsPlainText -Force
            { Sync-JIMMetaverseObjectPassword -Id ([guid]::NewGuid()) -Password $password -Wait 31 -Force -ErrorAction Stop } |
                Should -Throw -ErrorId 'ParameterArgumentValidationError*'
        }
    }

    Context 'Request body' {

        It 'Should send the wait alongside the password when -Wait is given' {
            InModuleScope JIM {
                $script:JIMConnection = @{ Url = 'https://jim.example.test' }
                Mock Invoke-JIMApi -MockWith { @{ ActivityId = [guid]::NewGuid(); Settled = $true; Targets = @() } }

                Sync-JIMMetaverseObjectPassword -Id ([guid]'8f1c2d3e-4a5b-6c7d-8e9f-0a1b2c3d4e5f') `
                    -Password (ConvertTo-SecureString 'Correct-Horse-42' -AsPlainText -Force) -Wait 10 -Force | Out-Null

                Should -Invoke Invoke-JIMApi -Times 1 -ParameterFilter {
                    $Method -eq 'POST' -and
                    $Endpoint -eq '/api/v1/metaverse/objects/8f1c2d3e-4a5b-6c7d-8e9f-0a1b2c3d4e5f/password' -and
                    $Body.wait -eq 10 -and
                    $Body.password -eq 'Correct-Horse-42'
                }
            }
        }

        It 'Should not send a wait when -Wait is not given' {
            # Omitted rather than sent as zero: the server's default is the contract, and a request that names no
            # wait must keep meaning "return on enqueue" whatever that default becomes.
            InModuleScope JIM {
                $script:JIMConnection = @{ Url = 'https://jim.example.test' }
                Mock Invoke-JIMApi -MockWith { @{ ActivityId = [guid]::NewGuid(); Settled = $false; Targets = @() } }

                Sync-JIMMetaverseObjectPassword -Id ([guid]'8f1c2d3e-4a5b-6c7d-8e9f-0a1b2c3d4e5f') `
                    -Password (ConvertTo-SecureString 'Correct-Horse-42' -AsPlainText -Force) -Force | Out-Null

                Should -Invoke Invoke-JIMApi -Times 1 -ParameterFilter {
                    $Method -eq 'POST' -and -not $Body.ContainsKey('wait')
                }
            }
        }
    }

    Context 'Output' {

        # The wire shape is what POST /api/v1/metaverse/objects/{id}/password returns once every target has
        # settled (200) or the wait ran out (202): the top-level Settled and one State per target are what a
        # script keys on. Mocked at the level Invoke-JIMApi hands back, which is already PascalCased.
        It 'Should report Settled and each target''s State when every system answered within the wait' {
            InModuleScope JIM {
                $script:JIMConnection = @{ Url = 'https://jim.example.test' }
                Mock Invoke-JIMApi -MockWith {
                    [PSCustomObject]@{
                        ActivityId         = [guid]'0a1b2c3d-4e5f-6a7b-8c9d-0e1f2a3b4c5d'
                        Settled            = $true
                        QueuedForNoSystems = $false
                        Targets            = @(
                            [PSCustomObject]@{ ConnectedSystemId = 3; ConnectedSystemName = 'Corporate AD'; Enabled = $true; State = 'Set'; NextAttemptAt = $null; Message = 'Password set'; AttemptCount = 1 },
                            [PSCustomObject]@{ ConnectedSystemId = 7; ConnectedSystemName = 'Payroll'; Enabled = $true; State = 'Parked'; NextAttemptAt = $null; Message = 'The password does not meet the length, complexity or history requirement of the domain.'; AttemptCount = 1 }
                        )
                    }
                }

                $result = Sync-JIMMetaverseObjectPassword -Id ([guid]'8f1c2d3e-4a5b-6c7d-8e9f-0a1b2c3d4e5f') `
                    -Password (ConvertTo-SecureString 'Correct-Horse-42' -AsPlainText -Force) -Wait 10 -Force

                $result.Settled | Should -BeTrue
                $result.ActivityId | Should -Be ([guid]'0a1b2c3d-4e5f-6a7b-8c9d-0e1f2a3b4c5d')
                @($result.Targets).Count | Should -Be 2
                @($result.Targets | Where-Object State -eq 'Set').ConnectedSystemName | Should -Be 'Corporate AD'
                $parked = $result.Targets | Where-Object State -eq 'Parked'
                $parked.ConnectedSystemName | Should -Be 'Payroll'
                $parked.Message | Should -BeLike '*requirement of the domain*'
                $parked.AttemptCount | Should -Be 1
            }
        }

        It 'Should report Settled false with what is known when the wait ran out' {
            InModuleScope JIM {
                $script:JIMConnection = @{ Url = 'https://jim.example.test' }
                $nextAttempt = (Get-Date).AddMinutes(1).ToUniversalTime()
                Mock Invoke-JIMApi -MockWith {
                    [PSCustomObject]@{
                        ActivityId         = [guid]::NewGuid()
                        Settled            = $false
                        QueuedForNoSystems = $false
                        Targets            = @(
                            [PSCustomObject]@{ ConnectedSystemId = 3; ConnectedSystemName = 'Corporate AD'; Enabled = $true; State = 'Delivering'; NextAttemptAt = $null; Message = $null; AttemptCount = 0 },
                            [PSCustomObject]@{ ConnectedSystemId = 7; ConnectedSystemName = 'Payroll'; Enabled = $true; State = 'Retrying'; NextAttemptAt = $nextAttempt; Message = 'The directory is unavailable.'; AttemptCount = 1 }
                        )
                    }
                }

                $result = Sync-JIMMetaverseObjectPassword -Id ([guid]'8f1c2d3e-4a5b-6c7d-8e9f-0a1b2c3d4e5f') `
                    -Password (ConvertTo-SecureString 'Correct-Horse-42' -AsPlainText -Force) -Wait 5 -Force

                $result.Settled | Should -BeFalse
                @($result.Targets | Where-Object State -eq 'Delivering').Count | Should -Be 1
                $retrying = $result.Targets | Where-Object State -eq 'Retrying'
                $retrying.NextAttemptAt | Should -Be $nextAttempt
                $retrying.AttemptCount | Should -Be 1
            }
        }
    }

    Context 'Connection Validation' {

        It 'Should error when not connected to JIM' {
            InModuleScope JIM {
                $script:JIMConnection = $null
                $password = ConvertTo-SecureString 'Correct-Horse-42' -AsPlainText -Force
                { Sync-JIMMetaverseObjectPassword -Id ([guid]::NewGuid()) -Password $password -Force -ErrorAction Stop } |
                    Should -Throw -ExpectedMessage '*not connected to JIM*'
            }
        }
    }
}
