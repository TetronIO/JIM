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

    It 'derives the NetBIOS name when it is passed empty, as New-LabDomainController passes it when not given one' {
        (Get-LabDomainInfo -Domain 'PANOPLY.LOCAL' -ShortName 'dc1' -NetBiosName '').NetBiosName | Should -Be 'PANOPLY'
    }

    It 'still rejects a NetBIOS name with characters NetBIOS does not allow' {
        { Get-LabDomainInfo -Domain 'PANOPLY.LOCAL' -ShortName 'dc1' -NetBiosName 'PAN OPLY' } | Should -Throw
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

    It 'gives License nothing but the phase: it needs no settings and no secret' {
        $arguments = Get-LabGuestPhaseArgument -Phase License -Settings $script:Settings
        @($arguments.Keys) | Should -Be @('Phase')
        $arguments.Phase | Should -Be 'License'
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

    It 'throws listing what the media does hold when there is no Datacenter image, as a Standard-only ISO might' {
        $evaluation = @([pscustomobject]@{ ImageIndex = 1; ImageName = 'Windows Server 2025 Standard Evaluation' })
        { Resolve-LabInstallImage -Image $evaluation } | Should -Throw '*Standard Evaluation*'
    }

    It 'flags a full Datacenter image as not Evaluation' {
        (Resolve-LabInstallImage -Image $script:Images).Evaluation | Should -BeFalse
    }

    It 'falls back to the Datacenter Evaluation image, flagged, when the media holds nothing else (the free Evaluation Center ISO)' {
        $evaluation = @(
            [pscustomobject]@{ ImageIndex = 1; ImageName = 'Windows Server 2025 Standard Evaluation' }
            [pscustomobject]@{ ImageIndex = 2; ImageName = 'Windows Server 2025 Standard Evaluation (Desktop Experience)' }
            [pscustomobject]@{ ImageIndex = 3; ImageName = 'Windows Server 2025 Datacenter Evaluation' }
            [pscustomobject]@{ ImageIndex = 4; ImageName = 'Windows Server 2025 Datacenter Evaluation (Desktop Experience)' }
        )
        $image = Resolve-LabInstallImage -Image $evaluation
        $image.ImageName | Should -Be 'Windows Server 2025 Datacenter Evaluation (Desktop Experience)'
        $image.ImageIndex | Should -Be 4
        $image.Evaluation | Should -BeTrue
        (Resolve-LabInstallImage -Image $evaluation -ServerCore).ImageName | Should -Be 'Windows Server 2025 Datacenter Evaluation'
    }

    It 'prefers a full Datacenter image over an Evaluation one on the same media' {
        $mixed = @(
            [pscustomobject]@{ ImageIndex = 1; ImageName = 'Windows Server 2025 Datacenter Evaluation (Desktop Experience)' }
            [pscustomobject]@{ ImageIndex = 2; ImageName = 'Windows Server 2025 Datacenter (Desktop Experience)' }
        )
        $image = Resolve-LabInstallImage -Image $mixed
        $image.ImageIndex | Should -Be 2
        $image.Evaluation | Should -BeFalse
    }
}

Describe 'Get-LabDirectoryResultCode' {
    It 'reads the result code from the response when the exception carries one' {
        $exception = [pscustomobject]@{ Message = 'The user has insufficient access rights.'; Response = [pscustomobject]@{ ResultCode = 'InsufficientAccessRights' } }
        Get-LabDirectoryResultCode -Exception $exception | Should -Be 'InsufficientAccessRights'
    }

    It 'recognises a strong-authentication refusal that arrives with no response, as a Windows Server 2025 simple bind does' {
        $exception = [pscustomobject]@{ Message = 'Strong authentication is required for this operation.'; Response = $null }
        Get-LabDirectoryResultCode -Exception $exception | Should -Be 'StrongAuthRequired'
    }

    It 'recognises an access refusal that arrives with no response' {
        $exception = [pscustomobject]@{ Message = 'The user has insufficient access rights.'; Response = $null }
        Get-LabDirectoryResultCode -Exception $exception | Should -Be 'InsufficientAccessRights'
    }

    It 'copes with an exception that has no Response property at all' {
        Get-LabDirectoryResultCode -Exception ([pscustomobject]@{ Message = 'Strong authentication is required for this operation.' }) | Should -Be 'StrongAuthRequired'
    }

    It 'looks inside the wrapper PowerShell puts round an exception from a .NET method call such as Bind()' {
        $inner = [pscustomobject]@{ Message = 'Strong authentication is required for this operation.'; Response = $null; InnerException = $null }
        $wrapper = [pscustomobject]@{ Message = 'Exception calling "Bind" with "0" argument(s): "Strong authentication is required."'; InnerException = $inner }
        Get-LabDirectoryResultCode -Exception $wrapper | Should -Be 'StrongAuthRequired'
    }

    It 'returns nothing for an error it does not recognise, so a check cannot mistake it for a refusal' {
        Get-LabDirectoryResultCode -Exception ([pscustomobject]@{ Message = 'The LDAP server is unavailable.'; Response = $null }) | Should -BeNullOrEmpty
    }
}

Describe 'Get-LabAdProviderPath' {
    It 'puts the distinguished name on the AD: drive' {
        Get-LabAdProviderPath -DistinguishedName 'OU=Corp,DC=panoply,DC=local' | Should -Be 'AD:\OU=Corp,DC=panoply,DC=local'
    }

    It 'escapes wildcard characters, because Get-Acl -LiteralPath does not work on the AD: drive and -Path expands them' {
        Get-LabAdProviderPath -DistinguishedName 'OU=Team [A]*?,DC=panoply,DC=local' | Should -Be 'AD:\OU=Team `[A`]`*`?,DC=panoply,DC=local'
    }
}

Describe 'Test-LabEvaluationEdition' {
    It 'recognises the evaluation edition IDs Windows reports' {
        Test-LabEvaluationEdition -EditionId 'ServerDatacenterEval' | Should -BeTrue
        Test-LabEvaluationEdition -EditionId 'ServerStandardEval' | Should -BeTrue
    }

    It 'does not mistake a full edition for an evaluation one' {
        Test-LabEvaluationEdition -EditionId 'ServerDatacenter' | Should -BeFalse
        Test-LabEvaluationEdition -EditionId 'ServerStandard' | Should -BeFalse
    }
}

Describe 'ConvertTo-LabKeylessUnattend' {
    BeforeAll {
        $script:Template = Get-Content -LiteralPath $script:TemplatePath -Raw
        $script:KeylessValues = @{
            COMPUTER_NAME              = 'dc1'
            ADMIN_PASSWORD_ENCODED     = 'QUJD'
            AUTOLOGON_PASSWORD_ENCODED = 'REVG'
            IMAGE_NAME                 = 'Windows Server 2025 Datacenter Evaluation (Desktop Experience)'
            UI_LANGUAGE                = 'en-US'
            LOCALE                     = 'en-GB'
            TIME_ZONE                  = 'UTC'
        }
    }

    It 'removes both product key settings, so evaluation media installs with none' {
        $keyless = ConvertTo-LabKeylessUnattend -Template $script:Template
        Get-LabUnattendPlaceholder -Template $keyless | Should -Not -Contain 'PRODUCT_KEY'
        $keyless | Should -Not -Match '<ProductKey>'
    }

    It 'leaves a template that renders to well-formed XML from every other value' {
        $xml = ConvertTo-LabUnattendXml -Template (ConvertTo-LabKeylessUnattend -Template $script:Template) -Values $script:KeylessValues
        { [xml]$xml } | Should -Not -Throw
        $xml | Should -Match '<ComputerName>dc1</ComputerName>'
        $xml | Should -Match '<AcceptEula>true</AcceptEula>'
    }

    It 'throws when the template carries no product key, rather than silently doing nothing' {
        { ConvertTo-LabKeylessUnattend -Template '<unattend></unattend>' } | Should -Throw '*ProductKey*'
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

# ---------------------------------------------------------------------------------------------
# The monthly rebuild (host/Invoke-LabRebuild.ps1): names, settings, decisions, results, phases
# ---------------------------------------------------------------------------------------------

Describe 'Get-LabRebuildVmName' {
    It 'names the live, candidate and previous VM of a domain controller' {
        Get-LabRebuildVmName -LiveName 'dc-primary' -Kind Live | Should -Be 'dc-primary'
        Get-LabRebuildVmName -LiveName 'dc-primary' -Kind Candidate | Should -Be 'dc-primary-candidate'
        Get-LabRebuildVmName -LiveName 'dc-primary' -Kind Prev | Should -Be 'dc-primary-prev'
    }

    It 'refuses a live name that already carries a suffix, so a name is never doubled' {
        { Get-LabRebuildVmName -LiveName 'dc-primary-candidate' -Kind Candidate } | Should -Throw '*already ends in*'
        { Get-LabRebuildVmName -LiveName 'dc-primary-prev' -Kind Prev } | Should -Throw '*already ends in*'
    }

    It 'refuses a name Hyper-V cannot be given safely' {
        { Get-LabRebuildVmName -LiveName 'dc*' -Kind Live } | Should -Throw '*not a valid VM name*'
        { Get-LabRebuildVmName -LiveName 'dc primary' -Kind Live } | Should -Throw '*not a valid VM name*'
    }

    It 'refuses a live name with no room for its suffix' {
        $long = 'a' * 60
        Get-LabRebuildVmName -LiveName $long -Kind Live | Should -Be $long
        { Get-LabRebuildVmName -LiveName $long -Kind Candidate } | Should -Throw '*too long*'
    }
}

Describe 'Get-LabRebuildGenerationName' {
    It 'is rebuild- and the UTC date' {
        Get-LabRebuildGenerationName -NowUtc ([datetime]::new(2026, 9, 30, 23, 59, 0, [System.DateTimeKind]::Utc)) | Should -Be 'rebuild-20260930'
    }

    It 'converts a local time to UTC first' {
        $local = [datetime]::new(2026, 10, 1, 0, 30, 0, [System.DateTimeKind]::Utc).ToLocalTime()
        Get-LabRebuildGenerationName -NowUtc $local | Should -Be 'rebuild-20261001'
    }
}

Describe 'ConvertFrom-LabRebuildSetting' {
    BeforeAll {
        function Get-RebuildSetting {
            return @{
                isoPath                   = 'D:\media\WindowsServer2025.iso'
                cumulativeUpdateDirectory = 'D:\media\updates'
                vhdDirectory              = 'D:\Hyper-V\Virtual Hard Disks'
                switchName                = 'Lab'
                ntpServer                 = 'time.example.internal'
                dnsForwarder              = '10.0.0.1'
                domainControllers         = @{
                    'dc-primary' = @{ domain = 'PANOPLY.LOCAL'; ipAddress = '10.99.0.11'; prefixLength = 24; gateway = '10.99.0.1'; enableRecycleBin = $true }
                    'dc-source'  = @{ domain = 'RESURGAM.LOCAL'; ipAddress = '10.99.0.12'; prefixLength = 24; gateway = '10.99.0.1'; enableRecycleBin = $false }
                    'dc-target'  = @{ domain = 'GENTIAN.LOCAL'; ipAddress = '10.99.0.13'; prefixLength = 24; gateway = '10.99.0.1' }
                }
            }
        }
    }

    It 'maps the three domain controllers onto candidate builds that keep the live address' {
        $plan = ConvertFrom-LabRebuildSetting -Settings (Get-RebuildSetting) -GenerationName 'rebuild-20260930'

        @($plan.Roles).Count | Should -Be 3
        @($plan.Roles | ForEach-Object { $_.LiveName }) | Should -Be @('dc-primary', 'dc-source', 'dc-target')
        @($plan.Roles | ForEach-Object { $_.Role }) | Should -Be @('Primary', 'Source', 'Target')
        @($plan.Roles | ForEach-Object { $_.EnvironmentVariable }) | Should -Be @('JIM_AD_LAB_PRIMARY_VM', 'JIM_AD_LAB_SOURCE_VM', 'JIM_AD_LAB_TARGET_VM')

        $primary = $plan.Roles[0]
        $primary.CandidateName | Should -Be 'dc-primary-candidate'
        $primary.PrevName | Should -Be 'dc-primary-prev'
        $primary.BuildParameters.Name | Should -Be 'dc-primary-candidate'
        $primary.BuildParameters.Domain | Should -Be 'PANOPLY.LOCAL'
        $primary.BuildParameters.IPAddress | Should -Be '10.99.0.11'
        $primary.BuildParameters.PrefixLength | Should -Be 24
        $primary.BuildParameters.Gateway | Should -Be '10.99.0.1'
        $primary.BuildParameters.IsoPath | Should -Be 'D:\media\WindowsServer2025.iso'
        $primary.BuildParameters.SwitchName | Should -Be 'Lab'
        $primary.BuildParameters.NtpServer | Should -Be 'time.example.internal'
        $primary.BuildParameters.DnsForwarder | Should -Be '10.0.0.1'
    }

    It 'builds each candidate beneath a fresh folder of the generation, never over an existing disk' {
        $plan = ConvertFrom-LabRebuildSetting -Settings (Get-RebuildSetting) -GenerationName 'rebuild-20260930'
        $plan.Generation | Should -Be 'rebuild-20260930'
        $plan.Roles[0].BuildParameters.VhdDirectory | Should -Be 'D:\Hyper-V\Virtual Hard Disks\rebuild-20260930'
    }

    It 'passes -EnableRecycleBin only for a domain controller that has it on' {
        $plan = ConvertFrom-LabRebuildSetting -Settings (Get-RebuildSetting) -GenerationName 'g1'
        $plan.Roles[0].BuildParameters.ContainsKey('EnableRecycleBin') | Should -BeTrue
        $plan.Roles[0].BuildParameters.EnableRecycleBin | Should -BeTrue
        $plan.Roles[1].BuildParameters.ContainsKey('EnableRecycleBin') | Should -BeFalse
        $plan.Roles[2].BuildParameters.ContainsKey('EnableRecycleBin') | Should -BeFalse
    }

    It 'passes the cumulative update to every build when there is one, and to none when there is not' {
        $with = ConvertFrom-LabRebuildSetting -Settings (Get-RebuildSetting) -GenerationName 'g1' -CumulativeUpdatePath 'D:\media\updates\kb1.msu'
        @($with.Roles | ForEach-Object { $_.BuildParameters.CumulativeUpdatePath }) | Should -Be @('D:\media\updates\kb1.msu', 'D:\media\updates\kb1.msu', 'D:\media\updates\kb1.msu')

        $without = ConvertFrom-LabRebuildSetting -Settings (Get-RebuildSetting) -GenerationName 'g1'
        foreach ($role in $without.Roles) { $role.BuildParameters.ContainsKey('CumulativeUpdatePath') | Should -BeFalse }
    }

    It 'reads settings parsed from JSON as well as a hashtable' {
        $json = (Get-RebuildSetting | ConvertTo-Json -Depth 6) | ConvertFrom-Json
        $plan = ConvertFrom-LabRebuildSetting -Settings $json -GenerationName 'g1'
        $plan.Roles[1].BuildParameters.Domain | Should -Be 'RESURGAM.LOCAL'
        $plan.Roles[0].BuildParameters.EnableRecycleBin | Should -BeTrue
    }

    It 'names the missing setting: <Key>' -TestCases @(
        @{ Key = 'isoPath' }, @{ Key = 'vhdDirectory' }, @{ Key = 'switchName' }, @{ Key = 'ntpServer' }
    ) {
        $settings = Get-RebuildSetting
        $settings.Remove($Key)
        { ConvertFrom-LabRebuildSetting -Settings $settings -GenerationName 'g1' } | Should -Throw "*'$Key'*"
    }

    It 'passes the DNS forwarder on when there is one, and leaves it out when there is none (the lab has no uplink)' {
        $with = ConvertFrom-LabRebuildSetting -Settings (Get-RebuildSetting) -GenerationName 'g1'
        $with.Roles[0].BuildParameters.DnsForwarder | Should -Be '10.0.0.1'

        foreach ($empty in $null, '', '  ') {
            $settings = Get-RebuildSetting
            $settings.dnsForwarder = $empty
            $plan = ConvertFrom-LabRebuildSetting -Settings $settings -GenerationName 'g1'
            foreach ($role in $plan.Roles) { $role.BuildParameters.ContainsKey('DnsForwarder') | Should -BeFalse }
        }

        $absent = Get-RebuildSetting
        $absent.Remove('dnsForwarder')
        foreach ($role in (ConvertFrom-LabRebuildSetting -Settings $absent -GenerationName 'g1').Roles) { $role.BuildParameters.ContainsKey('DnsForwarder') | Should -BeFalse }
    }

    It 'refuses a DNS forwarder that is not an IPv4 address' {
        $settings = Get-RebuildSetting
        $settings.dnsForwarder = 'dns.example.internal'
        { ConvertFrom-LabRebuildSetting -Settings $settings -GenerationName 'g1' } | Should -Throw "*'dnsForwarder'*not an IPv4 address*"
    }

    It 'refuses a domain controller missing from the settings' {
        $settings = Get-RebuildSetting
        $settings.domainControllers.Remove('dc-target')
        { ConvertFrom-LabRebuildSetting -Settings $settings -GenerationName 'g1' } | Should -Throw "*no 'dc-target'*"
    }

    It 'refuses a domain controller the lab does not have, because the runner only reads three VM variables' {
        $settings = Get-RebuildSetting
        $settings.domainControllers['dc-extra'] = @{ domain = 'EXTRA.LOCAL'; ipAddress = '10.99.0.14'; prefixLength = 24; gateway = '10.99.0.1' }
        { ConvertFrom-LabRebuildSetting -Settings $settings -GenerationName 'g1' } | Should -Throw '*dc-extra*'
    }

    It 'refuses two domain controllers with one address' {
        $settings = Get-RebuildSetting
        $settings.domainControllers['dc-source'].ipAddress = '10.99.0.11'
        { ConvertFrom-LabRebuildSetting -Settings $settings -GenerationName 'g1' } | Should -Throw '*already has*'
    }

    It 'refuses two domain controllers with one domain' {
        $settings = Get-RebuildSetting
        $settings.domainControllers['dc-source'].domain = 'panoply.local'
        { ConvertFrom-LabRebuildSetting -Settings $settings -GenerationName 'g1' } | Should -Throw '*domain*already has*'
    }

    It 'refuses an address that is not IPv4: <Value>' -TestCases @(
        @{ Value = 'dc1' }, @{ Value = '10.99.0' }, @{ Value = '10' }, @{ Value = '10.99.0.11.5' }, @{ Value = '999.1.1.1' }
    ) {
        $settings = Get-RebuildSetting
        $settings.domainControllers['dc-primary'].ipAddress = $Value
        { ConvertFrom-LabRebuildSetting -Settings $settings -GenerationName 'g1' } | Should -Throw '*not an IPv4 address*'
    }

    It 'refuses a prefix length outside 8 to 30' {
        $settings = Get-RebuildSetting
        $settings.domainControllers['dc-primary'].prefixLength = 31
        { ConvertFrom-LabRebuildSetting -Settings $settings -GenerationName 'g1' } | Should -Throw '*prefixLength*'
    }

    It 'refuses a domain that is not a DNS name' {
        $settings = Get-RebuildSetting
        $settings.domainControllers['dc-primary'].domain = 'PANOPLY'
        { ConvertFrom-LabRebuildSetting -Settings $settings -GenerationName 'g1' } | Should -Throw '*not a DNS domain name*'
    }

    It 'refuses to build without a generation name' {
        { ConvertFrom-LabRebuildSetting -Settings (Get-RebuildSetting) } | Should -Throw '*generation name*'
    }

    It 'gives just the names, whatever the settings hold, with -NamesOnly, so a rollback is never blocked by a bad settings file' {
        $plan = ConvertFrom-LabRebuildSetting -Settings @{ isoPath = '' } -NamesOnly
        @($plan.Roles | ForEach-Object { $_.LiveName }) | Should -Be @('dc-primary', 'dc-source', 'dc-target')
        @($plan.Roles | ForEach-Object { $_.CandidateName }) | Should -Be @('dc-primary-candidate', 'dc-source-candidate', 'dc-target-candidate')
        @($plan.Roles | ForEach-Object { $_.PrevName }) | Should -Be @('dc-primary-prev', 'dc-source-prev', 'dc-target-prev')
        $plan.Roles[0].BuildParameters | Should -BeNullOrEmpty
        $plan.VhdDirectory | Should -BeNullOrEmpty
        (ConvertFrom-LabRebuildSetting -Settings $null -NamesOnly).Roles.Count | Should -Be 3
    }

    It 'still reports the VHD directory with -NamesOnly when the settings have one' {
        (ConvertFrom-LabRebuildSetting -Settings (Get-RebuildSetting) -NamesOnly).VhdDirectory | Should -Be 'D:\Hyper-V\Virtual Hard Disks'
    }
}

Describe 'Get-LabNewestCumulativeUpdate' {
    BeforeAll {
        function Get-FakeFile {
            param([string]$Name, [datetime]$WrittenUtc)
            return [pscustomobject]@{ Name = $Name; FullName = "D:\media\updates\$Name"; LastWriteTimeUtc = $WrittenUtc }
        }
    }

    It 'picks the newest .msu by modification time' {
        $files = @(
            (Get-FakeFile 'old.msu' ([datetime]::new(2026, 8, 1, 0, 0, 0, [System.DateTimeKind]::Utc))),
            (Get-FakeFile 'new.msu' ([datetime]::new(2026, 9, 10, 0, 0, 0, [System.DateTimeKind]::Utc))),
            (Get-FakeFile 'mid.msu' ([datetime]::new(2026, 8, 20, 0, 0, 0, [System.DateTimeKind]::Utc)))
        )
        (Get-LabNewestCumulativeUpdate -File $files).Name | Should -Be 'new.msu'
    }

    It 'ignores anything that is not an .msu, even when newer' {
        $files = @(
            (Get-FakeFile 'kb.msu' ([datetime]::new(2026, 8, 1, 0, 0, 0, [System.DateTimeKind]::Utc))),
            (Get-FakeFile 'notes.txt' ([datetime]::new(2026, 9, 10, 0, 0, 0, [System.DateTimeKind]::Utc))),
            (Get-FakeFile 'kb.msu.bak' ([datetime]::new(2026, 9, 11, 0, 0, 0, [System.DateTimeKind]::Utc)))
        )
        (Get-LabNewestCumulativeUpdate -File $files).Name | Should -Be 'kb.msu'
    }

    It 'is case-insensitive about the extension and breaks a tie by name' {
        $when = [datetime]::new(2026, 9, 1, 0, 0, 0, [System.DateTimeKind]::Utc)
        $files = @((Get-FakeFile 'a.MSU' $when), (Get-FakeFile 'b.msu' $when))
        (Get-LabNewestCumulativeUpdate -File $files).Name | Should -Be 'b.msu'
    }

    It 'returns nothing for an empty or null list, or one with no update in it' {
        Get-LabNewestCumulativeUpdate -File @() | Should -BeNullOrEmpty
        Get-LabNewestCumulativeUpdate -File $null | Should -BeNullOrEmpty
        Get-LabNewestCumulativeUpdate -File @((Get-FakeFile 'readme.txt' ([datetime]::UtcNow))) | Should -BeNullOrEmpty
    }
}

Describe 'Get-LabRebuildBuildAction' {
    BeforeAll {
        $script:BuildNow = [datetime]::new(2026, 9, 30, 12, 0, 0, [System.DateTimeKind]::Utc)
    }

    It 'builds when there is no candidate' {
        Get-LabRebuildBuildAction -CandidateExists $false -NowUtc $script:BuildNow | Should -Be 'Build'
    }

    It 'reuses a recent candidate that has its baseline (a retry, or this run''s own)' {
        Get-LabRebuildBuildAction -CandidateExists $true -CandidateCreatedUtc $script:BuildNow.AddHours(-3) -CandidateHasBaseline $true -NowUtc $script:BuildNow | Should -Be 'Reuse'
    }

    It 'resumes a recent candidate whose build stopped before the baseline' {
        Get-LabRebuildBuildAction -CandidateExists $true -CandidateCreatedUtc $script:BuildNow.AddHours(-3) -CandidateHasBaseline $false -NowUtc $script:BuildNow | Should -Be 'Resume'
    }

    It 'replaces a candidate older than the age limit, so last month''s red leftover is never mistaken for this month''s' {
        Get-LabRebuildBuildAction -CandidateExists $true -CandidateCreatedUtc $script:BuildNow.AddDays(-30) -CandidateHasBaseline $true -NowUtc $script:BuildNow | Should -Be 'Replace'
        Get-LabRebuildBuildAction -CandidateExists $true -CandidateCreatedUtc $script:BuildNow.AddDays(-30) -CandidateHasBaseline $false -NowUtc $script:BuildNow | Should -Be 'Replace'
    }

    It 'treats exactly the age limit as still recent, and one hour more as stale' {
        Get-LabRebuildBuildAction -CandidateExists $true -CandidateCreatedUtc $script:BuildNow.AddHours(-24) -CandidateHasBaseline $true -NowUtc $script:BuildNow | Should -Be 'Reuse'
        Get-LabRebuildBuildAction -CandidateExists $true -CandidateCreatedUtc $script:BuildNow.AddHours(-25) -CandidateHasBaseline $true -NowUtc $script:BuildNow | Should -Be 'Replace'
    }

    It 'honours a different age limit' {
        Get-LabRebuildBuildAction -CandidateExists $true -CandidateCreatedUtc $script:BuildNow.AddHours(-30) -CandidateHasBaseline $true -NowUtc $script:BuildNow -MaxAgeHours 48 | Should -Be 'Reuse'
    }

    It 'replaces a recent candidate when asked for a fresh one' {
        Get-LabRebuildBuildAction -CandidateExists $true -CandidateCreatedUtc $script:BuildNow.AddHours(-1) -CandidateHasBaseline $true -NowUtc $script:BuildNow -Fresh | Should -Be 'Replace'
    }

    It 'does not mind a candidate stamped slightly in the future (clock skew)' {
        Get-LabRebuildBuildAction -CandidateExists $true -CandidateCreatedUtc $script:BuildNow.AddMinutes(5) -CandidateHasBaseline $true -NowUtc $script:BuildNow | Should -Be 'Reuse'
    }
}

Describe 'Get-LabPromotePlan' {
    It 'demotes the live VM and promotes the candidate: live=<Live> candidate=<Candidate> prev=<Prev>' -TestCases @(
        @{ Live = $true; Candidate = $true; Prev = $false; Expected = @('RenameLiveToPrev', 'RenameCandidateToLive') }
        @{ Live = $true; Candidate = $true; Prev = $true; Expected = @('RemovePrev', 'RenameLiveToPrev', 'RenameCandidateToLive') }
        @{ Live = $false; Candidate = $true; Prev = $true; Expected = @('RenameCandidateToLive') }
        @{ Live = $true; Candidate = $false; Prev = $false; Expected = @() }
        @{ Live = $true; Candidate = $false; Prev = $true; Expected = @() }
    ) {
        $plan = @(Get-LabPromotePlan -LiveExists $Live -CandidateExists $Candidate -PrevExists $Prev)
        $plan.Count | Should -Be $Expected.Count
        for ($index = 0; $index -lt $Expected.Count; $index++) { $plan[$index] | Should -Be $Expected[$index] }
    }

    It 'throws when there is nothing to keep: live=<Live> candidate=<Candidate> prev=<Prev>' -TestCases @(
        @{ Live = $false; Candidate = $false; Prev = $false }
        @{ Live = $false; Candidate = $false; Prev = $true }
        @{ Live = $false; Candidate = $true; Prev = $false }
    ) {
        { Get-LabPromotePlan -LiveExists $Live -CandidateExists $Candidate -PrevExists $Prev } | Should -Throw
    }

    It 'never renames the candidate before the live VM has stepped aside' {
        foreach ($prev in $true, $false) {
            $plan = @(Get-LabPromotePlan -LiveExists $true -CandidateExists $true -PrevExists $prev)
            [array]::IndexOf($plan, 'RenameLiveToPrev') | Should -BeLessThan ([array]::IndexOf($plan, 'RenameCandidateToLive'))
        }
    }
}

Describe 'Get-LabRollbackPlan' {
    It 'stops the candidate and starts the live VM: live=<Live> candidate=<Candidate> prev=<Prev>' -TestCases @(
        @{ Live = $true; Candidate = $true; Prev = $false; Expected = @('StopCandidate', 'StartLive') }
        @{ Live = $true; Candidate = $true; Prev = $true; Expected = @('StopCandidate', 'StartLive') }
        @{ Live = $true; Candidate = $false; Prev = $false; Expected = @('StartLive') }
        @{ Live = $false; Candidate = $true; Prev = $true; Expected = @('StopCandidate', 'RenamePrevToLive', 'StartLive') }
        @{ Live = $false; Candidate = $false; Prev = $true; Expected = @('RenamePrevToLive', 'StartLive') }
    ) {
        $plan = @(Get-LabRollbackPlan -LiveExists $Live -CandidateExists $Candidate -PrevExists $Prev)
        $plan.Count | Should -Be $Expected.Count
        for ($index = 0; $index -lt $Expected.Count; $index++) { $plan[$index] | Should -Be $Expected[$index] }
    }

    It 'throws when there is no live VM and no previous one to put back' {
        { Get-LabRollbackPlan -LiveExists $false -CandidateExists $true -PrevExists $false } | Should -Throw '*by hand*'
        { Get-LabRollbackPlan -LiveExists $false -CandidateExists $false -PrevExists $false } | Should -Throw '*by hand*'
    }
}

Describe 'Get-LabPrunePlan' {
    BeforeAll {
        $script:PruneNow = [datetime]::new(2026, 9, 30, 12, 0, 0, [System.DateTimeKind]::Utc)
    }

    It 'removes a previous VM demoted 7 or more days ago and keeps a younger one' {
        $prev = @(
            [pscustomobject]@{ Name = 'dc-primary-prev'; DemotedUtc = $script:PruneNow.AddDays(-7) },
            [pscustomobject]@{ Name = 'dc-source-prev'; DemotedUtc = $script:PruneNow.AddDays(-7).AddMinutes(1) },
            [pscustomobject]@{ Name = 'dc-target-prev'; DemotedUtc = $script:PruneNow.AddDays(-30) }
        )
        $plan = @(Get-LabPrunePlan -Prev $prev -NowUtc $script:PruneNow)
        $plan.Count | Should -Be 3
        $plan[0].Remove | Should -BeTrue
        $plan[1].Remove | Should -BeFalse
        $plan[2].Remove | Should -BeTrue
        $plan[2].Reason | Should -BeLike '*30 days*'
    }

    It 'keeps a previous VM whose age is unknown rather than guess' {
        $plan = @(Get-LabPrunePlan -Prev @([pscustomobject]@{ Name = 'dc-primary-prev'; DemotedUtc = $null }) -NowUtc $script:PruneNow)
        $plan[0].Remove | Should -BeFalse
        $plan[0].Reason | Should -BeLike '*age is unknown*'
    }

    It 'honours a different retention' {
        $prev = @([pscustomobject]@{ Name = 'dc-primary-prev'; DemotedUtc = $script:PruneNow.AddDays(-3) })
        @(Get-LabPrunePlan -Prev $prev -NowUtc $script:PruneNow -MinAgeDays 2)[0].Remove | Should -BeTrue
        @(Get-LabPrunePlan -Prev $prev -NowUtc $script:PruneNow -MinAgeDays 4)[0].Remove | Should -BeFalse
    }

    It 'plans nothing for nothing' {
        @(Get-LabPrunePlan -Prev @() -NowUtc $script:PruneNow).Count | Should -Be 0
    }
}

Describe 'Get-LabVmNoteValue and Set-LabVmNoteValue' {
    BeforeAll {
        $script:Note = ConvertTo-LabVmNote -Domain 'PANOPLY.LOCAL' -ShortName 'dc1' -NetBiosName 'PANOPLY' -IPAddress '10.99.0.11'
    }

    It 'reads a key that is there and nothing for one that is not' {
        Get-LabVmNoteValue -Notes $script:Note -Key 'ip' | Should -Be '10.99.0.11'
        Get-LabVmNoteValue -Notes $script:Note -Key 'demoted' | Should -BeNullOrEmpty
    }

    It 'reads nothing from Notes that carry no lab marker' {
        Get-LabVmNoteValue -Notes "ip=10.0.0.1" -Key 'ip' | Should -BeNullOrEmpty
        Get-LabVmNoteValue -Notes '' -Key 'ip' | Should -BeNullOrEmpty
    }

    It 'adds a line and keeps the marker and the other lines, so the other scripts still read the Notes' {
        $stamped = Set-LabVmNoteValue -Notes $script:Note -Key 'demoted' -Value '2026-09-30T04:00:00Z'
        Test-LabVmNote -Notes $stamped | Should -BeTrue
        Get-LabVmNoteValue -Notes $stamped -Key 'demoted' | Should -Be '2026-09-30T04:00:00Z'
        $meta = ConvertFrom-LabVmNote -Notes $stamped
        $meta.Domain | Should -Be 'PANOPLY.LOCAL'
        $meta.NetBiosName | Should -Be 'PANOPLY'
        $meta.ComputerName | Should -Be 'dc1'
        $meta.IPAddress | Should -Be '10.99.0.11'
    }

    It 'replaces a line rather than adding a second one' {
        $first = Set-LabVmNoteValue -Notes $script:Note -Key 'demoted' -Value 'one'
        $second = Set-LabVmNoteValue -Notes $first -Key 'demoted' -Value 'two'
        @($second -split "`n" | Where-Object { $_ -like 'demoted=*' }).Count | Should -Be 1
        Get-LabVmNoteValue -Notes $second -Key 'demoted' | Should -Be 'two'
    }

    It 'removes a line when the value is empty' {
        $stamped = Set-LabVmNoteValue -Notes $script:Note -Key 'demoted' -Value 'one'
        $cleared = Set-LabVmNoteValue -Notes $stamped -Key 'demoted' -Value ''
        $cleared | Should -Be $script:Note
    }

    It 'refuses to edit Notes that carry no lab marker, or a value with a line break' {
        { Set-LabVmNoteValue -Notes 'something else' -Key 'demoted' -Value 'x' } | Should -Throw '*no lab marker*'
        { Set-LabVmNoteValue -Notes $script:Note -Key 'demoted' -Value "a`nb" } | Should -Throw '*span lines*'
    }
}

Describe 'ConvertTo-LabChildArgument' {
    It 'turns a hashtable into named arguments, in a stable order' {
        $arguments = ConvertTo-LabChildArgument -Parameter @{ Name = 'dc-primary-candidate'; PrefixLength = 24; IPAddress = '10.99.0.11' }
        $arguments | Should -Be @('-IPAddress', '10.99.0.11', '-Name', 'dc-primary-candidate', '-PrefixLength', '24')
    }

    It 'passes a true switch as a bare flag and leaves a false one out' {
        $arguments = ConvertTo-LabChildArgument -Parameter @{ EnableRecycleBin = $true; Force = $false; Name = 'x' }
        $arguments | Should -Be @('-EnableRecycleBin', '-Name', 'x')
    }

    It 'keeps a value with spaces as one argument' {
        $arguments = ConvertTo-LabChildArgument -Parameter @{ VhdDirectory = 'D:\Hyper-V\Virtual Hard Disks\g1' }
        $arguments | Should -Be @('-VhdDirectory', 'D:\Hyper-V\Virtual Hard Disks\g1')
    }

    It 'refuses a bad parameter name, a list, a null and a line break' {
        { ConvertTo-LabChildArgument -Parameter @{ 'Name; calc' = 'x' } } | Should -Throw '*not a parameter name*'
        { ConvertTo-LabChildArgument -Parameter @{ Name = @('a', 'b') } } | Should -Throw '*single value*'
        { ConvertTo-LabChildArgument -Parameter @{ Name = $null } } | Should -Throw '*single value*'
        { ConvertTo-LabChildArgument -Parameter @{ Name = "a`nb" } } | Should -Throw '*line break*'
    }

    It 'accepts what a real plan produces' {
        $settings = @{
            isoPath = 'D:\iso.iso'; vhdDirectory = 'D:\vm'; switchName = 'Lab'; ntpServer = 'ntp'; dnsForwarder = '10.0.0.1'
            domainControllers = @{
                'dc-primary' = @{ domain = 'PANOPLY.LOCAL'; ipAddress = '10.99.0.11'; prefixLength = 24; gateway = '10.99.0.1'; enableRecycleBin = $true }
                'dc-source'  = @{ domain = 'RESURGAM.LOCAL'; ipAddress = '10.99.0.12'; prefixLength = 24; gateway = '10.99.0.1' }
                'dc-target'  = @{ domain = 'GENTIAN.LOCAL'; ipAddress = '10.99.0.13'; prefixLength = 24; gateway = '10.99.0.1' }
            }
        }
        $plan = ConvertFrom-LabRebuildSetting -Settings $settings -GenerationName 'g1' -CumulativeUpdatePath 'D:\u\kb.msu'
        $arguments = ConvertTo-LabChildArgument -Parameter $plan.Roles[0].BuildParameters
        $arguments | Should -Contain '-EnableRecycleBin'
        $arguments | Should -Contain '-CumulativeUpdatePath'
        $arguments | Should -Contain 'dc-primary-candidate'
    }
}

Describe 'Test-LabOwnedVmFolder' {
    It 'accepts a folder named for a lab VM beneath the VHD directory, with either slash and any case' {
        $names = 'dc-primary', 'dc-primary-candidate', 'dc-primary-prev'
        Test-LabOwnedVmFolder -Path 'D:\Hyper-V\Virtual Hard Disks\rebuild-20260930\dc-primary-candidate' -Root 'D:\Hyper-V\Virtual Hard Disks' -Name $names | Should -BeTrue
        Test-LabOwnedVmFolder -Path 'D:\Hyper-V\Virtual Hard Disks\dc-primary' -Root 'D:\Hyper-V\Virtual Hard Disks' -Name $names | Should -BeTrue
        Test-LabOwnedVmFolder -Path 'd:/hyper-v/virtual hard disks/DC-Primary-Prev/' -Root 'D:\Hyper-V\Virtual Hard Disks\' -Name $names | Should -BeTrue
    }

    It 'refuses the VHD directory itself, a folder outside it and a folder named for something else' {
        $names = 'dc-primary'
        Test-LabOwnedVmFolder -Path 'D:\Hyper-V\Virtual Hard Disks' -Root 'D:\Hyper-V\Virtual Hard Disks' -Name $names | Should -BeFalse
        Test-LabOwnedVmFolder -Path 'D:\Other\dc-primary' -Root 'D:\Hyper-V\Virtual Hard Disks' -Name $names | Should -BeFalse
        Test-LabOwnedVmFolder -Path 'D:\Hyper-V\Virtual Hard Disks\backups' -Root 'D:\Hyper-V\Virtual Hard Disks' -Name $names | Should -BeFalse
        Test-LabOwnedVmFolder -Path 'D:\Hyper-V\Virtual Hard Disks-old\dc-primary' -Root 'D:\Hyper-V\Virtual Hard Disks' -Name $names | Should -BeFalse
    }

    It 'refuses a path that climbs out with dot segments, and an empty root' {
        Test-LabOwnedVmFolder -Path 'D:\Hyper-V\Virtual Hard Disks\..\..\dc-primary' -Root 'D:\Hyper-V\Virtual Hard Disks' -Name 'dc-primary' | Should -BeFalse
        Test-LabOwnedVmFolder -Path 'D:\dc-primary' -Root '\' -Name 'dc-primary' | Should -BeFalse
    }
}

Describe 'Get-LabRunSummary and Get-LabRunStatusDescription' {
    BeforeAll {
        function Get-ResultsFolder {
            $folder = Join-Path $TestDrive ([guid]::NewGuid().ToString('N'))
            New-Item -ItemType Directory -Path $folder | Out-Null
            return $folder
        }

        function Write-Regression {
            param([string]$Folder, [string]$Name, [object[]]$Scenarios, $Builds)
            $report = [ordered]@{ Mode = 'FullRegression'; DirectoryType = 'ActiveDirectory'; Scenarios = @($Scenarios) }
            if ($null -ne $Builds) { $report.DomainControllerBuilds = $Builds }
            $report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $Folder $Name)
        }
    }

    It 'counts a full run from its regression report and reads the builds' {
        $folder = Get-ResultsFolder
        Write-Regression -Folder $folder -Name 'full-regression-2026-09-30_020000.json' -Builds ([ordered]@{ 'dc-primary' = '26100.4652'; 'dc-source' = '26100.4652' }) -Scenarios @(
            @{ Name = 'Scenario-001'; Success = $true; Skipped = $false },
            @{ Name = 'Scenario-002'; Success = $true; Skipped = $false },
            @{ Name = 'Scenario-003'; Success = $false; Skipped = $false },
            @{ Name = 'Scenario-004'; Success = $false; Skipped = $true }
        )
        $summary = Get-LabRunSummary -ResultsDirectory $folder
        $summary.Total | Should -Be 4
        $summary.Passed | Should -Be 2
        $summary.Failed | Should -Be 1
        $summary.Skipped | Should -Be 1
        @($summary.FailedScenarios) | Should -Be @('Scenario-003')
        $summary.Builds['dc-primary'] | Should -Be '26100.4652'
        $summary.Builds.Count | Should -Be 2
        $summary.Report | Should -BeLike '*full-regression-2026-09-30_020000.json'
    }

    It 'uses the newest regression report when there are several' {
        $folder = Get-ResultsFolder
        Write-Regression -Folder $folder -Name 'full-regression-old.json' -Scenarios @(@{ Success = $false; Skipped = $false })
        (Get-Item (Join-Path $folder 'full-regression-old.json')).LastWriteTimeUtc = [datetime]::new(2026, 1, 1, 0, 0, 0, [System.DateTimeKind]::Utc)
        Write-Regression -Folder $folder -Name 'full-regression-new.json' -Scenarios @(@{ Success = $true; Skipped = $false }, @{ Success = $true; Skipped = $false })
        $summary = Get-LabRunSummary -ResultsDirectory $folder
        $summary.Total | Should -Be 2
        $summary.Passed | Should -Be 2
    }

    It 'counts a single-scenario run as one scenario and reads its builds from the performance file' {
        $folder = Get-ResultsFolder
        $performance = Join-Path (Join-Path $folder 'performance') 'runner01'
        New-Item -ItemType Directory -Path $performance -Force | Out-Null
        [ordered]@{ Scenario = 'Scenario-001'; DomainControllerBuilds = [ordered]@{ 'dc-primary' = '26100.1742' } } |
            ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $performance 'Scenario-001-Medium-2026-09-30_020000.json')

        $passing = Get-LabRunSummary -ResultsDirectory $folder -Success $true
        $passing.Total | Should -Be 1
        $passing.Passed | Should -Be 1
        $passing.Builds['dc-primary'] | Should -Be '26100.1742'

        $failing = Get-LabRunSummary -ResultsDirectory $folder -Success $false
        $failing.Total | Should -Be 1
        $failing.Passed | Should -Be 0
        $failing.Failed | Should -Be 1
    }

    It 'falls back to the performance file for the builds when the report has none' {
        $folder = Get-ResultsFolder
        Write-Regression -Folder $folder -Name 'full-regression-a.json' -Scenarios @(@{ Success = $true; Skipped = $false })
        $performance = Join-Path (Join-Path $folder 'performance') 'runner01'
        New-Item -ItemType Directory -Path $performance -Force | Out-Null
        [ordered]@{ DomainControllerBuilds = [ordered]@{ 'dc-primary' = '26100.1' } } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $performance 'x.json')
        (Get-LabRunSummary -ResultsDirectory $folder).Builds['dc-primary'] | Should -Be '26100.1'
    }

    It 'reports nothing for a folder with no results, or one that is not there' {
        $empty = Get-LabRunSummary -ResultsDirectory (Get-ResultsFolder)
        $empty.Total | Should -Be 0
        $empty.Builds.Count | Should -Be 0
        (Get-LabRunSummary -ResultsDirectory (Join-Path $TestDrive 'missing')).Total | Should -Be 0
    }

    It 'describes a green run in one short line' {
        $summary = [pscustomobject]@{ Total = 23; Passed = 23; Skipped = 0; Builds = [ordered]@{ 'dc-primary' = '26100.4652'; 'dc-source' = '26100.4652'; 'dc-target' = '26100.4652' } }
        Get-LabRunStatusDescription -Summary $summary | Should -Be '23/23 scenarios passed; DC build 26100.4652'
    }

    It 'says so when the domain controllers are on different builds, and lists each once' {
        $summary = [pscustomobject]@{ Total = 2; Passed = 1; Skipped = 0; Builds = [ordered]@{ 'a' = '26100.2'; 'b' = '26100.1'; 'c' = '26100.2' } }
        Get-LabRunStatusDescription -Summary $summary | Should -Be '1/2 scenarios passed; DC builds 26100.1, 26100.2'
    }

    It 'mentions skipped scenarios, and uses the singular for one' {
        Get-LabRunStatusDescription -Summary ([pscustomobject]@{ Total = 5; Passed = 2; Skipped = 2; Builds = [ordered]@{ 'a' = '26100.1' } }) | Should -Be '2/5 scenarios passed, 2 skipped; DC build 26100.1'
        Get-LabRunStatusDescription -Summary ([pscustomobject]@{ Total = 1; Passed = 1; Skipped = 0; Builds = [ordered]@{ 'a' = '26100.1' } }) | Should -Be '1/1 scenario passed; DC build 26100.1'
    }

    It 'says the build is unknown when it is, and that nothing ran when nothing did' {
        Get-LabRunStatusDescription -Summary ([pscustomobject]@{ Total = 3; Passed = 3; Skipped = 0; Builds = [ordered]@{ 'a' = 'unknown' } }) | Should -Be '3/3 scenarios passed; DC build unknown'
        Get-LabRunStatusDescription -Summary ([pscustomobject]@{ Total = 0; Passed = 0; Skipped = 0; Builds = [ordered]@{} }) | Should -Be 'no scenarios ran; DC build unknown'
    }

    It 'never exceeds the 140 characters GitHub allows, cutting with an ellipsis' {
        $builds = [ordered]@{}
        1..30 | ForEach-Object { $builds["dc$_"] = "26100.$($_ * 1111)" }
        $text = Get-LabRunStatusDescription -Summary ([pscustomobject]@{ Total = 9; Passed = 9; Skipped = 0; Builds = $builds })
        $text.Length | Should -BeLessOrEqual 140
        $text | Should -BeLike '*...'
    }
}

