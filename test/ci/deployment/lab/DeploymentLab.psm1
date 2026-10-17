# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    The deployment lab's functions: the matrix, the schedule, the sign-in pages and the report (pure, with Pester
    tests), and the LXD commands the runner drives its VMs with.

.DESCRIPTION
    The deployment lab installs JIM from a release bundle on throwaway LXD virtual machines, one per cell of a matrix
    of operating systems, container runtimes and networks, and checks each the way a customer would; see README.md.
    The functions that decide its results without a VM are pure and tested in Tests/DeploymentLab.Tests.ps1; the
    rest wrap the lxc command line and need the lab host.
#>

Set-StrictMode -Version Latest

$script:Scenarios = @('Install', 'Upgrade', 'InstallerCases', 'ExternalDatabase')
$script:Runtimes = @('docker', 'podman')
$script:Networks = @('airgap', 'online')
$script:MinimumMemoryGiB = 4

#region The matrix

function Test-LabMatrix {
    <#
    .SYNOPSIS
        Returns what is wrong with a matrix, one line per fault, and nothing for a valid one.
    #>
    [CmdletBinding()]
    [OutputType([string[]])]
    param(
        [Parameter(Mandatory)]
        [hashtable]$Matrix
    )

    $imageNames = @($Matrix.Images.Keys | Sort-Object)
    $seen = @{}
    foreach ($cell in $Matrix.Cells) {
        $name = $cell.Name
        if ($seen.ContainsKey($name)) {
            "${name}: more than one cell has this name"
        }
        $seen[$name] = $true

        # LXD takes letters, digits and hyphens, up to 63 characters; the runner prefixes the name, so keep it short.
        if ($name -cnotmatch '^[a-z][a-z0-9-]{0,39}$') {
            "${name}: a cell's name must be lower-case letters, digits and hyphens, starting with a letter, and at most 40 characters"
        }
        if ($cell.Image -notin $imageNames) {
            "${name}: image '$($cell.Image)' is not one of the matrix's images ($($imageNames -join ', '))"
        }
        if ($cell.Runtime -notin $script:Runtimes) {
            "${name}: runtime '$($cell.Runtime)' is not $($script:Runtimes -join ' or ')"
        }
        elseif ($cell.Runtime -eq 'docker' -and $cell.ContainsKey('Rootless') -and $cell.Rootless) {
            "${name}: rootless is for Podman only"
        }
        if ($cell.Network -notin $script:Networks) {
            "${name}: network '$($cell.Network)' is not $($script:Networks -join ' or ')"
        }
        if ($cell.Scenario -notin $script:Scenarios) {
            "${name}: scenario '$($cell.Scenario)' is not one of $($script:Scenarios -join ', ')"
        }
        if ($cell.MemoryGiB -lt $script:MinimumMemoryGiB) {
            "${name}: MemoryGiB must be at least $script:MinimumMemoryGiB"
        }
        $databaseMemory = if ($cell.ContainsKey('DatabaseMemoryGiB')) { $cell.DatabaseMemoryGiB } else { 0 }
        if ($cell.Scenario -eq 'ExternalDatabase' -and $databaseMemory -lt 1) {
            "${name}: an ExternalDatabase cell needs DatabaseMemoryGiB, at least 1, for its database server"
        }
        elseif ($cell.Scenario -ne 'ExternalDatabase' -and $cell.ContainsKey('DatabaseMemoryGiB')) {
            "${name}: DatabaseMemoryGiB is for ExternalDatabase cells only"
        }
    }
}

function Get-LabCellMemory {
    <#
    .SYNOPSIS
        Returns the GiB of memory a cell takes while it runs: its VM's, and its database server's if it has one.
    #>
    [CmdletBinding()]
    [OutputType([int])]
    param(
        [Parameter(Mandatory)]
        [object]$Cell
    )

    [int](Get-LabValue $Cell 'MemoryGiB') + [int](Get-LabValue $Cell 'DatabaseMemoryGiB')
}

function Select-LabCell {
    <#
    .SYNOPSIS
        Returns the cells a run takes: those matching any of the names given (wildcards allowed) and carrying every
        tag given, in the matrix's order.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [object[]]$Cell,

        [string[]]$Name,

        [string[]]$Tag
    )

    foreach ($candidate in $Cell) {
        if ($Name -and -not ($Name | Where-Object { $candidate.Name -like $_ })) {
            continue
        }
        if ($Tag -and ($Tag | Where-Object { $_ -notin @($candidate.Tags) })) {
            continue
        }
        $candidate
    }
}

