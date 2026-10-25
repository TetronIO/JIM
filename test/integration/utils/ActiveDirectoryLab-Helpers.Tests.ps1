# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

#Requires -Modules Pester

<#
.SYNOPSIS
    Pester tests for ActiveDirectoryLab-Helpers.ps1, the runner's Active Directory lab decisions.

.DESCRIPTION
    The Active Directory lab is Hyper-V virtual machines, so the runner cannot be exercised end to end
    without it. Everything the runner decides about the lab (which machines a scenario uses, which
    checkpoint each one is reverted to, whether a populated checkpoint is still current, what a
    control-plane answer means, what an ldapadd run did) is a function that is proven here with the
    control plane and the LDAP toolbox mocked.
#>

BeforeAll {
    . "$PSScriptRoot/LDAP-Helpers.ps1"
    . "$PSScriptRoot/Invoke-LabControl.ps1"
    . "$PSScriptRoot/ActiveDirectoryLab-Helpers.ps1"

    $script:LabModulePath = Join-Path $PSScriptRoot '..' 'ad-lab' 'guest' 'LabDomainController.psm1'

    function Get-TestConfig {
        param([string]$Instance = 'Source')
        $domain = @{ Primary = 'panoply'; Source = 'resurgam'; Target = 'gentian' }[$Instance]
        $vm = @{ Primary = 'dc-primary'; Source = 'dc-source'; Target = 'dc-target' }[$Instance]
        return @{
            DirectoryType    = 'ActiveDirectory'
            ContainerName    = $null
            Host             = "dc1.$domain.local"
            Address          = '10.20.30.40'
            VmName           = $vm
            BaseDN           = "DC=$domain,DC=local"
            BindDN           = "CN=Administrator,CN=Users,DC=$domain,DC=local"
            BindPassword     = 'placeholder-not-a-secret'
            LdapSearchPort   = 636
            LdapSearchScheme = 'ldaps'
        }
    }

    function Get-TestFederationConfig {
        return @{
            DirectoryType      = 'ActiveDirectoryFederation'
            Instance           = 'AdfsPrimary'
            ContainerName      = $null
            Host               = 'adfs.panoply.local'
            Address            = '10.20.30.41'
            VmName             = 'adfs-primary'
            Port               = 443
            Authority          = 'https://adfs.panoply.local/adfs'
            DiscoveryPath      = '/adfs/.well-known/openid-configuration'
            Issuer             = 'https://adfs.panoply.local/adfs'
            AccessTokenIssuer  = 'http://adfs.panoply.local/adfs/services/trust'
            CheckpointBaseline = 'baseline'
        }
    }

    function Get-StateJson {
        param([string]$Name, [string[]]$Checkpoint = @('baseline'), [string]$Build = '26100.1742', [switch]$NoGuest)
        $guest = if ($NoGuest) { $null } else {
            [ordered]@{ productName = 'Windows Server 2022 Datacenter'; displayVersion = '24H2'; currentBuild = '26100'; ubr = 1742; buildString = $Build }
        }
        return ([ordered]@{
            name           = $Name
            state          = 'Off'
            generation     = 2
            checkpointType = 'ProductionOnly'
            uptimeSeconds  = 0
            checkpoints    = @($Checkpoint | ForEach-Object { [ordered]@{ name = $_; createdUtc = '2026-09-01T00:00:00Z' } })
            guest          = $guest
        } | ConvertTo-Json -Depth 5 -Compress)
    }
}

Describe 'Get-JimComposeArgument' {
    It 'keeps the base arguments it is given in front of the lab overlay, so a lane''s project name and override survive' {
        $base = @('-p', 'jim-openldap', '-f', 'docker-compose.yml', '-f', 'docker-compose.override.yml', '-f', 'test/integration/docker/jim-lane.override.yml')
        $composeArgs = @(Get-JimComposeArgument -DirectoryType 'ActiveDirectory' -BaseArguments $base)
        $composeArgs[0..($base.Count - 1)] | Should -Be $base
        $composeArgs[-2..-1] | Should -Be @('-f', 'test/integration/docker/docker-compose.ad-lab.yml')
        @(Get-JimComposeArgument -DirectoryType 'SambaAD' -BaseArguments $base) | Should -Be $base
    }

    It 'is the two JIM compose files, and nothing else, for a container lab' {
        foreach ($type in 'SambaAD', 'OpenLDAP', 'DirectoryServer389') {
            $composeArgs = Get-JimComposeArgument -DirectoryType $type
            $composeArgs | Should -Be @('-f', 'docker-compose.yml', '-f', 'docker-compose.override.yml')
        }
    }

    It 'adds the lab overlay for ActiveDirectory so the JIM containers resolve the domain controllers' {
        $composeArgs = Get-JimComposeArgument -DirectoryType 'ActiveDirectory'

        $composeArgs | Should -Be @('-f', 'docker-compose.yml', '-f', 'docker-compose.override.yml', '-f', 'test/integration/docker/docker-compose.ad-lab.yml')
    }

    It 'names a file that exists' {
        $composeArgs = Get-JimComposeArgument -DirectoryType 'ActiveDirectory' -ScenarioNumber 30
        $repoRoot = Join-Path $PSScriptRoot '..' '..' '..'

        foreach ($file in $composeArgs | Where-Object { $_ -ne '-f' }) {
            Join-Path $repoRoot $file | Should -Exist
        }
    }

    It 'adds the AD FS overlay after the lab overlay for a scenario that signs in through AD FS, so JIM''s identity provider is adfs-primary' {
        $composeArgs = Get-JimComposeArgument -DirectoryType 'ActiveDirectory' -ScenarioNumber 30

        $composeArgs | Should -Be @('-f', 'docker-compose.yml', '-f', 'docker-compose.override.yml',
            '-f', 'test/integration/docker/docker-compose.ad-lab.yml', '-f', 'test/integration/docker/docker-compose.ad-lab-adfs.yml')
    }

    It 'leaves JIM''s identity provider alone for every other scenario, and for no scenario number' {
        foreach ($number in 1, 8, 25, $null) {
            Get-JimComposeArgument -DirectoryType 'ActiveDirectory' -ScenarioNumber $number |
                Should -Not -Contain 'test/integration/docker/docker-compose.ad-lab-adfs.yml'
        }
    }

    It 'never adds the AD FS overlay outside the Active Directory lab' {
        Get-JimComposeArgument -DirectoryType 'SambaAD' -ScenarioNumber 30 | Should -Be @('-f', 'docker-compose.yml', '-f', 'docker-compose.override.yml')
    }
}

