# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

#Requires -Modules Pester

<#
.SYNOPSIS
    Pester tests for the pure functions in LabDomainController.psm1.

.DESCRIPTION
    The Active Directory lab is built on Windows with Hyper-V, so none of the scripts can run in CI. Everything
    that can be decided without Hyper-V or the ActiveDirectory module lives in the module as a pure function,
    and is proven here on any platform: SDDL rendering from the delegation file (checked against the very shell
    pipeline jim-delegate.sh uses, where bash is available), checkpoint naming, unattend rendering, certificate
    name lists, OS build parsing and the argument strings for the guest phases.
#>

BeforeDiscovery {
    $script:HasBash = [bool](Get-Command bash -ErrorAction SilentlyContinue)
    $script:HasGrep = [bool](Get-Command grep -ErrorAction SilentlyContinue)
}

BeforeAll {
    $script:ModulePath = Join-Path $PSScriptRoot 'guest' 'LabDomainController.psm1'
    Import-Module $script:ModulePath -Force

    $script:AclPath = Join-Path $PSScriptRoot '..' 'docker' 'samba-ad-prebuilt' 'delegation' 'jim-ad-delegation.acl'
    $script:TemplatePath = Join-Path $PSScriptRoot 'guest' 'autounattend.xml'
    $script:TrusteeSid = 'S-1-5-21-1004336348-1177238915-682003330-1105'

    # A SecureString without ConvertTo-SecureString -AsPlainText, which the script analyser rightly dislikes.
    function Get-TestSecret {
        param([string]$Value)
        return (New-Object System.Net.NetworkCredential('', $Value)).SecurePassword
    }
}

Describe 'ConvertTo-LabDelegationSddl' {
    It 'drops comment lines, indented comment lines and blank lines, and substitutes the trustee' {
        $content = @'
# a comment

   # an indented comment
(OA;CI;CCDC;bf967aba-0de6-11d0-a285-00aa003049e2;;__JIM_TRUSTEE_SID__)

	# a tab-indented comment
(OA;CIIO;CR;00299570-246d-11d0-a768-00aa006e0529;bf967aba-0de6-11d0-a285-00aa003049e2;__JIM_TRUSTEE_SID__)
'@
        $sddl = ConvertTo-LabDelegationSddl -AclContent $content -TrusteeSid 'S-1-5-21-1-2-3-1105'
        $sddl | Should -Be '(OA;CI;CCDC;bf967aba-0de6-11d0-a285-00aa003049e2;;S-1-5-21-1-2-3-1105)(OA;CIIO;CR;00299570-246d-11d0-a768-00aa006e0529;bf967aba-0de6-11d0-a285-00aa003049e2;S-1-5-21-1-2-3-1105)'
    }

    It 'removes spaces inside an entry, as tr -d does' {
        $sddl = ConvertTo-LabDelegationSddl -AclContent "(A;;LC RP;;;__JIM_TRUSTEE_SID__)`n" -TrusteeSid 'S-1-5-21-9'
        $sddl | Should -Be '(A;;LCRP;;;S-1-5-21-9)'
    }

    It 'replaces every occurrence of the trustee token, not just the first' {
        $sddl = ConvertTo-LabDelegationSddl -AclContent "(A;;X;;;__JIM_TRUSTEE_SID__)(A;;Y;;;__JIM_TRUSTEE_SID__)" -TrusteeSid 'S-1-5-21-7'
        ([regex]::Matches($sddl, 'S-1-5-21-7')).Count | Should -Be 2
        $sddl | Should -Not -Match '__JIM_TRUSTEE_SID__'
    }

    It 'tolerates CRLF line endings, which a Windows checkout of the .acl file may carry' {
        $sddl = ConvertTo-LabDelegationSddl -AclContent "# c`r`n(A;;LC;;;__JIM_TRUSTEE_SID__)`r`n`r`n(A;;RP;;;__JIM_TRUSTEE_SID__)`r`n" -TrusteeSid 'S-1-5-21-5'
        $sddl | Should -Be '(A;;LC;;;S-1-5-21-5)(A;;RP;;;S-1-5-21-5)'
    }

    It 'renders the real delegation file as seven entries with no whitespace and no leftover token' {
        $sddl = ConvertTo-LabDelegationSddl -AclPath $script:AclPath -TrusteeSid $script:TrusteeSid
        $sddl | Should -Not -Match '\s'
        $sddl | Should -Not -Match '__JIM_TRUSTEE_SID__'
        $sddl | Should -Not -Match '#'
        ([regex]::Matches($sddl, '\(OA;')).Count | Should -Be 7
        $sddl | Should -Match ([regex]::Escape($script:TrusteeSid))
        $sddl | Should -BeLike '(OA;CI;CCDC;bf967aba-0de6-11d0-a285-00aa003049e2;;*'
    }

    It 'is byte-for-byte what jim-delegate.sh delegation_sddl() produces for the real file' -Skip:(-not ($script:HasBash -and $script:HasGrep)) {
        # The same pipeline as delegation_sddl() in jim-delegate.sh, run by the shell it was written for.
        $shell = & bash -c "grep -v '^\s*#' '$($script:AclPath)' | grep -v '^\s*`$' | tr -d '\n ' | sed 's/__JIM_TRUSTEE_SID__/$($script:TrusteeSid)/g'"
        $shell = ($shell -join '')
        $shell.Length | Should -BeGreaterThan 100
        ConvertTo-LabDelegationSddl -AclPath $script:AclPath -TrusteeSid $script:TrusteeSid | Should -BeExactly $shell
    }

    It 'throws when the file is missing rather than returning an empty delegation' {
        { ConvertTo-LabDelegationSddl -AclPath (Join-Path ([System.IO.Path]::GetTempPath()) 'no-such-file.acl') -TrusteeSid 'S-1-5-21-1' } | Should -Throw '*missing*'
    }

    It 'throws when the file holds no entries at all' {
        { ConvertTo-LabDelegationSddl -AclContent "# only comments`n`n" -TrusteeSid 'S-1-5-21-1' } | Should -Throw '*no access control entries*'
    }

    It 'rejects a trustee that is not a SID' {
        { ConvertTo-LabDelegationSddl -AclContent '(A;;LC;;;__JIM_TRUSTEE_SID__)' -TrusteeSid 'JIM Connectors' } | Should -Throw
    }
}

