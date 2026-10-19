# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

#Requires -Modules Pester

<#
.SYNOPSIS
    Pester tests for trusting the identity provider's certificate (#2363): Get-JIMIdentityProviderTrust,
    Add-JIMCertificate -FromIdentityProvider, -TrustedFor on Add-/Set-JIMCertificate, and the lock-out warnings on
    Set-/Remove-JIMCertificate.
#>

BeforeAll {
    $ModulePath = Join-Path $PSScriptRoot '..' 'JIM.psd1'
    Get-Module JIM -ErrorAction SilentlyContinue | Remove-Module -Force
    Import-Module $ModulePath -Force
}

AfterAll {
    Get-Module JIM -ErrorAction SilentlyContinue | Remove-Module -Force
}

Describe 'Get-JIMIdentityProviderTrust' {

    It 'Should throw when not connected' {
        Disconnect-JIM
        { Get-JIMIdentityProviderTrust -ErrorAction Stop } | Should -Throw '*Connect-JIM*'
    }

    It 'Reads the identity provider trust status' {
        InModuleScope JIM {
            $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
            Mock Invoke-JIMApi { [PSCustomObject]@{ state = 'Untrusted'; host = 'adfs.corp.contoso.com' } }

            $status = Get-JIMIdentityProviderTrust

            $status.state | Should -Be 'Untrusted'
            Should -Invoke Invoke-JIMApi -Times 1 -ParameterFilter { $Endpoint -eq '/api/v1/auth/identity-provider-trust' -and -not $Method }
        }
    }

    It 'Has help with examples' {
        (Get-Help Get-JIMIdentityProviderTrust -Full).Examples.Example.Count | Should -BeGreaterThan 0
    }
}

