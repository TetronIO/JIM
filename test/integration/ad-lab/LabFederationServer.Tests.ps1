# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

#Requires -Modules Pester

<#
.SYNOPSIS
    Pester tests for the pure functions in LabFederationServer.psm1, the AD FS server of the Active Directory lab.

.DESCRIPTION
    The AD FS server is built on Windows with Hyper-V, so its build cannot run in CI. What can be decided without
    Hyper-V or the ADFS module is a pure function and is proven here on any platform: the names the server and its
    service account take, the settings mapping, the arguments each guest phase receives, and what the Verify phase
    accepts as a working federation service.
#>

BeforeAll {
    Import-Module (Join-Path $PSScriptRoot 'guest' 'LabFederationServer.psm1') -Force

    $script:Info = Get-LabFederationInfo -Domain 'PANOPLY.LOCAL' -NetBiosName 'PANOPLY' -ServerName 'adfs1' -ServiceName 'adfs'

    function Get-TestDiscoveryDocument {
        # The members of /adfs/.well-known/openid-configuration the lab reads, as AD FS on Windows Server 2025
        # publishes them, plus a PKCE method list that AD FS itself leaves out (some providers publish one).
        return [pscustomobject][ordered]@{
            issuer                           = 'https://adfs.panoply.local/adfs'
            authorization_endpoint           = 'https://adfs.panoply.local/adfs/oauth2/authorize/'
            token_endpoint                   = 'https://adfs.panoply.local/adfs/oauth2/token/'
            jwks_uri                         = 'https://adfs.panoply.local/adfs/discovery/keys'
            end_session_endpoint             = 'https://adfs.panoply.local/adfs/oauth2/logout'
            access_token_issuer              = 'http://adfs.panoply.local/adfs/services/trust'
            code_challenge_methods_supported = @('plain', 'S256')
        }
    }
}

Describe 'Get-LabFederationInfo' {
    It 'derives the server and service names from the domain' {
        $script:Info.DnsDomain | Should -Be 'panoply.local'
        $script:Info.NetBiosName | Should -Be 'PANOPLY'
        $script:Info.ComputerName | Should -Be 'adfs1'
        $script:Info.Fqdn | Should -Be 'adfs1.panoply.local'
        $script:Info.ServiceFqdn | Should -Be 'adfs.panoply.local'
        $script:Info.ServiceUrl | Should -Be 'https://adfs.panoply.local/adfs'
    }

    It 'names the group managed service account after the service, in the forms AD and AD FS want' {
        $script:Info.GmsaName | Should -Be 'gmsa-adfs'
        $script:Info.GmsaAccount | Should -Be 'PANOPLY\gmsa-adfs$'
        $script:Info.ComputerAccount | Should -Be 'adfs1$'
    }

    It 'lower-cases the names and upper-cases the NetBIOS name, whatever it is given' {
        $info = Get-LabFederationInfo -Domain 'Panoply.Local' -NetBiosName 'panoply' -ServerName 'ADFS1' -ServiceName 'ADFS'
        $info.Fqdn | Should -Be 'adfs1.panoply.local'
        $info.ServiceFqdn | Should -Be 'adfs.panoply.local'
        $info.NetBiosName | Should -Be 'PANOPLY'
    }

    It 'refuses a service name equal to the computer name, whose HOST SPN would claim the service' {
        { Get-LabFederationInfo -Domain 'PANOPLY.LOCAL' -NetBiosName 'PANOPLY' -ServerName 'adfs' -ServiceName 'ADFS' } |
            Should -Throw '*SPN*'
    }

    It 'refuses a service name whose account name would pass the 15-character limit' {
        { Get-LabFederationInfo -Domain 'PANOPLY.LOCAL' -NetBiosName 'PANOPLY' -ServerName 'adfs1' -ServiceName 'federation1' } |
            Should -Throw '*15*'
    }

    It 'refuses names that are not single DNS labels' {
        { Get-LabFederationInfo -Domain 'PANOPLY.LOCAL' -NetBiosName 'PANOPLY' -ServerName 'adfs1' -ServiceName 'sts.panoply' } | Should -Throw
        { Get-LabFederationInfo -Domain 'PANOPLY.LOCAL' -NetBiosName 'PANOPLY' -ServerName 'adfs_1' -ServiceName 'adfs' } | Should -Throw
    }
}