Describe 'Get-LabDaclAce' {
    It 'splits a DACL into entries with type, flags, rights, object types and trustee' {
        $aces = @(Get-LabDaclAce -Sddl 'D:PAI(OA;CIIO;RPWPLCLORCSDDT;;bf967aba-0de6-11d0-a285-00aa003049e2;S-1-5-21-1-2-3-1105)(A;ID;LC;;;AU)')
        $aces.Count | Should -Be 2
        $aces[0].Type | Should -Be 'OA'
        $aces[0].Flags | Should -Be 'CIIO'
        $aces[0].Rights | Should -Be 'RPWPLCLORCSDDT'
        $aces[0].InheritedObjectType | Should -Be 'bf967aba-0de6-11d0-a285-00aa003049e2'
        $aces[0].Sid | Should -Be 'S-1-5-21-1-2-3-1105'
        $aces[0].Inherited | Should -BeFalse
        $aces[1].Inherited | Should -BeTrue
        $aces[1].Sid | Should -Be 'S-1-5-11'
    }

    It 'computes the access mask from two-letter rights' {
        (Get-LabDaclAce -Sddl 'D:(A;;LCRPRC;;;S-1-5-21-1-2-3-1105)')[0].Mask | Should -Be 0x20014
        (Get-LabDaclAce -Sddl 'D:(OA;;CR;00299570-246d-11d0-a768-00aa006e0529;;S-1-5-21-1-2-3-1105)')[0].Mask | Should -Be 0x100
    }

    It 'reads a hexadecimal access mask' {
        (Get-LabDaclAce -Sddl 'D:(A;;0x20014;;;S-1-5-21-1-2-3-1105)')[0].Mask | Should -Be 0x20014
    }

    It 'takes a bare list of entries, as the delegation renders' {
        @(Get-LabDaclAce -Sddl '(A;;LC;;;S-1-5-21-1)(A;;RP;;;S-1-5-21-1)').Count | Should -Be 2
    }

    It 'ignores the owner, group and SACL components around the DACL' {
        $aces = @(Get-LabDaclAce -Sddl 'O:BAG:BAD:PAI(A;;GA;;;BA)S:AI(AU;SA;GA;;;WD)')
        $aces.Count | Should -Be 1
        $aces[0].Sid | Should -Be 'S-1-5-32-544'
    }

    It 'returns nothing for a DACL with no entries' {
        @(Get-LabDaclAce -Sddl 'D:').Count | Should -Be 0
    }

    It 'rejects an entry without six fields' {
        { Get-LabDaclAce -Sddl 'D:(A;;LC)' } | Should -Throw '*six*'
    }
}

Describe 'Test-LabSddlContainsSid, Test-LabAclGrant and Merge-LabDelegationSddl' {
    BeforeAll {
        # An OU-like DACL: one explicit entry, then two inherited ones.
        $script:Existing = 'D:AI(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;DA)(A;ID;LC;;;AU)(A;ID;RP;;;AU)'
        $script:Delegation = ConvertTo-LabDelegationSddl -AclPath $script:AclPath -TrusteeSid $script:TrusteeSid
    }

    It 'finds a SID that is present in the DACL' {
        Test-LabSddlContainsSid -Sddl "D:(A;;LC;;;$($script:TrusteeSid))" -Sid $script:TrusteeSid | Should -BeTrue
    }

    It 'does not find a SID that is absent' {
        Test-LabSddlContainsSid -Sddl $script:Existing -Sid $script:TrusteeSid | Should -BeFalse
    }

    It 'does not match a SID that is only a prefix of the trustee' {
        Test-LabSddlContainsSid -Sddl 'D:(A;;LC;;;S-1-5-21-1-2-3-11050)' -Sid 'S-1-5-21-1-2-3-1105' | Should -BeFalse
    }

    It 'resolves well-known aliases to SIDs when looking for a trustee' {
        Test-LabSddlContainsSid -Sddl $script:Existing -Sid 'S-1-5-11' | Should -BeTrue
    }

    It 'reports a grant only when the trustee has an Allow entry carrying every requested right' {
        $sddl = "D:(A;;LCRPRC;;;$($script:TrusteeSid))"
        Test-LabAclGrant -Sddl $sddl -Sid $script:TrusteeSid -AccessMask 0x20014 | Should -BeTrue
        Test-LabAclGrant -Sddl $sddl -Sid $script:TrusteeSid -AccessMask 0x20 | Should -BeFalse
        Test-LabAclGrant -Sddl "D:(D;;LCRPRC;;;$($script:TrusteeSid))" -Sid $script:TrusteeSid -AccessMask 0x20014 | Should -BeFalse
        Test-LabAclGrant -Sddl $sddl -Sid 'S-1-5-21-9-9-9-1' -AccessMask 0x20014 | Should -BeFalse
    }

    It 'adds every delegated entry and keeps the existing ones' {
        $merged = Merge-LabDelegationSddl -ExistingSddl $script:Existing -DelegationSddl $script:Delegation -TrusteeSid $script:TrusteeSid
        $aces = @(Get-LabDaclAce -Sddl $merged)
        $aces.Count | Should -Be (3 + 7)
        (@($aces | Where-Object { $_.Sid -eq $script:TrusteeSid })).Count | Should -Be 7
    }

    It 'keeps the DACL flags and copies the entries through as written' {
        $merged = Merge-LabDelegationSddl -ExistingSddl $script:Existing -DelegationSddl $script:Delegation -TrusteeSid $script:TrusteeSid
        $merged | Should -BeLike 'D:AI(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;DA)(OA;CI;CCDC;bf967aba-0de6-11d0-a285-00aa003049e2;;*'
        $merged | Should -BeLike '*(A;ID;LC;;;AU)(A;ID;RP;;;AU)'
    }

    It 'places the new explicit entries ahead of the inherited ones, keeping the DACL canonical' {
        $merged = Merge-LabDelegationSddl -ExistingSddl $script:Existing -DelegationSddl $script:Delegation -TrusteeSid $script:TrusteeSid
        $inheritedSeen = $false
        foreach ($ace in (Get-LabDaclAce -Sddl $merged)) {
            if ($ace.Inherited) { $inheritedSeen = $true }
            elseif ($inheritedSeen) { throw 'an explicit entry follows an inherited one' }
        }
        $inheritedSeen | Should -BeTrue
    }

    It 'preserves the object type and inherited object type of an object entry' {
        $merged = Merge-LabDelegationSddl -ExistingSddl $script:Existing -DelegationSddl $script:Delegation -TrusteeSid $script:TrusteeSid
        $reset = @(Get-LabDaclAce -Sddl $merged | Where-Object { $_.ObjectType -eq '00299570-246d-11d0-a768-00aa006e0529' })
        $reset.Count | Should -Be 1
        $reset[0].InheritedObjectType | Should -Be 'bf967aba-0de6-11d0-a285-00aa003049e2'
        $reset[0].Flags | Should -Be 'CIIO'
    }

    It 'is idempotent: a second merge returns the descriptor untouched' {
        $once = Merge-LabDelegationSddl -ExistingSddl $script:Existing -DelegationSddl $script:Delegation -TrusteeSid $script:TrusteeSid
        $twice = Merge-LabDelegationSddl -ExistingSddl $once -DelegationSddl $script:Delegation -TrusteeSid $script:TrusteeSid
        $twice | Should -BeExactly $once
    }

    It 'appends after the explicit entries when the DACL has no inherited ones' {
        $merged = Merge-LabDelegationSddl -ExistingSddl 'D:(A;;GA;;;DA)' -DelegationSddl '(A;;LCRPRC;;;S-1-5-21-1-2-3-1105)' -TrusteeSid 'S-1-5-21-1-2-3-1105'
        $merged | Should -Be 'D:(A;;GA;;;DA)(A;;LCRPRC;;;S-1-5-21-1-2-3-1105)'
    }

    It 'keeps the owner, group and SACL components either side of the DACL' {
        $merged = Merge-LabDelegationSddl -ExistingSddl 'O:BAG:BAD:PAI(A;;GA;;;BA)(A;ID;LC;;;AU)S:AI(AU;SA;GA;;;WD)' -DelegationSddl '(A;;LCRPRC;;;S-1-5-21-1-2-3-1105)' -TrusteeSid 'S-1-5-21-1-2-3-1105'
        $merged | Should -Be 'O:BAG:BAD:PAI(A;;GA;;;BA)(A;;LCRPRC;;;S-1-5-21-1-2-3-1105)(A;ID;LC;;;AU)S:AI(AU;SA;GA;;;WD)'
    }

    It 'throws when there is no DACL to add to' {
        { Merge-LabDelegationSddl -ExistingSddl 'O:BAG:BA' -DelegationSddl '(A;;LC;;;S-1-5-21-1)' -TrusteeSid 'S-1-5-21-1' } | Should -Throw '*DACL*'
    }
}