Describe 'Add-JIMCertificate -FromIdentityProvider' {

    BeforeAll {
        $script:untrusted = [PSCustomObject]@{
            state = 'Untrusted'
            host = 'adfs.corp.contoso.com'
            suggestedThumbprint = '8F3A91D06B2E47C2C21D'
            presentedChain = @(
                [PSCustomObject]@{ commonName = 'adfs.corp.contoso.com'; thumbprint = 'AA11'; isServerCertificate = $true; validTo = '2027-02-02T00:00:00Z' }
                [PSCustomObject]@{ commonName = 'Contoso Issuing CA 01'; thumbprint = '8F3A91D06B2E47C2C21D'; isServerCertificate = $false; validTo = '2031-03-14T00:00:00Z' }
            )
        }
    }

    Context 'Parameters' {

        BeforeAll {
            $command = Get-Command Add-JIMCertificate
        }

        It 'Has a FromIdentityProvider parameter set' {
            $command.ParameterSets.Name | Should -Contain 'FromIdentityProvider'
        }

        It 'Does not require a name when trusting from the identity provider' {
            $set = $command.ParameterSets | Where-Object Name -eq 'FromIdentityProvider'
            ($set.Parameters | Where-Object Name -eq 'Name').IsMandatory | Should -BeFalse
        }

        It 'Still requires a name for a file' {
            $set = $command.ParameterSets | Where-Object Name -eq 'FromFile'
            ($set.Parameters | Where-Object Name -eq 'Name').IsMandatory | Should -BeTrue
        }

        It 'Does not offer -TrustedFor when trusting from the identity provider, which is always sign-in' {
            $set = $command.ParameterSets | Where-Object Name -eq 'FromIdentityProvider'
            $set.Parameters.Name | Should -Not -Contain 'TrustedFor'
        }
    }

    Context 'With -Thumbprint' {

        It 'Asks JIM to trust exactly that certificate, without asking first' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi { [PSCustomObject]@{ outcome = 'Trusted'; message = 'Trusted.' } }
                Mock Request-JIMConfirmation { $false }

                $result = Add-JIMCertificate -FromIdentityProvider -Thumbprint '8F:3A:91' -Name 'AD FS issuing CA' -ChangeReason 'First start' -PassThru

                $result.outcome | Should -Be 'Trusted'
                Should -Invoke Request-JIMConfirmation -Times 0
                Should -Invoke Invoke-JIMApi -Times 1 -ParameterFilter {
                    $Endpoint -eq '/api/v1/certificates/identity-provider' -and $Method -eq 'POST' -and
                    $Body.thumbprint -eq '8F:3A:91' -and $Body.name -eq 'AD FS issuing CA' -and $Body.changeReason -eq 'First start'
                }
            }
        }

        It 'Does nothing under -WhatIf' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi { }

                Add-JIMCertificate -FromIdentityProvider -Thumbprint '8F3A91' -WhatIf

                Should -Invoke Invoke-JIMApi -Times 0
            }
        }
    }

    Context 'Without -Thumbprint' {

        It 'Shows the certificate it would trust and trusts it once confirmed' {
            InModuleScope JIM -Parameters @{ Untrusted = $script:untrusted } {
                param($Untrusted)
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi { $Untrusted } -ParameterFilter { $Endpoint -eq '/api/v1/auth/identity-provider-trust' }
                Mock Invoke-JIMApi { [PSCustomObject]@{ outcome = 'Trusted' } } -ParameterFilter { $Endpoint -eq '/api/v1/certificates/identity-provider' }
                Mock Request-JIMConfirmation { $true }

                Add-JIMCertificate -FromIdentityProvider

                Should -Invoke Request-JIMConfirmation -Times 1 -ParameterFilter {
                    $Query -like '*Contoso Issuing CA 01*' -and $Query -like '*8F3A91D06B2E47C2C21D*' -and $Query -like '*adfs.corp.contoso.com*'
                }
                Should -Invoke Invoke-JIMApi -Times 1 -ParameterFilter {
                    $Endpoint -eq '/api/v1/certificates/identity-provider' -and $Body.thumbprint -eq '8F3A91D06B2E47C2C21D'
                }
            }
        }

        It 'Trusts nothing when the administrator declines' {
            InModuleScope JIM -Parameters @{ Untrusted = $script:untrusted } {
                param($Untrusted)
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi { $Untrusted } -ParameterFilter { $Endpoint -eq '/api/v1/auth/identity-provider-trust' }
                Mock Invoke-JIMApi { } -ParameterFilter { $Endpoint -eq '/api/v1/certificates/identity-provider' }
                Mock Request-JIMConfirmation { $false }

                Add-JIMCertificate -FromIdentityProvider

                Should -Invoke Invoke-JIMApi -Times 0 -ParameterFilter { $Endpoint -eq '/api/v1/certificates/identity-provider' }
            }
        }

        It '-Force trusts the suggested certificate without asking' {
            InModuleScope JIM -Parameters @{ Untrusted = $script:untrusted } {
                param($Untrusted)
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi { $Untrusted } -ParameterFilter { $Endpoint -eq '/api/v1/auth/identity-provider-trust' }
                Mock Invoke-JIMApi { [PSCustomObject]@{ outcome = 'Trusted' } } -ParameterFilter { $Endpoint -eq '/api/v1/certificates/identity-provider' }
                Mock Request-JIMConfirmation { $false }

                Add-JIMCertificate -FromIdentityProvider -Force

                Should -Invoke Request-JIMConfirmation -Times 0
                Should -Invoke Invoke-JIMApi -Times 1 -ParameterFilter { $Endpoint -eq '/api/v1/certificates/identity-provider' -and $Body.thumbprint -eq '8F3A91D06B2E47C2C21D' }
            }
        }

        It '-WhatIf -Force still only previews' {
            InModuleScope JIM -Parameters @{ Untrusted = $script:untrusted } {
                param($Untrusted)
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi { $Untrusted } -ParameterFilter { $Endpoint -eq '/api/v1/auth/identity-provider-trust' }
                Mock Invoke-JIMApi { } -ParameterFilter { $Endpoint -eq '/api/v1/certificates/identity-provider' }

                Add-JIMCertificate -FromIdentityProvider -Force -WhatIf

                Should -Invoke Invoke-JIMApi -Times 0 -ParameterFilter { $Endpoint -eq '/api/v1/certificates/identity-provider' }
            }
        }

        It 'Says why when there is nothing to trust, and trusts nothing' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi { [PSCustomObject]@{ state = 'NameMismatch'; message = 'The certificate does not name the host.'; presentedChain = @() } } -ParameterFilter { $Endpoint -eq '/api/v1/auth/identity-provider-trust' }
                Mock Invoke-JIMApi { } -ParameterFilter { $Endpoint -eq '/api/v1/certificates/identity-provider' }

                { Add-JIMCertificate -FromIdentityProvider -Force -ErrorAction Stop } | Should -Throw '*does not name the host*'
                Should -Invoke Invoke-JIMApi -Times 0 -ParameterFilter { $Endpoint -eq '/api/v1/certificates/identity-provider' }
            }
        }
    }
}

Describe 'Add-JIMCertificate -TrustedFor' {

    It 'Sends both purposes when both are named' {
        InModuleScope JIM {
            $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
            Mock Invoke-JIMApi { [PSCustomObject]@{ id = [guid]::NewGuid() } }

            Add-JIMCertificate -Name 'Contoso Root CA' -CertificateBase64 'AAAA' -TrustedFor SignIn, Connectors

            Should -Invoke Invoke-JIMApi -Times 1 -ParameterFilter { $Body.trustedFor -eq 'SignIn, Connectors' }
        }
    }

    It 'Sends no purpose when none is named, so JIM applies its default (Connectors)' {
        InModuleScope JIM {
            $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
            Mock Invoke-JIMApi { [PSCustomObject]@{ id = [guid]::NewGuid() } }

            Add-JIMCertificate -Name 'Contoso Root CA' -Path '/connector-files/root.pem'

            Should -Invoke Invoke-JIMApi -Times 1 -ParameterFilter { -not $Body.ContainsKey('trustedFor') }
        }
    }

    It 'Accepts only Connectors and SignIn' {
        (Get-Command Add-JIMCertificate).Parameters['TrustedFor'].Attributes.ValidValues | Should -Be @('Connectors', 'SignIn')
    }
}