#endregion

#region Upgrades

function Get-LabPostgresMemory {
    <#
    .SYNOPSIS
        Returns the bundled PostgreSQL's memory settings for a host, sized as the installer sizes them (setup.sh
        size_database; Bundled PostgreSQL Memory in the configuration guide), for an upgrade the guide says to size by
        hand.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [int]$MemoryMB
    )

    $shared = [math]::Min([math]::Floor($MemoryMB / 4), 8192)
    $maintenance = [math]::Min([math]::Floor($MemoryMB / 16), 2048)
    $work = [math]::Max([math]::Floor(($MemoryMB - $shared) / 600), 4)
    [ordered]@{
        JIM_DB_SHARED_BUFFERS = "${shared}MB"
        JIM_DB_EFFECTIVE_CACHE_SIZE = "$([math]::Floor($MemoryMB / 2))MB"
        JIM_DB_MAINTENANCE_WORK_MEM = "${maintenance}MB"
        JIM_DB_WORK_MEM = "${work}MB"
        JIM_DB_SHM_SIZE = "$($shared + [math]::Floor($shared / 4))mb"
    }
}

function ConvertTo-LabMegabytes {
    <#
    .SYNOPSIS
        Returns a PostgreSQL memory size (kB, MB, GB or TB, case-sensitive as PostgreSQL's units are) in MB.
    #>
    [CmdletBinding()]
    [OutputType([long])]
    param(
        [Parameter(Mandatory, Position = 0)]
        [string]$Size
    )

    if ($Size -cnotmatch '^\s*(\d+)\s*(kB|MB|GB|TB)\s*$') {
        throw "'$Size' is not a PostgreSQL size"
    }
    $factor = @{ kB = 1 / 1024; MB = 1; GB = 1024; TB = 1048576 }[$Matches[2]]
    [long]([long]$Matches[1] * $factor)
}

function ConvertTo-LabReleaseNumber {
    param([string]$Version)
    if ($Version -notmatch '^v?(\d+)\.(\d+)\.(\d+)') {
        throw "'$Version' is not a version"
    }
    [version]::new([int]$Matches[1], [int]$Matches[2], [int]$Matches[3])
}

function Test-LabVersionAtMost {
    <#
    .SYNOPSIS
        Says whether a version is the limit given or earlier, by its release number, for the upgrade guide's notes
        that apply to "v0.16.0 or earlier".
    #>
    [CmdletBinding()]
    [OutputType([bool])]
    param(
        [Parameter(Mandatory)]
        [string]$Version,

        [Parameter(Mandatory)]
        [string]$Limit
    )

    (ConvertTo-LabReleaseNumber $Version) -le (ConvertTo-LabReleaseNumber $Limit)
}

function Get-LabNextVersion {
    <#
    .SYNOPSIS
        Returns the patch release after the version given: what a build of the commits after a release reports, so
        that the installer takes it as newer than that release.
    #>
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)]
        [string]$Version
    )

    $number = ConvertTo-LabReleaseNumber $Version
    "$($number.Major).$($number.Minor).$($number.Build + 1)"
}

function ConvertTo-LabRootlessCommand {
    <#
    .SYNOPSIS
        Rewrites a documented command for a rootful JIM for a rootless one, as Running on Podman says: the
        jim-systemctl and jim-podman helpers in place of systemctl and (sudo) podman. A podman that is not a command,
        such as the bundle's podman folder, is left as it is.
    #>
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)]
        [string]$Command
    )

    $rewritten = $Command -replace '(?<![-\w/.])(?:sudo\s+)?podman(?=\s)', 'jim-podman'
    $rewritten -replace '(?<![-\w/.])(?:sudo\s+)?systemctl(?=\s)', 'jim-systemctl'
}

#endregion

#region What the installer leaves behind

