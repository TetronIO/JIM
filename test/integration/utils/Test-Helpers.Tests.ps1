# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

#Requires -Modules Pester

<#
.SYNOPSIS
    Pester tests for Get-DirectoryConfig and Test-IsRfcDirectory in Test-Helpers.ps1.

.DESCRIPTION
    Guards the directory-config contract the scenario scripts and the runner build on: every
    directory type and instance returns a hashtable stamped with its DirectoryType; the 389
    Directory Server configs mirror the OpenLDAP ones key for key (the two share populate scripts
    and scenarios); and Test-IsRfcDirectory answers the RFC-versus-AD question for every type and
    refuses an unknown one.
#>

BeforeAll {
    . "$PSScriptRoot/Test-Helpers.ps1"

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

Describe 'in-container LDAP URIs' {
    <#
        Static guard over the harness scripts themselves. An OpenLDAP client tool run through docker
        exec inside a directory container must be pointed at LdapSearchScheme://localhost:LdapSearchPort.
        Building that URI from Port (JIM's Connected System port) or leaving the port off entirely
        (the tool then defaults to 389) only works where the two ports coincide, and on 389 Directory
        Server they do not. Scenario 1's Set-DirectoryUserAttributes hung on exactly this.
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
