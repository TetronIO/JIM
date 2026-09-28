# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Directory-abstracted helpers for the integration scenario and setup scripts

.DESCRIPTION
    The scenario and setup scripts used to run "docker exec <container> samba-tool ...", ldbadd,
    ldbsearch and bare LDAP client tools directly. None of that can work against a real Active
    Directory domain controller: it is a Hyper-V virtual machine with no container, reached over LDAPS
    through the shared jim-ldap-toolbox container.

    Each function here performs one directory operation for a config from Get-DirectoryConfig and
    dispatches on its DirectoryType:

      SambaAD             the samba-tool (and ldb) commands the scripts have always run, unchanged.
      OpenLDAP            LDAP client tools inside the directory's container, unchanged.
      DirectoryServer389  as OpenLDAP.
      ActiveDirectory     LDAP client tools in the toolbox over LDAPS, through Invoke-LdapTool only; a
                          password is written as unicodePwd, never with a container command.

    Functions return a hashtable with Success, ExitCode and Output (the tool's merged output as one
    string), and an Outcome where the callers tell outcomes apart. They report a failure rather than
    throwing, because the scripts differ on whether a failure is fatal; a function throws only for
    misuse (an operation the directory type cannot do) and for reads that cannot proceed.

    Depends on LDAP-Helpers.ps1 (Invoke-LdapTool and friends), which is loaded here if it is not yet.
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if (-not (Get-Command -Name Invoke-LdapTool -ErrorAction SilentlyContinue)) {
    . "$PSScriptRoot/LDAP-Helpers.ps1"
}

# ---------------------------------------------------------------------------------------------
# Building blocks
# ---------------------------------------------------------------------------------------------

function Get-DirectoryFamily {
    <#
    .SYNOPSIS
        Which dispatch branch a config takes: SambaAD, ActiveDirectory or Rfc (OpenLDAP and 389).
    #>
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory=$true)]
        [hashtable]$DirectoryConfig
    )

    $directoryType = [string]$DirectoryConfig['DirectoryType']
    switch ($directoryType) {
        'SambaAD'            { return 'SambaAD' }
        'ActiveDirectory'    { return 'ActiveDirectory' }
        'OpenLDAP'           { return 'Rfc' }
        'DirectoryServer389' { return 'Rfc' }
        default {
            throw "Get-DirectoryFamily: the directory config carries an unknown DirectoryType '$directoryType' (expected SambaAD, OpenLDAP, DirectoryServer389 or ActiveDirectory). Build configs with Get-DirectoryConfig."
        }
    }
}

function Get-DirectoryToolUri {
    <#
    .SYNOPSIS
        The URI the LDAP client tools connect to for a config: in-container for a container, LDAPS to
        the domain controller for a container-less config.
    #>
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory=$true)]
        [hashtable]$DirectoryConfig
    )

    if (Test-LdapToolboxConfig -DirectoryConfig $DirectoryConfig) {
        return Get-LdapUri -DirectoryConfig $DirectoryConfig
    }

    return "$($DirectoryConfig['LdapSearchScheme'])://localhost:$($DirectoryConfig['LdapSearchPort'])"
}

function Get-DirectoryToolBindArgument {
    <#
    .SYNOPSIS
        The simple-bind arguments (-x -H <uri> -D <administrator> -w <password>) for an LDAP client
        tool, in the order the scripts always wrote them.
    #>
    [CmdletBinding()]
    [OutputType([string[]])]
    param(
        [Parameter(Mandatory=$true)]
        [hashtable]$DirectoryConfig
    )

    return @('-x', '-H', (Get-DirectoryToolUri -DirectoryConfig $DirectoryConfig),
             '-D', [string]$DirectoryConfig['BindDN'], '-w', [string]$DirectoryConfig['BindPassword'])
}

function Get-DirectoryContainerCommand {
    <#
    .SYNOPSIS
        Build the docker argument list that runs a command inside the directory's own container.

    .DESCRIPTION
        For the tools that exist only inside a Samba AD (or OpenLDAP) container: samba-tool, ldbadd,
        ldbsearch. Pure, so it is testable with no Docker. A config with no container (a real Active
        Directory domain controller) has nowhere to run them, so this throws and the caller must use
        the LDAP branch.
    #>
    [CmdletBinding()]
    [OutputType([string[]])]
    param(
        [Parameter(Mandatory=$true)]
        [hashtable]$DirectoryConfig,

        [Parameter(Mandatory=$true)]
        [string[]]$Command,

        [Parameter(Mandatory=$false)]
        [switch]$WithInput
    )

    if (Test-LdapToolboxConfig -DirectoryConfig $DirectoryConfig) {
        throw "Get-DirectoryContainerCommand: the '$($DirectoryConfig['DirectoryType'])' directory config has no container to run '$($Command[0])' in. Container commands exist only for the container labs; a real Active Directory domain controller is driven over LDAPS with Invoke-LdapTool."
    }

    $argv = [System.Collections.Generic.List[string]]::new()
    $argv.Add('exec')
    if ($WithInput) { $argv.Add('-i') }
    $argv.Add([string]$DirectoryConfig['ContainerName'])
    foreach ($element in $Command) { $argv.Add($element) }
    return $argv.ToArray()
}

function ConvertTo-DirectoryOutputText {
    # Joins whatever a native command produced (nothing, a string, or one element per line) into one string.
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory=$false)]
        [AllowNull()]
        $Output
    )

    if ($null -eq $Output) { return '' }
    return (@($Output | ForEach-Object { "$_" }) -join "`n")
}

function Invoke-DirectoryContainerCommand {
    <#
    .SYNOPSIS
        Run a command inside the directory's container and return @{ ExitCode; Output }.

    .DESCRIPTION
        Standard error is merged into the output, as the scripts have always done, so a failing
        tool's message is in what comes back. -InputObject is written to standard input (adds -i).
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory=$true)]
        [hashtable]$DirectoryConfig,

        [Parameter(Mandatory=$true)]
        [string[]]$Command,

        [Parameter(Mandatory=$false)]
        [string]$InputObject
    )

    $hasInput = $PSBoundParameters.ContainsKey('InputObject')
    $argv = Get-DirectoryContainerCommand -DirectoryConfig $DirectoryConfig -Command $Command -WithInput:$hasInput

    if ($hasInput) {
        $output = $InputObject | & docker @argv 2>&1
    }
    else {
        $output = & docker @argv 2>&1
    }
    $exitCode = $LASTEXITCODE

    return @{ ExitCode = $exitCode; Output = (ConvertTo-DirectoryOutputText -Output $output) }
}

function Invoke-DirectoryLdapTool {
    # Invoke-LdapTool with the result shaped like Invoke-DirectoryContainerCommand's: @{ ExitCode; Output (string) }.
    [CmdletBinding()]
    param(
        [Parameter(Mandatory=$true)]
        [hashtable]$DirectoryConfig,

        [Parameter(Mandatory=$true)]
        [ValidateSet('ldapsearch', 'ldapwhoami', 'ldapmodify', 'ldapadd', 'ldapdelete', 'ldappasswd')]
        [string]$Tool,

        [Parameter(Mandatory=$true)]
        [string[]]$Arguments,

        [Parameter(Mandatory=$false)]
        [string]$InputObject
    )

    $invoke = @{ DirectoryConfig = $DirectoryConfig; Tool = $Tool; Arguments = $Arguments; PassThruExitCode = $true }
    if ($PSBoundParameters.ContainsKey('InputObject')) { $invoke.InputObject = $InputObject }
    $result = Invoke-LdapTool @invoke

    return @{ ExitCode = $result.ExitCode; Output = (ConvertTo-DirectoryOutputText -Output $result.Output) }
}

