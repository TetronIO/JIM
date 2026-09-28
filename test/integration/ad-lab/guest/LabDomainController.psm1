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

        [ValidatePattern('^[A-Za-z0-9-]{1,15}$')]
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

function Resolve-LabInstallImage {
    <#
    .SYNOPSIS
        Chooses the Datacenter image (Desktop Experience unless -ServerCore) from the media's image list.

    .DESCRIPTION
        Refuses Evaluation images: an evaluation domain controller cannot be converted and its timer runs in
        real time regardless of checkpoint reverts. Handles both naming schemes, "Windows Server 2025
        Datacenter (Desktop Experience)" and the older upper-case "SERVERDATACENTER".
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [object[]]$Image,

        [switch]$ServerCore
    )

    $all = @($Image | ForEach-Object { [string]$_.ImageName })
    $datacenter = @($Image | Where-Object { $_.ImageName -match 'Datacenter' })
    $licensed = @($datacenter | Where-Object { $_.ImageName -notmatch 'Evaluation' })

    if ($licensed.Count -eq 0) {
        if ($datacenter.Count -gt 0) {
            throw ("The media holds only Evaluation Datacenter images ({0}). An Evaluation domain controller cannot be converted and expires in real time; use non-evaluation media." -f ($all -join '; '))
        }
        throw ("The media holds no Datacenter image, so Automatic Virtual Machine Activation cannot apply. It holds: {0}" -f ($all -join '; '))
    }

    $desktop = @($licensed | Where-Object { ($_.ImageName -match 'Desktop Experience') -or ($_.ImageName -cmatch 'SERVERDATACENTER$') })
    $core = @($licensed | Where-Object { $desktop -notcontains $_ })

    if ($ServerCore) {
        $pool = $core
    }
    else {
        $pool = $desktop
    }
    if ($pool.Count -eq 0) {
        throw ("The media has no matching Datacenter image. It holds: {0}" -f ($all -join '; '))
    }
    return $pool[0]
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
        result is an ordered dictionary for splatting; the Recycle Bin is a switch, present only when on.
    #>
    [CmdletBinding()]
    [OutputType([System.Collections.Specialized.OrderedDictionary])]
    param(
        [Parameter(Mandatory)]
        [ValidateSet('Prepare', 'Promote', 'Configure', 'Verify')]
        [string]$Phase,

        [Parameter(Mandatory)]
        [System.Collections.IDictionary]$Settings
    )

    $required = @{
        Prepare   = @('ComputerName', 'IPAddress', 'PrefixLength', 'Gateway', 'DnsForwarder')
        Promote   = @('Domain', 'NetBiosName', 'SafeModePassword')
        Configure = @('ComputerName', 'Domain', 'NetBiosName', 'DnsForwarder', 'NtpServer', 'ServiceAccountPassword', 'VmName', 'LabRoot')
        Verify    = @('ComputerName', 'Domain', 'NetBiosName', 'NtpServer', 'ServiceAccountPassword', 'VmName', 'LabRoot')
    }
    $optionalArray = @{
        Prepare   = @()
        Promote   = @()
        Configure = @('ExtraCertificateNames')
        Verify    = @('ExtraCertificateNames')
    }
    $optionalSwitch = @{
        Prepare   = @()
        Promote   = @()
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
        # FsiFileSystems: ISO9660 = 1, Joliet = 2, UDF = 4.
        $image.FileSystemsToCreate = 6
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
    Restart-VM -Name $VMName -Force -Confirm:$false
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
        [ValidateSet('Prepare', 'Promote', 'Configure', 'Verify')]
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
    $path = "AD:\$ContainerDn"
    if (-not (Test-Path -LiteralPath $path)) {
        throw "Could not read the permissions of '$ContainerDn'; does it exist?"
    }

    $access = [System.Security.AccessControl.AccessControlSections]::Access
    $acl = Get-Acl -LiteralPath $path
    $existing = $acl.GetSecurityDescriptorSddlForm($access)
    if (Test-LabSddlContainsSid -Sddl $existing -Sid $sid) {
        return [pscustomobject][ordered]@{ ContainerDn = $ContainerDn; Outcome = 'AlreadyPresent'; TrusteeSid = $sid }
    }

    $merged = Merge-LabDelegationSddl -ExistingSddl $existing -DelegationSddl $sddl -TrusteeSid $sid
    $acl.SetSecurityDescriptorSddlForm($merged, $access)
    Set-Acl -LiteralPath $path -AclObject $acl

    $after = (Get-Acl -LiteralPath $path).GetSecurityDescriptorSddlForm($access)
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
            $refused = ($_.Exception.Response.ResultCode -eq [System.DirectoryServices.Protocols.ResultCode]::InsufficientAccessRights)
            & $record 'svc-jim cannot create a user outside the delegated containers' $refused "result: $($_.Exception.Response.ResultCode)"
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
        return ($_.Exception.Response.ResultCode -eq [System.DirectoryServices.Protocols.ResultCode]::StrongAuthRequired)
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
    'Resolve-LabInstallImage',
    'Get-LabGuestPhaseArgument', 'ConvertTo-LabArgumentString', 'ConvertTo-LabOsBuild',
    'New-LabDomainControllerStatus', 'Resolve-LabSecret', 'Resolve-LabLayout',
    'New-LabIsoImage', 'Wait-LabGuestReady', 'Restart-LabGuest', 'Copy-LabAssetToGuest', 'Invoke-LabGuestPhase',
    'Get-LabGuestOsBuild', 'Wait-LabTcpPort', 'Send-LabKeyPress',
    'Get-LabGroupSid', 'Grant-LabContainerDelegation', 'Grant-LabTombstoneRead', 'Get-LabTombstoneDescriptor',
    'Get-LabCertificateDnsName', 'Test-LabLdapsEndpoint', 'New-LabLdapConnection',
    'Test-LabServiceAccountDelegation', 'Test-LabPlainLdapRefused'
)
