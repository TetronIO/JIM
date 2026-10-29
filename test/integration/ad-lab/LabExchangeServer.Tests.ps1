# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

#Requires -Modules Pester

<#
.SYNOPSIS
    Pester tests for the pure functions in LabExchangeServer.psm1, the Exchange servers of the Active Directory lab.

.DESCRIPTION
    The Exchange servers are built on Windows with Hyper-V, so their build cannot run in CI. What can be decided without
    Hyper-V or Exchange is a pure function and is proven here on any platform: the names a server takes, the settings
    mapping, the arguments each guest phase receives, the prerequisites and how they are recognised, Setup's
    arguments, and what the Verify phase accepts as a working server.
#>

BeforeAll {
    Import-Module (Join-Path $PSScriptRoot 'guest' 'LabExchangeServer.psm1') -Force

    $script:Info = Get-LabExchangeServerInfo -Domain 'PANOPLY.LOCAL' -NetBiosName 'PANOPLY' -ServerName 'ex1'
}

Describe 'Get-LabExchangeServerInfo' {
    It 'derives the server''s names from the domain' {
        $script:Info.DnsDomain | Should -Be 'panoply.local'
        $script:Info.NetBiosName | Should -Be 'PANOPLY'
        $script:Info.ComputerName | Should -Be 'ex1'
        $script:Info.Fqdn | Should -Be 'ex1.panoply.local'
        $script:Info.ComputerAccount | Should -Be 'ex1$'
        $script:Info.DomainDn | Should -Be 'DC=panoply,DC=local'
    }

    It 'gives the Remote PowerShell endpoint JIM will use over HTTPS, and the one the build manages the server through' {
        $script:Info.PowerShellUri | Should -Be 'https://ex1.panoply.local/PowerShell/'
        $script:Info.ManagementUri | Should -Be 'http://ex1.panoply.local/PowerShell/'
    }

    It 'names two mailbox databases, the second excluded from automatic distribution' {
        $script:Info.ProvisioningDatabase | Should -Be 'MDB01'
        $script:Info.ExcludedDatabase | Should -Be 'MDB02'
    }

    It 'puts GAL sync contacts in an OU of their own under Corp, and names JIM''s service account' {
        $script:Info.ContactOuName | Should -Be 'GALSync'
        $script:Info.ContactOuDn | Should -Be 'OU=GALSync,OU=Corp,DC=panoply,DC=local'
        $script:Info.ServiceAccountDn | Should -Be 'CN=svc-jim,OU=Services,DC=panoply,DC=local'
    }

    It 'lower-cases the names and upper-cases the NetBIOS name, whatever it is given' {
        $info = Get-LabExchangeServerInfo -Domain 'Resurgam.Local' -NetBiosName 'resurgam' -ServerName 'EX1'
        $info.Fqdn | Should -Be 'ex1.resurgam.local'
        $info.NetBiosName | Should -Be 'RESURGAM'
        $info.DomainDn | Should -Be 'DC=resurgam,DC=local'
    }

    It 'refuses names that are not single DNS labels' {
        { Get-LabExchangeServerInfo -Domain 'PANOPLY.LOCAL' -NetBiosName 'PANOPLY' -ServerName 'ex1.panoply' } | Should -Throw
        { Get-LabExchangeServerInfo -Domain 'PANOPLY.LOCAL' -NetBiosName 'PANOPLY' -ServerName 'ex_1' } | Should -Throw
    }
}

Describe 'Get-LabExchangeDatabasePath' {
    It 'keeps each database and its logs in a folder of its own' {
        $paths = Get-LabExchangeDatabasePath -Name 'MDB01'
        $paths.EdbFilePath | Should -Be 'C:\ExchangeDatabases\MDB01\MDB01.edb'
        $paths.LogFolderPath | Should -Be 'C:\ExchangeDatabases\MDB01\Logs'
    }
}

