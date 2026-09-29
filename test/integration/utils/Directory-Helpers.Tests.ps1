# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

#Requires -Modules Pester

<#
.SYNOPSIS
    Pester tests for the directory-abstracted helpers in Directory-Helpers.ps1.

.DESCRIPTION
    The scenario and setup scripts used to call "docker exec <container> samba-tool ...", ldbadd and
    the rest directly, which cannot work against a real Active Directory domain controller (a Hyper-V
    virtual machine with no container, reached over LDAPS through the jim-ldap-toolbox container).
    Directory-Helpers.ps1 puts each directory operation behind one function that dispatches on the
    config's DirectoryType.

    These tests pin the exact docker argument list, and the exact LDIF handed to the tool on standard
    input, for every directory type. Samba AD, OpenLDAP and 389 Directory Server behaviour must stay
    byte-identical to what the scripts ran before; the Active Directory branches must go through
    Invoke-LdapTool only. docker is a stand-in function that records every call, because a Pester
    mock cannot see what is piped to it and the tool's standard input is part of the contract.
#>

BeforeAll {
    . "$PSScriptRoot/Directory-Helpers.ps1"

    # A name with a non-ASCII letter (U+00DC), built from its code point so this file stays ASCII.
    $script:unalName = 'Zoe ' + [char]0x00DC + 'nal'

    function docker {
        begin { $script:stubStdin = [System.Collections.Generic.List[string]]::new() }
        process { if ($null -ne $_) { $script:stubStdin.Add([string]$_) } }
        end {
            $script:dockerCalls.Add(@{ Args = @($args); Stdin = ($script:stubStdin -join "`n") })
            if ($script:dockerResponses.Count -gt 0) {
                $response = $script:dockerResponses[0]
                $script:dockerResponses.RemoveAt(0)
            }
            else {
                $response = @{ ExitCode = 0; Output = @() }
            }
            $global:LASTEXITCODE = $response.ExitCode
            $response.Output
        }
    }

    function Add-DockerResponse {
        param([int]$ExitCode = 0, $Output = @())
        $script:dockerResponses.Add(@{ ExitCode = $ExitCode; Output = $Output })
    }

    function Get-SambaTestConfig {
        return @{
            DirectoryType = 'SambaAD'; ContainerName = 'samba-ad-primary'; Host = 'samba-ad-primary'
            BindDN = 'CN=Administrator,CN=Users,DC=panoply,DC=local'; BindPassword = 'Test@123!'
            BaseDN = 'DC=panoply,DC=local'; Domain = 'panoply.local'
            UserContainer = 'OU=Users,OU=Corp,DC=panoply,DC=local'; GroupContainer = 'OU=Groups,OU=Corp,DC=panoply,DC=local'
            UserRdnAttr = 'CN'; UserNameAttr = 'sAMAccountName'
            LdapSearchScheme = 'ldap'; LdapSearchPort = 389
        }
    }

    function Get-AdTestConfig {
        return @{
            DirectoryType = 'ActiveDirectory'; ContainerName = $null; Host = 'dc1.panoply.local'; Address = '192.0.2.10'
            BindDN = 'CN=Administrator,CN=Users,DC=panoply,DC=local'; BindPassword = 'Admin@Pw-123!'
            BaseDN = 'DC=panoply,DC=local'; Domain = 'panoply.local'
            UserContainer = 'OU=Users,OU=Corp,DC=panoply,DC=local'; GroupContainer = 'OU=Groups,OU=Corp,DC=panoply,DC=local'
            UserRdnAttr = 'CN'; UserNameAttr = 'sAMAccountName'
            LdapSearchScheme = 'ldaps'; LdapSearchPort = 636
        }
    }

    function Get-OpenLdapTestConfig {
        return @{
            DirectoryType = 'OpenLDAP'; ContainerName = 'openldap-primary'; Host = 'openldap-primary'
            BindDN = 'cn=admin,dc=yellowstone,dc=local'; BindPassword = 'Test@123!'
            BaseDN = 'dc=yellowstone,dc=local'; Domain = 'yellowstone.local'
            UserContainer = 'ou=People,dc=yellowstone,dc=local'; GroupContainer = 'ou=Groups,dc=yellowstone,dc=local'
            UserRdnAttr = 'uid'; UserNameAttr = 'uid'
            LdapSearchScheme = 'ldap'; LdapSearchPort = 1389
        }
    }
}

Describe 'Directory-Helpers' {
BeforeEach {
    $script:dockerCalls = [System.Collections.Generic.List[hashtable]]::new()
    $script:dockerResponses = [System.Collections.Generic.List[hashtable]]::new()
}

Describe 'Get-DirectoryToolUri and Get-DirectoryToolBindArgument' {
    It 'uses the in-container URI for a config with a container' {
        Get-DirectoryToolUri -DirectoryConfig (Get-SambaTestConfig) | Should -Be 'ldap://localhost:389'
        Get-DirectoryToolUri -DirectoryConfig (Get-OpenLdapTestConfig) | Should -Be 'ldap://localhost:1389'
    }

    It 'uses the LDAPS URI of the domain controller for a container-less config' {
        Get-DirectoryToolUri -DirectoryConfig (Get-AdTestConfig) | Should -Be 'ldaps://dc1.panoply.local:636'
    }

    It 'builds the administrator simple-bind arguments in the order the scripts always used' {
        $arguments = Get-DirectoryToolBindArgument -DirectoryConfig (Get-SambaTestConfig)

        $arguments | Should -Be @('-x', '-H', 'ldap://localhost:389', '-D', 'CN=Administrator,CN=Users,DC=panoply,DC=local', '-w', 'Test@123!')
    }
}

Describe 'Split-DirectoryDn' {
    It 'splits the leading RDN from the parent' {
        $dn = Split-DirectoryDn -Dn 'OU=Finance,OU=Users,OU=Corp,DC=panoply,DC=local'

        $dn.RdnAttribute | Should -Be 'OU'
        $dn.RdnValue | Should -Be 'Finance'
        $dn.ParentDn | Should -Be 'OU=Users,OU=Corp,DC=panoply,DC=local'
    }

    It 'does not split on an escaped comma' {
        $dn = Split-DirectoryDn -Dn 'CN=Smith\, Alice,OU=Users,DC=x'

        $dn.RdnValue | Should -Be 'Smith\, Alice'
        $dn.ParentDn | Should -Be 'OU=Users,DC=x'
    }

    It 'gives an empty parent for a single-RDN DN' {
        (Split-DirectoryDn -Dn 'DC=local').ParentDn | Should -Be ''
    }
}

Describe 'Get-DirectoryRelativeDn' {
    It 'strips the base DN suffix, ignoring case' {
        Get-DirectoryRelativeDn -Dn 'OU=TestUsers,dc=PANOPLY,DC=local' -BaseDn 'DC=panoply,DC=local' | Should -Be 'OU=TestUsers'
    }

    It 'returns an empty string for the base DN itself' {
        Get-DirectoryRelativeDn -Dn 'DC=panoply,DC=local' -BaseDn 'DC=panoply,DC=local' | Should -Be ''
    }

    It 'throws when the DN is not under the base DN' {
        { Get-DirectoryRelativeDn -Dn 'OU=X,DC=other,DC=local' -BaseDn 'DC=panoply,DC=local' } | Should -Throw
    }
}

Describe 'Format-LdifAttribute' {
    It 'writes a plain value as name: value' {
        Format-LdifAttribute -Name 'department' -Value 'Finance' | Should -Be 'department: Finance'
    }

    It 'base64 encodes a value with a leading space, a leading colon or non-ASCII text' {
        Format-LdifAttribute -Name 'description' -Value ' padded' | Should -Be "description:: $([Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes(' padded')))"
        Format-LdifAttribute -Name 'description' -Value ':colon' | Should -Match '^description:: '
        Format-LdifAttribute -Name 'displayName' -Value $script:unalName | Should -Match '^displayName:: '
    }
}

Describe 'ConvertFrom-LdifSearchOutput' {
    It 'reads entries, unfolds continuation lines and decodes base64 values' {
        $b64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($script:unalName))
        $output = @(
            'dn: CN=Alice Wonderland,OU=Information Technology,OU=Users,OU=Corp,DC=panoply,DC=l',
            ' ocal',
            'department: Finance',
            'memberOf: CN=A,DC=x',
            'memberOf: CN=B,DC=x',
            "displayName:: $b64",
            '',
            '# refldap://elsewhere',
            'dn: CN=Bob,DC=x',
            'sn: Bob'
        )

        $entries = @(ConvertFrom-LdifSearchOutput -Output $output)

        $entries.Count | Should -Be 2
        $entries[0].dn | Should -Be 'CN=Alice Wonderland,OU=Information Technology,OU=Users,OU=Corp,DC=panoply,DC=local'
        $entries[0].department | Should -Be @('Finance')
        $entries[0].memberOf | Should -Be @('CN=A,DC=x', 'CN=B,DC=x')
        $entries[0].displayName | Should -Be @($script:unalName)
        $entries[1].dn | Should -Be 'CN=Bob,DC=x'
    }

    It 'looks attributes up case-insensitively' {
        $entry = @(ConvertFrom-LdifSearchOutput -Output @('dn: CN=x,DC=y', 'sAMAccountName: x'))[0]

        $entry.samaccountname | Should -Be @('x')
    }

    It 'returns nothing for empty output' {
        @(ConvertFrom-LdifSearchOutput -Output @()).Count | Should -Be 0
        @(ConvertFrom-LdifSearchOutput -Output $null).Count | Should -Be 0
    }
}