function Split-DirectoryDn {
    <#
    .SYNOPSIS
        Split a Distinguished Name into its leading RDN and the parent DN, respecting escaped commas.

    .OUTPUTS
        A hashtable with Rdn, RdnAttribute, RdnValue and ParentDn (empty for a single-RDN DN).
    #>
    [CmdletBinding()]
    [OutputType([hashtable])]
    param(
        [Parameter(Mandatory=$true)]
        [string]$Dn
    )

    $index = 0
    while ($index -lt $Dn.Length) {
        $character = $Dn[$index]
        if ($character -eq '\') { $index += 2; continue }
        if ($character -eq ',') { break }
        $index++
    }
    if ($index -gt $Dn.Length) { $index = $Dn.Length }

    $rdn = $Dn.Substring(0, $index)
    $parent = if ($index -lt $Dn.Length) { $Dn.Substring($index + 1) } else { '' }
    $equals = $rdn.IndexOf('=')
    if ($equals -lt 1) {
        throw "Split-DirectoryDn: '$Dn' does not start with an attribute=value RDN."
    }

    return @{
        Rdn          = $rdn
        RdnAttribute = $rdn.Substring(0, $equals).Trim()
        RdnValue     = $rdn.Substring($equals + 1)
        ParentDn     = $parent
    }
}

function Get-DirectoryRelativeDn {
    <#
    .SYNOPSIS
        A DN relative to a base DN (the form samba-tool's --userou and --groupou take).

    .EXAMPLE
        Get-DirectoryRelativeDn -Dn 'OU=TestUsers,DC=panoply,DC=local' -BaseDn 'DC=panoply,DC=local'   # OU=TestUsers
    #>
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory=$true)]
        [string]$Dn,

        [Parameter(Mandatory=$true)]
        [string]$BaseDn
    )

    if ($Dn.Equals($BaseDn, [System.StringComparison]::OrdinalIgnoreCase)) { return '' }

    $suffix = ",$BaseDn"
    if (-not $Dn.EndsWith($suffix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Get-DirectoryRelativeDn: '$Dn' is not under the base DN '$BaseDn'."
    }
    return $Dn.Substring(0, $Dn.Length - $suffix.Length)
}

function ConvertTo-LdapFilterValue {
    # Escapes the characters that are special in an LDAP search filter value (RFC 4515).
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory=$true)]
        [AllowEmptyString()]
        [string]$Value
    )

    $builder = [System.Text.StringBuilder]::new()
    foreach ($character in $Value.ToCharArray()) {
        switch ($character) {
            '\' { [void]$builder.Append('\5c') }
            '*' { [void]$builder.Append('\2a') }
            '(' { [void]$builder.Append('\28') }
            ')' { [void]$builder.Append('\29') }
            default {
                if ([int]$character -eq 0) { [void]$builder.Append('\00') } else { [void]$builder.Append($character) }
            }
        }
    }
    return $builder.ToString()
}

function Format-LdifAttribute {
    <#
    .SYNOPSIS
        One LDIF attribute line: "name: value", or "name:: <base64>" when the value is not a safe string.
    #>
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory=$true)]
        [string]$Name,

        [Parameter(Mandatory=$true)]
        [AllowEmptyString()]
        [string]$Value
    )

    if ($Value -eq '' -or $Value -match '^[\x21-\x39\x3B\x3D-\x7E]([\x20-\x7E]*[\x21-\x7E])?$') {
        return "${Name}: $Value"
    }
    return "${Name}:: $([System.Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes($Value)))"
}

function ConvertFrom-LdifSearchOutput {
    <#
    .SYNOPSIS
        Parse ldapsearch -LLL output into entries: hashtables of attribute name (case-insensitive) to
        string[], plus dn as a plain string.

    .DESCRIPTION
        Unfolds RFC 2849 continuation lines, skips comments (the "# refldap://" lines a referral
        leaves), and decodes "attr:: base64" values as UTF-8.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory=$false)]
        [AllowNull()]
        [AllowEmptyCollection()]
        $Output
    )

    $text = ConvertTo-DirectoryOutputText -Output $Output
    if ([string]::IsNullOrWhiteSpace($text)) { return }

    $current = $null
    $currentValues = $null

    $finish = {
        if ($null -ne $current) {
            foreach ($key in @($currentValues.Keys)) { $current[$key] = $currentValues[$key].ToArray() }
            $current
        }
    }

    foreach ($line in (Expand-LDIFFoldedLine -RawLdif $text)) {
        if ($line -eq '') {
            & $finish
            $current = $null
            $currentValues = $null
            continue
        }
        if ($line.StartsWith('#') -or $line -match '^version:') { continue }
        if ($line -notmatch '^(?<name>[^:]+?)(?<sep>::?) ?(?<value>.*)$') { continue }

        $name = $Matches['name']
        $value = $Matches['value']
        if ($Matches['sep'] -eq '::') {
            $value = [System.Text.Encoding]::UTF8.GetString([System.Convert]::FromBase64String($value))
        }

        if ($name -eq 'dn') {
            & $finish
            $current = [hashtable]::new([System.StringComparer]::OrdinalIgnoreCase)
            $currentValues = [System.Collections.Generic.Dictionary[string, System.Collections.Generic.List[string]]]::new([System.StringComparer]::OrdinalIgnoreCase)
            $current['dn'] = $value
            continue
        }

        if ($null -eq $current) { continue }
        if (-not $currentValues.ContainsKey($name)) {
            $currentValues[$name] = [System.Collections.Generic.List[string]]::new()
        }
        $currentValues[$name].Add($value)
    }
    & $finish
}

function New-DirectoryResult {
    # The shape every operation returns.
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Test-harness helper against a disposable lab directory; a confirmation prompt would only stall an unattended run.')]
    [CmdletBinding()]
    [OutputType([hashtable])]
    param(
        [Parameter(Mandatory=$true)] [hashtable]$Raw,
        [Parameter(Mandatory=$false)] [string]$Outcome,
        [Parameter(Mandatory=$false)] [bool]$Success = ($Raw.ExitCode -eq 0)
    )

    $result = @{ Success = $Success; ExitCode = $Raw.ExitCode; Output = $Raw.Output }
    if ($Outcome) { $result.Outcome = $Outcome }
    return $result
}

function Get-DirectoryCreateOutcome {
    # Created / AlreadyExists / Failed for a create, from the tool's exit code and text.
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory=$true)] [hashtable]$Raw
    )

    if ($Raw.ExitCode -eq 0) { return 'Created' }
    if ($Raw.ExitCode -eq 68 -or $Raw.Output -match 'already exists') { return 'AlreadyExists' }
    return 'Failed'
}

# ---------------------------------------------------------------------------------------------
# Raw LDIF
# ---------------------------------------------------------------------------------------------