function Get-LabSetupMessage {
    <#
    .SYNOPSIS
        Returns the messages setup.sh printed at one level ([INFO], [OK], [WARN] or [ERR]), without the colour codes or
        the label, including one printed on the line of a question it asked.
    #>
    [CmdletBinding()]
    [OutputType([string[]])]
    param(
        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [AllowEmptyString()]
        [string[]]$Line,

        [Parameter(Mandatory)]
        [ValidateSet('INFO', 'OK', 'WARN', 'ERR')]
        [string]$Level
    )

    foreach ($text in $Line) {
        $plain = $text -replace "`e\[[0-9;]*m", ''
        # A message can follow a question on its line, as the answer's newline is the user's, not the installer's.
        if ($plain -match "(?:^|\]: )\[$Level\]\s+(.*)$") {
            $Matches[1]
        }
    }
}

function Get-LabImageNameToDrop {
    <#
    .SYNOPSIS
        Returns the names to take off the PostgreSQL image JIM ran before an upgrade, leaving only the one the
        installer keeps for rolling back (#2299).

    .DESCRIPTION
        When a release's PostgreSQL image takes over the previous one's tag, Docker's containerd image store keeps the
        previous image only through the name the installer gives it. Taking every other name off reproduces that on
        any upgrade, whether or not the two releases' images differ. An image the installer did not keep is left as it
        is: stripping every name would only show that the image is gone.
    #>
    [CmdletBinding()]
    [OutputType([string[]])]
    param(
        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [string[]]$Name,

        [string]$KeptTag = 'jim-previous'
    )

    $kept = @($Name | Where-Object { $_ -like "*:$KeptTag" })
    if ($kept.Count -eq 0) {
        return
    }
    $Name | Where-Object { $_ -notlike "*:$KeptTag" }
}

function Compare-LabFingerprint {
    <#
    .SYNOPSIS
        Returns how two fingerprints of an installation differ: "- <line>" for each line only before, then "+ <line>"
        for each only after, each in the order of its fingerprint, and nothing when they are the same.

    .DESCRIPTION
        A fingerprint (guest/Invoke-LabGuestCommand.ps1 -Action Fingerprint) lists every file of an installation with
        its checksum, and every JIM container running with its image: what a refused or interrupted installer run must
        leave exactly as it was.
    #>
    [CmdletBinding()]
    [OutputType([string[]])]
    param(
        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [AllowEmptyString()]
        [string[]]$Before,

        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [AllowEmptyString()]
        [string[]]$After
    )

    $kept = [System.Collections.Generic.HashSet[string]]::new([string[]]$After)
    $had = [System.Collections.Generic.HashSet[string]]::new([string[]]$Before)
    $Before | Where-Object { -not $kept.Contains($_) } | ForEach-Object { "- $_" }
    $After | Where-Object { -not $had.Contains($_) } | ForEach-Object { "+ $_" }
}

#endregion

#region The schedule

function Get-LabMemoryBudget {
    <#
    .SYNOPSIS
        Returns how many GiB of the host's memory the lab's VMs may take between them: its total less a reserve for
        the host itself (its identity provider, directory and the runner), in whole GiB.
    #>
    [CmdletBinding()]
    [OutputType([int])]
    param(
        [Parameter(Mandatory)]
        [string[]]$MemInfo,

        [Parameter(Mandatory)]
        [int]$ReserveGiB
    )

    $line = $MemInfo | Where-Object { $_ -match '^MemTotal:\s+(\d+)\s+kB' } | Select-Object -First 1
    if (-not $line) {
        throw 'No MemTotal line in the memory information given'
    }
    $null = $line -match '^MemTotal:\s+(\d+)\s+kB'
    $totalGiB = [int][math]::Floor([double]$Matches[1] / 1MB)
    [math]::Max(0, $totalGiB - $ReserveGiB)
}

function Select-LabCellToStart {
    <#
    .SYNOPSIS
        Returns the pending cells to start now: as many as fit in the memory left beside the running ones, largest
        first so that a big cell is not left waiting behind small ones, and the matrix's order among equals.

    .DESCRIPTION
        A cell larger than the whole budget starts on its own when nothing else runs, rather than never.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [object[]]$Pending,

        [Parameter(Mandatory)]
        [int]$RunningGiB,

        [int]$RunningCount = -1,

        [Parameter(Mandatory)]
        [int]$BudgetGiB,

        [int]$MaxParallel = 0
    )

    if ($RunningCount -lt 0) {
        $RunningCount = if ($RunningGiB -gt 0) { 1 } else { 0 }
    }
    $index = 0
    $ordered = $Pending |
        ForEach-Object { [pscustomobject]@{ Cell = $_; Order = $index++ } } |
        Sort-Object -Property @{ Expression = { Get-LabCellMemory -Cell $_.Cell }; Descending = $true }, @{ Expression = 'Order' }

    $free = $BudgetGiB - $RunningGiB
    $count = $RunningCount
    foreach ($entry in $ordered) {
        if ($MaxParallel -gt 0 -and $count -ge $MaxParallel) {
            break
        }
        $size = Get-LabCellMemory -Cell $entry.Cell
        if ($size -le $free) {
            $entry.Cell
            $free -= $size
            $count++
        }
        elseif ($count -eq 0) {
            # Larger than the whole budget: run it alone, now, rather than never.
            $entry.Cell
            break
        }
    }
}

