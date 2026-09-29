# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

#Requires -Modules Pester

<#
.SYNOPSIS
    Pester tests for Get-DirectoryConfig, Test-IsRfcDirectory and Add-DirectoryCertificateToJimStore in
    Test-Helpers.ps1.

.DESCRIPTION
    Guards the directory-config contract the scenario scripts and the runner build on: every
    directory type and instance returns a hashtable stamped with its DirectoryType; the 389
    Directory Server configs mirror the OpenLDAP ones key for key (the two share populate scripts
    and scenarios); and Test-IsRfcDirectory answers the RFC-versus-AD question for every type and
    refuses an unknown one; Get-DirectoryConfig's ActiveDirectory type (the real-domain-controller lab
    on Hyper-V, configured from JIM_AD_LAB_* environment variables) and its certificate-trust helpers;
    and Add-DirectoryCertificateToJimStore re-trusts the right directory's CA
    (or none, for OpenLDAP) from a config's DirectoryType.
#>

BeforeAll {
    . "$PSScriptRoot/Test-Helpers.ps1"

    # Every variable the Active Directory config reads. Tests set what they need and always clear
    # them again, so a value never leaks into another test or into the runner's own environment.
    $script:adLabVariables = @(
        'JIM_AD_LAB_PRIMARY_ADDRESS', 'JIM_AD_LAB_SOURCE_ADDRESS', 'JIM_AD_LAB_TARGET_ADDRESS',
        'JIM_AD_LAB_PRIMARY_HOST', 'JIM_AD_LAB_SOURCE_HOST', 'JIM_AD_LAB_TARGET_HOST',
        'JIM_AD_LAB_PRIMARY_VM', 'JIM_AD_LAB_SOURCE_VM', 'JIM_AD_LAB_TARGET_VM',
        'JIM_AD_LAB_ADMIN_PASSWORD', 'JIM_AD_LAB_JIM_PASSWORD'
    )
    function Clear-AdLabEnvironment {
        foreach ($name in $script:adLabVariables) {
            [System.Environment]::SetEnvironmentVariable($name, $null)
        }
    }
    function Initialize-AdLabEnvironment {
        $env:JIM_AD_LAB_PRIMARY_ADDRESS = '192.0.2.10'
        $env:JIM_AD_LAB_SOURCE_ADDRESS = '192.0.2.11'
        $env:JIM_AD_LAB_TARGET_ADDRESS = '192.0.2.12'
        $env:JIM_AD_LAB_ADMIN_PASSWORD = 'admin-pw-for-test'
        $env:JIM_AD_LAB_JIM_PASSWORD = 'jim-pw-for-test'
    }

    $script:directoryTypes = @('SambaAD', 'OpenLDAP', 'DirectoryServer389')
    $script:instances = @('Primary', 'Source', 'Target')
    $script:allPairs = foreach ($type in $script:directoryTypes) {
        foreach ($instance in $script:instances) {
            @{ DirectoryType = $type; Instance = $instance }
        }
    }
}

