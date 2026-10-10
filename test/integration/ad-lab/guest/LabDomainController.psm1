# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Shared functions for the Active Directory lab: the pure logic that can be tested anywhere, and the
    Windows-only helpers that drive Hyper-V and Active Directory.

.DESCRIPTION
    This module is imported in two places: on the Hyper-V host by the scripts in ../host (Windows
    PowerShell 5.1 or PowerShell 7), and inside each domain controller by Initialize-LabDomainController.ps1
    (Windows PowerShell 5.1 only, because that is what PowerShell Direct connects to). It is also imported
    by the Pester tests on Linux. Three rules follow from that:

      1. Nothing at module scope needs Hyper-V, the ActiveDirectory module or a Windows API. Every function
         that does calls Assert-LabWindows first and fails with a clear message elsewhere.
      2. The syntax stays within Windows PowerShell 5.1: no ternary, null-coalescing, pipeline chains, or
         Join-Path with three arguments.
      3. The decisions worth testing are pure functions (SDDL rendering, checkpoint names, unattend
         rendering, certificate name lists, OS build parsing, argument strings) and are covered in
         ../LabDomainController.Tests.ps1. The Windows-only functions are thin wrappers around them.

    No function here logs or writes a secret. Passwords travel as SecureString.
#>

Set-StrictMode -Version Latest

# ---------------------------------------------------------------------------------------------
# Platform guard
# ---------------------------------------------------------------------------------------------

function Test-LabIsWindows {
    <#
    .SYNOPSIS
        Answers whether the current platform is Windows, on both PowerShell 7 and Windows PowerShell 5.1.
    #>
    [CmdletBinding()]
    [OutputType([bool])]
    param()

    # $IsWindows does not exist in Windows PowerShell 5.1, which only ever runs on Windows.
    $variable = Get-Variable -Name IsWindows -ErrorAction SilentlyContinue
    if ($null -ne $variable) {
        return [bool]$variable.Value
    }
    return $true
}

function Assert-LabWindows {
    <#
    .SYNOPSIS
        Throws a clear message when a Windows-only function is called elsewhere.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$Feature
    )

    if (-not (Test-LabIsWindows)) {
        throw "$Feature needs Windows (Hyper-V or Active Directory); it cannot run on this platform."
    }
}

# ---------------------------------------------------------------------------------------------
# Delegation: SDDL rendering and merging (pure)
# ---------------------------------------------------------------------------------------------

$script:DelegationTrusteeToken = '__JIM_TRUSTEE_SID__'

function ConvertTo-LabDelegationSddl {
    <#
    .SYNOPSIS
        Renders jim-ad-delegation.acl into one SDDL string of access control entries, for a given trustee.

    .DESCRIPTION
        Mirrors delegation_sddl() in test/integration/docker/samba-ad-prebuilt/delegation/jim-delegate.sh
        exactly, so the Samba and Windows labs apply the same entries from the same file:

            grep -v '^\s*#' FILE | grep -v '^\s*$' | tr -d '\n ' | sed "s/__JIM_TRUSTEE_SID__/SID/g"

        Lines that are blank or start with optional whitespace and then # are dropped; the rest are joined
        with newlines and spaces removed; every trustee token is replaced by the SID. Carriage returns are
        also removed, which the shell version never needs to do because the file is checked out with LF on
        Linux, but a Windows checkout may not be.
    #>
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory, ParameterSetName = 'Path')]
        [string]$AclPath,

        [Parameter(Mandatory, ParameterSetName = 'Content')]
        [string]$AclContent,

        [Parameter(Mandatory)]
        [ValidatePattern('^S-1-\d+(-\d+)+$')]
        [string]$TrusteeSid
    )

    if ($PSCmdlet.ParameterSetName -eq 'Path') {
        if (-not (Test-Path -LiteralPath $AclPath -PathType Leaf)) {
            throw "The delegation file '$AclPath' is missing."
        }
        $AclContent = Get-Content -LiteralPath $AclPath -Raw
    }

    $kept = New-Object System.Collections.Generic.List[string]
    foreach ($line in ($AclContent -split "`n")) {
        if ($line -match '^\s*#') { continue }
        if ($line -match '^\s*$') { continue }
        $kept.Add($line)
    }

    $joined = ($kept -join "`n").Replace("`r", '').Replace("`n", '').Replace(' ', '')
    if ([string]::IsNullOrEmpty($joined)) {
        throw 'The delegation content holds no access control entries once comments and blank lines are removed.'
    }

    return $joined.Replace($script:DelegationTrusteeToken, $TrusteeSid)
}

# System.Security.AccessControl.RawSecurityDescriptor cannot parse SDDL on Linux, and the tests must run there, so
# the small part of SDDL this module needs is parsed here in managed PowerShell: split a descriptor into its
# components, split the DACL into entries, and read each entry's fields.

$script:SddlSidAlias = @{
    WD = 'S-1-1-0'; CO = 'S-1-3-0'; CG = 'S-1-3-1'; OW = 'S-1-3-4'; NU = 'S-1-5-2'; IU = 'S-1-5-4'; SU = 'S-1-5-6'
    AN = 'S-1-5-7'; PS = 'S-1-5-10'; AU = 'S-1-5-11'; RC = 'S-1-5-12'; SY = 'S-1-5-18'; LS = 'S-1-5-19'; NS = 'S-1-5-20'
    ED = 'S-1-5-9'; BA = 'S-1-5-32-544'; BU = 'S-1-5-32-545'; BG = 'S-1-5-32-546'; PU = 'S-1-5-32-547'
    AO = 'S-1-5-32-548'; SO = 'S-1-5-32-549'; PO = 'S-1-5-32-550'; BO = 'S-1-5-32-551'; RE = 'S-1-5-32-552'
    RU = 'S-1-5-32-554'; RD = 'S-1-5-32-555'; NO = 'S-1-5-32-556'; MU = 'S-1-5-32-558'; LU = 'S-1-5-32-559'
    CD = 'S-1-5-32-574'; RA = 'S-1-5-32-575'; ES = 'S-1-5-32-576'; MS = 'S-1-5-32-577'; HA = 'S-1-5-32-578'
    AC = 'S-1-15-2-1'
}

$script:SddlRight = @{
    CC = 0x1; DC = 0x2; LC = 0x4; SW = 0x8; RP = 0x10; WP = 0x20; DT = 0x40; LO = 0x80; CR = 0x100
    SD = 0x10000; RC = 0x20000; WD = 0x40000; WO = 0x80000
    GA = 0x10000000; GX = 0x20000000; GW = 0x40000000; GR = 0x80000000
}

function Split-LabSddlComponent {
    # Splits an SDDL string into its O:, G:, D: and S: components, returning each with its start and length so the
    # D: component can be replaced in place.
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$Sddl
    )

    $text = $Sddl.Trim()
    $depth = 0
    $marks = New-Object System.Collections.Generic.List[object]
    for ($i = 0; $i -lt $text.Length; $i++) {
        $c = $text[$i]
        if ($c -eq '(') { $depth++ }
        elseif ($c -eq ')') { $depth-- }
        elseif (($depth -eq 0) -and ('OGDS'.IndexOf($c) -ge 0) -and (($i + 1) -lt $text.Length) -and ($text[$i + 1] -eq ':')) {
            $marks.Add([pscustomobject]@{ Kind = [string]$c; Start = $i })
        }
    }
    $components = New-Object System.Collections.Generic.List[object]
    for ($m = 0; $m -lt $marks.Count; $m++) {
        if (($m + 1) -lt $marks.Count) { $stop = $marks[$m + 1].Start } else { $stop = $text.Length }
        $components.Add([pscustomobject]@{
                Kind   = $marks[$m].Kind
                Start  = $marks[$m].Start
                Length = ($stop - $marks[$m].Start)
                Text   = $text.Substring($marks[$m].Start, ($stop - $marks[$m].Start))
            })
    }
    return [pscustomobject]@{ Text = $text; Components = $components.ToArray() }
}

function Get-LabDaclAce {
    <#
    .SYNOPSIS
        Parses the DACL of an SDDL string into its entries, each with type, flags, rights, object types and trustee.

    .DESCRIPTION
        A string without a "D:" component is treated as a bare list of entries, which is what
        ConvertTo-LabDelegationSddl produces. Each result carries the entry's original Text, its Inherited state
        (the ID flag), the access Mask (from hexadecimal or two-letter rights; generic rights are kept as their
        SDDL bit values) and the trustee as a Sid (well-known aliases resolved; a domain-relative alias such as DA
        is left as written because it cannot be resolved without the domain SID).
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$Sddl
    )

    $split = Split-LabSddlComponent -Sddl $Sddl
    $dacl = $split.Components | Where-Object { $_.Kind -eq 'D' } | Select-Object -First 1
    if ($null -ne $dacl) {
        $body = $dacl.Text.Substring(2)
    }
    elseif ($split.Components.Count -eq 0) {
        $body = $split.Text
    }
    else {
        return @()
    }

    $aces = New-Object System.Collections.Generic.List[object]
    $depth = 0
    $begin = -1
    for ($i = 0; $i -lt $body.Length; $i++) {
        $c = $body[$i]
        if ($c -eq '(') {
            if ($depth -eq 0) { $begin = $i }
            $depth++
        }
        elseif ($c -eq ')') {
            $depth--
            if ($depth -eq 0 -and $begin -ge 0) {
                $text = $body.Substring($begin, $i - $begin + 1)
                $fields = $text.Substring(1, $text.Length - 2).Split(';')
                if ($fields.Count -lt 6) {
                    throw "The access control entry '$text' does not have the six SDDL fields."
                }
                $flagsText = $fields[1]
                $flagTokens = @()
                for ($f = 0; ($f + 1) -lt $flagsText.Length; $f += 2) { $flagTokens += $flagsText.Substring($f, 2) }

                $mask = 0
                $rights = $fields[2]
                if ($rights -match '^0[xX][0-9a-fA-F]+$') {
                    $mask = [Convert]::ToInt64($rights.Substring(2), 16)
                }
                else {
                    for ($r = 0; ($r + 1) -lt $rights.Length; $r += 2) {
                        $token = $rights.Substring($r, 2)
                        if ($script:SddlRight.ContainsKey($token)) { $mask = $mask -bor [int64]$script:SddlRight[$token] }
                    }
                }

                $trustee = $fields[5].Trim()
                if ($script:SddlSidAlias.ContainsKey($trustee)) { $trustee = $script:SddlSidAlias[$trustee] }

                $aces.Add([pscustomobject][ordered]@{
                        Text                = $text
                        Type                = $fields[0]
                        Flags               = $flagsText
                        Inherited           = ($flagTokens -contains 'ID')
                        Rights              = $rights
                        Mask                = [int64]$mask
                        ObjectType          = $fields[3]
                        InheritedObjectType = $fields[4]
                        Sid                 = $trustee
                    })
                $begin = -1
            }
        }
    }
    return $aces.ToArray()
}

function Test-LabSddlContainsSid {
    <#
    .SYNOPSIS
        Answers whether any entry in an SDDL DACL names the given SID as its trustee.

    .DESCRIPTION
        Compares parsed trustees rather than searching the text, so a well-known alias such as AU is resolved to
        its SID and a SID that merely appears as a prefix of another does not match.
    #>
    [CmdletBinding()]
    [OutputType([bool])]
    param(
        [Parameter(Mandatory)]
        [string]$Sddl,

        [Parameter(Mandatory)]
        [string]$Sid
    )

    foreach ($ace in @(Get-LabDaclAce -Sddl $Sddl)) {
        if ($ace.Sid -eq $Sid) {
            return $true
        }
    }
    return $false
}

function Test-LabAclGrant {
    <#
    .SYNOPSIS
        Answers whether an SDDL DACL has an Allow entry for a SID that carries every bit of an access mask.
    #>
    [CmdletBinding()]
    [OutputType([bool])]
    param(
        [Parameter(Mandatory)]
        [string]$Sddl,

        [Parameter(Mandatory)]
        [int]$AccessMask,

        [Parameter(Mandatory)]
        [string]$Sid
    )

    foreach ($ace in @(Get-LabDaclAce -Sddl $Sddl)) {
        if (($ace.Sid -eq $Sid) -and (@('A', 'OA') -contains $ace.Type) -and (($ace.Mask -band [int64]$AccessMask) -eq [int64]$AccessMask)) {
            return $true
        }
    }
    return $false
}

function Merge-LabDelegationSddl {
    <#
    .SYNOPSIS
        Adds the delegated entries to an existing DACL, unless the trustee is already in it.

    .DESCRIPTION
        The same rule jim-delegate.sh applies: a container that already names the trustee is left alone, so
        the delegation can be applied for every container a scenario creates without accumulating duplicates.
        New entries are inserted after the existing explicit entries and before the inherited ones, which
        keeps the DACL in canonical order; the entries themselves are copied through as written.

        Returns the existing SDDL unchanged when there is nothing to do, so callers can compare.
    #>
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)]
        [string]$ExistingSddl,

        [Parameter(Mandatory)]
        [string]$DelegationSddl,

        [Parameter(Mandatory)]
        [string]$TrusteeSid
    )

    if (Test-LabSddlContainsSid -Sddl $ExistingSddl -Sid $TrusteeSid) {
        return $ExistingSddl
    }

    $split = Split-LabSddlComponent -Sddl $ExistingSddl
    $dacl = $split.Components | Where-Object { $_.Kind -eq 'D' } | Select-Object -First 1
    if ($null -eq $dacl) {
        throw 'The existing security descriptor has no DACL component (D:) to add the delegation to.'
    }

    $existingAces = @(Get-LabDaclAce -Sddl $ExistingSddl)
    $newAces = @(Get-LabDaclAce -Sddl $DelegationSddl)
    if ($newAces.Count -eq 0) {
        throw 'The delegation holds no access control entries.'
    }

    # The DACL component is "D:" + flags + entries; keep the flags, rebuild the entries.
    $firstParenthesis = $dacl.Text.IndexOf('(')
    if ($firstParenthesis -lt 0) { $flags = $dacl.Text } else { $flags = $dacl.Text.Substring(0, $firstParenthesis) }

    $index = 0
    while (($index -lt $existingAces.Count) -and (-not $existingAces[$index].Inherited)) {
        $index++
    }
    $ordered = New-Object System.Collections.Generic.List[string]
    for ($i = 0; $i -lt $index; $i++) { $ordered.Add($existingAces[$i].Text) }
    foreach ($ace in $newAces) { $ordered.Add($ace.Text) }
    for ($i = $index; $i -lt $existingAces.Count; $i++) { $ordered.Add($existingAces[$i].Text) }

    $rebuilt = $flags + ($ordered -join '')
    return $split.Text.Substring(0, $dacl.Start) + $rebuilt + $split.Text.Substring($dacl.Start + $dacl.Length)
}

# ---------------------------------------------------------------------------------------------
# Names: checkpoints, VMs, domains, certificates (pure)
# ---------------------------------------------------------------------------------------------

function Get-LabCheckpointName {
    <#
    .SYNOPSIS
        Builds a checkpoint name: 'baseline', or 'populated-<template>-<hash>'.

    .DESCRIPTION
        The template is lower-cased and the hash must be 16 hexadecimal characters (lower-cased), the same
        shape as the Samba snapshot label, so the runner can compare names for equality.
    #>
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSReviewUnusedParameter', 'Baseline', Justification = 'The switch only selects the parameter set.')]
    [CmdletBinding(DefaultParameterSetName = 'Populated')]
    [OutputType([string])]
    param(
        [Parameter(Mandatory, ParameterSetName = 'Baseline')]
        [switch]$Baseline,

        [Parameter(Mandatory, ParameterSetName = 'Populated')]
        [ValidatePattern('^[A-Za-z0-9]+$')]
        [string]$Template,

        [Parameter(Mandatory, ParameterSetName = 'Populated')]
        [ValidatePattern('^[0-9a-fA-F]{16}$')]
        [string]$Hash
    )

    if ($PSCmdlet.ParameterSetName -eq 'Baseline') {
        return 'baseline'
    }
    return ('populated-{0}-{1}' -f $Template.ToLowerInvariant(), $Hash.ToLowerInvariant())
}

function Test-LabCheckpointName {
    <#
    .SYNOPSIS
        Answers whether a name is one the control plane accepts: 'baseline' or 'populated-<template>-<16 hex>'.
    #>
    [CmdletBinding()]
    [OutputType([bool])]
    param(
        [Parameter(Mandatory)]
        [AllowEmptyString()]
        [AllowNull()]
        [string]$Name
    )

    if ([string]::IsNullOrEmpty($Name)) {
        return $false
    }
    return ($Name -cmatch '^(baseline|populated-[a-z0-9]+-[0-9a-f]{16})$')
}

function Test-LabVmName {
    <#
    .SYNOPSIS
        Answers whether a name is safe to hand to Hyper-V: letters, digits and hyphens, no wildcards.
    #>
    [CmdletBinding()]
    [OutputType([bool])]
    param(
        [Parameter(Mandatory)]
        [AllowEmptyString()]
        [AllowNull()]
        [string]$Name
    )

    if ([string]::IsNullOrEmpty($Name)) {
        return $false
    }
    return ($Name -match '^[A-Za-z0-9][A-Za-z0-9-]{0,62}$')
}