#endregion

#region Signing in as a browser does

function ConvertFrom-HtmlAttribute {
    param([string]$Value)
    [System.Net.WebUtility]::HtmlDecode($Value)
}

function Get-LabLoginForm {
    <#
    .SYNOPSIS
        Returns where Keycloak's login page posts the username and password, or nothing for another page.
    #>
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)]
        [AllowEmptyString()]
        [string]$Html
    )

    $form = [regex]::Match($Html, '<form\b[^>]*\bid="kc-form-login"[^>]*>', 'IgnoreCase')
    if (-not $form.Success) {
        return
    }
    $action = [regex]::Match($form.Value, '\baction="([^"]*)"', 'IgnoreCase')
    if ($action.Success) {
        ConvertFrom-HtmlAttribute $action.Groups[1].Value
    }
}

function Get-LabFormPost {
    <#
    .SYNOPSIS
        Returns the form Keycloak's response_mode=form_post page posts back to JIM, its action and its hidden fields
        in order, or nothing for another page.

    .DESCRIPTION
        Keycloak writes that page's tags in upper case, so every match ignores case.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [AllowEmptyString()]
        [string]$Html
    )

    $form = [regex]::Match($Html, '<form\b[^>]*>', 'IgnoreCase')
    if (-not $form.Success) {
        return
    }
    $action = [regex]::Match($form.Value, '\baction="([^"]*)"', 'IgnoreCase')
    if (-not $action.Success -or $action.Groups[1].Value -notmatch 'signin-oidc') {
        return
    }

    $fields = [ordered]@{}
    foreach ($field in [regex]::Matches($Html, '<input\b[^>]*>', 'IgnoreCase')) {
        if ($field.Value -notmatch '\btype="hidden"') {
            continue
        }
        $name = [regex]::Match($field.Value, '\bname="([^"]*)"', 'IgnoreCase')
        $value = [regex]::Match($field.Value, '\bvalue="([^"]*)"', 'IgnoreCase')
        if ($name.Success) {
            $fields[(ConvertFrom-HtmlAttribute $name.Groups[1].Value)] = if ($value.Success) {
                ConvertFrom-HtmlAttribute $value.Groups[1].Value
            }
            else {
                ''
            }
        }
    }
    [pscustomobject]@{
        Action = ConvertFrom-HtmlAttribute $action.Groups[1].Value
        Fields = $fields
    }
}

#endregion

#region The report

function Get-LabValue {
    # A cell is a hashtable from Cells.psd1, or an object once its results have been through JSON.
    param([object]$Object, [string]$Name)
    if ($Object -is [System.Collections.IDictionary]) {
        if ($Object.Contains($Name)) { $Object[$Name] }
    }
    elseif ($Object.PSObject.Properties[$Name]) {
        $Object.$Name
    }
}

function Get-LabRuntimeLabel {
    param([object]$Cell)
    if ((Get-LabValue $Cell 'Runtime') -eq 'docker') {
        'Docker'
    }
    elseif (Get-LabValue $Cell 'Rootless') {
        'Podman rootless'
    }
    else {
        'Podman rootful'
    }
}

function Test-LabResultPassed {
    param([object]$Result)
    $checks = @($Result.Checks)
    $hasError = $Result.PSObject.Properties['Error'] -and $Result.Error
    $checks.Count -gt 0 -and -not $hasError -and -not ($checks | Where-Object { -not $_.Passed })
}