Describe 'ConvertFrom-LabExchangeSetting' {
    BeforeAll {
        $script:Settings = @'
{
  "switchName": "Lab",
  "exchangePrerequisiteDirectory": "C:\\media\\exchange-prerequisites",
  "directoryExtensions": [ { "product": "Exchange", "isoPath": "C:\\media\\ExchangeServerSE-x64.iso" } ],
  "domainControllers": {
    "dc-primary": { "domain": "PANOPLY.LOCAL", "ipAddress": "10.99.0.11", "prefixLength": 24, "gateway": "10.99.0.1" }
  },
  "exchangeServers": {
    "exchange-primary": { "domainController": "dc-primary", "ipAddress": "10.99.0.31", "prefixLength": 24, "gateway": "10.99.0.1" }
  }
}
'@ | ConvertFrom-Json
    }

    It 'takes the domain and the DNS server from the domain controller it joins' {
        $server = ConvertFrom-LabExchangeSetting -Settings $script:Settings -Name 'exchange-primary'
        $server.Name | Should -Be 'exchange-primary'
        $server.DomainControllerName | Should -Be 'dc-primary'
        $server.Domain | Should -Be 'PANOPLY.LOCAL'
        $server.DnsServer | Should -Be '10.99.0.11'
        $server.IPAddress | Should -Be '10.99.0.31'
        $server.PrefixLength | Should -Be 24
        $server.Gateway | Should -Be '10.99.0.1'
    }

    It 'installs from the media the forests were prepared with, and the prerequisites directory' {
        $server = ConvertFrom-LabExchangeSetting -Settings $script:Settings -Name 'exchange-primary'
        $server.ExchangeIsoPath | Should -Be 'C:\media\ExchangeServerSE-x64.iso'
        $server.PrerequisiteDirectory | Should -Be 'C:\media\exchange-prerequisites'
    }

    It 'defaults the computer name to ex1, and takes it from the settings when given' {
        (ConvertFrom-LabExchangeSetting -Settings $script:Settings -Name 'exchange-primary').ComputerName | Should -Be 'ex1'
        $settings = $script:Settings | ConvertTo-Json -Depth 5 | ConvertFrom-Json
        $settings.exchangeServers.'exchange-primary' | Add-Member -NotePropertyName computerName -NotePropertyValue 'mbx1'
        (ConvertFrom-LabExchangeSetting -Settings $settings -Name 'exchange-primary').ComputerName | Should -Be 'mbx1'
    }

    It 'names what is missing: the server, or the domain controller it points at' {
        { ConvertFrom-LabExchangeSetting -Settings $script:Settings -Name 'exchange-other' } | Should -Throw "*'exchange-other'*"
        $settings = $script:Settings | ConvertTo-Json -Depth 5 | ConvertFrom-Json
        $settings.exchangeServers.'exchange-primary'.domainController = 'dc-nowhere'
        { ConvertFrom-LabExchangeSetting -Settings $settings -Name 'exchange-primary' } | Should -Throw "*'dc-nowhere'*"
    }

    It 'refuses settings whose forests were not extended with Exchange, which a server cannot join' {
        $settings = $script:Settings | ConvertTo-Json -Depth 5 | ConvertFrom-Json
        $settings.directoryExtensions = @()
        { ConvertFrom-LabExchangeSetting -Settings $settings -Name 'exchange-primary' } | Should -Throw '*directoryExtensions*'
    }

    It 'refuses settings without the prerequisites directory' {
        $settings = $script:Settings | ConvertTo-Json -Depth 5 | ConvertFrom-Json
        $settings.PSObject.Properties.Remove('exchangePrerequisiteDirectory')
        { ConvertFrom-LabExchangeSetting -Settings $settings -Name 'exchange-primary' } | Should -Throw '*exchangePrerequisiteDirectory*'
    }
}