function Get-LabDomainInfo {
    <#
    .SYNOPSIS
        Derives the names the lab needs from a domain name and a computer name.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [ValidatePattern('^[A-Za-z0-9]([A-Za-z0-9-]*[A-Za-z0-9])?(\.[A-Za-z0-9]([A-Za-z0-9-]*[A-Za-z0-9])?)+$')]
        [string]$Domain,

        [Parameter(Mandatory)]
        [ValidatePattern('^[A-Za-z0-9]([A-Za-z0-9-]{0,13}[A-Za-z0-9])?$')]
        [string]$ShortName,

        # Empty is allowed and means "derive it from the domain": New-LabDomainController always passes its own
        # -NetBiosName through, which is an empty string when the operator did not give one.
        [ValidatePattern('^([A-Za-z0-9-]{1,15})?$')]
        [string]$NetBiosName
    )

    $dnsDomain = $Domain.ToLowerInvariant()
    $labels = $dnsDomain.Split('.')

    if ([string]::IsNullOrEmpty($NetBiosName)) {
        $NetBiosName = $labels[0].ToUpperInvariant()
    }
    if ($NetBiosName.Length -gt 15) {
        throw "The NetBIOS name '$NetBiosName' is longer than 15 characters; pass -NetBiosName."
    }

    $shortName = $ShortName.ToLowerInvariant()
    $baseDn = (($labels | ForEach-Object { 'DC=' + $_ }) -join ',')

    return [pscustomobject][ordered]@{
        DnsDomain   = $dnsDomain
        NetBiosName = $NetBiosName.ToUpperInvariant()
        ShortName   = $shortName
        Fqdn        = ('{0}.{1}' -f $shortName, $dnsDomain)
        BaseDn      = $baseDn
    }
}

function Get-LabCertificateName {
    <#
    .SYNOPSIS
        Builds the DNS names for the LDAPS certificate: FQDN, short name, domain, then any extras.

    .DESCRIPTION
        Mirrors CERT_SANS in post-provision.sh. Duplicates are dropped case-insensitively and the first
        spelling wins. Every name is validated as a DNS name, because the list ends up in a certificate
        command and in a certificate every client will trust.
    #>
    [CmdletBinding()]
    [OutputType([string[]])]
    param(
        [Parameter(Mandatory)]
        [string]$Fqdn,

        [Parameter(Mandatory)]
        [string]$ShortName,

        [Parameter(Mandatory)]
        [string]$Domain,

        [AllowNull()]
        [AllowEmptyCollection()]
        [AllowEmptyString()]
        [string[]]$ExtraName
    )

    $candidates = New-Object System.Collections.Generic.List[string]
    $candidates.Add($Fqdn)
    $candidates.Add($ShortName)
    $candidates.Add($Domain)
    if ($null -ne $ExtraName) {
        foreach ($extra in $ExtraName) {
            if (-not [string]::IsNullOrWhiteSpace($extra)) {
                $candidates.Add($extra.Trim())
            }
        }
    }

    $seen = New-Object System.Collections.Generic.HashSet[string]([System.StringComparer]::OrdinalIgnoreCase)
    $result = New-Object System.Collections.Generic.List[string]
    foreach ($name in $candidates) {
        if ($name -notmatch '^[A-Za-z0-9]([A-Za-z0-9._-]*[A-Za-z0-9])?$') {
            throw "'$name' is not a valid DNS name for the certificate."
        }
        if ($seen.Add($name)) {
            $result.Add($name)
        }
    }
    return $result.ToArray()
}

function Get-LabAdministratorUserName {
    <#
    .SYNOPSIS
        The Administrator account name to use for PowerShell Direct: local before promotion, domain after.
    #>
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)]
        [string]$NetBiosName,

        [switch]$Promoted
    )

    if ($Promoted) {
        return ('{0}\Administrator' -f $NetBiosName)
    }
    return '.\Administrator'
}

# ---------------------------------------------------------------------------------------------
# VM metadata (pure)
# ---------------------------------------------------------------------------------------------

$script:VmNoteMarker = 'jim-ad-lab v1'

function ConvertTo-LabVmNote {
    <#
    .SYNOPSIS
        Builds the text stored in a lab VM's Notes field: a marker plus the metadata the control plane needs.
    #>
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)]
        [string]$Domain,

        [Parameter(Mandatory)]
        [string]$ShortName,

        [Parameter(Mandatory)]
        [string]$NetBiosName,

        [string]$IPAddress
    )

    $lines = @($script:VmNoteMarker, "domain=$Domain", "netbios=$NetBiosName", "computer=$ShortName")
    if (-not [string]::IsNullOrEmpty($IPAddress)) {
        $lines += "ip=$IPAddress"
    }
    return ($lines -join "`n")
}

function Test-LabVmNote {
    <#
    .SYNOPSIS
        Answers whether a VM's Notes carry the lab marker. The control plane only touches such VMs.
    #>
    [CmdletBinding()]
    [OutputType([bool])]
    param(
        [AllowNull()]
        [AllowEmptyString()]
        [string]$Notes
    )

    if ([string]::IsNullOrEmpty($Notes)) {
        return $false
    }
    $first = ($Notes -split "`r?`n")[0].Trim()
    return ($first -match '^jim-ad-lab v\d+$')
}

function ConvertFrom-LabVmNote {
    <#
    .SYNOPSIS
        Reads the lab metadata back from a VM's Notes. Returns nothing when the marker is absent.
    #>
    [CmdletBinding()]
    param(
        [AllowNull()]
        [AllowEmptyString()]
        [string]$Notes
    )

    if (-not (Test-LabVmNote -Notes $Notes)) {
        return $null
    }
    $values = @{}
    foreach ($line in ($Notes -split "`r?`n")) {
        $index = $line.IndexOf('=')
        if ($index -gt 0) {
            $values[$line.Substring(0, $index).Trim()] = $line.Substring($index + 1).Trim()
        }
    }
    return [pscustomobject][ordered]@{
        Domain       = $values['domain']
        NetBiosName  = $values['netbios']
        ComputerName = $values['computer']
        IPAddress    = $values['ip']
    }
}

# ---------------------------------------------------------------------------------------------
# Unattend rendering (pure)
# ---------------------------------------------------------------------------------------------

function Get-LabAvmaKey {
    <#
    .SYNOPSIS
        The Automatic Virtual Machine Activation key for Windows Server 2025.

    .DESCRIPTION
        Copied from Microsoft's published table (Windows Server 2025 tab):
        https://learn.microsoft.com/windows-server/get-started/automatic-vm-activation
        These are generic keys that only activate against an activated Datacenter Hyper-V host running
        Windows Server 2025 or later. They are not licences.
    #>
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [ValidateSet('Datacenter', 'Standard')]
        [string]$Edition = 'Datacenter'
    )

    $keys = @{
        Datacenter = 'YQB4H-NKHHJ-Q6K4R-4VMY6-VCH67'
        Standard   = 'WWVGQ-PNHV9-B89P4-8GGM9-9HPQ4'
    }
    return $keys[$Edition]
}

function ConvertTo-LabUnattendPassword {
    <#
    .SYNOPSIS
        Encodes a password for an unattend file that marks it PlainText false.

    .DESCRIPTION
        Windows Setup reads base64 of UTF-16LE(password + suffix), where the suffix is 'AdministratorPassword'
        for the Administrator account setting and 'Password' for the auto-logon setting. This is an encoding,
        not encryption: the ISO carrying the file is deleted once the install has finished.
    #>
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)]
        [securestring]$Password,

        [Parameter(Mandatory)]
        [ValidateSet('AdministratorPassword', 'Password')]
        [string]$Kind
    )

    $pointer = [System.Runtime.InteropServices.Marshal]::SecureStringToBSTR($Password)
    try {
        $plain = [System.Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer)
        return [Convert]::ToBase64String([System.Text.Encoding]::Unicode.GetBytes($plain + $Kind))
    }
    finally {
        [System.Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer)
    }
}

$script:PlaceholderPattern = '__([A-Z][A-Z0-9_]*?)__'

function Get-LabUnattendPlaceholder {
    <#
    .SYNOPSIS
        Lists the distinct placeholder names in an unattend template, without the surrounding underscores.
    #>
    [CmdletBinding()]
    [OutputType([string[]])]
    param(
        [Parameter(Mandatory)]
        [string]$Template
    )

    $names = New-Object System.Collections.Generic.List[string]
    foreach ($match in [regex]::Matches($Template, $script:PlaceholderPattern)) {
        $name = $match.Groups[1].Value
        if (-not $names.Contains($name)) {
            $names.Add($name)
        }
    }
    return $names.ToArray()
}

function ConvertTo-LabUnattendXml {
    <#
    .SYNOPSIS
        Renders the unattend template by replacing every __NAME__ placeholder with its XML-escaped value.

    .DESCRIPTION
        Strict in both directions: a placeholder with no value, a value with no placeholder (a typo) and an
        empty value all throw. Substituted text is never scanned again, so a value that looks like a
        placeholder is inserted literally.
    #>
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)]
        [string]$Template,

        [Parameter(Mandatory)]
        [System.Collections.IDictionary]$Values
    )

    $names = @(Get-LabUnattendPlaceholder -Template $Template)

    foreach ($name in $names) {
        if (-not $Values.Contains($name)) {
            throw "The unattend template needs a value for the placeholder '$name'."
        }
        if ([string]::IsNullOrWhiteSpace([string]$Values[$name])) {
            throw "The value for the unattend placeholder '$name' is empty."
        }
    }
    foreach ($key in $Values.Keys) {
        if ($names -cnotcontains [string]$key) {
            throw "A value was supplied for '$key', but the unattend template has no such placeholder."
        }
    }

    $builder = New-Object System.Text.StringBuilder
    $position = 0
    foreach ($match in [regex]::Matches($Template, $script:PlaceholderPattern)) {
        [void]$builder.Append($Template.Substring($position, $match.Index - $position))
        [void]$builder.Append([System.Security.SecurityElement]::Escape([string]$Values[$match.Groups[1].Value]))
        $position = $match.Index + $match.Length
    }
    [void]$builder.Append($Template.Substring($position))
    return $builder.ToString()
}

function ConvertTo-LabKeylessUnattend {
    <#
    .SYNOPSIS
        Removes both product key settings from the unattend template, for evaluation media.

    .DESCRIPTION
        Evaluation media takes no product key at install time (the AVMA key does not match an evaluation edition, and
        Setup stops on it). The key is applied afterwards instead, by the guest's License phase, which converts the
        edition with it. Throws if the template has no product key to remove, so a template change cannot make this
        a silent no-op.
    #>
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)]
        [string]$Template
    )

    # The windowsPE pass: <ProductKey><Key>__PRODUCT_KEY__</Key>...</ProductKey>; the specialize pass:
    # <ProductKey>__PRODUCT_KEY__</ProductKey>. Each with its leading whitespace, so no blank line is left behind.
    $patterns = @('(?s)\r?\n[ \t]*<ProductKey>\s*<Key>__PRODUCT_KEY__</Key>.*?</ProductKey>', '\r?\n[ \t]*<ProductKey>__PRODUCT_KEY__</ProductKey>')
    $result = $Template
    foreach ($pattern in $patterns) {
        if ($result -notmatch $pattern) {
            throw 'The unattend template has no ProductKey setting in the expected form to remove; update ConvertTo-LabKeylessUnattend with the template.'
        }
        $result = [regex]::Replace($result, $pattern, '')
    }
    return $result
}

# ---------------------------------------------------------------------------------------------
# Directory extensions (pure)
#
# A product that extends the forest (Exchange today; Skype for Business could follow) is listed in settings.json
# directoryExtensions, in the order to apply them. Adding a product means: its name in $script:DirectoryExtensionProduct,
# its build script parameter there, a handler in the guest's Extend phase, and its Verify check. Nothing else about the
# sequencing changes.
# ---------------------------------------------------------------------------------------------

# Product name (as the lab spells it) -> the New-LabDomainController.ps1 parameter that carries its media.
$script:DirectoryExtensionProduct = [ordered]@{
    Exchange = 'ExchangeIsoPath'
}

function ConvertFrom-LabDirectoryExtensionSetting {
    <#
    .SYNOPSIS
        Validates settings.json directoryExtensions and returns the extensions in order: { Product, IsoPath }.

    .DESCRIPTION
        Absent or empty means a plain forest. Each entry needs a known product and its media; a product may appear only
        once. The order is the order the products are applied in, which matters once a product depends on another
        (Skype for Business on Exchange, say).
    #>
    [CmdletBinding()]
    param(
        [AllowNull()]
        [object]$Value
    )

    $result = New-Object System.Collections.Generic.List[object]
    if ($null -eq $Value) {
        return @()
    }
    $known = @($script:DirectoryExtensionProduct.Keys)
    $seen = @{}
    foreach ($entry in @($Value)) {
        $productText = [string](Get-LabSettingValue -Object $entry -Name 'product')
        $product = $known | Where-Object { $_ -eq $productText.Trim() } | Select-Object -First 1
        if (-not $product) {
            throw ("settings.json directoryExtensions names the product '{0}', which this lab does not know (known: {1})." -f $productText, ($known -join ', '))
        }
        $isoPath = [string](Get-LabSettingValue -Object $entry -Name 'isoPath')
        if ([string]::IsNullOrWhiteSpace($isoPath)) {
            throw "settings.json directoryExtensions entry '$product' has no 'isoPath' (the product's installation media)."
        }
        if ($seen.ContainsKey($product)) {
            throw "settings.json directoryExtensions lists '$product' more than once."
        }
        $seen[$product] = $true
        $result.Add([pscustomobject][ordered]@{ Product = [string]$product; IsoPath = $isoPath.Trim() })
    }
    return $result.ToArray()
}

function Get-LabDirectoryExtensionBuildParameter {
    <#
    .SYNOPSIS
        The New-LabDomainController.ps1 parameters that carry the directory extensions' media: { ExchangeIsoPath = ... }.

    .DESCRIPTION
        Single values only, because the monthly rebuild passes build parameters to a child process.
    #>
    [CmdletBinding()]
    [OutputType([hashtable])]
    param(
        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [object[]]$Extension
    )

    $parameters = @{}
    foreach ($entry in $Extension) {
        $parameters[$script:DirectoryExtensionProduct[[string]$entry.Product]] = [string]$entry.IsoPath
    }
    return $parameters
}

function Get-LabExchangeOrganizationName {
    <#
    .SYNOPSIS
        The Exchange organisation name for a forest: its NetBIOS name (PANOPLY, RESURGAM, GENTIAN).

    .DESCRIPTION
        Permanent once /PrepareAD has set it. Exchange allows letters, digits, hyphens and spaces, up to 64 characters,
        with no leading or trailing space:
        https://learn.microsoft.com/exchange/plan-and-deploy/prepare-ad-and-domains#step-2-prepare-active-directory
    #>
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)]
        [string]$NetBiosName
    )

    if (($NetBiosName -notmatch '^[A-Za-z0-9-]([A-Za-z0-9 -]{0,62}[A-Za-z0-9-])?$')) {
        throw "'$NetBiosName' cannot be an Exchange organisation name: letters, digits, hyphens and inner spaces only, up to 64 characters."
    }
    return $NetBiosName
}

function Get-LabExchangeSchemaTarget {
    <#
    .SYNOPSIS
        The Exchange schema version (rangeUpper of ms-Exch-Schema-Version-Pt) the media installs, from the text of its
        Setup\Data\SchemaVersion.ldf.

    .DESCRIPTION
        Read from the media rather than a table, so newer media needs no change here. 17003 for Exchange SE RTM.
    #>
    [CmdletBinding()]
    [OutputType([int])]
    param(
        [Parameter(Mandatory)]
        [string]$LdfText
    )

    $found = @([regex]::Matches($LdfText, '(?m)^rangeUpper:\s*(\d+)\s*$'))
    if ($found.Count -ne 1) {
        throw "SchemaVersion.ldf should state exactly one rangeUpper; found $($found.Count)."
    }
    return [int]$found[0].Groups[1].Value
}

function Get-LabExchangePreparationStep {
    <#
    .SYNOPSIS
        Which Exchange Setup preparation steps a forest still needs, in order: PrepareSchema, PrepareAD,
        PrepareAllDomains.

    .DESCRIPTION
        A forest already prepared by this media needs nothing, so a re-run converges; an interrupted preparation is
        finished from where it stopped. A schema update from newer media is always followed by PrepareAD and
        PrepareAllDomains, as Exchange requires. Media older than the forest's schema is refused: Exchange Setup cannot
        use it.
    #>
    [CmdletBinding()]
    [OutputType([string[]])]
    param(
        [AllowNull()]
        [Nullable[int]]$SchemaRangeUpper,

        [Parameter(Mandatory)]
        [int]$TargetRangeUpper,

        [Parameter(Mandatory)]
        [bool]$OrganizationPresent,

        [Parameter(Mandatory)]
        [bool]$DomainPrepared
    )

    if (($null -ne $SchemaRangeUpper) -and ($SchemaRangeUpper -gt $TargetRangeUpper)) {
        throw "The forest's Exchange schema ($SchemaRangeUpper) is newer than the media's ($TargetRangeUpper); use newer Exchange media."
    }
    $steps = New-Object System.Collections.Generic.List[string]
    $schemaNeeded = ($null -eq $SchemaRangeUpper) -or ($SchemaRangeUpper -lt $TargetRangeUpper)
    if ($schemaNeeded) { $steps.Add('PrepareSchema') }
    if ($schemaNeeded -or (-not $OrganizationPresent)) { $steps.Add('PrepareAD') }
    if ($schemaNeeded -or (-not $OrganizationPresent) -or (-not $DomainPrepared)) { $steps.Add('PrepareAllDomains') }
    return [string[]]$steps.ToArray()
}

