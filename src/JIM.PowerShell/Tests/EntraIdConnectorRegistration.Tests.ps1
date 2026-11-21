# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

#Requires -Modules Pester

<#
.SYNOPSIS
    Pester tests for Register-JIMEntraIdConnector (#361): the app registration the Microsoft Entra ID Connector
    authenticates as, its permissions and credential, and the Connected System that uses it. Microsoft Graph and JIM are
    never called here.
#>

BeforeAll {
    $ModulePath = Join-Path $PSScriptRoot '..' 'JIM.psd1'
    Get-Module JIM -ErrorAction SilentlyContinue | Remove-Module -Force
    Import-Module $ModulePath -Force

    $script:TenantId = '34599ee0-829e-4dd4-8da2-4727a0664d51'
    $script:AppId = '11111111-2222-3333-4444-555555555555'
    $script:Now = [datetime]'2026-11-21T12:00:00Z'

    # Graph's application roles by value, as its service principal lists them.
    $script:GraphRoles = @{
        'User.Read.All' = 'role-user-read'; 'User.ReadWrite.All' = 'role-user-readwrite'
        'Group.Read.All' = 'role-group-read'; 'Group.ReadWrite.All' = 'role-group-readwrite'
        'User-OnPremisesSyncBehavior.Read.All' = 'role-user-sob'; 'Group-OnPremisesSyncBehavior.Read.All' = 'role-group-sob'
        'Member.Read.Hidden' = 'role-hidden'; 'Directory.Read.All' = 'role-directory'
        'User-PasswordProfile.ReadWrite.All' = 'role-password'
    }

    # A tenant with nothing of JIM's in it, and a JIM with the Connector seeded but no Connected System yet.
    $script:NewEmptyState = {
        [PSCustomObject]@{
            TenantId                     = $script:TenantId
            Applications                 = @()
            ServicePrincipal             = $null
            GrantedRoles                 = @()
            GraphServicePrincipal        = [PSCustomObject]@{ Id = 'graph-sp'; Roles = $script:GraphRoles }
            ManagedIdentity              = $null
            UserAdministratorAssignments = @()
            ConnectorDefinition          = [PSCustomObject]@{ Id = 7; SettingIds = @{
                'Tenant ID' = 1; 'Client ID' = 2; 'Authentication Method' = 3; 'Certificate' = 4; 'Managed Identity Client ID' = 5 } }
            ConnectedSystem              = $null
        }
    }

    # Everything as it should be for the default run: read and write users and groups, with a certificate.
    $script:NewRegisteredState = {
        $state = & $script:NewEmptyState
        $state.Applications = @([PSCustomObject]@{
            Id = 'app-object'; AppId = $script:AppId; DisplayName = 'JIM Entra ID Connector'; SignInAudience = 'AzureADMyOrg'
            GraphRoleIds = @('role-user-readwrite', 'role-group-readwrite', 'role-user-sob', 'role-group-sob', 'role-hidden')
            OtherResourceAccess = @()
            Certificates = @([PSCustomObject]@{ KeyId = 'cert-1'; DisplayName = 'JIM Entra ID Connector'; EndDateTime = $script:Now.AddDays(200) })
            FederatedCredentials = @()
        })
        $state.ServicePrincipal = [PSCustomObject]@{ Id = 'app-sp' }
        $state.GrantedRoles = @('role-user-readwrite', 'role-group-readwrite', 'role-user-sob', 'role-group-sob', 'role-hidden') |
            ForEach-Object { [PSCustomObject]@{ Id = "grant-$_"; AppRoleId = $_ } }
        $state.ConnectedSystem = [PSCustomObject]@{ Id = 42; ConnectorDefinitionId = 7; HasCertificate = $true; Settings = @{
            'Tenant ID' = $script:TenantId; 'Client ID' = $script:AppId; 'Authentication Method' = 'Certificate' } }
        $state
    }

    $script:Plan = {
        param($Current, [hashtable]$Options = @{})
        InModuleScope JIM -Parameters @{ Current = $Current; Options = $Options; Now = $script:Now } {
            Get-JIMEntraIdConnectorPlan -Name 'JIM Entra ID Connector' -ConnectedSystemName 'Microsoft Entra ID' -Current $Current -Now $Now @Options
        }
    }

    $script:Actions = { param($Plan) @($Plan.Changes | ForEach-Object Action) }
}

AfterAll {
    Get-Module JIM -ErrorAction SilentlyContinue | Remove-Module -Force
}

Describe 'Get-JIMEntraIdConnectorPlan' {

    Context 'An empty tenant' {

        It 'creates the application, its service principal, grants each permission, makes a certificate and the Connected System, in that order' {
            $plan = & $script:Plan (& $script:NewEmptyState)

            & $script:Actions $plan | Should -Be @(
                'NewApplication', 'NewServicePrincipal',
                'GrantPermission', 'GrantPermission', 'GrantPermission', 'GrantPermission', 'GrantPermission',
                'NewCertificate', 'NewConnectedSystem')
        }

        It 'asks for least privilege: read and write users and groups, Source of Authority, and hidden membership' {
            $plan = & $script:Plan (& $script:NewEmptyState)

            $plan.Permissions | Should -Be @('User.ReadWrite.All', 'Group.ReadWrite.All', 'User-OnPremisesSyncBehavior.Read.All', 'Group-OnPremisesSyncBehavior.Read.All', 'Member.Read.Hidden')
            $create = $plan.Changes | Where-Object Action -eq 'NewApplication'
            $create.Arguments.Application.signInAudience | Should -Be 'AzureADMyOrg'
            $access = $create.Arguments.Application.requiredResourceAccess | Where-Object resourceAppId -eq '00000003-0000-0000-c000-000000000000'
            @($access.resourceAccess | ForEach-Object { "$($_.type):$($_.id)" }) | Should -Be @(
                'Role:role-user-readwrite', 'Role:role-group-readwrite', 'Role:role-user-sob', 'Role:role-group-sob', 'Role:role-hidden')
        }

        It 'puts every setting the Connector needs on the new Connected System' {
            $plan = & $script:Plan (& $script:NewEmptyState)

            $system = $plan.Changes | Where-Object Action -eq 'NewConnectedSystem'
            $system.Arguments.ConnectorDefinitionId | Should -Be 7
            $system.Arguments.Settings.'Tenant ID' | Should -Be $script:TenantId
            $system.Arguments.Settings.'Authentication Method' | Should -Be 'Certificate'
            $system.Arguments.SetsClientId | Should -BeTrue
            $system.Arguments.SetsCertificate | Should -BeTrue
        }
    }

    Context 'Options' {

        It 'with -ReadOnly, asks only to read users and groups' {
            $plan = & $script:Plan (& $script:NewEmptyState) @{ ReadOnly = $true }

            $plan.Permissions | Should -Contain 'User.Read.All'
            $plan.Permissions | Should -Contain 'Group.Read.All'
            $plan.Permissions | Should -Not -Contain 'User.ReadWrite.All'
            $plan.Permissions | Should -Not -Contain 'Group.ReadWrite.All'
        }

        It 'with -SkipHiddenMembership, leaves Member.Read.Hidden out' {
            $plan = & $script:Plan (& $script:NewEmptyState) @{ SkipHiddenMembership = $true }

            $plan.Permissions | Should -Not -Contain 'Member.Read.Hidden'
        }

        It 'with -DirectoryExtensions, adds Directory.Read.All' {
            $plan = & $script:Plan (& $script:NewEmptyState) @{ DirectoryExtensions = $true }

            $plan.Permissions | Should -Contain 'Directory.Read.All'
        }

        It 'with -PasswordManagement, adds the permission and assigns User Administrator over the Administrative Unit' {
            $plan = & $script:Plan (& $script:NewEmptyState) @{ PasswordManagement = $true; AdministrativeUnitId = 'au-1' }

            $plan.Permissions | Should -Contain 'User-PasswordProfile.ReadWrite.All'
            $assign = $plan.Changes | Where-Object Action -eq 'AssignUserAdministrator'
            $assign.Arguments.DirectoryScopeId | Should -Be '/administrativeUnits/au-1'
            (& $script:Actions $plan).IndexOf('AssignUserAdministrator') | Should -BeLessThan (& $script:Actions $plan).IndexOf('NewConnectedSystem')
        }

        It 'with -PasswordManagement, refuses to run without an Administrative Unit' {
            { & $script:Plan (& $script:NewEmptyState) @{ PasswordManagement = $true } } | Should -Throw '*AdministrativeUnitId*'
        }

        It 'with a managed identity, adds a federated credential trusting it, and makes no certificate' {
            $state = & $script:NewEmptyState
            $state.ManagedIdentity = [PSCustomObject]@{ ClientId = '3b9f6a1e-2c4d-4e8f-9a0b-1c2d3e4f5a6b'; PrincipalId = 'mi-principal' }

            $plan = & $script:Plan $state @{ ManagedIdentityClientId = '3b9f6a1e-2c4d-4e8f-9a0b-1c2d3e4f5a6b' }

            & $script:Actions $plan | Should -Not -Contain 'NewCertificate'
            $credential = $plan.Changes | Where-Object Action -eq 'NewFederatedCredential'
            $credential.Arguments.Credential.issuer | Should -Be "https://login.microsoftonline.com/$script:TenantId/v2.0"
            $credential.Arguments.Credential.subject | Should -Be 'mi-principal'
            $credential.Arguments.Credential.audiences | Should -Be @('api://AzureADTokenExchange')
            $system = $plan.Changes | Where-Object Action -eq 'NewConnectedSystem'
            $system.Arguments.Settings.'Authentication Method' | Should -Be 'Federated Credential'
            $system.Arguments.Settings.'Managed Identity Client ID' | Should -Be '3b9f6a1e-2c4d-4e8f-9a0b-1c2d3e4f5a6b'
        }

        It 'with a managed identity the tenant does not hold, refuses to run' {
            { & $script:Plan (& $script:NewEmptyState) @{ ManagedIdentityClientId = '3b9f6a1e-2c4d-4e8f-9a0b-1c2d3e4f5a6b' } } |
                Should -Throw '*managed identity*'
        }
    }

    Context 'A registration that is already right' {

        It 'changes nothing' {
            $plan = & $script:Plan (& $script:NewRegisteredState)

            $plan.Changes | Should -BeNullOrEmpty
        }
    }

    Context 'A registration that has drifted' {

        It 'grants a permission that was asked for but never granted' {
            $state = & $script:NewRegisteredState
            $state.GrantedRoles = @($state.GrantedRoles | Where-Object AppRoleId -ne 'role-hidden')

            $plan = & $script:Plan $state

            & $script:Actions $plan | Should -Be @('GrantPermission')
            $plan.Changes[0].Arguments.AppRoleId | Should -Be 'role-hidden'
        }

        It 'revokes a Microsoft Graph permission it no longer asks for, so the app holds no more than it needs' {
            $state = & $script:NewRegisteredState

            $plan = & $script:Plan $state @{ ReadOnly = $true }

            $revoked = @($plan.Changes | Where-Object Action -eq 'RevokePermission' | ForEach-Object { $_.Arguments.AssignmentId })
            $revoked | Should -Contain 'grant-role-user-readwrite'
            $revoked | Should -Contain 'grant-role-group-readwrite'
            ($plan.Changes | Where-Object Action -eq 'SetApplicationPermissions') | Should -Not -BeNullOrEmpty
        }

        It 'keeps permissions on other APIs that someone added to the application' {
            $state = & $script:NewRegisteredState
            $other = [PSCustomObject]@{ resourceAppId = 'other-api'; resourceAccess = @(@{ id = 'x'; type = 'Role' }) }
            $state.Applications[0].OtherResourceAccess = @($other)
            $state.Applications[0].GraphRoleIds = @('role-user-readwrite')

            $plan = & $script:Plan $state

            $set = $plan.Changes | Where-Object Action -eq 'SetApplicationPermissions'
            @($set.Arguments.RequiredResourceAccess | ForEach-Object resourceAppId) | Should -Contain 'other-api'
        }

        It 'renews a certificate that expires within 30 days, retiring the old one once JIM holds the new' {
            $state = & $script:NewRegisteredState
            $state.Applications[0].Certificates[0].EndDateTime = $script:Now.AddDays(20)

            $plan = & $script:Plan $state

            & $script:Actions $plan | Should -Be @('NewCertificate', 'SetConnectedSystem', 'RetireCertificates')
            ($plan.Changes | Where-Object Action -eq 'RetireCertificates').Arguments.KeyIds | Should -Be @('cert-1')
        }

        It 'with -ResetCertificate, makes a new certificate even though the current one is fine' {
            $plan = & $script:Plan (& $script:NewRegisteredState) @{ ResetCertificate = $true }

            & $script:Actions $plan | Should -Be @('NewCertificate', 'SetConnectedSystem', 'RetireCertificates')
        }

        It 'makes a certificate when the Connected System is new, since the one the application has cannot be read back' {
            $state = & $script:NewRegisteredState
            $state.ConnectedSystem = $null

            $plan = & $script:Plan $state

            & $script:Actions $plan | Should -Be @('NewCertificate', 'NewConnectedSystem', 'RetireCertificates')
        }

        It 'makes a certificate when the Connected System holds none, though the application has one' {
            $state = & $script:NewRegisteredState
            $state.ConnectedSystem.HasCertificate = $false

            $plan = & $script:Plan $state

            & $script:Actions $plan | Should -Be @('NewCertificate', 'SetConnectedSystem', 'RetireCertificates')
        }

        It 'corrects a Connected System setting that differs, and only that one' {
            $state = & $script:NewRegisteredState
            $state.ConnectedSystem.Settings.'Client ID' = 'someone-elses-app'

            $plan = & $script:Plan $state

            $set = $plan.Changes | Where-Object Action -eq 'SetConnectedSystem'
            $set.Arguments.SetsClientId | Should -BeTrue
            $set.Arguments.Settings.Keys | Should -Not -Contain 'Tenant ID'
        }

        It 'refuses a Connected System of that name that uses another Connector' {
            $state = & $script:NewRegisteredState
            $state.ConnectedSystem.ConnectorDefinitionId = 3

            { & $script:Plan $state } | Should -Throw '*another Connector*'
        }

        It 'refuses when the tenant holds more than one application of that name' {
            $state = & $script:NewRegisteredState
            $state.Applications = @($state.Applications[0], $state.Applications[0])

            { & $script:Plan $state } | Should -Throw '*more than one application*'
        }

        It 'switching to a managed identity retires the certificates it made' {
            $state = & $script:NewRegisteredState
            $state.ManagedIdentity = [PSCustomObject]@{ ClientId = '3b9f6a1e-2c4d-4e8f-9a0b-1c2d3e4f5a6b'; PrincipalId = 'mi-principal' }

            $plan = & $script:Plan $state @{ ManagedIdentityClientId = '3b9f6a1e-2c4d-4e8f-9a0b-1c2d3e4f5a6b' }

            & $script:Actions $plan | Should -Be @('NewFederatedCredential', 'SetConnectedSystem', 'RetireCertificates')
        }

        It 'leaves a User Administrator assignment that already covers the Administrative Unit' {
            $state = & $script:NewRegisteredState
            $state.Applications[0].GraphRoleIds += 'role-password'
            $state.GrantedRoles += [PSCustomObject]@{ Id = 'grant-role-password'; AppRoleId = 'role-password' }
            $state.UserAdministratorAssignments = @([PSCustomObject]@{ Id = 'ra-1'; DirectoryScopeId = '/administrativeUnits/au-1' })

            $plan = & $script:Plan $state @{ PasswordManagement = $true; AdministrativeUnitId = 'au-1' }

            $plan.Changes | Should -BeNullOrEmpty
        }
    }

    Context 'JIM without the Connector' {

        It 'refuses, naming the Connector' {
            $state = & $script:NewEmptyState
            $state.ConnectorDefinition = $null

            { & $script:Plan $state } | Should -Throw '*JIM Microsoft Entra ID Connector*'
        }
    }
}

Describe 'New-JIMEntraIdConnectorCertificate' {

    It 'makes an RSA certificate of at least 3072 bits, valid from now for the days asked, with its private key in the PFX' {
        InModuleScope JIM {
            $made = New-JIMEntraIdConnectorCertificate -Name 'JIM Entra ID Connector' -ValidityDays 365
            $pfx = [Convert]::FromBase64String([System.Net.NetworkCredential]::new('', $made.Pfx).Password)
            $certificate = [System.Security.Cryptography.X509Certificates.X509CertificateLoader]::LoadPkcs12($pfx, $null)
            try {
                $certificate.HasPrivateKey | Should -BeTrue
                ([System.Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPublicKey($certificate).ExportParameters($false).Modulus.Length * 8) | Should -BeGreaterOrEqual 3072
                $certificate.Subject | Should -Be 'CN=JIM Entra ID Connector'
                ($certificate.NotAfter.ToUniversalTime() - [datetime]::UtcNow).TotalDays | Should -BeGreaterThan 363
                $made.KeyCredential.type | Should -Be 'AsymmetricX509Cert'
                $made.KeyCredential.usage | Should -Be 'Verify'
                $made.KeyCredential.key | Should -Be ([Convert]::ToBase64String($certificate.RawData))
                $made.Thumbprint | Should -Be $certificate.Thumbprint
            }
            finally {
                $certificate.Dispose()
            }
        }
    }

    It 'keeps the private key out of everything but the PFX secure string' {
        InModuleScope JIM {
            $made = New-JIMEntraIdConnectorCertificate -Name 'JIM Entra ID Connector' -ValidityDays 365
            $made.Pfx | Should -BeOfType [securestring]
            ($made.KeyCredential | ConvertTo-Json) | Should -Not -Match 'PRIVATE'
        }
    }
}

Describe 'Register-JIMEntraIdConnector' {

    BeforeEach {
        InModuleScope JIM -Parameters @{ NewEmptyState = $script:NewEmptyState } {
            $script:Token = ConvertTo-SecureString 'graph-token' -AsPlainText -Force
            $script:JIMConnection = @{ Url = 'https://jim.example.com' }
            $script:EmptyState = & $NewEmptyState
            Mock Get-JIMEntraIdConnectorState { $script:EmptyState }
            Mock Get-JIMEntraIdTokenTenantId { '34599ee0-829e-4dd4-8da2-4727a0664d51' }
            Mock Get-JIMEntraIdAdminToken { ConvertTo-SecureString 'browser-token' -AsPlainText -Force }
            $script:Applied = [System.Collections.Generic.List[string]]::new()
            Mock Invoke-JIMEntraIdConnectorChange {
                $script:Applied.Add($Change.Action)
                switch ($Change.Action) {
                    'NewApplication' { $Connection.AppId = '11111111-2222-3333-4444-555555555555' }
                    'NewCertificate' { $Connection.Certificate = [PSCustomObject]@{ Thumbprint = 'ABC123'; NotAfter = [datetime]'2027-11-21' } }
                    'NewConnectedSystem' { $Connection.ConnectedSystemId = 42; $Connection.CertificateStored = $true }
                }
            }
        }
    }

    AfterEach {
        InModuleScope JIM { $script:JIMConnection = $null }
    }

    It 'needs a connection to JIM' {
        InModuleScope JIM {
            $script:JIMConnection = $null
            { Register-JIMEntraIdConnector -TenantId 'contoso.onmicrosoft.com' -Confirm:$false } | Should -Throw '*Connect-JIM*'
        }
    }

    It 'refuses -PasswordManagement without an Administrative Unit before anyone signs in' {
        InModuleScope JIM {
            { Register-JIMEntraIdConnector -TenantId 'contoso.onmicrosoft.com' -PasswordManagement -Confirm:$false } | Should -Throw '*AdministrativeUnitId*'
            Should -Invoke Get-JIMEntraIdAdminToken -Times 0
        }
    }

    It 'signs in through the browser for what it needs, and asks to manage directory roles only with -PasswordManagement' {
        InModuleScope JIM {
            $null = Register-JIMEntraIdConnector -TenantId 'contoso.onmicrosoft.com' -Confirm:$false
            Should -Invoke Get-JIMEntraIdAdminToken -Times 1 -ParameterFilter {
                -not $UseDeviceCode -and $Scopes -contains 'https://graph.microsoft.com/AppRoleAssignment.ReadWrite.All' -and
                $Scopes -notcontains 'https://graph.microsoft.com/RoleManagement.ReadWrite.Directory' }

            $null = Register-JIMEntraIdConnector -TenantId 'contoso.onmicrosoft.com' -PasswordManagement -AdministrativeUnitId '9d6f4c2e-1b3a-4e5f-8a7b-6c5d4e3f2a1b' -Confirm:$false
            Should -Invoke Get-JIMEntraIdAdminToken -Times 1 -ParameterFilter { $Scopes -contains 'https://graph.microsoft.com/RoleManagement.ReadWrite.Directory' }
        }
    }

    It 'makes each change in order and returns what it set up' {
        InModuleScope JIM {
            $result = Register-JIMEntraIdConnector -AccessToken $script:Token -Confirm:$false

            $script:Applied[0] | Should -Be 'NewApplication'
            $script:Applied[-1] | Should -Be 'NewConnectedSystem'
            $result.PSObject.TypeNames | Should -Contain 'JIM.EntraIdConnectorRegistration'
            $result.TenantId | Should -Be '34599ee0-829e-4dd4-8da2-4727a0664d51'
            $result.ClientId | Should -Be '11111111-2222-3333-4444-555555555555'
            $result.ConnectedSystemId | Should -Be 42
            $result.AuthenticationMethod | Should -Be 'Certificate'
            $result.CertificateThumbprint | Should -Be 'ABC123'
            $result.Permissions | Should -Contain 'User.ReadWrite.All'
            $result.Changes | Should -Not -BeNullOrEmpty
            Should -Invoke Get-JIMEntraIdAdminToken -Times 0
        }
    }

    It 'with -WhatIf, changes nothing and returns nothing' {
        InModuleScope JIM {
            $result = Register-JIMEntraIdConnector -AccessToken $script:Token -WhatIf

            $result | Should -BeNullOrEmpty
            Should -Invoke Invoke-JIMEntraIdConnectorChange -Times 0
        }
    }

    It 'warns when a certificate was made but JIM never received it' {
        InModuleScope JIM {
            Mock Invoke-JIMEntraIdConnectorChange {
                if ($Change.Action -eq 'NewCertificate') { $Connection.Certificate = [PSCustomObject]@{ Thumbprint = 'ABC123' } }
                if ($Change.Action -eq 'NewConnectedSystem') { throw 'JIM refused the settings' }
            }
            Mock Write-Warning {}

            { Register-JIMEntraIdConnector -AccessToken $script:Token -Confirm:$false } | Should -Throw '*JIM refused*'
            Should -Invoke Write-Warning -Times 1 -ParameterFilter { $Message -like '*-ResetCertificate*' }
        }
    }
}

Describe 'Invoke-JIMEntraIdConnectorChange' {

    BeforeEach {
        InModuleScope JIM {
            $script:GraphCalls = [System.Collections.Generic.List[object]]::new()
            $script:JimCalls = [System.Collections.Generic.List[object]]::new()
            Mock Invoke-JIMEntraIdGraphApi {
                $script:GraphCalls.Add([PSCustomObject]@{ Method = $Method; Path = $Path; Body = $Body })
                if ($Path -like '*select=keyCredentials') {
                    [PSCustomObject]@{ keyCredentials = @([PSCustomObject]@{
                        keyId = 'other-key'; type = 'AsymmetricX509Cert'; usage = 'Verify'; key = 'b3RoZXI='; displayName = 'Someone else'
                        startDateTime = '2026-01-01T00:00:00Z'; endDateTime = '2027-01-01T00:00:00Z' }) }
                }
            }
            Mock Invoke-JIMApi {
                $script:JimCalls.Add([PSCustomObject]@{ Method = $Method; Endpoint = $Endpoint; Body = $Body })
                if ($Method -eq 'POST') { [PSCustomObject]@{ id = 42 } }
            }
            $script:Connection = @{
                ApplicationId = 'app-object'; AppId = 'client-id'; ServicePrincipalId = 'app-sp'; GraphServicePrincipalId = 'graph-sp'
                CertificateValidityDays = 365; SettingIds = @{ 'Tenant ID' = 1; 'Client ID' = 2; 'Authentication Method' = 3; 'Certificate' = 4 }
            }
        }
    }

    It 'adds a new certificate alongside those the application already has' {
        InModuleScope JIM {
            Invoke-JIMEntraIdConnectorChange -Connection $script:Connection -Change ([PSCustomObject]@{ Action = 'NewCertificate'; Target = 'JIM Entra ID Connector'; Arguments = @{} })

            $patch = $script:GraphCalls | Where-Object Method -eq 'Patch' | Select-Object -First 1
            @($patch.Body.keyCredentials).Count | Should -Be 2
            @($patch.Body.keyCredentials | ForEach-Object { $_.keyId }) | Should -Contain 'other-key'
            ($patch.Body.keyCredentials | Where-Object { $_.displayName -eq 'JIM Entra ID Connector' }).usage | Should -Be 'Verify'
            $script:Connection.Certificate.Pfx | Should -BeOfType [securestring]
        }
    }

    It 'grants a permission to the application''s service principal from Microsoft Graph' {
        InModuleScope JIM {
            Invoke-JIMEntraIdConnectorChange -Connection $script:Connection -Change ([PSCustomObject]@{ Action = 'GrantPermission'; Target = 'x'; Arguments = @{ AppRoleId = 'role-hidden' } })

            $grant = $script:GraphCalls | Select-Object -First 1
            $grant.Path | Should -Be '/servicePrincipals/app-sp/appRoleAssignments'
            $grant.Body.principalId | Should -Be 'app-sp'
            $grant.Body.resourceId | Should -Be 'graph-sp'
            $grant.Body.appRoleId | Should -Be 'role-hidden'
        }
    }

    It 'creates the Connected System, then sets its values by setting ID, the certificate included, and forgets the certificate' {
        InModuleScope JIM {
            $script:Connection.Certificate = [PSCustomObject]@{ Pfx = (ConvertTo-SecureString 'UEZY' -AsPlainText -Force); Thumbprint = 'ABC' }
            Invoke-JIMEntraIdConnectorChange -Connection $script:Connection -Change ([PSCustomObject]@{
                Action = 'NewConnectedSystem'; Target = 'Microsoft Entra ID'
                Arguments = @{ ConnectorDefinitionId = 7; Settings = @{ 'Tenant ID' = 'tenant'; 'Authentication Method' = 'Certificate' }; SetsClientId = $true; SetsCertificate = $true } })

            $script:JimCalls[0].Method | Should -Be 'POST'
            $script:JimCalls[0].Body.connectorDefinitionId | Should -Be 7
            $put = $script:JimCalls[1]
            $put.Endpoint | Should -Be '/api/v1/synchronisation/connected-systems/42'
            $put.Body.settingValues['1'].stringValue | Should -Be 'tenant'
            $put.Body.settingValues['2'].stringValue | Should -Be 'client-id'
            $put.Body.settingValues['4'].stringValue | Should -Be 'UEZY'
            $script:Connection.Certificate.Pfx | Should -BeNullOrEmpty
            $script:Connection.CertificateStored | Should -BeTrue
        }
    }

    It 'retires only the certificates named, keeping the rest' {
        InModuleScope JIM {
            Invoke-JIMEntraIdConnectorChange -Connection $script:Connection -Change ([PSCustomObject]@{ Action = 'RetireCertificates'; Target = 'x'; Arguments = @{ KeyIds = @('cert-1') } })

            $patch = $script:GraphCalls | Where-Object Method -eq 'Patch' | Select-Object -First 1
            @($patch.Body.keyCredentials | ForEach-Object { $_.keyId }) | Should -Be @('other-key')
        }
    }
}
