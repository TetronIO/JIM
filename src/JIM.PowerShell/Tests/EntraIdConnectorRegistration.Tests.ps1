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
        'User-OnPremisesSyncBehavior.ReadWrite.All' = 'role-user-sob'; 'Group-OnPremisesSyncBehavior.ReadWrite.All' = 'role-group-sob'
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
                'Tenant ID' = 1; 'Client ID' = 2; 'Authentication Method' = 3; 'Certificate' = 4; 'Managed Identity Client ID' = 5
                'Track Source of Authority Changes' = 6 } }
            ConnectedSystem              = $null
        }
    }

    # Everything as it should be for the default run: read and write users and groups, with a certificate.
    $script:NewRegisteredState = {
        $state = & $script:NewEmptyState
        $state.Applications = @([PSCustomObject]@{
            Id = 'app-object'; AppId = $script:AppId; DisplayName = 'JIM Entra ID Connector'; SignInAudience = 'AzureADMyOrg'
            GraphRoleIds = @('role-user-readwrite', 'role-group-readwrite', 'role-hidden')
            OtherResourceAccess = @()
            Certificates = @([PSCustomObject]@{ KeyId = 'cert-1'; DisplayName = 'JIM Entra ID Connector'; EndDateTime = $script:Now.AddDays(200) })
            FederatedCredentials = @()
        })
        $state.ServicePrincipal = [PSCustomObject]@{ Id = 'app-sp' }
        $state.GrantedRoles = @('role-user-readwrite', 'role-group-readwrite', 'role-hidden') |
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

    # The registered state with Source of Authority tracking on: both permissions granted, and the setting ticked.
    $script:NewTrackingState = {
        $state = & $script:NewRegisteredState
        $state.Applications[0].GraphRoleIds += 'role-user-sob', 'role-group-sob'
        $state.GrantedRoles += 'role-user-sob', 'role-group-sob' | ForEach-Object { [PSCustomObject]@{ Id = "grant-$_"; AppRoleId = $_ } }
        $state.ConnectedSystem.Settings['Track Source of Authority Changes'] = $true
        $state
    }

    $script:Resolve = {
        param($Current, [hashtable]$Bound = @{})
        InModuleScope JIM -Parameters @{ Current = $Current; Bound = $Bound } {
            Resolve-JIMEntraIdConnectorOption -Current $Current -Bound $Bound
        }
    }
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
                'GrantPermission', 'GrantPermission', 'GrantPermission',
                'NewCertificate', 'NewConnectedSystem')
        }

        It 'asks for least privilege: read and write users and groups, and hidden membership' {
            $plan = & $script:Plan (& $script:NewEmptyState)

            $plan.Permissions | Should -Be @('User.ReadWrite.All', 'Group.ReadWrite.All', 'Member.Read.Hidden')
            $create = $plan.Changes | Where-Object Action -eq 'NewApplication'
            $create.Arguments.Application.signInAudience | Should -Be 'AzureADMyOrg'
            $access = $create.Arguments.Application.requiredResourceAccess | Where-Object resourceAppId -eq '00000003-0000-0000-c000-000000000000'
            @($access.resourceAccess | ForEach-Object { "$($_.type):$($_.id)" }) | Should -Be @(
                'Role:role-user-readwrite', 'Role:role-group-readwrite', 'Role:role-hidden')
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

        It 'leaves Source of Authority tracking off, asking for neither of its permissions' {
            $plan = & $script:Plan (& $script:NewEmptyState)

            $plan.Permissions | Should -Not -Contain 'User-OnPremisesSyncBehavior.ReadWrite.All'
            $plan.Permissions | Should -Not -Contain 'Group-OnPremisesSyncBehavior.ReadWrite.All'
            ($plan.Changes | Where-Object Action -eq 'NewConnectedSystem').Arguments.Settings.'Track Source of Authority Changes' | Should -BeFalse
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

        It 'with -PasswordManagement, adds the permission and assigns User Administrator across the tenant' {
            $plan = & $script:Plan (& $script:NewEmptyState) @{ PasswordManagement = $true; CheckRole = $true }

            $plan.Permissions | Should -Contain 'User-PasswordProfile.ReadWrite.All'
            $assign = $plan.Changes | Where-Object Action -eq 'AssignUserAdministrator'
            $assign.Arguments.DirectoryScopeId | Should -Be '/'
            (& $script:Actions $plan).IndexOf('AssignUserAdministrator') | Should -BeLessThan (& $script:Actions $plan).IndexOf('NewConnectedSystem')
        }

        It 'with password management kept from an earlier run, keeps the permission and leaves the role alone' {
            $plan = & $script:Plan (& $script:NewEmptyState) @{ PasswordManagement = $true }

            $plan.Permissions | Should -Contain 'User-PasswordProfile.ReadWrite.All'
            & $script:Actions $plan | Should -Not -Contain 'AssignUserAdministrator'
        }

        It 'with -TrackSourceOfAuthority, asks for both Source of Authority permissions and turns the setting on' {
            $plan = & $script:Plan (& $script:NewEmptyState) @{ TrackSourceOfAuthority = $true }

            $plan.Permissions | Should -Contain 'User-OnPremisesSyncBehavior.ReadWrite.All'
            $plan.Permissions | Should -Contain 'Group-OnPremisesSyncBehavior.ReadWrite.All'
            ($plan.Changes | Where-Object Action -eq 'NewConnectedSystem').Arguments.Settings.'Track Source of Authority Changes' | Should -BeTrue
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

        It 'with a managed identity other than the one the tenant was asked about, refuses to run' {
            $state = & $script:NewEmptyState
            $state.ManagedIdentity = [PSCustomObject]@{ ClientId = '00000000-2c4d-4e8f-9a0b-1c2d3e4f5a6b'; PrincipalId = 'mi-principal' }

            { & $script:Plan $state @{ ManagedIdentityClientId = '3b9f6a1e-2c4d-4e8f-9a0b-1c2d3e4f5a6b' } } | Should -Throw '*managed identity*'
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

        It 'changes nothing with Source of Authority tracking on' {
            $plan = & $script:Plan (& $script:NewTrackingState) @{ TrackSourceOfAuthority = $true }

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

        It 'under -WhatIf, still names the certificates it would retire' {
            # ForEach-Object with a member name honours -WhatIf, so under a preview it read nothing back.
            $plan = InModuleScope JIM -Parameters @{ Current = (& $script:NewRegisteredState); Now = $script:Now } {
                $WhatIfPreference = $true
                Get-JIMEntraIdConnectorPlan -Name 'JIM Entra ID Connector' -ConnectedSystemName 'Microsoft Entra ID' -Current $Current -Now $Now -ResetCertificate
            }

            $retire = $plan.Changes | Where-Object Action -eq 'RetireCertificates'
            @($retire.Arguments.KeyIds).Count | Should -BeGreaterThan 0
            @($retire.Arguments.KeyIds) | Should -Not -Contain $null
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

        It 'turning Source of Authority tracking on grants its permissions and ticks the setting' {
            $plan = & $script:Plan (& $script:NewRegisteredState) @{ TrackSourceOfAuthority = $true }

            & $script:Actions $plan | Should -Be @('SetApplicationPermissions', 'GrantPermission', 'GrantPermission', 'SetConnectedSystem')
            ($plan.Changes | Where-Object Action -eq 'SetConnectedSystem').Arguments.Settings.'Track Source of Authority Changes' | Should -BeTrue
        }

        It 'turning Source of Authority tracking off revokes its permissions and clears the setting' {
            $plan = & $script:Plan (& $script:NewTrackingState)

            @($plan.Changes | Where-Object Action -eq 'RevokePermission').Count | Should -Be 2
            ($plan.Changes | Where-Object Action -eq 'SetConnectedSystem').Arguments.Settings.'Track Source of Authority Changes' | Should -BeFalse
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

        It 'leaves a User Administrator assignment the application already holds across the tenant' {
            $state = & $script:NewRegisteredState
            $state.Applications[0].GraphRoleIds += 'role-password'
            $state.GrantedRoles += [PSCustomObject]@{ Id = 'grant-role-password'; AppRoleId = 'role-password' }
            $state.UserAdministratorAssignments = @([PSCustomObject]@{ Id = 'ra-1'; DirectoryScopeId = '/' })

            $plan = & $script:Plan $state @{ PasswordManagement = $true; CheckRole = $true }

            $plan.Changes | Should -BeNullOrEmpty
        }

        It 'with -PasswordManagement:$false, revokes the permission and removes the User Administrator role' {
            # Turning password management off leaves the application with no privileged directory role.
            $state = & $script:NewRegisteredState
            $state.Applications[0].GraphRoleIds += 'role-password'
            $state.GrantedRoles += [PSCustomObject]@{ Id = 'grant-role-password'; AppRoleId = 'role-password' }
            $state.UserAdministratorAssignments = @([PSCustomObject]@{ Id = 'ra-1'; DirectoryScopeId = '/' })

            $plan = & $script:Plan $state @{ PasswordManagement = $false; CheckRole = $true }

            $plan.Permissions | Should -Not -Contain 'User-PasswordProfile.ReadWrite.All'
            & $script:Actions $plan | Should -Contain 'RevokePermission'
            $remove = $plan.Changes | Where-Object Action -eq 'RemoveUserAdministrator'
            $remove.Arguments.AssignmentId | Should -Be 'ra-1'
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

Describe 'Resolve-JIMEntraIdConnectorOption' {

    It 'on a new setup, leaves every capability at its default' {
        $options = & $script:Resolve (& $script:NewEmptyState)

        $options.ReadOnly | Should -BeFalse
        $options.SkipHiddenMembership | Should -BeFalse
        $options.DirectoryExtensions | Should -BeFalse
        $options.PasswordManagement | Should -BeFalse
        $options.TrackSourceOfAuthority | Should -BeFalse
        $options.ContainsKey('ManagedIdentityClientId') | Should -BeFalse
    }

    It 'on an existing setup, keeps each capability the application was granted, so a re-run without its switch removes nothing' {
        $state = & $script:NewEmptyState
        $state.Applications = @([PSCustomObject]@{ Id = 'app-object'; AppId = $script:AppId })
        $state.ServicePrincipal = [PSCustomObject]@{ Id = 'app-sp' }
        $state.GrantedRoles = @('role-user-read', 'role-group-read', 'role-directory', 'role-password', 'role-user-sob', 'role-group-sob') |
            ForEach-Object { [PSCustomObject]@{ Id = "grant-$_"; AppRoleId = $_ } }

        $options = & $script:Resolve $state

        $options.ReadOnly | Should -BeTrue
        $options.SkipHiddenMembership | Should -BeTrue
        $options.DirectoryExtensions | Should -BeTrue
        $options.PasswordManagement | Should -BeTrue
        $options.TrackSourceOfAuthority | Should -BeTrue
    }

    It 'under -WhatIf, still reads what the application was granted, so a preview reports no drift that is not there' {
        $options = InModuleScope JIM -Parameters @{ Current = (& $script:NewRegisteredState) } {
            $WhatIfPreference = $true
            Resolve-JIMEntraIdConnectorOption -Current $Current -Bound @{}
        }

        $options.SkipHiddenMembership | Should -BeFalse
    }

    It 'turns tracking on where the setting was ticked in the portal, so a re-run grants its permissions' {
        $state = & $script:NewRegisteredState
        $state.ConnectedSystem.Settings['Track Source of Authority Changes'] = $true

        (& $script:Resolve $state).TrackSourceOfAuthority | Should -BeTrue
    }

    It 'lets a switch given as false turn a capability off' {
        $options = & $script:Resolve (& $script:NewTrackingState) @{ TrackSourceOfAuthority = $false; SkipHiddenMembership = $true }

        $options.TrackSourceOfAuthority | Should -BeFalse
        $options.SkipHiddenMembership | Should -BeTrue
    }

    It 'keeps the managed identity of a Connected System that authenticates with one' {
        $state = & $script:NewRegisteredState
        $state.ConnectedSystem.Settings['Authentication Method'] = 'Federated Credential'
        $state.ConnectedSystem.Settings['Managed Identity Client ID'] = '3b9f6a1e-2c4d-4e8f-9a0b-1c2d3e4f5a6b'

        (& $script:Resolve $state).ManagedIdentityClientId | Should -Be '3b9f6a1e-2c4d-4e8f-9a0b-1c2d3e4f5a6b'
    }

    It 'with -ResetCertificate, moves a Connected System that authenticates with a managed identity back to a certificate' {
        $state = & $script:NewRegisteredState
        $state.ConnectedSystem.Settings['Authentication Method'] = 'Federated Credential'
        $state.ConnectedSystem.Settings['Managed Identity Client ID'] = '3b9f6a1e-2c4d-4e8f-9a0b-1c2d3e4f5a6b'

        (& $script:Resolve $state @{ ResetCertificate = $true }).ContainsKey('ManagedIdentityClientId') | Should -BeFalse
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

    It 'gives Microsoft Graph exactly the dates the certificate holds, which are whole seconds' {
        # Graph refuses a keyCredential whose end date is later than the certificate's (KeyCredentialsInvalidEndDate),
        # and an X.509 certificate keeps its dates to the second, so fractional seconds made every end date too late.
        InModuleScope JIM {
            $made = New-JIMEntraIdConnectorCertificate -Name 'JIM Entra ID Connector' -ValidityDays 365
            $certificate = [System.Security.Cryptography.X509Certificates.X509CertificateLoader]::LoadCertificate([Convert]::FromBase64String($made.KeyCredential.key))
            try {
                $start = [datetime]::Parse($made.KeyCredential.startDateTime, $null, 'RoundtripKind')
                $end = [datetime]::Parse($made.KeyCredential.endDateTime, $null, 'RoundtripKind')
                $start | Should -Be $certificate.NotBefore.ToUniversalTime()
                $end | Should -Be $certificate.NotAfter.ToUniversalTime()
                $made.NotAfter | Should -Be $certificate.NotAfter.ToUniversalTime()
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

    It 'takes no Administrative Unit: password management assigns its role across the tenant' {
        (Get-Command Register-JIMEntraIdConnector).Parameters.ContainsKey('AdministrativeUnitId') | Should -BeFalse
    }

    It 'with -PasswordManagement:$false, asks to manage directory roles, so it can remove the role' {
        InModuleScope JIM {
            $null = Register-JIMEntraIdConnector -TenantId 'contoso.onmicrosoft.com' -PasswordManagement:$false -Confirm:$false
            Should -Invoke Get-JIMEntraIdAdminToken -Times 1 -ParameterFilter { $Scopes -contains 'https://graph.microsoft.com/RoleManagement.ReadWrite.Directory' }
        }
    }

    It 'signs in through the browser for what it needs, and asks to manage directory roles only with -PasswordManagement' {
        InModuleScope JIM {
            $null = Register-JIMEntraIdConnector -TenantId 'contoso.onmicrosoft.com' -Confirm:$false
            Should -Invoke Get-JIMEntraIdAdminToken -Times 1 -ParameterFilter {
                -not $UseDeviceCode -and $Scopes -contains 'https://graph.microsoft.com/AppRoleAssignment.ReadWrite.All' -and
                $Scopes -notcontains 'https://graph.microsoft.com/RoleManagement.ReadWrite.Directory' }

            $null = Register-JIMEntraIdConnector -TenantId 'contoso.onmicrosoft.com' -PasswordManagement -Confirm:$false
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

    It 'with -TrackSourceOfAuthority, asks for its permissions and ticks the setting' {
        InModuleScope JIM {
            $result = Register-JIMEntraIdConnector -AccessToken $script:Token -TrackSourceOfAuthority -Confirm:$false

            $result.Permissions | Should -Contain 'User-OnPremisesSyncBehavior.ReadWrite.All'
            Should -Invoke Invoke-JIMEntraIdConnectorChange -ParameterFilter {
                $Change.Action -eq 'NewConnectedSystem' -and $Change.Arguments.Settings.'Track Source of Authority Changes' -eq $true }
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
                        keyId = 'other-key'; type = 'AsymmetricX509Cert'; usage = 'Verify'; key = 'b3RoZXI='; displayName = 'Someone else'; customKeyIdentifier = 'QUJDRA=='
                        startDateTime = '2026-01-01T00:00:00Z'; endDateTime = '2027-01-01T00:00:00Z' }) }
                }
            }
            Mock Invoke-JIMApi {
                $script:JimCalls.Add([PSCustomObject]@{ Method = $Method; Endpoint = $Endpoint; Body = $Body })
                if ($Method -eq 'POST') { [PSCustomObject]@{ id = 42 } }
            }
            $script:Connection = @{
                ApplicationId = 'app-object'; AppId = 'client-id'; ServicePrincipalId = 'app-sp'; GraphServicePrincipalId = 'graph-sp'
                CertificateValidityDays = 365; SettingIds = @{ 'Tenant ID' = 1; 'Client ID' = 2; 'Authentication Method' = 3; 'Certificate' = 4; 'Track Source of Authority Changes' = 6 }
            }
        }
    }

    It 'removes the User Administrator role assignment it is given' {
        InModuleScope JIM {
            Invoke-JIMEntraIdConnectorChange -Connection $script:Connection -Change ([PSCustomObject]@{ Action = 'RemoveUserAdministrator'; Target = 'JIM Entra ID Connector'; Arguments = @{ AssignmentId = 'ra-1' } })

            $call = $script:GraphCalls | Select-Object -Last 1
            $call.Method | Should -Be 'Delete'
            $call.Path | Should -Be '/roleManagement/directory/roleAssignments/ra-1'
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
                Arguments = @{ ConnectorDefinitionId = 7; Settings = @{ 'Tenant ID' = 'tenant'; 'Authentication Method' = 'Certificate'; 'Track Source of Authority Changes' = $true }; SetsClientId = $true; SetsCertificate = $true } })

            $script:JimCalls[0].Method | Should -Be 'POST'
            $script:JimCalls[0].Body.connectorDefinitionId | Should -Be 7
            $put = $script:JimCalls[1]
            $put.Endpoint | Should -Be '/api/v1/synchronisation/connected-systems/42'
            $put.Body.settingValues['1'].stringValue | Should -Be 'tenant'
            $put.Body.settingValues['2'].stringValue | Should -Be 'client-id'
            $put.Body.settingValues['4'].stringValue | Should -Be 'UEZY'
            $put.Body.settingValues['6'].checkboxValue | Should -BeTrue
            $put.Body.settingValues['6'].ContainsKey('stringValue') | Should -BeFalse
            $script:Connection.Certificate.Pfx | Should -BeNullOrEmpty
            $script:Connection.CertificateStored | Should -BeTrue
        }
    }

    It 'tries the Connected System again while Entra ID is still applying a credential and permissions just granted' {
        InModuleScope JIM {
            Mock Start-Sleep {}
            $script:Puts = 0
            Mock Invoke-JIMApi {
                if ($Method -eq 'PUT') {
                    $script:Puts++
                    if ($script:Puts -le 2) { throw 'One or more Connected System settings are invalid: The access token carries no Microsoft Graph application permissions.' }
                }
            }
            Invoke-JIMEntraIdConnectorChange -Connection $script:Connection -Change ([PSCustomObject]@{
                Action = 'SetConnectedSystem'; Target = 'Microsoft Entra ID'
                Arguments = @{ ConnectedSystemId = 42; Settings = @{ 'Tenant ID' = 'tenant' }; SetsClientId = $false; SetsCertificate = $false } })

            $script:Puts | Should -Be 3
            Should -Invoke Start-Sleep -Times 2
        }
    }

    It 'tries the Connected System again while Microsoft Graph does not yet know a service principal made moments ago' {
        # Seen against a real tenant: a new application's service principal is refused as unknown for a while after
        # it is created, though its token is issued.
        InModuleScope JIM {
            Mock Start-Sleep {}
            $script:Puts = 0
            Mock Invoke-JIMApi {
                if ($Method -eq 'PUT') {
                    $script:Puts++
                    if ($script:Puts -le 1) { throw 'JIM API error (400): One or more Connected System settings are invalid. - Tenant ID: Authenticated, but Microsoft Graph refused the test request: Microsoft Graph refused GET /v1.0/users with HTTP 401 (Authorization_IdentityNotFound): The identity of the calling application could not be established.' }
                }
            }
            Invoke-JIMEntraIdConnectorChange -Connection $script:Connection -Change ([PSCustomObject]@{
                Action = 'SetConnectedSystem'; Target = 'Microsoft Entra ID'
                Arguments = @{ ConnectedSystemId = 42; Settings = @{ 'Tenant ID' = 'tenant' }; SetsClientId = $false; SetsCertificate = $false } })

            $script:Puts | Should -Be 2
        }
    }

    It 'does not try the Connected System again for a refusal that waiting will not cure' {
        InModuleScope JIM {
            Mock Start-Sleep {}
            Mock Invoke-JIMApi { if ($Method -eq 'PUT') { throw 'One or more Connected System settings are invalid: The Tenant ID must be a GUID.' } }

            { Invoke-JIMEntraIdConnectorChange -Connection $script:Connection -Change ([PSCustomObject]@{
                Action = 'SetConnectedSystem'; Target = 'Microsoft Entra ID'
                Arguments = @{ ConnectedSystemId = 42; Settings = @{ 'Tenant ID' = 'x' }; SetsClientId = $false; SetsCertificate = $false } }) } | Should -Throw '*GUID*'
            Should -Invoke Start-Sleep -Times 0
        }
    }

    # Microsoft Graph refuses the whole list (KeyNotUpdatable) when a credential it already holds comes back with any
    # property changed or missing, so a kept credential goes back exactly as Graph returned it.
    It 'sends the certificates it keeps back exactly as Microsoft Graph returned them, when adding one' {
        InModuleScope JIM {
            Invoke-JIMEntraIdConnectorChange -Connection $script:Connection -Change ([PSCustomObject]@{ Action = 'NewCertificate'; Target = 'JIM Entra ID Connector'; Arguments = @{} })

            $patch = $script:GraphCalls | Where-Object Method -eq 'Patch' | Select-Object -First 1
            $kept = $patch.Body.keyCredentials | Where-Object { $_.keyId -eq 'other-key' }
            $kept.customKeyIdentifier | Should -Be 'QUJDRA=='
            $kept.key | Should -Be 'b3RoZXI='
            $kept.endDateTime | Should -Be '2027-01-01T00:00:00Z'
        }
    }

    It 'sends the certificates it keeps back exactly as Microsoft Graph returned them, when retiring others' {
        InModuleScope JIM {
            Invoke-JIMEntraIdConnectorChange -Connection $script:Connection -Change ([PSCustomObject]@{ Action = 'RetireCertificates'; Target = 'x'; Arguments = @{ KeyIds = @('cert-1') } })

            $patch = $script:GraphCalls | Where-Object Method -eq 'Patch' | Select-Object -First 1
            ($patch.Body.keyCredentials | Where-Object { $_.keyId -eq 'other-key' }).customKeyIdentifier | Should -Be 'QUJDRA=='
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
