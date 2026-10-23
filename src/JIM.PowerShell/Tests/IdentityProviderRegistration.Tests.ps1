# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

#Requires -Modules Pester

<#
.SYNOPSIS
    Pester tests for registering JIM with its identity provider (#2364): Register-JIMIdentityProvider -Provider ADFS,
    and the plan it makes from what AD FS already holds. AD FS itself is never called here; the lab runs the command
    against a real federation server (#2355).
#>

BeforeAll {
    $ModulePath = Join-Path $PSScriptRoot '..' 'JIM.psd1'
    Get-Module JIM -ErrorAction SilentlyContinue | Remove-Module -Force
    Import-Module $ModulePath -Force
}

AfterAll {
    Get-Module JIM -ErrorAction SilentlyContinue | Remove-Module -Force
}

Describe 'Get-JIMAdfsRegistrationPlan' {

    BeforeAll {
        # What a complete, correct registration looks like when AD FS is read back, for the plan to find nothing to do.
        $script:NewCompleteState = {
            InModuleScope JIM {
                $rules = Get-JIMAdfsIssuanceTransformRule
                [PSCustomObject]@{
                    HostName           = 'adfs.example.com'
                    Identifier         = 'http://adfs.example.com/adfs/services/trust'
                    GroupExists        = $true
                    ServerApplications = @([PSCustomObject]@{
                        Name = 'JIM Identity Management - Portal'; Identifier = '11111111-1111-1111-1111-111111111111'
                        RedirectUri = @('https://jim.example.com/signin-oidc', 'https://jim.example.com/signout-callback-oidc')
                    })
                    NativeApplications = @([PSCustomObject]@{
                        Name = 'JIM Identity Management - PowerShell'; Identifier = '22222222-2222-2222-2222-222222222222'
                        RedirectUri = @(8400..8409 | ForEach-Object { "http://localhost:$_/callback/" })
                    })
                    WebApiApplications = @([PSCustomObject]@{
                        Name = 'JIM Identity Management - API'; Identifier = @('api://11111111-1111-1111-1111-111111111111')
                        AccessControlPolicyName = 'Permit everyone'; IssuanceTransformRules = $rules
                        IssueOAuthRefreshTokensTo = 'AllDevices'
                    })
                    Permissions        = @(
                        [PSCustomObject]@{
                            ObjectIdentifier = 'p1'; ClientRoleIdentifier = '11111111-1111-1111-1111-111111111111'
                            ServerRoleIdentifier = 'api://11111111-1111-1111-1111-111111111111'
                            ScopeNames = @('openid', 'profile', 'email', 'allatclaims')
                        },
                        [PSCustomObject]@{
                            ObjectIdentifier = 'p2'; ClientRoleIdentifier = '22222222-2222-2222-2222-222222222222'
                            ServerRoleIdentifier = 'api://11111111-1111-1111-1111-111111111111'
                            ScopeNames = @('openid', 'profile')
                        }
                    )
                }
            }
        }
    }

    It 'Creates everything when the Application Group does not exist' {
        InModuleScope JIM {
            $current = [PSCustomObject]@{
                HostName = 'adfs.example.com'; Identifier = 'http://adfs.example.com/adfs/services/trust'; GroupExists = $false
                ServerApplications = @(); NativeApplications = @(); WebApiApplications = @(); Permissions = @()
            }

            $plan = Get-JIMAdfsRegistrationPlan -Url 'https://jim.example.com/' -Name 'JIM Identity Management' -Current $current

            @($plan.Changes.Action) | Should -Be @(
                'NewApplicationGroup', 'NewServerApplication', 'NewNativeApplication', 'NewWebApiApplication',
                'GrantPermission', 'GrantPermission')
            { [guid]::Parse($plan.ClientId) } | Should -Not -Throw
            { [guid]::Parse($plan.PublicClientId) } | Should -Not -Throw
            $plan.ClientId | Should -Not -Be $plan.PublicClientId
            $plan.ApiIdentifier | Should -Be "api://$($plan.ClientId)"

            $server = $plan.Changes | Where-Object Action -eq 'NewServerApplication'
            $server.Arguments.Identifier | Should -Be $plan.ClientId
            $server.Arguments.RedirectUri | Should -Be @('https://jim.example.com/signin-oidc', 'https://jim.example.com/signout-callback-oidc')

            $native = $plan.Changes | Where-Object Action -eq 'NewNativeApplication'
            $native.Arguments.RedirectUri | Should -HaveCount 10
            $native.Arguments.RedirectUri[0] | Should -Be 'http://localhost:8400/callback/'
            $native.Arguments.RedirectUri[9] | Should -Be 'http://localhost:8409/callback/'

            $api = $plan.Changes | Where-Object Action -eq 'NewWebApiApplication'
            $api.Arguments.Identifier | Should -Be @($plan.ApiIdentifier)
            $api.Arguments.AccessControlPolicyName | Should -Be 'Permit everyone'
            $api.Arguments.IssueOAuthRefreshTokensTo | Should -Be 'AllDevices'
            $api.Arguments.IssuanceTransformRules | Should -Match 'objectGUID'
        }
    }

    It 'Gives the portal and the PowerShell module only the scopes each needs' {
        InModuleScope JIM {
            $current = [PSCustomObject]@{
                HostName = 'adfs.example.com'; Identifier = 'http://adfs.example.com/adfs/services/trust'; GroupExists = $false
                ServerApplications = @(); NativeApplications = @(); WebApiApplications = @(); Permissions = @()
            }

            $plan = Get-JIMAdfsRegistrationPlan -Url 'https://jim.example.com' -Name 'JIM Identity Management' -Current $current

            $grants = @($plan.Changes | Where-Object Action -eq 'GrantPermission')
            $portal = $grants | Where-Object { $_.Arguments.ClientRoleIdentifier -eq $plan.ClientId }
            $module = $grants | Where-Object { $_.Arguments.ClientRoleIdentifier -eq $plan.PublicClientId }
            $portal.Arguments.ScopeNames | Should -Be @('openid', 'profile', 'email', 'allatclaims')
            # AD FS has no offline_access scope; the API's IssueOAuthRefreshTokensTo gives the module its refresh token.
            $module.Arguments.ScopeNames | Should -Be @('openid', 'profile')
            $grants.Arguments.ServerRoleIdentifier | Sort-Object -Unique | Should -Be $plan.ApiIdentifier
        }
    }

    It 'Finds nothing to do when the registration is already right' {
        $current = & $script:NewCompleteState

        InModuleScope JIM -Parameters @{ Current = $current } {
            param($Current)
            $plan = Get-JIMAdfsRegistrationPlan -Url 'https://jim.example.com' -Name 'JIM Identity Management' -Current $Current

            $plan.Changes | Should -BeNullOrEmpty
            $plan.ClientId | Should -Be '11111111-1111-1111-1111-111111111111'
            $plan.PublicClientId | Should -Be '22222222-2222-2222-2222-222222222222'
            $plan.ApiIdentifier | Should -Be 'api://11111111-1111-1111-1111-111111111111'
        }
    }

    It 'Ignores the order of redirect URIs and scopes, and the layout of the claim rules' {
        $current = & $script:NewCompleteState
        $current.ServerApplications[0].RedirectUri = @('https://jim.example.com/signout-callback-oidc', 'https://jim.example.com/signin-oidc')
        $current.Permissions[0].ScopeNames = @('allatclaims', 'email', 'profile', 'openid')
        $current.WebApiApplications[0].IssuanceTransformRules = "`r`n  " + ($current.WebApiApplications[0].IssuanceTransformRules -replace ' ', "`r`n ") + "`r`n"

        InModuleScope JIM -Parameters @{ Current = $current } {
            param($Current)
            (Get-JIMAdfsRegistrationPlan -Url 'https://jim.example.com' -Name 'JIM Identity Management' -Current $Current).Changes |
                Should -BeNullOrEmpty
        }
    }

    It 'Corrects drift without recreating anything' {
        $current = & $script:NewCompleteState
        $current.ServerApplications[0].RedirectUri = @('https://old-jim.example.com/signin-oidc')
        $current.NativeApplications[0].RedirectUri = @('http://localhost:8400/callback/')
        $current.WebApiApplications[0].AccessControlPolicyName = 'Permit specific group'
        $current.WebApiApplications[0].IssueOAuthRefreshTokensTo = 'NoDevice'
        $current.WebApiApplications[0].IssuanceTransformRules = 'c:[Type == "upn"] => issue(Type = "sub", Value = c.Value);'
        $current.Permissions[0].ScopeNames = @('openid', 'profile', 'email')

        InModuleScope JIM -Parameters @{ Current = $current } {
            param($Current)
            $plan = Get-JIMAdfsRegistrationPlan -Url 'https://jim.example.com' -Name 'JIM Identity Management' -Current $Current

            @($plan.Changes.Action) | Should -Be @(
                'SetServerApplication', 'SetNativeApplication', 'SetWebApiApplication', 'SetPermission')
            $plan.Changes[0].Arguments.TargetIdentifier | Should -Be '11111111-1111-1111-1111-111111111111'
            $plan.Changes[0].Arguments.RedirectUri | Should -Contain 'https://jim.example.com/signin-oidc'
            $plan.Changes[2].Arguments.TargetName | Should -Be 'JIM Identity Management - API'
            $plan.Changes[2].Arguments.AccessControlPolicyName | Should -Be 'Permit everyone'
            $plan.Changes[2].Arguments.IssueOAuthRefreshTokensTo | Should -Be 'AllDevices'
            $plan.Changes[2].Arguments.IssuanceTransformRules | Should -Match 'objectGUID'
            $plan.Changes[3].Arguments.TargetIdentifier | Should -Be 'p1'
            $plan.Changes[3].Arguments.ScopeNames | Should -Be @('openid', 'profile', 'email', 'allatclaims')
        }
    }

    It 'Revokes the API from any client other than JIM''s two' {
        $current = & $script:NewCompleteState
        $current.Permissions += [PSCustomObject]@{
            ObjectIdentifier = 'p3'; ClientRoleIdentifier = '33333333-3333-3333-3333-333333333333'
            ServerRoleIdentifier = 'api://11111111-1111-1111-1111-111111111111'; ScopeNames = @('openid')
        }

        InModuleScope JIM -Parameters @{ Current = $current } {
            param($Current)
            $plan = Get-JIMAdfsRegistrationPlan -Url 'https://jim.example.com' -Name 'JIM Identity Management' -Current $Current

            @($plan.Changes.Action) | Should -Be @('RevokePermission')
            $plan.Changes[0].Arguments.TargetIdentifier | Should -Be 'p3'
        }
    }

    It 'Adopts the one application of each kind a hand-made group holds, whatever it is called' {
        $current = & $script:NewCompleteState
        $current.ServerApplications[0].Name = 'JIM Identity Management - Server application - Web API'
        $current.WebApiApplications[0].Name = 'JIM Identity Management - Web API'

        InModuleScope JIM -Parameters @{ Current = $current } {
            param($Current)
            $plan = Get-JIMAdfsRegistrationPlan -Url 'https://jim.example.com' -Name 'JIM Identity Management' -Current $Current

            $plan.Changes | Should -BeNullOrEmpty
            $plan.ClientId | Should -Be '11111111-1111-1111-1111-111111111111'
        }
    }

    It 'Refuses to guess when a group holds two applications of a kind and neither has the expected name' {
        $current = & $script:NewCompleteState
        $current.ServerApplications = @(
            [PSCustomObject]@{ Name = 'First'; Identifier = 'a'; RedirectUri = @() },
            [PSCustomObject]@{ Name = 'Second'; Identifier = 'b'; RedirectUri = @() })

        InModuleScope JIM -Parameters @{ Current = $current } {
            param($Current)
            { Get-JIMAdfsRegistrationPlan -Url 'https://jim.example.com' -Name 'JIM Identity Management' -Current $Current } |
                Should -Throw '*First*Second*'
        }
    }

    It 'Moves the API to the identifier JIM expects and grants it afresh' {
        $current = & $script:NewCompleteState
        $current.WebApiApplications[0].Identifier = @('api://jim')
        foreach ($permission in $current.Permissions) { $permission.ServerRoleIdentifier = 'api://jim' }

        InModuleScope JIM -Parameters @{ Current = $current } {
            param($Current)
            $plan = Get-JIMAdfsRegistrationPlan -Url 'https://jim.example.com' -Name 'JIM Identity Management' -Current $Current

            @($plan.Changes.Action) | Should -Be @(
                'SetWebApiApplication', 'RevokePermission', 'RevokePermission', 'GrantPermission', 'GrantPermission')
            $plan.Changes[0].Arguments.Identifier | Should -Be @('api://11111111-1111-1111-1111-111111111111')
        }
    }

    It 'Plans a new client secret only when asked' {
        $current = & $script:NewCompleteState

        InModuleScope JIM -Parameters @{ Current = $current } {
            param($Current)
            $plan = Get-JIMAdfsRegistrationPlan -Url 'https://jim.example.com' -Name 'JIM Identity Management' -Current $Current -ResetClientSecret

            @($plan.Changes.Action) | Should -Be @('ResetClientSecret')
            $plan.Changes[0].Arguments.TargetIdentifier | Should -Be '11111111-1111-1111-1111-111111111111'
        }
    }

    It 'Keeps JIM''s port when it is not 443' {
        InModuleScope JIM {
            $current = [PSCustomObject]@{
                HostName = 'adfs.example.com'; Identifier = 'http://adfs.example.com/adfs/services/trust'; GroupExists = $false
                ServerApplications = @(); NativeApplications = @(); WebApiApplications = @(); Permissions = @()
            }

            $plan = Get-JIMAdfsRegistrationPlan -Url 'https://jim.example.com:8443' -Name 'JIM' -Current $current

            ($plan.Changes | Where-Object Action -eq 'NewServerApplication').Arguments.RedirectUri |
                Should -Be @('https://jim.example.com:8443/signin-oidc', 'https://jim.example.com:8443/signout-callback-oidc')
        }
    }
}

Describe 'Register-JIMIdentityProvider -Provider ADFS' {

    BeforeEach {
        InModuleScope JIM {
            # PSSession has no public constructor, and the command takes nothing else.
            $script:FakeSession = [System.Runtime.CompilerServices.RuntimeHelpers]::GetUninitializedObject([System.Management.Automation.Runspaces.PSSession])
            Mock New-JIMAdfsSession { $script:FakeSession }
            Mock Remove-PSSession { }
            Mock Get-JIMAdfsRegistrationState {
                [PSCustomObject]@{
                    HostName = 'adfs.example.com'; Identifier = 'http://adfs.example.com/adfs/services/trust'; GroupExists = $false
                    ServerApplications = @(); NativeApplications = @(); WebApiApplications = @(); Permissions = @()
                }
            }
            Mock Invoke-JIMAdfsRegistrationChange {
                if ($Change.Action -in 'NewServerApplication', 'ResetClientSecret') { 'generated-secret' }
            }
        }
    }

    It 'Rejects a JIM URL that is not absolute HTTPS' {
        { Register-JIMIdentityProvider -Provider ADFS -Url 'jim.example.com' -ErrorAction Stop } | Should -Throw '*https*'
        { Register-JIMIdentityProvider -Provider ADFS -Url 'http://jim.example.com' -ErrorAction Stop } | Should -Throw '*https*'
    }

    It 'Registers JIM and returns what JIM needs, with the secret kept secure' {
        InModuleScope JIM {
            $result = Register-JIMIdentityProvider -Provider ADFS -Url 'https://jim.example.com' -Confirm:$false

            $result.PSObject.TypeNames | Should -Contain 'JIM.IdentityProviderRegistration'
            $result.Provider | Should -Be 'ADFS'
            $result.Authority | Should -Be 'https://adfs.example.com/adfs'
            $result.ValidIssuers | Should -Be @('https://adfs.example.com/adfs', 'http://adfs.example.com/adfs/services/trust')
            $result.ApiScope | Should -Be "api://$($result.ClientId)"
            $result.ClaimType | Should -Be 'oid'
            $result.MvAttribute | Should -Be 'Object GUID'
            $result.ClientSecret | Should -BeOfType [securestring]
            [System.Net.NetworkCredential]::new('', $result.ClientSecret).Password | Should -Be 'generated-secret'
            $result.Changes | Should -HaveCount 6
            Should -Invoke Invoke-JIMAdfsRegistrationChange -Times 6
        }
    }

    It 'Changes nothing and returns no secret when the registration is already right' {
        InModuleScope JIM {
            Mock Get-JIMAdfsRegistrationPlan {
                [PSCustomObject]@{ ClientId = 'c'; PublicClientId = 'n'; ApiIdentifier = 'api://c'; Changes = @() }
            }

            $result = Register-JIMIdentityProvider -Provider ADFS -Url 'https://jim.example.com'

            $result.ClientSecret | Should -BeNullOrEmpty
            $result.Changes | Should -BeNullOrEmpty
            Should -Invoke Invoke-JIMAdfsRegistrationChange -Times 0
        }
    }

    It 'Reports drift with -WhatIf and changes nothing' {
        InModuleScope JIM {
            $result = Register-JIMIdentityProvider -Provider ADFS -Url 'https://jim.example.com' -WhatIf

            Should -Invoke Invoke-JIMAdfsRegistrationChange -Times 0
            $result | Should -BeNullOrEmpty
        }
    }

    It 'Uses the session it is given and leaves it open' {
        InModuleScope JIM {
            $given = [System.Runtime.CompilerServices.RuntimeHelpers]::GetUninitializedObject([System.Management.Automation.Runspaces.PSSession])

            $null = Register-JIMIdentityProvider -Provider ADFS -Url 'https://jim.example.com' -Session $given -Confirm:$false

            Should -Invoke New-JIMAdfsSession -Times 0
            Should -Invoke Get-JIMAdfsRegistrationState -Times 1 -ParameterFilter { [object]::ReferenceEquals($Session, $given) }
            Should -Invoke Remove-PSSession -Times 0
        }
    }

    It 'Opens a local session when given none, and closes it even when AD FS fails' {
        InModuleScope JIM {
            Mock Invoke-JIMAdfsRegistrationChange { throw 'AD FS said no' }

            { Register-JIMIdentityProvider -Provider ADFS -Url 'https://jim.example.com' -Confirm:$false -ErrorAction Stop } |
                Should -Throw '*AD FS said no*'

            Should -Invoke New-JIMAdfsSession -Times 1
            Should -Invoke Remove-PSSession -Times 1
        }
    }

    It 'Says how to get a new secret when a later change fails after the secret was made' {
        InModuleScope JIM {
            Mock Invoke-JIMAdfsRegistrationChange {
                if ($Change.Action -eq 'NewServerApplication') { 'generated-secret' }
                if ($Change.Action -eq 'GrantPermission') { throw 'MSIS7628' }
            }

            Mock Write-Warning { }

            { Register-JIMIdentityProvider -Provider ADFS -Url 'https://jim.example.com' -Confirm:$false -ErrorAction Stop } |
                Should -Throw '*MSIS7628*'

            Should -Invoke Write-Warning -Times 1 -Exactly -ParameterFilter { $Message -match '-ResetClientSecret' }
        }
    }

    It 'Passes the Application Group name through' {
        InModuleScope JIM {
            $null = Register-JIMIdentityProvider -Provider ADFS -Url 'https://jim.example.com' -Name 'JIM Test' -Confirm:$false

            Should -Invoke Get-JIMAdfsRegistrationState -Times 1 -ParameterFilter { $Name -eq 'JIM Test' }
            Should -Invoke Invoke-JIMAdfsRegistrationChange -Times 1 -ParameterFilter {
                $Change.Action -eq 'NewApplicationGroup' -and $Change.Arguments.Name -eq 'JIM Test'
            }
        }
    }
}