Describe 'Invoke-LabRebuildPhase' {
    BeforeAll {
        function Get-Utc {
            param([int]$Day, [int]$Hour = 4)
            return [datetime]::new(2026, 9, $Day, $Hour, 0, 0, [System.DateTimeKind]::Utc)
        }

        function Get-FakePlan {
            $settings = @{
                isoPath = 'D:\iso.iso'; vhdDirectory = 'D:\vm'; switchName = 'Lab'; ntpServer = 'ntp'; dnsForwarder = '10.0.0.1'
                domainControllers = @{
                    'dc-primary' = @{ domain = 'PANOPLY.LOCAL'; ipAddress = '10.99.0.11'; prefixLength = 24; gateway = '10.99.0.1'; enableRecycleBin = $true }
                    'dc-source'  = @{ domain = 'RESURGAM.LOCAL'; ipAddress = '10.99.0.12'; prefixLength = 24; gateway = '10.99.0.1' }
                    'dc-target'  = @{ domain = 'GENTIAN.LOCAL'; ipAddress = '10.99.0.13'; prefixLength = 24; gateway = '10.99.0.1' }
                }
            }
            return (ConvertFrom-LabRebuildSetting -Settings $settings -GenerationName 'rebuild-20260930')
        }

        # An in-memory Hyper-V: VMs by name, every adapter call recorded in order, and the address rule that
        # makes the ordering matter (two running VMs cannot share an address).
        function Get-FakeLab {
            param([datetime]$Now = (Get-Utc -Day 30))

            $lab = @{
                Vms           = @{}
                Calls         = (New-Object System.Collections.ArrayList)
                Now           = $Now
                FailBuildFor  = $null
                FailRestoreOf = $null
                Builds        = @{}
                BuildIndex    = 0
            }
            $lab.Adapter = @{
                GetVm         = { param($Name) if ($lab.Vms.ContainsKey($Name)) { return $lab.Vms[$Name] } return $null }.GetNewClosure()
                StopVm        = { param($Name) [void]$lab.Calls.Add("StopVm $Name"); $lab.Vms[$Name].State = 'Off' }.GetNewClosure()
                RestoreVm     = {
                    param($Name, $Checkpoint)
                    [void]$lab.Calls.Add("RestoreVm $Name $Checkpoint")
                    if ($lab.FailRestoreOf -eq $Name) { throw "simulated restore failure of $Name" }
                    if (-not $lab.Vms[$Name].HasBaseline) { throw "no checkpoint $Checkpoint on $Name" }
                    $address = (Get-LabVmNoteValue -Notes $lab.Vms[$Name].Notes -Key 'ip')
                    foreach ($other in $lab.Vms.Values) {
                        if (($other.Name -ne $Name) -and ($other.State -eq 'Running') -and ((Get-LabVmNoteValue -Notes $other.Notes -Key 'ip') -eq $address)) { throw "address $address is in use by $($other.Name)" }
                    }
                    $lab.Vms[$Name].State = 'Running'
                }.GetNewClosure()
                RenameVm      = {
                    param($Name, $NewName)
                    [void]$lab.Calls.Add("RenameVm $Name $NewName")
                    if ($lab.Vms.ContainsKey($NewName)) { throw "a VM called $NewName already exists" }
                    $lab.Vms[$NewName] = $lab.Vms[$Name]
                    $lab.Vms[$NewName].Name = $NewName
                    $lab.Vms.Remove($Name)
                }.GetNewClosure()
                SetNotes      = { param($Name, $Notes) [void]$lab.Calls.Add("SetNotes $Name"); $lab.Vms[$Name].Notes = $Notes }.GetNewClosure()
                RemoveVm      = { param($Name) [void]$lab.Calls.Add("RemoveVm $Name"); $lab.Vms.Remove($Name) }.GetNewClosure()
                BuildVm       = {
                    param($Parameters)
                    [void]$lab.Calls.Add("BuildVm $($Parameters.Name)")
                    if ($lab.FailBuildFor -eq $Parameters.Name) { throw "simulated build failure of $($Parameters.Name)" }
                    foreach ($other in $lab.Vms.Values) {
                        if (($other.State -eq 'Running') -and ((Get-LabVmNoteValue -Notes $other.Notes -Key 'ip') -eq $Parameters.IPAddress)) { throw "address $($Parameters.IPAddress) is in use by $($other.Name)" }
                    }
                    $lab.BuildIndex++
                    $lab.Vms[$Parameters.Name] = @{
                        Name        = $Parameters.Name
                        State       = 'Running'
                        Notes       = (ConvertTo-LabVmNote -Domain $Parameters.Domain -ShortName 'dc1' -NetBiosName ($Parameters.Domain.Split('.')[0]) -IPAddress $Parameters.IPAddress)
                        CreatedUtc  = $lab.Now
                        HasBaseline = $true
                        Build       = "26100.$(4000 + $lab.BuildIndex)"
                    }
                }.GetNewClosure()
                GetGuestBuild = {
                    param($Name)
                    if ($lab.Vms.ContainsKey($Name) -and ($lab.Vms[$Name].State -eq 'Running')) { return $lab.Vms[$Name].Build }
                    return $null
                }.GetNewClosure()
                NowUtc        = { return $lab.Now }.GetNewClosure()
                Log           = { param($Text) $null = $Text }.GetNewClosure()
            }
            return $lab
        }

        # A lab as it stands before a rebuild: the three live VMs, running, with a baseline, on build 26100.1000.
        function Add-FakeLiveSet {
            param($Lab)
            foreach ($entry in @(@('dc-primary', 'PANOPLY.LOCAL', '10.99.0.11'), @('dc-source', 'RESURGAM.LOCAL', '10.99.0.12'), @('dc-target', 'GENTIAN.LOCAL', '10.99.0.13'))) {
                $Lab.Vms[$entry[0]] = @{
                    Name        = $entry[0]
                    State       = 'Running'
                    Notes       = (ConvertTo-LabVmNote -Domain $entry[1] -ShortName 'dc1' -NetBiosName ($entry[1].Split('.')[0]) -IPAddress $entry[2])
                    CreatedUtc  = (Get-Utc -Day 1)
                    HasBaseline = $true
                    Build       = '26100.1000'
                }
            }
        }

        function Get-CallIndex {
            param($Lab, [string]$Call)
            return $Lab.Calls.IndexOf($Call)
        }
    }

    Context 'Build' {
        It 'stops the live domain controller before building the candidate on its address' {
            $lab = Get-FakeLab
            Add-FakeLiveSet $lab
            $results = @(Invoke-LabRebuildPhase -Phase Build -Plan (Get-FakePlan) -Adapter $lab.Adapter -Role 'dc-primary')

            $results.Count | Should -Be 1
            $results[0].action | Should -Be 'Build'
            $results[0].candidate | Should -Be 'dc-primary-candidate'
            $results[0].guestBuild | Should -Be '26100.4001'
            (Get-CallIndex $lab 'StopVm dc-primary') | Should -BeGreaterOrEqual 0
            (Get-CallIndex $lab 'StopVm dc-primary') | Should -BeLessThan (Get-CallIndex $lab 'BuildVm dc-primary-candidate')
            $lab.Vms['dc-primary'].State | Should -Be 'Off'
            $lab.Vms['dc-primary-candidate'].HasBaseline | Should -BeTrue
        }

        It 'builds all three when no role is named, leaving every live domain controller off' {
            $lab = Get-FakeLab
            Add-FakeLiveSet $lab
            $results = @(Invoke-LabRebuildPhase -Phase Build -Plan (Get-FakePlan) -Adapter $lab.Adapter)
            $results.Count | Should -Be 3
            @($results | ForEach-Object { $_.action }) | Should -Be @('Build', 'Build', 'Build')
            foreach ($name in 'dc-primary', 'dc-source', 'dc-target') {
                $lab.Vms[$name].State | Should -Be 'Off'
                $lab.Vms["$name-candidate"].State | Should -Be 'Running'
            }
        }

        It 'reuses a recent finished candidate without building again (a retry)' {
            $lab = Get-FakeLab
            Add-FakeLiveSet $lab
            [void](Invoke-LabRebuildPhase -Phase Build -Plan (Get-FakePlan) -Adapter $lab.Adapter -Role 'dc-primary')
            $lab.Calls.Clear()
            $lab.Now = $lab.Now.AddHours(2)

            $results = @(Invoke-LabRebuildPhase -Phase Build -Plan (Get-FakePlan) -Adapter $lab.Adapter -Role 'dc-primary')
            $results[0].action | Should -Be 'Reuse'
            @($lab.Calls | Where-Object { $_ -like 'BuildVm*' -or $_ -like 'RemoveVm*' }).Count | Should -Be 0
        }

        It 'replaces last month''s leftover candidate, and does not reuse it' {
            $lab = Get-FakeLab
            Add-FakeLiveSet $lab
            [void](Invoke-LabRebuildPhase -Phase Build -Plan (Get-FakePlan) -Adapter $lab.Adapter -Role 'dc-primary')
            $lab.Calls.Clear()
            $lab.Now = $lab.Now.AddDays(30)

            $results = @(Invoke-LabRebuildPhase -Phase Build -Plan (Get-FakePlan) -Adapter $lab.Adapter -Role 'dc-primary')
            $results[0].action | Should -Be 'Replace'
            (Get-CallIndex $lab 'RemoveVm dc-primary-candidate') | Should -BeLessThan (Get-CallIndex $lab 'BuildVm dc-primary-candidate')
            $lab.Vms['dc-primary-candidate'].CreatedUtc | Should -Be $lab.Now
        }

        It 'replaces a recent candidate when a fresh one is asked for' {
            $lab = Get-FakeLab
            Add-FakeLiveSet $lab
            [void](Invoke-LabRebuildPhase -Phase Build -Plan (Get-FakePlan) -Adapter $lab.Adapter -Role 'dc-primary')
            $results = @(Invoke-LabRebuildPhase -Phase Build -Plan (Get-FakePlan) -Adapter $lab.Adapter -Role 'dc-primary' -Fresh)
            $results[0].action | Should -Be 'Replace'
        }

        It 'resumes a candidate whose build stopped before its baseline checkpoint' {
            $lab = Get-FakeLab
            Add-FakeLiveSet $lab
            $lab.Vms['dc-primary'].State = 'Off'
            $lab.Vms['dc-primary-candidate'] = @{
                Name = 'dc-primary-candidate'; State = 'Off'; CreatedUtc = $lab.Now.AddHours(-2); HasBaseline = $false; Build = '26100.4000'
                Notes = (ConvertTo-LabVmNote -Domain 'PANOPLY.LOCAL' -ShortName 'dc1' -NetBiosName 'PANOPLY' -IPAddress '10.99.0.11')
            }
            $results = @(Invoke-LabRebuildPhase -Phase Build -Plan (Get-FakePlan) -Adapter $lab.Adapter -Role 'dc-primary')
            $results[0].action | Should -Be 'Resume'
            (Get-CallIndex $lab 'BuildVm dc-primary-candidate') | Should -BeGreaterOrEqual 0
            $lab.Vms['dc-primary-candidate'].HasBaseline | Should -BeTrue
        }

        It 'names the domain controller and the step when a build fails' {
            $lab = Get-FakeLab
            Add-FakeLiveSet $lab
            $lab.FailBuildFor = 'dc-source-candidate'
            { Invoke-LabRebuildPhase -Phase Build -Plan (Get-FakePlan) -Adapter $lab.Adapter } | Should -Throw '*Build failed for dc-source*simulated build failure*'
        }

        It 'refuses a VM the lab did not create' {
            $lab = Get-FakeLab
            Add-FakeLiveSet $lab
            $lab.Vms['dc-primary-candidate'] = @{ Name = 'dc-primary-candidate'; State = 'Off'; Notes = 'someone else''s VM'; CreatedUtc = $lab.Now; HasBaseline = $true; Build = '1' }
            { Invoke-LabRebuildPhase -Phase Build -Plan (Get-FakePlan) -Adapter $lab.Adapter -Role 'dc-primary' } | Should -Throw '*not created by this lab*'
            @($lab.Calls | Where-Object { $_ -like 'BuildVm*' -or $_ -like 'RemoveVm*' -or $_ -like 'StopVm*' }).Count | Should -Be 0
        }

        It 'still builds when there is no live VM at all (the first rebuild of a new lab)' {
            $lab = Get-FakeLab
            $results = @(Invoke-LabRebuildPhase -Phase Build -Plan (Get-FakePlan) -Adapter $lab.Adapter -Role 'dc-primary')
            $results[0].action | Should -Be 'Build'
        }
    }

    Context 'Stage' {
        It 'starts the candidate from its baseline on the address the live one has stopped using' {
            $lab = Get-FakeLab
            Add-FakeLiveSet $lab
            [void](Invoke-LabRebuildPhase -Phase Build -Plan (Get-FakePlan) -Adapter $lab.Adapter)
            foreach ($name in 'dc-primary', 'dc-source', 'dc-target') { $lab.Vms["$name-candidate"].State = 'Off' }
            $lab.Calls.Clear()

            $results = @(Invoke-LabRebuildPhase -Phase Stage -Plan (Get-FakePlan) -Adapter $lab.Adapter)
            @($results | ForEach-Object { $_.action }) | Should -Be @('Staged', 'Staged', 'Staged')
            foreach ($name in 'dc-primary', 'dc-source', 'dc-target') {
                $lab.Vms[$name].State | Should -Be 'Off'
                $lab.Vms["$name-candidate"].State | Should -Be 'Running'
            }
            $lab.Calls | Should -Contain 'RestoreVm dc-primary-candidate baseline'
            $results[0].guestBuild | Should -Be '26100.4001'
        }

        It 'stops a live VM that is still running before starting the candidate, or the address would clash' {
            $lab = Get-FakeLab
            Add-FakeLiveSet $lab
            $lab.Vms['dc-primary-candidate'] = @{
                Name = 'dc-primary-candidate'; State = 'Off'; CreatedUtc = $lab.Now; HasBaseline = $true; Build = '26100.4001'
                Notes = (ConvertTo-LabVmNote -Domain 'PANOPLY.LOCAL' -ShortName 'dc1' -NetBiosName 'PANOPLY' -IPAddress '10.99.0.11')
            }
            [void](Invoke-LabRebuildPhase -Phase Stage -Plan (Get-FakePlan) -Adapter $lab.Adapter -Role 'dc-primary')
            (Get-CallIndex $lab 'StopVm dc-primary') | Should -BeLessThan (Get-CallIndex $lab 'RestoreVm dc-primary-candidate baseline')
        }

        It 'fails, saying to build first, when there is no candidate' {
            $lab = Get-FakeLab
            Add-FakeLiveSet $lab
            { Invoke-LabRebuildPhase -Phase Stage -Plan (Get-FakePlan) -Adapter $lab.Adapter -Role 'dc-primary' } | Should -Throw '*run the Build phase first*'
        }

        It 'fails when the candidate has no baseline checkpoint' {
            $lab = Get-FakeLab
            Add-FakeLiveSet $lab
            $lab.Vms['dc-primary-candidate'] = @{ Name = 'dc-primary-candidate'; State = 'Off'; CreatedUtc = $lab.Now; HasBaseline = $false; Build = '1'; Notes = $lab.Vms['dc-primary'].Notes }
            { Invoke-LabRebuildPhase -Phase Stage -Plan (Get-FakePlan) -Adapter $lab.Adapter -Role 'dc-primary' } | Should -Throw '*no baseline checkpoint*'
        }
    }

    Context 'Promote' {
        BeforeEach {
            $script:Lab = Get-FakeLab
            Add-FakeLiveSet $script:Lab
            [void](Invoke-LabRebuildPhase -Phase Build -Plan (Get-FakePlan) -Adapter $script:Lab.Adapter)
            [void](Invoke-LabRebuildPhase -Phase Stage -Plan (Get-FakePlan) -Adapter $script:Lab.Adapter)
            $script:Lab.Calls.Clear()
        }

        It 'names the live VM previous and the candidate live, and keeps the candidate''s own machine' {
            $candidateBuild = $script:Lab.Vms['dc-primary-candidate'].Build
            $results = @(Invoke-LabRebuildPhase -Phase Promote -Plan (Get-FakePlan) -Adapter $script:Lab.Adapter)

            @($results | ForEach-Object { $_.action }) | Should -Be @('Promoted', 'Promoted', 'Promoted')
            foreach ($name in 'dc-primary', 'dc-source', 'dc-target') {
                $script:Lab.Vms.ContainsKey("$name-candidate") | Should -BeFalse
                $script:Lab.Vms[$name].State | Should -Be 'Running'
                $script:Lab.Vms["$name-prev"].State | Should -Be 'Off'
            }
            $script:Lab.Vms['dc-primary'].Build | Should -Be $candidateBuild
            $script:Lab.Vms['dc-primary-prev'].Build | Should -Be '26100.1000'
            $results[0].guestBuild | Should -Be $candidateBuild
        }

        It 'stamps when the live VM was demoted, in Notes the other scripts still read' {
            [void](Invoke-LabRebuildPhase -Phase Promote -Plan (Get-FakePlan) -Adapter $script:Lab.Adapter -Role 'dc-primary')
            $stamp = Get-LabVmNoteValue -Notes $script:Lab.Vms['dc-primary-prev'].Notes -Key 'demoted'
            $stamp | Should -Be '2026-09-30T04:00:00Z'
            (ConvertFrom-LabVmNote -Notes $script:Lab.Vms['dc-primary-prev'].Notes).IPAddress | Should -Be '10.99.0.11'
            Get-LabVmNoteValue -Notes $script:Lab.Vms['dc-primary'].Notes -Key 'demoted' | Should -BeNullOrEmpty
        }

        It 'steps the live VM aside before renaming the candidate to its name' {
            [void](Invoke-LabRebuildPhase -Phase Promote -Plan (Get-FakePlan) -Adapter $script:Lab.Adapter -Role 'dc-primary')
            (Get-CallIndex $script:Lab 'RenameVm dc-primary dc-primary-prev') | Should -BeLessThan (Get-CallIndex $script:Lab 'RenameVm dc-primary-candidate dc-primary')
            (Get-CallIndex $script:Lab 'RenameVm dc-primary dc-primary-prev') | Should -BeGreaterOrEqual 0
        }

        It 'can be run again: the second run changes nothing and says so' {
            [void](Invoke-LabRebuildPhase -Phase Promote -Plan (Get-FakePlan) -Adapter $script:Lab.Adapter)
            $script:Lab.Calls.Clear()
            $results = @(Invoke-LabRebuildPhase -Phase Promote -Plan (Get-FakePlan) -Adapter $script:Lab.Adapter)
            @($results | ForEach-Object { $_.action }) | Should -Be @('AlreadyPromoted', 'AlreadyPromoted', 'AlreadyPromoted')
            @($script:Lab.Calls | Where-Object { $_ -like 'RenameVm*' -or $_ -like 'RemoveVm*' -or $_ -like 'SetNotes*' }).Count | Should -Be 0
        }

        It 'finishes a promotion that stopped after the live VM was demoted' {
            $lab = $script:Lab
            $stamped = Set-LabVmNoteValue -Notes $lab.Vms['dc-primary'].Notes -Key 'demoted' -Value '2026-09-30T03:59:00Z'
            $lab.Vms['dc-primary'].Notes = $stamped
            $lab.Vms['dc-primary-prev'] = $lab.Vms['dc-primary']
            $lab.Vms['dc-primary-prev'].Name = 'dc-primary-prev'
            $lab.Vms.Remove('dc-primary')

            $results = @(Invoke-LabRebuildPhase -Phase Promote -Plan (Get-FakePlan) -Adapter $lab.Adapter -Role 'dc-primary')
            $results[0].action | Should -Be 'Promoted'
            $lab.Vms.ContainsKey('dc-primary') | Should -BeTrue
            $lab.Vms.ContainsKey('dc-primary-candidate') | Should -BeFalse
            $lab.Vms['dc-primary-prev'].Build | Should -Be '26100.1000'
        }

        It 'removes an older previous set to make way, and says so' {
            $lab = $script:Lab
            $lab.Vms['dc-primary-prev'] = @{
                Name = 'dc-primary-prev'; State = 'Off'; CreatedUtc = (Get-Utc -Day 1); HasBaseline = $true; Build = '26100.900'
                Notes = (Set-LabVmNoteValue -Notes $lab.Vms['dc-primary'].Notes -Key 'demoted' -Value '2026-09-02T04:00:00Z')
            }
            $results = @(Invoke-LabRebuildPhase -Phase Promote -Plan (Get-FakePlan) -Adapter $lab.Adapter -Role 'dc-primary')
            $results[0].detail | Should -BeLike '*removed the earlier dc-primary-prev (demoted 2026-09-02T04:00:00Z)*'
            $lab.Vms['dc-primary-prev'].Build | Should -Be '26100.1000'
        }

        It 'renames nothing at all when any one candidate cannot be promoted' {
            $lab = $script:Lab
            $lab.Vms['dc-target-candidate'].HasBaseline = $false
            { Invoke-LabRebuildPhase -Phase Promote -Plan (Get-FakePlan) -Adapter $lab.Adapter } | Should -Throw '*before anything was renamed*dc-target*'
            @($lab.Calls | Where-Object { $_ -like 'RenameVm*' -or $_ -like 'RemoveVm*' }).Count | Should -Be 0
            $lab.Vms.ContainsKey('dc-primary-candidate') | Should -BeTrue
        }
    }

    Context 'Rollback' {
        It 'stops the candidates and starts the live domain controllers again, leaving the candidates for diagnosis' {
            $lab = Get-FakeLab
            Add-FakeLiveSet $lab
            [void](Invoke-LabRebuildPhase -Phase Build -Plan (Get-FakePlan) -Adapter $lab.Adapter)
            [void](Invoke-LabRebuildPhase -Phase Stage -Plan (Get-FakePlan) -Adapter $lab.Adapter)
            $lab.Calls.Clear()

            $results = @(Invoke-LabRebuildPhase -Phase Rollback -Plan (Get-FakePlan) -Adapter $lab.Adapter)
            @($results | ForEach-Object { $_.action }) | Should -Be @('RolledBack', 'RolledBack', 'RolledBack')
            foreach ($name in 'dc-primary', 'dc-source', 'dc-target') {
                $lab.Vms[$name].State | Should -Be 'Running'
                $lab.Vms["$name-candidate"].State | Should -Be 'Off'
            }
            (Get-CallIndex $lab 'StopVm dc-primary-candidate') | Should -BeLessThan (Get-CallIndex $lab 'RestoreVm dc-primary baseline')
            $results[0].guestBuild | Should -Be '26100.1000'
        }

        It 'recovers after a build that failed part way, when only some live domain controllers were stopped' {
            $lab = Get-FakeLab
            Add-FakeLiveSet $lab
            $lab.FailBuildFor = 'dc-target-candidate'
            { Invoke-LabRebuildPhase -Phase Build -Plan (Get-FakePlan) -Adapter $lab.Adapter } | Should -Throw
            $lab.Vms['dc-primary'].State | Should -Be 'Off'

            [void](Invoke-LabRebuildPhase -Phase Rollback -Plan (Get-FakePlan) -Adapter $lab.Adapter)
            foreach ($name in 'dc-primary', 'dc-source', 'dc-target') { $lab.Vms[$name].State | Should -Be 'Running' }
            $lab.Vms['dc-primary-candidate'].State | Should -Be 'Off'
            $lab.Vms.ContainsKey('dc-target-candidate') | Should -BeFalse
        }

        It 'can be run again' {
            $lab = Get-FakeLab
            Add-FakeLiveSet $lab
            [void](Invoke-LabRebuildPhase -Phase Build -Plan (Get-FakePlan) -Adapter $lab.Adapter)
            [void](Invoke-LabRebuildPhase -Phase Rollback -Plan (Get-FakePlan) -Adapter $lab.Adapter)
            { Invoke-LabRebuildPhase -Phase Rollback -Plan (Get-FakePlan) -Adapter $lab.Adapter } | Should -Not -Throw
            $lab.Vms['dc-primary'].State | Should -Be 'Running'
        }

        It 'puts the previous VM back in service when a promotion was interrupted after demoting the live one' {
            $lab = Get-FakeLab
            Add-FakeLiveSet $lab
            [void](Invoke-LabRebuildPhase -Phase Build -Plan (Get-FakePlan) -Adapter $lab.Adapter -Role 'dc-primary')
            [void](Invoke-LabRebuildPhase -Phase Stage -Plan (Get-FakePlan) -Adapter $lab.Adapter -Role 'dc-primary')
            $lab.Vms['dc-primary'].Notes = Set-LabVmNoteValue -Notes $lab.Vms['dc-primary'].Notes -Key 'demoted' -Value '2026-09-30T04:00:00Z'
            $lab.Vms['dc-primary-prev'] = $lab.Vms['dc-primary']
            $lab.Vms['dc-primary-prev'].Name = 'dc-primary-prev'
            $lab.Vms.Remove('dc-primary')

            [void](Invoke-LabRebuildPhase -Phase Rollback -Plan (Get-FakePlan) -Adapter $lab.Adapter -Role 'dc-primary')
            $lab.Vms.ContainsKey('dc-primary-prev') | Should -BeFalse
            $lab.Vms['dc-primary'].State | Should -Be 'Running'
            $lab.Vms['dc-primary'].Build | Should -Be '26100.1000'
            Get-LabVmNoteValue -Notes $lab.Vms['dc-primary'].Notes -Key 'demoted' | Should -BeNullOrEmpty
            $lab.Vms['dc-primary-candidate'].State | Should -Be 'Off'
        }

        It 'fails loudly, naming the domain controller, when the live VM will not start' {
            $lab = Get-FakeLab
            Add-FakeLiveSet $lab
            $lab.FailRestoreOf = 'dc-source'
            { Invoke-LabRebuildPhase -Phase Rollback -Plan (Get-FakePlan) -Adapter $lab.Adapter } | Should -Throw '*Rollback failed for dc-source*simulated restore failure*'
        }
    }

    Context 'Prune' {
        It 'removes a previous VM demoted a week ago and keeps a younger one' {
            $lab = Get-FakeLab
            Add-FakeLiveSet $lab
            $note = $lab.Vms['dc-primary'].Notes
            $lab.Vms['dc-primary-prev'] = @{ Name = 'dc-primary-prev'; State = 'Off'; CreatedUtc = (Get-Utc -Day 1); HasBaseline = $true; Build = '1'; Notes = (Set-LabVmNoteValue -Notes $note -Key 'demoted' -Value '2026-09-23T04:00:00Z') }
            $lab.Vms['dc-source-prev'] = @{ Name = 'dc-source-prev'; State = 'Off'; CreatedUtc = (Get-Utc -Day 1); HasBaseline = $true; Build = '1'; Notes = (Set-LabVmNoteValue -Notes $lab.Vms['dc-source'].Notes -Key 'demoted' -Value '2026-09-24T04:00:00Z') }

            $results = @(Invoke-LabRebuildPhase -Phase Prune -Plan (Get-FakePlan) -Adapter $lab.Adapter)
            @($results | ForEach-Object { $_.action }) | Should -Be @('Pruned', 'Kept', 'NothingToPrune')
            $lab.Vms.ContainsKey('dc-primary-prev') | Should -BeFalse
            $lab.Vms.ContainsKey('dc-source-prev') | Should -BeTrue
            $lab.Vms.ContainsKey('dc-primary') | Should -BeTrue
        }

        It 'keeps a previous VM with no demotion stamp, and one with a stamp it cannot read' {
            $lab = Get-FakeLab
            Add-FakeLiveSet $lab
            $lab.Vms['dc-primary-prev'] = @{ Name = 'dc-primary-prev'; State = 'Off'; CreatedUtc = (Get-Utc -Day 1); HasBaseline = $true; Build = '1'; Notes = $lab.Vms['dc-primary'].Notes }
            $lab.Vms['dc-source-prev'] = @{ Name = 'dc-source-prev'; State = 'Off'; CreatedUtc = (Get-Utc -Day 1); HasBaseline = $true; Build = '1'; Notes = (Set-LabVmNoteValue -Notes $lab.Vms['dc-source'].Notes -Key 'demoted' -Value 'last tuesday') }
            $results = @(Invoke-LabRebuildPhase -Phase Prune -Plan (Get-FakePlan) -Adapter $lab.Adapter)
            $results[0].action | Should -Be 'Kept'
            $results[1].action | Should -Be 'Kept'
            $lab.Vms.ContainsKey('dc-primary-prev') | Should -BeTrue
            $lab.Vms.ContainsKey('dc-source-prev') | Should -BeTrue
        }
    }

    Context 'A whole rebuild' {
        It 'runs prune, build, stage, promote, then prunes the replaced set a week later' {
            $lab = Get-FakeLab
            Add-FakeLiveSet $lab
            $plan = Get-FakePlan
            foreach ($phase in 'Prune', 'Build', 'Stage', 'Promote') {
                [void](Invoke-LabRebuildPhase -Phase $phase -Plan $plan -Adapter $lab.Adapter)
            }
            foreach ($name in 'dc-primary', 'dc-source', 'dc-target') {
                $lab.Vms[$name].State | Should -Be 'Running'
                $lab.Vms[$name].Build | Should -Not -Be '26100.1000'
                $lab.Vms["$name-prev"].Build | Should -Be '26100.1000'
                $lab.Vms.ContainsKey("$name-candidate") | Should -BeFalse
            }

            $lab.Now = $lab.Now.AddDays(3)
            [void](Invoke-LabRebuildPhase -Phase Prune -Plan $plan -Adapter $lab.Adapter)
            $lab.Vms.ContainsKey('dc-primary-prev') | Should -BeTrue

            $lab.Now = $lab.Now.AddDays(5)
            [void](Invoke-LabRebuildPhase -Phase Prune -Plan $plan -Adapter $lab.Adapter)
            $lab.Vms.ContainsKey('dc-primary-prev') | Should -BeFalse
            $lab.Vms['dc-primary'].State | Should -Be 'Running'
        }

        It 'survives a second month: the new candidate is built while the first rebuild''s previous set is gone' {
            $lab = Get-FakeLab
            Add-FakeLiveSet $lab
            $plan = Get-FakePlan
            foreach ($phase in 'Prune', 'Build', 'Stage', 'Promote') { [void](Invoke-LabRebuildPhase -Phase $phase -Plan $plan -Adapter $lab.Adapter) }
            $firstBuild = $lab.Vms['dc-primary'].Build

            $lab.Now = $lab.Now.AddDays(28)
            foreach ($phase in 'Prune', 'Build', 'Stage', 'Promote') { [void](Invoke-LabRebuildPhase -Phase $phase -Plan $plan -Adapter $lab.Adapter) }
            $lab.Vms['dc-primary'].Build | Should -Not -Be $firstBuild
            $lab.Vms['dc-primary-prev'].Build | Should -Be $firstBuild
            $lab.Vms.ContainsKey('dc-primary-candidate') | Should -BeFalse
        }

        It 'promotes over an older previous set when a second rebuild is forced inside the week' {
            $lab = Get-FakeLab
            Add-FakeLiveSet $lab
            $plan = Get-FakePlan
            foreach ($phase in 'Prune', 'Build', 'Stage', 'Promote') { [void](Invoke-LabRebuildPhase -Phase $phase -Plan $plan -Adapter $lab.Adapter) }
            $lab.Now = $lab.Now.AddDays(2)
            foreach ($phase in 'Prune', 'Build', 'Stage', 'Promote') { [void](Invoke-LabRebuildPhase -Phase $phase -Plan $plan -Adapter $lab.Adapter) }
            $lab.Vms.ContainsKey('dc-primary-prev') | Should -BeTrue
            $lab.Vms.ContainsKey('dc-primary-candidate') | Should -BeFalse
        }
    }

    Context 'Status and arguments' {
        It 'reports what exists and in what state, and the live build when it is running' {
            $lab = Get-FakeLab
            Add-FakeLiveSet $lab
            $lab.Vms['dc-primary-candidate'] = @{ Name = 'dc-primary-candidate'; State = 'Off'; CreatedUtc = $lab.Now; HasBaseline = $true; Build = '2'; Notes = $lab.Vms['dc-primary'].Notes }
            $results = @(Invoke-LabRebuildPhase -Phase Status -Plan (Get-FakePlan) -Adapter $lab.Adapter -Role 'dc-primary')
            $results[0].action | Should -Be 'Reported'
            $results[0].detail | Should -BeLike '*live dc-primary: Running, baseline*'
            $results[0].detail | Should -BeLike '*candidate dc-primary-candidate: Off, baseline*'
            $results[0].detail | Should -BeLike '*prev dc-primary-prev: absent*'
            $results[0].guestBuild | Should -Be '26100.1000'
        }

        It 'accepts a comma-separated role list, and refuses a role the settings do not define' {
            $lab = Get-FakeLab
            Add-FakeLiveSet $lab
            @(Invoke-LabRebuildPhase -Phase Status -Plan (Get-FakePlan) -Adapter $lab.Adapter -Role 'dc-primary,dc-target').Count | Should -Be 2
            { Invoke-LabRebuildPhase -Phase Status -Plan (Get-FakePlan) -Adapter $lab.Adapter -Role 'dc-other' } | Should -Throw '*Unknown domain controller*dc-other*'
        }

        It 'refuses an adapter that lacks an operation, naming it' {
            $lab = Get-FakeLab
            $lab.Adapter.Remove('RenameVm')
            { Invoke-LabRebuildPhase -Phase Status -Plan (Get-FakePlan) -Adapter $lab.Adapter } | Should -Throw "*no 'RenameVm' operation*"
        }

        It 'still reports a run whose guest build cannot be read, as no build rather than an error' {
            $lab = Get-FakeLab
            Add-FakeLiveSet $lab
            $lab.Adapter.GetGuestBuild = { param($Name) throw "PowerShell Direct is not available for $Name" }
            $results = @(Invoke-LabRebuildPhase -Phase Status -Plan (Get-FakePlan) -Adapter $lab.Adapter -Role 'dc-primary')
            ($null -eq $results[0].guestBuild) | Should -BeTrue
        }
    }
}