Describe 'Get-LabExchangePhaseArgument' {
    BeforeAll {
        # A SecureString without ConvertTo-SecureString -AsPlainText, which the script analyser rightly dislikes.
        $script:Secret = (New-Object System.Net.NetworkCredential('', 'not-a-real-password')).SecurePassword
        $script:PhaseSettings = @{
            ComputerName          = 'ex1'
            IPAddress             = '10.99.0.31'
            PrefixLength          = 24
            Gateway               = '10.99.0.1'
            DnsServer             = '10.99.0.11'
            Domain                = 'PANOPLY.LOCAL'
            NetBiosName           = 'PANOPLY'
            AdministratorPassword = $script:Secret
            VmName                = 'exchange-primary'
            LabRoot               = 'C:\jim-ad-lab'
        }
    }

    It 'gives License nothing but the phase' {
        @((Get-LabExchangePhaseArgument -Phase License -Settings $script:PhaseSettings).Keys) | Should -Be @('Phase')
    }

    It 'gives Prepare the network settings, the domain controller as resolver, and no secret' {
        $arguments = Get-LabExchangePhaseArgument -Phase Prepare -Settings $script:PhaseSettings
        @($arguments.Keys) | Should -Be @('Phase', 'ComputerName', 'IPAddress', 'PrefixLength', 'Gateway', 'DnsServer')
    }

    It 'gives the Administrator password only to the phases that need it explicitly' {
        foreach ($phase in 'License', 'Prepare', 'Prerequisites', 'Install') {
            (Get-LabExchangePhaseArgument -Phase $phase -Settings $script:PhaseSettings).Contains('AdministratorPassword') | Should -BeFalse
        }
        foreach ($phase in 'Join', 'Configure', 'Verify') {
            (Get-LabExchangePhaseArgument -Phase $phase -Settings $script:PhaseSettings).AdministratorPassword | Should -BeOfType [securestring]
        }
    }

    It 'gives Install, Configure and Verify the names they build and check' {
        foreach ($phase in 'Install', 'Configure', 'Verify') {
            $arguments = Get-LabExchangePhaseArgument -Phase $phase -Settings $script:PhaseSettings
            foreach ($name in 'Domain', 'NetBiosName', 'ComputerName', 'LabRoot') {
                $arguments[$name] | Should -Be $script:PhaseSettings[$name]
            }
        }
    }

    It 'names the setting a phase is missing' {
        $partial = @{} + $script:PhaseSettings
        $partial.Remove('DnsServer')
        { Get-LabExchangePhaseArgument -Phase Prepare -Settings $partial } | Should -Throw "*'DnsServer'*"
    }

    It 'refuses a phase the guest script does not have' {
        { Get-LabExchangePhaseArgument -Phase Farm -Settings $script:PhaseSettings } | Should -Throw
    }
}

Describe 'Get-LabExchangeWindowsFeature' {
    It 'is Microsoft''s list for a Mailbox server with the Desktop Experience' {
        $features = @(Get-LabExchangeWindowsFeature)
        foreach ($name in 'Server-Media-Foundation', 'NET-Framework-45-ASPNET', 'RPC-over-HTTP-proxy', 'RSAT-Clustering-PowerShell', 'Web-Server', 'Web-Windows-Auth', 'Web-Mgmt-Console', 'Windows-Identity-Foundation', 'RSAT-ADDS') {
            $features | Should -Contain $name
        }
    }

    It 'adds the Active Directory module, which Configure and Verify use' {
        @(Get-LabExchangeWindowsFeature) | Should -Contain 'RSAT-AD-PowerShell'
    }

    It 'leaves out Message Queuing, which Exchange no longer needs' {
        @(Get-LabExchangeWindowsFeature) | Should -Not -Contain 'MSMQ'
        @(Get-LabExchangeWindowsFeature) | Should -Not -Contain 'NET-WCF-MSMQ-Activation45'
    }

    It 'names each feature once' {
        $features = @(Get-LabExchangeWindowsFeature)
        @($features | Sort-Object -Unique).Count | Should -Be $features.Count
    }
}