function Invoke-DirectoryLdif {
    <#
    .SYNOPSIS
        Apply LDIF to the directory as its administrator: ldapadd, ldapmodify or ldapdelete over
        standard input.

    .DESCRIPTION
        Inside the container for the container labs, in the toolbox over LDAPS for Active Directory,
        always through Invoke-LdapTool. CRLF line endings are normalised to LF first: this repository's
        scripts are CRLF and their here-strings embed it, which OpenLDAP's tools tolerate and Samba's
        ldb LDIF parser does not.

    .PARAMETER Ldif
        The LDIF. For -Operation delete, the DNs to delete, one per line.

    .PARAMETER Operation
        add (ldapadd), modify (ldapmodify) or delete (ldapdelete).

    .PARAMETER Continue
        Adds -c: carry on past a per-entry error instead of stopping at the first.

    .OUTPUTS
        A hashtable with Success, ExitCode and Output; for an add, also Outcome (Created,
        AlreadyExists or Failed). Success is true only when the tool exited 0.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory=$true)]
        [hashtable]$DirectoryConfig,

        [Parameter(Mandatory=$true)]
        [string]$Ldif,

        [Parameter(Mandatory=$true)]
        [ValidateSet('add', 'modify', 'delete')]
        [string]$Operation,

        [Parameter(Mandatory=$false)]
        [switch]$Continue
    )

    $tool = switch ($Operation) { 'add' { 'ldapadd' } 'modify' { 'ldapmodify' } 'delete' { 'ldapdelete' } }
    $arguments = @(Get-DirectoryToolBindArgument -DirectoryConfig $DirectoryConfig)
    if ($Continue) { $arguments += '-c' }

    $raw = Invoke-DirectoryLdapTool -DirectoryConfig $DirectoryConfig -Tool $tool -Arguments $arguments -InputObject ($Ldif -replace "`r`n", "`n")
    if ($Operation -eq 'add') {
        # An add is what the scripts tolerate "already exists" on, so it says which of three happened.
        $outcome = Get-DirectoryCreateOutcome -Raw $raw
        return New-DirectoryResult -Raw $raw -Outcome $outcome -Success ($raw.ExitCode -eq 0)
    }
    return New-DirectoryResult -Raw $raw
}

# ---------------------------------------------------------------------------------------------
# Reads
# ---------------------------------------------------------------------------------------------

function Get-DirectorySearchAttributeList {
    # Attribute names to pass to ldapsearch: dn is always returned and "*" is glob-expanded by shells, so both
    # are dropped; a request for dn alone becomes 1.1 (no attributes).
    [CmdletBinding()]
    [OutputType([string[]])]
    param(
        [Parameter(Mandatory=$false)]
        [string[]]$Attributes
    )

    $requested = @($Attributes | Where-Object { $_ })
    $named = @($requested | Where-Object { $_ -ne 'dn' -and $_ -ne '*' })
    if ($named.Count -eq 0 -and ($requested -contains 'dn')) { return @('1.1') }
    return $named
}

function Find-DirectoryEntry {
    <#
    .SYNOPSIS
        Search the directory as its administrator. Replaces the ldbsearch and "samba-tool user show"
        reads.

    .DESCRIPTION
        Returns the matching entries (hashtables, see ConvertFrom-LdifSearchOutput). With -AsText it
        returns the unfolded LDIF text instead ("dn: ..." then "attr: value" lines, the shape ldbsearch
        printed) and never throws: a failed search returns the tool's error text, which is what the
        scripts that match on the text and print it on a mismatch relied on.

    .PARAMETER Filter
        The LDAP filter.

    .PARAMETER BaseDn
        Where to search from. Defaults to the config's BaseDN.

    .PARAMETER Scope
        base, one or sub (the default).

    .PARAMETER Attributes
        Attributes to return. dn is always returned.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory=$true)]
        [hashtable]$DirectoryConfig,

        [Parameter(Mandatory=$true)]
        [string]$Filter,

        [Parameter(Mandatory=$false)]
        [string]$BaseDn,

        [Parameter(Mandatory=$false)]
        [ValidateSet('base', 'one', 'sub')]
        [string]$Scope = 'sub',

        [Parameter(Mandatory=$false)]
        [string[]]$Attributes,

        [Parameter(Mandatory=$false)]
        [switch]$AsText
    )

    if (-not $BaseDn) { $BaseDn = [string]$DirectoryConfig['BaseDN'] }

    $arguments = @(Get-DirectoryToolBindArgument -DirectoryConfig $DirectoryConfig) +
        @('-LLL', '-b', $BaseDn, '-s', $Scope, $Filter) +
        @(Get-DirectorySearchAttributeList -Attributes $Attributes)

    $raw = Invoke-DirectoryLdapTool -DirectoryConfig $DirectoryConfig -Tool ldapsearch -Arguments $arguments

    if ($AsText) {
        # Unfolded, with "attr:: <base64>" decoded, so the text reads as ldbsearch printed it.
        $textLines = foreach ($line in (Expand-LDIFFoldedLine -RawLdif $raw.Output)) {
            if ($line -match '^(?<name>[^:\s]+):: (?<value>[A-Za-z0-9+/=]*)$') {
                "$($Matches['name']): $([System.Text.Encoding]::UTF8.GetString([System.Convert]::FromBase64String($Matches['value'])))"
            }
            else {
                $line
            }
        }
        return (@($textLines) -join "`n")
    }

    if ($raw.ExitCode -eq 32) { return }
    if ($raw.ExitCode -ne 0) {
        throw "Find-DirectoryEntry: ldapsearch for $Filter under '$BaseDn' failed (exit $($raw.ExitCode)): $($raw.Output)"
    }
    ConvertFrom-LdifSearchOutput -Output $raw.Output
}

function Test-DirectoryEntryPresent {
    <#
    .SYNOPSIS
        Whether a search finds at least one entry. False for "no such object"; a search that could not
        run (the directory is unreachable, the bind was refused) throws rather than reading as "not there".
    #>
    [CmdletBinding()]
    [OutputType([bool])]
    param(
        [Parameter(Mandatory=$true)]
        [hashtable]$DirectoryConfig,

        [Parameter(Mandatory=$true)]
        [string]$Filter,

        [Parameter(Mandatory=$false)]
        [string]$BaseDn
    )

    $search = @{ DirectoryConfig = $DirectoryConfig; Filter = $Filter; Attributes = @('dn') }
    if ($BaseDn) { $search.BaseDn = $BaseDn }
    return (@(Find-DirectoryEntry @search).Count -gt 0)
}

function Get-DirectoryEntry {
    <#
    .SYNOPSIS
        Read one entry by DN as the directory's administrator; $null when it does not exist.

    .OUTPUTS
        A hashtable of attribute name (case-insensitive) to string[], with dn as a plain string; or $null.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory=$true)]
        [hashtable]$DirectoryConfig,

        [Parameter(Mandatory=$true)]
        [string]$Dn,

        [Parameter(Mandatory=$false)]
        [string[]]$Attributes
    )

    $arguments = @(Get-DirectoryToolBindArgument -DirectoryConfig $DirectoryConfig) +
        @('-LLL', '-b', $Dn, '-s', 'base', '(objectClass=*)') +
        @(Get-DirectorySearchAttributeList -Attributes $Attributes)

    $raw = Invoke-DirectoryLdapTool -DirectoryConfig $DirectoryConfig -Tool ldapsearch -Arguments $arguments
    if ($raw.ExitCode -eq 32) { return $null }
    if ($raw.ExitCode -ne 0) {
        throw "Get-DirectoryEntry: ldapsearch for '$Dn' failed (exit $($raw.ExitCode)): $($raw.Output)"
    }

    $entries = @(ConvertFrom-LdifSearchOutput -Output $raw.Output)
    if ($entries.Count -eq 0) { return $null }
    return $entries[0]
}

