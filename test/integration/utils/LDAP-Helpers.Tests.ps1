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