Describe 'Get-LabExchangePrerequisite' {
    It 'lists the four installers, the runtimes first and URL Rewrite after IIS''s features' {
        @(Get-LabExchangePrerequisite | ForEach-Object { $_.Name }) |
            Should -Be @('Visual C++ 2012 Redistributable (x64)', 'Visual C++ 2013 Redistributable (x64)', 'IIS URL Rewrite Module 2.1', 'Unified Communications Managed API 4.0')
    }

    It 'takes three from the prerequisites directory and UCMA from the Exchange media' {
        $prerequisites = @(Get-LabExchangePrerequisite)
        @($prerequisites | Where-Object { $_.Source -eq 'Directory' } | ForEach-Object { $_.FileName }) |
            Should -Be @('vcredist_2012_x64.exe', 'vcredist_2013_x64.exe', 'rewrite_amd64_en-US.msi')
        $ucma = $prerequisites | Where-Object { $_.Source -eq 'Media' }
        $ucma.FileName | Should -Be 'UCMARedist\Setup.exe'
    }

    It 'gives each installer a download address on a Microsoft host, or none when it is on the media' {
        foreach ($prerequisite in @(Get-LabExchangePrerequisite)) {
            if ($prerequisite.Source -eq 'Directory') {
                ([uri]$prerequisite.DownloadUrl).Host | Should -Match '(^|\.)(microsoft\.com|aka\.ms)$'
            }
            else {
                $prerequisite.DownloadUrl | Should -BeNullOrEmpty
            }
        }
    }

    It 'installs each one silently and without restarting' {
        foreach ($prerequisite in @(Get-LabExchangePrerequisite)) {
            ($prerequisite.Arguments -join ' ') | Should -Match '(/quiet|/q|/qn)\b'
            ($prerequisite.Arguments -join ' ') | Should -Match '/norestart'
        }
    }
}

Describe 'Test-LabExchangePrerequisiteInstalled' {
    BeforeAll {
        $script:Prerequisites = @{}
        foreach ($prerequisite in @(Get-LabExchangePrerequisite)) { $script:Prerequisites[$prerequisite.Name] = $prerequisite }
    }

    It 'recognises each installer by the names it registers as installed software' {
        $installed = @(
            'Microsoft Visual C++ 2012 x64 Minimum Runtime - 11.0.61030',
            'Microsoft Visual C++ 2013 x64 Minimum Runtime - 12.0.40664',
            'IIS URL Rewrite Module 2',
            'Microsoft Unified Communications Managed API 4.0, Runtime'
        )
        foreach ($prerequisite in $script:Prerequisites.Values) {
            Test-LabExchangePrerequisiteInstalled -Prerequisite $prerequisite -InstalledName $installed | Should -BeTrue
        }
    }

    It 'does not mistake one runtime for another, nor the 32-bit runtime for the 64-bit one' {
        $installed = @('Microsoft Visual C++ 2013 x64 Minimum Runtime - 12.0.40664', 'Microsoft Visual C++ 2012 x86 Minimum Runtime - 11.0.61030')
        Test-LabExchangePrerequisiteInstalled -Prerequisite $script:Prerequisites['Visual C++ 2012 Redistributable (x64)'] -InstalledName $installed | Should -BeFalse
        Test-LabExchangePrerequisiteInstalled -Prerequisite $script:Prerequisites['Visual C++ 2013 Redistributable (x64)'] -InstalledName $installed | Should -BeTrue
    }

    It 'is false when nothing is installed' {
        foreach ($prerequisite in $script:Prerequisites.Values) {
            Test-LabExchangePrerequisiteInstalled -Prerequisite $prerequisite -InstalledName @() | Should -BeFalse
        }
    }
}