function Test-LabExchangeSchemaMasterBuild {
    <#
    .SYNOPSIS
        Whether a schema master's Windows build is safe for Exchange to extend the schema on.

    .DESCRIPTION
        Microsoft requires a Windows Server 2025 schema master to have the November 2025 update (build 26100.7171) or
        later before Exchange extends the schema, or replication breaks:
        https://learn.microsoft.com/exchange/plan-and-deploy/supportability-matrix#supported-active-directory-environments
    #>
    [CmdletBinding()]
    [OutputType([bool])]
    param(
        [Parameter(Mandatory)]
        [int]$CurrentBuild,

        [Parameter(Mandatory)]
        [int]$Ubr
    )

    if ($CurrentBuild -ne 26100) {
        return $true
    }
    return ($Ubr -ge 7171)
}

function Get-LabExchangeSetupArgument {
    <#
    .SYNOPSIS
        Exchange Setup's arguments for one preparation step, accepting the licence terms with diagnostic data off.
    #>
    [CmdletBinding()]
    [OutputType([string[]])]
    param(
        [Parameter(Mandatory)]
        [ValidateSet('PrepareSchema', 'PrepareAD', 'PrepareAllDomains')]
        [string]$Step,

        [Parameter(Mandatory)]
        [string]$OrganizationName
    )

    $arguments = @('/IAcceptExchangeServerLicenseTerms_DiagnosticDataOFF', "/$Step")
    if ($Step -eq 'PrepareAD') {
        $arguments += "/OrganizationName:$(Get-LabExchangeOrganizationName -NetBiosName $OrganizationName)"
    }
    return [string[]]$arguments
}

function Get-LabDirectoryResultCode {
    <#
    .SYNOPSIS
        The LDAP result code a directory exception stands for, as a ResultCode name, or nothing when unrecognised.

    .DESCRIPTION
        Reads Response.ResultCode when the exception carries a response. A Windows Server 2025 domain controller
        refusing a simple bind over plain LDAP raises a DirectoryOperationException with no Response at all, only the
        message "Strong authentication is required for this operation.", so the message is the fallback (the lab guests
        are installed en-US, so the text is stable). Looks inside the MethodInvocationException PowerShell wraps round
        an exception from a .NET method call such as Bind(). Anything unrecognised returns nothing, so a check can
        never read an unrelated failure (the server being down, say) as the refusal it wanted.
    #>
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)]
        [object]$Exception
    )

    $current = $Exception
    while (($null -eq $current.PSObject.Properties['Response']) -and $current.PSObject.Properties['InnerException'] -and ($null -ne $current.InnerException)) {
        $current = $current.InnerException
    }
    if ($current.PSObject.Properties['Response'] -and ($null -ne $current.Response) -and $current.Response.PSObject.Properties['ResultCode']) {
        return [string]$current.Response.ResultCode
    }
    $message = [string]$current.Message
    if ($message -match 'Strong authentication is required') { return 'StrongAuthRequired' }
    if ($message -match 'insufficient access rights') { return 'InsufficientAccessRights' }
    return $null
}

function Get-LabAdProviderPath {
    <#
    .SYNOPSIS
        The AD: drive path for a distinguished name, escaped for -Path.

    .DESCRIPTION
        Get-Acl -LiteralPath does not work on the ActiveDirectory provider's drive (it returns nothing and writes
        "Cannot find path '//RootDSE/...'"), so callers use -Path, which treats [ ] * ? as wildcards. Escaping them
        keeps a distinguished name that contains one meaning exactly itself.
    #>
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)]
        [string]$DistinguishedName
    )

    return ('AD:\' + [System.Management.Automation.WildcardPattern]::Escape($DistinguishedName))
}

function Test-LabEvaluationEdition {
    <#
    .SYNOPSIS
        Whether a Windows EditionID (HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion) is an evaluation edition.
    #>
    [CmdletBinding()]
    [OutputType([bool])]
    param(
        [Parameter(Mandatory)]
        [string]$EditionId
    )

    return ($EditionId -match 'Eval$')
}

function Resolve-LabInstallImage {
    <#
    .SYNOPSIS
        Chooses the Datacenter image (Desktop Experience unless -ServerCore) from the media's image list.

    .DESCRIPTION
        Prefers a full (non-evaluation) Datacenter image. When the media holds only Evaluation Datacenter images (the
        free ISO from Microsoft's Evaluation Center), it returns the Evaluation one with Evaluation set: the build then
        installs it without a product key and the guest's License phase converts it to full Datacenter with the AVMA
        key before promotion. That order matters: Microsoft does not support converting an evaluation edition once it
        is a domain controller, and an evaluation one expires in real time regardless of checkpoint reverts.

        Returns { ImageIndex, ImageName, Evaluation }. Handles both naming schemes, "Windows Server 2025 Datacenter
        (Desktop Experience)" and the older upper-case "SERVERDATACENTER".
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [object[]]$Image,

        [switch]$ServerCore
    )

    $all = @($Image | ForEach-Object { [string]$_.ImageName })
    $datacenter = @($Image | Where-Object { $_.ImageName -match 'Datacenter' })
    if ($datacenter.Count -eq 0) {
        throw ("The media holds no Datacenter image, so Automatic Virtual Machine Activation cannot apply. It holds: {0}" -f ($all -join '; '))
    }
    $licensed = @($datacenter | Where-Object { $_.ImageName -notmatch 'Evaluation' })
    $evaluation = ($licensed.Count -eq 0)
    $candidates = if ($evaluation) { $datacenter } else { $licensed }

    $desktop = @($candidates | Where-Object { ($_.ImageName -match 'Desktop Experience') -or ($_.ImageName -cmatch 'SERVERDATACENTER$') })
    $core = @($candidates | Where-Object { $desktop -notcontains $_ })

    if ($ServerCore) {
        $pool = $core
    }
    else {
        $pool = $desktop
    }
    if ($pool.Count -eq 0) {
        throw ("The media has no matching Datacenter image. It holds: {0}" -f ($all -join '; '))
    }
    return [pscustomobject]@{
        ImageIndex = $pool[0].ImageIndex
        ImageName  = [string]$pool[0].ImageName
        Evaluation = $evaluation
    }
}

# ---------------------------------------------------------------------------------------------
# Guest phases: settings, arguments and results (pure)
# ---------------------------------------------------------------------------------------------

function Get-LabGuestPhaseArgument {
    <#
    .SYNOPSIS
        Builds the named parameters for one phase of Initialize-LabDomainController.ps1, from the full settings.

    .DESCRIPTION
        Each phase gets only what it uses, so a secret is never sent to a phase that does not need it. The
        result is an ordered dictionary for splatting; the Recycle Bin is a switch, present only when on, and the
        DNS forwarder (Prepare and Configure) is present only when it has a value.
    #>
    [CmdletBinding()]
    [OutputType([System.Collections.Specialized.OrderedDictionary])]
    param(
        [Parameter(Mandatory)]
        [ValidateSet('License', 'Prepare', 'Promote', 'Extend', 'Configure', 'Verify')]
        [string]$Phase,

        [Parameter(Mandatory)]
        [System.Collections.IDictionary]$Settings
    )

    $required = @{
        License   = @()
        Prepare   = @('ComputerName', 'IPAddress', 'PrefixLength', 'Gateway')
        Promote   = @('Domain', 'NetBiosName', 'SafeModePassword')
        Extend    = @('Domain', 'NetBiosName')
        Configure = @('ComputerName', 'Domain', 'NetBiosName', 'NtpServer', 'ServiceAccountPassword', 'VmName', 'LabRoot')
        Verify    = @('ComputerName', 'Domain', 'NetBiosName', 'NtpServer', 'ServiceAccountPassword', 'VmName', 'LabRoot')
    }
    # The DNS forwarder is optional: the lab network has no uplink, so there is normally nothing to forward to. It
    # is passed only when it has a value, so the guest never receives an empty argument.
    $optionalScalar = @{
        License   = @()
        Prepare   = @('DnsForwarder')
        Promote   = @()
        Extend    = @()
        Configure = @('DnsForwarder')
        Verify    = @()
    }
    # DirectoryExtension: the products extending the forest, in order (Exchange). Absent for a plain forest.
    $optionalArray = @{
        License   = @()
        Prepare   = @()
        Promote   = @()
        Extend    = @('DirectoryExtension')
        Configure = @('ExtraCertificateNames')
        Verify    = @('ExtraCertificateNames', 'DirectoryExtension')
    }
    $optionalSwitch = @{
        License   = @()
        Prepare   = @()
        Promote   = @()
        Extend    = @()
        Configure = @('EnableRecycleBin')
        Verify    = @('EnableRecycleBin')
    }

    $arguments = [ordered]@{ Phase = $Phase }
    foreach ($name in $required[$Phase]) {
        if ((-not $Settings.Contains($name)) -or ($null -eq $Settings[$name]) -or ([string]$Settings[$name] -eq '' -and $Settings[$name] -isnot [securestring])) {
            throw "Phase $Phase needs the setting '$name'."
        }
        $arguments[$name] = $Settings[$name]
    }
    foreach ($name in $optionalScalar[$Phase]) {
        if ($Settings.Contains($name) -and (-not [string]::IsNullOrWhiteSpace([string]$Settings[$name]))) {
            $arguments[$name] = $Settings[$name]
        }
    }
    foreach ($name in $optionalArray[$Phase]) {
        if ($Settings.Contains($name) -and ($null -ne $Settings[$name]) -and (@($Settings[$name]).Count -gt 0)) {
            $arguments[$name] = $Settings[$name]
        }
    }
    foreach ($name in $optionalSwitch[$Phase]) {
        if ($Settings.Contains($name) -and [bool]$Settings[$name]) {
            $arguments[$name] = $true
        }
    }
    return $arguments
}

function ConvertTo-LabArgumentString {
    <#
    .SYNOPSIS
        Renders named arguments as a command tail for logging, with every SecureString redacted.
    #>
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)]
        [System.Collections.IDictionary]$Argument
    )

    $format = {
        param($value)
        $text = [string]$value
        if ($text -match '^[A-Za-z0-9._:\\/=-]+$') {
            return $text
        }
        return ("'" + $text.Replace("'", "''") + "'")
    }

    $parts = New-Object System.Collections.Generic.List[string]
    foreach ($key in $Argument.Keys) {
        $value = $Argument[$key]
        if ($value -is [securestring]) {
            $parts.Add("-$key <redacted>")
        }
        elseif (($value -is [bool]) -or ($value -is [System.Management.Automation.SwitchParameter])) {
            if ([bool]$value) {
                $parts.Add("-$key")
            }
        }
        elseif (($value -is [System.Collections.IEnumerable]) -and ($value -isnot [string])) {
            $items = @($value | ForEach-Object { & $format $_ })
            $parts.Add("-$key " + ($items -join ','))
        }
        else {
            $parts.Add("-$key " + (& $format $value))
        }
    }
    return ($parts -join ' ')
}

function ConvertTo-LabOsBuild {
    <#
    .SYNOPSIS
        Parses a Windows build into the shape the runner records: currentBuild, ubr and buildString "26100.1234".

    .DESCRIPTION
        Accepts the registry values (CurrentBuild and UBR under HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion)
        or a four-part version string such as 10.0.26100.1234. A missing UBR gives the bare build number, so a
        half-read registry shows up in the record rather than aborting the run.

        Note that ProductName can still read "Windows Server 2022 Datacenter" on Windows Server 2025; the
        build number, 26100 and above, is what identifies the release, so it is what the record leads with.
    #>
    [CmdletBinding(DefaultParameterSetName = 'Registry')]
    param(
        [Parameter(ParameterSetName = 'Registry')]
        [string]$ProductName,

        [Parameter(ParameterSetName = 'Registry')]
        [string]$DisplayVersion,

        [Parameter(Mandatory, ParameterSetName = 'Registry')]
        [string]$CurrentBuild,

        [Parameter(ParameterSetName = 'Registry')]
        [object]$Ubr,

        [Parameter(Mandatory, ParameterSetName = 'Version')]
        [string]$VersionString
    )

    if ($PSCmdlet.ParameterSetName -eq 'Version') {
        if ($VersionString.Trim() -notmatch '^\d+\.\d+\.(\d+)\.(\d+)$') {
            throw "'$VersionString' is not a four-part Windows version such as 10.0.26100.1234."
        }
        $CurrentBuild = $Matches[1]
        $Ubr = $Matches[2]
    }

    $build = ([string]$CurrentBuild).Trim()
    if ($build -notmatch '^\d+$') {
        throw "The build number '$CurrentBuild' is not numeric."
    }

    $revision = $null
    if ($null -ne $Ubr) {
        $ubrText = ([string]$Ubr).Trim()
        if ($ubrText -ne '') {
            $parsed = 0
            if (-not [int]::TryParse($ubrText, [ref]$parsed)) {
                throw "The update build revision '$Ubr' is not numeric."
            }
            $revision = $parsed
        }
    }

    if ($null -eq $revision) {
        $buildString = $build
    }
    else {
        $buildString = ('{0}.{1}' -f $build, $revision)
    }

    return [pscustomobject][ordered]@{
        productName    = $ProductName
        displayVersion = $DisplayVersion
        currentBuild   = $build
        ubr            = $revision
        buildString    = $buildString
    }
}

function New-LabDomainControllerStatus {
    <#
    .SYNOPSIS
        Builds the object Get-LabDomainController.ps1 -AsJson prints.

    .DESCRIPTION
        Shape: { name, state, generation, checkpointType, uptimeSeconds, checkpoints: [{ name, createdUtc }],
        guest: { productName, displayVersion, currentBuild, ubr, buildString } | null }.
    #>
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Builds an in-memory object; changes no state.')]
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [object]$Vm,

        [AllowNull()]
        [AllowEmptyCollection()]
        [object[]]$Checkpoint,

        [AllowNull()]
        [object]$Guest
    )

    $checkpoints = @()
    if ($null -ne $Checkpoint) {
        $checkpoints = @($Checkpoint | ForEach-Object {
                [pscustomobject][ordered]@{
                    name       = [string]$_.Name
                    createdUtc = ([datetime]$_.CreationTime).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ', [System.Globalization.CultureInfo]::InvariantCulture)
                }
            })
    }

    return [pscustomobject][ordered]@{
        name           = [string]$Vm.Name
        state          = [string]$Vm.State
        generation     = [int]$Vm.Generation
        checkpointType = [string]$Vm.CheckpointType
        uptimeSeconds  = [int][math]::Floor(([timespan]$Vm.Uptime).TotalSeconds)
        checkpoints    = [object[]]$checkpoints
        guest          = $Guest
    }
}

function Resolve-LabSecret {
    <#
    .SYNOPSIS
        Returns a SecureString from a parameter or, failing that, an environment variable.

    .DESCRIPTION
        The parameter wins. With neither, throws naming the variable, unless -Optional. The value is never
        written anywhere.
    #>
    [CmdletBinding()]
    [OutputType([securestring])]
    param(
        [securestring]$Value,

        [Parameter(Mandatory)]
        [string]$EnvironmentVariable,

        [switch]$Optional
    )

    if ($null -ne $Value) {
        return $Value
    }
    $fromEnvironment = [System.Environment]::GetEnvironmentVariable($EnvironmentVariable)
    if ([string]::IsNullOrEmpty($fromEnvironment)) {
        if ($Optional) {
            return $null
        }
        throw "No password was supplied: pass it as a SecureString parameter or set the environment variable $EnvironmentVariable."
    }
    $secure = New-Object System.Security.SecureString
    foreach ($character in $fromEnvironment.ToCharArray()) {
        $secure.AppendChar($character)
    }
    $secure.MakeReadOnly()
    return $secure
}

function Resolve-LabLayout {
    <#
    .SYNOPSIS
        Finds the guest folder, the module and the delegation file relative to a host script's folder.

    .DESCRIPTION
        Two layouts are supported. The repository layout has the host scripts in test/integration/ad-lab/host,
        the guest folder beside it, and the delegation file under test/integration/docker. The deployed layout
        (JIM_AD_LAB_SCRIPT_ROOT, C:\jim-ad-lab by default) has the host scripts in the root, with guest and
        delegation as sub-folders. The deployed layout is checked first.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$ScriptRoot
    )

    $root = [System.IO.Path]::GetFullPath($ScriptRoot)
    $parent = [System.IO.Path]::GetFullPath((Join-Path $root '..'))

    $guestCandidates = @((Join-Path $root 'guest'), (Join-Path $parent 'guest'))
    $aclName = 'jim-ad-delegation.acl'
    $repoAcl = [System.IO.Path]::GetFullPath((Join-Path (Join-Path (Join-Path (Join-Path $parent '..') 'docker') 'samba-ad-prebuilt') (Join-Path 'delegation' $aclName)))
    $aclCandidates = @((Join-Path (Join-Path $root 'delegation') $aclName), $repoAcl, (Join-Path (Join-Path $root 'guest') $aclName))

    $guestDirectory = $guestCandidates | Where-Object { Test-Path -LiteralPath (Join-Path $_ 'LabDomainController.psm1') } | Select-Object -First 1
    $aclPath = $aclCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1

    if ((-not $guestDirectory) -or (-not $aclPath)) {
        $looked = @($guestCandidates | ForEach-Object { Join-Path $_ 'LabDomainController.psm1' }) + $aclCandidates
        throw ("Could not find LabDomainController.psm1 and {0}. Looked in: {1}" -f $aclName, ($looked -join '; '))
    }

    return [pscustomobject][ordered]@{
        GuestDirectory    = $guestDirectory
        ModulePath        = (Join-Path $guestDirectory 'LabDomainController.psm1')
        DelegationAclPath = $aclPath
    }
}