Describe 'Get-DirectoryConfig' {
    Context 'every directory type and instance' {
        It 'returns a hashtable stamped with its DirectoryType for <DirectoryType>/<Instance>' -ForEach @(
            @{ DirectoryType = 'SambaAD';            Instance = 'Primary' }
            @{ DirectoryType = 'SambaAD';            Instance = 'Source' }
            @{ DirectoryType = 'SambaAD';            Instance = 'Target' }
            @{ DirectoryType = 'OpenLDAP';           Instance = 'Primary' }
            @{ DirectoryType = 'OpenLDAP';           Instance = 'Source' }
            @{ DirectoryType = 'OpenLDAP';           Instance = 'Target' }
            @{ DirectoryType = 'DirectoryServer389'; Instance = 'Primary' }
            @{ DirectoryType = 'DirectoryServer389'; Instance = 'Source' }
            @{ DirectoryType = 'DirectoryServer389'; Instance = 'Target' }
        ) {
            $config = Get-DirectoryConfig -DirectoryType $DirectoryType -Instance $Instance

            $config | Should -BeOfType [hashtable]
            $config.ContainsKey('DirectoryType') | Should -BeTrue
            $config.DirectoryType | Should -Be $DirectoryType
        }

        It 'carries both bind identities on every config' {
            foreach ($pair in $script:allPairs) {
                $config = Get-DirectoryConfig @pair
                foreach ($key in 'BindDN', 'BindPassword', 'JimBindDN', 'JimBindPassword', 'ContainerName', 'Host', 'Port', 'BaseDN', 'ComposeProfiles', 'PopulateScript', 'ConnectedSystemName') {
                    $config.ContainsKey($key) | Should -BeTrue -Because "$($pair.DirectoryType)/$($pair.Instance) must carry $key"
                }
            }
        }
        It 'carries the in-container LdapSearchPort and LdapSearchScheme on every config' {
            # Every config holds two port facts. Port/UseSSL is what JIM's Connected System connects
            # with, from the JIM container over the Docker network. LdapSearchPort/LdapSearchScheme is
            # what the harness's own docker exec ldapsearch/ldapmodify/ldapadd/ldapdelete calls must
            # use inside the directory container. Scenario, populate and helper scripts build their
            # in-container URIs from the latter pair, never from Port (see the 'in-container LDAP
            # URIs' Describe below).
            foreach ($pair in $script:allPairs) {
                $config = Get-DirectoryConfig @pair
                $label = "$($pair.DirectoryType)/$($pair.Instance)"
                $config.ContainsKey('LdapSearchPort')   | Should -BeTrue -Because "$label must say which in-container port the harness's ldap tools use"
                $config.ContainsKey('LdapSearchScheme') | Should -BeTrue -Because "$label must say which scheme the harness's ldap tools use"
                $config.LdapSearchPort   | Should -BeOfType [int]
                $config.LdapSearchPort   | Should -BeGreaterThan 0
                $config.LdapSearchScheme | Should -BeIn @('ldap', 'ldaps') -Because "$label must name a scheme the OpenLDAP client tools accept"
            }
        }

    }

    Context 'DirectoryServer389' {
        BeforeAll {
            $script:primary = Get-DirectoryConfig -DirectoryType DirectoryServer389 -Instance Primary
        }

        It 'Primary binds as Directory Manager over LDAPS 3636 under the dirsrv compose profile' {
            $script:primary.ContainerName   | Should -Be 'dirsrv-primary'
            $script:primary.Host            | Should -Be 'dirsrv-primary'
            $script:primary.Port            | Should -Be 3636
            $script:primary.UseSSL          | Should -BeTrue
            $script:primary.BindDN          | Should -Be 'cn=Directory Manager'
            $script:primary.BindPassword    | Should -Be 'Test@123!'
            $script:primary.SecondBindDN    | Should -Be 'cn=Directory Manager'
            $script:primary.ComposeProfiles | Should -Be @('dirsrv')
        }

        It 'Primary binds JIM as the delegated service accounts with the shared lab passwords' {
            $script:primary.JimBindDN                     | Should -Be 'cn=svc-jim,ou=Services,dc=yellowstone,dc=local'
            $script:primary.JimBindPassword               | Should -Be 'Svc-Jim@123!'
            $script:primary.MultiPartitionJimBindDN       | Should -Be 'cn=svc-jim-partitions,ou=Services,dc=yellowstone,dc=local'
            $script:primary.MultiPartitionJimBindPassword | Should -Be 'Svc-Jim-Partitions@123!'
            $script:primary.SecondJimBindDN               | Should -Be 'cn=svc-jim,ou=Services,dc=glitterband,dc=local'
        }

        It 'connects the <Instance> Connected System over LDAPS 3636 and keeps the in-container ldapsearch checks on plain LDAP 3389' -ForEach @(
            @{ Instance = 'Primary' }
            @{ Instance = 'Source' }
            @{ Instance = 'Target' }
        ) {
            # 389 accepts Password Modify only over a secure connection; the harness's own docker exec
            # ldapsearch checks stay on the container's plain LDAP port.
            $dirsrv = Get-DirectoryConfig -DirectoryType DirectoryServer389 -Instance $Instance
            $dirsrv.Port             | Should -Be 3636
            $dirsrv.UseSSL           | Should -BeTrue
            $dirsrv.LdapSearchPort   | Should -Be 3389
            $dirsrv.LdapSearchScheme | Should -Be 'ldap'
        }

        It 'listens for the <Instance> Connected System on a different port from the in-container ldap tools' -ForEach @(
            @{ Instance = 'Primary' }
            @{ Instance = 'Source' }
            @{ Instance = 'Target' }
        ) {
            # The two port facts are allowed to differ, and on 389 they do (LDAPS 3636 for JIM, plain
            # LDAP 3389 inside the container). Samba AD and OpenLDAP happen to reach the same
            # listener either way (389 with ldap, 1389 with ldap), which is how scripts that built an
            # in-container URI from Port went unnoticed: on 389 such a URI talks plain LDAP to the
            # LDAPS port and hangs forever.
            $dirsrv = Get-DirectoryConfig -DirectoryType DirectoryServer389 -Instance $Instance
            $dirsrv.Port | Should -Not -Be $dirsrv.LdapSearchPort
            "$($dirsrv.LdapSearchScheme)://localhost:$($dirsrv.LdapSearchPort)" | Should -Be 'ldap://localhost:3389'
        }

        It 'shares the OpenLDAP populate script and the two-suffix model' {
            $script:primary.PopulateScript | Should -Be 'Populate-OpenLDAP.ps1'
            $script:primary.BaseDN         | Should -Be 'dc=yellowstone,dc=local'
            $script:primary.SecondSuffix   | Should -Be 'dc=glitterband,dc=local'
        }

        It 'mirrors the OpenLDAP <Instance> instance key for key' -ForEach @(
            @{ Instance = 'Primary' }
            @{ Instance = 'Source' }
            @{ Instance = 'Target' }
        ) {
            $openLdap = Get-DirectoryConfig -DirectoryType OpenLDAP -Instance $Instance
            $dirsrv = Get-DirectoryConfig -DirectoryType DirectoryServer389 -Instance $Instance

            $dirsrvKeys = @($dirsrv.Keys | Sort-Object)
            $openLdapKeys = @($openLdap.Keys | Sort-Object)
            $dirsrvKeys | Should -Be $openLdapKeys

            # Everything that describes the tree rather than the server is identical, because the
            # populate scripts and scenario assertions are shared between the two.
            foreach ($key in 'BaseDN', 'UserContainer', 'GroupContainer', 'UserObjectClass', 'GroupObjectClass', 'UserRdnAttr', 'UserNameAttr', 'ExternalIdAttr', 'DepartmentAttr', 'DeleteBehaviour', 'DnTemplate', 'Domain', 'JimBindDN', 'JimBindPassword', 'PopulateScript') {
                $dirsrv[$key] | Should -Be $openLdap[$key] -Because "$key must match OpenLDAP's $Instance instance"
            }
        }

        It 'keeps the Source and Target Connected System names the scenarios assert on' {
            (Get-DirectoryConfig -DirectoryType DirectoryServer389 -Instance Source).ConnectedSystemName | Should -Be 'Yellowstone APAC'
            (Get-DirectoryConfig -DirectoryType DirectoryServer389 -Instance Target).ConnectedSystemName | Should -Be 'Glitterband EMEA'
            (Get-DirectoryConfig -DirectoryType DirectoryServer389 -Instance Primary).ConnectedSystemName | Should -Be 'Yellowstone Directory Server'
        }
    }

    Context 'unknown input' {
        It 'throws on an unknown <DirectoryType> instance' -ForEach @(
            @{ DirectoryType = 'SambaAD' }
            @{ DirectoryType = 'OpenLDAP' }
            @{ DirectoryType = 'DirectoryServer389' }
            @{ DirectoryType = 'ActiveDirectory' }
        ) {
            { Get-DirectoryConfig -DirectoryType $DirectoryType -Instance 'Nowhere' } | Should -Throw "*Unknown $DirectoryType instance: Nowhere*"
        }

        It 'rejects an unknown directory type at the parameter' {
            { Get-DirectoryConfig -DirectoryType 'eDirectory' -Instance 'Primary' } | Should -Throw
        }
    }
}

Describe 'Test-IsRfcDirectory' {
    It 'answers <Expected> for <DirectoryType>' -ForEach @(
        @{ DirectoryType = 'OpenLDAP';           Expected = $true }
        @{ DirectoryType = 'DirectoryServer389'; Expected = $true }
        @{ DirectoryType = 'SambaAD';            Expected = $false }
    ) {
        foreach ($instance in 'Primary', 'Source', 'Target') {
            $config = Get-DirectoryConfig -DirectoryType $DirectoryType -Instance $instance
            Test-IsRfcDirectory $config | Should -Be $Expected -Because "$DirectoryType/$instance"
        }
    }

    It 'answers false for ActiveDirectory' {
        Test-IsRfcDirectory @{ DirectoryType = 'ActiveDirectory' } | Should -BeFalse
    }

    It 'lists all four directory types in the error for an unknown one' {
        { Test-IsRfcDirectory @{ DirectoryType = 'eDirectory' } } | Should -Throw '*SambaAD, OpenLDAP, DirectoryServer389 or ActiveDirectory*'
    }

    It 'accepts the config positionally and by name' {
        $config = Get-DirectoryConfig -DirectoryType OpenLDAP
        Test-IsRfcDirectory $config | Should -BeTrue
        Test-IsRfcDirectory -DirectoryConfig $config | Should -BeTrue
    }

    It 'throws on an unknown DirectoryType' {
        { Test-IsRfcDirectory @{ DirectoryType = 'eDirectory' } } | Should -Throw '*unknown DirectoryType*'
    }

    It 'throws when the config carries no DirectoryType at all' {
        { Test-IsRfcDirectory @{ ContainerName = 'hand-built' } } | Should -Throw '*unknown DirectoryType*'
    }
}