function Format-LabReport {
    <#
    .SYNOPSIS
        Returns a run's results as Markdown: the outcome, a row per cell, and each failed check with its detail.
    #>
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [object[]]$Result,

        [Parameter(Mandatory)]
        [string]$Title
    )

    $passedCells = @($Result | Where-Object { Test-LabResultPassed $_ }).Count
    $lines = [System.Collections.Generic.List[string]]::new()
    $lines.Add("# $Title")
    $lines.Add('')
    $lines.Add("$passedCells of $(@($Result).Count) cells passed.")
    $lines.Add('')
    $lines.Add('| Cell | Operating system | Runtime | Network | Scenario | Result | Time | Failed |')
    $lines.Add('|---|---|---|---|---|---|---|---|')

    $failures = [System.Collections.Generic.List[string]]::new()
    foreach ($entry in $Result) {
        $cell = $entry.Cell
        $checks = @($entry.Checks)
        $passed = @($checks | Where-Object { $_.Passed }).Count
        $failedChecks = @($checks | Where-Object { -not $_.Passed })
        $cellError = if ($entry.PSObject.Properties['Error'] -and $entry.Error) { $entry.Error }
        $mark = if (Test-LabResultPassed $entry) { '✅' } else { '❌' }
        $network = Get-LabValue $cell 'Network'
        if ($network -eq 'airgap') {
            $network = 'air-gapped'
        }
        $failedText = @(@($failedChecks | ForEach-Object { $_.Name }) + @($cellError) | Where-Object { $_ }) -join '; '
        $lines.Add("| $(Get-LabValue $cell 'Name') | $(Get-LabValue $cell 'Os') | $(Get-LabRuntimeLabel $cell) | $network | $(Get-LabValue $cell 'Scenario') | $mark $passed/$($checks.Count) | $([math]::Round($entry.Minutes)) min | $failedText |")

        foreach ($check in $failedChecks) {
            $failures.Add("- **$(Get-LabValue $cell 'Name')**: $($check.Name)$(if ($check.Detail) { ": $($check.Detail)" })")
        }
        if ($cellError) {
            $failures.Add("- **$(Get-LabValue $cell 'Name')**: $cellError")
        }
    }

    if ($failures.Count -gt 0) {
        $lines.Add('')
        $lines.Add('## Failures')
        $lines.Add('')
        $lines.AddRange($failures)
    }
    $lines -join "`n"
}

#endregion

#region LXD (needs the lab host)

function Invoke-LabLxc {
    <#
    .SYNOPSIS
        Runs lxc with the arguments given and returns its output, throwing with that output if it fails.

    .DESCRIPTION
        lxc's standard input is always closed: lxc init and lxc launch read instance YAML from a standard input that
        is not a terminal, and wait for ever on one that never closes, such as a CI runner's or an agent's.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string[]]$Arguments,

        [switch]$AllowFailure,

        # Also append each line to this file as it comes, to follow a long command while it runs.
        [string]$LogPath
    )

    $output = $null | & lxc @Arguments 2>&1 | ForEach-Object {
        $line = "$_"
        if ($LogPath) {
            Add-Content -LiteralPath $LogPath -Value $line
        }
        $line
    }
    if ($LASTEXITCODE -ne 0 -and -not $AllowFailure) {
        $said = ($output | Select-Object -Last 5) -join ' ' -replace '\x1b\[[0-9;]*m', ''
        throw "lxc $($Arguments[0]) $($Arguments[1]) failed (exit $LASTEXITCODE): $said"
    }
    $output
}

function Invoke-LabVm {
    <#
    .SYNOPSIS
        Runs a command in a lab VM, as root, through the LXD agent, and returns its output.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$Name,

        [Parameter(Mandatory)]
        [string[]]$Command,

        [hashtable]$Environment = @{},

        [string]$WorkingDirectory,

        [switch]$AllowFailure,

        [string]$LogPath
    )

    $arguments = @('exec', $Name)
    foreach ($key in $Environment.Keys) {
        $arguments += @('--env', "$key=$($Environment[$key])")
    }
    if ($WorkingDirectory) {
        $arguments += @('--cwd', $WorkingDirectory)
    }
    Invoke-LabLxc -Arguments ($arguments + '--' + $Command) -AllowFailure:$AllowFailure -LogPath $LogPath
}