function Get-DirectoryHealthStatus {
    <#
    .SYNOPSIS
        'healthy' when the directory is up, for a container or a container-less config alike.

    .DESCRIPTION
        A container config reports its Docker health (docker inspect), as the scripts always asked.
        A config with no container (Active Directory) has no Docker health, so it is asked the question
        that matters instead: does an LDAPS bind as the administrator succeed. 'unreachable' when not.
    #>
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory=$true)]
        [hashtable]$DirectoryConfig
    )

    if (Test-LdapToolboxConfig -DirectoryConfig $DirectoryConfig) {
        $bind = Test-LDAPBind -BindDN ([string]$DirectoryConfig['BindDN']) -BindPassword ([string]$DirectoryConfig['BindPassword']) -DirectoryConfig $DirectoryConfig
        return $(if ($bind.Outcome -eq 'Success') { 'healthy' } else { 'unreachable' })
    }

    $output = & docker inspect '--format={{.State.Health.Status}}' ([string]$DirectoryConfig['ContainerName']) 2>&1
    return (ConvertTo-DirectoryOutputText -Output $output).Trim()
}

# ---------------------------------------------------------------------------------------------
# Entries, OUs, users
# ---------------------------------------------------------------------------------------------

function Remove-DirectoryEntry {
    <#
    .SYNOPSIS
        Delete an entry by DN with ldapdelete (any directory type); -Recurse adds -r.

    .OUTPUTS
        A hashtable with Outcome (Deleted, NotFound or Failed), Success (true for Deleted and NotFound:
        the entry is gone either way), ExitCode and Output.
    #>
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Test-harness helper against a disposable lab directory; a confirmation prompt would only stall an unattended run.')]
    param(
        [Parameter(Mandatory=$true)]
        [hashtable]$DirectoryConfig,

        [Parameter(Mandatory=$true)]
        [string]$Dn,

        [Parameter(Mandatory=$false)]
        [switch]$Recurse
    )

    $arguments = @(Get-DirectoryToolBindArgument -DirectoryConfig $DirectoryConfig)
    if ($Recurse) { $arguments += '-r' }
    $arguments += $Dn

    $raw = Invoke-DirectoryLdapTool -DirectoryConfig $DirectoryConfig -Tool ldapdelete -Arguments $arguments
    $outcome = if ($raw.ExitCode -eq 0) { 'Deleted' }
               elseif ($raw.ExitCode -eq 32 -or $raw.Output -match 'No such object') { 'NotFound' }
               else { 'Failed' }
    return New-DirectoryResult -Raw $raw -Outcome $outcome -Success ($outcome -ne 'Failed')
}