Describe 'Get-LabFederationCertificateName' {
    It 'names the federation service first, then the server, which is what AD FS binds and browsers check' {
        Get-LabFederationCertificateName -Info $script:Info | Should -Be @('adfs.panoply.local', 'adfs1.panoply.local')
    }
}

Describe 'ConvertFrom-LabFederationSetting' {
    BeforeAll {
        $script:Settings = @'
{
  "switchName": "Lab",
  "domainControllers": {
    "dc-primary": { "domain": "PANOPLY.LOCAL", "ipAddress": "10.99.0.11", "prefixLength": 24, "gateway": "10.99.0.1" }
  },
  "federationServers": {
    "adfs-primary": { "domainController": "dc-primary", "ipAddress": "10.99.0.21", "prefixLength": 24, "gateway": "10.99.0.1" }
  }
}
'@ | ConvertFrom-Json
    }

    It 'takes the domain and the DNS server from the domain controller it joins' {
        $server = ConvertFrom-LabFederationSetting -Settings $script:Settings -Name 'adfs-primary'
        $server.Name | Should -Be 'adfs-primary'
        $server.DomainControllerName | Should -Be 'dc-primary'
        $server.Domain | Should -Be 'PANOPLY.LOCAL'
        $server.DnsServer | Should -Be '10.99.0.11'
        $server.IPAddress | Should -Be '10.99.0.21'
        $server.PrefixLength | Should -Be 24
        $server.Gateway | Should -Be '10.99.0.1'
    }

    It 'defaults the computer and service names when the settings leave them out' {
        $server = ConvertFrom-LabFederationSetting -Settings $script:Settings -Name 'adfs-primary'
        $server.ComputerName | Should -Be 'adfs1'
        $server.ServiceName | Should -Be 'adfs'
    }

    It 'takes the computer and service names from the settings when given' {
        $settings = $script:Settings | ConvertTo-Json -Depth 5 | ConvertFrom-Json
        $settings.federationServers.'adfs-primary' | Add-Member -NotePropertyName computerName -NotePropertyValue 'fs1'
        $settings.federationServers.'adfs-primary' | Add-Member -NotePropertyName serviceName -NotePropertyValue 'sts'
        $server = ConvertFrom-LabFederationSetting -Settings $settings -Name 'adfs-primary'
        $server.ComputerName | Should -Be 'fs1'
        $server.ServiceName | Should -Be 'sts'
    }

    It 'names what is missing: the server, or the domain controller it points at' {
        { ConvertFrom-LabFederationSetting -Settings $script:Settings -Name 'adfs-other' } | Should -Throw "*'adfs-other'*"
        $settings = $script:Settings | ConvertTo-Json -Depth 5 | ConvertFrom-Json
        $settings.federationServers.'adfs-primary'.domainController = 'dc-nowhere'
        { ConvertFrom-LabFederationSetting -Settings $settings -Name 'adfs-primary' } | Should -Throw "*'dc-nowhere'*"
    }
}