Describe 'Get-LabGuestPhaseArgument without a DNS forwarder' {
    BeforeAll {
        # The lab network has no uplink, so there is nothing to forward to and the setting is optional.
        function Get-ForwarderTestSetting {
            return @{
                ComputerName           = 'dc1'
                Domain                 = 'PANOPLY.LOCAL'
                NetBiosName            = 'PANOPLY'
                IPAddress              = '10.99.0.11'
                PrefixLength           = 24
                Gateway                = '10.99.0.1'
                NtpServer              = '10.99.0.1'
                ServiceAccountPassword = (Get-TestSecret 'Svc-Secret-1')
                VmName                 = 'dc-primary'
                LabRoot                = 'C:\jim-ad-lab'
            }
        }
    }

    It 'gives Prepare and Configure no DnsForwarder when the setting is absent' {
        $settings = Get-ForwarderTestSetting
        foreach ($phase in 'Prepare', 'Configure') {
            { Get-LabGuestPhaseArgument -Phase $phase -Settings $settings } | Should -Not -Throw
            (Get-LabGuestPhaseArgument -Phase $phase -Settings $settings).Contains('DnsForwarder') | Should -BeFalse
        }
    }

    It 'omits DnsForwarder when the value is null, empty or blank, so it never reaches the guest as an empty argument' {
        foreach ($empty in $null, '', '   ') {
            $settings = Get-ForwarderTestSetting
            $settings.DnsForwarder = $empty
            foreach ($phase in 'Prepare', 'Configure') {
                (Get-LabGuestPhaseArgument -Phase $phase -Settings $settings).Contains('DnsForwarder') | Should -BeFalse
            }
        }
    }

    It 'still passes a forwarder to Prepare and Configure when there is one, and to no other phase' {
        $settings = Get-ForwarderTestSetting
        $settings.DnsForwarder = '10.20.30.2'
        (Get-LabGuestPhaseArgument -Phase Prepare -Settings $settings).DnsForwarder | Should -Be '10.20.30.2'
        (Get-LabGuestPhaseArgument -Phase Configure -Settings $settings).DnsForwarder | Should -Be '10.20.30.2'
        (Get-LabGuestPhaseArgument -Phase Verify -Settings $settings).Contains('DnsForwarder') | Should -BeFalse
        $settings.SafeModePassword = (Get-TestSecret 'Dsrm-Secret-1')
        (Get-LabGuestPhaseArgument -Phase Promote -Settings $settings).Contains('DnsForwarder') | Should -BeFalse
    }

    It 'still requires the rest of what each phase needs' {
        $partial = Get-ForwarderTestSetting
        $partial.Remove('Gateway')
        { Get-LabGuestPhaseArgument -Phase Prepare -Settings $partial } | Should -Throw '*Gateway*'
        $partial = Get-ForwarderTestSetting
        $partial.Remove('NtpServer')
        { Get-LabGuestPhaseArgument -Phase Configure -Settings $partial } | Should -Throw '*NtpServer*'
    }
}