function New-DirectoryOu {
    <#
    .SYNOPSIS
        Create an organisational unit, tolerating "already exists".

    .DESCRIPTION
        Samba AD: samba-tool ou create. Everything else: ldapadd of an organizationalUnit as the
        directory's administrator.

    .PARAMETER Dn
        The OU's DN, for example OU=Finance,OU=Users,OU=Corp,DC=panoply,DC=local.

    .PARAMETER ViaServer
        Samba AD only: route through the running server (-H ldap://localhost -U <admin>%<password>)
        rather than samba-tool's direct access to the database file, which races the server's own
        writes on a live domain controller (Scenario 005 does this).

    .OUTPUTS
        A hashtable with Outcome (Created, AlreadyExists or Failed), Success (true for the first two),
        ExitCode and Output.
    #>
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Test-harness helper against a disposable lab directory; a confirmation prompt would only stall an unattended run.')]
    param(
        [Parameter(Mandatory=$true)]
        [hashtable]$DirectoryConfig,

        [Parameter(Mandatory=$true)]
        [string]$Dn,

        [Parameter(Mandatory=$false)]
        [switch]$ViaServer
    )

    if ((Get-DirectoryFamily -DirectoryConfig $DirectoryConfig) -eq 'SambaAD') {
        $command = @('samba-tool', 'ou', 'create', $Dn)
        if ($ViaServer) {
            $adminName = (([string]$DirectoryConfig['BindDN']) -split ',')[0] -replace '^CN=', ''
            $command += @('-H', 'ldap://localhost', '-U', "$adminName%$($DirectoryConfig['BindPassword'])")
        }
        $raw = Invoke-DirectoryContainerCommand -DirectoryConfig $DirectoryConfig -Command $command
    }
    else {
        $ouName = (Split-DirectoryDn -Dn $Dn).RdnValue
        $ldif = @("dn: $Dn", 'objectClass: top', 'objectClass: organizationalUnit', "ou: $ouName") -join "`n"
        $raw = Invoke-DirectoryLdapTool -DirectoryConfig $DirectoryConfig -Tool ldapadd `
            -Arguments (Get-DirectoryToolBindArgument -DirectoryConfig $DirectoryConfig) -InputObject $ldif
    }

    $outcome = Get-DirectoryCreateOutcome -Raw $raw
    return New-DirectoryResult -Raw $raw -Outcome $outcome -Success ($outcome -ne 'Failed')
}

function Remove-DirectoryOu {
    <#
    .SYNOPSIS
        Delete an organisational unit; -Recurse deletes the subtree.

    .DESCRIPTION
        Samba AD: samba-tool ou delete [--force-subtree-delete]. Everything else: ldapdelete [-r].
    #>
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Test-harness helper against a disposable lab directory; a confirmation prompt would only stall an unattended run.')]
    param(
        [Parameter(Mandatory=$true)]
        [hashtable]$DirectoryConfig,

        [Parameter(Mandatory=$true)]
        [string]$Dn,

        [Parameter(Mandatory=$false)]
        [switch]$Recurse
    )

    if ((Get-DirectoryFamily -DirectoryConfig $DirectoryConfig) -ne 'SambaAD') {
        return Remove-DirectoryEntry -DirectoryConfig $DirectoryConfig -Dn $Dn -Recurse:$Recurse
    }

    $command = @('samba-tool', 'ou', 'delete', $Dn)
    if ($Recurse) { $command += '--force-subtree-delete' }
    $raw = Invoke-DirectoryContainerCommand -DirectoryConfig $DirectoryConfig -Command $command
    $outcome = if ($raw.ExitCode -eq 0) { 'Deleted' }
               elseif ($raw.Output -match 'Unable to find|No such object|does not exist') { 'NotFound' }
               else { 'Failed' }
    return New-DirectoryResult -Raw $raw -Outcome $outcome -Success ($outcome -ne 'Failed')
}

function Remove-DirectoryUser {
    <#
    .SYNOPSIS
        Delete a user by account name (or by DN).

    .DESCRIPTION
        Samba AD: samba-tool user delete, run inside bash so its exit code is echoed after its output
        (the text the scripts have always matched: "Deleted user", "Unable to find user"). Active
        Directory: find the DN by sAMAccountName, then ldapdelete it. OpenLDAP and 389: ldapdelete of
        <UserRdnAttr>=<name>,<UserContainer>. -Dn skips the lookup.

    .OUTPUTS
        A hashtable with Outcome (Deleted, NotFound or Failed), Success (true for Deleted and NotFound),
        ExitCode and Output.
    #>
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Test-harness helper against a disposable lab directory; a confirmation prompt would only stall an unattended run.')]
    param(
        [Parameter(Mandatory=$true)]
        [hashtable]$DirectoryConfig,

        [Parameter(Mandatory=$true)]
        [string]$SamAccountName,

        [Parameter(Mandatory=$false)]
        [string]$Dn
    )

    $family = Get-DirectoryFamily -DirectoryConfig $DirectoryConfig

    if ($family -eq 'SambaAD' -and -not $Dn) {
        $script = "samba-tool user delete '$SamAccountName' 2>&1; echo EXIT_CODE:" + '$?'
        $raw = Invoke-DirectoryContainerCommand -DirectoryConfig $DirectoryConfig -Command @('bash', '-c', $script)
        $outcome = if ($raw.Output -match 'Deleted user') { 'Deleted' }
                   elseif ($raw.Output -match 'Unable to find user') { 'NotFound' }
                   else { 'Failed' }
        return New-DirectoryResult -Raw $raw -Outcome $outcome -Success ($outcome -ne 'Failed')
    }

    if (-not $Dn) {
        if ($family -eq 'ActiveDirectory' -or $family -eq 'SambaAD') {
            $nameAttribute = if ($DirectoryConfig['UserNameAttr']) { [string]$DirectoryConfig['UserNameAttr'] } else { 'sAMAccountName' }
            $found = @(Find-DirectoryEntry -DirectoryConfig $DirectoryConfig -Filter "($nameAttribute=$(ConvertTo-LdapFilterValue -Value $SamAccountName))" -Attributes '1.1')
            if ($found.Count -eq 0) {
                return @{ Success = $true; ExitCode = 32; Output = "No entry with $nameAttribute=$SamAccountName"; Outcome = 'NotFound' }
            }
            $Dn = [string]$found[0]['dn']
        }
        else {
            $Dn = "$($DirectoryConfig['UserRdnAttr'])=$SamAccountName,$($DirectoryConfig['UserContainer'])"
        }
    }

    return Remove-DirectoryEntry -DirectoryConfig $DirectoryConfig -Dn $Dn
}

function New-DirectoryUser {
    <#
    .SYNOPSIS
        Create an enabled user account with a password (Samba AD and Active Directory).

    .DESCRIPTION
        Samba AD: samba-tool user create <sam> <password> --userou=... plus the options its
        attributes map to (givenName, sn, mail, department, description, title, company); any other
        attribute is applied afterwards with ldapmodify. samba-tool derives the CN from the names, so
        the DN's CN must be "<givenName> <sn>" or the account name (--use-username-as-cn); anything
        else throws rather than silently creating a different DN.

        Active Directory: ldapadd of a user (objectClass top, person, organizationalPerson, user; cn,
        sAMAccountName and a userPrincipalName of <sam>@<Domain> unless one is given), then one
        ldapmodify over LDAPS that sets unicodePwd and, when enabled, userAccountControl 512. A user
        added over LDAP starts disabled, and Active Directory refuses to enable an account that has
        no password, hence the order. The password must satisfy the domain policy (complexity is on):
        it must not contain the account name or a name part of three characters or more.

        OpenLDAP and 389 Directory Server: not supported; the scripts build their users from LDIF
        against the schema they use (inetOrgPerson), with Invoke-DirectoryLdif.

    .PARAMETER Dn
        The account's DN.

    .PARAMETER Enabled
        Whether the account is enabled (default true).

    .PARAMETER Attributes
        Attribute name to value (a string, or an array for a multi-valued attribute).

    .OUTPUTS
        A hashtable with Outcome (Created, AlreadyExists or Failed), Success, ExitCode and Output.
    #>
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Test-harness helper against a disposable lab directory; a confirmation prompt would only stall an unattended run.')]
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingPlainTextForPassword', '',
        Justification = 'Lab passwords are plain-text test fixtures, handed to a native tool as arguments or LDIF; a SecureString would only be unwrapped again at once.')]
    param(
        [Parameter(Mandatory=$true)]
        [hashtable]$DirectoryConfig,

        [Parameter(Mandatory=$true)]
        [string]$Dn,

        [Parameter(Mandatory=$true)]
        [string]$SamAccountName,

        [Parameter(Mandatory=$true)]
        [string]$Password,

        [Parameter(Mandatory=$false)]
        [bool]$Enabled = $true,

        [Parameter(Mandatory=$false)]
        [System.Collections.IDictionary]$Attributes
    )

    $family = Get-DirectoryFamily -DirectoryConfig $DirectoryConfig
    if ($family -eq 'Rfc') {
        throw "New-DirectoryUser: creating users is supported for Samba AD and Active Directory only. The '$($DirectoryConfig['DirectoryType'])' scripts build their users from LDIF against their own schema; use Invoke-DirectoryLdif."
    }

    $rdn = Split-DirectoryDn -Dn $Dn
    if ($rdn.RdnAttribute -ne 'CN') {
        throw "New-DirectoryUser: the DN '$Dn' must start with CN=, as a user's does."
    }

    # Case-insensitive view of the attributes, so givenName, GivenName and givenname are one.
    $attributeMap = [System.Collections.Specialized.OrderedDictionary]::new([System.StringComparer]::OrdinalIgnoreCase)
    if ($Attributes) { foreach ($key in $Attributes.Keys) { $attributeMap[[string]$key] = $Attributes[$key] } }

    if ($family -eq 'SambaAD') {
        $optionFor = [ordered]@{
            givenName = '--given-name'; sn = '--surname'; mail = '--mail-address'; department = '--department'
            description = '--description'; title = '--job-title'; company = '--company'
        }

        $names = @('givenName', 'sn' | Where-Object { $attributeMap.Contains($_) -and "$($attributeMap[$_])" -ne '' } | ForEach-Object { "$($attributeMap[$_])" })
        $expectedCn = $names -join ' '
        $useUsernameAsCn = $false
        if ($rdn.RdnValue -ne $expectedCn) {
            if ($rdn.RdnValue -eq $SamAccountName) {
                $useUsernameAsCn = $true
            }
            else {
                throw "New-DirectoryUser: samba-tool user create cannot produce the CN '$($rdn.RdnValue)' for '$SamAccountName'. Its CN is '$expectedCn' from the given name and surname, or the account name with --use-username-as-cn."
            }
        }

        $command = @('samba-tool', 'user', 'create', $SamAccountName, $Password)
        $parentRelative = Get-DirectoryRelativeDn -Dn $rdn.ParentDn -BaseDn ([string]$DirectoryConfig['BaseDN'])
        if ($parentRelative) { $command += "--userou=$parentRelative" }
        if ($useUsernameAsCn) { $command += '--use-username-as-cn' }
        foreach ($attributeName in $optionFor.Keys) {
            if ($attributeMap.Contains($attributeName)) { $command += "$($optionFor[$attributeName])=$($attributeMap[$attributeName])" }
        }

        $raw = Invoke-DirectoryContainerCommand -DirectoryConfig $DirectoryConfig -Command $command
        $outcome = Get-DirectoryCreateOutcome -Raw $raw
        if ($outcome -ne 'Created') {
            return New-DirectoryResult -Raw $raw -Outcome $outcome -Success ($outcome -ne 'Failed')
        }

        if (-not $Enabled) {
            $disable = Invoke-DirectoryContainerCommand -DirectoryConfig $DirectoryConfig -Command @('samba-tool', 'user', 'disable', $SamAccountName)
            if ($disable.ExitCode -ne 0) {
                return New-DirectoryResult -Raw $disable -Outcome 'Failed'
            }
        }

        $unmapped = @($attributeMap.Keys | Where-Object { -not $optionFor.Contains($_) })
        if ($unmapped.Count -gt 0) {
            $records = foreach ($attributeName in $unmapped) {
                $valueLines = foreach ($value in @($attributeMap[$attributeName])) { Format-LdifAttribute -Name $attributeName -Value ([string]$value) }
                (@("replace: $attributeName") + @($valueLines)) -join "`n"
            }
            $ldif = "dn: $Dn`nchangetype: modify`n" + ($records -join "`n-`n")
            $modify = Invoke-DirectoryLdif -DirectoryConfig $DirectoryConfig -Ldif $ldif -Operation modify
            if (-not $modify.Success) {
                return New-DirectoryResult -Raw $modify -Outcome 'Failed'
            }
        }

        return New-DirectoryResult -Raw $raw -Outcome 'Created'
    }

    # Active Directory.
    $lines = [System.Collections.Generic.List[string]]::new()
    $lines.Add("dn: $Dn")
    foreach ($objectClass in @('top', 'person', 'organizationalPerson', 'user')) { $lines.Add("objectClass: $objectClass") }
    $lines.Add((Format-LdifAttribute -Name 'cn' -Value $rdn.RdnValue))
    $lines.Add("sAMAccountName: $SamAccountName")
    if (-not $attributeMap.Contains('userPrincipalName') -and $DirectoryConfig['Domain']) {
        $lines.Add("userPrincipalName: $SamAccountName@$($DirectoryConfig['Domain'])")
    }
    foreach ($attributeName in $attributeMap.Keys) {
        foreach ($value in @($attributeMap[$attributeName])) { $lines.Add((Format-LdifAttribute -Name $attributeName -Value ([string]$value))) }
    }

    $add = Invoke-DirectoryLdif -DirectoryConfig $DirectoryConfig -Ldif ($lines -join "`n") -Operation add
    $addOutcome = Get-DirectoryCreateOutcome -Raw $add
    if ($addOutcome -ne 'Created') {
        return New-DirectoryResult -Raw $add -Outcome $addOutcome -Success ($addOutcome -ne 'Failed')
    }

    $passwordRecord = @("dn: $Dn", 'changetype: modify', 'replace: unicodePwd', "unicodePwd:: $(ConvertTo-UnicodePwdValue -Password $Password)")
    $records = @(($passwordRecord -join "`n"))
    if ($Enabled) {
        $records += (@("dn: $Dn", 'changetype: modify', 'replace: userAccountControl', 'userAccountControl: 512') -join "`n")
    }
    $secure = Invoke-DirectoryLdif -DirectoryConfig $DirectoryConfig -Ldif ($records -join "`n`n") -Operation modify
    if (-not $secure.Success) {
        $secure.Output = "The account '$Dn' was created but setting its password or enabling it failed, so it is left disabled: $($secure.Output)"
        return New-DirectoryResult -Raw $secure -Outcome 'Failed'
    }

    return New-DirectoryResult -Raw $add -Outcome 'Created'
}