Describe 'Add-DirectoryCertificateToJimStore' {
    <#
        The dispatcher a scenario calls to re-trust its directory after a factory reset. It must pick
        the right per-directory function from DirectoryType and hand it the config's ContainerName,
        so a scenario never runs the Samba function against dirsrv-primary again (the Scenario 010
        failure on the 389 lab). Both leaf functions are mocked: they docker cp and upload.
    #>
    BeforeAll {
        Mock Add-SambaCertificateToJimStore { }
        Mock Add-DirsrvCertificateToJimStore { }
        Mock Add-ActiveDirectoryCertificateToJimStore { }
        $script:jimUrl = 'http://localhost:5200'
        $script:apiKey = 'jim_test'
    }

    It 'returns without calling either leaf function for OpenLDAP' {
        $config = Get-DirectoryConfig -DirectoryType OpenLDAP -Instance Primary
        Add-DirectoryCertificateToJimStore -DirectoryConfig $config -JIMUrl $script:jimUrl -ApiKey $script:apiKey
        Should -Invoke Add-SambaCertificateToJimStore -Times 0 -Exactly
        Should -Invoke Add-DirsrvCertificateToJimStore -Times 0 -Exactly
    }

    It 'calls the Samba function with the config''s container for SambaAD' {
        $config = Get-DirectoryConfig -DirectoryType SambaAD -Instance Target
        Add-DirectoryCertificateToJimStore -DirectoryConfig $config -JIMUrl $script:jimUrl -ApiKey $script:apiKey
        Should -Invoke Add-SambaCertificateToJimStore -Times 1 -Exactly -ParameterFilter {
            $ContainerName -eq $config.ContainerName -and $JIMUrl -eq $script:jimUrl -and $ApiKey -eq $script:apiKey
        }
        Should -Invoke Add-DirsrvCertificateToJimStore -Times 0 -Exactly
    }

    It 'calls the dirsrv function with the config''s container for DirectoryServer389' {
        $config = Get-DirectoryConfig -DirectoryType DirectoryServer389 -Instance Primary
        Add-DirectoryCertificateToJimStore -DirectoryConfig $config -JIMUrl $script:jimUrl -ApiKey $script:apiKey
        Should -Invoke Add-DirsrvCertificateToJimStore -Times 1 -Exactly -ParameterFilter {
            $ContainerName -eq $config.ContainerName -and $JIMUrl -eq $script:jimUrl -and $ApiKey -eq $script:apiKey
        }
        Should -Invoke Add-SambaCertificateToJimStore -Times 0 -Exactly
    }

    It 'calls the Active Directory function with the whole config for ActiveDirectory, and neither container function' {
        Initialize-AdLabEnvironment
        try {
            $config = Get-DirectoryConfig -DirectoryType ActiveDirectory -Instance Primary
            Add-DirectoryCertificateToJimStore -DirectoryConfig $config -JIMUrl $script:jimUrl -ApiKey $script:apiKey
        }
        finally {
            Clear-AdLabEnvironment
        }
        Should -Invoke Add-ActiveDirectoryCertificateToJimStore -Times 1 -Exactly -ParameterFilter {
            $DirectoryConfig.VmName -eq 'dc-primary' -and $JIMUrl -eq $script:jimUrl -and $ApiKey -eq $script:apiKey
        }
        Should -Invoke Add-SambaCertificateToJimStore -Times 0 -Exactly
        Should -Invoke Add-DirsrvCertificateToJimStore -Times 0 -Exactly
    }

    It 'throws on an unknown DirectoryType' {
        { Add-DirectoryCertificateToJimStore -DirectoryConfig @{ DirectoryType = 'eDirectory'; ContainerName = 'x' } -JIMUrl $script:jimUrl -ApiKey $script:apiKey } |
            Should -Throw '*unknown DirectoryType*eDirectory*'
        Should -Invoke Add-SambaCertificateToJimStore -Times 0 -Exactly
        Should -Invoke Add-DirsrvCertificateToJimStore -Times 0 -Exactly
    }

    It 'throws when the config carries no DirectoryType at all' {
        { Add-DirectoryCertificateToJimStore -DirectoryConfig @{ ContainerName = 'hand-built' } -JIMUrl $script:jimUrl -ApiKey $script:apiKey } |
            Should -Throw '*unknown DirectoryType*'
    }
}

Describe 'in-container LDAP URIs' {
    <#
        Static guard over the harness scripts themselves. An OpenLDAP client tool run through docker
        exec inside a directory container must be pointed at LdapSearchScheme://localhost:LdapSearchPort.
        Building that URI from Port (JIM's Connected System port) or leaving the port off entirely
        (the tool then defaults to 389) only works where the two ports coincide, and on 389 Directory
        Server they do not. Scenario 001's Set-DirectoryUserAttributes hung on exactly this.
    #>
    BeforeAll {
        $integrationRoot = Split-Path -Parent $PSScriptRoot
        $script:harnessScripts = @(
            Get-ChildItem -Path (Join-Path $integrationRoot 'scenarios') -Filter '*.ps1' -File
            Get-ChildItem -Path $integrationRoot -Filter 'Setup-*.ps1' -File
            Get-ChildItem -Path $integrationRoot -Filter 'Populate-*.ps1' -File
            Get-ChildItem -Path $PSScriptRoot -Filter '*.ps1' -File | Where-Object { $_.Name -notlike '*.Tests.ps1' }
        )
        $script:ldapClientTools = 'ldapsearch|ldapmodify|ldapadd|ldapdelete|ldappasswd|ldapmodrdn|ldapwhoami|ldapcompare|ldapexop'
    }

    It 'finds the scripts it guards' {
        $script:harnessScripts.Count | Should -BeGreaterThan 20
    }

    It 'never builds an in-container LDAP URI from a config''s Port' {
        # ldap://localhost:$($X.Port), ldaps://localhost:$($X.Port), or a -Port $X.Port handed to
        # Invoke-LDAPSearch; every one of these must read LdapSearchPort instead.
        $pattern = 'ldaps?://localhost:\$\(\$\w+\.Port\)|Invoke-LDAPSearch\b[^\r\n]*-Port\s+\$\w+\.Port\b'
        $offenders = foreach ($file in $script:harnessScripts) {
            $lineNumber = 0
            foreach ($line in [System.IO.File]::ReadAllLines($file.FullName)) {
                $lineNumber++
                if ($line -match $pattern) { "$($file.Name):${lineNumber}: $($line.Trim())" }
            }
        }
        $offenders | Should -BeNullOrEmpty
    }

    It 'never runs an OpenLDAP client tool against ldap://localhost with no port' {
        # Inside the lab containers nothing listens on the client tools' default port 389 except
        # Samba AD, so a portless URI is a Samba-only assumption that breaks on the other two labs.
        $pattern = "\b($script:ldapClientTools)\b[^\r\n|]*-H\s+['`"]?ldaps?://localhost['`"]?(\s|$)"
        $offenders = foreach ($file in $script:harnessScripts) {
            $lineNumber = 0
            foreach ($line in [System.IO.File]::ReadAllLines($file.FullName)) {
                $lineNumber++
                if ($line -match $pattern) { "$($file.Name):${lineNumber}: $($line.Trim())" }
            }
        }
        $offenders | Should -BeNullOrEmpty
    }
}