Describe 'Get-LabCheckpointName' {
    It 'names the baseline checkpoint' {
        Get-LabCheckpointName -Baseline | Should -Be 'baseline'
    }

    It 'names a populated checkpoint from the template and the content hash, lower-cased' {
        Get-LabCheckpointName -Template 'Medium' -Hash '0123456789abcdef' | Should -Be 'populated-medium-0123456789abcdef'
    }

    It 'lower-cases an upper-case hash' {
        Get-LabCheckpointName -Template 'XLarge' -Hash '0123456789ABCDEF' | Should -Be 'populated-xlarge-0123456789abcdef'
    }

    It 'rejects a hash that is not 16 hexadecimal characters' {
        { Get-LabCheckpointName -Template 'Small' -Hash '0123' } | Should -Throw
        { Get-LabCheckpointName -Template 'Small' -Hash '0123456789abcdeg' } | Should -Throw
        { Get-LabCheckpointName -Template 'Small' -Hash '0123456789abcdef0' } | Should -Throw
    }

    It 'rejects a template that would break the name' {
        { Get-LabCheckpointName -Template 'Med ium' -Hash '0123456789abcdef' } | Should -Throw
        { Get-LabCheckpointName -Template '' -Hash '0123456789abcdef' } | Should -Throw
        { Get-LabCheckpointName -Template 'a-b' -Hash '0123456789abcdef' } | Should -Throw
    }
}

Describe 'Test-LabCheckpointName' {
    It 'accepts the two forms the control plane knows' {
        Test-LabCheckpointName -Name 'baseline' | Should -BeTrue
        Test-LabCheckpointName -Name 'populated-medium-0123456789abcdef' | Should -BeTrue
    }

    It 'rejects anything else, including names built to smuggle in more than a checkpoint' {
        Test-LabCheckpointName -Name 'Baseline' | Should -BeFalse
        Test-LabCheckpointName -Name 'populated-medium-XYZ' | Should -BeFalse
        Test-LabCheckpointName -Name 'baseline; Remove-VM x' | Should -BeFalse
        Test-LabCheckpointName -Name '' | Should -BeFalse
    }
}

Describe 'Get-LabAvmaKey' {
    It 'returns a well-formed key for Windows Server 2025 Datacenter' {
        Get-LabAvmaKey -Edition Datacenter | Should -Match '^[A-Z0-9]{5}(-[A-Z0-9]{5}){4}$'
    }

    It 'returns the key Microsoft publishes for Windows Server 2025 Datacenter' {
        # https://learn.microsoft.com/windows-server/get-started/automatic-vm-activation (Windows Server 2025 tab)
        Get-LabAvmaKey -Edition Datacenter | Should -Be 'YQB4H-NKHHJ-Q6K4R-4VMY6-VCH67'
    }

    It 'returns a different key for Standard' {
        Get-LabAvmaKey -Edition Standard | Should -Not -Be (Get-LabAvmaKey -Edition Datacenter)
    }
}