Describe 'Get-LabFederationPhaseArgument' {
    BeforeAll {
        # A SecureString without ConvertTo-SecureString -AsPlainText, which the script analyser rightly dislikes.
        $script:Secret = (New-Object System.Net.NetworkCredential('', 'not-a-real-password')).SecurePassword
        $script:PhaseSettings = @{
            ComputerName          = 'adfs1'
            IPAddress             = '10.99.0.21'
            PrefixLength          = 24
            Gateway               = '10.99.0.1'
            DnsServer             = '10.99.0.11'
            Domain                = 'PANOPLY.LOCAL'
            NetBiosName           = 'PANOPLY'
            ServiceName           = 'adfs'
            AdministratorPassword = $script:Secret
            VmName                = 'adfs-primary'
            LabRoot               = 'C:\jim-ad-lab'
        }
    }

    It 'gives License nothing but the phase' {
        @((Get-LabFederationPhaseArgument -Phase License -Settings $script:PhaseSettings).Keys) | Should -Be @('Phase')
    }

    It 'gives Prepare the network settings, the domain controller as resolver, and no secret' {
        $arguments = Get-LabFederationPhaseArgument -Phase Prepare -Settings $script:PhaseSettings
        @($arguments.Keys) | Should -Be @('Phase', 'ComputerName', 'IPAddress', 'PrefixLength', 'Gateway', 'DnsServer')
        $arguments.DnsServer | Should -Be '10.99.0.11'
    }

    It 'gives the Administrator password only to the phases that act as the domain Administrator' {
        foreach ($phase in 'License', 'Prepare', 'Verify') {
            (Get-LabFederationPhaseArgument -Phase $phase -Settings $script:PhaseSettings).Contains('AdministratorPassword') | Should -BeFalse
        }
        foreach ($phase in 'Join', 'Farm') {
            (Get-LabFederationPhaseArgument -Phase $phase -Settings $script:PhaseSettings).AdministratorPassword | Should -BeOfType [securestring]
        }
    }

    It 'gives Farm and Verify the names they build and check' {
        foreach ($phase in 'Farm', 'Verify') {
            $arguments = Get-LabFederationPhaseArgument -Phase $phase -Settings $script:PhaseSettings
            foreach ($name in 'Domain', 'NetBiosName', 'ComputerName', 'ServiceName', 'VmName', 'LabRoot') {
                $arguments[$name] | Should -Be $script:PhaseSettings[$name]
            }
        }
    }

    It 'names the setting a phase is missing' {
        $partial = @{} + $script:PhaseSettings
        $partial.Remove('DnsServer')
        { Get-LabFederationPhaseArgument -Phase Prepare -Settings $partial } | Should -Throw "*'DnsServer'*"
    }

    It 'refuses a phase the guest script does not have' {
        { Get-LabFederationPhaseArgument -Phase Promote -Settings $script:PhaseSettings } | Should -Throw
    }
}

Describe 'Test-LabFederationDiscovery' {
    It 'accepts the discovery document of a working federation service' {
        @(Test-LabFederationDiscovery -Document (Get-TestDiscoveryDocument) -ServiceFqdn 'adfs.panoply.local') | Should -BeNullOrEmpty
    }

    It 'names an issuer that is not the federation service' {
        $document = Get-TestDiscoveryDocument
        $document.issuer = 'https://adfs1.panoply.local/adfs'
        @(Test-LabFederationDiscovery -Document $document -ServiceFqdn 'adfs.panoply.local') | Should -Match 'issuer'
    }

    It 'names an endpoint that is not on the federation service over HTTPS' {
        $document = Get-TestDiscoveryDocument
        $document.token_endpoint = 'http://adfs.panoply.local/adfs/oauth2/token/'
        @(Test-LabFederationDiscovery -Document $document -ServiceFqdn 'adfs.panoply.local') | Should -Match 'token_endpoint'
    }

    It 'names a missing endpoint' {
        $document = Get-TestDiscoveryDocument
        $document.PSObject.Properties.Remove('jwks_uri')
        @(Test-LabFederationDiscovery -Document $document -ServiceFqdn 'adfs.panoply.local') | Should -Match 'jwks_uri'
    }

    It 'refuses a list of PKCE methods without S256, which Connect-JIM uses' {
        $document = Get-TestDiscoveryDocument
        $document.code_challenge_methods_supported = @('plain')
        @(Test-LabFederationDiscovery -Document $document -ServiceFqdn 'adfs.panoply.local') | Should -Match 'S256'
    }

    It 'accepts a document that lists no PKCE methods at all, as AD FS on Windows Server 2025 publishes it' {
        # AD FS has supported PKCE since 2019 but does not advertise it; Connect-JIM's sign-in proves it instead.
        $document = Get-TestDiscoveryDocument
        $document.PSObject.Properties.Remove('code_challenge_methods_supported')
        @(Test-LabFederationDiscovery -Document $document -ServiceFqdn 'adfs.panoply.local') | Should -BeNullOrEmpty
    }

    It 'compares host names without regard to case' {
        $document = Get-TestDiscoveryDocument
        $document.issuer = 'https://ADFS.panoply.local/adfs'
        @(Test-LabFederationDiscovery -Document $document -ServiceFqdn 'adfs.panoply.local') | Should -BeNullOrEmpty
    }
}