Describe 'Get-ActiveDirectoryLabSetting' {
    BeforeEach { Clear-AdLabEnvironment }
    AfterEach { Clear-AdLabEnvironment }

    It 'returns the environment variable''s value when it is set' {
        $env:JIM_AD_LAB_PRIMARY_HOST = 'dc9.example.test'

        Get-ActiveDirectoryLabSetting -Name 'JIM_AD_LAB_PRIMARY_HOST' -Default 'dc1.panoply.local' | Should -Be 'dc9.example.test'
    }

    It 'returns the default when the variable is unset' {
        Get-ActiveDirectoryLabSetting -Name 'JIM_AD_LAB_PRIMARY_HOST' -Default 'dc1.panoply.local' | Should -Be 'dc1.panoply.local'
    }

    It 'returns the default when the variable is set to whitespace' {
        $env:JIM_AD_LAB_PRIMARY_HOST = '   '

        Get-ActiveDirectoryLabSetting -Name 'JIM_AD_LAB_PRIMARY_HOST' -Default 'dc1.panoply.local' | Should -Be 'dc1.panoply.local'
    }

    It 'throws naming the variable when it is required and unset' {
        { Get-ActiveDirectoryLabSetting -Name 'JIM_AD_LAB_PRIMARY_ADDRESS' } | Should -Throw '*JIM_AD_LAB_PRIMARY_ADDRESS*'
    }

    It 'throws naming the variable when it is required and empty' {
        $env:JIM_AD_LAB_ADMIN_PASSWORD = ''

        { Get-ActiveDirectoryLabSetting -Name 'JIM_AD_LAB_ADMIN_PASSWORD' } | Should -Throw '*JIM_AD_LAB_ADMIN_PASSWORD*'
    }
}

