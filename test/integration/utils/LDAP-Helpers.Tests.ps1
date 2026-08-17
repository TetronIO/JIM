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
    unfold a folded dn: line truncated a DN and broke Scenario 1 IEO Phase 3 on Samba AD
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
        which is precisely the distinction Scenario 17 exists to assert.

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

    It 'reports an unrecognised failure as Failed rather than guessing' {
        Get-LDAPBindOutcome -ExitCode 1 -BindOutput 'ldap_sasl_bind(SIMPLE): Cannot contact LDAP server (-1)' |
            Should -Be 'Failed'
    }

    It 'does not report success on a non-zero exit code with empty output' {
        Get-LDAPBindOutcome -ExitCode 49 -BindOutput '' | Should -Be 'Failed'
    }
}