Describe 'Test-LabMicrosoftSignature' {
    It 'accepts a valid signature by Microsoft Corporation' {
        Test-LabMicrosoftSignature -Status 'Valid' -SignerSubject 'CN=Microsoft Corporation, OU=MOPR, O=Microsoft Corporation, L=Redmond, S=Washington, C=US' | Should -BeTrue
        Test-LabMicrosoftSignature -Status 'Valid' -SignerSubject 'CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US' | Should -BeTrue
    }

    It 'refuses a signature that is not valid, however it is signed' {
        foreach ($status in 'NotSigned', 'HashMismatch', 'NotTrusted', 'UnknownError') {
            Test-LabMicrosoftSignature -Status $status -SignerSubject 'CN=Microsoft Corporation, O=Microsoft Corporation, C=US' | Should -BeFalse
        }
    }

    It 'refuses a valid signature by anyone else, including a name that only contains Microsoft''s' {
        Test-LabMicrosoftSignature -Status 'Valid' -SignerSubject 'CN=Contoso, O=Contoso Ltd, C=GB' | Should -BeFalse
        Test-LabMicrosoftSignature -Status 'Valid' -SignerSubject 'CN=Microsoft Corporation, O=Not Microsoft Corporation, C=US' | Should -BeFalse
        Test-LabMicrosoftSignature -Status 'Valid' -SignerSubject 'CN=x, O=Microsoft Corporation Fakes, C=US' | Should -BeFalse
        Test-LabMicrosoftSignature -Status 'Valid' -SignerSubject '' | Should -BeFalse
    }
}

Describe 'Get-LabExchangePageFileSize' {
    It 'is a quarter of the memory, in megabytes, as Microsoft recommends for a Mailbox server' {
        Get-LabExchangePageFileSize -MemoryBytes 12GB | Should -Be 3072
        Get-LabExchangePageFileSize -MemoryBytes 16GB | Should -Be 4096
    }
}

Describe 'Get-LabExchangeSetupInstallArgument' {
    It 'installs the Mailbox role, accepting the licence terms with diagnostic data off' {
        $arguments = Get-LabExchangeSetupInstallArgument -DatabaseName 'MDB01'
        $arguments | Should -Contain '/Mode:Install'
        $arguments | Should -Contain '/Roles:Mailbox'
        $arguments | Should -Contain '/IAcceptExchangeServerLicenseTerms_DiagnosticDataOFF'
    }

    It 'names the first database and puts it and its logs where Configure expects them' {
        $arguments = Get-LabExchangeSetupInstallArgument -DatabaseName 'MDB01'
        $arguments | Should -Contain '/MdbName:MDB01'
        $arguments | Should -Contain '/DbFilePath:C:\ExchangeDatabases\MDB01\MDB01.edb'
        $arguments | Should -Contain '/LogFolderPath:C:\ExchangeDatabases\MDB01\Logs'
    }

    It 'leaves the Windows features to the Prerequisites phase, and names no organisation (the forest has one)' {
        $text = (Get-LabExchangeSetupInstallArgument -DatabaseName 'MDB01') -join ' '
        $text | Should -Not -Match 'InstallWindowsComponents'
        $text | Should -Not -Match 'OrganizationName'
    }
}

Describe 'Get-LabExchangeRoleState' {
    It 'is NotInstalled when Setup has left no Mailbox role key' {
        Get-LabExchangeRoleState -RoleKey $null | Should -Be 'NotInstalled'
    }

    It 'is Partial while Setup''s watermark remains, which a failed or interrupted install leaves' {
        Get-LabExchangeRoleState -RoleKey ([pscustomobject]@{ Watermark = 'MailboxComponent___abc'; Action = 'Install' }) | Should -Be 'Partial'
        Get-LabExchangeRoleState -RoleKey ([pscustomobject]@{ Watermark = 'x'; ConfiguredVersion = '15.2.2562.17' }) | Should -Be 'Partial'
    }

    It 'is Installed once Setup has recorded the configured version and cleared its watermark' {
        Get-LabExchangeRoleState -RoleKey ([pscustomobject]@{ ConfiguredVersion = '15.2.2562.17'; UnpackedVersion = '15.2.2562.17' }) | Should -Be 'Installed'
    }

    It 'is Partial when the key exists with neither, as Setup leaves it part way through' {
        Get-LabExchangeRoleState -RoleKey ([pscustomobject]@{ UnpackedVersion = '15.2.2562.17' }) | Should -Be 'Partial'
    }
}