Describe 'Get-DirectoryContainerCommand' {
    It 'builds docker exec for the config''s container' {
        Get-DirectoryContainerCommand -DirectoryConfig (Get-SambaTestConfig) -Command @('samba-tool', 'ou', 'create', 'OU=x') |
            Should -Be @('exec', 'samba-ad-primary', 'samba-tool', 'ou', 'create', 'OU=x')
    }

    It 'adds -i when the command reads standard input' {
        Get-DirectoryContainerCommand -DirectoryConfig (Get-SambaTestConfig) -Command @('ldbadd') -WithInput |
            Should -Be @('exec', '-i', 'samba-ad-primary', 'ldbadd')
    }

    It 'refuses a config with no container, naming the way out' {
        { Get-DirectoryContainerCommand -DirectoryConfig (Get-AdTestConfig) -Command @('samba-tool') } |
            Should -Throw '*no container*'
    }
}

Describe 'Invoke-DirectoryLdif' {
    It 'runs ldapadd in the container over stdin with the arguments the scripts always used' -ForEach @(
        @{ Name = 'Samba AD'; Config = { Get-SambaTestConfig }; Container = 'samba-ad-primary'; Uri = 'ldap://localhost:389'; Bind = 'CN=Administrator,CN=Users,DC=panoply,DC=local'; Password = 'Test@123!' }
        @{ Name = 'OpenLDAP'; Config = { Get-OpenLdapTestConfig }; Container = 'openldap-primary'; Uri = 'ldap://localhost:1389'; Bind = 'cn=admin,dc=yellowstone,dc=local'; Password = 'Test@123!' }
    ) {
        $result = Invoke-DirectoryLdif -DirectoryConfig (& $Config) -Ldif "dn: x`nobjectClass: top" -Operation add

        $script:dockerCalls.Count | Should -Be 1
        $script:dockerCalls[0].Args | Should -Be @('exec', '-i', $Container, 'ldapadd', '-x', '-H', $Uri, '-D', $Bind, '-w', $Password)
        $script:dockerCalls[0].Stdin | Should -Be "dn: x`nobjectClass: top"
        $result.Success | Should -BeTrue
    }

    It 'adds -c after the bind arguments for -Continue' {
        Invoke-DirectoryLdif -DirectoryConfig (Get-OpenLdapTestConfig) -Ldif 'dn: x' -Operation modify -Continue | Out-Null

        $script:dockerCalls[0].Args | Should -Be @('exec', '-i', 'openldap-primary', 'ldapmodify', '-x', '-H', 'ldap://localhost:1389', '-D', 'cn=admin,dc=yellowstone,dc=local', '-w', 'Test@123!', '-c')
    }

    It 'goes to the toolbox over LDAPS for Active Directory' {
        Invoke-DirectoryLdif -DirectoryConfig (Get-AdTestConfig) -Ldif 'dn: x' -Operation modify | Out-Null

        $script:dockerCalls[0].Args | Should -Be @('exec', '-i', 'jim-ldap-toolbox', 'ldapmodify', '-x', '-H', 'ldaps://dc1.panoply.local:636', '-D', 'CN=Administrator,CN=Users,DC=panoply,DC=local', '-w', 'Admin@Pw-123!')
    }

    It 'normalises CRLF line endings to LF, which the ldb LDIF parser needs and OpenLDAP tolerates' {
        Invoke-DirectoryLdif -DirectoryConfig (Get-SambaTestConfig) -Ldif "dn: x`r`nobjectClass: top`r`n" -Operation add | Out-Null

        $script:dockerCalls[0].Stdin | Should -Not -Match "`r"
    }

    It 'runs ldapdelete for the delete operation, reading DNs from standard input' {
        Invoke-DirectoryLdif -DirectoryConfig (Get-AdTestConfig) -Ldif "CN=a,DC=x`nCN=b,DC=x" -Operation delete -Continue | Out-Null

        $script:dockerCalls[0].Args[3] | Should -Be 'ldapdelete'
        $script:dockerCalls[0].Args[-1] | Should -Be '-c'
        $script:dockerCalls[0].Stdin | Should -Be "CN=a,DC=x`nCN=b,DC=x"
    }

    It 'reports a failure without throwing, with the exit code and the tool output' {
        Add-DockerResponse -ExitCode 68 -Output @('ldap_add: Already exists (68)')

        $result = Invoke-DirectoryLdif -DirectoryConfig (Get-AdTestConfig) -Ldif 'dn: x' -Operation add

        $result.Success | Should -BeFalse
        $result.ExitCode | Should -Be 68
        $result.Output | Should -Match 'Already exists'
    }

    It 'classifies an add as Created, AlreadyExists or Failed in Outcome' {
        Invoke-DirectoryLdif -DirectoryConfig (Get-AdTestConfig) -Ldif 'dn: x' -Operation add | ForEach-Object { $_.Outcome } | Should -Be 'Created'

        Add-DockerResponse -ExitCode 68 -Output @('ldap_add: Already exists (68)')
        (Invoke-DirectoryLdif -DirectoryConfig (Get-AdTestConfig) -Ldif 'dn: x' -Operation add).Outcome | Should -Be 'AlreadyExists'

        Add-DockerResponse -ExitCode 50 -Output @('ldap_add: Insufficient access (50)')
        (Invoke-DirectoryLdif -DirectoryConfig (Get-AdTestConfig) -Ldif 'dn: x' -Operation add).Outcome | Should -Be 'Failed'
    }

    It 'carries no Outcome for a modify or a delete' {
        (Invoke-DirectoryLdif -DirectoryConfig (Get-AdTestConfig) -Ldif 'dn: x' -Operation modify).ContainsKey('Outcome') | Should -BeFalse
    }
}