Describe 'Get-DirectoryConfig -DirectoryType ActiveDirectory' {
    BeforeEach {
        Clear-AdLabEnvironment
        Initialize-AdLabEnvironment
    }
    AfterEach { Clear-AdLabEnvironment }

    It 'is stamped ActiveDirectory for <Instance>' -ForEach @(
        @{ Instance = 'Primary' }, @{ Instance = 'Source' }, @{ Instance = 'Target' }
    ) {
        $config = Get-DirectoryConfig -DirectoryType ActiveDirectory -Instance $Instance

        $config | Should -BeOfType [hashtable]
        $config.DirectoryType | Should -Be 'ActiveDirectory'
    }

    It 'carries every key SambaAD carries, plus Address, VmName and CheckpointBaseline, for <Instance>' -ForEach @(
        @{ Instance = 'Primary' }, @{ Instance = 'Source' }, @{ Instance = 'Target' }
    ) {
        $samba = Get-DirectoryConfig -DirectoryType SambaAD -Instance $Instance
        $ad = Get-DirectoryConfig -DirectoryType ActiveDirectory -Instance $Instance

        $expected = @(@($samba.Keys) + @('Address', 'VmName', 'CheckpointBaseline') | Sort-Object -Unique)
        @($ad.Keys | Sort-Object) | Should -Be $expected
    }

    It 'is a real directory over LDAPS with no container, for <Instance>' -ForEach @(
        @{ Instance = 'Primary' }, @{ Instance = 'Source' }, @{ Instance = 'Target' }
    ) {
        $config = Get-DirectoryConfig -DirectoryType ActiveDirectory -Instance $Instance

        $config.ContainsKey('ContainerName') | Should -BeTrue
        $config.ContainerName | Should -BeNullOrEmpty
        $config.Port | Should -Be 636
        $config.UseSSL | Should -BeTrue
        $config.LdapSearchPort | Should -Be 636
        $config.LdapSearchScheme | Should -Be 'ldaps'
        $config.ComposeProfiles | Should -Be @('ad-lab')
        $config.PopulateScript | Should -Be 'Populate-SambaAD.ps1'
        $config.CheckpointBaseline | Should -Be 'baseline'
    }

    It 'describes the tree exactly as the SambaAD <Instance> instance does' -ForEach @(
        @{ Instance = 'Primary' }, @{ Instance = 'Source' }, @{ Instance = 'Target' }
    ) {
        $samba = Get-DirectoryConfig -DirectoryType SambaAD -Instance $Instance
        $ad = Get-DirectoryConfig -DirectoryType ActiveDirectory -Instance $Instance

        foreach ($key in 'BaseDN', 'UserContainer', 'GroupContainer', 'UserObjectClass', 'GroupObjectClass', 'UserRdnAttr', 'UserNameAttr',
                'ExternalIdAttr', 'DepartmentAttr', 'DeleteBehaviour', 'DisableAttribute', 'DnTemplate', 'Domain', 'ShortDomain', 'AuthType',
                'BindDN', 'JimBindDN', 'MultiPartitionJimBindDN', 'ConnectedSystemName') {
            $ad[$key] | Should -Be $samba[$key] -Because "$key must match SambaAD's $Instance instance"
        }
    }

    It 'binds as the domain Administrator and the delegated service account' {
        $config = Get-DirectoryConfig -DirectoryType ActiveDirectory -Instance Source

        $config.BindDN | Should -Be 'CN=Administrator,CN=Users,DC=resurgam,DC=local'
        $config.JimBindDN | Should -Be 'CN=svc-jim,OU=Services,DC=resurgam,DC=local'
        $config.MultiPartitionJimBindDN | Should -Be $config.JimBindDN
    }

    It 'names the Connected Systems Panoply AD, Resurgam AD and Gentian AD' {
        (Get-DirectoryConfig -DirectoryType ActiveDirectory -Instance Primary).ConnectedSystemName | Should -Be 'Panoply AD'
        (Get-DirectoryConfig -DirectoryType ActiveDirectory -Instance Source).ConnectedSystemName | Should -Be 'Resurgam AD'
        (Get-DirectoryConfig -DirectoryType ActiveDirectory -Instance Target).ConnectedSystemName | Should -Be 'Gentian AD'
    }

    It 'defaults the host and VM names from the topology' {
        $primary = Get-DirectoryConfig -DirectoryType ActiveDirectory -Instance Primary
        $source = Get-DirectoryConfig -DirectoryType ActiveDirectory -Instance Source
        $target = Get-DirectoryConfig -DirectoryType ActiveDirectory -Instance Target

        $primary.Host | Should -Be 'dc1.panoply.local'
        $primary.VmName | Should -Be 'dc-primary'
        $source.Host | Should -Be 'dc1.resurgam.local'
        $source.VmName | Should -Be 'dc-source'
        $target.Host | Should -Be 'dc1.gentian.local'
        $target.VmName | Should -Be 'dc-target'
    }

    It 'takes the address, host and VM name from the environment' {
        $env:JIM_AD_LAB_SOURCE_ADDRESS = '198.51.100.7'
        $env:JIM_AD_LAB_SOURCE_HOST = 'dcx.resurgam.example'
        $env:JIM_AD_LAB_SOURCE_VM = 'lab-dc-2'

        $config = Get-DirectoryConfig -DirectoryType ActiveDirectory -Instance Source

        $config.Address | Should -Be '198.51.100.7'
        $config.Host | Should -Be 'dcx.resurgam.example'
        $config.VmName | Should -Be 'lab-dc-2'
    }

    It 'takes both passwords from the environment, the same for every forest' {
        $primary = Get-DirectoryConfig -DirectoryType ActiveDirectory -Instance Primary
        $target = Get-DirectoryConfig -DirectoryType ActiveDirectory -Instance Target

        foreach ($config in $primary, $target) {
            $config.BindPassword | Should -Be 'admin-pw-for-test'
            $config.JimBindPassword | Should -Be 'jim-pw-for-test'
            $config.MultiPartitionJimBindPassword | Should -Be 'jim-pw-for-test'
        }
    }

    It 'never carries the Samba lab passwords' {
        $config = Get-DirectoryConfig -DirectoryType ActiveDirectory -Instance Primary

        $config.BindPassword | Should -Not -Be 'Test@123!'
        $config.JimBindPassword | Should -Not -Be 'Svc-Jim@123!'
    }

    It 'throws naming the variable when the instance''s address is unset' {
        [System.Environment]::SetEnvironmentVariable('JIM_AD_LAB_TARGET_ADDRESS', $null)

        { Get-DirectoryConfig -DirectoryType ActiveDirectory -Instance Target } | Should -Throw '*JIM_AD_LAB_TARGET_ADDRESS*'
    }

    It 'does not need the address of an instance it is not asked for' {
        [System.Environment]::SetEnvironmentVariable('JIM_AD_LAB_TARGET_ADDRESS', $null)
        [System.Environment]::SetEnvironmentVariable('JIM_AD_LAB_SOURCE_ADDRESS', $null)

        { Get-DirectoryConfig -DirectoryType ActiveDirectory -Instance Primary } | Should -Not -Throw
    }

    It 'throws naming the variable when the Administrator password is unset' {
        [System.Environment]::SetEnvironmentVariable('JIM_AD_LAB_ADMIN_PASSWORD', $null)

        { Get-DirectoryConfig -DirectoryType ActiveDirectory -Instance Primary } | Should -Throw '*JIM_AD_LAB_ADMIN_PASSWORD*'
    }

    It 'throws naming the variable when the svc-jim password is unset' {
        [System.Environment]::SetEnvironmentVariable('JIM_AD_LAB_JIM_PASSWORD', $null)

        { Get-DirectoryConfig -DirectoryType ActiveDirectory -Instance Primary } | Should -Throw '*JIM_AD_LAB_JIM_PASSWORD*'
    }

    It 'throws for an unknown instance before it reads any variable' {
        Clear-AdLabEnvironment

        { Get-DirectoryConfig -DirectoryType ActiveDirectory -Instance 'Nowhere' } | Should -Throw '*Unknown ActiveDirectory instance: Nowhere*'
    }

    It 'returns a fresh hashtable each call, so a caller mutating one cannot affect the next' {
        $first = Get-DirectoryConfig -DirectoryType ActiveDirectory -Instance Primary
        $first.Host = 'mutated'

        (Get-DirectoryConfig -DirectoryType ActiveDirectory -Instance Primary).Host | Should -Be 'dc1.panoply.local'
    }
}

Describe 'Get-FirstPemCertificate' {
    It 'returns the first certificate block of s_client output, normalised to LF with a trailing newline' {
        $text = "CONNECTED(00000003)`r`n---`r`nCertificate chain`r`n 0 s:CN = dc1.panoply.local`r`n-----BEGIN CERTIFICATE-----`r`nAAAA`r`nBBBB`r`n-----END CERTIFICATE-----`r`n 1 s:CN = Issuing CA`r`n-----BEGIN CERTIFICATE-----`r`nCCCC`r`n-----END CERTIFICATE-----`r`n"

        Get-FirstPemCertificate -Text $text | Should -Be "-----BEGIN CERTIFICATE-----`nAAAA`nBBBB`n-----END CERTIFICATE-----`n"
    }

    It 'accepts the array of lines docker exec produces' {
        Get-FirstPemCertificate -Text @('CONNECTED(00000003)', '-----BEGIN CERTIFICATE-----', 'AAAA', '-----END CERTIFICATE-----') |
            Should -Be "-----BEGIN CERTIFICATE-----`nAAAA`n-----END CERTIFICATE-----`n"
    }

    It 'throws when the output holds no certificate, with the output in the message' {
        { Get-FirstPemCertificate -Text 'connect:errno=111' } | Should -Throw '*connect:errno=111*'
    }

    It 'ignores a private key block' {
        { Get-FirstPemCertificate -Text "-----BEGIN PRIVATE KEY-----`nAAAA`n-----END PRIVATE KEY-----" } | Should -Throw
    }
}