Describe 'Get-ActiveDirectoryLabInstance' {
    It 'is Primary, Source and Target for Scenarios 2 and 8, the ones that use the two extra forests' {
        Get-ActiveDirectoryLabInstance -ScenarioNumber 2 | Should -Be @('Primary', 'Source', 'Target')
        Get-ActiveDirectoryLabInstance -ScenarioNumber 8 | Should -Be @('Primary', 'Source', 'Target')
    }

    It 'is Primary and Source for Scenario 25, which compares the Recycle Bin on (Primary) with the Recycle Bin off (Source)' {
        @(Get-ActiveDirectoryLabInstance -ScenarioNumber 25) | Should -Be @('Primary', 'Source')
    }

    It 'is Primary alone for every other scenario' {
        foreach ($number in 1, 4, 9, 17, 20, 23, 24) {
            @(Get-ActiveDirectoryLabInstance -ScenarioNumber $number) | Should -Be @('Primary')
        }
    }

    It 'is Primary alone when there is no scenario number' {
        @(Get-ActiveDirectoryLabInstance -ScenarioNumber $null) | Should -Be @('Primary')
    }

    It 'is Primary then the AD FS server for Scenario 30, so the pair is reverted domain controller first' {
        @(Get-ActiveDirectoryLabInstance -ScenarioNumber 30) | Should -Be @('Primary', 'AdfsPrimary')
    }

    It 'reverts the AD FS server for no other scenario: it takes minutes, and its baseline pairs with dc-primary''s' {
        foreach ($number in 1, 2, 8, 24, 25, 28, $null) {
            @(Get-ActiveDirectoryLabInstance -ScenarioNumber $number) | Should -Not -Contain 'AdfsPrimary'
        }
    }
}

Describe 'Test-ActiveDirectoryLabFederationScenario' {
    It 'is true for Scenario 30 only' {
        Test-ActiveDirectoryLabFederationScenario -ScenarioNumber 30 | Should -BeTrue
        foreach ($number in 1, 8, 25, 28, $null) {
            Test-ActiveDirectoryLabFederationScenario -ScenarioNumber $number | Should -BeFalse
        }
    }
}

Describe 'Get-ActiveDirectoryLabCheckpointName' {
    It 'builds baseline' {
        Get-ActiveDirectoryLabCheckpointName -Baseline | Should -Be 'baseline'
    }

    It 'builds the populated checkpoint name from the template and hash, in lower case' {
        Get-ActiveDirectoryLabCheckpointName -Template 'Scale100k50Groups' -Hash '0123456789ABCDEF' |
            Should -BeExactly 'populated-scale100k50groups-0123456789abcdef'
    }

    It 'refuses a hash that is not 16 hexadecimal characters' {
        { Get-ActiveDirectoryLabCheckpointName -Template 'Nano' -Hash 'xyz' } | Should -Throw
        { Get-ActiveDirectoryLabCheckpointName -Template 'Nano' -Hash '0123456789abcde' } | Should -Throw
    }

    It 'refuses a template that is not alphanumeric' {
        { Get-ActiveDirectoryLabCheckpointName -Template 'Nano;x' -Hash '0123456789abcdef' } | Should -Throw
    }

    It 'agrees with the control plane''s own Get-LabCheckpointName, so a name built here is one it accepts' {
        Import-Module $script:LabModulePath -Force
        try {
            foreach ($template in 'Nano', 'Small', 'Scale500k65Groups') {
                $hash = '00ff00ff00ff00ff'
                $mine = Get-ActiveDirectoryLabCheckpointName -Template $template -Hash $hash
                $theirs = Get-LabCheckpointName -Template $template -Hash $hash

                $mine | Should -BeExactly $theirs
                Test-LabCheckpointName -Name $mine | Should -BeTrue
            }
        }
        finally {
            Remove-Module LabDomainController -Force -ErrorAction SilentlyContinue
        }
    }
}