Describe 'New-DirectoryOu' {
    It 'runs samba-tool ou create with just the DN on Samba AD' {
        Add-DockerResponse -ExitCode 0

        $result = New-DirectoryOu -DirectoryConfig (Get-SambaTestConfig) -Dn 'OU=TestUsers,DC=panoply,DC=local'

        $script:dockerCalls[0].Args | Should -Be @('exec', 'samba-ad-primary', 'samba-tool', 'ou', 'create', 'OU=TestUsers,DC=panoply,DC=local')
        $result.Outcome | Should -Be 'Created'
    }

    It 'goes through the running server with -ViaServer, as Scenario 005 does' {
        New-DirectoryOu -DirectoryConfig (Get-SambaTestConfig) -Dn 'OU=Finance,OU=Users,OU=Corp,DC=panoply,DC=local' -ViaServer | Out-Null

        $script:dockerCalls[0].Args | Should -Be @('exec', 'samba-ad-primary', 'samba-tool', 'ou', 'create', 'OU=Finance,OU=Users,OU=Corp,DC=panoply,DC=local', '-H', 'ldap://localhost', '-U', 'Administrator%Test@123!')
    }

    It 'reports AlreadyExists when samba-tool says so' {
        Add-DockerResponse -ExitCode 255 -Output @('ERROR(ldb): Failed to create OU - Entry OU=TestUsers already exists')

        $result = New-DirectoryOu -DirectoryConfig (Get-SambaTestConfig) -Dn 'OU=TestUsers,DC=panoply,DC=local'

        $result.Outcome | Should -Be 'AlreadyExists'
        $result.Success | Should -BeTrue
    }

    It 'reports Failed for any other samba-tool failure' {
        Add-DockerResponse -ExitCode 255 -Output @('ERROR: parent does not exist')

        $result = New-DirectoryOu -DirectoryConfig (Get-SambaTestConfig) -Dn 'OU=X,OU=Missing,DC=panoply,DC=local'

        $result.Outcome | Should -Be 'Failed'
        $result.Success | Should -BeFalse
        $result.Output | Should -Match 'parent does not exist'
    }

    It 'adds an organizationalUnit with ldapadd through the toolbox on Active Directory' {
        $result = New-DirectoryOu -DirectoryConfig (Get-AdTestConfig) -Dn 'OU=Finance,OU=Users,OU=Corp,DC=panoply,DC=local'

        $script:dockerCalls[0].Args | Should -Be @('exec', '-i', 'jim-ldap-toolbox', 'ldapadd', '-x', '-H', 'ldaps://dc1.panoply.local:636', '-D', 'CN=Administrator,CN=Users,DC=panoply,DC=local', '-w', 'Admin@Pw-123!')
        $script:dockerCalls[0].Stdin | Should -Be "dn: OU=Finance,OU=Users,OU=Corp,DC=panoply,DC=local`nobjectClass: top`nobjectClass: organizationalUnit`nou: Finance"
        $result.Outcome | Should -Be 'Created'
    }

    It 'treats LDAP result 68 (Already exists) as AlreadyExists on Active Directory' {
        Add-DockerResponse -ExitCode 68 -Output @('ldap_add: Already exists (68)')

        (New-DirectoryOu -DirectoryConfig (Get-AdTestConfig) -Dn 'OU=TestUsers,DC=panoply,DC=local').Outcome | Should -Be 'AlreadyExists'
    }

    It 'uses ldapadd in the container on OpenLDAP' {
        New-DirectoryOu -DirectoryConfig (Get-OpenLdapTestConfig) -Dn 'ou=Extra,dc=yellowstone,dc=local' | Out-Null

        $script:dockerCalls[0].Args[0..4] | Should -Be @('exec', '-i', 'openldap-primary', 'ldapadd', '-x')
    }
}

Describe 'Remove-DirectoryEntry' {
    It 'runs ldapdelete with the DN last' {
        $result = Remove-DirectoryEntry -DirectoryConfig (Get-OpenLdapTestConfig) -Dn 'uid=x,ou=People,dc=yellowstone,dc=local'

        $script:dockerCalls[0].Args | Should -Be @('exec', 'openldap-primary', 'ldapdelete', '-x', '-H', 'ldap://localhost:1389', '-D', 'cn=admin,dc=yellowstone,dc=local', '-w', 'Test@123!', 'uid=x,ou=People,dc=yellowstone,dc=local')
        $result.Outcome | Should -Be 'Deleted'
    }

    It 'puts -r before the DN for a recursive delete' {
        Remove-DirectoryEntry -DirectoryConfig (Get-AdTestConfig) -Dn 'OU=X,DC=panoply,DC=local' -Recurse | Out-Null

        $script:dockerCalls[0].Args | Should -Be @('exec', 'jim-ldap-toolbox', 'ldapdelete', '-x', '-H', 'ldaps://dc1.panoply.local:636', '-D', 'CN=Administrator,CN=Users,DC=panoply,DC=local', '-w', 'Admin@Pw-123!', '-r', 'OU=X,DC=panoply,DC=local')
    }

    It 'classifies LDAP result 32 as NotFound' {
        Add-DockerResponse -ExitCode 32 -Output @('ldap_delete: No such object (32)')

        (Remove-DirectoryEntry -DirectoryConfig (Get-AdTestConfig) -Dn 'CN=x,DC=y').Outcome | Should -Be 'NotFound'
    }

    It 'classifies any other failure as Failed' {
        Add-DockerResponse -ExitCode 50 -Output @('ldap_delete: Insufficient access (50)')

        $result = Remove-DirectoryEntry -DirectoryConfig (Get-AdTestConfig) -Dn 'CN=x,DC=y'

        $result.Outcome | Should -Be 'Failed'
        $result.Success | Should -BeFalse
    }
}

Describe 'Remove-DirectoryOu' {
    It 'runs samba-tool ou delete on Samba AD' {
        Remove-DirectoryOu -DirectoryConfig (Get-SambaTestConfig) -Dn 'OU=X,DC=panoply,DC=local' | Out-Null

        $script:dockerCalls[0].Args | Should -Be @('exec', 'samba-ad-primary', 'samba-tool', 'ou', 'delete', 'OU=X,DC=panoply,DC=local')
    }

    It 'adds --force-subtree-delete with -Recurse on Samba AD' {
        Remove-DirectoryOu -DirectoryConfig (Get-SambaTestConfig) -Dn 'OU=X,DC=panoply,DC=local' -Recurse | Out-Null

        $script:dockerCalls[0].Args | Should -Be @('exec', 'samba-ad-primary', 'samba-tool', 'ou', 'delete', 'OU=X,DC=panoply,DC=local', '--force-subtree-delete')
    }

    It 'runs ldapdelete -r with -Recurse on Active Directory' {
        Remove-DirectoryOu -DirectoryConfig (Get-AdTestConfig) -Dn 'OU=X,DC=panoply,DC=local' -Recurse | Out-Null

        $script:dockerCalls[0].Args[2] | Should -Be 'ldapdelete'
        $script:dockerCalls[0].Args[-2..-1] | Should -Be @('-r', 'OU=X,DC=panoply,DC=local')
    }
}