Describe 'Test-LabFederationKeySet' {
    It 'accepts a key set holding an RSA signing key with a key ID' {
        $keys = [pscustomobject]@{ keys = @([pscustomobject]@{ kty = 'RSA'; use = 'sig'; kid = 'abc'; n = 'xyz'; e = 'AQAB' }) }
        @(Test-LabFederationKeySet -Document $keys) | Should -BeNullOrEmpty
    }

    It 'refuses an empty key set, against which no token could be validated' {
        @(Test-LabFederationKeySet -Document ([pscustomobject]@{ keys = @() })) | Should -Match 'no signing key'
    }

    It 'refuses a key set whose keys have no key ID' {
        $keys = [pscustomobject]@{ keys = @([pscustomobject]@{ kty = 'RSA'; use = 'sig'; n = 'xyz'; e = 'AQAB' }) }
        @(Test-LabFederationKeySet -Document $keys) | Should -Match 'no signing key'
    }
}

Describe 'Test-LabModuleFile' {
    It 'counts <Path> as part of the module' -TestCases @(
        @{ Path = 'JIM.psd1' }, @{ Path = 'JIM.psm1' },
        @{ Path = 'Public/Connection/Connect-JIM.ps1' }, @{ Path = 'Private/AdfsRegistration.ps1' },
        @{ Path = 'Public\IdentityProviders\Register-JIMIdentityProvider.ps1' }
    ) {
        Test-LabModuleFile -RelativePath $Path | Should -BeTrue
    }

    It 'leaves out <Path>' -TestCases @(
        @{ Path = 'Tests/IdentityProviderRegistration.Tests.ps1' }, @{ Path = 'CLAUDE.md' },
        @{ Path = 'Public/notes.md' }, @{ Path = 'public/Connect-JIM.ps1' }, @{ Path = 'Other/Thing.ps1' },
        @{ Path = 'Public/../Tests/Escape.ps1' }, @{ Path = 'jim.psd1' }
    ) {
        Test-LabModuleFile -RelativePath $Path | Should -BeFalse
    }
}

Describe 'Get-LabModuleDigest' {
    BeforeAll {
        $script:Files = @(
            [pscustomobject]@{ Path = 'JIM.psd1'; Text = "@{`n    RootModule = 'JIM.psm1'`n}`n" },
            [pscustomobject]@{ Path = 'Public/Connect-JIM.ps1'; Text = "function Connect-JIM {`n}`n" }
        )
        $script:Digest = Get-LabModuleDigest -Entry $script:Files
    }

    It 'is 64 lower-case hex characters' {
        $script:Digest | Should -Match '^[0-9a-f]{64}$'
    }

    It 'is the same with CRLF line endings, as a Windows checkout has them' {
        $crlf = $script:Files | ForEach-Object { [pscustomobject]@{ Path = $_.Path; Text = $_.Text.Replace("`n", "`r`n") } }
        Get-LabModuleDigest -Entry $crlf | Should -Be $script:Digest
    }

    It 'is the same with backslashes in the paths, and in any order' {
        $other = @(
            [pscustomobject]@{ Path = 'Public\Connect-JIM.ps1'; Text = $script:Files[1].Text },
            $script:Files[0]
        )
        Get-LabModuleDigest -Entry $other | Should -Be $script:Digest
    }

    It 'changes when a file is edited' {
        $edited = @($script:Files[0], [pscustomobject]@{ Path = 'Public/Connect-JIM.ps1'; Text = "function Connect-JIM {`n    'changed'`n}`n" })
        Get-LabModuleDigest -Entry $edited | Should -Not -Be $script:Digest
    }

    It 'changes when a file is added, removed or renamed' {
        $added = $script:Files + @([pscustomobject]@{ Path = 'Private/New.ps1'; Text = '' })
        Get-LabModuleDigest -Entry $added | Should -Not -Be $script:Digest
        Get-LabModuleDigest -Entry @($script:Files[0]) | Should -Not -Be $script:Digest
        $renamed = @($script:Files[0], [pscustomobject]@{ Path = 'Public/Connect-JIM2.ps1'; Text = $script:Files[1].Text })
        Get-LabModuleDigest -Entry $renamed | Should -Not -Be $script:Digest
    }

    It 'gives a digest for no files at all' {
        Get-LabModuleDigest -Entry @() | Should -Match '^[0-9a-f]{64}$'
    }
}

