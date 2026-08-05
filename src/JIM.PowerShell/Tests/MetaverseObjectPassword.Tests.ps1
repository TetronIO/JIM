# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

#Requires -Modules Pester

<#
.SYNOPSIS
    Pester tests for Set-JIMMetaverseObjectPassword (#1172).

.DESCRIPTION
    The rules worth guarding are the ones that decide which accounts get written to: naming the Connected
    Systems must be deliberate rather than defaulted, one system refusing must not stop the others, and every
    account must come back with its own outcome.
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

        <#
            Setting a password everywhere by default would turn a reset in one system into a reset in all of
            them. Asserted against the parameters rather than against set names, because the sets were split
            per password source when -Generate arrived (BySystem became BySystemSuppliedPassword and
            BySystemGeneratedPassword) and this test pinned the old names; the requirement is that choosing
            the accounts is mandatory in every set, not what those sets happen to be called.
        #>
        It 'Should require either named Connected Systems or an explicit AllAccounts' {
            $accountChoosers = @('ConnectedSystemId', 'AllAccounts')

            foreach ($set in $command.ParameterSets) {
                $mandatory = $set.Parameters |
                    Where-Object { $_.Name -in $accountChoosers -and $_.IsMandatory }

                $mandatory | Should -Not -BeNullOrEmpty -Because "parameter set '$($set.Name)' must make the caller choose which accounts to touch"
            }
        }

        It 'Should take the password as a SecureString' {
            $command.Parameters['Password'].ParameterType | Should -Be ([securestring])
        }

        It 'Should confirm before setting a password' {
            $command.ScriptBlock.Attributes |
                Where-Object { $_ -is [System.Management.Automation.CmdletBindingAttribute] -and $_.SupportsShouldProcess -and $_.ConfirmImpact -eq 'High' } |
                Should -Not -BeNullOrEmpty
        }
    }

    Context 'Behaviour' {

        BeforeAll {
            $script:MvoId = [guid]'8f1c2d3e-4a5b-6c7d-8e9f-0a1b2c3d4e5f'
        }

        It 'Should error rather than call the API when not connected' {
            InModuleScope JIM {
                $script:JIMConnection = $null
                Mock Invoke-JIMApi { throw 'the API must not be called' }

                { Set-JIMMetaverseObjectPassword -Id ([guid]::NewGuid()) -AllAccounts `
                        -Password (ConvertTo-SecureString 'Correct-Horse-42' -AsPlainText -Force) -Force -ErrorAction Stop } | Should -Throw
                Should -Invoke Invoke-JIMApi -Times 0
            }
        }

        It 'Should set the password on every account when AllAccounts is given' {
            InModuleScope JIM {
                $script:JIMConnection = @{ Url = 'https://jim.example.test' }
                Mock Invoke-JIMApi -ParameterFilter { $Method -eq 'GET' } -MockWith {
                    @{ ConnectedSystemObjects = @(
                        @{ Id = [guid]'11111111-1111-1111-1111-111111111111'; ConnectedSystemId = 1; ConnectedSystemName = 'Contoso AD' },
                        @{ Id = [guid]'22222222-2222-2222-2222-222222222222'; ConnectedSystemId = 2; ConnectedSystemName = 'Fabrikam HR' }) }
                }
                Mock Invoke-JIMApi -ParameterFilter { $Method -eq 'POST' } -MockWith { @{ AppliedExpiryBehaviour = 'RequireChangeAtNextSignIn' } }

                $results = Set-JIMMetaverseObjectPassword -Id ([guid]'8f1c2d3e-4a5b-6c7d-8e9f-0a1b2c3d4e5f') -AllAccounts `
                    -Password (ConvertTo-SecureString 'Correct-Horse-42' -AsPlainText -Force) -Force

                Should -Invoke Invoke-JIMApi -Times 2 -ParameterFilter { $Method -eq 'POST' }
                $results.Count | Should -Be 2
                @($results | Where-Object Success).Count | Should -Be 2
            }
        }

        # Accounts in systems the caller did not name are somebody else's password, and must be left alone.
        It 'Should set the password only in the Connected Systems named' {
            InModuleScope JIM {
                $script:JIMConnection = @{ Url = 'https://jim.example.test' }
                Mock Invoke-JIMApi -ParameterFilter { $Method -eq 'GET' } -MockWith {
                    @{ ConnectedSystemObjects = @(
                        @{ Id = [guid]'11111111-1111-1111-1111-111111111111'; ConnectedSystemId = 1; ConnectedSystemName = 'Contoso AD' },
                        @{ Id = [guid]'22222222-2222-2222-2222-222222222222'; ConnectedSystemId = 2; ConnectedSystemName = 'Fabrikam HR' }) }
                }
                Mock Invoke-JIMApi -ParameterFilter { $Method -eq 'POST' } -MockWith { @{} }

                Set-JIMMetaverseObjectPassword -Id ([guid]'8f1c2d3e-4a5b-6c7d-8e9f-0a1b2c3d4e5f') -ConnectedSystemId 2 `
                    -Password (ConvertTo-SecureString 'Correct-Horse-42' -AsPlainText -Force) -Force | Out-Null

                Should -Invoke Invoke-JIMApi -Times 1 -ParameterFilter {
                    $Method -eq 'POST' -and $Endpoint -like '*/connected-systems/2/*'
                }
            }
        }

        # A refusal from one Connected System says nothing about the others, and stopping there would leave the
        # administrator to repeat the exercise for accounts that would have worked.
        It 'Should continue to the remaining systems when one refuses' {
            InModuleScope JIM {
                $script:JIMConnection = @{ Url = 'https://jim.example.test' }
                Mock Invoke-JIMApi -ParameterFilter { $Method -eq 'GET' } -MockWith {
                    @{ ConnectedSystemObjects = @(
                        @{ Id = [guid]'11111111-1111-1111-1111-111111111111'; ConnectedSystemId = 1; ConnectedSystemName = 'Contoso AD' },
                        @{ Id = [guid]'22222222-2222-2222-2222-222222222222'; ConnectedSystemId = 2; ConnectedSystemName = 'Fabrikam HR' }) }
                }
                Mock Invoke-JIMApi -ParameterFilter { $Method -eq 'POST' -and $Endpoint -like '*/connected-systems/1/*' } -MockWith {
                    throw 'The password does not meet the requirements of the domain.'
                }
                Mock Invoke-JIMApi -ParameterFilter { $Method -eq 'POST' -and $Endpoint -like '*/connected-systems/2/*' } -MockWith { @{} }

                $results = Set-JIMMetaverseObjectPassword -Id ([guid]'8f1c2d3e-4a5b-6c7d-8e9f-0a1b2c3d4e5f') -AllAccounts `
                    -Password (ConvertTo-SecureString 'Correct-Horse-42' -AsPlainText -Force) -Force -ErrorAction SilentlyContinue

                $results.Count | Should -Be 2
                @($results | Where-Object { -not $_.Success }).Count | Should -Be 1
                ($results | Where-Object { -not $_.Success }).ConnectedSystemName | Should -Be 'Contoso AD'
                ($results | Where-Object Success).ConnectedSystemName | Should -Be 'Fabrikam HR'
            }
        }

        It 'Should report the refusing system''s own reason' {
            InModuleScope JIM {
                $script:JIMConnection = @{ Url = 'https://jim.example.test' }
                Mock Invoke-JIMApi -ParameterFilter { $Method -eq 'GET' } -MockWith {
                    @{ ConnectedSystemObjects = @(@{ Id = [guid]'11111111-1111-1111-1111-111111111111'; ConnectedSystemId = 1; ConnectedSystemName = 'Contoso AD' }) }
                }
                Mock Invoke-JIMApi -ParameterFilter { $Method -eq 'POST' } -MockWith { throw 'History requirements of the domain.' }

                $result = Set-JIMMetaverseObjectPassword -Id ([guid]'8f1c2d3e-4a5b-6c7d-8e9f-0a1b2c3d4e5f') -AllAccounts `
                    -Password (ConvertTo-SecureString 'Correct-Horse-42' -AsPlainText -Force) -Force -ErrorAction SilentlyContinue

                $result.Message | Should -BeLike '*History requirements of the domain*'
            }
        }

        It 'Should warn rather than call the API when no account matches the systems given' {
            InModuleScope JIM {
                $script:JIMConnection = @{ Url = 'https://jim.example.test' }
                Mock Invoke-JIMApi -ParameterFilter { $Method -eq 'GET' } -MockWith {
                    @{ ConnectedSystemObjects = @(@{ Id = [guid]'11111111-1111-1111-1111-111111111111'; ConnectedSystemId = 1; ConnectedSystemName = 'Contoso AD' }) }
                }
                Mock Invoke-JIMApi -ParameterFilter { $Method -eq 'POST' } -MockWith { throw 'the API must not be called' }

                Set-JIMMetaverseObjectPassword -Id ([guid]'8f1c2d3e-4a5b-6c7d-8e9f-0a1b2c3d4e5f') -ConnectedSystemId 99 `
                    -Password (ConvertTo-SecureString 'Correct-Horse-42' -AsPlainText -Force) -Force -WarningAction SilentlyContinue | Out-Null

                Should -Invoke Invoke-JIMApi -Times 0 -ParameterFilter { $Method -eq 'POST' }
            }
        }

        # Omitted, not false. False would ask each Connected System to disable an account nobody asked it to
        # touch, which on a reset for working accounts would lock their owner out of all of them at once.
        It 'Should not send enableAccount when it was not asked for' {
            InModuleScope JIM {
                $script:JIMConnection = @{ Url = 'https://jim.example.test' }
                Mock Invoke-JIMApi -ParameterFilter { $Method -eq 'GET' } -MockWith {
                    @{ ConnectedSystemObjects = @(@{ Id = [guid]'11111111-1111-1111-1111-111111111111'; ConnectedSystemId = 1; ConnectedSystemName = 'Contoso AD' }) }
                }
                Mock Invoke-JIMApi -ParameterFilter { $Method -eq 'POST' } -MockWith { @{} }

                Set-JIMMetaverseObjectPassword -Id ([guid]'8f1c2d3e-4a5b-6c7d-8e9f-0a1b2c3d4e5f') -AllAccounts `
                    -Password (ConvertTo-SecureString 'Correct-Horse-42' -AsPlainText -Force) -Force | Out-Null

                Should -Invoke Invoke-JIMApi -Times 1 -ParameterFilter {
                    $Method -eq 'POST' -and -not $Body.ContainsKey('enableAccount')
                }
            }
        }

        It 'Should not call the API when WhatIf is specified' {
            InModuleScope JIM {
                $script:JIMConnection = @{ Url = 'https://jim.example.test' }
                Mock Invoke-JIMApi -ParameterFilter { $Method -eq 'GET' } -MockWith {
                    @{ ConnectedSystemObjects = @(@{ Id = [guid]'11111111-1111-1111-1111-111111111111'; ConnectedSystemId = 1; ConnectedSystemName = 'Contoso AD' }) }
                }
                Mock Invoke-JIMApi -ParameterFilter { $Method -eq 'POST' } -MockWith { throw 'the API must not be called' }

                Set-JIMMetaverseObjectPassword -Id ([guid]'8f1c2d3e-4a5b-6c7d-8e9f-0a1b2c3d4e5f') -AllAccounts `
                    -Password (ConvertTo-SecureString 'Correct-Horse-42' -AsPlainText -Force) -WhatIf | Out-Null

                Should -Invoke Invoke-JIMApi -Times 0 -ParameterFilter { $Method -eq 'POST' }
            }
        }
    }
}