function Wait-LabVm {
    <#
    .SYNOPSIS
        Waits until a lab VM's agent answers and the VM has finished booting (systemd and cloud-init both done).
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$Name,

        [int]$TimeoutSeconds = 600
    )

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        # is-system-running --wait returns once boot is over, as running or degraded; either is booted.
        $state = Invoke-LabVm -Name $Name -Command @('systemctl', 'is-system-running', '--wait') -AllowFailure
        if ($LASTEXITCODE -ne 255 -and "$state" -match 'running|degraded') {
            Invoke-LabVm -Name $Name -Command @('cloud-init', 'status', '--wait') -AllowFailure | Out-Null
            return
        }
        Start-Sleep -Seconds 3
    }
    throw "$Name did not finish booting within $TimeoutSeconds s"
}

function New-LabVm {
    <#
    .SYNOPSIS
        Creates and starts a lab VM from one of the lab's images, on the lab network given, and waits for it to boot.
        A VM of the same name is deleted first.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$Name,

        [Parameter(Mandatory)]
        [string]$Image,

        [Parameter(Mandatory)]
        [string[]]$Profile,

        [Parameter(Mandatory)]
        [int]$MemoryGiB,

        [int]$CpuCount = 2,

        [int]$DiskGiB = 0
    )

    Remove-LabVm -Name $Name
    $arguments = @('init', $Image, $Name, '--vm', '-c', "limits.memory=${MemoryGiB}GiB", '-c', "limits.cpu=$CpuCount")
    foreach ($entry in $Profile) {
        $arguments += @('-p', $entry)
    }
    Invoke-LabLxc -Arguments $arguments | Out-Null
    if ($DiskGiB -gt 0) {
        Invoke-LabLxc -Arguments @('config', 'device', 'override', $Name, 'root', "size=${DiskGiB}GiB") | Out-Null
    }
    Invoke-LabLxc -Arguments @('start', $Name) | Out-Null
    Wait-LabVm -Name $Name
}

function Remove-LabVm {
    <#
    .SYNOPSIS
        Deletes a lab VM, running or not; nothing happens if there is none of that name.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$Name
    )

    Invoke-LabLxc -Arguments @('delete', '--force', $Name) -AllowFailure | Out-Null
}

function Restart-LabVm {
    <#
    .SYNOPSIS
        Shuts a lab VM down cleanly, as an administrator rebooting the host would, starts it again and waits for it
        to boot.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$Name
    )

    Invoke-LabLxc -Arguments @('stop', $Name, '--timeout', '180') -AllowFailure | Out-Null
    if ($LASTEXITCODE -ne 0) {
        Invoke-LabLxc -Arguments @('stop', $Name, '--force') | Out-Null
    }
    Invoke-LabLxc -Arguments @('start', $Name) | Out-Null
    Wait-LabVm -Name $Name
}

function Copy-LabFile {
    <#
    .SYNOPSIS
        Copies a file into a lab VM (-Destination 'vm:/path') or out of one (-Source 'vm:/path'), folders with
        -Recurse.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$Source,

        [Parameter(Mandatory)]
        [string]$Destination,

        [switch]$Recurse
    )

    $verb = if ($Source -match '^[a-z][a-z0-9-]*:/') { 'pull' } else { 'push' }
    $remote = { param($Path) $Path -replace '^([a-z][a-z0-9-]*):/', '$1/' }
    $from = if ($verb -eq 'pull') { & $remote $Source } else { $Source }
    $to = if ($verb -eq 'push') { & $remote $Destination } else { $Destination }
    $arguments = @('file', $verb, $from, $to, '--create-dirs')
    if ($Recurse) {
        $arguments += '--recursive'
    }
    Invoke-LabLxc -Arguments $arguments | Out-Null
}

function Set-LabVmProfile {
    <#
    .SYNOPSIS
        Gives a lab VM a new set of profiles, such as another network's, by shutting it down, assigning them and
        starting it again, and waits for it to boot.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$Name,

        [Parameter(Mandatory)]
        [string[]]$Profile
    )

    Invoke-LabLxc -Arguments @('stop', $Name, '--timeout', '180') | Out-Null
    Invoke-LabLxc -Arguments @('profile', 'assign', $Name, ($Profile -join ',')) | Out-Null
    Invoke-LabLxc -Arguments @('start', $Name) | Out-Null
    Wait-LabVm -Name $Name
}

