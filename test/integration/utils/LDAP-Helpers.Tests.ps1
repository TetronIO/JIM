# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

#Requires -Modules Pester

<#
.SYNOPSIS
    Pester tests for the LDIF line-unfolding helper in LDAP-Helpers.ps1.

.DESCRIPTION
    RFC 2849 folds long LDIF values (e.g. Distinguished Names) at 78 columns: a
    continuation line begins with a single space. Guards Expand-LDIFFoldedLine, which
    reassembles those continuation lines, and its use by Get-LDAPUser, whose failure to
    unfold a folded dn: line truncated a DN and broke Scenario 001 IEO Phase 3 on Samba AD
    (issue exposed by PR #1100's new test; see #1102 follow-up SPEC-1102B).
#>

BeforeAll {
    . "$PSScriptRoot/LDAP-Helpers.ps1"
}

Describe 'Expand-LDIFFoldedLine' {
    It 'reassembles a dn: line folded at exactly 78 columns' {
        # The real failure: "dn: CN=Oscar Harper,...,DC=l" is exactly 78 characters,
        # ldapsearch folds after it, and the continuation " ocal" completes "DC=local".
        $foldedFirstLine = 'dn: CN=Oscar Harper,OU=Information Technology,OU=Users,OU=Corp,DC=panoply,DC=l'
        $foldedFirstLine.Length | Should -Be 78
        $raw = "$foldedFirstLine`n ocal`nobjectClass: user"

        $lines = Expand-LDIFFoldedLine -RawLdif $raw

        $lines[0] | Should -Be 'dn: CN=Oscar Harper,OU=Information Technology,OU=Users,OU=Corp,DC=panoply,DC=local'
        $lines[1] | Should -Be 'objectClass: user'
    }

    It 'reassembles a folded member DN inside a multi-valued attribute block' {
        $raw = (@(
            'dn: CN=Group1,OU=Groups,DC=panoply,DC=local'
            'objectClass: group'
            'member: CN=Alice Wonderland,OU=Information Technology,OU=Users,OU=Corp,DC=panoply,DC=l'
            ' ocal'
            'member: CN=Bob,OU=Users,DC=panoply,DC=local'
        ) -join "`n")

        $lines = Expand-LDIFFoldedLine -RawLdif $raw
        $memberLines = @($lines | Where-Object { $_ -match '^member:' })

        $memberLines | Should -HaveCount 2
        $memberLines[0] | Should -Be 'member: CN=Alice Wonderland,OU=Information Technology,OU=Users,OU=Corp,DC=panoply,DC=local'
        $memberLines[1] | Should -Be 'member: CN=Bob,OU=Users,DC=panoply,DC=local'
    }

    It 'strips trailing carriage returns from CRLF line endings' {
        $raw = "dn: CN=x,DC=y`r`nobjectClass: user`r`n"

        $lines = Expand-LDIFFoldedLine -RawLdif $raw

        foreach ($line in $lines) {
            $line | Should -Not -Match "`r"
        }
        $lines[0] | Should -Be 'dn: CN=x,DC=y'
    }

    It 'preserves comment and blank lines as their own logical lines without merging' {
        $raw = "# refldap://example`ndn: CN=x,DC=y`n`nobjectClass: user"

        $lines = Expand-LDIFFoldedLine -RawLdif $raw

        $lines.Count | Should -Be 4
        $lines[0] | Should -Be '# refldap://example'
        $lines[1] | Should -Be 'dn: CN=x,DC=y'
        $lines[2] | Should -Be ''
        $lines[3] | Should -Be 'objectClass: user'
    }

    It 'keeps a leading-space line with no predecessor as-is rather than throwing' {
        { Expand-LDIFFoldedLine -RawLdif ' orphan continuation' } | Should -Not -Throw
        (Expand-LDIFFoldedLine -RawLdif ' orphan continuation')[0] | Should -Be ' orphan continuation'
    }
}

Describe 'Get-LDAPUser (folded LDIF)' {
    It 'returns the complete dn value when ldapsearch output folds the dn line' {
        # Mimics real Invoke-LDAPSearch output: PowerShell captures native command output
        # as a string array, one element per physical (pre-unfold) line.
        Mock Invoke-LDAPSearch {
            return @(
                'dn: CN=Oscar Harper,OU=Information Technology,OU=Users,OU=Corp,DC=panoply,DC=l',
                ' ocal',
                'objectClass: user',
                'sAMAccountName: oharper'
            )
        }

        $user = Get-LDAPUser -UserIdentifier 'oharper' -BaseDN 'DC=panoply,DC=local' `
            -BindDN 'CN=Administrator,CN=Users,DC=panoply,DC=local' -BindPassword 'Test@123!'

        $user.dn | Should -Be 'CN=Oscar Harper,OU=Information Technology,OU=Users,OU=Corp,DC=panoply,DC=local'
    }
}

Describe 'Get-LDAPBindOutcome' {
    <#
        Active Directory reports every bind refusal as result code 49 (invalidCredentials) and puts the
        actual reason in a hexadecimal sub-code in the diagnostic message. Reading only the result code
        therefore cannot tell "the password is wrong" from "the password is right and must be changed",
        which is precisely the distinction Scenario 017 exists to assert.

        Every string below was captured verbatim from a live Samba AD domain controller
        (ghcr.io/tetronio/jim-samba-ad:primary), not composed by hand: Samba emits the Windows-compatible
        sub-codes, so the same classification serves both.
    #>

    It 'classifies a successful bind from ldapwhoami output' {
        Get-LDAPBindOutcome -ExitCode 0 -BindOutput 'u:PANOPLY\s17probe' |
            Should -Be 'Success'
    }

    It 'classifies data 773 as the password being correct but requiring a change' {
        $output = @'
ldap_bind: Invalid credentials (49)
	additional info: 80090308: LdapErr: DSID-0C0903A9, comment: AcceptSecurityContext error, data 773, v1db1
'@
        Get-LDAPBindOutcome -ExitCode 49 -BindOutput $output | Should -Be 'MustChangePassword'
    }

    It 'classifies data 52e as a genuinely wrong password' {
        $output = @'
ldap_bind: Invalid credentials (49)
	additional info: 80090308: LdapErr: DSID-0C0903A9, comment: AcceptSecurityContext error, data 52e, v1db1
'@
        Get-LDAPBindOutcome -ExitCode 49 -BindOutput $output | Should -Be 'InvalidCredentials'
    }

    It 'distinguishes a disabled account from a wrong password' {
        $output = 'ldap_bind: Invalid credentials (49)
	additional info: 80090308: LdapErr: DSID-0C0903A9, comment: AcceptSecurityContext error, data 533, v1db1'
        Get-LDAPBindOutcome -ExitCode 49 -BindOutput $output | Should -Be 'AccountDisabled'
    }

    It 'distinguishes an expired password from one that must be changed' {
        # 532 is the password ageing out under policy; 773 is an administrator having reset it. A
        # scenario asserting the second must not silently accept the first.
        $output = 'ldap_bind: Invalid credentials (49)
	additional info: 80090308: LdapErr: DSID-0C0903A9, comment: AcceptSecurityContext error, data 532, v1db1'
        Get-LDAPBindOutcome -ExitCode 49 -BindOutput $output | Should -Be 'PasswordExpired'
    }

    It 'classifies a locked-out account' {
        $output = 'ldap_bind: Invalid credentials (49)
	additional info: 80090308: LdapErr: DSID-0C0903A9, comment: AcceptSecurityContext error, data 775, v1db1'
        Get-LDAPBindOutcome -ExitCode 49 -BindOutput $output | Should -Be 'AccountLockedOut'
    }

    It 'classifies a missing account' {
        $output = 'ldap_bind: Invalid credentials (49)
	additional info: 80090308: LdapErr: DSID-0C0903A9, comment: AcceptSecurityContext error, data 525, v1db1'
        Get-LDAPBindOutcome -ExitCode 49 -BindOutput $output | Should -Be 'UserNotFound'
    }

    It 'classifies OpenLDAP''s plain wrong-password message, which carries no AD-style sub-code' {
        # Captured verbatim from a live OpenLDAP container (jim-openldap:primary): OpenLDAP has no
        # equivalent of Active Directory's hexadecimal sub-code, so this is the only signal available to
        # distinguish a wrong password from a directory the client simply could not reach.
        Get-LDAPBindOutcome -ExitCode 49 -BindOutput 'ldap_bind: Invalid credentials (49)' |
            Should -Be 'InvalidCredentials'
    }

    It 'reports an unrecognised failure as Failed rather than guessing' {
        Get-LDAPBindOutcome -ExitCode 1 -BindOutput 'ldap_sasl_bind(SIMPLE): Cannot contact LDAP server (-1)' |
            Should -Be 'Failed'
    }

    It 'does not report success on a non-zero exit code with empty output' {
        Get-LDAPBindOutcome -ExitCode 49 -BindOutput '' | Should -Be 'Failed'
    }
}

Describe 'Get-LDAPPasswordModifyOutcome' {
    <#
        ldappasswd reports an RFC 3062 refusal as "Result: <text> (<code>)" followed by an
        "Additional info:" line carrying the server's own message. The number is what is classified on,
        because the text differs between servers. Scenario 022's negative control depends on telling a
        constraint violation (the password policy refused the value) from an access or bind failure
        (the fixture is wrong), both of which are a non-zero exit code.

        The strings below follow ldappasswd's output format; unlike the bind strings above they were
        composed from it rather than captured from a live server, which is why the classifier keys on
        the parenthesised result code alone.
    #>

    It 'classifies a zero exit code as success whatever the output' {
        Get-LDAPPasswordModifyOutcome -ExitCode 0 -Output '' | Should -Be 'Success'
    }

    It 'classifies result 19 as the password policy refusing the value' {
        $output = "Result: Constraint violation (19)`nAdditional info: Password fails quality checking policy"
        Get-LDAPPasswordModifyOutcome -ExitCode 1 -Output $output | Should -Be 'ConstraintViolation'
    }

    It 'classifies result 50 as an access problem rather than an enforced policy' {
        Get-LDAPPasswordModifyOutcome -ExitCode 1 -Output 'Result: Insufficient access (50)' |
            Should -Be 'InsufficientAccess'
    }

    It 'classifies a refused bind as invalid credentials' {
        Get-LDAPPasswordModifyOutcome -ExitCode 49 -Output 'ldap_bind: Invalid credentials (49)' |
            Should -Be 'InvalidCredentials'
    }

    It 'classifies result 53 as the directory declining the operation' {
        Get-LDAPPasswordModifyOutcome -ExitCode 1 -Output 'Result: Server is unwilling to perform (53)' |
            Should -Be 'UnwillingToPerform'
    }

    It 'reports an unrecognised failure as Failed rather than guessing' {
        Get-LDAPPasswordModifyOutcome -ExitCode 1 -Output 'ldap_sasl_bind(SIMPLE): Cannot contact LDAP server (-1)' |
            Should -Be 'Failed'
    }

    It 'does not report success on a non-zero exit code with empty output' {
        Get-LDAPPasswordModifyOutcome -ExitCode 1 -Output '' | Should -Be 'Failed'
    }
}

Describe 'Get-LDAPPasswordModifyOutcome (Active Directory unicodePwd)' {
    <#
        Active Directory has no RFC 3062 Password Modify. A password write is an ldapmodify of
        unicodePwd, and AD reports why it refused in a hexadecimal error code at the head of the
        "additional info" line, which the LDAP result code alone does not distinguish: a policy refusal
        and a wrong old password are both a constraint violation (19), and a policy refusal can also
        arrive as unwilling to perform (53).

        Unlike the bind strings above, these were composed from the documented shapes of AD's messages
        (0x52D is ERROR_PASSWORD_RESTRICTION, 0x56 is ERROR_INVALID_PASSWORD), not captured from a live
        domain controller. The first Active Directory run should confirm them.
    #>

    It 'classifies 0000052D under a constraint violation as the password policy refusing the value' {
        $output = "ldap_modify: Constraint violation (19)`n`tadditional info: 0000052D: AtrErr: DSID-03191083, #1:`n`t`t0: 0000052D: DSID-03191083, problem 1005 (CONSTRAINT_VIOLATION), data 0, Att 9005a (unicodePwd)"
        Get-LDAPPasswordModifyOutcome -ExitCode 19 -Output $output | Should -Be 'ConstraintViolation'
    }

    It 'classifies 0000052D under unwilling to perform as the password policy too, not as a declined operation' {
        $output = "ldap_modify: Server is unwilling to perform (53)`n`tadditional info: 0000052D: SvcErr: DSID-031A12D2, problem 5003 (WILL_NOT_PERFORM), data 0"
        Get-LDAPPasswordModifyOutcome -ExitCode 53 -Output $output | Should -Be 'ConstraintViolation'
    }

    It 'classifies 00000056 as the old password being wrong, not as a policy refusal' {
        # Result code 19 alone would read as a policy refusal; the hexadecimal code is what tells the
        # two constraint violations apart, and a scenario asserting "policy enforced" must not pass on
        # a wrong old password.
        $output = "ldap_modify: Constraint violation (19)`n`tadditional info: 00000056: AtrErr: DSID-03190F80, #1:`n`t`t0: 00000056: DSID-03190F80, problem 1005 (CONSTRAINT_VIOLATION), data 0, Att 9005a (unicodePwd)"
        Get-LDAPPasswordModifyOutcome -ExitCode 19 -Output $output | Should -Be 'InvalidOldPassword'
    }

    It 'reads the hexadecimal code whatever its case' {
        Get-LDAPPasswordModifyOutcome -ExitCode 19 -Output "ldap_modify: Constraint violation (19)`n`tadditional info: 0000052d: AtrErr" |
            Should -Be 'ConstraintViolation'
    }

    It 'reads the hexadecimal code from ldappasswd''s capitalised "Additional info" line' {
        Get-LDAPPasswordModifyOutcome -ExitCode 1 -Output "Result: Constraint violation (19)`nAdditional info: 00000056: AtrErr" |
            Should -Be 'InvalidOldPassword'
    }

    It 'does not let the hexadecimal codes turn a success into anything else' {
        Get-LDAPPasswordModifyOutcome -ExitCode 0 -Output 'additional info: 0000052D: leftover' | Should -Be 'Success'
    }

    It 'still classifies an unwilling-to-perform with a different additional code as UnwillingToPerform' {
        # 00002077 is ERROR_DS_UNWILLING_TO_PERFORM style text; anything that is not one of the two
        # password codes falls back to the result code.
        Get-LDAPPasswordModifyOutcome -ExitCode 53 -Output "ldap_modify: Server is unwilling to perform (53)`n`tadditional info: 00002077: SvcErr: DSID-031A0F1E, problem 5003 (WILL_NOT_PERFORM), data 0" |
            Should -Be 'UnwillingToPerform'
    }

    It 'ignores a hexadecimal code that is not at the head of an additional info line' {
        Get-LDAPPasswordModifyOutcome -ExitCode 50 -Output "ldap_modify: Insufficient access (50)`n`tadditional info: 00000005: SecErr: DSID-031521D0, problem 4003 (INSUFF_ACCESS_RIGHTS), data 0, 0000052D" |
            Should -Be 'InsufficientAccess'
    }
}

Describe 'Get-LdapToolInvocation' {
    <#
        The one place that decides where an LDAP client tool runs. A config with a container keeps the
        long-standing behaviour (the tool runs inside the directory's own container); a config with no
        container, which is what a real Active Directory domain controller has, runs it in the shared
        jim-ldap-toolbox container instead. The function is pure: it returns the argument list for
        docker, so it is testable with no Docker present.
    #>

    It 'builds a docker exec against the config''s container when it has one' {
        $argv = Get-LdapToolInvocation -DirectoryConfig @{ ContainerName = 'samba-ad-primary' } `
            -Tool ldapsearch -Arguments @('-x', '-LLL', '-H', 'ldap://localhost:389')

        $argv | Should -Be @('exec', 'samba-ad-primary', 'ldapsearch', '-x', '-LLL', '-H', 'ldap://localhost:389')
    }

    It 'targets the toolbox when the config''s ContainerName is null' {
        $argv = Get-LdapToolInvocation -DirectoryConfig @{ ContainerName = $null } `
            -Tool ldapwhoami -Arguments @('-x', '-H', 'ldaps://dc1.panoply.local:636')

        $argv | Should -Be @('exec', 'jim-ldap-toolbox', 'ldapwhoami', '-x', '-H', 'ldaps://dc1.panoply.local:636')
    }

    It 'targets the toolbox when the config''s ContainerName is empty' {
        (Get-LdapToolInvocation -DirectoryConfig @{ ContainerName = '' } -Tool ldapsearch -Arguments @('-x'))[1] |
            Should -Be 'jim-ldap-toolbox'
    }

    It 'targets the toolbox when the config has no ContainerName key at all' {
        (Get-LdapToolInvocation -DirectoryConfig @{ Host = 'dc1.panoply.local' } -Tool ldapsearch -Arguments @('-x'))[1] |
            Should -Be 'jim-ldap-toolbox'
    }

    It 'adds -i, and only then, when the tool is to read standard input' {
        $withInput = Get-LdapToolInvocation -DirectoryConfig @{ ContainerName = 'samba-ad-primary' } `
            -Tool ldapmodify -Arguments @('-x') -WithInput
        $withoutInput = Get-LdapToolInvocation -DirectoryConfig @{ ContainerName = 'samba-ad-primary' } `
            -Tool ldapmodify -Arguments @('-x')

        $withInput | Should -Be @('exec', '-i', 'samba-ad-primary', 'ldapmodify', '-x')
        $withoutInput | Should -Be @('exec', 'samba-ad-primary', 'ldapmodify', '-x')
    }

    It 'adds -i ahead of the toolbox name too' {
        Get-LdapToolInvocation -DirectoryConfig @{ ContainerName = $null } -Tool ldapadd -Arguments @('-x') -WithInput |
            Should -Be @('exec', '-i', 'jim-ldap-toolbox', 'ldapadd', '-x')
    }

    It 'passes every argument through as its own element, unglobbed and unsplit' {
        # Elements reach docker exec directly, with no shell in between, so a '*' or a space is safe.
        $argv = Get-LdapToolInvocation -DirectoryConfig @{ ContainerName = 'c' } -Tool ldapsearch `
            -Arguments @('-D', 'CN=Test User,OU=Users,DC=x', '-w', 'Test@123!', '(cn=*)')

        $argv[-4..-1] | Should -Be @('CN=Test User,OU=Users,DC=x', '-w', 'Test@123!', '(cn=*)')
        $argv.Count | Should -Be 8
    }

    It 'keeps an empty argument, such as the root DSE''s empty search base' {
        $argv = Get-LdapToolInvocation -DirectoryConfig @{ ContainerName = 'c' } -Tool ldapsearch -Arguments @('-s', 'base', '-b', '', 'currentTime')

        $argv | Should -Be @('exec', 'c', 'ldapsearch', '-s', 'base', '-b', '', 'currentTime')
    }

    It 'accepts <Tool>' -ForEach @(
        @{ Tool = 'ldapsearch' }, @{ Tool = 'ldapwhoami' }, @{ Tool = 'ldapmodify' },
        @{ Tool = 'ldapadd' }, @{ Tool = 'ldapdelete' }, @{ Tool = 'ldappasswd' }
    ) {
        (Get-LdapToolInvocation -DirectoryConfig @{ ContainerName = 'c' } -Tool $Tool -Arguments @('-x'))[2] | Should -Be $Tool
    }

    It 'refuses a tool that is not an LDAP client' {
        { Get-LdapToolInvocation -DirectoryConfig @{ ContainerName = 'c' } -Tool 'bash' -Arguments @('-c', 'id') } | Should -Throw
    }

    It 'returns an array even for a tool with no arguments' {
        $argv = Get-LdapToolInvocation -DirectoryConfig @{ ContainerName = 'c' } -Tool ldapwhoami -Arguments @()
        , $argv | Should -BeOfType [object[]]
        $argv | Should -Be @('exec', 'c', 'ldapwhoami')
    }
}

Describe 'Get-LdapUri' {
    It 'builds the URI from the config''s search scheme, host and search port' {
        Get-LdapUri -DirectoryConfig @{ LdapSearchScheme = 'ldaps'; Host = 'dc1.panoply.local'; LdapSearchPort = 636 } |
            Should -Be 'ldaps://dc1.panoply.local:636'
    }

    It 'uses LdapSearchPort, not the connector Port' {
        Get-LdapUri -DirectoryConfig @{ LdapSearchScheme = 'ldap'; Host = 'h'; LdapSearchPort = 3389; Port = 3636 } |
            Should -Be 'ldap://h:3389'
    }
}

Describe 'Test-LdapToolboxConfig' {
    It 'is true for a config with no container' {
        Test-LdapToolboxConfig -DirectoryConfig @{ ContainerName = $null } | Should -BeTrue
    }

    It 'is false for a config with a container' {
        Test-LdapToolboxConfig -DirectoryConfig @{ ContainerName = 'samba-ad-primary' } | Should -BeFalse
    }
}

Describe 'Invoke-LdapTool' {
    BeforeAll {
        # A stand-in for the docker executable. A plain function rather than a Pester mock, because a
        # mock cannot see what is piped to it and the tool's standard input is part of the contract.
        function docker {
            begin { $script:stubStdin = [System.Collections.Generic.List[string]]::new() }
            process { if ($null -ne $_) { $script:stubStdin.Add([string]$_) } }
            end {
                $script:stubArgs = @($args)
                $global:LASTEXITCODE = $script:stubExitCode
                $script:stubOutput
            }
        }
    }

    BeforeEach {
        $script:stubArgs = $null
        $script:stubExitCode = 0
        $script:stubOutput = @('line one', 'line two')
    }

    It 'runs the tool through docker with the argument list Get-LdapToolInvocation builds' {
        Invoke-LdapTool -DirectoryConfig @{ ContainerName = 'samba-ad-primary' } -Tool ldapsearch -Arguments @('-x', '-b', 'DC=x') | Out-Null

        $script:stubArgs | Should -Be @('exec', 'samba-ad-primary', 'ldapsearch', '-x', '-b', 'DC=x')
    }

    It 'runs against the toolbox for a container-less config' {
        Invoke-LdapTool -DirectoryConfig @{ ContainerName = $null } -Tool ldapwhoami -Arguments @('-x') | Out-Null

        $script:stubArgs | Should -Be @('exec', 'jim-ldap-toolbox', 'ldapwhoami', '-x')
    }

    It 'returns the tool''s output lines by default' {
        $output = Invoke-LdapTool -DirectoryConfig @{ ContainerName = 'c' } -Tool ldapsearch -Arguments @('-x')

        $output | Should -Be @('line one', 'line two')
    }

    It 'returns the exit code and output together with -PassThruExitCode' {
        $script:stubExitCode = 49
        $script:stubOutput = 'ldap_bind: Invalid credentials (49)'

        $result = Invoke-LdapTool -DirectoryConfig @{ ContainerName = 'c' } -Tool ldapwhoami -Arguments @('-x') -PassThruExitCode

        $result.ExitCode | Should -Be 49
        $result.Output | Should -Be 'ldap_bind: Invalid credentials (49)'
    }

    It 'hands -InputObject to the tool on standard input and adds -i' {
        Invoke-LdapTool -DirectoryConfig @{ ContainerName = 'c' } -Tool ldapmodify -Arguments @('-x') -InputObject "dn: x`nchangetype: modify" | Out-Null

        $script:stubArgs | Should -Be @('exec', '-i', 'c', 'ldapmodify', '-x')
        ($script:stubStdin -join "`n") | Should -Be "dn: x`nchangetype: modify"
    }

    It 'does not add -i, or write to standard input, when there is no input' {
        Invoke-LdapTool -DirectoryConfig @{ ContainerName = 'c' } -Tool ldapmodify -Arguments @('-x') | Out-Null

        $script:stubArgs | Should -Not -Contain '-i'
        $script:stubStdin.Count | Should -Be 0
    }
}

Describe 'Invoke-LDAPSearch' {
    BeforeEach {
        Mock Invoke-LdapTool { @{ ExitCode = 0; Output = @('dn: CN=x,DC=y') } }
    }

    It 'keeps the URI and flags a container config has always produced' {
        Invoke-LDAPSearch -ContainerName 'samba-ad-primary' -Server 'localhost' -Port 389 -Scheme 'ldap' `
            -BaseDN 'DC=panoply,DC=local' -BindDN 'CN=Administrator,CN=Users,DC=panoply,DC=local' `
            -BindPassword 'Test@123!' -Filter '(cn=x)' -Attributes @('cn', 'mail') | Out-Null

        Should -Invoke Invoke-LdapTool -Times 1 -Exactly -ParameterFilter {
            $Tool -eq 'ldapsearch' -and $DirectoryConfig.ContainerName -eq 'samba-ad-primary' -and
            (($Arguments -join '|') -eq '-x|-LLL|-H|ldap://localhost:389|-D|CN=Administrator,CN=Users,DC=panoply,DC=local|-w|Test@123!|-b|DC=panoply,DC=local|(cn=x)|cn|mail')
        }
    }

    It 'does not pass * as an attribute name' {
        Invoke-LDAPSearch -ContainerName 'c' -Server 'localhost' -BaseDN 'DC=x' -BindDN 'cn=a' -BindPassword 'p' -Filter '(cn=x)' | Out-Null

        Should -Invoke Invoke-LdapTool -Times 1 -Exactly -ParameterFilter { $Arguments -notcontains '*' }
    }

    It 'searches over LDAPS through the toolbox for a container-less config' {
        $config = @{ ContainerName = $null; Host = 'dc1.panoply.local'; LdapSearchPort = 636; LdapSearchScheme = 'ldaps' }

        Invoke-LDAPSearch -DirectoryConfig $config -BaseDN 'DC=panoply,DC=local' -BindDN 'CN=Administrator,CN=Users,DC=panoply,DC=local' `
            -BindPassword 'pw' -Filter '(cn=x)' -Attributes @('dn') | Out-Null

        Should -Invoke Invoke-LdapTool -Times 1 -Exactly -ParameterFilter {
            $Tool -eq 'ldapsearch' -and $null -eq $DirectoryConfig.ContainerName -and
            $Arguments -contains 'ldaps://dc1.panoply.local:636' -and $Arguments -notcontains 'ldap://localhost:636'
        }
    }

    It 'returns $null when the tool fails' {
        Mock Invoke-LdapTool { @{ ExitCode = 32; Output = @('No such object (32)') } }

        Invoke-LDAPSearch -ContainerName 'c' -Server 'localhost' -BaseDN 'DC=x' -BindDN 'cn=a' -BindPassword 'p' -Filter '(cn=x)' |
            Should -BeNullOrEmpty
    }
}

Describe 'callers of Invoke-LDAPSearch with a container-less config' {
    BeforeEach {
        Mock Invoke-LDAPSearch { @('dn: CN=x,OU=Users,DC=panoply,DC=local', 'sAMAccountName: x') }
        $script:adConfig = @{
            ContainerName = $null; Host = 'dc1.panoply.local'; LdapSearchPort = 636; LdapSearchScheme = 'ldaps'
            BaseDN = 'DC=panoply,DC=local'; GroupContainer = 'OU=Groups,OU=Corp,DC=panoply,DC=local'
            BindDN = 'CN=Administrator,CN=Users,DC=panoply,DC=local'; BindPassword = 'pw'
            UserNameAttr = 'sAMAccountName'; UserObjectClass = 'user'; GroupObjectClass = 'group'
        }
    }

    It 'Get-LDAPUser hands the config through rather than defaulting to the Samba container' {
        Get-LDAPUser -UserIdentifier 'x' -DirectoryConfig $script:adConfig | Out-Null

        Should -Invoke Invoke-LDAPSearch -Times 1 -Exactly -ParameterFilter { $DirectoryConfig.Host -eq 'dc1.panoply.local' }
    }

    It 'Get-LDAPUserCount hands the config through' {
        Get-LDAPUserCount -DirectoryConfig $script:adConfig | Out-Null

        Should -Invoke Invoke-LDAPSearch -Times 1 -Exactly -ParameterFilter { $DirectoryConfig.Host -eq 'dc1.panoply.local' }
    }

    It 'Get-LDAPGroup hands the config through' {
        Get-LDAPGroup -GroupName 'g' -DirectoryConfig $script:adConfig | Out-Null

        Should -Invoke Invoke-LDAPSearch -Times 1 -Exactly -ParameterFilter { $DirectoryConfig.Host -eq 'dc1.panoply.local' }
    }

    It 'Get-LDAPGroupCount hands the config through' {
        Get-LDAPGroupCount -DirectoryConfig $script:adConfig | Out-Null

        Should -Invoke Invoke-LDAPSearch -Times 1 -Exactly -ParameterFilter { $DirectoryConfig.Host -eq 'dc1.panoply.local' }
    }

    It 'Get-LDAPGroupList hands the config through' {
        Get-LDAPGroupList -DirectoryConfig $script:adConfig | Out-Null

        Should -Invoke Invoke-LDAPSearch -Times 1 -Exactly -ParameterFilter { $DirectoryConfig.Host -eq 'dc1.panoply.local' }
    }
}

Describe 'ConvertTo-UnicodePwdValue' {
    It 'is the base64 of the UTF-16LE bytes of the double-quoted password' {
        # Built from first principles: a double quote, each UTF-16 code unit low byte first, a double quote.
        $quoted = '"Test@123!"'
        $bytes = foreach ($c in $quoted.ToCharArray()) { [byte]([int]$c -band 0xFF); [byte]([int]$c -shr 8) }
        $expected = [System.Convert]::ToBase64String([byte[]]$bytes)

        ConvertTo-UnicodePwdValue -Password 'Test@123!' | Should -Be $expected
    }

    It 'matches a value computed independently of PowerShell' {
        # Computed with Python's str.encode('utf-16-le') and base64, not with the code under test.
        ConvertTo-UnicodePwdValue -Password 'Test@123!' | Should -Be 'IgBUAGUAcwB0AEAAMQAyADMAIQAiAA=='
    }

    It 'decodes to a quoted, two-bytes-per-character password' {
        $decoded = [System.Convert]::FromBase64String((ConvertTo-UnicodePwdValue -Password 'Test@123!'))

        $decoded.Length | Should -Be (('Test@123!'.Length + 2) * 2)
        # 0x22 0x00 is the opening quote; the closing quote is the last two bytes.
        $decoded[0..3] | Should -Be @(0x22, 0x00, 0x54, 0x00)
        $decoded[-4..-1] | Should -Be @(0x21, 0x00, 0x22, 0x00)
    }

    It 'encodes a character outside ASCII as its UTF-16 code unit, low byte first' {
        $decoded = [System.Convert]::FromBase64String((ConvertTo-UnicodePwdValue -Password ([string][char]0xE9)))

        $decoded | Should -Be @(0x22, 0x00, 0xE9, 0x00, 0x22, 0x00)
    }

    It 'produces base64 that is safe for an LDIF value with a double colon' {
        (ConvertTo-UnicodePwdValue -Password 'Test@123!') | Should -Match '^[A-Za-z0-9+/]+={0,2}$'
    }
}

Describe 'ConvertFrom-LdapGeneralizedTime' {
    It 'parses the form Active Directory reports in currentTime' {
        $time = ConvertFrom-LdapGeneralizedTime -Value '20260928101530.0Z'

        $time | Should -Be ([datetime]::new(2026, 9, 28, 10, 15, 30, [System.DateTimeKind]::Utc))
        $time.Kind | Should -Be ([System.DateTimeKind]::Utc)
    }

    It 'parses a value with no fractional part' {
        ConvertFrom-LdapGeneralizedTime -Value '20260928101530Z' |
            Should -Be ([datetime]::new(2026, 9, 28, 10, 15, 30, [System.DateTimeKind]::Utc))
    }

    It 'converts a value with a UTC offset to UTC' {
        ConvertFrom-LdapGeneralizedTime -Value '20260928111530.0+0100' |
            Should -Be ([datetime]::new(2026, 9, 28, 10, 15, 30, [System.DateTimeKind]::Utc))
    }

    It 'throws on a value that is not a GeneralizedTime' -ForEach @(
        @{ Value = 'yesterday' }, @{ Value = '2026-09-28T10:15:30Z' }, @{ Value = '20260928' }, @{ Value = '20261328101530.0Z' }
    ) {
        { ConvertFrom-LdapGeneralizedTime -Value $Value } | Should -Throw '*GeneralizedTime*'
    }
}

Describe 'Test-LdapClockSkew' {
    BeforeAll {
        $script:reference = [datetime]::new(2026, 9, 28, 10, 0, 0, [System.DateTimeKind]::Utc)
    }

    It 'is within tolerance when the directory clock is a few seconds ahead' {
        $result = Test-LdapClockSkew -DirectoryTime $script:reference.AddSeconds(3) -ReferenceTime $script:reference

        $result.WithinTolerance | Should -BeTrue
        $result.SkewSeconds | Should -Be 3
    }

    It 'reports a negative skew when the directory clock is behind' {
        $result = Test-LdapClockSkew -DirectoryTime $script:reference.AddSeconds(-6) -ReferenceTime $script:reference

        $result.WithinTolerance | Should -BeFalse
        $result.SkewSeconds | Should -Be -6
    }

    It 'treats exactly the tolerance as within it' {
        (Test-LdapClockSkew -DirectoryTime $script:reference.AddSeconds(5) -ReferenceTime $script:reference).WithinTolerance | Should -BeTrue
        (Test-LdapClockSkew -DirectoryTime $script:reference.AddSeconds(-5) -ReferenceTime $script:reference).WithinTolerance | Should -BeTrue
    }

    It 'fails just past the tolerance' {
        (Test-LdapClockSkew -DirectoryTime $script:reference.AddSeconds(5.5) -ReferenceTime $script:reference).WithinTolerance | Should -BeFalse
    }

    It 'honours a custom tolerance' {
        $result = Test-LdapClockSkew -DirectoryTime $script:reference.AddSeconds(30) -ReferenceTime $script:reference -ToleranceSeconds 60

        $result.WithinTolerance | Should -BeTrue
        $result.ToleranceSeconds | Should -Be 60
    }

    It 'compares against the runner''s UTC clock by default' {
        (Test-LdapClockSkew -DirectoryTime ([datetime]::UtcNow)).WithinTolerance | Should -BeTrue
        (Test-LdapClockSkew -DirectoryTime ([datetime]::UtcNow.AddMinutes(10))).WithinTolerance | Should -BeFalse
    }
}

Describe 'Test-LDAPBind routing' {
    BeforeEach {
        Mock Invoke-LdapTool { @{ ExitCode = 0; Output = 'u:PANOPLY\svc-jim' } }
    }

    It 'binds inside the named container over the in-container URI, as it always has' {
        $result = Test-LDAPBind -BindDN 'CN=a,DC=x' -BindPassword 'pw' -ContainerName 'samba-ad-primary' -Port 389 -Scheme 'ldap'

        $result.Outcome | Should -Be 'Success'
        Should -Invoke Invoke-LdapTool -Times 1 -Exactly -ParameterFilter {
            $Tool -eq 'ldapwhoami' -and $DirectoryConfig.ContainerName -eq 'samba-ad-primary' -and
            (($Arguments -join '|') -eq '-x|-H|ldap://localhost:389|-D|CN=a,DC=x|-w|pw')
        }
    }

    It 'defaults to the Samba primary container when given neither a container nor a config' {
        Test-LDAPBind -BindDN 'CN=a,DC=x' -BindPassword 'pw' | Out-Null

        Should -Invoke Invoke-LdapTool -Times 1 -Exactly -ParameterFilter { $DirectoryConfig.ContainerName -eq 'samba-ad-primary' }
    }

    It 'binds over LDAPS through the toolbox for a container-less config' {
        $config = @{ ContainerName = $null; Host = 'dc1.panoply.local'; LdapSearchPort = 636; LdapSearchScheme = 'ldaps' }

        Test-LDAPBind -BindDN 'CN=a,DC=x' -BindPassword 'pw' -DirectoryConfig $config | Out-Null

        Should -Invoke Invoke-LdapTool -Times 1 -Exactly -ParameterFilter {
            $null -eq $DirectoryConfig.ContainerName -and $Arguments -contains 'ldaps://dc1.panoply.local:636'
        }
    }

    It 'classifies a refused bind from the exit code and output' {
        Mock Invoke-LdapTool { @{ ExitCode = 49; Output = "ldap_bind: Invalid credentials (49)`n`tadditional info: 80090308: LdapErr: DSID-0C0903A9, comment: AcceptSecurityContext error, data 773, v1db1" } }

        (Test-LDAPBind -BindDN 'CN=a,DC=x' -BindPassword 'pw' -ContainerName 'c').Outcome | Should -Be 'MustChangePassword'
    }
}

Describe 'Set-LDAPUserPasswordWithPasswordModify' {
    BeforeEach {
        Mock Invoke-LdapTool { @{ ExitCode = 0; Output = '' } }
        $script:sambaConfig = @{ ContainerName = 'openldap-primary'; LdapSearchScheme = 'ldap'; LdapSearchPort = 1389 }
        $script:adConfig = @{ DirectoryType = 'ActiveDirectory'; ContainerName = $null; Host = 'dc1.panoply.local'; LdapSearchScheme = 'ldaps'; LdapSearchPort = 636 }
    }

    It 'keeps running ldappasswd with the same arguments for a container config' {
        Set-LDAPUserPasswordWithPasswordModify -BindDN 'uid=a,dc=x' -BindPassword 'old' -NewPassword 'new' `
            -TargetDN 'uid=b,dc=x' -DirectoryConfig $script:sambaConfig | Out-Null

        Should -Invoke Invoke-LdapTool -Times 1 -Exactly -ParameterFilter {
            $Tool -eq 'ldappasswd' -and $DirectoryConfig.ContainerName -eq 'openldap-primary' -and
            (($Arguments -join '|') -eq '-x|-H|ldap://localhost:1389|-D|uid=a,dc=x|-w|old|-s|new|uid=b,dc=x')
        }
    }

    It 'omits the target DN for a self change on a container config' {
        Set-LDAPUserPasswordWithPasswordModify -BindDN 'uid=a,dc=x' -BindPassword 'old' -NewPassword 'new' -DirectoryConfig $script:sambaConfig | Out-Null

        Should -Invoke Invoke-LdapTool -Times 1 -Exactly -ParameterFilter {
            (($Arguments -join '|') -eq '-x|-H|ldap://localhost:1389|-D|uid=a,dc=x|-w|old|-s|new')
        }
    }

    It 'returns the classified outcome, exit code and output' {
        Mock Invoke-LdapTool { @{ ExitCode = 19; Output = 'Result: Constraint violation (19)' } }

        $result = Set-LDAPUserPasswordWithPasswordModify -BindDN 'uid=a,dc=x' -BindPassword 'old' -NewPassword 'new' -DirectoryConfig $script:sambaConfig

        $result.Outcome | Should -Be 'ConstraintViolation'
        $result.ExitCode | Should -Be 19
    }

    Context 'Active Directory (unicodePwd over LDAPS)' {
        It 'replaces unicodePwd when the bound account resets another account''s password' {
            Set-LDAPUserPasswordWithPasswordModify -BindDN 'CN=Administrator,CN=Users,DC=panoply,DC=local' -BindPassword 'admin-pw' `
                -NewPassword 'New@Pass1!' -TargetDN 'CN=Bob,OU=Users,OU=Corp,DC=panoply,DC=local' -DirectoryConfig $script:adConfig | Out-Null

            $expected = ConvertTo-UnicodePwdValue -Password 'New@Pass1!'
            Should -Invoke Invoke-LdapTool -Times 1 -Exactly -ParameterFilter {
                $Tool -eq 'ldapmodify' -and $null -eq $DirectoryConfig.ContainerName -and
                $Arguments -contains 'ldaps://dc1.panoply.local:636' -and
                $InputObject -match "(?m)^dn: CN=Bob,OU=Users,OU=Corp,DC=panoply,DC=local$" -and
                $InputObject -match '(?m)^changetype: modify$' -and
                $InputObject -match '(?m)^replace: unicodePwd$' -and
                $InputObject -notmatch 'delete: unicodePwd' -and
                $InputObject.Contains("unicodePwd:: $expected")
            }
        }

        It 'deletes the old value and adds the new one in a single modify for a self change' {
            $dn = 'CN=Bob,OU=Users,OU=Corp,DC=panoply,DC=local'
            Set-LDAPUserPasswordWithPasswordModify -BindDN $dn -BindPassword 'Old@Pass1!' -NewPassword 'New@Pass1!' -DirectoryConfig $script:adConfig | Out-Null

            $old = ConvertTo-UnicodePwdValue -Password 'Old@Pass1!'
            $new = ConvertTo-UnicodePwdValue -Password 'New@Pass1!'
            $expectedLdif = "dn: $dn`nchangetype: modify`ndelete: unicodePwd`nunicodePwd:: $old`n-`nadd: unicodePwd`nunicodePwd:: $new`n-`n"
            Should -Invoke Invoke-LdapTool -Times 1 -Exactly -ParameterFilter { $Tool -eq 'ldapmodify' -and $InputObject -eq $expectedLdif }
        }

        It 'treats a TargetDN equal to the BindDN, whatever its case, as a self change' {
            $dn = 'CN=Bob,OU=Users,OU=Corp,DC=panoply,DC=local'
            Set-LDAPUserPasswordWithPasswordModify -BindDN $dn -BindPassword 'Old@Pass1!' -NewPassword 'New@Pass1!' `
                -TargetDN $dn.ToUpperInvariant() -DirectoryConfig $script:adConfig | Out-Null

            Should -Invoke Invoke-LdapTool -Times 1 -Exactly -ParameterFilter { $InputObject -match '(?m)^delete: unicodePwd$' }
        }

        It 'binds with the given credentials, not the password being set' {
            Set-LDAPUserPasswordWithPasswordModify -BindDN 'CN=Administrator,CN=Users,DC=panoply,DC=local' -BindPassword 'admin-pw' `
                -NewPassword 'New@Pass1!' -TargetDN 'CN=Bob,DC=panoply,DC=local' -DirectoryConfig $script:adConfig | Out-Null

            Should -Invoke Invoke-LdapTool -Times 1 -Exactly -ParameterFilter {
                $Arguments -contains '-D' -and $Arguments -contains 'CN=Administrator,CN=Users,DC=panoply,DC=local' -and $Arguments -contains 'admin-pw'
            }
        }

        It 'never puts a clear-text password in the LDIF' {
            Set-LDAPUserPasswordWithPasswordModify -BindDN 'CN=Administrator,CN=Users,DC=panoply,DC=local' -BindPassword 'admin-pw' `
                -NewPassword 'Clear@Text1!' -TargetDN 'CN=Bob,DC=panoply,DC=local' -DirectoryConfig $script:adConfig | Out-Null

            Should -Invoke Invoke-LdapTool -Times 1 -Exactly -ParameterFilter { -not $InputObject.Contains('Clear@Text1!') }
        }

        It 'refuses a self change when the BindDN is not a DN and no TargetDN names the entry' {
            { Set-LDAPUserPasswordWithPasswordModify -BindDN 'PANOPLY\bob' -BindPassword 'old' -NewPassword 'new' -DirectoryConfig $script:adConfig } |
                Should -Throw '*TargetDN*'
        }

        It 'classifies a policy refusal as ConstraintViolation and a wrong old password as InvalidOldPassword' {
            Mock Invoke-LdapTool { @{ ExitCode = 19; Output = "ldap_modify: Constraint violation (19)`n`tadditional info: 0000052D: AtrErr" } }
            $policy = Set-LDAPUserPasswordWithPasswordModify -BindDN 'CN=A,DC=x' -BindPassword 'o' -NewPassword 'n' -DirectoryConfig $script:adConfig
            Mock Invoke-LdapTool { @{ ExitCode = 19; Output = "ldap_modify: Constraint violation (19)`n`tadditional info: 00000056: AtrErr" } }
            $wrongOld = Set-LDAPUserPasswordWithPasswordModify -BindDN 'CN=A,DC=x' -BindPassword 'o' -NewPassword 'n' -DirectoryConfig $script:adConfig

            $policy.Outcome | Should -Be 'ConstraintViolation'
            $wrongOld.Outcome | Should -Be 'InvalidOldPassword'
        }
    }
}

Describe 'Set-LDAPUserPasswordAsAccountHolder' {
    Context 'Samba AD (smbpasswd, unchanged)' {
        BeforeAll {
            function docker {
                begin { $script:stubStdin = [System.Collections.Generic.List[string]]::new() }
                process { if ($null -ne $_) { $script:stubStdin.Add([string]$_) } }
                end {
                    $script:stubArgs = @($args)
                    $global:LASTEXITCODE = 0
                    'Password changed for user probe'
                }
            }
        }

        It 'runs smbpasswd remote mode inside the container with the passwords on standard input' {
            $config = @{ ContainerName = 'samba-ad-primary'; Domain = 'panoply.local'; DirectoryType = 'SambaAD' }

            $result = Set-LDAPUserPasswordAsAccountHolder -AccountName 'probe' -CurrentPassword 'Old@123!' -NewPassword 'New@123!' -DirectoryConfig $config

            $script:stubArgs | Should -Be @('exec', '-i', 'samba-ad-primary', 'smbpasswd', '-r', 'dc1.panoply.local', '-U', 'probe', '-s')
            ($script:stubStdin -join "`n") | Should -Be "Old@123!`nNew@123!`nNew@123!`n"
            $result.Success | Should -BeTrue
            $result.ExitCode | Should -Be 0
            $result.Outcome | Should -Be 'Success'
        }
    }

    Context 'Active Directory (unicodePwd over LDAPS)' {
        BeforeEach {
            Mock Invoke-LdapTool { @{ ExitCode = 0; Output = 'modifying entry' } }
            $script:adConfig = @{
                ContainerName = $null; Host = 'dc1.panoply.local'; LdapSearchScheme = 'ldaps'; LdapSearchPort = 636
                Domain = 'panoply.local'; ShortDomain = 'PANOPLY'; DirectoryType = 'ActiveDirectory'
                BaseDN = 'DC=panoply,DC=local'; UserNameAttr = 'sAMAccountName'
                BindDN = 'CN=Administrator,CN=Users,DC=panoply,DC=local'; BindPassword = 'admin-pw'
            }
        }

        It 'binds as the account holder and changes unicodePwd with delete of the old value and add of the new' {
            $dn = 'CN=Probe,OU=Users,OU=Corp,DC=panoply,DC=local'
            $result = Set-LDAPUserPasswordAsAccountHolder -AccountName 'probe' -CurrentPassword 'Old@Pass1!' -NewPassword 'New@Pass1!' `
                -AccountDN $dn -DirectoryConfig $script:adConfig

            $old = ConvertTo-UnicodePwdValue -Password 'Old@Pass1!'
            $new = ConvertTo-UnicodePwdValue -Password 'New@Pass1!'
            $expectedLdif = "dn: $dn`nchangetype: modify`ndelete: unicodePwd`nunicodePwd:: $old`n-`nadd: unicodePwd`nunicodePwd:: $new`n-`n"
            Should -Invoke Invoke-LdapTool -Times 1 -Exactly -ParameterFilter {
                $Tool -eq 'ldapmodify' -and $Arguments -contains 'ldaps://dc1.panoply.local:636' -and
                $Arguments -contains 'PANOPLY\probe' -and $Arguments -contains 'Old@Pass1!' -and $InputObject -eq $expectedLdif
            }
            $result.Success | Should -BeTrue
            $result.Outcome | Should -Be 'Success'
        }

        It 'looks the account''s DN up as the administrator when none is given' {
            Mock Get-LDAPUser { @{ dn = 'CN=Probe,OU=Users,OU=Corp,DC=panoply,DC=local' } }

            Set-LDAPUserPasswordAsAccountHolder -AccountName 'probe' -CurrentPassword 'Old@Pass1!' -NewPassword 'New@Pass1!' -DirectoryConfig $script:adConfig | Out-Null

            Should -Invoke Get-LDAPUser -Times 1 -Exactly -ParameterFilter { $UserIdentifier -eq 'probe' }
            Should -Invoke Invoke-LdapTool -Times 1 -Exactly -ParameterFilter { $InputObject -match '(?m)^dn: CN=Probe,OU=Users,OU=Corp,DC=panoply,DC=local$' }
        }

        It 'throws when the account cannot be found' {
            Mock Get-LDAPUser { $null }

            { Set-LDAPUserPasswordAsAccountHolder -AccountName 'ghost' -CurrentPassword 'o' -NewPassword 'n' -DirectoryConfig $script:adConfig } |
                Should -Throw '*ghost*'
        }

        It 'reports a refusal with its classified outcome' {
            Mock Invoke-LdapTool { @{ ExitCode = 19; Output = "ldap_modify: Constraint violation (19)`n`tadditional info: 0000052D: AtrErr" } }

            $result = Set-LDAPUserPasswordAsAccountHolder -AccountName 'probe' -CurrentPassword 'o' -NewPassword 'n' `
                -AccountDN 'CN=Probe,DC=panoply,DC=local' -DirectoryConfig $script:adConfig

            $result.Success | Should -BeFalse
            $result.ExitCode | Should -Be 19
            $result.Outcome | Should -Be 'ConstraintViolation'
        }
    }
}

Describe 'Get-LdapRootDseTime' {
    BeforeEach {
        $script:adConfig = @{ ContainerName = $null; Host = 'dc1.panoply.local'; LdapSearchPort = 636; LdapSearchScheme = 'ldaps' }
    }

    It 'reads currentTime from the root DSE over LDAPS through the toolbox and parses it' {
        Mock Invoke-LdapTool { @{ ExitCode = 0; Output = @('dn:', 'currentTime: 20260928101530.0Z', '') } }

        $time = Get-LdapRootDseTime -DirectoryConfig $script:adConfig -BindDN 'CN=svc-jim,OU=Services,DC=panoply,DC=local' -BindPassword 'pw'

        $time | Should -Be ([datetime]::new(2026, 9, 28, 10, 15, 30, [System.DateTimeKind]::Utc))
        Should -Invoke Invoke-LdapTool -Times 1 -Exactly -ParameterFilter {
            $Tool -eq 'ldapsearch' -and $null -eq $DirectoryConfig.ContainerName -and
            (($Arguments -join '|') -eq '-x|-LLL|-H|ldaps://dc1.panoply.local:636|-D|CN=svc-jim,OU=Services,DC=panoply,DC=local|-w|pw|-s|base|-b||currentTime')
        }
    }

    It 'throws with the exit code and output when the search fails' {
        Mock Invoke-LdapTool { @{ ExitCode = 255; Output = @("ldap_sasl_bind(SIMPLE): Can't contact LDAP server (-1)") } }

        { Get-LdapRootDseTime -DirectoryConfig $script:adConfig -BindDN 'x' -BindPassword 'pw' } | Should -Throw "*exit code 255*Can't contact*"
    }

    It 'throws when the reply carries no currentTime' {
        Mock Invoke-LdapTool { @{ ExitCode = 0; Output = @('dn:', '') } }

        { Get-LdapRootDseTime -DirectoryConfig $script:adConfig -BindDN 'x' -BindPassword 'pw' } | Should -Throw '*currentTime*'
    }
}