Describe 'ConvertTo-LabIdentityProviderRegistration' {
    BeforeAll {
        function New-TestRegistration {
            param([securestring]$Secret = (ConvertTo-SecureString 'generated-secret' -AsPlainText -Force))
            [pscustomobject]@{
                Provider = 'ADFS'; Authority = 'https://adfs.panoply.local/adfs'
                ClientId = '11111111-1111-1111-1111-111111111111'; ClientSecret = $Secret
                PublicClientId = '22222222-2222-2222-2222-222222222222'; ApiScope = 'api://11111111-1111-1111-1111-111111111111'
                ClaimType = 'oid'; MvAttribute = 'Object GUID'; Changes = @('Create the Application Group', 'Add the portal')
            }
        }
    }

    It 'gives the runner what JIM needs, with the client secret in plain text for it to mask' {
        $result = ConvertTo-LabIdentityProviderRegistration -Registration (New-TestRegistration) -ModuleDigest ('a' * 64)
        $result.authority | Should -Be 'https://adfs.panoply.local/adfs'
        $result.clientId | Should -Be '11111111-1111-1111-1111-111111111111'
        $result.clientSecret | Should -Be 'generated-secret'
        $result.publicClientId | Should -Be '22222222-2222-2222-2222-222222222222'
        $result.apiScope | Should -Be 'api://11111111-1111-1111-1111-111111111111'
        $result.claimType | Should -Be 'oid'
        $result.mvAttribute | Should -Be 'Object GUID'
        @($result.changes) | Should -HaveCount 2
        $result.moduleDigest | Should -Be ('a' * 64)
    }

    It 'refuses a registration that returned no client secret, which JIM cannot sign anyone in without' {
        $registration = New-TestRegistration
        $registration.ClientSecret = $null
        { ConvertTo-LabIdentityProviderRegistration -Registration $registration -ModuleDigest ('a' * 64) } | Should -Throw '*no client secret*'
    }

    It 'refuses a registration with no client ID' {
        $registration = New-TestRegistration
        $registration.ClientId = ''
        { ConvertTo-LabIdentityProviderRegistration -Registration $registration -ModuleDigest ('a' * 64) } | Should -Throw '*ClientId*'
    }
}

Describe 'Read-LabModuleFile' {
    BeforeAll {
        $script:Root = Join-Path ([System.IO.Path]::GetTempPath()) "jim-lab-module-$([guid]::NewGuid().ToString('N'))"
        foreach ($relative in 'JIM.psd1', 'Public/Connection/Connect-JIM.ps1', 'Tests/Connect.Tests.ps1', 'CLAUDE.md') {
            $path = Join-Path $script:Root $relative
            $null = New-Item -ItemType Directory -Path (Split-Path -Parent $path) -Force
            [System.IO.File]::WriteAllText($path, "# $relative`r`n")
        }
    }

    AfterAll {
        Remove-Item -LiteralPath $script:Root -Recurse -Force -ErrorAction SilentlyContinue
    }

    It 'reads the module files under the folder, with forward-slash paths, and nothing else' {
        $entries = @(Read-LabModuleFile -Root $script:Root | Sort-Object Path)
        @($entries.Path) | Should -Be @('JIM.psd1', 'Public/Connection/Connect-JIM.ps1')
        $entries[0].Text | Should -Be "# JIM.psd1`r`n"
    }

    It 'reads the real module, whose digest is that of the checkout' {
        $module = Join-Path $PSScriptRoot '..' '..' '..' 'src' 'JIM.PowerShell'
        $entries = @(Read-LabModuleFile -Root $module)
        @($entries.Path) | Should -Contain 'Public/IdentityProviders/Register-JIMIdentityProvider.ps1'
        @($entries.Path | Where-Object { $_ -like 'Tests/*' }) | Should -BeNullOrEmpty
        Get-LabModuleDigest -Entry $entries | Should -Match '^[0-9a-f]{64}$'
    }

    It 'fails for a folder that does not exist' {
        { Read-LabModuleFile -Root (Join-Path $script:Root 'missing') } | Should -Throw '*does not exist*'
    }
}