# ---------------------------------------------------------------------------------------------
# Rebuild: names, settings and decisions (pure)
# ---------------------------------------------------------------------------------------------
#
# The monthly rebuild (host/Invoke-LabRebuild.ps1) builds a candidate set beside the live set, runs the suite
# against it, and only then swaps names. Everything it decides is here as a pure function, and the phases that
# drive Hyper-V are one orchestrator (Invoke-LabRebuildPhase) that reaches the host only through an adapter, so the
# ordering and the recovery from an interrupted run are tested on any platform.

function Get-LabSettingValue {
    # Internal: reads one setting from a hashtable or a parsed JSON object, or returns $null when it is not there.
    # Not exported; it keeps the settings readers free of StrictMode property errors.
    param(
        [AllowNull()]
        [object]$Object,

        [Parameter(Mandatory)]
        [string]$Name
    )

    if ($null -eq $Object) {
        return $null
    }
    if ($Object -is [System.Collections.IDictionary]) {
        if ($Object.Contains($Name)) {
            return $Object[$Name]
        }
        return $null
    }
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) {
        return $null
    }
    return $property.Value
}

function Get-LabSettingName {
    # Internal: the names of the settings an object or hashtable holds.
    param(
        [AllowNull()]
        [object]$Object
    )

    if ($null -eq $Object) {
        return @()
    }
    if ($Object -is [System.Collections.IDictionary]) {
        return @($Object.Keys | ForEach-Object { [string]$_ })
    }
    return @($Object.PSObject.Properties | ForEach-Object { $_.Name })
}

function Join-LabPathText {
    # Internal: a child folder beneath a root, by text, using the separator the root already uses. Join-Path is not
    # used because it needs the drive to exist, and the rebuild's settings hold host paths (D:\...) that the tests
    # read on Linux.
    param(
        [Parameter(Mandatory)]
        [string]$Root,

        [Parameter(Mandatory)]
        [string]$Child
    )

    $separator = '/'
    if (($Root.Contains('\')) -or ($Root -match '^[A-Za-z]:')) {
        $separator = '\'
    }
    return ($Root.TrimEnd('\', '/') + $separator + $Child)
}

function Get-LabRebuildVmName {
    <#
    .SYNOPSIS
        The name of the live, candidate or previous VM of one domain controller: dc-primary, dc-primary-candidate,
        dc-primary-prev.

    .DESCRIPTION
        The live name is what the nightly run's configuration always uses. The rebuild builds
        <live>-candidate, and on success renames the live VM to <live>-prev and the candidate to <live>.
    #>
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)]
        [string]$LiveName,

        [Parameter(Mandatory)]
        [ValidateSet('Live', 'Candidate', 'Prev')]
        [string]$Kind
    )

    if (-not (Test-LabVmName -Name $LiveName)) {
        throw "'$LiveName' is not a valid VM name here: letters, digits and hyphens only."
    }
    if ($LiveName -match '-(candidate|prev)$') {
        throw "'$LiveName' already ends in -candidate or -prev; the rebuild names its VMs from the live name."
    }

    if ($Kind -eq 'Candidate') {
        $name = "$LiveName-candidate"
    }
    elseif ($Kind -eq 'Prev') {
        $name = "$LiveName-prev"
    }
    else {
        $name = $LiveName
    }
    if (-not (Test-LabVmName -Name $name)) {
        throw "'$name' is not a valid VM name here (too long?): the live name must leave room for its suffix."
    }
    return $name
}

function Get-LabRebuildGenerationName {
    <#
    .SYNOPSIS
        The folder name a rebuild builds its candidate disks under: rebuild-yyyyMMdd (UTC).

    .DESCRIPTION
        New-LabDomainController.ps1 puts a VM's files in <VhdDirectory>\<VM name>\. A candidate is later renamed to
        the live name and keeps its folder, so a second rebuild's candidate (same name again) would find its disk
        already there and refuse. Each rebuild therefore passes a fresh VhdDirectory, this name beneath the
        configured one.
    #>
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)]
        [datetime]$NowUtc
    )

    return ('rebuild-' + $NowUtc.ToUniversalTime().ToString('yyyyMMdd', [System.Globalization.CultureInfo]::InvariantCulture))
}

function Get-LabRebuildRoleEntry {
    # Internal: one domain controller's names, and the parameters of its candidate build when there are any.
    param(
        [Parameter(Mandatory)]
        [string]$LiveName,

        [AllowNull()]
        [hashtable]$BuildParameters
    )

    $roleName = [System.Globalization.CultureInfo]::InvariantCulture.TextInfo.ToTitleCase($LiveName.Substring(3))
    return [pscustomobject][ordered]@{
        Role                = $roleName
        LiveName            = $LiveName
        CandidateName       = (Get-LabRebuildVmName -LiveName $LiveName -Kind Candidate)
        PrevName            = (Get-LabRebuildVmName -LiveName $LiveName -Kind Prev)
        EnvironmentVariable = ('JIM_AD_LAB_{0}_VM' -f $LiveName.Substring(3).ToUpperInvariant())
        BuildParameters     = $BuildParameters
    }
}

function ConvertFrom-LabRebuildSetting {
    <#
    .SYNOPSIS
        Validates settings.json (parsed) and maps it onto what a rebuild does: the three domain controllers, their
        VM names and the parameters of New-LabDomainController.ps1 for each candidate.

    .DESCRIPTION
        The settings hold the paths, network and time source shared by every domain controller and one entry per
        domain controller:

            isoPath, cumulativeUpdateDirectory (optional), vhdDirectory, switchName, ntpServer,
            dnsForwarder (optional), domainControllers: { dc-primary|dc-source|dc-target: { domain, ipAddress, prefixLength, gateway,
                                                                    enableRecycleBin (optional) } }

        Exactly the three lab names are accepted, because the runner reads JIM_AD_LAB_PRIMARY_VM, _SOURCE_VM and
        _TARGET_VM and nothing else. A candidate is built with the live domain controller's own address (its
        checkpoints carry it, so it cannot be changed later); the live VM is therefore off while the candidate
        is built.

        Returns Roles (ordered Primary, Source, Target), each with LiveName, CandidateName, PrevName, Role,
        EnvironmentVariable (the JIM_AD_LAB_<ROLE>_VM variable that points the runner at a candidate) and
        BuildParameters (a hashtable for New-LabDomainController.ps1).

        With -NamesOnly nothing is validated and the roles carry no BuildParameters. Every phase except Build only
        needs the names, and a rollback must never be blocked by a settings file that is wrong for building.

    .PARAMETER Settings
        The parsed settings: ConvertFrom-Json output or a hashtable.

    .PARAMETER GenerationName
        The folder beneath vhdDirectory that receives this rebuild's disks (Get-LabRebuildGenerationName).

    .PARAMETER CumulativeUpdatePath
        A .msu to pass to every build, when one exists.

    .PARAMETER NamesOnly
        Return the roles without validating the settings or preparing builds.
    #>
    [CmdletBinding()]
    param(
        [AllowNull()]
        [object]$Settings,

        [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9-]*$')]
        [string]$GenerationName,

        [string]$CumulativeUpdatePath,

        [switch]$NamesOnly
    )

    $liveNames = @('dc-primary', 'dc-source', 'dc-target')

    if ($NamesOnly) {
        $vhdDirectory = $null
        $configured = Get-LabSettingValue -Object $Settings -Name 'vhdDirectory'
        if (-not [string]::IsNullOrWhiteSpace([string]$configured)) {
            $vhdDirectory = ([string]$configured).Trim()
        }
        return [pscustomobject][ordered]@{
            VhdDirectory = $vhdDirectory
            Generation   = $null
            Roles        = @($liveNames | ForEach-Object { Get-LabRebuildRoleEntry -LiveName $_ -BuildParameters $null })
        }
    }

    if ([string]::IsNullOrEmpty($GenerationName)) {
        throw 'A generation name is needed to build (Get-LabRebuildGenerationName).'
    }

    $ipv4 = '^\d{1,3}(\.\d{1,3}){3}$'
    $shared = @{}
    foreach ($key in @('isoPath', 'vhdDirectory', 'switchName', 'ntpServer')) {
        $value = Get-LabSettingValue -Object $Settings -Name $key
        if ([string]::IsNullOrWhiteSpace([string]$value)) {
            throw "settings.json has no '$key'. Every value in settings.example.json is needed."
        }
        $shared[$key] = ([string]$value).Trim()
    }
    # The forwarder is optional: the lab network has no uplink, and the build script accepts none. When there is
    # one it must be an address, and it is passed on.
    $shared['dnsForwarder'] = $null
    $forwarderValue = Get-LabSettingValue -Object $Settings -Name 'dnsForwarder'
    if (-not [string]::IsNullOrWhiteSpace([string]$forwarderValue)) {
        $forwarder = $null
        $forwarderText = ([string]$forwarderValue).Trim()
        if (($forwarderText -notmatch $ipv4) -or (-not [System.Net.IPAddress]::TryParse($forwarderText, [ref]$forwarder))) {
            throw "settings.json 'dnsForwarder' is '$forwarderText', which is not an IPv4 address."
        }
        $shared['dnsForwarder'] = $forwarderText
    }

    # Every forest gets the same extensions, in the order listed (validated before anything is built).
    $extensions = @(ConvertFrom-LabDirectoryExtensionSetting -Value (Get-LabSettingValue -Object $Settings -Name 'directoryExtensions'))

    $controllers = Get-LabSettingValue -Object $Settings -Name 'domainControllers'
    if ($null -eq $controllers) {
        throw "settings.json has no 'domainControllers'."
    }
    foreach ($name in (Get-LabSettingName -Object $controllers)) {
        if ($liveNames -notcontains $name) {
            throw "settings.json domainControllers holds '$name'; this lab has exactly dc-primary, dc-source and dc-target."
        }
    }

    $roles = New-Object System.Collections.Generic.List[object]
    $addresses = @{}
    $domains = @{}
    foreach ($liveName in $liveNames) {
        $dc = Get-LabSettingValue -Object $controllers -Name $liveName
        if ($null -eq $dc) {
            throw "settings.json domainControllers has no '$liveName'."
        }

        $domain = [string](Get-LabSettingValue -Object $dc -Name 'domain')
        $address = [string](Get-LabSettingValue -Object $dc -Name 'ipAddress')
        $gateway = [string](Get-LabSettingValue -Object $dc -Name 'gateway')
        $prefixText = [string](Get-LabSettingValue -Object $dc -Name 'prefixLength')

        if ($domain -notmatch '^[A-Za-z0-9]([A-Za-z0-9-]*[A-Za-z0-9])?(\.[A-Za-z0-9]([A-Za-z0-9-]*[A-Za-z0-9])?)+$') {
            throw "settings.json $liveName 'domain' is '$domain', which is not a DNS domain name."
        }
        foreach ($pair in @(@('ipAddress', $address), @('gateway', $gateway))) {
            $parsed = $null
            if (($pair[1] -notmatch $ipv4) -or (-not [System.Net.IPAddress]::TryParse($pair[1], [ref]$parsed))) {
                throw "settings.json $liveName '$($pair[0])' is '$($pair[1])', which is not an IPv4 address."
            }
        }
        $prefix = 0
        if ((-not [int]::TryParse($prefixText, [ref]$prefix)) -or ($prefix -lt 8) -or ($prefix -gt 30)) {
            throw "settings.json $liveName 'prefixLength' is '$prefixText'; it must be a number from 8 to 30."
        }
        if ($addresses.ContainsKey($address)) {
            throw "settings.json gives $liveName the address $address, which $($addresses[$address]) already has."
        }
        $addresses[$address] = $liveName
        if ($domains.ContainsKey($domain.ToLowerInvariant())) {
            throw "settings.json gives $liveName the domain $domain, which $($domains[$domain.ToLowerInvariant()]) already has."
        }
        $domains[$domain.ToLowerInvariant()] = $liveName

        $recycleBin = $false
        $recycleBinValue = Get-LabSettingValue -Object $dc -Name 'enableRecycleBin'
        if ($null -ne $recycleBinValue) {
            $recycleBin = [bool]$recycleBinValue
        }

        $parameters = @{
            Name         = (Get-LabRebuildVmName -LiveName $liveName -Kind Candidate)
            Domain       = $domain
            IsoPath      = $shared['isoPath']
            VhdDirectory = (Join-LabPathText -Root $shared['vhdDirectory'] -Child $GenerationName)
            SwitchName   = $shared['switchName']
            IPAddress    = $address
            PrefixLength = $prefix
            Gateway      = $gateway
            NtpServer    = $shared['ntpServer']
        }
        if ($null -ne $shared['dnsForwarder']) {
            $parameters['DnsForwarder'] = $shared['dnsForwarder']
        }
        if ($recycleBin) {
            $parameters['EnableRecycleBin'] = $true
        }
        if (-not [string]::IsNullOrEmpty($CumulativeUpdatePath)) {
            $parameters['CumulativeUpdatePath'] = $CumulativeUpdatePath
        }
        $extensionParameters = Get-LabDirectoryExtensionBuildParameter -Extension $extensions
        foreach ($key in $extensionParameters.Keys) {
            $parameters[$key] = $extensionParameters[$key]
        }
        $roles.Add((Get-LabRebuildRoleEntry -LiveName $liveName -BuildParameters $parameters))
    }

    return [pscustomobject][ordered]@{
        VhdDirectory = $shared['vhdDirectory']
        Generation   = $GenerationName
        Roles        = $roles.ToArray()
    }
}

function Get-LabNewestCumulativeUpdate {
    <#
    .SYNOPSIS
        Picks the newest .msu (by last write time) from a list of files, or nothing when there is none.

    .DESCRIPTION
        The rebuild installs one cumulative update, the newest in cumulativeUpdateDirectory. Keep only the update
        wanted in that folder: "newest" is the file's modification time, and a servicing stack update saved later
        than the cumulative update would win.
    #>
    [CmdletBinding()]
    param(
        [AllowNull()]
        [AllowEmptyCollection()]
        [object[]]$File
    )

    $candidates = @($File | Where-Object { ($null -ne $_) -and (([string]$_.Name) -match '\.msu$') })
    if ($candidates.Count -eq 0) {
        return $null
    }
    return ($candidates |
            Sort-Object -Property @{ Expression = { [datetime]$_.LastWriteTimeUtc }; Descending = $true }, @{ Expression = { [string]$_.Name }; Descending = $true } |
            Select-Object -First 1)
}

function Get-LabRebuildBuildAction {
    <#
    .SYNOPSIS
        What the Build phase does about one candidate: Build, Resume, Reuse or Replace.

    .DESCRIPTION
        Build phase re-runs must converge, and a candidate a red run left behind for diagnosis must never be mistaken
        for this month's:

          none there                                 Build   (create it)
          -Fresh                                     Replace (remove it, build again)
          older than MaxAgeHours                     Replace (a leftover from an earlier rebuild)
          recent, has its baseline checkpoint        Reuse   (this run's, or a retry's; already built)
          recent, no baseline checkpoint yet         Resume  (a build that failed part way; the build script converges)
    #>
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)]
        [bool]$CandidateExists,

        [datetime]$CandidateCreatedUtc = [datetime]::MinValue,

        [bool]$CandidateHasBaseline = $false,

        [Parameter(Mandatory)]
        [datetime]$NowUtc,

        [ValidateRange(1, 720)]
        [int]$MaxAgeHours = 24,

        [switch]$Fresh
    )

    if (-not $CandidateExists) {
        return 'Build'
    }
    if ($Fresh) {
        return 'Replace'
    }
    $age = $NowUtc.ToUniversalTime() - $CandidateCreatedUtc.ToUniversalTime()
    if ($age.TotalHours -gt $MaxAgeHours) {
        return 'Replace'
    }
    if ($CandidateHasBaseline) {
        return 'Reuse'
    }
    return 'Resume'
}