Describe 'Remove-DirectoryUser' {
    It 'runs samba-tool user delete inside bash on Samba AD, keeping the EXIT_CODE trailer' {
        Add-DockerResponse -ExitCode 0 -Output @('Deleted user test.reconnect', 'EXIT_CODE:0')

        $result = Remove-DirectoryUser -DirectoryConfig (Get-SambaTestConfig) -SamAccountName 'test.reconnect'

        $script:dockerCalls[0].Args | Should -Be @('exec', 'samba-ad-primary', 'bash', '-c', 'samba-tool user delete ''test.reconnect'' 2>&1; echo EXIT_CODE:$?')
        $result.Outcome | Should -Be 'Deleted'
    }

    It 'classifies "Unable to find user" as NotFound on Samba AD' {
        Add-DockerResponse -ExitCode 0 -Output @('ERROR: Unable to find user "x"', 'EXIT_CODE:1')

        (Remove-DirectoryUser -DirectoryConfig (Get-SambaTestConfig) -SamAccountName 'x').Outcome | Should -Be 'NotFound'
    }

    It 'looks the DN up by sAMAccountName and then deletes it on Active Directory' {
        Add-DockerResponse -ExitCode 0 -Output @('dn: CN=Test Reconnect,OU=Users,OU=Corp,DC=panoply,DC=local', 'sAMAccountName: test.reconnect')
        Add-DockerResponse -ExitCode 0

        $result = Remove-DirectoryUser -DirectoryConfig (Get-AdTestConfig) -SamAccountName 'test.reconnect'

        $script:dockerCalls.Count | Should -Be 2
        $script:dockerCalls[0].Args[2] | Should -Be 'ldapsearch'
        $script:dockerCalls[0].Args | Should -Contain '(sAMAccountName=test.reconnect)'
        $script:dockerCalls[1].Args[2] | Should -Be 'ldapdelete'
        $script:dockerCalls[1].Args[-1] | Should -Be 'CN=Test Reconnect,OU=Users,OU=Corp,DC=panoply,DC=local'
        $result.Outcome | Should -Be 'Deleted'
    }

    It 'reports NotFound without deleting when the account is not on Active Directory' {
        Add-DockerResponse -ExitCode 0 -Output @()

        $result = Remove-DirectoryUser -DirectoryConfig (Get-AdTestConfig) -SamAccountName 'nobody'

        $script:dockerCalls.Count | Should -Be 1
        $result.Outcome | Should -Be 'NotFound'
    }

    It 'escapes LDAP filter metacharacters in the account name' {
        Remove-DirectoryUser -DirectoryConfig (Get-AdTestConfig) -SamAccountName 'a*b(c)' | Out-Null

        $script:dockerCalls[0].Args | Should -Contain '(sAMAccountName=a\2ab\28c\29)'
    }

    It 'deletes by the RDN attribute and user container on OpenLDAP' {
        Remove-DirectoryUser -DirectoryConfig (Get-OpenLdapTestConfig) -SamAccountName 'alice' | Out-Null

        $script:dockerCalls[0].Args[-1] | Should -Be 'uid=alice,ou=People,dc=yellowstone,dc=local'
    }
}