Describe 'ConvertFrom-LabRebuildSetting with a settings.json that has no dnsForwarder' {
    It 'accepts the parsed file and builds every candidate without a DnsForwarder parameter' {
        $json = @'
{
  "isoPath": "D:\\media\\WindowsServer2025.iso",
  "vhdDirectory": "D:\\Hyper-V\\Virtual Hard Disks",
  "switchName": "Lab",
  "ntpServer": "10.99.0.1",
  "domainControllers": {
    "dc-primary": { "domain": "PANOPLY.LOCAL", "ipAddress": "10.99.0.11", "prefixLength": 24, "gateway": "10.99.0.1" },
    "dc-source":  { "domain": "RESURGAM.LOCAL", "ipAddress": "10.99.0.12", "prefixLength": 24, "gateway": "10.99.0.1" },
    "dc-target":  { "domain": "GENTIAN.LOCAL", "ipAddress": "10.99.0.13", "prefixLength": 24, "gateway": "10.99.0.1" }
  }
}
'@
        $plan = ConvertFrom-LabRebuildSetting -Settings ($json | ConvertFrom-Json) -GenerationName 'g1'
        @($plan.Roles).Count | Should -Be 3
        foreach ($role in $plan.Roles) {
            $role.BuildParameters.ContainsKey('DnsForwarder') | Should -BeFalse
            $role.BuildParameters.NtpServer | Should -Be '10.99.0.1'
        }
    }
}