Describe 'ConvertTo-LabUnattendPassword' {
    It 'encodes the password with the suffix Windows Setup expects, as base64 UTF-16LE' {
        $encoded = ConvertTo-LabUnattendPassword -Password (Get-TestSecret 'Abc123!') -Kind AdministratorPassword
        [System.Text.Encoding]::Unicode.GetString([Convert]::FromBase64String($encoded)) | Should -Be 'Abc123!AdministratorPassword'
    }

    It 'uses the plain Password suffix for the auto-logon entry' {
        $encoded = ConvertTo-LabUnattendPassword -Password (Get-TestSecret 'Abc123!') -Kind Password
        [System.Text.Encoding]::Unicode.GetString([Convert]::FromBase64String($encoded)) | Should -Be 'Abc123!Password'
    }

    It 'never contains the clear-text password' {
        $encoded = ConvertTo-LabUnattendPassword -Password (Get-TestSecret 'Abc123!') -Kind Password
        $encoded | Should -Not -Match 'Abc123'
    }
}

Describe 'ConvertTo-LabUnattendXml' {
    BeforeAll {
        $script:Values = @{
            COMPUTER_NAME              = 'dc1'
            ADMIN_PASSWORD_ENCODED     = 'QUJD'
            AUTOLOGON_PASSWORD_ENCODED = 'REVG'
            IMAGE_NAME                 = 'Windows Server 2025 SERVERDATACENTER'
            PRODUCT_KEY                = 'YQB4H-NKHHJ-Q6K4R-4VMY6-VCH67'
            UI_LANGUAGE                = 'en-US'
            LOCALE                     = 'en-GB'
            TIME_ZONE                  = 'UTC'
        }
        $script:Template = Get-Content -LiteralPath $script:TemplatePath -Raw
    }

    It 'lists exactly the placeholders the values cover, so template and defaults cannot drift' {
        $names = Get-LabUnattendPlaceholder -Template $script:Template
        ($names | Sort-Object) | Should -Be ($script:Values.Keys | Sort-Object)
    }

    It 'renders every placeholder and leaves none behind' {
        $xml = ConvertTo-LabUnattendXml -Template $script:Template -Values $script:Values
        $xml | Should -Not -Match '__[A-Z][A-Z0-9_]*__'
        $xml | Should -Match '<ComputerName>dc1</ComputerName>'
        $xml | Should -Match 'YQB4H-NKHHJ-Q6K4R-4VMY6-VCH67'
        $xml | Should -Match '<TimeZone>UTC</TimeZone>'
    }

    It 'produces well-formed XML' {
        $xml = ConvertTo-LabUnattendXml -Template $script:Template -Values $script:Values
        { [xml]$xml } | Should -Not -Throw
    }

    It 'selects the image by name and installs to the disk configured for UEFI' {
        $xml = [xml](ConvertTo-LabUnattendXml -Template $script:Template -Values $script:Values)
        $xml.OuterXml | Should -Match 'Windows Server 2025 SERVERDATACENTER'
        $xml.OuterXml | Should -Match '<Type>EFI</Type>'
    }

    It 'installs nothing at first logon and does not enable OpenSSH' {
        $xml = ConvertTo-LabUnattendXml -Template $script:Template -Values $script:Values
        $xml | Should -Not -Match '(?i)openssh|Install-WindowsFeature|choco|winget|msiexec'
    }

    It 'XML-escapes values' {
        $withMarkup = $script:Values.Clone()
        $withMarkup.IMAGE_NAME = 'Windows <Server> & "Co"'
        $xml = ConvertTo-LabUnattendXml -Template $script:Template -Values $withMarkup
        { [xml]$xml } | Should -Not -Throw
        $xml | Should -Match 'Windows &lt;Server&gt; &amp; &quot;Co&quot;'
    }

    It 'throws naming the placeholder when a value is missing' {
        $missing = $script:Values.Clone()
        $missing.Remove('COMPUTER_NAME')
        { ConvertTo-LabUnattendXml -Template $script:Template -Values $missing } | Should -Throw '*COMPUTER_NAME*'
    }

    It 'throws when a value has no placeholder, which is what a typo looks like' {
        $extra = $script:Values.Clone()
        $extra.COMPUTERNAME = 'x'
        { ConvertTo-LabUnattendXml -Template $script:Template -Values $extra } | Should -Throw '*COMPUTERNAME*'
    }

    It 'throws when a value is empty' {
        $empty = $script:Values.Clone()
        $empty.TIME_ZONE = ''
        { ConvertTo-LabUnattendXml -Template $script:Template -Values $empty } | Should -Throw '*TIME_ZONE*'
    }

    It 'does not re-scan substituted text for placeholders' {
        $tricky = $script:Values.Clone()
        $tricky.COMPUTER_NAME = '__TIME_ZONE__'
        $xml = ConvertTo-LabUnattendXml -Template $script:Template -Values $tricky
        $xml | Should -Match '<ComputerName>__TIME_ZONE__</ComputerName>'
    }

    It 'names the AVMA key source next to the key in the template' {
        $script:Template | Should -Match 'automatic-vm-activation'
    }

    It 'carries the copyright notice' {
        $script:Template | Should -Match 'Copyright \(c\) Tetron Limited\. All rights reserved\.'
    }
}

Describe 'Get-LabDomainInfo' {
    It 'derives the names every other function needs from the domain and the computer name' {
        $info = Get-LabDomainInfo -Domain 'PANOPLY.LOCAL' -ShortName 'dc1'
        $info.DnsDomain | Should -Be 'panoply.local'
        $info.NetBiosName | Should -Be 'PANOPLY'
        $info.ShortName | Should -Be 'dc1'
        $info.Fqdn | Should -Be 'dc1.panoply.local'
        $info.BaseDn | Should -Be 'DC=panoply,DC=local'
    }

    It 'honours an explicit NetBIOS name' {
        (Get-LabDomainInfo -Domain 'RESURGAM.LOCAL' -ShortName 'dc1' -NetBiosName 'RSG').NetBiosName | Should -Be 'RSG'
    }

    It 'builds the base DN for a deeper name' {
        (Get-LabDomainInfo -Domain 'corp.example.test' -ShortName 'dc1').BaseDn | Should -Be 'DC=corp,DC=example,DC=test'
    }

    It 'rejects a single-label domain, which cannot host a forest' {
        { Get-LabDomainInfo -Domain 'PANOPLY' -ShortName 'dc1' } | Should -Throw
    }

    It 'rejects a NetBIOS name longer than 15 characters' {
        { Get-LabDomainInfo -Domain 'AVERYLONGDOMAINNAMEINDEED.LOCAL' -ShortName 'dc1' -NetBiosName 'AVERYLONGDOMAINNAMEINDEED' } | Should -Throw
    }

    It 'rejects a computer name with a dot in it' {
        { Get-LabDomainInfo -Domain 'PANOPLY.LOCAL' -ShortName 'dc1.panoply.local' } | Should -Throw
    }

    It 'matches the three lab forests' {
        (Get-LabDomainInfo -Domain 'GENTIAN.LOCAL' -ShortName 'dc1').Fqdn | Should -Be 'dc1.gentian.local'
        (Get-LabDomainInfo -Domain 'RESURGAM.LOCAL' -ShortName 'dc1').BaseDn | Should -Be 'DC=resurgam,DC=local'
    }
}