Describe 'New-DirectoryUser' {
    It 'runs samba-tool user create with the argument order Scenario 002 always used' {
        $result = New-DirectoryUser -DirectoryConfig (Get-SambaTestConfig) -Dn 'CN=CrossDomain TestUser,OU=TestUsers,DC=panoply,DC=local' `
            -SamAccountName 'crossdomain.test1' -Password 'Password123!' `
            -Attributes ([ordered]@{ givenName = 'CrossDomain'; sn = 'TestUser'; mail = 'crossdomain.test1@panoply.local'; department = 'Engineering' })

        $script:dockerCalls.Count | Should -Be 1
        $script:dockerCalls[0].Args | Should -Be @(
            'exec', 'samba-ad-primary', 'samba-tool', 'user', 'create', 'crossdomain.test1', 'Password123!',
            '--userou=OU=TestUsers', '--given-name=CrossDomain', '--surname=TestUser',
            '--mail-address=crossdomain.test1@panoply.local', '--department=Engineering')
        $result.Outcome | Should -Be 'Created'
    }

    It 'passes --use-username-as-cn when the DN''s CN is the account name' {
        New-DirectoryUser -DirectoryConfig (Get-SambaTestConfig) -Dn 'CN=svc.probe,OU=Services,DC=panoply,DC=local' -SamAccountName 'svc.probe' -Password 'Password123!' | Out-Null

        $script:dockerCalls[0].Args | Should -Contain '--use-username-as-cn'
        $script:dockerCalls[0].Args | Should -Contain '--userou=OU=Services'
    }

    It 'refuses a Samba AD DN whose CN samba-tool could not produce' {
        { New-DirectoryUser -DirectoryConfig (Get-SambaTestConfig) -Dn 'CN=Something Else,OU=Users,DC=panoply,DC=local' -SamAccountName 'x' -Password 'Password123!' -Attributes @{ givenName = 'A'; sn = 'B' } } |
            Should -Throw '*CN*'
    }

    It 'disables the account after creating it on Samba AD when -Enabled is false' {
        New-DirectoryUser -DirectoryConfig (Get-SambaTestConfig) -Dn 'CN=svc.probe,OU=Services,DC=panoply,DC=local' -SamAccountName 'svc.probe' -Password 'Password123!' -Enabled $false | Out-Null

        $script:dockerCalls.Count | Should -Be 2
        $script:dockerCalls[1].Args | Should -Be @('exec', 'samba-ad-primary', 'samba-tool', 'user', 'disable', 'svc.probe')
    }

    It 'applies attributes samba-tool has no option for with a follow-up ldapmodify' {
        New-DirectoryUser -DirectoryConfig (Get-SambaTestConfig) -Dn 'CN=svc.probe,OU=Services,DC=panoply,DC=local' -SamAccountName 'svc.probe' -Password 'Password123!' -Attributes @{ employeeID = 'E1' } | Out-Null

        $script:dockerCalls.Count | Should -Be 2
        $script:dockerCalls[1].Args[3] | Should -Be 'ldapmodify'
        $script:dockerCalls[1].Stdin | Should -Be "dn: CN=svc.probe,OU=Services,DC=panoply,DC=local`nchangetype: modify`nreplace: employeeID`nemployeeID: E1"
    }

    It 'reports AlreadyExists when samba-tool says the user exists' {
        Add-DockerResponse -ExitCode 255 -Output @('ERROR: Failed to add user: Entry CN=x already exists')

        (New-DirectoryUser -DirectoryConfig (Get-SambaTestConfig) -Dn 'CN=x,OU=Services,DC=panoply,DC=local' -SamAccountName 'x' -Password 'Password123!').Outcome | Should -Be 'AlreadyExists'
    }

    It 'adds the user with ldapadd, then sets unicodePwd and enables it, over LDAPS on Active Directory' {
        $result = New-DirectoryUser -DirectoryConfig (Get-AdTestConfig) -Dn 'CN=CrossDomain TestUser,OU=TestUsers,DC=panoply,DC=local' `
            -SamAccountName 'crossdomain.test1' -Password 'Password123!' `
            -Attributes ([ordered]@{ givenName = 'CrossDomain'; sn = 'TestUser'; department = 'Engineering' })

        $script:dockerCalls.Count | Should -Be 2
        $script:dockerCalls[0].Args[0..4] | Should -Be @('exec', '-i', 'jim-ldap-toolbox', 'ldapadd', '-x')
        $script:dockerCalls[0].Stdin | Should -Be ((@(
            'dn: CN=CrossDomain TestUser,OU=TestUsers,DC=panoply,DC=local',
            'objectClass: top', 'objectClass: person', 'objectClass: organizationalPerson', 'objectClass: user',
            'cn: CrossDomain TestUser', 'sAMAccountName: crossdomain.test1', 'userPrincipalName: crossdomain.test1@panoply.local',
            'givenName: CrossDomain', 'sn: TestUser', 'department: Engineering') -join "`n"))

        $unicodePwd = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes('"Password123!"'))
        $script:dockerCalls[1].Args[0..4] | Should -Be @('exec', '-i', 'jim-ldap-toolbox', 'ldapmodify', '-x')
        $script:dockerCalls[1].Stdin | Should -Be ((@(
            'dn: CN=CrossDomain TestUser,OU=TestUsers,DC=panoply,DC=local', 'changetype: modify',
            'replace: unicodePwd', "unicodePwd:: $unicodePwd", '',
            'dn: CN=CrossDomain TestUser,OU=TestUsers,DC=panoply,DC=local', 'changetype: modify',
            'replace: userAccountControl', 'userAccountControl: 512') -join "`n"))
        $result.Outcome | Should -Be 'Created'
    }

    It 'leaves the account disabled (514 is the directory default) when -Enabled is false on Active Directory' {
        New-DirectoryUser -DirectoryConfig (Get-AdTestConfig) -Dn 'CN=x,OU=Services,DC=panoply,DC=local' -SamAccountName 'x' -Password 'Password123!' -Enabled $false | Out-Null

        $script:dockerCalls[1].Stdin | Should -Not -Match 'userAccountControl: 512'
    }

    It 'does not set a password when the add says the entry already exists on Active Directory' {
        Add-DockerResponse -ExitCode 68 -Output @('ldap_add: Already exists (68)')

        $result = New-DirectoryUser -DirectoryConfig (Get-AdTestConfig) -Dn 'CN=x,OU=Services,DC=panoply,DC=local' -SamAccountName 'x' -Password 'Password123!'

        $script:dockerCalls.Count | Should -Be 1
        $result.Outcome | Should -Be 'AlreadyExists'
    }

    It 'reports Failed with the tool output when the password step is refused on Active Directory' {
        Add-DockerResponse -ExitCode 0
        Add-DockerResponse -ExitCode 19 -Output @('ldap_modify: Constraint violation (19)', "`tadditional info: 0000052D: AtrErr")

        $result = New-DirectoryUser -DirectoryConfig (Get-AdTestConfig) -Dn 'CN=x,OU=Services,DC=panoply,DC=local' -SamAccountName 'x' -Password 'weak'

        $result.Outcome | Should -Be 'Failed'
        $result.Output | Should -Match '0000052D'
    }

    It 'refuses an RFC directory, whose users the scripts build from LDIF' {
        { New-DirectoryUser -DirectoryConfig (Get-OpenLdapTestConfig) -Dn 'uid=x,ou=People,dc=yellowstone,dc=local' -SamAccountName 'x' -Password 'p' } |
            Should -Throw '*Active Directory*'
    }
}

Describe 'Get-DirectoryEntry' {
    It 'searches the DN at base scope and returns the entry as a hashtable of string arrays' {
        Add-DockerResponse -ExitCode 0 -Output @('dn: CN=Alice,OU=Users,DC=panoply,DC=local', 'givenName: Alice', 'memberOf: CN=A,DC=x', 'memberOf: CN=B,DC=x')

        $entry = Get-DirectoryEntry -DirectoryConfig (Get-AdTestConfig) -Dn 'CN=Alice,OU=Users,DC=panoply,DC=local' -Attributes 'givenName', 'memberOf'

        $script:dockerCalls[0].Args | Should -Be @(
            'exec', 'jim-ldap-toolbox', 'ldapsearch', '-x', '-H', 'ldaps://dc1.panoply.local:636',
            '-D', 'CN=Administrator,CN=Users,DC=panoply,DC=local', '-w', 'Admin@Pw-123!',
            '-LLL', '-b', 'CN=Alice,OU=Users,DC=panoply,DC=local', '-s', 'base', '(objectClass=*)', 'givenName', 'memberOf')
        $entry.dn | Should -Be 'CN=Alice,OU=Users,DC=panoply,DC=local'
        $entry.givenName | Should -Be @('Alice')
        $entry.memberOf | Should -Be @('CN=A,DC=x', 'CN=B,DC=x')
    }

    It 'returns null for LDAP result 32 (no such object)' {
        Add-DockerResponse -ExitCode 32 -Output @('No such object (32)')

        Get-DirectoryEntry -DirectoryConfig (Get-SambaTestConfig) -Dn 'CN=x,DC=y' | Should -BeNullOrEmpty
    }

    It 'throws for a failure that is not "no such object"' {
        Add-DockerResponse -ExitCode 255 -Output @("Can't contact LDAP server (-1)")

        { Get-DirectoryEntry -DirectoryConfig (Get-SambaTestConfig) -Dn 'CN=x,DC=y' } | Should -Throw "*Can't contact*"
    }
}