Describe 'Get-LabExchangeServiceHealthProblem' {
    It 'is nothing when every role has its required services running' {
        $health = @(
            [pscustomobject]@{ Role = 'Mailbox Server Role'; RequiredServicesRunning = $true; ServicesNotRunning = @() },
            [pscustomobject]@{ Role = 'Client Access Server Role'; RequiredServicesRunning = $true; ServicesNotRunning = @() }
        )
        @(Get-LabExchangeServiceHealthProblem -Health $health) | Should -BeNullOrEmpty
    }

    It 'names each role whose required services are not all running, and the services' {
        $health = @(
            [pscustomobject]@{ Role = 'Mailbox Server Role'; RequiredServicesRunning = $false; ServicesNotRunning = @('MSExchangeIS', 'MSExchangeTransport') },
            [pscustomobject]@{ Role = 'Client Access Server Role'; RequiredServicesRunning = $true; ServicesNotRunning = @() }
        )
        $problems = @(Get-LabExchangeServiceHealthProblem -Health $health)
        $problems.Count | Should -Be 1
        $problems[0] | Should -Match 'Mailbox Server Role'
        $problems[0] | Should -Match 'MSExchangeIS, MSExchangeTransport'
    }

    It 'is a problem when Exchange reports no role at all' {
        @(Get-LabExchangeServiceHealthProblem -Health @()) | Should -Match 'no role'
    }
}

Describe 'Test-LabExchangeDatabaseLayout' {
    BeforeAll {
        function Get-TestDatabase {
            param([string]$Name, [bool]$Mounted = $true, [bool]$Excluded = $false, [string]$Server = 'EX1')
            return [pscustomobject]@{ Name = $Name; Server = $Server; Mounted = $Mounted; IsExcludedFromProvisioning = $Excluded }
        }
    }

    It 'accepts both databases mounted on this server, the second alone excluded from provisioning' {
        $databases = @((Get-TestDatabase 'MDB01'), (Get-TestDatabase 'MDB02' -Excluded $true))
        @(Test-LabExchangeDatabaseLayout -Database $databases -Info $script:Info) | Should -BeNullOrEmpty
    }

    It 'names a missing database' {
        @(Test-LabExchangeDatabaseLayout -Database @(Get-TestDatabase 'MDB01') -Info $script:Info) | Should -Match 'MDB02'
    }

    It 'names a database that is not mounted' {
        $databases = @((Get-TestDatabase 'MDB01' -Mounted $false), (Get-TestDatabase 'MDB02' -Excluded $true))
        @(Test-LabExchangeDatabaseLayout -Database $databases -Info $script:Info) | Should -Match 'MDB01.*not mounted'
    }

    It 'names the wrong database excluded, or neither' {
        $databases = @((Get-TestDatabase 'MDB01' -Excluded $true), (Get-TestDatabase 'MDB02'))
        $problems = @(Test-LabExchangeDatabaseLayout -Database $databases -Info $script:Info)
        $problems -join ' ' | Should -Match 'MDB01'
        $problems -join ' ' | Should -Match 'MDB02'
    }

    It 'names a database on another server' {
        $databases = @((Get-TestDatabase 'MDB01'), (Get-TestDatabase 'MDB02' -Excluded $true -Server 'EX2'))
        @(Test-LabExchangeDatabaseLayout -Database $databases -Info $script:Info) | Should -Match 'EX2'
    }
}