Describe 'Get-LabCertificateName' {
    It 'lists the FQDN, the short name and the domain, in that order' {
        Get-LabCertificateName -Fqdn 'dc1.panoply.local' -ShortName 'dc1' -Domain 'panoply.local' | Should -Be @('dc1.panoply.local', 'dc1', 'panoply.local')
    }

    It 'appends extra names after the three fixed ones' {
        Get-LabCertificateName -Fqdn 'dc1.panoply.local' -ShortName 'dc1' -Domain 'panoply.local' -ExtraName 'samba-ad-primary', 'lab-dc' |
            Should -Be @('dc1.panoply.local', 'dc1', 'panoply.local', 'samba-ad-primary', 'lab-dc')
    }

    It 'drops duplicates case-insensitively and keeps the first spelling' {
        Get-LabCertificateName -Fqdn 'dc1.panoply.local' -ShortName 'dc1' -Domain 'panoply.local' -ExtraName 'DC1', 'Lab-DC', 'lab-dc', 'PANOPLY.LOCAL' |
            Should -Be @('dc1.panoply.local', 'dc1', 'panoply.local', 'Lab-DC')
    }

    It 'ignores blank and whitespace-only extras and trims the rest' {
        Get-LabCertificateName -Fqdn 'dc1.panoply.local' -ShortName 'dc1' -Domain 'panoply.local' -ExtraName '', '  ', ' host-a ' |
            Should -Be @('dc1.panoply.local', 'dc1', 'panoply.local', 'host-a')
    }

    It 'accepts no extras at all' {
        (Get-LabCertificateName -Fqdn 'dc1.gentian.local' -ShortName 'dc1' -Domain 'gentian.local' -ExtraName @()).Count | Should -Be 3
    }

    It 'rejects a name that is not a DNS name, so nothing odd reaches the certificate command' {
        { Get-LabCertificateName -Fqdn 'dc1.panoply.local' -ShortName 'dc1' -Domain 'panoply.local' -ExtraName 'bad name' } | Should -Throw
        { Get-LabCertificateName -Fqdn 'dc1.panoply.local' -ShortName 'dc1' -Domain 'panoply.local' -ExtraName 'a,b' } | Should -Throw
        { Get-LabCertificateName -Fqdn 'dc1.panoply.local' -ShortName 'dc1' -Domain 'panoply.local' -ExtraName 'x;y' } | Should -Throw
    }
}

Describe 'ConvertTo-LabOsBuild' {
    It 'joins the build and the update build revision' {
        $build = ConvertTo-LabOsBuild -ProductName 'Windows Server 2022 Datacenter' -DisplayVersion '24H2' -CurrentBuild '26100' -Ubr 1742
        $build.buildString | Should -Be '26100.1742'
        $build.currentBuild | Should -Be '26100'
        $build.ubr | Should -Be 1742
        $build.productName | Should -Be 'Windows Server 2022 Datacenter'
        $build.displayVersion | Should -Be '24H2'
    }

    It 'accepts registry values as strings' {
        (ConvertTo-LabOsBuild -CurrentBuild '26100' -Ubr '4061').buildString | Should -Be '26100.4061'
    }

    It 'parses a four-part version string' {
        $build = ConvertTo-LabOsBuild -VersionString '10.0.26100.1742'
        $build.currentBuild | Should -Be '26100'
        $build.ubr | Should -Be 1742
        $build.buildString | Should -Be '26100.1742'
    }

    It 'trims whitespace the registry may carry' {
        (ConvertTo-LabOsBuild -CurrentBuild ' 26100 ' -Ubr ' 12 ').buildString | Should -Be '26100.12'
    }

    It 'rejects a build that is not a number' {
        { ConvertTo-LabOsBuild -CurrentBuild 'abc' -Ubr 1 } | Should -Throw
    }

    It 'rejects a malformed version string' {
        { ConvertTo-LabOsBuild -VersionString '10.0.26100' } | Should -Throw
        { ConvertTo-LabOsBuild -VersionString 'ten' } | Should -Throw
    }

    It 'reports a missing revision as the bare build, so a half-read registry is visible rather than fatal' {
        $build = ConvertTo-LabOsBuild -CurrentBuild '26100'
        $build.buildString | Should -Be '26100'
        $build.ubr | Should -BeNullOrEmpty
    }
}