Describe 'Test-CertificateSanHasName' {
    BeforeAll {
        $script:san = "X509v3 Subject Alternative Name: `n    DNS:dc1.panoply.local, DNS:dc1, DNS:panoply.local`n"
    }

    It 'finds a name in the list' {
        Test-CertificateSanHasName -SubjectAltNameText $script:san -DnsName 'dc1.panoply.local' | Should -BeTrue
        Test-CertificateSanHasName -SubjectAltNameText $script:san -DnsName 'panoply.local' | Should -BeTrue
        Test-CertificateSanHasName -SubjectAltNameText $script:san -DnsName 'dc1' | Should -BeTrue
    }

    It 'matches whatever the case' {
        Test-CertificateSanHasName -SubjectAltNameText $script:san -DnsName 'DC1.PANOPLY.LOCAL' | Should -BeTrue
    }

    It 'does not match a name that is only a substring of an entry' {
        Test-CertificateSanHasName -SubjectAltNameText $script:san -DnsName 'local' | Should -BeFalse
        Test-CertificateSanHasName -SubjectAltNameText $script:san -DnsName 'dc1.panoply' | Should -BeFalse
    }

    It 'does not match a name that merely ends an entry' {
        Test-CertificateSanHasName -SubjectAltNameText 'DNS:xdc1.panoply.local' -DnsName 'dc1.panoply.local' | Should -BeFalse
    }

    It 'accepts the array of lines docker exec produces' {
        Test-CertificateSanHasName -SubjectAltNameText @('X509v3 Subject Alternative Name: ', '    DNS:dc1.panoply.local, DNS:dc1') -DnsName 'dc1.panoply.local' | Should -BeTrue
    }

    It 'does not treat a wildcard character in the name as a pattern' {
        Test-CertificateSanHasName -SubjectAltNameText $script:san -DnsName 'dc1.panoply.*' | Should -BeFalse
    }
}

Describe 'Build-ActiveDirectoryLabCaBundle' {
    It 'concatenates every certificate file except the bundle itself, in name order' {
        Set-Content -Path (Join-Path $TestDrive 'dc-target.pem') -Value "-----BEGIN CERTIFICATE-----`nTTTT`n-----END CERTIFICATE-----" -NoNewline
        Set-Content -Path (Join-Path $TestDrive 'dc-primary.pem') -Value "-----BEGIN CERTIFICATE-----`nPPPP`n-----END CERTIFICATE-----`n" -NoNewline
        Set-Content -Path (Join-Path $TestDrive 'ad-lab-ca.pem') -Value 'STALE' -NoNewline
        Set-Content -Path (Join-Path $TestDrive 'notes.txt') -Value 'not a certificate' -NoNewline

        $path = Build-ActiveDirectoryLabCaBundle -CertificateDirectory $TestDrive

        $path | Should -Be (Join-Path $TestDrive 'ad-lab-ca.pem')
        [System.IO.File]::ReadAllText($path) | Should -Be "-----BEGIN CERTIFICATE-----`nPPPP`n-----END CERTIFICATE-----`n-----BEGIN CERTIFICATE-----`nTTTT`n-----END CERTIFICATE-----`n"
    }

    It 'drops a certificate whose file has been removed on the next refresh' {
        $dir = Join-Path $TestDrive 'refresh'
        New-Item -ItemType Directory -Path $dir | Out-Null
        Set-Content -Path (Join-Path $dir 'dc-a.pem') -Value "AAAA`n" -NoNewline
        Set-Content -Path (Join-Path $dir 'dc-b.pem') -Value "BBBB`n" -NoNewline
        Build-ActiveDirectoryLabCaBundle -CertificateDirectory $dir | Out-Null
        Remove-Item (Join-Path $dir 'dc-a.pem')

        Build-ActiveDirectoryLabCaBundle -CertificateDirectory $dir | Out-Null

        [System.IO.File]::ReadAllText((Join-Path $dir 'ad-lab-ca.pem')) | Should -Be "BBBB`n"
    }

    It 'writes an empty bundle when there are no certificates' {
        $dir = Join-Path $TestDrive 'empty'
        New-Item -ItemType Directory -Path $dir | Out-Null

        $path = Build-ActiveDirectoryLabCaBundle -CertificateDirectory $dir

        [System.IO.File]::ReadAllText($path) | Should -Be ''
    }

    It 'writes without a byte order mark' {
        $dir = Join-Path $TestDrive 'bom'
        New-Item -ItemType Directory -Path $dir | Out-Null
        Set-Content -Path (Join-Path $dir 'dc-a.pem') -Value "AAAA`n" -NoNewline

        $bytes = [System.IO.File]::ReadAllBytes((Build-ActiveDirectoryLabCaBundle -CertificateDirectory $dir))

        $bytes[0] | Should -Be ([byte][char]'A')
    }
}