# ---------------------------------------------------------------------------------------------
# Groups
# ---------------------------------------------------------------------------------------------

function New-DirectoryGroup {
    <#
    .SYNOPSIS
        Create a security group (Samba AD and Active Directory).

    .DESCRIPTION
        Samba AD: samba-tool group add <name> --groupou=... --description=.... Active Directory:
        ldapadd of a group with the group type for -Scope (a global security group by default). The
        group's name is the DN's CN unless -SamAccountName is given. OpenLDAP and 389 Directory Server
        are not supported: their groups are schema-specific LDIF (Scenario 008's jimGroup).

    .OUTPUTS
        A hashtable with Outcome (Created, AlreadyExists or Failed), Success, ExitCode and Output.
    #>
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Test-harness helper against a disposable lab directory; a confirmation prompt would only stall an unattended run.')]
    param(
        [Parameter(Mandatory=$true)]
        [hashtable]$DirectoryConfig,

        [Parameter(Mandatory=$true)]
        [string]$Dn,

        [Parameter(Mandatory=$false)]
        [string]$Description,

        [Parameter(Mandatory=$false)]
        [string]$SamAccountName,

        [Parameter(Mandatory=$false)]
        [ValidateSet('Global', 'Universal', 'DomainLocal')]
        [string]$Scope = 'Global'
    )

    $family = Get-DirectoryFamily -DirectoryConfig $DirectoryConfig
    if ($family -eq 'Rfc') {
        throw "New-DirectoryGroup: creating groups is supported for Samba AD and Active Directory only. The '$($DirectoryConfig['DirectoryType'])' scripts build their groups from LDIF against their own schema; use Invoke-DirectoryLdif."
    }

    $rdn = Split-DirectoryDn -Dn $Dn
    if ($rdn.RdnAttribute -ne 'CN') {
        throw "New-DirectoryGroup: the DN '$Dn' must start with CN=, as a group's does."
    }
    $groupName = if ($SamAccountName) { $SamAccountName } else { $rdn.RdnValue }

    if ($family -eq 'SambaAD') {
        if ($groupName -ne $rdn.RdnValue) {
            throw "New-DirectoryGroup: samba-tool group add names the group and its CN alike, so '$groupName' cannot sit at the CN '$($rdn.RdnValue)'."
        }
        $command = @('samba-tool', 'group', 'add', $groupName)
        $parentRelative = Get-DirectoryRelativeDn -Dn $rdn.ParentDn -BaseDn ([string]$DirectoryConfig['BaseDN'])
        if ($parentRelative) { $command += "--groupou=$parentRelative" }
        if ($Description) { $command += "--description=$Description" }
        if ($Scope -ne 'Global') { $command += "--group-scope=$Scope" }
        $raw = Invoke-DirectoryContainerCommand -DirectoryConfig $DirectoryConfig -Command $command
    }
    else {
        # Security-enabled (0x80000000) plus the scope: global 2, domain local 4, universal 8.
        $groupType = @{ Global = -2147483646; DomainLocal = -2147483644; Universal = -2147483640 }[$Scope]
        $lines = @("dn: $Dn", 'objectClass: top', 'objectClass: group', (Format-LdifAttribute -Name 'cn' -Value $rdn.RdnValue),
                   (Format-LdifAttribute -Name 'sAMAccountName' -Value $groupName), "groupType: $groupType")
        if ($Description) { $lines += (Format-LdifAttribute -Name 'description' -Value $Description) }
        $raw = Invoke-DirectoryLdapTool -DirectoryConfig $DirectoryConfig -Tool ldapadd `
            -Arguments (Get-DirectoryToolBindArgument -DirectoryConfig $DirectoryConfig) -InputObject ($lines -join "`n")
    }

    $outcome = Get-DirectoryCreateOutcome -Raw $raw
    return New-DirectoryResult -Raw $raw -Outcome $outcome -Success ($outcome -ne 'Failed')
}

function Remove-DirectoryGroup {
    <#
    .SYNOPSIS
        Delete a group by DN. Samba AD: samba-tool group delete <name>. Everything else: ldapdelete.

    .OUTPUTS
        A hashtable with Outcome (Deleted, NotFound or Failed), Success, ExitCode and Output.
    #>
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Test-harness helper against a disposable lab directory; a confirmation prompt would only stall an unattended run.')]
    param(
        [Parameter(Mandatory=$true)]
        [hashtable]$DirectoryConfig,

        [Parameter(Mandatory=$true)]
        [string]$Dn,

        [Parameter(Mandatory=$false)]
        [string]$SamAccountName
    )

    if ((Get-DirectoryFamily -DirectoryConfig $DirectoryConfig) -ne 'SambaAD') {
        return Remove-DirectoryEntry -DirectoryConfig $DirectoryConfig -Dn $Dn
    }

    $groupName = if ($SamAccountName) { $SamAccountName } else { (Split-DirectoryDn -Dn $Dn).RdnValue }
    $raw = Invoke-DirectoryContainerCommand -DirectoryConfig $DirectoryConfig -Command @('samba-tool', 'group', 'delete', $groupName)
    $outcome = if ($raw.ExitCode -eq 0) { 'Deleted' }
               elseif ($raw.Output -match 'Unable to find|No such object|does not exist') { 'NotFound' }
               else { 'Failed' }
    return New-DirectoryResult -Raw $raw -Outcome $outcome -Success ($outcome -ne 'Failed')
}

function Set-DirectoryGroupMember {
    # Shared body of Add-DirectoryGroupMember and Remove-DirectoryGroupMember.
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Test-harness helper against a disposable lab directory; a confirmation prompt would only stall an unattended run.')]
    param(
        [hashtable]$DirectoryConfig,
        [string]$GroupDn,
        [string]$MemberDn,
        [string]$MemberSamAccountName,
        [ValidateSet('add', 'delete')]
        [string]$Action
    )

    $family = Get-DirectoryFamily -DirectoryConfig $DirectoryConfig

    if ($MemberSamAccountName -and $family -eq 'SambaAD') {
        $groupName = (Split-DirectoryDn -Dn $GroupDn).RdnValue
        $verb = if ($Action -eq 'add') { 'addmembers' } else { 'removemembers' }
        $raw = Invoke-DirectoryContainerCommand -DirectoryConfig $DirectoryConfig -Command @('samba-tool', 'group', $verb, $groupName, $MemberSamAccountName)
        return New-DirectoryResult -Raw $raw
    }

    if (-not $MemberDn) {
        $nameAttribute = if ($DirectoryConfig['UserNameAttr']) { [string]$DirectoryConfig['UserNameAttr'] } else { 'sAMAccountName' }
        $found = @(Find-DirectoryEntry -DirectoryConfig $DirectoryConfig -Filter "($nameAttribute=$(ConvertTo-LdapFilterValue -Value $MemberSamAccountName))" -Attributes '1.1')
        if ($found.Count -eq 0) {
            throw "Set-DirectoryGroupMember: no account '$MemberSamAccountName' ($nameAttribute) found under '$($DirectoryConfig['BaseDN'])' to change the membership of '$GroupDn'."
        }
        $MemberDn = [string]$found[0]['dn']
    }

    $ldif = @("dn: $GroupDn", 'changetype: modify', "${Action}: member", (Format-LdifAttribute -Name 'member' -Value $MemberDn)) -join "`n"
    return Invoke-DirectoryLdif -DirectoryConfig $DirectoryConfig -Ldif $ldif -Operation modify
}

function Add-DirectoryGroupMember {
    <#
    .SYNOPSIS
        Add a member to a group, by member DN or by account name.

    .DESCRIPTION
        By account name on Samba AD: samba-tool group addmembers <group> <name>. Otherwise (a member DN,
        or an account name on any other directory, resolved to its DN first) an ldapmodify that adds the
        member value. The group's name for samba-tool is the CN of -GroupDn.

    .OUTPUTS
        A hashtable with Success, ExitCode and Output.
    #>
    param(
        [Parameter(Mandatory=$true)]
        [hashtable]$DirectoryConfig,

        [Parameter(Mandatory=$true)]
        [string]$GroupDn,

        [Parameter(Mandatory=$true, ParameterSetName='ByDn')]
        [string]$MemberDn,

        [Parameter(Mandatory=$true, ParameterSetName='ByName')]
        [string]$MemberSamAccountName
    )

    return Set-DirectoryGroupMember -DirectoryConfig $DirectoryConfig -GroupDn $GroupDn -MemberDn $MemberDn -MemberSamAccountName $MemberSamAccountName -Action add
}

function Remove-DirectoryGroupMember {
    <#
    .SYNOPSIS
        Remove a member from a group, by member DN or by account name. See Add-DirectoryGroupMember.

    .OUTPUTS
        A hashtable with Success, ExitCode and Output.
    #>
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Test-harness helper against a disposable lab directory; a confirmation prompt would only stall an unattended run.')]
    param(
        [Parameter(Mandatory=$true)]
        [hashtable]$DirectoryConfig,

        [Parameter(Mandatory=$true)]
        [string]$GroupDn,

        [Parameter(Mandatory=$true, ParameterSetName='ByDn')]
        [string]$MemberDn,

        [Parameter(Mandatory=$true, ParameterSetName='ByName')]
        [string]$MemberSamAccountName
    )

    return Set-DirectoryGroupMember -DirectoryConfig $DirectoryConfig -GroupDn $GroupDn -MemberDn $MemberDn -MemberSamAccountName $MemberSamAccountName -Action delete
}

function Get-DirectoryGroupMember {
    <#
    .SYNOPSIS
        A group's direct members, as DNs or (with -AsSamAccountName) as account names.

    .DESCRIPTION
        Samba AD with -AsSamAccountName: samba-tool group listmembers. Samba AD and Active Directory
        otherwise: a search on memberOf, which a group with more members than one read of its member
        attribute returns (Active Directory answers those in ranges) does not truncate. OpenLDAP and 389
        Directory Server: the group's member attribute (account names are the members' RDN values).

    .OUTPUTS
        string[]
    #>
    [CmdletBinding()]
    [OutputType([string[]])]
    param(
        [Parameter(Mandatory=$true)]
        [hashtable]$DirectoryConfig,

        [Parameter(Mandatory=$true)]
        [string]$GroupDn,

        [Parameter(Mandatory=$false)]
        [switch]$AsSamAccountName
    )

    $family = Get-DirectoryFamily -DirectoryConfig $DirectoryConfig

    if ($family -eq 'SambaAD' -and $AsSamAccountName) {
        $groupName = (Split-DirectoryDn -Dn $GroupDn).RdnValue
        $raw = Invoke-DirectoryContainerCommand -DirectoryConfig $DirectoryConfig -Command @('samba-tool', 'group', 'listmembers', $groupName)
        if ($raw.ExitCode -ne 0 -or -not $raw.Output) { return @() }
        return @($raw.Output -split "`n" | Where-Object { $_.Trim() -ne '' } | ForEach-Object { $_.Trim() })
    }

    if ($family -eq 'Rfc') {
        $entry = Get-DirectoryEntry -DirectoryConfig $DirectoryConfig -Dn $GroupDn -Attributes 'member'
        if ($null -eq $entry -or -not $entry.ContainsKey('member')) { return @() }
        $memberDns = @($entry['member'])
        if ($AsSamAccountName) { return @($memberDns | ForEach-Object { (Split-DirectoryDn -Dn $_).RdnValue }) }
        return $memberDns
    }

    $attribute = if ($AsSamAccountName) { 'sAMAccountName' } else { '1.1' }
    $members = @(Find-DirectoryEntry -DirectoryConfig $DirectoryConfig -Filter "(memberOf=$(ConvertTo-LdapFilterValue -Value $GroupDn))" -Attributes $attribute)
    if ($AsSamAccountName) {
        return @($members | ForEach-Object { [string]$_['sAMAccountName'][0] })
    }
    return @($members | ForEach-Object { [string]$_['dn'] })
}