Describe 'Get-LabGuestPhaseArgument' {
    BeforeAll {
        $script:Settings = @{
            ComputerName           = 'dc1'
            Domain                 = 'PANOPLY.LOCAL'
            NetBiosName            = 'PANOPLY'
            IPAddress              = '10.20.30.41'
            PrefixLength           = 24
            Gateway                = '10.20.30.1'
            DnsForwarder           = '10.20.30.2'
            NtpServer              = 'ntp.lab.test'
            SafeModePassword       = (Get-TestSecret 'Dsrm-Secret-1')
            ServiceAccountPassword = (Get-TestSecret 'Svc-Secret-1')
            EnableRecycleBin       = $true
            ExtraCertificateNames  = @('samba-ad-primary', 'lab-dc')
            VmName                 = 'dc-primary'
            LabRoot                = 'C:\jim-ad-lab'
        }
    }

    It 'gives Prepare the network settings and nothing that belongs to a later phase' {
        $arguments = Get-LabGuestPhaseArgument -Phase Prepare -Settings $script:Settings
        $arguments.Phase | Should -Be 'Prepare'
        $arguments.IPAddress | Should -Be '10.20.30.41'
        $arguments.PrefixLength | Should -Be 24
        $arguments.Gateway | Should -Be '10.20.30.1'
        $arguments.Contains('SafeModePassword') | Should -BeFalse
        $arguments.Contains('ServiceAccountPassword') | Should -BeFalse
        $arguments.Contains('EnableRecycleBin') | Should -BeFalse
    }

    It 'gives Promote the domain and the safe-mode password only' {
        $arguments = Get-LabGuestPhaseArgument -Phase Promote -Settings $script:Settings
        $arguments.Domain | Should -Be 'PANOPLY.LOCAL'
        $arguments.NetBiosName | Should -Be 'PANOPLY'
        $arguments.SafeModePassword | Should -BeOfType [securestring]
        $arguments.Contains('ServiceAccountPassword') | Should -BeFalse
        $arguments.Contains('IPAddress') | Should -BeFalse
    }

    It 'gives Configure everything it applies, with the Recycle Bin as a switch' {
        $arguments = Get-LabGuestPhaseArgument -Phase Configure -Settings $script:Settings
        $arguments.NtpServer | Should -Be 'ntp.lab.test'
        $arguments.DnsForwarder | Should -Be '10.20.30.2'
        $arguments.EnableRecycleBin | Should -BeTrue
        $arguments.ExtraCertificateNames | Should -Be @('samba-ad-primary', 'lab-dc')
        $arguments.ServiceAccountPassword | Should -BeOfType [securestring]
        $arguments.VmName | Should -Be 'dc-primary'
    }

    It 'leaves the Recycle Bin switch out when it is off, so splatting does not turn it on' {
        $off = $script:Settings.Clone()
        $off.EnableRecycleBin = $false
        (Get-LabGuestPhaseArgument -Phase Configure -Settings $off).Contains('EnableRecycleBin') | Should -BeFalse
    }

    It 'gives Verify the settings it checks against' {
        $arguments = Get-LabGuestPhaseArgument -Phase Verify -Settings $script:Settings
        $arguments.ServiceAccountPassword | Should -BeOfType [securestring]
        $arguments.ExtraCertificateNames | Should -Be @('samba-ad-primary', 'lab-dc')
        $arguments.NtpServer | Should -Be 'ntp.lab.test'
        $arguments.Contains('SafeModePassword') | Should -BeFalse
    }

    It 'puts the phase first' {
        @((Get-LabGuestPhaseArgument -Phase Verify -Settings $script:Settings).Keys)[0] | Should -Be 'Phase'
    }

    It 'throws naming the setting when one the phase needs is missing' {
        $partial = $script:Settings.Clone()
        $partial.Remove('Gateway')
        { Get-LabGuestPhaseArgument -Phase Prepare -Settings $partial } | Should -Throw '*Gateway*'
    }

    It 'does not need settings a phase ignores' {
        $partial = $script:Settings.Clone()
        $partial.Remove('Gateway')
        { Get-LabGuestPhaseArgument -Phase Promote -Settings $partial } | Should -Not -Throw
    }
}

Describe 'ConvertTo-LabArgumentString' {
    It 'renders names, values, switches and arrays as a copy-pasteable command tail' {
        $text = ConvertTo-LabArgumentString -Argument ([ordered]@{
                Phase                 = 'Configure'
                Domain                = 'PANOPLY.LOCAL'
                LabRoot               = 'C:\jim-ad-lab'
                EnableRecycleBin      = $true
                ExtraCertificateNames = @('samba-ad-primary', 'lab-dc')
                PrefixLength          = 24
            })
        $text | Should -Be "-Phase Configure -Domain PANOPLY.LOCAL -LabRoot C:\jim-ad-lab -EnableRecycleBin -ExtraCertificateNames samba-ad-primary,lab-dc -PrefixLength 24"
    }

    It 'quotes a value that needs it and doubles embedded single quotes' {
        $text = ConvertTo-LabArgumentString -Argument ([ordered]@{ Note = "it's a value" })
        $text | Should -Be "-Note 'it''s a value'"
    }

    It 'redacts a SecureString, so a logged command line carries no secret' {
        $text = ConvertTo-LabArgumentString -Argument ([ordered]@{ SafeModePassword = (Get-TestSecret 'Dsrm-Secret-1'); Domain = 'PANOPLY.LOCAL' })
        $text | Should -Be '-SafeModePassword <redacted> -Domain PANOPLY.LOCAL'
        $text | Should -Not -Match 'Dsrm'
    }

    It 'omits a switch that is false' {
        ConvertTo-LabArgumentString -Argument ([ordered]@{ EnableRecycleBin = $false; Phase = 'Verify' }) | Should -Be '-Phase Verify'
    }
}