function Get-LabVmAddress {
    <#
    .SYNOPSIS
        Returns a lab VM's IPv4 address on the lab network whose gateway is given, waiting for DHCP if need be.
    #>
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)]
        [string]$Name,

        [Parameter(Mandatory)]
        [string]$Gateway,

        [int]$TimeoutSeconds = 120
    )

    $prefix = [regex]::Escape(($Gateway -replace '\.\d+$', '.'))
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        $addresses = Invoke-LabLxc -Arguments @('list', $Name, '--columns', '4', '--format', 'csv') -AllowFailure
        $match = [regex]::Match("$addresses", "($prefix\d+)")
        if ($match.Success) {
            return $match.Groups[1].Value
        }
        Start-Sleep -Seconds 3
    }
    throw "$Name has no address on the network of $Gateway"
}

#endregion

#region Signing in (needs the lab host)

function Invoke-LabSignIn {
    <#
    .SYNOPSIS
        Signs in to a lab JIM through its identity provider the way a browser does, and says whether JIM let the user
        in: JIM sends an anonymous visitor to the identity provider's login page, the user signs in there, the
        identity provider sends them back to JIM (posting the response, for response_mode=form_post), and JIM then
        serves its home page to the session.

    .DESCRIPTION
        JIM is reached at the name its certificate carries, mapped to the VM's address, trusting only the installer's
        certificate authority, so the check also proves the certificate and the published port.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$Address,

        [Parameter(Mandatory)]
        [string]$HostName,

        [Parameter(Mandatory)]
        [string]$CaPath,

        [Parameter(Mandatory)]
        [string]$User,

        [Parameter(Mandatory)]
        [string]$Password
    )

    $jar = New-TemporaryFile
    $page = New-TemporaryFile
    try {
        $curl = @('curl', '-sS', '--max-time', '30', '--noproxy', $HostName, '--cacert', $CaPath,
            '--resolve', "${HostName}:443:$Address", '-b', $jar.FullName, '-c', $jar.FullName)
        $request = {
            param([string[]]$Arguments)
            $null | & $curl[0] @($curl[1..($curl.Count - 1)] + '-o' + $page.FullName + '-w' + '%{http_code} %{url_effective}' + $Arguments) 2>&1
        }
        $fail = { param([string]$Detail) [pscustomobject]@{ Passed = $false; Detail = $Detail } }

        # 1. JIM sends an anonymous visitor to the identity provider.
        $null = & $request -Arguments @('-L', "https://$HostName/")
        $action = Get-LabLoginForm -Html (Get-Content -Raw $page.FullName)
        if (-not $action) {
            return & $fail 'JIM did not send an anonymous visitor to the identity provider''s login page'
        }

        # 2. Sign in there, and follow the identity provider back to JIM.
        $outcome = & $request -Arguments @('-L', '--data-urlencode', "username=$User", '--data-urlencode', "password=$Password",
            '--data', 'credentialId=', $action)
        $html = Get-Content -Raw $page.FullName
        $post = Get-LabFormPost -Html $html
        if ($post) {
            $fields = foreach ($key in $post.Fields.Keys) { @('--data-urlencode', "$key=$($post.Fields[$key])") }
            $outcome = & $request -Arguments (@('-L') + $fields + $post.Action)
            $html = Get-Content -Raw $page.FullName
        }
        if (Get-LabLoginForm -Html $html) {
            return & $fail 'the identity provider refused the sign-in'
        }
        if ($html -match 'Invalid parameter: redirect_uri|invalid_redirect_uri') {
            return & $fail "the identity provider refused JIM's redirect URI"
        }
        $code, $url = "$outcome".Split(' ', 2)
        if ($code -ne '200' -or $url -notlike "https://$HostName/*") {
            return & $fail "after signing in: HTTP $code at $url"
        }

        # 3. The session JIM issued opens the home page, which sends anyone not signed in to the identity provider.
        $homePage = & $request -Arguments @("https://$HostName/")
        $homeCode = "$homePage".Split(' ')[0]
        if ($homeCode -ne '200') {
            return & $fail "JIM's home page answered the session with HTTP $homeCode"
        }
        [pscustomobject]@{ Passed = $true; Detail = '' }
    }
    finally {
        Remove-Item $jar.FullName, $page.FullName -ErrorAction SilentlyContinue
    }
}

#endregion