Describe 'Save-ActiveDirectoryCertificate' {
    BeforeAll {
        function docker { }
        $script:pem = "-----BEGIN CERTIFICATE-----`nAAAA`nBBBB`n-----END CERTIFICATE-----`n"
    }

    BeforeEach {
        $script:dockerCalls = [System.Collections.Generic.List[object]]::new()
        $script:sanText = "X509v3 Subject Alternative Name: `n    DNS:dc1.panoply.local, DNS:dc1, DNS:panoply.local"
        $script:sClientText = "CONNECTED(00000003)`n---`nCertificate chain`n$($script:pem)---`nServer certificate`n"
        $script:config = @{
            DirectoryType = 'ActiveDirectory'; ContainerName = $null; Host = 'dc1.panoply.local'
            Address = '192.0.2.10'; VmName = 'dc-primary'; Port = 636
        }
        $script:certDir = Join-Path $TestDrive ([guid]::NewGuid().ToString('N'))
        Mock docker {
            $script:dockerCalls.Add(@($args))
            $global:LASTEXITCODE = 0
            $joined = @($args) -join ' '
            if ($joined -like 'ps *') { return 'jim-ldap-toolbox' }
            if ($joined -like '*s_client*') { return ($script:sClientText -split "`n") }
            if ($joined -like '*x509*') { return ($script:sanText -split "`n") }
        }
    }

    It 'fetches the certificate through the toolbox with s_client against the address, by the host name' {
        Save-ActiveDirectoryCertificate -DirectoryConfig $script:config -CertificateDirectory $script:certDir | Out-Null

        $fetch = @($script:dockerCalls | Where-Object { (@($_) -join ' ') -like '*s_client*' })
        $fetch.Count | Should -Be 1
        $text = @($fetch[0]) -join ' '
        $text | Should -Match 'exec jim-ldap-toolbox'
        $text | Should -Match 'openssl s_client -connect 192\.0\.2\.10:636 -servername dc1\.panoply\.local -showcerts </dev/null'
    }

    It 'writes the first certificate to the VM''s .pem file and returns its path' {
        $result = Save-ActiveDirectoryCertificate -DirectoryConfig $script:config -CertificateDirectory $script:certDir

        $result.Path | Should -Be (Join-Path $script:certDir 'dc-primary.pem')
        [System.IO.File]::ReadAllText($result.Path) | Should -Be $script:pem
    }

    It 'refreshes ad-lab-ca.pem with every fetched certificate' {
        New-Item -ItemType Directory -Path $script:certDir | Out-Null
        Set-Content -Path (Join-Path $script:certDir 'dc-source.pem') -Value "-----BEGIN CERTIFICATE-----`nSSSS`n-----END CERTIFICATE-----`n" -NoNewline

        $result = Save-ActiveDirectoryCertificate -DirectoryConfig $script:config -CertificateDirectory $script:certDir

        $result.BundlePath | Should -Be (Join-Path $script:certDir 'ad-lab-ca.pem')
        $bundle = [System.IO.File]::ReadAllText($result.BundlePath)
        $bundle | Should -Match 'AAAA'
        $bundle | Should -Match 'SSSS'
    }

    It 'checks the SAN list in the toolbox, feeding it the certificate on standard input' {
        Save-ActiveDirectoryCertificate -DirectoryConfig $script:config -CertificateDirectory $script:certDir | Out-Null

        $san = @($script:dockerCalls | Where-Object { (@($_) -join ' ') -like '*x509*' })
        $san.Count | Should -Be 1
        (@($san[0]) -join ' ') | Should -Be 'exec -i jim-ldap-toolbox openssl x509 -noout -ext subjectAltName'
    }

    It 'throws, and leaves no certificate behind, when the SAN list lacks the host' {
        $script:sanText = "X509v3 Subject Alternative Name: `n    DNS:some-other-host.local"

        { Save-ActiveDirectoryCertificate -DirectoryConfig $script:config -CertificateDirectory $script:certDir } |
            Should -Throw '*dc1.panoply.local*Subject Alternative Name*'
        Test-Path (Join-Path $script:certDir 'dc-primary.pem') | Should -BeFalse
    }

    It 'throws when the toolbox is not running' {
        Mock docker { $global:LASTEXITCODE = 0; if ((@($args) -join ' ') -like 'ps *') { return $null } }

        { Save-ActiveDirectoryCertificate -DirectoryConfig $script:config -CertificateDirectory $script:certDir } |
            Should -Throw '*jim-ldap-toolbox*not running*'
    }

    It 'throws with the s_client output when no certificate comes back' {
        $script:sClientText = 'connect:errno=111'

        { Save-ActiveDirectoryCertificate -DirectoryConfig $script:config -CertificateDirectory $script:certDir } |
            Should -Throw '*connect:errno=111*'
    }

    It 'refuses a <Key> that is not a plain name, since it reaches a shell in the toolbox' -ForEach @(
        @{ Key = 'Address'; Value = '1.2.3.4; id' }
        @{ Key = 'Host'; Value = 'dc1$(id).local' }
        @{ Key = 'VmName'; Value = '..\evil' }
    ) {
        $bad = $script:config.Clone()
        $bad[$Key] = $Value

        { Save-ActiveDirectoryCertificate -DirectoryConfig $bad -CertificateDirectory $script:certDir } | Should -Throw "*$Key*"
        Should -Invoke docker -Times 0 -Exactly
    }

    It 'refuses a config that is not an Active Directory one' {
        { Save-ActiveDirectoryCertificate -DirectoryConfig @{ DirectoryType = 'SambaAD'; ContainerName = 'samba-ad-primary' } -CertificateDirectory $script:certDir } |
            Should -Throw '*ActiveDirectory*'
    }
}

Describe 'Add-ActiveDirectoryCertificateToJimStore' {
    BeforeEach {
        Mock Save-ActiveDirectoryCertificate { @{ Path = '/certs/dc-primary.pem'; BundlePath = '/certs/ad-lab-ca.pem' } }
        Mock Add-CertificateBytesToJimStore { }
        $script:config = @{
            DirectoryType = 'ActiveDirectory'; ContainerName = $null; Host = 'dc1.panoply.local'
            Address = '192.0.2.10'; VmName = 'dc-primary'
        }
    }

    It 'saves the certificate, then uploads that file''s bytes under the VM''s name' {
        Add-ActiveDirectoryCertificateToJimStore -DirectoryConfig $script:config -JIMUrl 'http://localhost:5200' -ApiKey 'k'

        Should -Invoke Save-ActiveDirectoryCertificate -Times 1 -Exactly
        Should -Invoke Add-CertificateBytesToJimStore -Times 1 -Exactly -ParameterFilter {
            $JIMUrl -eq 'http://localhost:5200' -and $ApiKey -eq 'k' -and $CertificateName -eq 'dc-primary CA' -and
            $CertificatePath -eq '/certs/dc-primary.pem' -and $CallerName -eq 'Add-ActiveDirectoryCertificateToJimStore' -and
            $Notes -match 'dc-primary' -and $Notes -match 'dc1\.panoply\.local'
        }
    }

    It 'does not upload anything when saving the certificate fails' {
        Mock Save-ActiveDirectoryCertificate { throw 'SAN check failed' }

        { Add-ActiveDirectoryCertificateToJimStore -DirectoryConfig $script:config -JIMUrl 'http://localhost:5200' -ApiKey 'k' } | Should -Throw '*SAN check failed*'
        Should -Invoke Add-CertificateBytesToJimStore -Times 0 -Exactly
    }
}