Describe 'Set-JIMCertificate -TrustedFor' {

    It 'Sends the new purpose' {
        InModuleScope JIM {
            $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
            Mock Invoke-JIMApi { [PSCustomObject]@{ signInDependsOnIt = $false } } -ParameterFilter { -not $Method }
            Mock Invoke-JIMApi { } -ParameterFilter { $Method -eq 'PATCH' }

            Set-JIMCertificate -Id ([guid]::NewGuid()) -TrustedFor Connectors, SignIn

            Should -Invoke Invoke-JIMApi -Times 1 -ParameterFilter { $Method -eq 'PATCH' -and $Body.trustedFor -eq 'Connectors, SignIn' }
        }
    }

    It 'Warns and asks before no longer trusting for sign-in the certificate sign-in depends on' {
        InModuleScope JIM {
            $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
            Mock Invoke-JIMApi { [PSCustomObject]@{ name = 'Contoso Issuing CA 01'; signInDependsOnIt = $true } } -ParameterFilter { -not $Method }
            Mock Invoke-JIMApi { } -ParameterFilter { $Method -eq 'PATCH' }
            Mock Request-JIMConfirmation { $false }

            Set-JIMCertificate -Id ([guid]::NewGuid()) -TrustedFor Connectors -WarningVariable warnings -WarningAction SilentlyContinue

            $warnings | Should -Not -BeNullOrEmpty
            "$warnings" | Should -BeLike '*stops everyone signing in*'
            Should -Invoke Request-JIMConfirmation -Times 1
            Should -Invoke Invoke-JIMApi -Times 0 -ParameterFilter { $Method -eq 'PATCH' }
        }
    }

    It 'Warns before disabling it, and -Force goes ahead without asking' {
        InModuleScope JIM {
            $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
            Mock Invoke-JIMApi { [PSCustomObject]@{ name = 'Contoso Issuing CA 01'; signInDependsOnIt = $true } } -ParameterFilter { -not $Method }
            Mock Invoke-JIMApi { } -ParameterFilter { $Method -eq 'PATCH' }
            Mock Request-JIMConfirmation { $false }

            Set-JIMCertificate -Id ([guid]::NewGuid()) -Disable -Force -WarningAction SilentlyContinue

            Should -Invoke Request-JIMConfirmation -Times 0
            Should -Invoke Invoke-JIMApi -Times 1 -ParameterFilter { $Method -eq 'PATCH' -and $Body.isEnabled -eq $false }
        }
    }

    It 'Warns under -WhatIf too, so the preview says what the change would do' {
        InModuleScope JIM {
            $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
            Mock Invoke-JIMApi { [PSCustomObject]@{ name = 'Contoso Issuing CA 01'; signInDependsOnIt = $true } } -ParameterFilter { -not $Method }
            Mock Invoke-JIMApi { } -ParameterFilter { $Method -eq 'PATCH' }
            Mock Request-JIMConfirmation { $true }

            Set-JIMCertificate -Id ([guid]::NewGuid()) -Disable -WhatIf -WarningVariable warnings -WarningAction SilentlyContinue

            "$warnings" | Should -BeLike '*stops everyone signing in*'
            Should -Invoke Request-JIMConfirmation -Times 0
            Should -Invoke Invoke-JIMApi -Times 0 -ParameterFilter { $Method -eq 'PATCH' }
        }
    }

    It 'Does not ask when sign-in does not depend on the certificate' {
        InModuleScope JIM {
            $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
            Mock Invoke-JIMApi { [PSCustomObject]@{ name = 'Lab SQL'; signInDependsOnIt = $false } } -ParameterFilter { -not $Method }
            Mock Invoke-JIMApi { } -ParameterFilter { $Method -eq 'PATCH' }
            Mock Request-JIMConfirmation { $false }

            Set-JIMCertificate -Id ([guid]::NewGuid()) -Disable

            Should -Invoke Request-JIMConfirmation -Times 0
            Should -Invoke Invoke-JIMApi -Times 1 -ParameterFilter { $Method -eq 'PATCH' }
        }
    }

    It 'Does not look the certificate up for a change that cannot affect sign-in' {
        InModuleScope JIM {
            $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
            Mock Invoke-JIMApi { }

            Set-JIMCertificate -Id ([guid]::NewGuid()) -Name 'Renamed'

            Should -Invoke Invoke-JIMApi -Times 0 -ParameterFilter { -not $Method }
        }
    }
}

Describe 'Remove-JIMCertificate lock-out warning' {

    It 'Warns that deleting the certificate sign-in depends on stops everyone signing in' {
        InModuleScope JIM {
            $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
            Mock Invoke-JIMApi { [PSCustomObject]@{ id = [guid]::NewGuid(); name = 'Contoso Issuing CA 01'; signInDependsOnIt = $true } } -ParameterFilter { -not $Method }
            Mock Invoke-JIMApi { } -ParameterFilter { $Method -eq 'DELETE' }

            Remove-JIMCertificate -Id ([guid]::NewGuid()) -WhatIf -WarningVariable warnings -WarningAction SilentlyContinue

            "$warnings" | Should -BeLike '*stops everyone signing in*'
            Should -Invoke Invoke-JIMApi -Times 0 -ParameterFilter { $Method -eq 'DELETE' }
        }
    }
}