function Get-LabPromotePlan {
    <#
    .SYNOPSIS
        The ordered steps that turn the candidate into the live VM, from which VMs exist: RemovePrev,
        RenameLiveToPrev, RenameCandidateToLive.

    .DESCRIPTION
        Written so that a Promote interrupted at any point and run again finishes the job:

          candidate, live, prev     RemovePrev, RenameLiveToPrev, RenameCandidateToLive   (prev is an older set)
          candidate, live           RenameLiveToPrev, RenameCandidateToLive
          candidate, prev           RenameCandidateToLive                                 (interrupted after the live VM was demoted)
          live only, or live, prev  nothing (already promoted, or nothing was staged)
          candidate only, or none   an error: there is no live VM to demote or to keep
    #>
    [CmdletBinding()]
    [OutputType([string[]])]
    param(
        [Parameter(Mandatory)]
        [bool]$LiveExists,

        [Parameter(Mandatory)]
        [bool]$CandidateExists,

        [Parameter(Mandatory)]
        [bool]$PrevExists
    )

    if (-not $CandidateExists) {
        if ($LiveExists) {
            return [string[]]@()
        }
        throw 'There is no live VM and no candidate: nothing to promote, and nothing to keep.'
    }
    if ($LiveExists) {
        if ($PrevExists) {
            return [string[]]@('RemovePrev', 'RenameLiveToPrev', 'RenameCandidateToLive')
        }
        return [string[]]@('RenameLiveToPrev', 'RenameCandidateToLive')
    }
    if ($PrevExists) {
        return [string[]]@('RenameCandidateToLive')
    }
    throw 'There is a candidate but no live VM and no previous VM: the live set is gone, so there is nothing to demote. Restore it by hand.'
}

function Get-LabRollbackPlan {
    <#
    .SYNOPSIS
        The ordered steps that put the live VM back in service after a failed rebuild: StopCandidate,
        RenamePrevToLive, StartLive.

    .DESCRIPTION
        The candidate is stopped and left in place for diagnosis. If a Promote was interrupted after it demoted the
        live VM (no live VM, a previous one, and the candidate), the previous VM is named live again first.
    #>
    [CmdletBinding()]
    [OutputType([string[]])]
    param(
        [Parameter(Mandatory)]
        [bool]$LiveExists,

        [Parameter(Mandatory)]
        [bool]$CandidateExists,

        [Parameter(Mandatory)]
        [bool]$PrevExists
    )

    $steps = New-Object System.Collections.Generic.List[string]
    if ($CandidateExists) {
        $steps.Add('StopCandidate')
    }
    if (-not $LiveExists) {
        if (-not $PrevExists) {
            throw 'There is no live VM and no previous VM to put back in service. Restore the lab by hand.'
        }
        $steps.Add('RenamePrevToLive')
    }
    $steps.Add('StartLive')
    return [string[]]$steps.ToArray()
}

function Get-LabPrunePlan {
    <#
    .SYNOPSIS
        Which previous-set VMs the Prune phase removes: those demoted at least MinAgeDays days ago.

    .DESCRIPTION
        A previous VM whose Notes carry no demotion time (renamed by hand, say) is kept and reported, never guessed
        at: deleting a rollback copy on a guess is the wrong way to be wrong.

    .PARAMETER Prev
        Objects with Name and DemotedUtc (a datetime, or null when unknown).
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [object[]]$Prev,

        [Parameter(Mandatory)]
        [datetime]$NowUtc,

        [ValidateRange(1, 365)]
        [int]$MinAgeDays = 7
    )

    $plan = New-Object System.Collections.Generic.List[object]
    foreach ($item in $Prev) {
        $demoted = $item.DemotedUtc
        if ($null -eq $demoted) {
            $plan.Add([pscustomobject][ordered]@{ Name = [string]$item.Name; Remove = $false; Reason = 'kept: no demotion time is recorded on it, so its age is unknown' })
            continue
        }
        $age = $NowUtc.ToUniversalTime() - ([datetime]$demoted).ToUniversalTime()
        $ageDays = [math]::Floor($age.TotalDays)
        if ($age.TotalDays -ge $MinAgeDays) {
            $plan.Add([pscustomobject][ordered]@{ Name = [string]$item.Name; Remove = $true; Reason = "removed: demoted $ageDays days ago (at least $MinAgeDays)" })
        }
        else {
            $plan.Add([pscustomobject][ordered]@{ Name = [string]$item.Name; Remove = $false; Reason = "kept: demoted $ageDays days ago (removed at $MinAgeDays)" })
        }
    }
    return $plan.ToArray()
}

function Get-LabVmNoteValue {
    <#
    .SYNOPSIS
        Reads one key=value line from a lab VM's Notes, or returns nothing.
    #>
    [CmdletBinding()]
    param(
        [AllowNull()]
        [AllowEmptyString()]
        [string]$Notes,

        [Parameter(Mandatory)]
        [ValidatePattern('^[a-z][a-z0-9-]*$')]
        [string]$Key
    )

    if (-not (Test-LabVmNote -Notes $Notes)) {
        return $null
    }
    foreach ($line in ($Notes -split "`r?`n")) {
        $index = $line.IndexOf('=')
        if (($index -gt 0) -and ($line.Substring(0, $index).Trim() -ceq $Key)) {
            return $line.Substring($index + 1).Trim()
        }
    }
    return $null
}

function Set-LabVmNoteValue {
    <#
    .SYNOPSIS
        Returns the Notes text with one key=value line set (or removed, with an empty value). The marker line and
        the other lines are kept.

    .DESCRIPTION
        The rebuild stamps demoted=<UTC time> on a live VM as it renames it to <name>-prev, so the Prune phase knows
        how long the set has been the rollback copy. ConvertFrom-LabVmNote ignores keys it does not know, so the
        stamp does not disturb the other scripts.
    #>
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Returns text; changes no state.')]
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [AllowNull()]
        [AllowEmptyString()]
        [string]$Notes,

        [Parameter(Mandatory)]
        [ValidatePattern('^[a-z][a-z0-9-]*$')]
        [string]$Key,

        [AllowNull()]
        [AllowEmptyString()]
        [string]$Value
    )

    if (-not (Test-LabVmNote -Notes $Notes)) {
        throw 'The Notes carry no lab marker; refusing to edit them.'
    }
    $kept = New-Object System.Collections.Generic.List[string]
    foreach ($line in ($Notes -split "`r?`n")) {
        if ($line.Trim() -eq '') {
            continue
        }
        $index = $line.IndexOf('=')
        if (($index -gt 0) -and ($line.Substring(0, $index).Trim() -ceq $Key)) {
            continue
        }
        $kept.Add($line)
    }
    if (-not [string]::IsNullOrEmpty($Value)) {
        if ($Value -match '[\r\n]') {
            throw 'A Notes value cannot span lines.'
        }
        $kept.Add("$Key=$Value")
    }
    return ($kept -join "`n")
}

function ConvertTo-LabChildArgument {
    <#
    .SYNOPSIS
        Turns a parameter hashtable into the argument list for running a script in a child process:
        -Key value pairs, and a bare -Key for a switch that is true (one that is false is left out).

    .DESCRIPTION
        The rebuild runs each lab script in its own process so the script's own module import, StrictMode and exit
        code cannot disturb the rebuild. Only scalar values are accepted; text with a line break is refused.
    #>
    [CmdletBinding()]
    [OutputType([string[]])]
    param(
        [Parameter(Mandatory)]
        [hashtable]$Parameter
    )

    $arguments = New-Object System.Collections.Generic.List[string]
    foreach ($key in ($Parameter.Keys | Sort-Object)) {
        if ($key -notmatch '^[A-Za-z][A-Za-z0-9]*$') {
            throw "'$key' is not a parameter name."
        }
        $value = $Parameter[$key]
        if ($value -is [bool]) {
            if ($value) {
                $arguments.Add("-$key")
            }
            continue
        }
        if (($null -eq $value) -or ($value -is [System.Collections.IEnumerable] -and $value -isnot [string])) {
            throw "The value of -$key must be a single value."
        }
        $text = [string]$value
        if ($text -match '[\r\n\0]') {
            throw "The value of -$key holds a line break."
        }
        $arguments.Add("-$key")
        $arguments.Add($text)
    }
    return [string[]]$arguments.ToArray()
}

function Test-LabOwnedVmFolder {
    <#
    .SYNOPSIS
        Whether a VM's folder is one the rebuild may delete after removing the VM: strictly beneath the configured
        VHD directory, and named for one of the lab's VMs.

    .DESCRIPTION
        Remove-LabDomainController.ps1 only deletes a VM's folder when the folder has the VM's name, and a renamed
        VM keeps the folder of the name it was built under, so the rebuild clears such folders itself. The check is
        on path segments (either slash, case-insensitive), so it needs no file system and refuses anything with a
        dot segment.
    #>
    [CmdletBinding()]
    [OutputType([bool])]
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [Parameter(Mandatory)]
        [string]$Root,

        [Parameter(Mandatory)]
        [string[]]$Name
    )

    $pathParts = @((($Path -replace '\\', '/').Trim('/')) -split '/' | Where-Object { $_ -ne '' })
    $rootParts = @((($Root -replace '\\', '/').Trim('/')) -split '/' | Where-Object { $_ -ne '' })
    if (($rootParts.Count -eq 0) -or ($pathParts.Count -le $rootParts.Count)) {
        return $false
    }
    foreach ($part in @($pathParts) + @($rootParts)) {
        if (($part -eq '..') -or ($part -eq '.')) {
            return $false
        }
    }
    for ($index = 0; $index -lt $rootParts.Count; $index++) {
        if ($pathParts[$index] -ne $rootParts[$index]) {
            return $false
        }
    }
    return ($Name -contains $pathParts[$pathParts.Count - 1])
}

# ---------------------------------------------------------------------------------------------
# Rebuild: reading a run's results (for the workflows)
# ---------------------------------------------------------------------------------------------

function Get-LabRunSummary {
    <#
    .SYNOPSIS
        Reads what Run-IntegrationTests.ps1 left in test/integration/results and summarises it: how many scenarios
        passed and which domain controller OS builds the run was against.

    .DESCRIPTION
        A -Scenario All run writes results/full-regression-<time>.json (Scenarios with Success and Skipped,
        DomainControllerBuilds by VM name). A single-scenario run writes no such report, only
        results/performance/<host>/<scenario>-<template>-<time>.json, which carries DomainControllerBuilds too; it
        is then counted as one scenario, passed when -Success says the run passed. The newest file of each kind is
        used. A results folder with neither gives zero scenarios and no builds.

        Returns Total, Passed, Failed, Skipped, FailedScenarios (the names of those that failed, from a regression
        report), Builds (an ordered map of VM name to build such as 26100.4652) and Report (the file the counts came
        from, or null).
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$ResultsDirectory,

        [bool]$Success = $false
    )

    $builds = [ordered]@{}
    $total = 0
    $passed = 0
    $skipped = 0
    $failedNames = New-Object System.Collections.Generic.List[string]
    $reportPath = $null

    $reportFile = $null
    $performanceFile = $null
    if (Test-Path -LiteralPath $ResultsDirectory -PathType Container) {
        $reportFile = Get-ChildItem -LiteralPath $ResultsDirectory -Filter 'full-regression-*.json' -File -ErrorAction SilentlyContinue |
            Sort-Object -Property LastWriteTimeUtc, Name -Descending | Select-Object -First 1
        $performanceDirectory = Join-Path $ResultsDirectory 'performance'
        if (Test-Path -LiteralPath $performanceDirectory -PathType Container) {
            $performanceFile = Get-ChildItem -LiteralPath $performanceDirectory -Filter '*.json' -File -Recurse -ErrorAction SilentlyContinue |
                Sort-Object -Property LastWriteTimeUtc, Name -Descending | Select-Object -First 1
        }
    }

    if ($null -ne $reportFile) {
        $report = Get-Content -LiteralPath $reportFile.FullName -Raw | ConvertFrom-Json
        $reportPath = $reportFile.FullName
        foreach ($scenario in @(Get-LabSettingValue -Object $report -Name 'Scenarios')) {
            if ($null -eq $scenario) {
                continue
            }
            $total++
            if ([bool](Get-LabSettingValue -Object $scenario -Name 'Skipped')) {
                $skipped++
            }
            elseif ([bool](Get-LabSettingValue -Object $scenario -Name 'Success')) {
                $passed++
            }
            else {
                $failedNames.Add([string](Get-LabSettingValue -Object $scenario -Name 'Name'))
            }
        }
        $reported = Get-LabSettingValue -Object $report -Name 'DomainControllerBuilds'
        foreach ($vmName in (Get-LabSettingName -Object $reported)) {
            $builds[$vmName] = [string](Get-LabSettingValue -Object $reported -Name $vmName)
        }
    }
    elseif ($null -ne $performanceFile) {
        $total = 1
        if ($Success) {
            $passed = 1
        }
        $reportPath = $performanceFile.FullName
    }

    if (($builds.Count -eq 0) -and ($null -ne $performanceFile)) {
        $performance = Get-Content -LiteralPath $performanceFile.FullName -Raw | ConvertFrom-Json
        $reported = Get-LabSettingValue -Object $performance -Name 'DomainControllerBuilds'
        foreach ($vmName in (Get-LabSettingName -Object $reported)) {
            $builds[$vmName] = [string](Get-LabSettingValue -Object $reported -Name $vmName)
        }
    }

    return [pscustomobject][ordered]@{
        Total           = $total
        Passed          = $passed
        Failed          = ($total - $passed - $skipped)
        Skipped         = $skipped
        FailedScenarios = $failedNames.ToArray()
        Builds          = $builds
        Report          = $reportPath
    }
}

function Get-LabRunStatusDescription {
    <#
    .SYNOPSIS
        The one-line description of the jim-ad-lab commit status: scenario counts and the domain controller OS build,
        for example "23/23 scenarios passed; DC build 26100.4652".

    .DESCRIPTION
        GitHub caps a status description at 140 characters, and a longer one is refused outright, which would lose
        the status. The text is cut with an ellipsis rather than risk that.
    #>
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)]
        [object]$Summary,

        [ValidateRange(20, 140)]
        [int]$MaxLength = 140
    )

    $total = [int](Get-LabSettingValue -Object $Summary -Name 'Total')
    $passed = [int](Get-LabSettingValue -Object $Summary -Name 'Passed')
    $skipped = [int](Get-LabSettingValue -Object $Summary -Name 'Skipped')

    if ($total -eq 0) {
        $counts = 'no scenarios ran'
    }
    else {
        $noun = 'scenarios'
        if ($total -eq 1) {
            $noun = 'scenario'
        }
        $counts = "$passed/$total $noun passed"
        if ($skipped -gt 0) {
            $counts += ", $skipped skipped"
        }
    }

    $buildValues = @()
    $builds = Get-LabSettingValue -Object $Summary -Name 'Builds'
    foreach ($vmName in (Get-LabSettingName -Object $builds)) {
        $value = [string](Get-LabSettingValue -Object $builds -Name $vmName)
        if ((-not [string]::IsNullOrWhiteSpace($value)) -and ($value -ne 'unknown')) {
            $buildValues += $value
        }
    }
    $distinct = @($buildValues | Sort-Object -Unique)
    if ($distinct.Count -eq 0) {
        $buildText = 'DC build unknown'
    }
    elseif ($distinct.Count -eq 1) {
        $buildText = 'DC build ' + $distinct[0]
    }
    else {
        $buildText = 'DC builds ' + ($distinct -join ', ')
    }

    $text = "$counts; $buildText"
    if ($text.Length -gt $MaxLength) {
        $text = $text.Substring(0, $MaxLength - 3) + '...'
    }
    return $text
}

# ---------------------------------------------------------------------------------------------
# Rebuild: the phases (orchestration over an adapter)
# ---------------------------------------------------------------------------------------------
#
# Invoke-LabRebuildPhase decides and sequences; everything that touches Hyper-V is an adapter operation, supplied by
# host/Invoke-LabRebuild.ps1 (and by a fake in the tests). The adapter is a hashtable of script blocks:
#
#   GetVm         { param($Name) }                 -> $null, or an object with Name, State ('Running', 'Off', ...),
#                                                     Notes, CreatedUtc (datetime) and HasBaseline (bool)
#   StopVm        { param($Name) }                 shut the VM down (turn it off if it will not stop)
#   RestoreVm     { param($Name, $Checkpoint) }    apply the checkpoint, start the VM, wait until it answers
#   RenameVm      { param($Name, $NewName) }
#   SetNotes      { param($Name, $Notes) }
#   RemoveVm      { param($Name) }                 remove the VM, its checkpoints, its disk and its folder
#   BuildVm       { param($Parameters) }           New-LabDomainController.ps1 with these parameters (a hashtable)
#   GetGuestBuild { param($Name) }                 -> the guest OS build string, or $null
#   NowUtc        { }                              -> the current UTC time
#   Log           { param($Text) }

function Assert-LabRebuildVm {
    # Internal: the rebuild only ever touches VMs this lab created.
    param(
        [AllowNull()]
        [object]$Vm,

        [Parameter(Mandatory)]
        [string]$Name
    )

    if (($null -ne $Vm) -and (-not (Test-LabVmNote -Notes ([string]$Vm.Notes)))) {
        throw "'$Name' exists but was not created by this lab (its Notes carry no lab marker); refusing to touch it."
    }
}

function ConvertTo-LabRebuildResult {
    # Internal: one role's outcome, in the shape the phase's JSON reports.
    param(
        [Parameter(Mandatory)]
        [object]$Role,

        [Parameter(Mandatory)]
        [string]$Action,

        [string]$Detail = '',

        [AllowNull()]
        [object]$GuestBuild
    )

    return [pscustomobject][ordered]@{
        role       = $Role.Role
        live       = $Role.LiveName
        candidate  = $Role.CandidateName
        prev       = $Role.PrevName
        action     = $Action
        detail     = $Detail
        guestBuild = $GuestBuild
    }
}