Describe 'Resolve-LabInstallImage' {
    BeforeAll {
        $script:Images = @(
            [pscustomobject]@{ ImageIndex = 1; ImageName = 'Windows Server 2025 Standard' }
            [pscustomobject]@{ ImageIndex = 2; ImageName = 'Windows Server 2025 Standard (Desktop Experience)' }
            [pscustomobject]@{ ImageIndex = 3; ImageName = 'Windows Server 2025 Datacenter' }
            [pscustomobject]@{ ImageIndex = 4; ImageName = 'Windows Server 2025 Datacenter (Desktop Experience)' }
        )
    }

    It 'picks Datacenter with the Desktop Experience' {
        (Resolve-LabInstallImage -Image $script:Images).ImageName | Should -Be 'Windows Server 2025 Datacenter (Desktop Experience)'
    }

    It 'picks the Server Core Datacenter image when asked for it' {
        (Resolve-LabInstallImage -Image $script:Images -ServerCore).ImageName | Should -Be 'Windows Server 2025 Datacenter'
    }

    It 'copes with the older naming, where the edition is upper case and Server Core is the bare name' {
        $legacy = @(
            [pscustomobject]@{ ImageIndex = 3; ImageName = 'Windows Server 2025 SERVERDATACENTERCORE' }
            [pscustomobject]@{ ImageIndex = 4; ImageName = 'Windows Server 2025 SERVERDATACENTER' }
        )
        (Resolve-LabInstallImage -Image $legacy).ImageIndex | Should -Be 4
    }

    It 'throws listing what the media does hold when there is no Datacenter image, as an evaluation-only ISO might' {
        $evaluation = @([pscustomobject]@{ ImageIndex = 1; ImageName = 'Windows Server 2025 Standard Evaluation' })
        { Resolve-LabInstallImage -Image $evaluation } | Should -Throw '*Standard Evaluation*'
    }

    It 'refuses an Evaluation edition even when it is Datacenter, because it cannot be converted and would expire' {
        $evaluation = @([pscustomobject]@{ ImageIndex = 4; ImageName = 'Windows Server 2025 Datacenter Evaluation (Desktop Experience)' })
        { Resolve-LabInstallImage -Image $evaluation } | Should -Throw '*Evaluation*'
    }
}

Describe 'VM notes' {
    It 'round-trips the lab metadata' {
        $notes = ConvertTo-LabVmNote -Domain 'PANOPLY.LOCAL' -ShortName 'dc1' -NetBiosName 'PANOPLY'
        $parsed = ConvertFrom-LabVmNote -Notes $notes
        $parsed.Domain | Should -Be 'PANOPLY.LOCAL'
        $parsed.ComputerName | Should -Be 'dc1'
        $parsed.NetBiosName | Should -Be 'PANOPLY'
    }

    It 'round-trips the address when one is given' {
        $notes = ConvertTo-LabVmNote -Domain 'PANOPLY.LOCAL' -ShortName 'dc1' -NetBiosName 'PANOPLY' -IPAddress '10.20.30.41'
        (ConvertFrom-LabVmNote -Notes $notes).IPAddress | Should -Be '10.20.30.41'
    }

    It 'tolerates Windows line endings in the stored notes' {
        $notes = (ConvertTo-LabVmNote -Domain 'PANOPLY.LOCAL' -ShortName 'dc1' -NetBiosName 'PANOPLY') -replace "`n", "`r`n"
        (ConvertFrom-LabVmNote -Notes $notes).NetBiosName | Should -Be 'PANOPLY'
    }

    It 'recognises a lab VM by its marker' {
        Test-LabVmNote -Notes (ConvertTo-LabVmNote -Domain 'GENTIAN.LOCAL' -ShortName 'dc1' -NetBiosName 'GENTIAN') | Should -BeTrue
    }

    It 'does not recognise a VM someone else made, so the control plane cannot be pointed at it' {
        Test-LabVmNote -Notes 'my personal VM' | Should -BeFalse
        Test-LabVmNote -Notes '' | Should -BeFalse
        Test-LabVmNote -Notes $null | Should -BeFalse
    }

    It 'returns nothing for notes without the marker' {
        ConvertFrom-LabVmNote -Notes 'domain=PANOPLY.LOCAL' | Should -BeNullOrEmpty
    }
}

Describe 'Test-LabVmName' {
    It 'accepts the lab names and ordinary Hyper-V names' {
        Test-LabVmName -Name 'dc-primary' | Should -BeTrue
        Test-LabVmName -Name 'dc-source' | Should -BeTrue
        Test-LabVmName -Name 'DC2' | Should -BeTrue
    }

    It 'rejects wildcards, separators and anything that could widen a Hyper-V lookup' {
        Test-LabVmName -Name '*' | Should -BeFalse
        Test-LabVmName -Name 'dc-*' | Should -BeFalse
        Test-LabVmName -Name 'dc primary' | Should -BeFalse
        Test-LabVmName -Name 'a;b' | Should -BeFalse
        Test-LabVmName -Name '' | Should -BeFalse
    }
}

Describe 'New-LabDomainControllerStatus' {
    BeforeAll {
        $script:Vm = [pscustomobject]@{ Name = 'dc-primary'; State = 'Running'; Generation = 2; CheckpointType = 'ProductionOnly'; Uptime = [timespan]::FromSeconds(3725.9) }
        $script:Checkpoints = @(
            [pscustomobject]@{ Name = 'baseline'; CreationTime = [datetime]::SpecifyKind([datetime]'2026-09-01T02:03:04', [DateTimeKind]::Utc) }
            [pscustomobject]@{ Name = 'populated-medium-0123456789abcdef'; CreationTime = [datetime]::SpecifyKind([datetime]'2026-09-02T05:06:07', [DateTimeKind]::Utc) }
        )
    }

    It 'has exactly the documented shape' {
        $guest = ConvertTo-LabOsBuild -ProductName 'Windows Server 2022 Datacenter' -DisplayVersion '24H2' -CurrentBuild '26100' -Ubr 1742
        $status = New-LabDomainControllerStatus -Vm $script:Vm -Checkpoint $script:Checkpoints -Guest $guest
        @($status.PSObject.Properties.Name) | Should -Be @('name', 'state', 'generation', 'checkpointType', 'uptimeSeconds', 'checkpoints', 'guest')
        $status.name | Should -Be 'dc-primary'
        $status.state | Should -Be 'Running'
        $status.generation | Should -Be 2
        $status.checkpointType | Should -Be 'ProductionOnly'
        $status.uptimeSeconds | Should -Be 3725
        @($status.checkpoints).Count | Should -Be 2
        @($status.checkpoints[0].PSObject.Properties.Name) | Should -Be @('name', 'createdUtc')
        $status.checkpoints[0].createdUtc | Should -Be '2026-09-01T02:03:04Z'
        @($status.guest.PSObject.Properties.Name) | Should -Be @('productName', 'displayVersion', 'currentBuild', 'ubr', 'buildString')
        $status.guest.buildString | Should -Be '26100.1742'
    }

    It 'serialises to JSON with a null guest when the VM is not running' {
        $off = [pscustomobject]@{ Name = 'dc-source'; State = 'Off'; Generation = 2; CheckpointType = 'ProductionOnly'; Uptime = [timespan]::Zero }
        $json = New-LabDomainControllerStatus -Vm $off -Checkpoint @() -Guest $null | ConvertTo-Json -Depth 5
        $parsed = $json | ConvertFrom-Json
        $parsed.guest | Should -BeNullOrEmpty
        $parsed.uptimeSeconds | Should -Be 0
        @($parsed.checkpoints).Count | Should -Be 0
        $json | Should -Match '"guest":\s*null'
        $json | Should -Match '"checkpoints":\s*\[\s*\]'
    }

    It 'keeps a single checkpoint as an array in JSON' {
        $json = New-LabDomainControllerStatus -Vm $script:Vm -Checkpoint @($script:Checkpoints[0]) -Guest $null | ConvertTo-Json -Depth 5
        $json | Should -Match '"checkpoints":\s*\['
    }
}