# ---------------------------------------------------------------------------------------------
# Domain password policy
# ---------------------------------------------------------------------------------------------

# Active Directory stores ages as negative 100 nanosecond intervals: this many per day.
$script:DirectoryTicksPerDay = 864000000000
# The value maxPwdAge holds when passwords never expire (Int64.MinValue).
$script:DirectoryNeverExpires = '-9223372036854775808'

function Get-DirectoryPasswordPolicy {
    <#
    .SYNOPSIS
        The domain password policy (Samba AD and Active Directory).

    .DESCRIPTION
        Samba AD: samba-tool domain passwordsettings show, parsed. Active Directory: the domain head
        object's pwdProperties (bit 1 is DOMAIN_PASSWORD_COMPLEX), minPwdLength, pwdHistoryLength,
        minPwdAge and maxPwdAge, the ages converted from negative 100 ns intervals to days (a maxPwdAge
        of the minimum 64-bit value, meaning never expires, reads as 0, as samba-tool shows it).

    .OUTPUTS
        A hashtable with Complexity (bool), MinLength, HistoryLength, MinAgeDays and MaxAgeDays.
    #>
    [CmdletBinding()]
    [OutputType([hashtable])]
    param(
        [Parameter(Mandatory=$true)]
        [hashtable]$DirectoryConfig
    )

    $family = Get-DirectoryFamily -DirectoryConfig $DirectoryConfig
    if ($family -eq 'Rfc') {
        throw "Get-DirectoryPasswordPolicy: the '$($DirectoryConfig['DirectoryType'])' lab has no domain password policy (OpenLDAP enforces its own ppolicy overlay per entry)."
    }

    if ($family -eq 'SambaAD') {
        $raw = Invoke-DirectoryContainerCommand -DirectoryConfig $DirectoryConfig -Command @('samba-tool', 'domain', 'passwordsettings', 'show')
        $read = {
            param([string]$Pattern)
            if ($raw.Output -match $Pattern) { return $Matches[1] }
            return $null
        }
        $minLength = & $read 'Minimum password length:\s*(\d+)'
        if ($null -eq $minLength) {
            throw "Get-DirectoryPasswordPolicy: could not read the domain's Minimum password length from samba-tool. Output: $($raw.Output)"
        }
        $complexity = & $read 'Password complexity:\s*(\w+)'
        $history = & $read 'Password history length:\s*(\d+)'
        $minAge = & $read 'Minimum password age \(days\):\s*(\d+)'
        $maxAge = & $read 'Maximum password age \(days\):\s*(\d+)'
        return @{
            Complexity    = ($complexity -eq 'on')
            MinLength     = [int]$minLength
            HistoryLength = $(if ($null -ne $history) { [int]$history } else { $null })
            MinAgeDays    = $(if ($null -ne $minAge) { [int]$minAge } else { $null })
            MaxAgeDays    = $(if ($null -ne $maxAge) { [int]$maxAge } else { $null })
        }
    }

    $baseDn = [string]$DirectoryConfig['BaseDN']
    $entry = Get-DirectoryEntry -DirectoryConfig $DirectoryConfig -Dn $baseDn -Attributes 'pwdProperties', 'minPwdLength', 'pwdHistoryLength', 'minPwdAge', 'maxPwdAge'
    if ($null -eq $entry) {
        throw "Get-DirectoryPasswordPolicy: the domain head object '$baseDn' was not found."
    }

    $ticksToDays = {
        param([string]$AttributeName)
        if (-not $entry.ContainsKey($AttributeName)) { return 0 }
        $value = [string]@($entry[$AttributeName])[0]
        if ($value -eq $script:DirectoryNeverExpires) { return 0 }
        return [int][math]::Round(-([double]$value) / $script:DirectoryTicksPerDay)
    }
    return @{
        Complexity    = ((([int]$entry['pwdProperties'][0]) -band 1) -ne 0)
        MinLength     = [int]$entry['minPwdLength'][0]
        HistoryLength = [int]$entry['pwdHistoryLength'][0]
        MinAgeDays    = (& $ticksToDays 'minPwdAge')
        MaxAgeDays    = (& $ticksToDays 'maxPwdAge')
    }
}