Describe 'Find-DirectoryEntry' {
    It 'searches the subtree from the base DN by default' {
        Add-DockerResponse -ExitCode 0 -Output @('dn: CN=A,DC=x', 'department: Finance')

        $entries = @(Find-DirectoryEntry -DirectoryConfig (Get-SambaTestConfig) -Filter '(sAMAccountName=alice)' -Attributes 'department')

        $script:dockerCalls[0].Args | Should -Be @(
            'exec', 'samba-ad-primary', 'ldapsearch', '-x', '-H', 'ldap://localhost:389',
            '-D', 'CN=Administrator,CN=Users,DC=panoply,DC=local', '-w', 'Test@123!',
            '-LLL', '-b', 'DC=panoply,DC=local', '-s', 'sub', '(sAMAccountName=alice)', 'department')
        $entries.Count | Should -Be 1
        $entries[0].department | Should -Be @('Finance')
    }

    It 'returns unfolded LDIF text with -AsText, in the shape ldbsearch printed' {
        Add-DockerResponse -ExitCode 0 -Output @('dn: CN=Alice,OU=Finance,DC=panoply,DC=l', ' ocal', 'department: Finance')

        $text = Find-DirectoryEntry -DirectoryConfig (Get-AdTestConfig) -Filter '(sAMAccountName=alice)' -Attributes 'dn', 'department' -AsText

        $text | Should -Match 'dn: CN=Alice,OU=Finance,DC=panoply,DC=local'
        $text | Should -Match 'department: Finance'
        $script:dockerCalls[0].Args | Should -Not -Contain 'dn'
    }

    It 'decodes base64 values in the -AsText text, so it reads as ldbsearch printed it' {
        $b64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes("CN=$($script:unalName),OU=Users,DC=x"))
        Add-DockerResponse -ExitCode 0 -Output @("dn:: $b64", "sn: $([char]0x00DC)nal")

        $text = Find-DirectoryEntry -DirectoryConfig (Get-AdTestConfig) -Filter '(sn=*)' -AsText

        $text | Should -Match ([regex]::Escape("dn: CN=$($script:unalName),OU=Users,DC=x"))
        $text | Should -Not -Match '::'
    }

    It 'returns the tool''s error text with -AsText instead of throwing' {
        Add-DockerResponse -ExitCode 255 -Output @("Can't contact LDAP server (-1)")

        Find-DirectoryEntry -DirectoryConfig (Get-AdTestConfig) -Filter '(cn=x)' -AsText | Should -Match "Can't contact"
    }

    It 'returns no entries for LDAP result 32' {
        Add-DockerResponse -ExitCode 32 -Output @('No such object (32)')

        @(Find-DirectoryEntry -DirectoryConfig (Get-AdTestConfig) -Filter '(cn=x)').Count | Should -Be 0
    }
}

Describe 'Test-DirectoryEntryPresent' {
    It 'is true when the search finds an entry' {
        Add-DockerResponse -ExitCode 0 -Output @('dn: CN=A,DC=x')

        Test-DirectoryEntryPresent -DirectoryConfig (Get-AdTestConfig) -Filter '(sAMAccountName=a)' | Should -BeTrue
        $script:dockerCalls[0].Args | Should -Contain '1.1'
    }

    It 'is false when there is no such entry' {
        Add-DockerResponse -ExitCode 0 -Output @()

        Test-DirectoryEntryPresent -DirectoryConfig (Get-SambaTestConfig) -Filter '(sAMAccountName=a)' | Should -BeFalse
    }

    It 'is false for LDAP result 32, and throws for a search that could not run' {
        Add-DockerResponse -ExitCode 32 -Output @('No such object (32)')
        Test-DirectoryEntryPresent -DirectoryConfig (Get-SambaTestConfig) -Filter '(cn=a)' -BaseDn 'OU=Gone,DC=panoply,DC=local' | Should -BeFalse

        Add-DockerResponse -ExitCode 255 -Output @("Can't contact LDAP server (-1)")
        { Test-DirectoryEntryPresent -DirectoryConfig (Get-SambaTestConfig) -Filter '(cn=a)' } | Should -Throw "*Can't contact*"
    }
}

Describe 'New-DirectoryGroup' {
    It 'runs samba-tool group add with the arguments Scenario 008 always used' {
        $result = New-DirectoryGroup -DirectoryConfig (Get-SambaTestConfig) -Dn 'CN=Team Alpha,OU=Entitlements,OU=Corp,DC=panoply,DC=local' -Description 'Team Alpha'

        $script:dockerCalls[0].Args | Should -Be @('exec', 'samba-ad-primary', 'samba-tool', 'group', 'add', 'Team Alpha', '--groupou=OU=Entitlements,OU=Corp', '--description=Team Alpha')
        $result.Outcome | Should -Be 'Created'
    }

    It 'adds a global security group with ldapadd on Active Directory' {
        New-DirectoryGroup -DirectoryConfig (Get-AdTestConfig) -Dn 'CN=Team Alpha,OU=Entitlements,OU=Corp,DC=panoply,DC=local' -Description 'Team Alpha' | Out-Null

        $script:dockerCalls[0].Args[3] | Should -Be 'ldapadd'
        $script:dockerCalls[0].Stdin | Should -Be ((@(
            'dn: CN=Team Alpha,OU=Entitlements,OU=Corp,DC=panoply,DC=local', 'objectClass: top', 'objectClass: group',
            'cn: Team Alpha', 'sAMAccountName: Team Alpha', 'groupType: -2147483646', 'description: Team Alpha') -join "`n"))
    }

    It 'refuses an RFC directory' {
        { New-DirectoryGroup -DirectoryConfig (Get-OpenLdapTestConfig) -Dn 'cn=g,ou=Groups,dc=yellowstone,dc=local' -Description 'x' } | Should -Throw '*Active Directory*'
    }
}

Describe 'Remove-DirectoryGroup' {
    It 'runs samba-tool group delete with the CN as the group name on Samba AD' {
        Remove-DirectoryGroup -DirectoryConfig (Get-SambaTestConfig) -Dn 'CN=Team Alpha,OU=Entitlements,OU=Corp,DC=panoply,DC=local' | Out-Null

        $script:dockerCalls[0].Args | Should -Be @('exec', 'samba-ad-primary', 'samba-tool', 'group', 'delete', 'Team Alpha')
    }

    It 'runs ldapdelete by DN on Active Directory' {
        Remove-DirectoryGroup -DirectoryConfig (Get-AdTestConfig) -Dn 'CN=Team Alpha,OU=Entitlements,OU=Corp,DC=panoply,DC=local' | Out-Null

        $script:dockerCalls[0].Args[1..2] | Should -Be @('jim-ldap-toolbox', 'ldapdelete')
        $script:dockerCalls[0].Args[-1] | Should -Be 'CN=Team Alpha,OU=Entitlements,OU=Corp,DC=panoply,DC=local'
    }
}