Describe 'Add-CertificateBytesToJimStore' {
    <#
        The upload tail shared by the Samba AD, 389 Directory Server and Active Directory functions:
        connect to JIM, remove a stale certificate of the same name, upload the fresh bytes, disconnect.
        The JIM module's cmdlets are stubbed, so this guards the behaviour all three depend on without a
        JIM instance.
    #>
    BeforeAll {
        function Connect-JIM { [CmdletBinding()] param($Url, $ApiKey) $null = $Url, $ApiKey }
        function Disconnect-JIM { [CmdletBinding()] param() }
        function Get-JIMCertificate { [CmdletBinding()] param() }
        function Remove-JIMCertificate { [CmdletBinding(SupportsShouldProcess)] param($Id, [switch]$Force) if ($PSCmdlet.ShouldProcess($Id)) { $null = $Force } }
        function Add-JIMCertificate { [CmdletBinding()] param($Name, $CertificateData, $Notes, [switch]$PassThru) $null = $Name, $CertificateData, $Notes, $PassThru }
    }

    BeforeEach {
        Mock Import-Module { }
        Mock Remove-Module { }
        Mock Connect-JIM { }
        Mock Disconnect-JIM { }
        Mock Get-JIMCertificate { @([pscustomobject]@{ id = 'old-1'; name = 'dc-primary CA' }, [pscustomobject]@{ id = 'other'; name = 'something else' }) }
        Mock Remove-JIMCertificate { }
        Mock Add-JIMCertificate { [pscustomobject]@{ id = 'new-1'; thumbprint = 'ABCD' } }
        $script:certFile = Join-Path $TestDrive 'dc-primary.pem'
        [System.IO.File]::WriteAllBytes($script:certFile, [byte[]](1, 2, 3, 4))
    }

    It 'removes only a stale certificate of the same name, then uploads the file''s exact bytes' {
        Add-CertificateBytesToJimStore -JIMUrl 'http://localhost:5200' -ApiKey 'k' -CertificateName 'dc-primary CA' `
            -CertificatePath $script:certFile -Notes 'notes' -CallerName 'Caller' -TrustedDescription "dc-primary's certificate"

        Should -Invoke Connect-JIM -Times 1 -Exactly -ParameterFilter { $Url -eq 'http://localhost:5200' -and $ApiKey -eq 'k' }
        Should -Invoke Remove-JIMCertificate -Times 1 -Exactly -ParameterFilter { $Id -eq 'old-1' -and $Force }
        Should -Invoke Add-JIMCertificate -Times 1 -Exactly -ParameterFilter {
            $Name -eq 'dc-primary CA' -and $Notes -eq 'notes' -and (($CertificateData -join ',') -eq '1,2,3,4') -and $PassThru
        }
    }

    It 'disconnects even when the upload fails' {
        Mock Add-JIMCertificate { throw 'boom' }

        { Add-CertificateBytesToJimStore -JIMUrl 'u' -ApiKey 'k' -CertificateName 'n' -CertificatePath $script:certFile -Notes 'x' -CallerName 'Caller' -TrustedDescription 'd' } |
            Should -Throw '*boom*'
        Should -Invoke Disconnect-JIM -Times 1 -Exactly
    }

    It 'throws, naming the caller and the certificate, when the upload returns nothing' {
        Mock Add-JIMCertificate { $null }

        { Add-CertificateBytesToJimStore -JIMUrl 'u' -ApiKey 'k' -CertificateName 'dc-primary CA' -CertificatePath $script:certFile -Notes 'x' -CallerName 'Add-XToJimStore' -TrustedDescription 'd' } |
            Should -Throw "*Add-XToJimStore*dc-primary CA*"
    }
}

Describe 'Grant-JimAdDelegation' {
    BeforeAll {
        . "$PSScriptRoot/Invoke-LabControl.ps1"

        # A stand-in for the docker executable, recording what it was asked to run.
        function docker {
            $script:grantDockerCalls.Add(@($args))
            $global:LASTEXITCODE = $script:grantDockerExitCode
            $script:grantDockerOutput
        }
    }

    BeforeEach {
        $script:grantDockerCalls = [System.Collections.Generic.List[object]]::new()
        $script:grantDockerExitCode = 0
        $script:grantDockerOutput = @('Delegation applied')
    }

    Context 'a container name (Samba AD)' {
        It 'runs the image''s own jim-delegate.sh in the container, as it always did' {
            Grant-JimAdDelegation -ContainerName 'samba-ad-source' -ContainerDn 'OU=TestUsers,DC=resurgam,DC=local'

            $script:grantDockerCalls.Count | Should -Be 1
            $script:grantDockerCalls[0] | Should -Be @('exec', 'samba-ad-source', '/usr/local/sbin/jim-delegate.sh', 'OU=TestUsers,DC=resurgam,DC=local')
        }

        It 'throws with the script output when jim-delegate.sh fails' {
            $script:grantDockerExitCode = 1
            $script:grantDockerOutput = @('access denied')

            { Grant-JimAdDelegation -ContainerName 'samba-ad-source' -ContainerDn 'OU=X,DC=resurgam,DC=local' } |
                Should -Throw '*access denied*'
        }
    }

    Context 'a directory config with a container' {
        It 'takes the Samba AD path with the config''s container' {
            $config = Get-DirectoryConfig -DirectoryType SambaAD -Instance Target

            Grant-JimAdDelegation -DirectoryConfig $config -ContainerDn 'OU=TestUsers,DC=gentian,DC=local'

            $script:grantDockerCalls[0] | Should -Be @('exec', 'samba-ad-target', '/usr/local/sbin/jim-delegate.sh', 'OU=TestUsers,DC=gentian,DC=local')
        }
    }

    Context 'a directory config with no container (Active Directory)' {
        BeforeAll {
            $script:adConfig = @{ DirectoryType = 'ActiveDirectory'; ContainerName = $null; VmName = 'dc-source'; Host = 'dc1.resurgam.local' }
        }

        It 'asks the lab host to apply it to the VM, and never touches docker' {
            Mock Invoke-LabControl { 'Delegation applied over OU=TestUsers,DC=resurgam,DC=local' }

            Grant-JimAdDelegation -DirectoryConfig $script:adConfig -ContainerDn 'OU=TestUsers,DC=resurgam,DC=local'

            Should -Invoke Invoke-LabControl -Times 1 -Exactly -ParameterFilter {
                $Script -eq 'Grant-LabDelegation.ps1' -and
                ($Arguments -join '|') -eq '-Name|dc-source|-ContainerDn|OU=TestUsers,DC=resurgam,DC=local'
            }
            $script:grantDockerCalls.Count | Should -Be 0
        }

        It 'throws a message naming the container and the consequence when the lab host refuses' {
            Mock Invoke-LabControl { throw 'exit 1: no such VM' }

            { Grant-JimAdDelegation -DirectoryConfig $script:adConfig -ContainerDn 'OU=Scenario,DC=resurgam,DC=local' } |
                Should -Throw "*could not delegate JIM's access over 'OU=Scenario,DC=resurgam,DC=local'*no such VM*"
        }

        It 'throws when the config has no VmName to address' {
            { Grant-JimAdDelegation -DirectoryConfig @{ DirectoryType = 'ActiveDirectory'; ContainerName = $null } -ContainerDn 'OU=X,DC=y' } |
                Should -Throw '*VmName*'
        }
    }
}

Describe 'Test-TemplateSpansSyncPages' {
    # A run whose users all fit in one synchronisation page never crosses a page boundary, so the code the
    # worker runs between pages (tracker clear, cross-page reference fix-up, re-attaching bulk-created rows)
    # goes unexercised; Scenario 023 passed at Micro and failed at Pre-Release's Medium for exactly that reason.
    It 'returns <Expected> for <Template> at the default page size of 500' -ForEach @(
        @{ Template = 'Nano';   Expected = $false }
        @{ Template = 'Micro';  Expected = $false }
        @{ Template = 'Small';  Expected = $false }
        @{ Template = 'Medium'; Expected = $true }
        @{ Template = 'Large';  Expected = $true }
    ) {
        Test-TemplateSpansSyncPages -Template $Template | Should -Be $Expected
    }

    It 'honours a non-default page size' {
        Test-TemplateSpansSyncPages -Template 'Small' -SyncPageSize 50 | Should -BeTrue
        Test-TemplateSpansSyncPages -Template 'Medium' -SyncPageSize 1000 | Should -BeFalse
    }
}