function Get-LabRebuildGuestBuild {
    # Internal: the guest OS build of a VM, or $null when it cannot be read (an off VM, a guest still booting).
    param(
        [Parameter(Mandatory)]
        [hashtable]$Adapter,

        [Parameter(Mandatory)]
        [string]$Name
    )

    try {
        $build = & $Adapter.GetGuestBuild $Name
        if ([string]::IsNullOrWhiteSpace([string]$build)) {
            return $null
        }
        return [string]$build
    }
    catch {
        & $Adapter.Log "Could not read the guest OS build of ${Name}: $($_.Exception.Message)"
        return $null
    }
}

function Invoke-LabRebuildRole {
    # Internal: one phase for one domain controller.
    param(
        [Parameter(Mandatory)]
        [string]$Phase,

        [Parameter(Mandatory)]
        [object]$Role,

        [Parameter(Mandatory)]
        [hashtable]$Adapter,

        [bool]$Fresh,
        [int]$MaxCandidateAgeHours,
        [int]$PrevRetentionDays
    )

    $live = & $Adapter.GetVm $Role.LiveName
    $candidate = & $Adapter.GetVm $Role.CandidateName
    $prev = & $Adapter.GetVm $Role.PrevName
    Assert-LabRebuildVm -Vm $live -Name $Role.LiveName
    Assert-LabRebuildVm -Vm $candidate -Name $Role.CandidateName
    Assert-LabRebuildVm -Vm $prev -Name $Role.PrevName
    $now = ([datetime](& $Adapter.NowUtc)).ToUniversalTime()

    switch ($Phase) {
        'Build' {
            $createdUtc = [datetime]::MinValue
            $hasBaseline = $false
            if ($null -ne $candidate) {
                $createdUtc = [datetime]$candidate.CreatedUtc
                $hasBaseline = [bool]$candidate.HasBaseline
            }
            $action = Get-LabRebuildBuildAction -CandidateExists ($null -ne $candidate) -CandidateCreatedUtc $createdUtc `
                -CandidateHasBaseline $hasBaseline -NowUtc $now -MaxAgeHours $MaxCandidateAgeHours -Fresh:$Fresh

            # The candidate is built with the live domain controller's address, so the live one must be off first.
            if (($null -ne $live) -and ($live.State -ne 'Off')) {
                & $Adapter.Log "Stopping $($Role.LiveName): its address is the candidate's."
                & $Adapter.StopVm $Role.LiveName
            }

            if ($action -eq 'Reuse') {
                $detail = "$($Role.CandidateName) is recent and already has its baseline checkpoint; reusing it."
            }
            else {
                if ($action -eq 'Replace') {
                    & $Adapter.Log "Removing the earlier $($Role.CandidateName) before building a new one."
                    & $Adapter.RemoveVm $Role.CandidateName
                }
                elseif ($action -eq 'Resume') {
                    & $Adapter.Log "Resuming the unfinished build of $($Role.CandidateName)."
                }
                & $Adapter.BuildVm $Role.BuildParameters
                $built = & $Adapter.GetVm $Role.CandidateName
                if (($null -eq $built) -or (-not [bool]$built.HasBaseline)) {
                    throw "The build of '$($Role.CandidateName)' finished but it has no baseline checkpoint."
                }
                $detail = "$($Role.CandidateName) built and checkpointed as baseline."
            }
            return (ConvertTo-LabRebuildResult -Role $Role -Action $action -Detail $detail -GuestBuild (Get-LabRebuildGuestBuild -Adapter $Adapter -Name $Role.CandidateName))
        }

        'Stage' {
            if ($null -eq $candidate) {
                throw "'$($Role.CandidateName)' does not exist; run the Build phase first."
            }
            if (-not [bool]$candidate.HasBaseline) {
                throw "'$($Role.CandidateName)' has no baseline checkpoint; run the Build phase again."
            }
            if (($null -ne $live) -and ($live.State -ne 'Off')) {
                & $Adapter.Log "Stopping $($Role.LiveName)."
                & $Adapter.StopVm $Role.LiveName
            }
            & $Adapter.Log "Starting $($Role.CandidateName) from its baseline checkpoint."
            & $Adapter.RestoreVm $Role.CandidateName 'baseline'
            return (ConvertTo-LabRebuildResult -Role $Role -Action 'Staged' -Detail "$($Role.LiveName) is off and $($Role.CandidateName) is running on its address." -GuestBuild (Get-LabRebuildGuestBuild -Adapter $Adapter -Name $Role.CandidateName))
        }

        'Promote' {
            $steps = @(Get-LabPromotePlan -LiveExists ($null -ne $live) -CandidateExists ($null -ne $candidate) -PrevExists ($null -ne $prev))
            if ($steps.Count -eq 0) {
                return (ConvertTo-LabRebuildResult -Role $Role -Action 'AlreadyPromoted' -Detail "There is no $($Role.CandidateName) to promote; $($Role.LiveName) is already the live set." -GuestBuild (Get-LabRebuildGuestBuild -Adapter $Adapter -Name $Role.LiveName))
            }
            if (-not [bool]$candidate.HasBaseline) {
                throw "'$($Role.CandidateName)' has no baseline checkpoint; refusing to promote it."
            }
            $notes = @()
            foreach ($step in $steps) {
                if ($step -eq 'RemovePrev') {
                    $earlier = Get-LabVmNoteValue -Notes ([string]$prev.Notes) -Key 'demoted'
                    $notes += "removed the earlier $($Role.PrevName) (demoted $earlier)"
                    & $Adapter.Log "Removing the earlier $($Role.PrevName) to make way."
                    & $Adapter.RemoveVm $Role.PrevName
                }
                elseif ($step -eq 'RenameLiveToPrev') {
                    if ($live.State -ne 'Off') {
                        & $Adapter.StopVm $Role.LiveName
                    }
                    $stamped = Set-LabVmNoteValue -Notes ([string]$live.Notes) -Key 'demoted' -Value ($now.ToString('yyyy-MM-ddTHH:mm:ssZ', [System.Globalization.CultureInfo]::InvariantCulture))
                    & $Adapter.SetNotes $Role.LiveName $stamped
                    & $Adapter.Log "Renaming $($Role.LiveName) to $($Role.PrevName)."
                    & $Adapter.RenameVm $Role.LiveName $Role.PrevName
                    $notes += "$($Role.LiveName) is now $($Role.PrevName)"
                }
                elseif ($step -eq 'RenameCandidateToLive') {
                    & $Adapter.Log "Renaming $($Role.CandidateName) to $($Role.LiveName)."
                    & $Adapter.RenameVm $Role.CandidateName $Role.LiveName
                    $notes += "$($Role.CandidateName) is now $($Role.LiveName)"
                }
            }
            if (($null -eq (& $Adapter.GetVm $Role.LiveName)) -or ($null -ne (& $Adapter.GetVm $Role.CandidateName))) {
                throw "After promoting, expected $($Role.LiveName) to exist and $($Role.CandidateName) not to; the lab is in an unexpected state."
            }
            return (ConvertTo-LabRebuildResult -Role $Role -Action 'Promoted' -Detail ($notes -join '; ') -GuestBuild (Get-LabRebuildGuestBuild -Adapter $Adapter -Name $Role.LiveName))
        }

        'Rollback' {
            $steps = @(Get-LabRollbackPlan -LiveExists ($null -ne $live) -CandidateExists ($null -ne $candidate) -PrevExists ($null -ne $prev))
            foreach ($step in $steps) {
                if ($step -eq 'StopCandidate') {
                    if ($candidate.State -ne 'Off') {
                        & $Adapter.Log "Stopping $($Role.CandidateName); it is left in place for diagnosis."
                        & $Adapter.StopVm $Role.CandidateName
                    }
                }
                elseif ($step -eq 'RenamePrevToLive') {
                    $cleared = Set-LabVmNoteValue -Notes ([string]$prev.Notes) -Key 'demoted' -Value ''
                    & $Adapter.SetNotes $Role.PrevName $cleared
                    & $Adapter.Log "Renaming $($Role.PrevName) back to $($Role.LiveName)."
                    & $Adapter.RenameVm $Role.PrevName $Role.LiveName
                }
                elseif ($step -eq 'StartLive') {
                    & $Adapter.Log "Starting $($Role.LiveName) from its baseline checkpoint."
                    & $Adapter.RestoreVm $Role.LiveName 'baseline'
                }
            }
            return (ConvertTo-LabRebuildResult -Role $Role -Action 'RolledBack' -Detail "$($Role.LiveName) is running again; $($Role.CandidateName) is stopped and left for diagnosis." -GuestBuild (Get-LabRebuildGuestBuild -Adapter $Adapter -Name $Role.LiveName))
        }

        'Prune' {
            if ($null -eq $prev) {
                return (ConvertTo-LabRebuildResult -Role $Role -Action 'NothingToPrune' -Detail "There is no $($Role.PrevName).")
            }
            $demoted = $null
            $stamp = Get-LabVmNoteValue -Notes ([string]$prev.Notes) -Key 'demoted'
            if (-not [string]::IsNullOrEmpty($stamp)) {
                $parsed = [datetime]::MinValue
                if ([datetime]::TryParseExact($stamp, 'yyyy-MM-ddTHH:mm:ssZ', [System.Globalization.CultureInfo]::InvariantCulture, ([System.Globalization.DateTimeStyles]::AdjustToUniversal -bor [System.Globalization.DateTimeStyles]::AssumeUniversal), [ref]$parsed)) {
                    $demoted = $parsed
                }
            }
            $plan = @(Get-LabPrunePlan -Prev @([pscustomobject]@{ Name = $Role.PrevName; DemotedUtc = $demoted }) -NowUtc $now -MinAgeDays $PrevRetentionDays)[0]
            if ($plan.Remove) {
                & $Adapter.Log "Removing $($Role.PrevName): $($plan.Reason)."
                & $Adapter.RemoveVm $Role.PrevName
                return (ConvertTo-LabRebuildResult -Role $Role -Action 'Pruned' -Detail "$($Role.PrevName) $($plan.Reason)")
            }
            return (ConvertTo-LabRebuildResult -Role $Role -Action 'Kept' -Detail "$($Role.PrevName) $($plan.Reason)")
        }

        'Status' {
            $parts = @()
            foreach ($pair in @(@('live', $live, $Role.LiveName), @('candidate', $candidate, $Role.CandidateName), @('prev', $prev, $Role.PrevName))) {
                if ($null -eq $pair[1]) {
                    $parts += "$($pair[0]) $($pair[2]): absent"
                }
                else {
                    $baseline = 'no baseline'
                    if ([bool]$pair[1].HasBaseline) {
                        $baseline = 'baseline'
                    }
                    $parts += "$($pair[0]) $($pair[2]): $($pair[1].State), $baseline"
                }
            }
            $build = $null
            if (($null -ne $live) -and ($live.State -eq 'Running')) {
                $build = Get-LabRebuildGuestBuild -Adapter $Adapter -Name $Role.LiveName
            }
            return (ConvertTo-LabRebuildResult -Role $Role -Action 'Reported' -Detail ($parts -join '; ') -GuestBuild $build)
        }
    }
}

function Invoke-LabRebuildPhase {
    <#
    .SYNOPSIS
        Runs one phase of the monthly rebuild over the domain controllers of a plan, through an adapter, and returns
        one result per domain controller.

    .DESCRIPTION
        Every phase can be run again and converges:

          Prune     removes each <live>-prev demoted at least PrevRetentionDays ago (run first, so its disk is free
                    before three new ones are built)
          Build     stops the live VM (the candidate takes its address), then builds <live>-candidate, taking the
                    baseline checkpoint; reuses, resumes or replaces one that is already there
                    (Get-LabRebuildBuildAction)
          Stage     stops the live VM and starts the candidate from its baseline checkpoint, waiting until it answers
          Promote   names the live VM <live>-prev (stamping when) and the candidate <live>
                    (Get-LabPromotePlan); interrupted, it finishes on the next run
          Rollback  stops the candidate (left for diagnosis) and starts the live VM again (Get-LabRollbackPlan)
          Status    reports which VMs exist and what state they are in

        Nothing here touches a VM the lab did not create (its Notes must carry the lab marker), and a failure throws
        naming the domain controller and step.

    .PARAMETER Plan
        ConvertFrom-LabRebuildSetting output.

    .PARAMETER Adapter
        The operations that reach Hyper-V; see the notes above this function.

    .PARAMETER Role
        Limit the phase to these live VM names (a comma-separated list is accepted). Default: all three.
    #>
    [CmdletBinding()]
    [OutputType([pscustomobject[]])]
    param(
        [Parameter(Mandatory)]
        [ValidateSet('Prune', 'Build', 'Stage', 'Promote', 'Rollback', 'Status')]
        [string]$Phase,

        [Parameter(Mandatory)]
        [object]$Plan,

        [Parameter(Mandatory)]
        [hashtable]$Adapter,

        [string[]]$Role = @(),

        [switch]$Fresh,

        [ValidateRange(1, 720)]
        [int]$MaxCandidateAgeHours = 24,

        [ValidateRange(1, 365)]
        [int]$PrevRetentionDays = 7
    )

    foreach ($operation in @('GetVm', 'StopVm', 'RestoreVm', 'RenameVm', 'SetNotes', 'RemoveVm', 'BuildVm', 'GetGuestBuild', 'NowUtc', 'Log')) {
        if (-not $Adapter.ContainsKey($operation)) {
            throw "The rebuild adapter has no '$operation' operation."
        }
    }

    $roles = @($Plan.Roles)
    $wanted = @($Role | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ -ne '' })
    if ($wanted.Count -gt 0) {
        $known = @($roles | ForEach-Object { $_.LiveName })
        $unknown = @($wanted | Where-Object { $known -notcontains $_ })
        if ($unknown.Count -gt 0) {
            throw "Unknown domain controller '$($unknown -join ', ')'; the settings define $($known -join ', ')."
        }
        $roles = @($roles | Where-Object { $wanted -contains $_.LiveName })
    }

    if ($Phase -eq 'Promote') {
        # Check every domain controller before renaming any of them: a promotion that stopped after the first would
        # leave the three forests on different builds.
        foreach ($item in $roles) {
            try {
                $liveVm = & $Adapter.GetVm $item.LiveName
                $candidateVm = & $Adapter.GetVm $item.CandidateName
                $prevVm = & $Adapter.GetVm $item.PrevName
                Assert-LabRebuildVm -Vm $liveVm -Name $item.LiveName
                Assert-LabRebuildVm -Vm $candidateVm -Name $item.CandidateName
                Assert-LabRebuildVm -Vm $prevVm -Name $item.PrevName
                $steps = @(Get-LabPromotePlan -LiveExists ($null -ne $liveVm) -CandidateExists ($null -ne $candidateVm) -PrevExists ($null -ne $prevVm))
                if (($steps.Count -gt 0) -and (-not [bool]$candidateVm.HasBaseline)) {
                    throw "'$($item.CandidateName)' has no baseline checkpoint; refusing to promote it."
                }
            }
            catch {
                throw "Promote failed for $($item.LiveName) before anything was renamed: $($_.Exception.Message)"
            }
        }
    }

    $results = New-Object System.Collections.Generic.List[object]
    foreach ($item in $roles) {
        try {
            $results.Add((Invoke-LabRebuildRole -Phase $Phase -Role $item -Adapter $Adapter -Fresh ([bool]$Fresh) -MaxCandidateAgeHours $MaxCandidateAgeHours -PrevRetentionDays $PrevRetentionDays))
        }
        catch {
            throw "$Phase failed for $($item.LiveName): $($_.Exception.Message)"
        }
    }
    return $results.ToArray()
}

# ---------------------------------------------------------------------------------------------
# Windows only: ISO creation (host)
# ---------------------------------------------------------------------------------------------

function New-LabIsoImage {
    <#
    .SYNOPSIS
        Builds an ISO from a folder using the IMAPI2 file system image COM object, so no extra tool is needed.

    .DESCRIPTION
        The unattend ISO is tiny, holding autounattend.xml at its root. Joliet and UDF are both written so the
        16-character file name survives (ISO 9660 alone would shorten it to 8.3). The C# helper only copies the
        COM stream to a file; it is compiled once per session and targets the C# 5 compiler Windows PowerShell 5.1
        ships with.
    #>
    [CmdletBinding(SupportsShouldProcess)]
    param(
        [Parameter(Mandatory)]
        [string]$SourceDirectory,

        [Parameter(Mandatory)]
        [string]$IsoPath,

        [string]$VolumeLabel = 'UNATTEND'
    )

    Assert-LabWindows -Feature 'Creating an ISO with IMAPI2'
    if (-not $PSCmdlet.ShouldProcess($IsoPath, 'Create ISO image')) {
        return
    }

    if (-not ('LabIsoStream' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

public static class LabIsoStream
{
    public static void WriteToFile(string path, object stream, int blockSize, int totalBlocks)
    {
        IStream source = stream as IStream;
        if (source == null)
        {
            throw new ArgumentException("The image stream is not an IStream.");
        }
        byte[] buffer = new byte[blockSize];
        IntPtr bytesRead = Marshal.AllocHGlobal(sizeof(int));
        try
        {
            using (FileStream target = File.Create(path))
            {
                for (int block = 0; block < totalBlocks; block++)
                {
                    source.Read(buffer, blockSize, bytesRead);
                    int count = Marshal.ReadInt32(bytesRead);
                    target.Write(buffer, 0, count);
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(bytesRead);
        }
    }
}
'@
    }

    $image = New-Object -ComObject IMAPI2FS.MsftFileSystemImage
    $result = $null
    try {
        # FsiFileSystems: ISO9660 = 1, Joliet = 2, UDF = 4. Joliet is an extension of ISO 9660, so IMAPI2 refuses
        # Joliet without it ("The value specified for parameter 'fileSystems' is not valid"): all three, 7.
        $image.FileSystemsToCreate = 7
        $image.VolumeName = $VolumeLabel
        $image.Root.AddTree($SourceDirectory, $false)
        $result = $image.CreateResultImage()
        [LabIsoStream]::WriteToFile($IsoPath, $result.ImageStream, $result.BlockSize, $result.TotalBlocks)
    }
    finally {
        if ($null -ne $result) { [void][System.Runtime.InteropServices.Marshal]::ReleaseComObject($result) }
        [void][System.Runtime.InteropServices.Marshal]::ReleaseComObject($image)
    }
}

# ---------------------------------------------------------------------------------------------
# Windows only: talking to a guest through PowerShell Direct (host)
# ---------------------------------------------------------------------------------------------

function Wait-LabGuestReady {
    <#
    .SYNOPSIS
        Waits until PowerShell Direct answers, and returns the credential that worked.

    .DESCRIPTION
        Tries each credential in turn on every attempt, because before promotion only the local
        Administrator works and afterwards only the domain one does. Retries until the timeout.
    #>
    [CmdletBinding()]
    [OutputType([System.Management.Automation.PSCredential])]
    param(
        [Parameter(Mandatory)]
        [string]$VMName,

        [Parameter(Mandatory)]
        [System.Management.Automation.PSCredential[]]$Credential,

        [int]$TimeoutSeconds = 900,

        [int]$IntervalSeconds = 10
    )

    Assert-LabWindows -Feature 'PowerShell Direct'
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $lastError = 'no attempt was made'
    while ((Get-Date) -lt $deadline) {
        foreach ($candidate in $Credential) {
            try {
                $null = Invoke-Command -VMName $VMName -Credential $candidate -ScriptBlock { $env:COMPUTERNAME } -ErrorAction Stop
                return $candidate
            }
            catch {
                $lastError = $_.Exception.Message
            }
        }
        Start-Sleep -Seconds $IntervalSeconds
    }
    throw "The guest '$VMName' did not answer PowerShell Direct within $TimeoutSeconds seconds. Last error: $lastError"
}

function Restart-LabGuest {
    <#
    .SYNOPSIS
        Restarts a VM and waits for the guest to answer again.

    .DESCRIPTION
        Restarts through Hyper-V rather than from inside the guest, so the PowerShell Direct session is not
        broken mid-command. Waits for the VM's uptime to reset before polling the guest, so a guest that has
        not yet begun to shut down is not mistaken for one that has come back.

        The restart is a clean one (-Type Reboot, through the guest's Shutdown integration service), never the
        default, which is a hard reset: "like powering the computer down ... This can result in data loss"
        (https://learn.microsoft.com/powershell/module/hyper-v/restart-vm). Every phase restarts straight after
        writing its changes, so a hard reset lost what had not yet reached the disk: a Trusted Root import two
        seconds old, and very likely promotion's own final settings (Active Directory Web Services was left
        Disabled on the first real build).
    #>
    [CmdletBinding(SupportsShouldProcess)]
    [OutputType([System.Management.Automation.PSCredential])]
    param(
        [Parameter(Mandatory)]
        [string]$VMName,

        [Parameter(Mandatory)]
        [System.Management.Automation.PSCredential[]]$Credential,

        [int]$TimeoutSeconds = 900
    )

    Assert-LabWindows -Feature 'Restarting a virtual machine'
    if (-not $PSCmdlet.ShouldProcess($VMName, 'Restart the virtual machine')) {
        return
    }

    $before = (Get-VM -Name $VMName).Uptime
    Restart-VM -Name $VMName -Type Reboot -Force -Confirm:$false
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        $vm = Get-VM -Name $VMName
        if (($vm.State -eq 'Running') -and ($vm.Uptime -lt $before)) {
            break
        }
        Start-Sleep -Seconds 2
    }
    $remaining = [int][math]::Max(60, ($deadline - (Get-Date)).TotalSeconds)
    return (Wait-LabGuestReady -VMName $VMName -Credential $Credential -TimeoutSeconds $remaining)
}

function Copy-LabAssetToGuest {
    <#
    .SYNOPSIS
        Copies files into a folder in the guest over a PowerShell Direct session.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$VMName,

        [Parameter(Mandatory)]
        [System.Management.Automation.PSCredential]$Credential,

        [Parameter(Mandatory)]
        [string[]]$Path,

        [Parameter(Mandatory)]
        [string]$Destination
    )

    Assert-LabWindows -Feature 'Copying files into a guest'
    $session = New-PSSession -VMName $VMName -Credential $Credential -ErrorAction Stop
    try {
        Invoke-Command -Session $session -ScriptBlock {
            if (-not (Test-Path -LiteralPath $using:Destination)) { New-Item -ItemType Directory -Path $using:Destination -Force | Out-Null }
        }
        foreach ($file in $Path) {
            Copy-Item -LiteralPath $file -Destination $Destination -ToSession $session -Force
        }
    }
    finally {
        Remove-PSSession -Session $session -ErrorAction SilentlyContinue
    }
}

function Invoke-LabGuestPhase {
    <#
    .SYNOPSIS
        Runs one phase of Initialize-LabDomainController.ps1 in the guest and returns its result object.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$VMName,

        [Parameter(Mandatory)]
        [System.Management.Automation.PSCredential]$Credential,

        [Parameter(Mandatory)]
        [ValidateSet('License', 'Prepare', 'Promote', 'Extend', 'Configure', 'Verify')]
        [string]$Phase,

        [Parameter(Mandatory)]
        [System.Collections.IDictionary]$Settings,

        [string]$GuestScript = 'C:\jim-ad-lab\Initialize-LabDomainController.ps1'
    )

    Assert-LabWindows -Feature 'Running a guest phase'
    $arguments = Get-LabGuestPhaseArgument -Phase $Phase -Settings $Settings
    Write-Verbose ('Guest: {0} {1}' -f $GuestScript, (ConvertTo-LabArgumentString -Argument $arguments))

    # A plain Hashtable crosses the remoting boundary; SecureString values travel encrypted by the session.
    $splat = @{}
    foreach ($key in $arguments.Keys) { $splat[$key] = $arguments[$key] }

    $output = Invoke-Command -VMName $VMName -Credential $Credential -ErrorAction Stop -ScriptBlock {
        param($script, $splat)
        & $script @splat
    } -ArgumentList $GuestScript, $splat

    $result = @($output | Where-Object { $null -ne $_ -and $_.PSObject.Properties['RebootRequired'] }) | Select-Object -Last 1
    if ($null -eq $result) {
        throw "Phase $Phase returned no result object."
    }
    return $result
}

function Get-LabGuestOsBuild {
    <#
    .SYNOPSIS
        Reads the guest's OS build from its registry over PowerShell Direct.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$VMName,

        [Parameter(Mandatory)]
        [System.Management.Automation.PSCredential]$Credential
    )

    Assert-LabWindows -Feature 'Reading a guest OS build'
    $key = Invoke-Command -VMName $VMName -Credential $Credential -ErrorAction Stop -ScriptBlock {
        $current = Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion'
        [pscustomobject]@{
            ProductName    = $current.ProductName
            DisplayVersion = $current.PSObject.Properties['DisplayVersion'].Value
            CurrentBuild   = $current.CurrentBuild
            Ubr            = $current.PSObject.Properties['UBR'].Value
        }
    }
    return (ConvertTo-LabOsBuild -ProductName $key.ProductName -DisplayVersion $key.DisplayVersion -CurrentBuild $key.CurrentBuild -Ubr $key.Ubr)
}

function Wait-LabTcpPort {
    <#
    .SYNOPSIS
        Waits until a TCP connection to a port succeeds, from the machine this runs on.
    #>
    [CmdletBinding()]
    [OutputType([bool])]
    param(
        [Parameter(Mandatory)]
        [string]$HostName,

        [Parameter(Mandatory)]
        [int]$Port,

        [int]$TimeoutSeconds = 300
    )

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        $client = New-Object System.Net.Sockets.TcpClient
        try {
            $attempt = $client.BeginConnect($HostName, $Port, $null, $null)
            if ($attempt.AsyncWaitHandle.WaitOne(3000) -and $client.Connected) {
                $client.EndConnect($attempt)
                return $true
            }
        }
        catch {
            Write-Verbose "Connect to ${HostName}:$Port failed: $($_.Exception.Message)"
        }
        finally {
            $client.Close()
        }
        Start-Sleep -Seconds 3
    }
    return $false
}

function Send-LabKeyPress {
    <#
    .SYNOPSIS
        Types a key into a VM's console through the Hyper-V WMI keyboard, to answer "Press any key to boot from CD".
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$VMName,

        [int]$KeyCode = 13,

        [int]$Count = 10,

        [int]$IntervalSeconds = 1
    )

    Assert-LabWindows -Feature 'Sending a key press to a virtual machine'
    $computer = Get-CimInstance -Namespace 'root\virtualization\v2' -ClassName Msvm_ComputerSystem -Filter "ElementName='$VMName'"
    $keyboard = Get-CimAssociatedInstance -InputObject $computer -ResultClassName Msvm_Keyboard
    for ($i = 0; $i -lt $Count; $i++) {
        $null = Invoke-CimMethod -InputObject $keyboard -MethodName TypeKey -Arguments @{ keyCode = $KeyCode }
        Start-Sleep -Seconds $IntervalSeconds
    }
}

# ---------------------------------------------------------------------------------------------
# Windows only: guest side, Active Directory (runs inside a domain controller)
# ---------------------------------------------------------------------------------------------

function Get-LabGroupSid {
    # The SID of the group the delegation is granted to; throws when it does not exist.
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [ValidatePattern('^[A-Za-z0-9 ._-]+$')]
        [string]$GroupName = 'JIM Connectors'
    )

    Assert-LabWindows -Feature 'Reading a group SID'
    Import-Module ActiveDirectory -ErrorAction Stop
    $group = Get-ADGroup -Filter "Name -eq '$GroupName'" -ErrorAction Stop
    if ($null -eq $group) {
        throw "The delegation group '$GroupName' was not found; the Configure phase should have created it."
    }
    return $group.SID.Value
}

function Grant-LabContainerDelegation {
    <#
    .SYNOPSIS
        Applies JIM's delegation (jim-ad-delegation.acl, unchanged) to a container and everything below it.

    .DESCRIPTION
        The single implementation the Configure phase and Grant-LabDelegation.ps1 both use. Renders the
        SDDL for the JIM Connectors group, and adds the entries to the container's DACL unless the group is
        already in it (idempotent, as jim-delegate.sh is). Reads the DACL back afterwards and throws if the
        group is still absent, so a delegation that did not apply fails here rather than as an access error
        hours later.

        Returns { ContainerDn, Outcome = Delegated | AlreadyPresent, TrusteeSid }.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [ValidatePattern('^[A-Za-z]{2}=.+')]
        [string]$ContainerDn,

        [Parameter(Mandatory)]
        [string]$AclPath,

        [ValidatePattern('^[A-Za-z0-9 ._-]+$')]
        [string]$GroupName = 'JIM Connectors'
    )

    Assert-LabWindows -Feature 'Applying the delegation'
    Import-Module ActiveDirectory -ErrorAction Stop

    $sid = Get-LabGroupSid -GroupName $GroupName
    $sddl = ConvertTo-LabDelegationSddl -AclPath $AclPath -TrusteeSid $sid
    # -Path with an escaped name, never -LiteralPath: on the AD: drive, Get-Acl -LiteralPath returns nothing and writes
    # "Cannot find path '//RootDSE/...'" (seen on Windows Server 2025, Windows PowerShell 5.1), and -ErrorAction Stop so
    # a failed read can never be mistaken for an empty DACL.
    $path = Get-LabAdProviderPath -DistinguishedName $ContainerDn
    if (-not (Test-Path -Path $path)) {
        throw "Could not read the permissions of '$ContainerDn'; does it exist?"
    }

    $access = [System.Security.AccessControl.AccessControlSections]::Access
    $acl = Get-Acl -Path $path -ErrorAction Stop
    $existing = $acl.GetSecurityDescriptorSddlForm($access)
    if (Test-LabSddlContainsSid -Sddl $existing -Sid $sid) {
        return [pscustomobject][ordered]@{ ContainerDn = $ContainerDn; Outcome = 'AlreadyPresent'; TrusteeSid = $sid }
    }

    $merged = Merge-LabDelegationSddl -ExistingSddl $existing -DelegationSddl $sddl -TrusteeSid $sid
    $acl.SetSecurityDescriptorSddlForm($merged, $access)
    Set-Acl -Path $path -AclObject $acl -ErrorAction Stop

    $after = (Get-Acl -Path $path -ErrorAction Stop).GetSecurityDescriptorSddlForm($access)
    if (-not (Test-LabSddlContainsSid -Sddl $after -Sid $sid)) {
        throw "The delegation was written to '$ContainerDn' but reading it back does not show the '$GroupName' group."
    }
    return [pscustomobject][ordered]@{ ContainerDn = $ContainerDn; Outcome = 'Delegated'; TrusteeSid = $sid }
}

function Get-LabTombstoneDescriptor {
    # Reads the Deleted Objects container's security descriptor over LDAP with the show-deleted and
    # security-descriptor-flags controls (Active Directory's provider cannot see deleted objects). Returns SDDL.
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)]
        [string]$DomainDn
    )

    Assert-LabWindows -Feature 'Reading the Deleted Objects permissions'
    Add-Type -AssemblyName System.DirectoryServices.Protocols
    $protocols = 'System.DirectoryServices.Protocols'

    $identifier = New-Object "$protocols.LdapDirectoryIdentifier" -ArgumentList 'localhost'
    $connection = New-Object "$protocols.LdapConnection" -ArgumentList $identifier
    try {
        $connection.SessionOptions.ProtocolVersion = 3
        $connection.Bind()
        $request = New-Object "$protocols.SearchRequest" -ArgumentList "CN=Deleted Objects,$DomainDn", '(objectClass=*)', ([System.DirectoryServices.Protocols.SearchScope]::Base), 'nTSecurityDescriptor'
        [void]$request.Controls.Add((New-Object "$protocols.ShowDeletedControl"))
        $masks = [System.DirectoryServices.Protocols.SecurityMasks]'Owner,Dacl'
        [void]$request.Controls.Add((New-Object "$protocols.SecurityDescriptorFlagControl" -ArgumentList $masks))
        $response = $connection.SendRequest($request)
        if ($response.Entries.Count -ne 1) {
            throw 'The Deleted Objects container did not come back from the directory.'
        }
        $bytes = [byte[]]$response.Entries[0].Attributes['nTSecurityDescriptor'][0]
        $descriptor = [System.Security.AccessControl.RawSecurityDescriptor]::new($bytes, 0)
        return $descriptor.GetSddlForm([System.Security.AccessControl.AccessControlSections]'Owner,Access')
    }
    finally {
        $connection.Dispose()
    }
}

function Grant-LabTombstoneRead {
    <#
    .SYNOPSIS
        Lets the JIM Connectors group read the Deleted Objects container, so Delta Import sees deletions.

    .DESCRIPTION
        What jim-delegate.sh --tombstones does on Samba. The container's owner is SYSTEM and its default
        permissions do not let even a domain administrator read its own security descriptor, so ownership is
        taken first (dsacls /takeownership, which is also Microsoft's documented route), then the group is
        granted List Contents, Read Property and Read Permissions (LC, RP, RC): the entry (A;;LCRPRC;;;SID).
        The result is verified by reading the descriptor back over LDAP with the show-deleted control.

        Withholding this read produces no error: tombstones are simply not returned, and a Delta Import that
        should see deletions sees none of them, silently. Hence the read-back.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [ValidatePattern('^[A-Za-z0-9-]{1,15}$')]
        [string]$NetBiosName,

        [ValidatePattern('^[A-Za-z0-9 ._-]+$')]
        [string]$GroupName = 'JIM Connectors'
    )

    Assert-LabWindows -Feature 'Granting read over Deleted Objects'
    Import-Module ActiveDirectory -ErrorAction Stop

    # List Contents 0x4, Read Property 0x10, Read Control 0x20000.
    $mask = 0x20014
    $domainDn = (Get-ADDomain).DistinguishedName
    $container = "CN=Deleted Objects,$domainDn"
    $sid = Get-LabGroupSid -GroupName $GroupName

    $null = & dsacls.exe $container /takeownership 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "Could not take ownership of '$container' (dsacls exit code $LASTEXITCODE)."
    }

    if (Test-LabAclGrant -Sddl (Get-LabTombstoneDescriptor -DomainDn $domainDn) -Sid $sid -AccessMask $mask) {
        return [pscustomobject][ordered]@{ ContainerDn = $container; Outcome = 'AlreadyPresent'; TrusteeSid = $sid }
    }

    $null = & dsacls.exe $container /G "${NetBiosName}\${GroupName}:LCRPRC" 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "Could not grant read over '$container' (dsacls exit code $LASTEXITCODE)."
    }

    if (-not (Test-LabAclGrant -Sddl (Get-LabTombstoneDescriptor -DomainDn $domainDn) -Sid $sid -AccessMask $mask)) {
        throw "Read over '$container' was granted but reading the descriptor back does not show it."
    }
    return [pscustomobject][ordered]@{ ContainerDn = $container; Outcome = 'Delegated'; TrusteeSid = $sid }
}

function Get-LabCertificateDnsName {
    # The DNS names in a certificate's Subject Alternative Name extension.
    [CmdletBinding()]
    [OutputType([string[]])]
    param(
        [Parameter(Mandatory)]
        [System.Security.Cryptography.X509Certificates.X509Certificate2]$Certificate
    )

    $names = New-Object System.Collections.Generic.List[string]
    foreach ($extension in $Certificate.Extensions) {
        if ($extension.Oid.Value -eq '2.5.29.17') {
            foreach ($match in [regex]::Matches($extension.Format($false), 'DNS[ -]Name=([^,\s]+)')) {
                $names.Add($match.Groups[1].Value)
            }
        }
    }
    return $names.ToArray()
}

function Test-LabLdapsEndpoint {
    <#
    .SYNOPSIS
        Connects to a TLS port with SslStream and reports the certificate the server presents.

    .DESCRIPTION
        Certificate validation is deliberately off for this probe: the lab certificate is self-signed and the
        point is to see WHICH certificate Schannel serves, so the caller compares its thumbprint and names.
    #>
    [CmdletBinding()]
    param(
        [string]$HostName = 'localhost',

        [int]$Port = 636,

        [string]$TargetName
    )

    Assert-LabWindows -Feature 'Probing LDAPS'
    if ([string]::IsNullOrEmpty($TargetName)) { $TargetName = $HostName }

    $client = New-Object System.Net.Sockets.TcpClient
    try {
        $client.Connect($HostName, $Port)
        $callback = [System.Net.Security.RemoteCertificateValidationCallback]{ $true }
        $stream = New-Object System.Net.Security.SslStream($client.GetStream(), $false, $callback)
        try {
            $stream.AuthenticateAsClient($TargetName)
            $certificate = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2($stream.RemoteCertificate)
            return [pscustomobject][ordered]@{
                Thumbprint = $certificate.Thumbprint
                Subject    = $certificate.Subject
                DnsNames   = @(Get-LabCertificateDnsName -Certificate $certificate)
                NotAfter   = $certificate.NotAfter
            }
        }
        finally {
            $stream.Dispose()
        }
    }
    finally {
        $client.Close()
    }
}

function New-LabLdapConnection {
    <#
    .SYNOPSIS
        Opens a simple-bind LDAP connection, over LDAPS pinned to a certificate thumbprint, or plain LDAP.
    #>
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Opens a connection; changes no state.')]
    [CmdletBinding()]
    param(
        [string]$HostName = 'localhost',

        [Parameter(Mandatory)]
        [string]$BindDn,

        [Parameter(Mandatory)]
        [securestring]$Password,

        [switch]$PlainLdap,

        [string]$ExpectedThumbprint
    )

    Assert-LabWindows -Feature 'Opening an LDAP connection'
    Add-Type -AssemblyName System.DirectoryServices.Protocols
    $protocols = 'System.DirectoryServices.Protocols'

    if ($PlainLdap) {
        $identifier = New-Object "$protocols.LdapDirectoryIdentifier" -ArgumentList $HostName, 389, $false, $false
    }
    else {
        $identifier = New-Object "$protocols.LdapDirectoryIdentifier" -ArgumentList $HostName, 636, $false, $false
    }
    $connection = New-Object "$protocols.LdapConnection" -ArgumentList $identifier
    $connection.SessionOptions.ProtocolVersion = 3
    $connection.AuthType = [System.DirectoryServices.Protocols.AuthType]::Basic
    $connection.Credential = New-Object System.Net.NetworkCredential($BindDn, $Password)
    if (-not $PlainLdap) {
        $connection.SessionOptions.SecureSocketLayer = $true
        # The callback runs during Bind(), after this function has returned, so it must carry the thumbprint with
        # it: a closure, not a reference to a local that will be gone.
        $expected = $ExpectedThumbprint
        $verify = {
            # The callback receives the connection and the certificate the server presented.
            $presented = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2($args[1])
            return ([string]::IsNullOrEmpty($expected) -or ($presented.Thumbprint -eq $expected))
        }.GetNewClosure()
        $connection.SessionOptions.VerifyServerCertificate = [System.DirectoryServices.Protocols.VerifyServerCertificateCallback]$verify
    }
    return $connection
}

function Test-LabServiceAccountDelegation {
    <#
    .SYNOPSIS
        Proves the delegation works, as post-provision.sh does: as svc-jim over LDAPS, perform each of JIM's
        operations, and check the account cannot do what it should not.

    .DESCRIPTION
        Creates a probe user in OU=Users,OU=Corp, writes an attribute, sets its password (which needs the
        Reset Password right), reads its nTSecurityDescriptor (which needs Read Control), deletes it, and reads
        the tombstone. Then confirms it cannot create a user in CN=Users, and is not a Domain Admin. Returns a
        list of { Name, Passed, Detail }; the caller decides what a failure means.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$DomainDn,

        [Parameter(Mandatory)]
        [securestring]$ServiceAccountPassword,

        [string]$ExpectedThumbprint
    )

    Assert-LabWindows -Feature 'Verifying the delegation'
    Import-Module ActiveDirectory -ErrorAction Stop
    $protocols = 'System.DirectoryServices.Protocols'

    $serviceDn = "CN=svc-jim,OU=Services,$DomainDn"
    $probeDn = "CN=Delegation Probe,OU=Users,OU=Corp,$DomainDn"
    $results = New-Object System.Collections.Generic.List[object]
    $record = {
        param($name, $passed, $detail)
        $results.Add([pscustomobject][ordered]@{ Name = $name; Passed = [bool]$passed; Detail = $detail })
    }

    # A leftover probe from an interrupted run would make the create fail for the wrong reason.
    $leftover = Get-ADUser -Filter "SamAccountName -eq 'jim-probe'" -ErrorAction SilentlyContinue
    if ($null -ne $leftover) {
        Remove-ADUser -Identity $leftover -Confirm:$false
    }

    # A throw-away password for the probe, drawn from the system generator, that meets complexity.
    $entropy = New-Object byte[] 18
    $generator = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try { $generator.GetBytes($entropy) } finally { $generator.Dispose() }
    $probePassword = 'Pr!' + [Convert]::ToBase64String($entropy) + 'a1'

    $connection = New-LabLdapConnection -BindDn $serviceDn -Password $ServiceAccountPassword -ExpectedThumbprint $ExpectedThumbprint
    try {
        try {
            $connection.Bind()
            & $record 'svc-jim binds over LDAPS' $true 'simple bind accepted'
        }
        catch {
            & $record 'svc-jim binds over LDAPS' $false $_.Exception.Message
            return $results.ToArray()
        }

        $created = $false
        try {
            $attributes = @(
                [System.DirectoryServices.Protocols.DirectoryAttribute]::new('objectClass', 'user'),
                [System.DirectoryServices.Protocols.DirectoryAttribute]::new('sAMAccountName', 'jim-probe'),
                [System.DirectoryServices.Protocols.DirectoryAttribute]::new('userAccountControl', '514'),
                [System.DirectoryServices.Protocols.DirectoryAttribute]::new('displayName', 'Delegation Probe')
            )
            $null = $connection.SendRequest([System.DirectoryServices.Protocols.AddRequest]::new($probeDn, [System.DirectoryServices.Protocols.DirectoryAttribute[]]$attributes))
            $created = $true
            & $record 'svc-jim can create a user in OU=Users,OU=Corp' $true $probeDn
        }
        catch {
            & $record 'svc-jim can create a user in OU=Users,OU=Corp' $false $_.Exception.Message
        }

        if ($created) {
            try {
                $null = $connection.SendRequest([System.DirectoryServices.Protocols.ModifyRequest]::new($probeDn, [System.DirectoryServices.Protocols.DirectoryAttributeOperation]::Replace, 'department', 'Delegation'))
                & $record 'svc-jim can write an attribute on a user it created' $true 'department'
            }
            catch {
                & $record 'svc-jim can write an attribute on a user it created' $false $_.Exception.Message
            }

            try {
                # Setting a password is the Reset Password control access right, separate from write access.
                $modification = New-Object "$protocols.DirectoryAttributeModification"
                $modification.Name = 'unicodePwd'
                $modification.Operation = [System.DirectoryServices.Protocols.DirectoryAttributeOperation]::Replace
                [void]$modification.Add([byte[]][System.Text.Encoding]::Unicode.GetBytes('"' + $probePassword + '"'))
                $null = $connection.SendRequest([System.DirectoryServices.Protocols.ModifyRequest]::new($probeDn, $modification))
                & $record 'svc-jim can set a password (Reset Password right)' $true 'unicodePwd'
            }
            catch {
                & $record 'svc-jim can set a password (Reset Password right)' $false $_.Exception.Message
            }

            try {
                $read = [System.DirectoryServices.Protocols.SearchRequest]::new($probeDn, '(objectClass=*)', [System.DirectoryServices.Protocols.SearchScope]::Base, 'nTSecurityDescriptor')
                $masks = [System.DirectoryServices.Protocols.SecurityMasks]'Owner,Group,Dacl'
                [void]$read.Controls.Add((New-Object "$protocols.SecurityDescriptorFlagControl" -ArgumentList $masks))
                $response = $connection.SendRequest($read)
                $count = 0
                if ($response.Entries.Count -eq 1) { $count = $response.Entries[0].Attributes['nTSecurityDescriptor'].Count }
                & $record 'svc-jim can read nTSecurityDescriptor (reset-rights preflight)' ($count -eq 1) "values returned: $count"
            }
            catch {
                & $record 'svc-jim can read nTSecurityDescriptor (reset-rights preflight)' $false $_.Exception.Message
            }

            try {
                $null = $connection.SendRequest([System.DirectoryServices.Protocols.DeleteRequest]::new($probeDn))
                & $record 'svc-jim can delete the user it created' $true $probeDn
            }
            catch {
                & $record 'svc-jim can delete the user it created' $false $_.Exception.Message
            }
        }

        try {
            # The delete just made a tombstone. An account without read here is told nothing rather than refused.
            $tombstones = [System.DirectoryServices.Protocols.SearchRequest]::new("CN=Deleted Objects,$DomainDn", '(isDeleted=TRUE)', [System.DirectoryServices.Protocols.SearchScope]::OneLevel, 'distinguishedName')
            [void]$tombstones.Controls.Add((New-Object "$protocols.ShowDeletedControl"))
            $response = $connection.SendRequest($tombstones)
            & $record 'svc-jim can read tombstones in Deleted Objects' ($response.Entries.Count -ge 1) "tombstones returned: $($response.Entries.Count)"
        }
        catch {
            & $record 'svc-jim can read tombstones in Deleted Objects' $false $_.Exception.Message
        }

        try {
            $outside = @(
                [System.DirectoryServices.Protocols.DirectoryAttribute]::new('objectClass', 'user'),
                [System.DirectoryServices.Protocols.DirectoryAttribute]::new('sAMAccountName', 'jim-probe-outside'),
                [System.DirectoryServices.Protocols.DirectoryAttribute]::new('userAccountControl', '514')
            )
            $null = $connection.SendRequest([System.DirectoryServices.Protocols.AddRequest]::new("CN=Should Not Exist,CN=Users,$DomainDn", [System.DirectoryServices.Protocols.DirectoryAttribute[]]$outside))
            & $record 'svc-jim cannot create a user outside the delegated containers' $false 'a user was created in CN=Users; the delegation is too broad'
            Get-ADUser -Filter "SamAccountName -eq 'jim-probe-outside'" | Remove-ADUser -Confirm:$false
        }
        catch [System.DirectoryServices.Protocols.DirectoryOperationException] {
            $code = Get-LabDirectoryResultCode -Exception $_.Exception
            & $record 'svc-jim cannot create a user outside the delegated containers' ($code -eq 'InsufficientAccessRights') "result: $code ($($_.Exception.Message))"
        }
        catch {
            & $record 'svc-jim cannot create a user outside the delegated containers' $false $_.Exception.Message
        }
    }
    finally {
        $connection.Dispose()
        $stray = Get-ADUser -Filter "SamAccountName -eq 'jim-probe'" -ErrorAction SilentlyContinue
        if ($null -ne $stray) {
            Remove-ADUser -Identity $stray -Confirm:$false
        }
    }

    $admins = @(Get-ADGroupMember -Identity 'Domain Admins' | Where-Object { $_.SamAccountName -eq 'svc-jim' })
    & $record 'svc-jim is not a member of Domain Admins' ($admins.Count -eq 0) 'the lab would prove nothing about delegated access otherwise'
    return $results.ToArray()
}

function Test-LabPlainLdapRefused {
    <#
    .SYNOPSIS
        Confirms the domain controller still refuses a simple bind over plain LDAP, the Windows Server 2025
        default (LDAP signing enforced) that the Samba lab does not reproduce.
    #>
    [CmdletBinding()]
    [OutputType([bool])]
    param(
        [Parameter(Mandatory)]
        [string]$DomainDn,

        [Parameter(Mandatory)]
        [securestring]$ServiceAccountPassword
    )

    Assert-LabWindows -Feature 'Checking LDAP signing'
    $connection = New-LabLdapConnection -BindDn "CN=svc-jim,OU=Services,$DomainDn" -Password $ServiceAccountPassword -PlainLdap
    try {
        $connection.Bind()
        return $false
    }
    catch [System.DirectoryServices.Protocols.DirectoryOperationException] {
        # Windows Server 2025 refuses with "Strong authentication is required for this operation." and no Response.
        return ((Get-LabDirectoryResultCode -Exception $_.Exception) -eq 'StrongAuthRequired')
    }
    catch [System.DirectoryServices.Protocols.LdapException] {
        # Error 8 is strongerAuthRequired; error 49 with the signing data code also counts as a refusal.
        return ($_.Exception.ErrorCode -in @(8, 49))
    }
    finally {
        $connection.Dispose()
    }
}

Export-ModuleMember -Function @(
    'Test-LabIsWindows', 'Assert-LabWindows',
    'ConvertTo-LabDelegationSddl', 'Get-LabDaclAce', 'Test-LabSddlContainsSid', 'Test-LabAclGrant', 'Merge-LabDelegationSddl',
    'Get-LabCheckpointName', 'Test-LabCheckpointName', 'Test-LabVmName',
    'Get-LabDomainInfo', 'Get-LabCertificateName', 'Get-LabAdministratorUserName',
    'ConvertTo-LabVmNote', 'Test-LabVmNote', 'ConvertFrom-LabVmNote',
    'Get-LabAvmaKey', 'ConvertTo-LabUnattendPassword', 'Get-LabUnattendPlaceholder', 'ConvertTo-LabUnattendXml',
    'ConvertTo-LabKeylessUnattend', 'Test-LabEvaluationEdition', 'Get-LabAdProviderPath', 'Get-LabDirectoryResultCode',
    'ConvertFrom-LabDirectoryExtensionSetting', 'Get-LabDirectoryExtensionBuildParameter', 'Get-LabExchangeOrganizationName',
    'Get-LabExchangeSchemaTarget', 'Get-LabExchangePreparationStep', 'Test-LabExchangeSchemaMasterBuild', 'Get-LabExchangeSetupArgument', 'Resolve-LabInstallImage',
    'Get-LabGuestPhaseArgument', 'ConvertTo-LabArgumentString', 'ConvertTo-LabOsBuild',
    'New-LabDomainControllerStatus', 'Resolve-LabSecret', 'Resolve-LabLayout',
    'New-LabIsoImage', 'Wait-LabGuestReady', 'Restart-LabGuest', 'Copy-LabAssetToGuest', 'Invoke-LabGuestPhase',
    'Get-LabGuestOsBuild', 'Wait-LabTcpPort', 'Send-LabKeyPress',
    'Get-LabGroupSid', 'Grant-LabContainerDelegation', 'Grant-LabTombstoneRead', 'Get-LabTombstoneDescriptor',
    'Get-LabCertificateDnsName', 'Test-LabLdapsEndpoint', 'New-LabLdapConnection',
    'Test-LabServiceAccountDelegation', 'Test-LabPlainLdapRefused',
    'Get-LabRebuildVmName', 'Get-LabRebuildGenerationName', 'ConvertFrom-LabRebuildSetting', 'Get-LabNewestCumulativeUpdate',
    'Get-LabRebuildBuildAction', 'Get-LabPromotePlan', 'Get-LabRollbackPlan', 'Get-LabPrunePlan',
    'Get-LabVmNoteValue', 'Set-LabVmNoteValue', 'ConvertTo-LabChildArgument', 'Test-LabOwnedVmFolder',
    'Get-LabRunSummary', 'Get-LabRunStatusDescription', 'Invoke-LabRebuildPhase'
)