Describe 'Add-DirectoryGroupMember and Remove-DirectoryGroupMember' {
    It 'runs samba-tool group addmembers by account name on Samba AD' {
        Add-DirectoryGroupMember -DirectoryConfig (Get-SambaTestConfig) -GroupDn 'CN=Team Alpha,OU=Entitlements,OU=Corp,DC=panoply,DC=local' -MemberSamAccountName 'alice.smith' | Out-Null

        $script:dockerCalls[0].Args | Should -Be @('exec', 'samba-ad-primary', 'samba-tool', 'group', 'addmembers', 'Team Alpha', 'alice.smith')
    }

    It 'runs samba-tool group removemembers by account name on Samba AD' {
        Remove-DirectoryGroupMember -DirectoryConfig (Get-SambaTestConfig) -GroupDn 'CN=Team Alpha,OU=Entitlements,OU=Corp,DC=panoply,DC=local' -MemberSamAccountName 'alice.smith' | Out-Null

        $script:dockerCalls[0].Args | Should -Be @('exec', 'samba-ad-primary', 'samba-tool', 'group', 'removemembers', 'Team Alpha', 'alice.smith')
    }

    It 'modifies the member attribute by DN over LDAP with -MemberDn' {
        Add-DirectoryGroupMember -DirectoryConfig (Get-AdTestConfig) -GroupDn 'CN=G,OU=Groups,DC=panoply,DC=local' -MemberDn 'CN=Alice,OU=Users,DC=panoply,DC=local' | Out-Null

        $script:dockerCalls[0].Args[3] | Should -Be 'ldapmodify'
        $script:dockerCalls[0].Stdin | Should -Be "dn: CN=G,OU=Groups,DC=panoply,DC=local`nchangetype: modify`nadd: member`nmember: CN=Alice,OU=Users,DC=panoply,DC=local"
    }

    It 'deletes the member value with -MemberDn on removal' {
        Remove-DirectoryGroupMember -DirectoryConfig (Get-AdTestConfig) -GroupDn 'CN=G,OU=Groups,DC=panoply,DC=local' -MemberDn 'CN=Alice,OU=Users,DC=panoply,DC=local' | Out-Null

        $script:dockerCalls[0].Stdin | Should -Be "dn: CN=G,OU=Groups,DC=panoply,DC=local`nchangetype: modify`ndelete: member`nmember: CN=Alice,OU=Users,DC=panoply,DC=local"
    }

    It 'resolves an account name to a DN first on Active Directory' {
        Add-DockerResponse -ExitCode 0 -Output @('dn: CN=Alice Smith,OU=Users,OU=Corp,DC=panoply,DC=local')

        Add-DirectoryGroupMember -DirectoryConfig (Get-AdTestConfig) -GroupDn 'CN=G,OU=Groups,DC=panoply,DC=local' -MemberSamAccountName 'alice.smith' | Out-Null

        $script:dockerCalls[0].Args | Should -Contain '(sAMAccountName=alice.smith)'
        $script:dockerCalls[1].Stdin | Should -Match 'member: CN=Alice Smith,OU=Users,OU=Corp,DC=panoply,DC=local'
    }

    It 'throws when the account name resolves to nothing' {
        Add-DockerResponse -ExitCode 0 -Output @()

        { Add-DirectoryGroupMember -DirectoryConfig (Get-AdTestConfig) -GroupDn 'CN=G,DC=x' -MemberSamAccountName 'nobody' } | Should -Throw "*'nobody'*"
    }
}

Describe 'Get-DirectoryGroupMember' {
    It 'runs samba-tool group listmembers and returns one account name per line on Samba AD' {
        Add-DockerResponse -ExitCode 0 -Output @('alice.smith', '', 'bob.jones ')

        $members = @(Get-DirectoryGroupMember -DirectoryConfig (Get-SambaTestConfig) -GroupDn 'CN=Team Alpha,OU=Entitlements,OU=Corp,DC=panoply,DC=local' -AsSamAccountName)

        $script:dockerCalls[0].Args | Should -Be @('exec', 'samba-ad-primary', 'samba-tool', 'group', 'listmembers', 'Team Alpha')
        $members | Should -Be @('alice.smith', 'bob.jones')
    }

    It 'returns an empty list when samba-tool fails' {
        Add-DockerResponse -ExitCode 255 -Output @('ERROR')

        @(Get-DirectoryGroupMember -DirectoryConfig (Get-SambaTestConfig) -GroupDn 'CN=X,DC=y' -AsSamAccountName).Count | Should -Be 0
    }

    It 'finds members through memberOf, so a large group is not truncated by ranged retrieval, on Active Directory' {
        Add-DockerResponse -ExitCode 0 -Output @('dn: CN=Alice,DC=x', 'sAMAccountName: alice', '', 'dn: CN=Bob,DC=x', 'sAMAccountName: bob')

        $members = @(Get-DirectoryGroupMember -DirectoryConfig (Get-AdTestConfig) -GroupDn 'CN=G,OU=Groups,DC=panoply,DC=local' -AsSamAccountName)

        $script:dockerCalls[0].Args | Should -Contain '(memberOf=CN=G,OU=Groups,DC=panoply,DC=local)'
        $members | Should -Be @('alice', 'bob')
    }

    It 'returns member DNs when -AsSamAccountName is not given' {
        Add-DockerResponse -ExitCode 0 -Output @('dn: CN=Alice,DC=x', '', 'dn: CN=Bob,DC=x')

        @(Get-DirectoryGroupMember -DirectoryConfig (Get-AdTestConfig) -GroupDn 'CN=G,DC=x') | Should -Be @('CN=Alice,DC=x', 'CN=Bob,DC=x')
    }
}

Describe 'Get-DirectoryPasswordPolicy' {
    It 'parses samba-tool domain passwordsettings show' {
        Add-DockerResponse -ExitCode 0 -Output @(
            'Password information for domain ''DC=panoply,DC=local''', '',
            'Password complexity: off', 'Store plaintext passwords: off', 'Password history length: 24',
            'Minimum password length: 7', 'Minimum password age (days): 1', 'Maximum password age (days): 42')

        $policy = Get-DirectoryPasswordPolicy -DirectoryConfig (Get-SambaTestConfig)

        $script:dockerCalls[0].Args | Should -Be @('exec', 'samba-ad-primary', 'samba-tool', 'domain', 'passwordsettings', 'show')
        $policy.Complexity | Should -BeFalse
        $policy.MinLength | Should -Be 7
        $policy.HistoryLength | Should -Be 24
        $policy.MinAgeDays | Should -Be 1
        $policy.MaxAgeDays | Should -Be 42
    }

    It 'throws when the samba-tool output has no minimum length' {
        Add-DockerResponse -ExitCode 0 -Output @('nonsense')

        { Get-DirectoryPasswordPolicy -DirectoryConfig (Get-SambaTestConfig) } | Should -Throw '*Minimum password length*'
    }

    It 'reads the domain head object and converts 100 ns intervals to days on Active Directory' {
        # 42 days and 1 day as negative 100 nanosecond intervals; pwdProperties 1 is DOMAIN_PASSWORD_COMPLEX.
        Add-DockerResponse -ExitCode 0 -Output @(
            'dn: DC=panoply,DC=local', 'pwdProperties: 1', 'minPwdLength: 7', 'pwdHistoryLength: 24',
            'minPwdAge: -864000000000', 'maxPwdAge: -36288000000000')

        $policy = Get-DirectoryPasswordPolicy -DirectoryConfig (Get-AdTestConfig)

        $script:dockerCalls[0].Args | Should -Contain 'DC=panoply,DC=local'
        $script:dockerCalls[0].Args | Should -Contain 'base'
        $policy.Complexity | Should -BeTrue
        $policy.MinLength | Should -Be 7
        $policy.HistoryLength | Should -Be 24
        $policy.MinAgeDays | Should -Be 1
        $policy.MaxAgeDays | Should -Be 42
    }

    It 'reads a maxPwdAge of the minimum 64-bit value as 0 (passwords never expire)' {
        Add-DockerResponse -ExitCode 0 -Output @('dn: DC=panoply,DC=local', 'pwdProperties: 0', 'minPwdLength: 0', 'pwdHistoryLength: 0', 'minPwdAge: 0', 'maxPwdAge: -9223372036854775808')

        $policy = Get-DirectoryPasswordPolicy -DirectoryConfig (Get-AdTestConfig)

        $policy.MaxAgeDays | Should -Be 0
        $policy.Complexity | Should -BeFalse
    }

    It 'refuses an RFC directory' {
        { Get-DirectoryPasswordPolicy -DirectoryConfig (Get-OpenLdapTestConfig) } | Should -Throw '*domain password policy*'
    }
}