#region PowerShell for the VMs

function Get-LabPowerShellArchive {
    <#
    .SYNOPSIS
        Returns the path of PowerShell's Linux archive, of the version running this, from the lab's cache, fetching
        it from PowerShell's GitHub releases the first time. An air-gapped VM cannot fetch it itself.
    #>
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)]
        [string]$CachePath
    )

    $version = $PSVersionTable.PSVersion.ToString()
    $name = "powershell-$version-linux-x64.tar.gz"
    $path = Join-Path $CachePath $name
    if (-not (Test-Path $path)) {
        New-Item -ItemType Directory -Path $CachePath -Force | Out-Null
        $partial = "$path.partial"
        Invoke-WebRequest -Uri "https://github.com/PowerShell/PowerShell/releases/download/v$version/$name" -OutFile $partial
        Move-Item $partial $path -Force
    }
    $path
}

function Get-LabReleaseBundle {
    <#
    .SYNOPSIS
        Returns the path of a published release's bundle archive, with its .sha256 beside it, downloading both from
        GitHub into the lab's cache the first time: the latest release, or the one tagged.
    #>
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)]
        [string]$CachePath,

        [string]$Tag
    )

    $api = if ($Tag) { "https://api.github.com/repos/TetronIO/JIM/releases/tags/$Tag" } else { 'https://api.github.com/repos/TetronIO/JIM/releases/latest' }
    $release = Invoke-RestMethod -Uri $api -Headers @{ Accept = 'application/vnd.github+json' }
    $archive = $release.assets | Where-Object { $_.name -match '^jim-release-.*\.tar\.gz$' } | Select-Object -First 1
    # The archive's own .sha256 since #1942; before it, the release's checksums.sha256, which lists every asset.
    $checksum = $release.assets | Where-Object { $_.name -eq "$($archive.name).sha256" } | Select-Object -First 1
    if (-not $checksum) {
        $checksum = $release.assets | Where-Object { $_.name -eq 'checksums.sha256' } | Select-Object -First 1
    }
    if (-not $archive -or -not $checksum) {
        throw "Release $($release.tag_name) has no bundle archive with a published checksum"
    }
    $folder = Join-Path $CachePath 'releases' $release.tag_name
    New-Item -ItemType Directory -Path $folder -Force | Out-Null
    foreach ($asset in @($checksum, $archive)) {
        $path = Join-Path $folder $asset.name
        if (-not (Test-Path $path)) {
            Invoke-WebRequest -Uri $asset.browser_download_url -OutFile "$path.partial"
            Move-Item "$path.partial" $path -Force
        }
    }
    $line = Get-Content (Join-Path $folder $checksum.name) | Where-Object { $_ -match "\s\*?$([regex]::Escape($archive.name))$" } | Select-Object -First 1
    if (-not $line) {
        # v0.16.0 and earlier published no checksum of the archive (#1942); it came from GitHub over HTTPS.
        Write-Warning "Release $($release.tag_name) publishes no checksum of $($archive.name), so it is used unchecked"
        return Join-Path $folder $archive.name
    }
    $line | Set-Content (Join-Path $folder "$($archive.name).sha256")
    Push-Location $folder
    try {
        $null = & sha256sum -c --quiet "$($archive.name).sha256"
        if ($LASTEXITCODE -ne 0) {
            throw "$($archive.name) does not match its published checksum"
        }
    }
    finally {
        Pop-Location
    }
    Join-Path $folder $archive.name
}

#endregion

Export-ModuleMember -Function Test-LabMatrix, Select-LabCell, Get-LabCellMemory, Get-LabMemoryBudget, Select-LabCellToStart,
    Get-LabPostgresMemory, ConvertTo-LabMegabytes, Test-LabVersionAtMost, Get-LabNextVersion, ConvertTo-LabRootlessCommand,
    Get-LabSetupMessage, Get-LabImageNameToDrop, Compare-LabFingerprint, Get-LabLoginForm, Get-LabFormPost, Format-LabReport, Invoke-LabLxc,
    Invoke-LabVm, Wait-LabVm, New-LabVm, Remove-LabVm, Restart-LabVm, Set-LabVmProfile, Copy-LabFile, Get-LabVmAddress,
    Invoke-LabSignIn, Get-LabPowerShellArchive, Get-LabReleaseBundle