Describe 'Get-ActiveDirectoryLabPopulateHash' {
    BeforeEach {
        $script:root = Join-Path ([System.IO.Path]::GetTempPath()) ("adlab-hash-" + [Guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path (Join-Path $script:root 'utils') -Force | Out-Null
        $script:files = @(
            (Join-Path $script:root 'utils' 'Test-Helpers.ps1'),
            (Join-Path $script:root 'utils' 'Test-GroupHelpers.ps1'),
            (Join-Path $script:root 'utils' 'LDAP-Helpers.ps1'),
            (Join-Path $script:root 'utils' 'ActiveDirectoryLab-Helpers.ps1'),
            (Join-Path $script:root 'Populate-SambaAD-Scenario-008.ps1')
        )
        $n = 0
        foreach ($file in $script:files) { $n++; Set-Content -Path $file -Value "content $n" -NoNewline }
    }

    AfterEach {
        Remove-Item -Path $script:root -Recurse -Force -ErrorAction SilentlyContinue
    }

    It 'is 16 lower-case hexadecimal characters' {
        Get-ActiveDirectoryLabPopulateHash -ScriptRoot $script:root | Should -Match '^[0-9a-f]{16}$'
    }

    It 'is the first 16 characters of the SHA-256 of the five files concatenated in order, as Get-PopulateScriptHash is' {
        $combined = 'content 1content 2content 3content 4content 5'
        $bytes = [System.Security.Cryptography.SHA256]::HashData([System.Text.Encoding]::UTF8.GetBytes($combined))
        $expected = [System.BitConverter]::ToString($bytes).Replace('-', '').Substring(0, 16).ToLower()

        Get-ActiveDirectoryLabPopulateHash -ScriptRoot $script:root | Should -Be $expected
    }

    It 'is stable when nothing changes' {
        (Get-ActiveDirectoryLabPopulateHash -ScriptRoot $script:root) | Should -Be (Get-ActiveDirectoryLabPopulateHash -ScriptRoot $script:root)
    }

    It 'changes when any one of the five files changes, including the Active Directory delivery helpers' {
        $before = Get-ActiveDirectoryLabPopulateHash -ScriptRoot $script:root
        foreach ($file in $script:files) {
            $original = Get-Content -Path $file -Raw
            Set-Content -Path $file -Value ($original + ' changed') -NoNewline

            Get-ActiveDirectoryLabPopulateHash -ScriptRoot $script:root | Should -Not -Be $before

            Set-Content -Path $file -Value $original -NoNewline
        }
        Get-ActiveDirectoryLabPopulateHash -ScriptRoot $script:root | Should -Be $before
    }

    It 'skips a file that is not there, as Get-PopulateScriptHash does, rather than failing the run' {
        Remove-Item -Path $script:files[2]

        { Get-ActiveDirectoryLabPopulateHash -ScriptRoot $script:root } | Should -Not -Throw
    }
}

Describe 'Select-ActiveDirectoryLabCheckpoint' {
    BeforeAll {
        $script:hash = '0123456789abcdef'
        $script:populated = 'populated-nano-0123456789abcdef'
    }

    It 'reverts Scenario 8 Source and Target to the populated checkpoint when it is there with the current hash' {
        foreach ($instance in 'Source', 'Target') {
            $pick = Select-ActiveDirectoryLabCheckpoint -Instance $instance -ScenarioNumber 8 -Template 'Nano' -Hash $script:hash `
                -AvailableCheckpoint @('baseline', $script:populated)

            $pick.Checkpoint | Should -Be $script:populated
            $pick.NeedsPopulation | Should -BeFalse
        }
    }

    It 'falls back to baseline and asks for population when the populated checkpoint is missing' {
        $pick = Select-ActiveDirectoryLabCheckpoint -Instance 'Source' -ScenarioNumber 8 -Template 'Nano' -Hash $script:hash `
            -AvailableCheckpoint @('baseline')

        $pick.Checkpoint | Should -Be 'baseline'
        $pick.NeedsPopulation | Should -BeTrue
    }

    It 'rejects a populated checkpoint whose hash does not match (the populate scripts changed) and asks for a rebuild' {
        $pick = Select-ActiveDirectoryLabCheckpoint -Instance 'Source' -ScenarioNumber 8 -Template 'Nano' -Hash $script:hash `
            -AvailableCheckpoint @('baseline', 'populated-nano-ffffffffffffffff')

        $pick.Checkpoint | Should -Be 'baseline'
        $pick.NeedsPopulation | Should -BeTrue
    }

    It 'does not reuse a checkpoint populated for another template' {
        $pick = Select-ActiveDirectoryLabCheckpoint -Instance 'Source' -ScenarioNumber 8 -Template 'Small' -Hash $script:hash `
            -AvailableCheckpoint @('baseline', $script:populated)

        $pick.Checkpoint | Should -Be 'baseline'
        $pick.NeedsPopulation | Should -BeTrue
    }

    It 'matches the template without regard to case, since checkpoint names are lower case' {
        $pick = Select-ActiveDirectoryLabCheckpoint -Instance 'Source' -ScenarioNumber 8 -Template 'NANO' -Hash $script:hash `
            -AvailableCheckpoint @('baseline', $script:populated)

        $pick.Checkpoint | Should -Be $script:populated
    }

    It 'always reverts Primary to baseline, even for Scenario 8, which never populates it' {
        $pick = Select-ActiveDirectoryLabCheckpoint -Instance 'Primary' -ScenarioNumber 8 -Template 'Nano' -Hash $script:hash `
            -AvailableCheckpoint @('baseline', $script:populated)

        $pick.Checkpoint | Should -Be 'baseline'
        $pick.NeedsPopulation | Should -BeFalse
    }

    It 'reverts every instance to baseline for a scenario that populates live (anything but 8)' {
        foreach ($instance in 'Primary', 'Source', 'Target') {
            $pick = Select-ActiveDirectoryLabCheckpoint -Instance $instance -ScenarioNumber 2 -Template 'Nano' -Hash $script:hash `
                -AvailableCheckpoint @('baseline', $script:populated)

            $pick.Checkpoint | Should -Be 'baseline'
            $pick.NeedsPopulation | Should -BeFalse
        }
    }

    It 'reverts the AD FS server to baseline and never asks for population' {
        $pick = Select-ActiveDirectoryLabCheckpoint -Instance 'AdfsPrimary' -ScenarioNumber 30 -Template 'Nano' -Hash $script:hash `
            -AvailableCheckpoint @('baseline')

        $pick.Checkpoint | Should -Be 'baseline'
        $pick.NeedsPopulation | Should -BeFalse
    }

    It 'reverts Scenario 25 Source to baseline and never asks for population, even when a populated checkpoint exists' {
        foreach ($instance in 'Primary', 'Source') {
            $pick = Select-ActiveDirectoryLabCheckpoint -Instance $instance -ScenarioNumber 25 -Template 'Nano' -Hash $script:hash `
                -AvailableCheckpoint @('baseline', $script:populated)

            $pick.Checkpoint | Should -Be 'baseline'
            $pick.NeedsPopulation | Should -BeFalse
        }
    }
}

Describe 'ConvertFrom-LabDomainControllerJson' {
    It 'reads the state, the checkpoint names and the guest build' {
        $state = ConvertFrom-LabDomainControllerJson -Json (Get-StateJson -Name 'dc-primary' -Checkpoint 'baseline', 'populated-nano-0123456789abcdef')

        $state.Name | Should -Be 'dc-primary'
        $state.State | Should -Be 'Off'
        $state.CheckpointNames | Should -Be @('baseline', 'populated-nano-0123456789abcdef')
        $state.GuestBuild | Should -Be '26100.1742'
    }

    It 'gives a null guest build when the guest was not reachable' {
        $state = ConvertFrom-LabDomainControllerJson -Json (Get-StateJson -Name 'dc-primary' -NoGuest)

        $state.GuestBuild | Should -BeNullOrEmpty
    }

    It 'gives an empty checkpoint list, not a null, when there are none' {
        $state = ConvertFrom-LabDomainControllerJson -Json (Get-StateJson -Name 'dc-primary' -Checkpoint @())

        @($state.CheckpointNames).Count | Should -Be 0
    }

    It 'tolerates a banner or trailing text around the JSON object on standard output' {
        $json = "Warning: something the SSH profile printed`n" + (Get-StateJson -Name 'dc-target') + "`nBye"

        (ConvertFrom-LabDomainControllerJson -Json $json).Name | Should -Be 'dc-target'
    }

    It 'throws when there is no JSON object, naming what it was given' {
        { ConvertFrom-LabDomainControllerJson -Json 'nothing useful here' } | Should -Throw '*nothing useful here*'
        { ConvertFrom-LabDomainControllerJson -Json '' } | Should -Throw
    }
}

Describe 'Get-ActiveDirectoryLabState' {
    BeforeEach {
        Mock Invoke-LabControl {
            $name = $Arguments[1]
            return (Get-StateJson -Name $name -Checkpoint 'baseline')
        }
    }

    It 'asks the control plane about each instance''s virtual machine, as JSON, in Primary, Source, Target order' {
        $configs = @{ Target = (Get-TestConfig 'Target'); Primary = (Get-TestConfig 'Primary'); Source = (Get-TestConfig 'Source') }

        $states = @(Get-ActiveDirectoryLabState -DirectoryConfig $configs)

        $states.Count | Should -Be 3
        ($states | ForEach-Object { $_.Instance }) | Should -Be @('Primary', 'Source', 'Target')
        ($states | ForEach-Object { $_.VmName }) | Should -Be @('dc-primary', 'dc-source', 'dc-target')
        Should -Invoke Invoke-LabControl -Times 3 -Exactly -ParameterFilter {
            $Script -eq 'Get-LabDomainController.ps1' -and $Arguments[0] -eq '-Name' -and $Arguments[2] -eq '-AsJson'
        }
    }

    It 'fails, naming the machine and the remedy, when the baseline checkpoint is missing' {
        Mock Invoke-LabControl { return (Get-StateJson -Name 'dc-source' -Checkpoint @('populated-nano-0123456789abcdef')) }

        { Get-ActiveDirectoryLabState -DirectoryConfig @{ Source = (Get-TestConfig 'Source') } } |
            Should -Throw "*dc-source*'baseline'*New-LabDomainController.ps1*"
    }

    It 'lets a control plane failure through rather than treating the machine as fine' {
        Mock Invoke-LabControl { throw 'ssh: connect to host lab port 22: Connection refused' }

        { Get-ActiveDirectoryLabState -DirectoryConfig @{ Primary = (Get-TestConfig 'Primary') } } | Should -Throw '*Connection refused*'
    }

    It 'asks about the AD FS server after the domain controller it pairs with' {
        $configs = @{ AdfsPrimary = (Get-TestFederationConfig); Primary = (Get-TestConfig 'Primary') }

        $states = @(Get-ActiveDirectoryLabState -DirectoryConfig $configs)

        ($states | ForEach-Object { $_.Instance }) | Should -Be @('Primary', 'AdfsPrimary')
        ($states | ForEach-Object { $_.VmName }) | Should -Be @('dc-primary', 'adfs-primary')
    }

    It 'names the AD FS server''s own build script when its baseline checkpoint is missing' {
        Mock Invoke-LabControl { return (Get-StateJson -Name 'adfs-primary' -Checkpoint @()) }

        { Get-ActiveDirectoryLabState -DirectoryConfig @{ AdfsPrimary = (Get-TestFederationConfig) } } |
            Should -Throw "*adfs-primary*'baseline'*New-LabFederationServer.ps1*"
    }
}

Describe 'Get-ActiveDirectoryLabFederationRequest' {
    It 'asks for the discovery document by the service name, closing the connection after one response, in printf''s escapes' {
        $request = Get-ActiveDirectoryLabFederationRequest -FederationConfig (Get-TestFederationConfig)

        $request | Should -Be "GET /adfs/.well-known/openid-configuration HTTP/1.1\r\nHost: adfs.panoply.local\r\nConnection: close\r\n\r\n"
    }
}

Describe 'Test-ActiveDirectoryLabFederationDiscovery' {
    BeforeAll {
        function Get-TestResponse {
            param([string]$Status = 'HTTP/1.1 200 OK', [string]$Issuer = 'https://adfs.panoply.local/adfs',
                [string]$Authorize = 'https://adfs.panoply.local/adfs/oauth2/authorize/')
            # The shape adfs-primary publishes (checked against it): the issuer is the authority, and access tokens
            # name the federation service identifier instead.
            $body = [ordered]@{
                issuer = $Issuer; authorization_endpoint = $Authorize; jwks_uri = 'https://adfs.panoply.local/adfs/discovery/keys'
                access_token_issuer = 'http://adfs.panoply.local/adfs/services/trust'
            } |
                ConvertTo-Json -Compress
            return @($Status, 'Content-Type: application/json;charset=UTF-8', 'Connection: close', '', $body)
        }
    }

    It 'is ready when AD FS answers 200 with its own issuer and endpoints on the service' {
        $result = Test-ActiveDirectoryLabFederationDiscovery -Response (Get-TestResponse) -FederationConfig (Get-TestFederationConfig)

        $result.Ready | Should -BeTrue
    }

    It 'is not ready, saying so, while AD FS is still starting and answers 503' {
        $result = Test-ActiveDirectoryLabFederationDiscovery -Response @('HTTP/1.1 503 Service Unavailable', '', 'Service Unavailable') `
            -FederationConfig (Get-TestFederationConfig)

        $result.Ready | Should -BeFalse
        $result.Reason | Should -Match '503'
    }

    It 'is not ready when nothing came back' {
        $result = Test-ActiveDirectoryLabFederationDiscovery -Response @() -FederationConfig (Get-TestFederationConfig)

        $result.Ready | Should -BeFalse
        $result.Reason | Should -Match 'no response'
    }

    It 'is not ready, naming both, when the issuer is not the one JIM will be told to expect' {
        $result = Test-ActiveDirectoryLabFederationDiscovery -Response (Get-TestResponse -Issuer 'https://other.example/adfs') `
            -FederationConfig (Get-TestFederationConfig)

        $result.Ready | Should -BeFalse
        $result.Reason | Should -Match 'other\.example'
        $result.Reason | Should -Match 'https://adfs\.panoply\.local/adfs'
    }

    It 'is not ready when the endpoints are not on the service name, as a server built for another name would publish' {
        $result = Test-ActiveDirectoryLabFederationDiscovery -Response (Get-TestResponse -Authorize 'https://adfs1.panoply.local/adfs/oauth2/authorize/') `
            -FederationConfig (Get-TestFederationConfig)

        $result.Ready | Should -BeFalse
        $result.Reason | Should -Match 'authorization_endpoint'
    }

    It 'is not ready when the body is not JSON' {
        $result = Test-ActiveDirectoryLabFederationDiscovery -Response @('HTTP/1.1 200 OK', '', '<html>sign in</html>') `
            -FederationConfig (Get-TestFederationConfig)

        $result.Ready | Should -BeFalse
        $result.Reason | Should -Match 'JSON'
    }
}

Describe 'Wait-ActiveDirectoryLabFederationReady' {
    BeforeAll {
        function docker { }
        # A stand-in with the real one's parameters (it lives in Test-Helpers.ps1), so Mock can replace it.
        function Save-ActiveDirectoryCertificate { param($DirectoryConfig, $CertificateDirectory) $null = $DirectoryConfig, $CertificateDirectory }
        $script:ok = @('HTTP/1.1 200 OK', '', ([ordered]@{
            issuer = 'https://adfs.panoply.local/adfs'
            authorization_endpoint = 'https://adfs.panoply.local/adfs/oauth2/authorize/'
        } | ConvertTo-Json -Compress))
    }

    BeforeEach {
        $script:dockerCalls = [System.Collections.Generic.List[object]]::new()
        $script:responses = [System.Collections.Generic.Queue[object]]::new()
        Mock Start-Sleep { }
        Mock Save-ActiveDirectoryCertificate { }
        Mock docker {
            $script:dockerCalls.Add(@($args))
            $global:LASTEXITCODE = 0
            if ($script:responses.Count -gt 0) { return $script:responses.Dequeue() }
            return $script:ok
        }
    }

    It 'saves the certificate first, then asks for discovery over TLS verified against that certificate and the service name' {
        Wait-ActiveDirectoryLabFederationReady -FederationConfig (Get-TestFederationConfig) -TimeoutSeconds 60 | Should -BeTrue

        Should -Invoke Save-ActiveDirectoryCertificate -Times 1 -Exactly
        $text = @($script:dockerCalls[0]) -join ' '
        $text | Should -Match 'exec jim-ldap-toolbox sh -c'
        $text | Should -Match 'openssl s_client -quiet -connect 10\.20\.30\.41:443 -servername adfs\.panoply\.local'
        $text | Should -Match '-verify_return_error -verify_hostname adfs\.panoply\.local -CAfile /certs/adfs-primary\.pem'
    }

    It 'keeps asking while AD FS is starting, and is ready once discovery answers' {
        $script:responses.Enqueue(@('HTTP/1.1 503 Service Unavailable', ''))
        $script:responses.Enqueue(@())

        Wait-ActiveDirectoryLabFederationReady -FederationConfig (Get-TestFederationConfig) -TimeoutSeconds 60 -PollSeconds 5 | Should -BeTrue

        $script:dockerCalls.Count | Should -Be 3
    }

    It 'keeps trying to save the certificate until the server answers on 443 at all' {
        $script:saveAttempts = 0
        Mock Save-ActiveDirectoryCertificate {
            $script:saveAttempts++
            if ($script:saveAttempts -lt 3) { throw 'No certificate was found in the openssl s_client output.' }
        }

        Wait-ActiveDirectoryLabFederationReady -FederationConfig (Get-TestFederationConfig) -TimeoutSeconds 60 -PollSeconds 5 | Should -BeTrue

        $script:saveAttempts | Should -Be 3
    }

    It 'gives up after the timeout, returning false with the last reason written' {
        Mock docker { $global:LASTEXITCODE = 0; return @('HTTP/1.1 503 Service Unavailable', '') }

        Wait-ActiveDirectoryLabFederationReady -FederationConfig (Get-TestFederationConfig) -TimeoutSeconds 10 -PollSeconds 5 |
            Should -BeFalse
    }
}

Describe 'Get-ActiveDirectoryLabRestorePlan' {
    BeforeAll {
        $script:hash = '0123456789abcdef'
        function Get-State {
            param([string]$Instance, [string[]]$Checkpoint)
            return @{ Instance = $Instance; VmName = @{ Primary = 'dc-primary'; Source = 'dc-source'; Target = 'dc-target' }[$Instance]; State = 'Running'; CheckpointNames = $Checkpoint; GuestBuild = $null }
        }
    }

    It 'plans one entry per state with the instance, machine, checkpoint and whether population is needed' {
        $states = @(
            (Get-State 'Primary' @('baseline')),
            (Get-State 'Source' @('baseline', 'populated-nano-0123456789abcdef')),
            (Get-State 'Target' @('baseline'))
        )

        $plan = @(Get-ActiveDirectoryLabRestorePlan -State $states -ScenarioNumber 8 -Template 'Nano' -Hash $script:hash)

        $plan.Count | Should -Be 3
        $plan[0].Checkpoint | Should -Be 'baseline'
        $plan[0].NeedsPopulation | Should -BeFalse
        $plan[1].Instance | Should -Be 'Source'
        $plan[1].VmName | Should -Be 'dc-source'
        $plan[1].Checkpoint | Should -Be 'populated-nano-0123456789abcdef'
        $plan[1].NeedsPopulation | Should -BeFalse
        $plan[2].Checkpoint | Should -Be 'baseline'
        $plan[2].NeedsPopulation | Should -BeTrue
    }
}

Describe 'Invoke-ActiveDirectoryLabRestore' {
    BeforeEach {
        Mock Invoke-LabControl { '' }
    }

    It 'restores each machine to its planned checkpoint through the control plane, in plan order' {
        $plan = @(
            @{ Instance = 'Primary'; VmName = 'dc-primary'; Checkpoint = 'baseline'; NeedsPopulation = $false },
            @{ Instance = 'Source'; VmName = 'dc-source'; Checkpoint = 'populated-nano-0123456789abcdef'; NeedsPopulation = $false }
        )

        Invoke-ActiveDirectoryLabRestore -Plan $plan

        Should -Invoke Invoke-LabControl -Times 2 -Exactly
        Should -Invoke Invoke-LabControl -Times 1 -Exactly -ParameterFilter {
            $Script -eq 'Restore-LabDomainController.ps1' -and ($Arguments -join ' ') -eq '-Name dc-primary -Checkpoint baseline'
        }
        Should -Invoke Invoke-LabControl -Times 1 -Exactly -ParameterFilter {
            $Script -eq 'Restore-LabDomainController.ps1' -and ($Arguments -join ' ') -eq '-Name dc-source -Checkpoint populated-nano-0123456789abcdef'
        }
    }

    It 'stops at the first failed restore rather than carrying on against a machine in an unknown state' {
        Mock Invoke-LabControl { throw "Checkpoint 'baseline' does not exist" }
        $plan = @(
            @{ Instance = 'Primary'; VmName = 'dc-primary'; Checkpoint = 'baseline'; NeedsPopulation = $false },
            @{ Instance = 'Source'; VmName = 'dc-source'; Checkpoint = 'baseline'; NeedsPopulation = $false }
        )

        { Invoke-ActiveDirectoryLabRestore -Plan $plan } | Should -Throw '*does not exist*'
        Should -Invoke Invoke-LabControl -Times 1 -Exactly
    }
}

Describe 'Invoke-ActiveDirectoryLabCheckpoint' {
    It 'takes the checkpoint with -Replace, so a stale one of the same name is removed first' {
        Mock Invoke-LabControl { '' }

        Invoke-ActiveDirectoryLabCheckpoint -VmName 'dc-source' -Checkpoint 'populated-nano-0123456789abcdef'

        Should -Invoke Invoke-LabControl -Times 1 -Exactly -ParameterFilter {
            $Script -eq 'Checkpoint-LabDomainController.ps1' -and
            ($Arguments -join ' ') -eq '-Name dc-source -Checkpoint populated-nano-0123456789abcdef -Replace'
        }
    }
}

Describe 'Get-ActiveDirectoryLabGuestBuild' {
    It 'maps each machine to its build string, in order, and says unknown when the guest was not reachable' {
        $states = @(
            @{ Instance = 'Primary'; VmName = 'dc-primary'; GuestBuild = '26100.1742' },
            @{ Instance = 'Source'; VmName = 'dc-source'; GuestBuild = $null }
        )

        $builds = Get-ActiveDirectoryLabGuestBuild -State $states

        @($builds.Keys) | Should -Be @('dc-primary', 'dc-source')
        $builds['dc-primary'] | Should -Be '26100.1742'
        $builds['dc-source'] | Should -Be 'unknown'
    }

    It 'formats as one line for the run summary' {
        $builds = [ordered]@{ 'dc-primary' = '26100.1742'; 'dc-source' = 'unknown' }

        Format-ActiveDirectoryLabGuestBuild -GuestBuilds $builds | Should -Be 'dc-primary 26100.1742, dc-source unknown'
    }

    It 'formats nothing as an empty string' {
        Format-ActiveDirectoryLabGuestBuild -GuestBuilds ([ordered]@{}) | Should -Be ''
    }
}

Describe 'Get-LdapWriteOutcome' {
    It 'succeeds when the tool exits zero, counting the entries it applied' {
        $outcome = Get-LdapWriteOutcome -ExitCode 0 -Output @('adding new entry "CN=a,DC=x"', '', 'adding new entry "CN=b,DC=x"')

        $outcome.Succeeded | Should -BeTrue
        $outcome.Entries | Should -Be 2
        $outcome.Applied | Should -Be 2
        $outcome.AcceptedCount | Should -Be 0
    }

    It 'succeeds with no output at all when the exit code is zero' {
        (Get-LdapWriteOutcome -ExitCode 0 -Output @()).Succeeded | Should -BeTrue
    }

    It 'accepts the result codes it is told to (entry already exists) and does not count those entries as applied' {
        $output = @(
            'adding new entry "OU=Corp,DC=x"',
            'ldap_add: Already exists (68)',
            "`tadditional info: 00002071: UpdErr: DSID-0315251D, problem 6005 (ENTRY_EXISTS)",
            'adding new entry "OU=New,DC=x"'
        )

        $outcome = Get-LdapWriteOutcome -ExitCode 68 -Output $output -AcceptedResultCode 68

        $outcome.Succeeded | Should -BeTrue
        $outcome.Entries | Should -Be 2
        $outcome.Applied | Should -Be 1
        $outcome.AcceptedCount | Should -Be 1
        @($outcome.Failures).Count | Should -Be 0
    }

    It 'fails on a result code it was not told to accept, and names the entry it belongs to' {
        $output = @(
            'adding new entry "CN=Ada,OU=Users,DC=x"',
            'ldap_add: Constraint violation (19)',
            "`tadditional info: 0000052D: SvcErr: DSID-031A11D5, problem 5003 (WILL_NOT_PERFORM)"
        )

        $outcome = Get-LdapWriteOutcome -ExitCode 19 -Output $output -AcceptedResultCode 68

        $outcome.Succeeded | Should -BeFalse
        @($outcome.Failures).Count | Should -Be 1
        $outcome.Failures[0] | Should -BeLike '*CN=Ada,OU=Users,DC=x*'
        $outcome.Failures[0] | Should -BeLike '*Constraint violation (19)*'
        $outcome.Failures[0] | Should -BeLike '*0000052D*'
    }

    It 'fails when an accepted and an unaccepted code are mixed, reporting only the unaccepted one' {
        $output = @(
            'adding new entry "OU=Corp,DC=x"', 'ldap_add: Already exists (68)',
            'adding new entry "CN=Ada,DC=x"', 'ldap_add: Constraint violation (19)'
        )

        $outcome = Get-LdapWriteOutcome -ExitCode 19 -Output $output -AcceptedResultCode 68

        $outcome.Succeeded | Should -BeFalse
        $outcome.AcceptedCount | Should -Be 1
        @($outcome.Failures).Count | Should -Be 1
    }

    It 'fails on a non-zero exit code with output it cannot classify (a docker or connection error), quoting the output' {
        $outcome = Get-LdapWriteOutcome -ExitCode 1 -Output @('Error response from daemon: No such container: jim-ldap-toolbox')

        $outcome.Succeeded | Should -BeFalse
        $outcome.Failures[0] | Should -BeLike '*No such container*'
    }

    It 'fails a bind that was refused' {
        $outcome = Get-LdapWriteOutcome -ExitCode 49 -Output @('ldap_bind: Invalid credentials (49)') -AcceptedResultCode 68

        $outcome.Succeeded | Should -BeFalse
        $outcome.Failures[0] | Should -BeLike '*Invalid credentials (49)*'
    }

    It 'reads a tool error whose operation carries a parenthesised detail, as ldap_sasl_bind(SIMPLE) does' {
        $outcome = Get-LdapWriteOutcome -ExitCode 255 -Output @("ldap_sasl_bind(SIMPLE): Can't contact LDAP server (-1)")

        $outcome.Succeeded | Should -BeFalse
        $outcome.Failures[0] | Should -BeLike "*Can't contact LDAP server (-1)*"
    }

    It 'treats an unaccepted code as a failure even when the exit code is zero' {
        $output = @('modifying entry "CN=g,DC=x"', 'ldap_modify: Insufficient access (50)')

        (Get-LdapWriteOutcome -ExitCode 0 -Output $output).Succeeded | Should -BeFalse
    }
}

Describe 'Get-LdapOrganizationalUnitLdif' {
    It 'builds an organizationalUnit entry named from the first RDN' {
        $ldif = Get-LdapOrganizationalUnitLdif -Dn 'OU=Entitlements,OU=Corp,DC=resurgam,DC=local'

        $ldif | Should -Be ("dn: OU=Entitlements,OU=Corp,DC=resurgam,DC=local`nobjectClass: top`nobjectClass: organizationalUnit`nou: Entitlements`n")
    }

    It 'refuses a DN whose first RDN is not an OU' {
        { Get-LdapOrganizationalUnitLdif -Dn 'CN=Users,DC=x,DC=local' } | Should -Throw
    }
}

Describe 'Get-LdapMemberAddLdif' {
    BeforeAll {
        $script:groupDn = 'CN=Company-Panoply,OU=Entitlements,OU=Corp,DC=resurgam,DC=local'
        function Get-MemberDn { param([int]$Count) 1..$Count | ForEach-Object { "CN=User $_,OU=Users,OU=Corp,DC=resurgam,DC=local" } }
    }

    It 'adds members to the group with one modify record' {
        $ldif = Get-LdapMemberAddLdif -GroupDn $script:groupDn -MemberDn (Get-MemberDn 2) -ChunkSize 500

        $ldif | Should -Be ("dn: $($script:groupDn)`nchangetype: modify`nadd: member`nmember: CN=User 1,OU=Users,OU=Corp,DC=resurgam,DC=local`nmember: CN=User 2,OU=Users,OU=Corp,DC=resurgam,DC=local`n")
    }

    It 'splits a large membership into records of at most the chunk size, separated by a blank line' {
        $ldif = Get-LdapMemberAddLdif -GroupDn $script:groupDn -MemberDn (Get-MemberDn 1200) -ChunkSize 500

        $records = @($ldif -split "`n`n" | Where-Object { $_.Trim() })
        $records.Count | Should -Be 3
        @($records | ForEach-Object { @([regex]::Matches($_, '(?m)^member: ')).Count }) | Should -Be @(500, 500, 200)
        foreach ($record in $records) {
            $record | Should -BeLike "dn: $($script:groupDn)*changetype: modify*add: member*"
        }
    }

    It 'does not lose or repeat a member across chunks' {
        $members = Get-MemberDn 1201
        $ldif = Get-LdapMemberAddLdif -GroupDn $script:groupDn -MemberDn $members -ChunkSize 500

        $emitted = @([regex]::Matches($ldif, '(?m)^member: (.+)$') | ForEach-Object { $_.Groups[1].Value })
        $emitted | Should -Be $members
    }

    It 'is empty for a group with no members' {
        Get-LdapMemberAddLdif -GroupDn $script:groupDn -MemberDn @() -ChunkSize 500 | Should -Be ''
    }
}

Describe 'Invoke-ActiveDirectoryLdifDelivery' {
    BeforeEach {
        $script:config = Get-TestConfig 'Source'
        Mock Invoke-LdapTool { @{ ExitCode = 0; Output = @('adding new entry "CN=a,DC=x"') } }
    }

    It 'delivers through Invoke-LdapTool as the domain administrator over LDAPS, continuing past entries that already exist' {
        Invoke-ActiveDirectoryLdifDelivery -DirectoryConfig $script:config -Tool ldapadd -Ldif "dn: CN=a,DC=x`n" | Out-Null

        Should -Invoke Invoke-LdapTool -Times 1 -Exactly -ParameterFilter {
            $Tool -eq 'ldapadd' -and
            $PassThruExitCode -and
            $InputObject -eq "dn: CN=a,DC=x`n" -and
            ($Arguments -join ' ') -eq "-x -c -H ldaps://dc1.resurgam.local:636 -D CN=Administrator,CN=Users,DC=resurgam,DC=local -w placeholder-not-a-secret"
        }
    }

    It 'supports ldapmodify' {
        Invoke-ActiveDirectoryLdifDelivery -DirectoryConfig $script:config -Tool ldapmodify -Ldif "dn: CN=a,DC=x`n" | Out-Null

        Should -Invoke Invoke-LdapTool -Times 1 -Exactly -ParameterFilter { $Tool -eq 'ldapmodify' }
    }

    It 'returns the outcome, with the count of entries applied' {
        $outcome = Invoke-ActiveDirectoryLdifDelivery -DirectoryConfig $script:config -Tool ldapadd -Ldif "dn: CN=a,DC=x`n"

        $outcome.Succeeded | Should -BeTrue
        $outcome.Applied | Should -Be 1
    }

    It 'throws on a result code it was not told to accept, naming what failed and never the password' {
        Mock Invoke-LdapTool { @{ ExitCode = 19; Output = @('adding new entry "CN=Ada,DC=x"', 'ldap_add: Constraint violation (19)') } }

        $message = $null
        try {
            Invoke-ActiveDirectoryLdifDelivery -DirectoryConfig $script:config -Tool ldapadd -Ldif "dn: CN=Ada,DC=x`n" -Description 'users chunk 1'
        }
        catch { $message = $_.Exception.Message }

        $message | Should -BeLike '*users chunk 1*'
        $message | Should -BeLike '*CN=Ada,DC=x*'
        $message | Should -BeLike '*Constraint violation (19)*'
        $message | Should -Not -BeLike '*placeholder-not-a-secret*'
    }

    It 'does not throw on a result code it was told to accept' {
        Mock Invoke-LdapTool { @{ ExitCode = 68; Output = @('adding new entry "OU=Corp,DC=x"', 'ldap_add: Already exists (68)') } }

        { Invoke-ActiveDirectoryLdifDelivery -DirectoryConfig $script:config -Tool ldapadd -Ldif "dn: OU=Corp,DC=x`n" -AcceptedResultCode 68 } |
            Should -Not -Throw
    }

    It 'refuses a config that is not an ActiveDirectory config, so a Samba container is never driven this way by mistake' {
        $sambaConfig = @{ DirectoryType = 'SambaAD'; ContainerName = 'samba-ad-source'; BindDN = 'x'; BindPassword = 'y' }

        { Invoke-ActiveDirectoryLdifDelivery -DirectoryConfig $sambaConfig -Tool ldapadd -Ldif 'dn: x' } | Should -Throw '*ActiveDirectory*'
        Should -Invoke Invoke-LdapTool -Times 0 -Exactly
    }

    It 'does nothing for an empty LDIF' {
        $outcome = Invoke-ActiveDirectoryLdifDelivery -DirectoryConfig $script:config -Tool ldapadd -Ldif ''

        $outcome.Succeeded | Should -BeTrue
        $outcome.Applied | Should -Be 0
        Should -Invoke Invoke-LdapTool -Times 0 -Exactly
    }
}

Describe 'Run-IntegrationTests.ps1 handling of the Active Directory only scenarios' {
    BeforeAll {
        # The runner cannot be run without Docker and the lab. Which directory types Scenarios 24 and 25 (and
        # every other scenario) run on is decided by the table in utils/Get-ScenarioDirectoryTypes.ps1, and the
        # runner's wiring to it is proven in Get-ScenarioDirectoryTypes.Tests.ps1; what is left here is that the
        # runner parses and treats the two as independent of the template.
        $script:runnerPath = Join-Path $PSScriptRoot '..' 'Run-IntegrationTests.ps1'
        $script:runnerText = Get-Content -LiteralPath $script:runnerPath -Raw
    }

    It 'parses' {
        $tokens = $null
        $parseErrors = $null
        [System.Management.Automation.Language.Parser]::ParseFile($script:runnerPath, [ref]$tokens, [ref]$parseErrors) | Out-Null

        @($parseErrors).Count | Should -Be 0
    }

    It 'treats Scenarios 24 and 25 as independent of the template, like the other scenarios with fixed data' {
        $array = [regex]::Match($script:runnerText, '(?s)\$templateIrrelevantScenarioNumbers\s*=\s*@\((?<body>.*?)\r?\n\)')
        $array.Success | Should -BeTrue

        $numbers = @([regex]::Matches($array.Groups['body'].Value, '(?m)^\s*(\d+)\s*,?') | ForEach-Object { [int]$_.Groups[1].Value })
        $numbers | Should -Contain 24
        $numbers | Should -Contain 25
    }

    It 'treats Scenario 027 as independent of the template: the tutorial''s sample data is fixed at 25 people' {
        $array = [regex]::Match($script:runnerText, '(?s)\$templateIrrelevantScenarioNumbers\s*=\s*@\((?<body>.*?)\r?\n\)')
        $numbers = @([regex]::Matches($array.Groups['body'].Value, '(?m)^\s*(\d+)\s*,?') | ForEach-Object { [int]$_.Groups[1].Value })
        $numbers | Should -Contain 27
    }
}

Describe 'Test-PasswordContainsToken' {
    It 'finds a token in any case, which is how Active Directory compares a name with a password' {
        Test-PasswordContainsToken -Password 'xx-WINTERGREEN-9!' -Token 'Wintergreen' | Should -BeTrue
        Test-PasswordContainsToken -Password 'xx-wintergreen-9!' -Token 'WINTERGREEN' | Should -BeTrue
    }

    It 'does not find a token that is not there' {
        Test-PasswordContainsToken -Password 'Copper-Fernleaf-2286!' -Token 'Wintergreen' | Should -BeFalse
    }

    It 'ignores a token shorter than three characters, as Active Directory does' {
        Test-PasswordContainsToken -Password 'Copper-Fernleaf-2286!' -Token 'Co' | Should -BeFalse
        Test-PasswordContainsToken -Password 'Copper-Fernleaf-2286!' -Token '' | Should -BeFalse
    }

    It 'finds a token of exactly three characters' {
        Test-PasswordContainsToken -Password 'Copper-Fernleaf-2286!' -Token 'per' | Should -BeTrue
    }
}

Describe 'Get-ActiveDirectoryFixturePassword' {
    It 'is as long as asked, 20 characters by default' {
        (Get-ActiveDirectoryFixturePassword).Length | Should -Be 20
        (Get-ActiveDirectoryFixturePassword -Length 12).Length | Should -Be 12
        (Get-ActiveDirectoryFixturePassword -Length 64).Length | Should -Be 64
    }

    It 'draws on all four character classes Active Directory counts, every time, even at the shortest length' {
        foreach ($attempt in 1..200) {
            $password = Get-ActiveDirectoryFixturePassword -Length 12

            $password | Should -MatchExactly '[A-Z]'
            $password | Should -MatchExactly '[a-z]'
            $password | Should -Match '[0-9]'
            $password | Should -Match '[^A-Za-z0-9]'
        }
    }

    It 'never starts with a symbol, so it is safe as the value of an LDAP tool option' {
        foreach ($attempt in 1..200) {
            (Get-ActiveDirectoryFixturePassword -Length 12) | Should -Match '^[A-Za-z0-9]'
        }
    }

    It 'is different every time' {
        $passwords = @(1..50 | ForEach-Object { Get-ActiveDirectoryFixturePassword })

        @($passwords | Select-Object -Unique).Count | Should -Be 50
    }

    It 'leaves out every token it was told to avoid' {
        # The characters a token is made of are all in the alphabet, so a token of three digits is one the
        # generator can produce by chance; over many draws it must never let one through.
        foreach ($attempt in 1..300) {
            $password = Get-ActiveDirectoryFixturePassword -Length 12 -Avoid '234', '345', '456', 'abc'

            Test-PasswordContainsToken -Password $password -Token '234' | Should -BeFalse
            Test-PasswordContainsToken -Password $password -Token '345' | Should -BeFalse
            Test-PasswordContainsToken -Password $password -Token '456' | Should -BeFalse
            Test-PasswordContainsToken -Password $password -Token 'abc' | Should -BeFalse
        }
    }

    It 'refuses a length too short to be a policy-compliant password' {
        { Get-ActiveDirectoryFixturePassword -Length 8 } | Should -Throw
    }
}

Describe 'Get-ActiveDirectoryLabModuleDigest' {
    It 'is the digest of the checkout''s PowerShell module, as the host computes it over its copy' {
        Import-Module (Join-Path $PSScriptRoot '..' 'ad-lab' 'guest' 'LabFederationServer.psm1') -Force
        $root = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..' '..' '..'))
        $expected = Get-LabModuleDigest -Entry @(Read-LabModuleFile -Root (Join-Path $root 'src' 'JIM.PowerShell'))
        Get-ActiveDirectoryLabModuleDigest -RepositoryRoot $root | Should -Be $expected
    }
}

Describe 'ConvertFrom-LabIdentityProviderRegistrationOutput' {
    BeforeAll {
        $script:Json = ([ordered]@{
                authority = 'https://adfs.panoply.local/adfs'; clientId = '11111111-1111-1111-1111-111111111111'
                clientSecret = 'placeholder-client-secret'; publicClientId = '22222222-2222-2222-2222-222222222222'
                apiScope = 'api://11111111-1111-1111-1111-111111111111'; claimType = 'oid'; mvAttribute = 'Object GUID'
                changes = @('Create the Application Group'); moduleDigest = ('a' * 64)
            } | ConvertTo-Json -Compress)
    }

    It 'reads the JSON the host printed last, past any line before it' {
        $registration = ConvertFrom-LabIdentityProviderRegistrationOutput -Text "WARNING: something`n$($script:Json)`n"
        $registration.ClientId | Should -Be '11111111-1111-1111-1111-111111111111'
        $registration.ClientSecret | Should -Be 'placeholder-client-secret'
        $registration.PublicClientId | Should -Be '22222222-2222-2222-2222-222222222222'
        $registration.ApiScope | Should -Be 'api://11111111-1111-1111-1111-111111111111'
        $registration.ClaimType | Should -Be 'oid'
        $registration.MvAttribute | Should -Be 'Object GUID'
    }

    It 'refuses output with no JSON, naming none of it' {
        { ConvertFrom-LabIdentityProviderRegistrationOutput -Text 'not json at all' } | Should -Throw '*not the JSON*'
    }

    It 'refuses JSON without a client secret' {
        $json = $script:Json | ConvertFrom-Json
        $json.clientSecret = ''
        { ConvertFrom-LabIdentityProviderRegistrationOutput -Text ($json | ConvertTo-Json -Compress) } | Should -Throw '*clientSecret*'
    }
}

Describe 'Register-ActiveDirectoryLabIdentityProvider' {
    BeforeEach {
        $script:Previous = @{ Actions = $env:GITHUB_ACTIONS; Env = $env:GITHUB_ENV }
        $script:EnvFile = Join-Path ([System.IO.Path]::GetTempPath()) "jim-github-env-$([guid]::NewGuid().ToString('N'))"
        $env:GITHUB_ACTIONS = $null
        $env:GITHUB_ENV = $null
        Mock Invoke-LabControl {
            return ([ordered]@{
                    authority = 'https://adfs.panoply.local/adfs'; clientId = 'c1'; clientSecret = 'placeholder-client-secret'
                    publicClientId = 'p1'; apiScope = 'api://c1'; claimType = 'oid'; mvAttribute = 'Object GUID'
                    changes = @(); moduleDigest = ('b' * 64)
                } | ConvertTo-Json -Compress)
        }
    }

    AfterEach {
        $env:GITHUB_ACTIONS = $script:Previous.Actions
        $env:GITHUB_ENV = $script:Previous.Env
        Remove-Item -LiteralPath $script:EnvFile -ErrorAction SilentlyContinue
    }

    It 'runs Register-LabIdentityProvider.ps1 on the host for the AD FS server, the URL and the digest' {
        $null = Register-ActiveDirectoryLabIdentityProvider -FederationConfig (Get-TestFederationConfig) -Url 'https://localhost:5243' -ModuleDigest ('b' * 64)
        Should -Invoke Invoke-LabControl -Times 1 -ParameterFilter {
            $Script -eq 'Register-LabIdentityProvider.ps1' -and
            (($Arguments -join ' ') -eq "-Name adfs-primary -Url https://localhost:5243 -ModuleDigest $('b' * 64)")
        }
    }

    It 'returns the registration' {
        $registration = Register-ActiveDirectoryLabIdentityProvider -FederationConfig (Get-TestFederationConfig) -Url 'https://localhost:5243' -ModuleDigest ('b' * 64)
        $registration.ClientId | Should -Be 'c1'
        $registration.ClientSecret | Should -Be 'placeholder-client-secret'
    }

    It 'in Actions, masks the client secret in the log and hands it to the scrub step, but writes it nowhere else' {
        $env:GITHUB_ACTIONS = 'true'
        $env:GITHUB_ENV = $script:EnvFile
        $output = Register-ActiveDirectoryLabIdentityProvider -FederationConfig (Get-TestFederationConfig) -Url 'https://localhost:5243' -ModuleDigest ('b' * 64) 6>&1 | Out-String
        $output | Should -Match '::add-mask::placeholder-client-secret'
        (Get-Content -LiteralPath $script:EnvFile -Raw) | Should -Match '(?m)^JIM_AD_LAB_SCRUB_[0-9a-f]{32}=placeholder-client-secret\r?$'
    }
}

Describe 'Set-ActiveDirectoryLabSsoEnvironment' {
    AfterEach {
        foreach ($name in 'JIM_AD_LAB_ADFS_CLIENT_ID', 'JIM_AD_LAB_ADFS_CLIENT_SECRET', 'JIM_AD_LAB_ADFS_API_SCOPE', 'JIM_AD_LAB_ADFS_PUBLIC_CLIENT_ID') {
            [Environment]::SetEnvironmentVariable($name, $null)
        }
    }

    It 'puts the registration where docker-compose.ad-lab-adfs.yml reads it, in the process environment' {
        Set-ActiveDirectoryLabSsoEnvironment -Registration ([pscustomobject]@{ ClientId = 'c1'; ClientSecret = 'placeholder-client-secret'; ApiScope = 'api://c1'; PublicClientId = 'p1' })
        $env:JIM_AD_LAB_ADFS_CLIENT_ID | Should -Be 'c1'
        $env:JIM_AD_LAB_ADFS_CLIENT_SECRET | Should -Be 'placeholder-client-secret'
        $env:JIM_AD_LAB_ADFS_API_SCOPE | Should -Be 'api://c1'
        $env:JIM_AD_LAB_ADFS_PUBLIC_CLIENT_ID | Should -Be 'p1'
    }

    It 'is what the overlay names' {
        $overlay = Get-Content -LiteralPath (Join-Path $PSScriptRoot '..' 'docker' 'docker-compose.ad-lab-adfs.yml') -Raw
        foreach ($name in 'JIM_AD_LAB_ADFS_CLIENT_ID', 'JIM_AD_LAB_ADFS_CLIENT_SECRET', 'JIM_AD_LAB_ADFS_API_SCOPE', 'JIM_AD_LAB_ADFS_PUBLIC_CLIENT_ID') {
            $overlay | Should -Match ([regex]::Escape("`${$name"))
        }
    }
}