Describe 'Set-DirectoryPasswordPolicy' {
    It 'runs samba-tool domain passwordsettings set with only the settings given' {
        Set-DirectoryPasswordPolicy -DirectoryConfig (Get-SambaTestConfig) -MinLength 10 | Out-Null

        $script:dockerCalls[0].Args | Should -Be @('exec', 'samba-ad-primary', 'samba-tool', 'domain', 'passwordsettings', 'set', '--min-pwd-length=10')
    }

    It 'passes every setting in a fixed order' {
        Set-DirectoryPasswordPolicy -DirectoryConfig (Get-SambaTestConfig) -Complexity off -MinLength 7 -HistoryLength 0 -MinAgeDays 0 -MaxAgeDays 0 | Out-Null

        $script:dockerCalls[0].Args[3..10] | Should -Be @('domain', 'passwordsettings', 'set', '--complexity=off', '--min-pwd-length=7', '--history-length=0', '--min-pwd-age=0', '--max-pwd-age=0')
    }

    It 'throws when nothing is asked for' {
        { Set-DirectoryPasswordPolicy -DirectoryConfig (Get-SambaTestConfig) } | Should -Throw '*at least one*'
    }

    It 'replaces the domain head object''s attributes over LDAPS on Active Directory' {
        Add-DockerResponse -ExitCode 0 -Output @('dn: DC=panoply,DC=local', 'pwdProperties: 9')

        Set-DirectoryPasswordPolicy -DirectoryConfig (Get-AdTestConfig) -Complexity off -MinLength 12 -HistoryLength 5 -MinAgeDays 1 -MaxAgeDays 0 | Out-Null

        $script:dockerCalls.Count | Should -Be 2
        $script:dockerCalls[0].Args[2] | Should -Be 'ldapsearch'
        $script:dockerCalls[1].Args[0..4] | Should -Be @('exec', '-i', 'jim-ldap-toolbox', 'ldapmodify', '-x')
        # pwdProperties keeps its other bits (9 is 8 plus 1); only DOMAIN_PASSWORD_COMPLEX (1) clears.
        $script:dockerCalls[1].Stdin | Should -Be ((@(
            'dn: DC=panoply,DC=local', 'changetype: modify',
            'replace: pwdProperties', 'pwdProperties: 8', '-',
            'replace: minPwdLength', 'minPwdLength: 12', '-',
            'replace: pwdHistoryLength', 'pwdHistoryLength: 5', '-',
            'replace: minPwdAge', 'minPwdAge: -864000000000', '-',
            'replace: maxPwdAge', 'maxPwdAge: -9223372036854775808') -join "`n"))
    }

    It 'sets the complexity bit when -Complexity is on, keeping the other pwdProperties bits' {
        Add-DockerResponse -ExitCode 0 -Output @('dn: DC=panoply,DC=local', 'pwdProperties: 8')

        Set-DirectoryPasswordPolicy -DirectoryConfig (Get-AdTestConfig) -Complexity on | Out-Null

        $script:dockerCalls[1].Stdin | Should -Match 'pwdProperties: 9'
    }

    It 'skips the read when -Complexity is not given' {
        Set-DirectoryPasswordPolicy -DirectoryConfig (Get-AdTestConfig) -MinLength 7 | Out-Null

        $script:dockerCalls.Count | Should -Be 1
        $script:dockerCalls[0].Args[3] | Should -Be 'ldapmodify'
    }

    It 'converts a positive age in days to a negative 100 ns interval' {
        Set-DirectoryPasswordPolicy -DirectoryConfig (Get-AdTestConfig) -MaxAgeDays 42 | Out-Null

        $script:dockerCalls[0].Stdin | Should -Match 'maxPwdAge: -36288000000000'
    }
}

Describe 'Get-DirectoryHealthStatus' {
    It 'reads the container health from docker inspect for a config with a container' {
        Add-DockerResponse -ExitCode 0 -Output @('healthy')

        $status = Get-DirectoryHealthStatus -DirectoryConfig (Get-SambaTestConfig)

        $script:dockerCalls[0].Args | Should -Be @('inspect', '--format={{.State.Health.Status}}', 'samba-ad-primary')
        $status | Should -Be 'healthy'
    }

    It 'reports healthy for a container-less config when an LDAPS bind as the administrator succeeds' {
        Mock Test-LDAPBind { @{ Outcome = 'Success'; ExitCode = 0; Output = 'u:PANOPLY\Administrator' } }

        Get-DirectoryHealthStatus -DirectoryConfig (Get-AdTestConfig) | Should -Be 'healthy'
        Should -Invoke Test-LDAPBind -Times 1 -ParameterFilter { $BindDN -eq 'CN=Administrator,CN=Users,DC=panoply,DC=local' }
    }

    It 'reports unreachable when the bind fails' {
        Mock Test-LDAPBind { @{ Outcome = 'Failed'; ExitCode = 255; Output = "Can't contact LDAP server" } }

        Get-DirectoryHealthStatus -DirectoryConfig (Get-AdTestConfig) | Should -Be 'unreachable'
    }
}

Describe 'no docker exec for an Active Directory config outside the toolbox' {
    It 'never names a container other than the toolbox when every helper is given an Active Directory config' {
        $config = Get-AdTestConfig

        New-DirectoryOu -DirectoryConfig $config -Dn 'OU=X,DC=panoply,DC=local' | Out-Null
        Remove-DirectoryOu -DirectoryConfig $config -Dn 'OU=X,DC=panoply,DC=local' -Recurse | Out-Null
        New-DirectoryUser -DirectoryConfig $config -Dn 'CN=u,OU=X,DC=panoply,DC=local' -SamAccountName 'u' -Password 'Password123!' | Out-Null
        Get-DirectoryEntry -DirectoryConfig $config -Dn 'CN=u,OU=X,DC=panoply,DC=local' | Out-Null
        Find-DirectoryEntry -DirectoryConfig $config -Filter '(cn=u)' | Out-Null
        New-DirectoryGroup -DirectoryConfig $config -Dn 'CN=g,OU=X,DC=panoply,DC=local' -Description 'g' | Out-Null
        Add-DirectoryGroupMember -DirectoryConfig $config -GroupDn 'CN=g,OU=X,DC=panoply,DC=local' -MemberDn 'CN=u,OU=X,DC=panoply,DC=local' | Out-Null
        Get-DirectoryGroupMember -DirectoryConfig $config -GroupDn 'CN=g,OU=X,DC=panoply,DC=local' | Out-Null
        Add-DockerResponse -ExitCode 0 -Output @('dn: DC=panoply,DC=local', 'pwdProperties: 1')
        Set-DirectoryPasswordPolicy -DirectoryConfig $config -Complexity off -MinLength 7 | Out-Null
        Remove-DirectoryUser -DirectoryConfig $config -SamAccountName 'u' | Out-Null
        Remove-DirectoryGroup -DirectoryConfig $config -Dn 'CN=g,OU=X,DC=panoply,DC=local' | Out-Null

        $script:dockerCalls.Count | Should -BeGreaterThan 10
        foreach ($call in $script:dockerCalls) {
            $call.Args[0] | Should -Be 'exec'
            ($call.Args | Where-Object { $_ -in @('jim-ldap-toolbox') }) | Should -Not -BeNullOrEmpty
            $call.Args | Should -Not -Contain 'samba-tool'
            $call.Args | Should -Not -Contain 'bash'
        }
    }
}
}