Describe 'Resolve-LabSecret' {
    AfterEach {
        Remove-Item Env:\JIM_AD_LAB_TEST_SECRET -ErrorAction SilentlyContinue
    }

    It 'prefers the parameter over the environment' {
        $env:JIM_AD_LAB_TEST_SECRET = 'from-env'
        $secret = Resolve-LabSecret -Value (Get-TestSecret 'from-parameter') -EnvironmentVariable 'JIM_AD_LAB_TEST_SECRET'
        (New-Object System.Net.NetworkCredential('', $secret)).Password | Should -Be 'from-parameter'
    }

    It 'falls back to the environment variable' {
        $env:JIM_AD_LAB_TEST_SECRET = 'from-env'
        $secret = Resolve-LabSecret -EnvironmentVariable 'JIM_AD_LAB_TEST_SECRET'
        $secret | Should -BeOfType [securestring]
        (New-Object System.Net.NetworkCredential('', $secret)).Password | Should -Be 'from-env'
    }

    It 'throws naming the variable when neither is supplied' {
        { Resolve-LabSecret -EnvironmentVariable 'JIM_AD_LAB_TEST_SECRET' } | Should -Throw '*JIM_AD_LAB_TEST_SECRET*'
    }

    It 'returns nothing when the secret is optional and absent' {
        Resolve-LabSecret -EnvironmentVariable 'JIM_AD_LAB_TEST_SECRET' -Optional | Should -BeNullOrEmpty
    }
}

Describe 'Get-LabAdministratorUserName' {
    It 'uses the local account before promotion' {
        Get-LabAdministratorUserName -NetBiosName 'PANOPLY' | Should -Be '.\Administrator'
    }

    It 'uses the domain account after promotion' {
        Get-LabAdministratorUserName -NetBiosName 'PANOPLY' -Promoted | Should -Be 'PANOPLY\Administrator'
    }
}

Describe 'Resolve-LabLayout' {
    BeforeEach {
        $script:Root = Join-Path ([System.IO.Path]::GetTempPath()) "jim-lab-layout-$([guid]::NewGuid())"
        New-Item -ItemType Directory -Path $script:Root | Out-Null
    }

    AfterEach {
        Remove-Item -LiteralPath $script:Root -Recurse -Force -ErrorAction SilentlyContinue
    }

    It 'finds the assets in the repository layout' {
        $host1 = Join-Path $script:Root 'ad-lab' 'host'
        $guest = Join-Path $script:Root 'ad-lab' 'guest'
        $acl = Join-Path $script:Root 'docker' 'samba-ad-prebuilt' 'delegation'
        New-Item -ItemType Directory -Path $host1, $guest, $acl -Force | Out-Null
        Set-Content -LiteralPath (Join-Path $guest 'LabDomainController.psm1') -Value '# m'
        Set-Content -LiteralPath (Join-Path $acl 'jim-ad-delegation.acl') -Value '# a'
        $layout = Resolve-LabLayout -ScriptRoot $host1
        $layout.GuestDirectory | Should -Be (Join-Path (Resolve-Path (Join-Path $host1 '..')).Path 'guest')
        (Split-Path $layout.DelegationAclPath -Leaf) | Should -Be 'jim-ad-delegation.acl'
        (Test-Path -LiteralPath $layout.ModulePath) | Should -BeTrue
    }

    It 'finds the assets in the deployed layout, where the host scripts sit in the root beside guest and delegation' {
        $guest = Join-Path $script:Root 'guest'
        $acl = Join-Path $script:Root 'delegation'
        New-Item -ItemType Directory -Path $guest, $acl | Out-Null
        Set-Content -LiteralPath (Join-Path $guest 'LabDomainController.psm1') -Value '# m'
        Set-Content -LiteralPath (Join-Path $acl 'jim-ad-delegation.acl') -Value '# a'
        $layout = Resolve-LabLayout -ScriptRoot $script:Root
        $layout.GuestDirectory | Should -Be $guest
        $layout.DelegationAclPath | Should -Be (Join-Path $acl 'jim-ad-delegation.acl')
    }

    It 'throws naming what it looked for when the assets are not there' {
        { Resolve-LabLayout -ScriptRoot $script:Root } | Should -Throw '*jim-ad-delegation.acl*'
    }
}

Describe 'Module surface' {
    It 'imports without Hyper-V or the ActiveDirectory module' {
        { Import-Module $script:ModulePath -Force } | Should -Not -Throw
    }

    It 'has a guard that answers for the current platform' {
        Test-LabIsWindows | Should -Be ([bool]($IsWindows -or ($PSVersionTable.PSEdition -eq 'Desktop')))
    }

    It 'throws a clear message from a Windows-only function on another platform' -Skip:([bool]($IsWindows -or ($PSVersionTable.PSEdition -eq 'Desktop'))) {
        { New-LabIsoImage -SourceDirectory '/tmp' -IsoPath '/tmp/x.iso' } | Should -Throw '*Windows*'
    }

    It 'carries no em dash anywhere in the module or the tests' {
        $emDash = [string][char]0x2014
        (Get-Content -LiteralPath $script:ModulePath -Raw).Contains($emDash) | Should -BeFalse
        (Get-Content -LiteralPath $PSCommandPath -Raw).Contains($emDash) | Should -BeFalse
    }
}