function Set-DirectoryPasswordPolicy {
    <#
    .SYNOPSIS
        Change the domain password policy (Samba AD and Active Directory); only the settings given change.

    .DESCRIPTION
        Samba AD: samba-tool domain passwordsettings set with a flag per setting given. Active Directory:
        one ldapmodify of the domain head object; -Complexity reads pwdProperties first so that only the
        DOMAIN_PASSWORD_COMPLEX bit changes. An age of 0 days for the maximum means passwords never expire.

    .PARAMETER Complexity
        on or off.

    .OUTPUTS
        A hashtable with Success, ExitCode and Output.
    #>
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Test-harness helper against a disposable lab directory; a confirmation prompt would only stall an unattended run.')]
    param(
        [Parameter(Mandatory=$true)]
        [hashtable]$DirectoryConfig,

        [Parameter(Mandatory=$false)]
        [ValidateSet('on', 'off')]
        [string]$Complexity,

        [Parameter(Mandatory=$false)]
        [int]$MinLength,

        [Parameter(Mandatory=$false)]
        [int]$HistoryLength,

        [Parameter(Mandatory=$false)]
        [Alias('MinAge')]
        [int]$MinAgeDays,

        [Parameter(Mandatory=$false)]
        [Alias('MaxAge')]
        [int]$MaxAgeDays
    )

    $bound = $PSBoundParameters
    if (-not ($bound.ContainsKey('Complexity') -or $bound.ContainsKey('MinLength') -or $bound.ContainsKey('HistoryLength') -or
              $bound.ContainsKey('MinAgeDays') -or $bound.ContainsKey('MaxAgeDays'))) {
        throw "Set-DirectoryPasswordPolicy: give at least one setting to change."
    }

    $family = Get-DirectoryFamily -DirectoryConfig $DirectoryConfig
    if ($family -eq 'Rfc') {
        throw "Set-DirectoryPasswordPolicy: the '$($DirectoryConfig['DirectoryType'])' lab has no domain password policy."
    }

    if ($family -eq 'SambaAD') {
        $command = @('samba-tool', 'domain', 'passwordsettings', 'set')
        if ($bound.ContainsKey('Complexity'))    { $command += "--complexity=$Complexity" }
        if ($bound.ContainsKey('MinLength'))     { $command += "--min-pwd-length=$MinLength" }
        if ($bound.ContainsKey('HistoryLength')) { $command += "--history-length=$HistoryLength" }
        if ($bound.ContainsKey('MinAgeDays'))    { $command += "--min-pwd-age=$MinAgeDays" }
        if ($bound.ContainsKey('MaxAgeDays'))    { $command += "--max-pwd-age=$MaxAgeDays" }
        $raw = Invoke-DirectoryContainerCommand -DirectoryConfig $DirectoryConfig -Command $command
        return New-DirectoryResult -Raw $raw
    }

    $baseDn = [string]$DirectoryConfig['BaseDN']
    $changes = [System.Collections.Generic.List[string]]::new()

    if ($bound.ContainsKey('Complexity')) {
        $entry = Get-DirectoryEntry -DirectoryConfig $DirectoryConfig -Dn $baseDn -Attributes 'pwdProperties'
        if ($null -eq $entry -or -not $entry.ContainsKey('pwdProperties')) {
            throw "Set-DirectoryPasswordPolicy: could not read pwdProperties from the domain head object '$baseDn'."
        }
        $current = [int]$entry['pwdProperties'][0]
        $updated = if ($Complexity -eq 'on') { $current -bor 1 } else { $current -band (-bnot 1) }
        $changes.Add("replace: pwdProperties`npwdProperties: $updated")
    }
    if ($bound.ContainsKey('MinLength')) { $changes.Add("replace: minPwdLength`nminPwdLength: $MinLength") }
    if ($bound.ContainsKey('HistoryLength')) { $changes.Add("replace: pwdHistoryLength`npwdHistoryLength: $HistoryLength") }
    if ($bound.ContainsKey('MinAgeDays')) {
        $changes.Add("replace: minPwdAge`nminPwdAge: $(if ($MinAgeDays -eq 0) { '0' } else { [string](-([long]$MinAgeDays * $script:DirectoryTicksPerDay)) })")
    }
    if ($bound.ContainsKey('MaxAgeDays')) {
        $changes.Add("replace: maxPwdAge`nmaxPwdAge: $(if ($MaxAgeDays -eq 0) { $script:DirectoryNeverExpires } else { [string](-([long]$MaxAgeDays * $script:DirectoryTicksPerDay)) })")
    }

    $ldif = "dn: $baseDn`nchangetype: modify`n" + ($changes -join "`n-`n")
    return Invoke-DirectoryLdif -DirectoryConfig $DirectoryConfig -Ldif $ldif -Operation modify
}
