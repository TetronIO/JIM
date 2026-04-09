<#
.SYNOPSIS
    Captures host hardware profile and resource availability for integration test metrics.

.DESCRIPTION
    Collects CPU, memory, disk, and swap information to enable fair cross-host performance
    comparison in the metrics dashboard. Runs at the start of every integration test run
    to ensure resource availability values (disk free, RAM free, swap free, CPU utilisation)
    are current.

    Cross-platform: works on Linux (devcontainers), macOS, and Windows.

.PARAMETER AsJson
    Return the fingerprint as a JSON string instead of a PowerShell object.

.EXAMPLE
    $fingerprint = & ./Get-HostFingerprint.ps1
    $fingerprint.hostClass  # e.g. "4c-8g-ssd"

.EXAMPLE
    $json = & ./Get-HostFingerprint.ps1 -AsJson
#>

param(
    [switch]$AsJson
)

$ErrorActionPreference = "Continue"

# --- CPU Model ---
$cpuModel = "Unknown"
if ($IsLinux) {
    $cpuInfo = Get-Content /proc/cpuinfo -ErrorAction SilentlyContinue | Select-String "model name"
    if ($cpuInfo) {
        $cpuModel = ($cpuInfo[0] -split ":\s*", 2)[1].Trim()
    }
}
elseif ($IsMacOS) {
    $cpuModel = (sysctl -n machdep.cpu.brand_string 2>$null)
    if (-not $cpuModel) { $cpuModel = "Unknown" }
}
else {
    try {
        $cpuModel = (Get-CimInstance Win32_Processor -ErrorAction SilentlyContinue).Name
        if (-not $cpuModel) { $cpuModel = "Unknown" }
    }
    catch { $cpuModel = "Unknown" }
}

# --- Core Count ---
$cores = 0
if ($IsLinux) {
    $cores = [int](nproc 2>$null)
}
elseif ($IsMacOS) {
    $cores = [int](sysctl -n hw.ncpu 2>$null)
}
else {
    $cores = [int]$env:NUMBER_OF_PROCESSORS
}
if ($cores -eq 0) { $cores = 1 }

# --- RAM Total (GB) ---
$ramGb = 0
if ($IsLinux) {
    $memInfo = Get-Content /proc/meminfo -ErrorAction SilentlyContinue | Select-String "MemTotal"
    if ($memInfo) {
        $kB = [long](($memInfo[0] -split "\s+")[1])
        $ramGb = [math]::Round($kB / 1048576, 1)
    }
}
elseif ($IsMacOS) {
    $bytes = [long](sysctl -n hw.memsize 2>$null)
    if ($bytes -gt 0) { $ramGb = [math]::Round($bytes / 1073741824, 1) }
}
else {
    try {
        $bytes = (Get-CimInstance Win32_ComputerSystem -ErrorAction SilentlyContinue).TotalPhysicalMemory
        if ($bytes -gt 0) { $ramGb = [math]::Round($bytes / 1073741824, 1) }
    }
    catch { }
}

# --- RAM Free (GB) ---
$ramFreeGb = 0
if ($IsLinux) {
    $memAvailable = Get-Content /proc/meminfo -ErrorAction SilentlyContinue | Select-String "MemAvailable"
    if ($memAvailable) {
        $kB = [long](($memAvailable[0] -split "\s+")[1])
        $ramFreeGb = [math]::Round($kB / 1048576, 1)
    }
}
elseif ($IsMacOS) {
    # vm_stat reports in pages; page size is typically 16384 on Apple Silicon, 4096 on Intel
    $pageSize = [long](sysctl -n hw.pagesize 2>$null)
    if ($pageSize -gt 0) {
        $vmStat = vm_stat 2>$null
        $freePages = 0
        $inactivePages = 0
        if ($vmStat) {
            $freeLine = $vmStat | Select-String "Pages free"
            if ($freeLine) { $freePages = [long](($freeLine -split "\s+")[-1] -replace '\.', '') }
            $inactiveLine = $vmStat | Select-String "Pages inactive"
            if ($inactiveLine) { $inactivePages = [long](($inactiveLine -split "\s+")[-1] -replace '\.', '') }
        }
        $ramFreeGb = [math]::Round(($freePages + $inactivePages) * $pageSize / 1073741824, 1)
    }
}
else {
    try {
        $os = Get-CimInstance Win32_OperatingSystem -ErrorAction SilentlyContinue
        if ($os) { $ramFreeGb = [math]::Round($os.FreePhysicalMemory / 1048576, 1) }
    }
    catch { }
}

# --- Disk Type ---
$diskType = "unknown"
if ($IsLinux) {
    # Find the root device and check if it's rotational
    $lsblkOutput = lsblk -d -o NAME,ROTA -n 2>$null
    if ($lsblkOutput) {
        # Get the first disk device (typically sda or vda)
        $firstDisk = ($lsblkOutput | Select-Object -First 1).Trim() -split "\s+"
        if ($firstDisk.Count -ge 2) {
            $diskType = if ($firstDisk[1] -eq "0") { "ssd" } else { "hdd" }
        }
    }
}
elseif ($IsMacOS) {
    $storageInfo = system_profiler SPStorageDataType 2>$null
    if ($storageInfo) {
        $diskType = if ($storageInfo -match "Solid State|SSD|NVMe") { "ssd" } else { "hdd" }
    }
}
else {
    try {
        $disk = Get-PhysicalDisk -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($disk) {
            $diskType = if ($disk.MediaType -eq "SSD" -or $disk.MediaType -eq "NVMe") { "ssd" } else { "hdd" }
        }
    }
    catch { }
}

# --- Disk Size and Free Space (GB) ---
$diskSizeGb = 0
$diskFreeGb = 0
if ($IsLinux) {
    $dfOutput = df -BG / 2>$null | Select-Object -Skip 1 | Select-Object -First 1
    if ($dfOutput) {
        $parts = $dfOutput.Trim() -split "\s+"
        if ($parts.Count -ge 4) {
            $diskSizeGb = [int]($parts[1] -replace 'G', '')
            $diskFreeGb = [int]($parts[3] -replace 'G', '')
        }
    }
}
elseif ($IsMacOS) {
    $dfOutput = df -g / 2>$null | Select-Object -Skip 1 | Select-Object -First 1
    if ($dfOutput) {
        $parts = $dfOutput.Trim() -split "\s+"
        if ($parts.Count -ge 4) {
            $diskSizeGb = [int]$parts[1]
            $diskFreeGb = [int]$parts[3]
        }
    }
}
else {
    try {
        $drive = Get-CimInstance Win32_LogicalDisk -Filter "DeviceID='C:'" -ErrorAction SilentlyContinue
        if ($drive) {
            $diskSizeGb = [math]::Round($drive.Size / 1073741824)
            $diskFreeGb = [math]::Round($drive.FreeSpace / 1073741824)
        }
    }
    catch { }
}

# --- Swap Size and Free (GB) ---
$swapSizeGb = 0
$swapFreeGb = 0
if ($IsLinux) {
    $swapTotal = Get-Content /proc/meminfo -ErrorAction SilentlyContinue | Select-String "SwapTotal"
    $swapFree = Get-Content /proc/meminfo -ErrorAction SilentlyContinue | Select-String "SwapFree"
    if ($swapTotal) {
        $kB = [long](($swapTotal[0] -split "\s+")[1])
        $swapSizeGb = [math]::Round($kB / 1048576, 1)
    }
    if ($swapFree) {
        $kB = [long](($swapFree[0] -split "\s+")[1])
        $swapFreeGb = [math]::Round($kB / 1048576, 1)
    }
}
elseif ($IsMacOS) {
    $swapUsage = sysctl -n vm.swapusage 2>$null
    if ($swapUsage) {
        if ($swapUsage -match "total\s*=\s*([\d.]+)M") { $swapSizeGb = [math]::Round([double]$Matches[1] / 1024, 1) }
        if ($swapUsage -match "free\s*=\s*([\d.]+)M") { $swapFreeGb = [math]::Round([double]$Matches[1] / 1024, 1) }
    }
}
else {
    try {
        $pageFile = Get-CimInstance Win32_PageFileUsage -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($pageFile) {
            $swapSizeGb = [math]::Round($pageFile.AllocatedBaseSize / 1024, 1)
            $swapFreeGb = [math]::Round(($pageFile.AllocatedBaseSize - $pageFile.CurrentUsage) / 1024, 1)
        }
    }
    catch { }
}

# --- CPU Utilisation % ---
$cpuUtilisationPct = 0
if ($IsLinux) {
    # Take two samples 0.5s apart; first sample is cumulative since boot and unreliable
    $topOutput = top -bn2 -d0.5 2>$null | Select-String "Cpu\(s\)" | Select-Object -Last 1
    if ($topOutput) {
        # Parse idle percentage and subtract from 100
        if ($topOutput -match "([\d.]+)\s*id") {
            $cpuUtilisationPct = [math]::Round(100 - [double]$Matches[1], 1)
        }
    }
}
elseif ($IsMacOS) {
    $topOutput = top -l2 -s1 -n0 2>$null | Select-String "CPU usage" | Select-Object -Last 1
    if ($topOutput) {
        if ($topOutput -match "([\d.]+)%\s*idle") {
            $cpuUtilisationPct = [math]::Round(100 - [double]$Matches[1], 1)
        }
    }
}
else {
    try {
        $cpu = Get-CimInstance Win32_Processor -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($cpu) { $cpuUtilisationPct = [math]::Round($cpu.LoadPercentage, 1) }
    }
    catch { }
}

# --- GitHub Username ---
$githubUsername = $null
try {
    $ghUser = gh api user --jq .login 2>$null
    if ($LASTEXITCODE -eq 0 -and $ghUser) {
        $githubUsername = $ghUser.Trim()
    }
}
catch { }

# --- Host Class Derivation ---
$ramRounded = [math]::Floor($ramGb)
$hostClass = "${cores}c-${ramRounded}g-${diskType}"

# --- Build Result ---
$fingerprint = [ordered]@{
    hostname           = [System.Net.Dns]::GetHostName()
    cpuModel           = $cpuModel
    cores              = $cores
    ramGb              = $ramGb
    ramFreeGb          = $ramFreeGb
    diskType           = $diskType
    diskSizeGb         = $diskSizeGb
    diskFreeGb         = $diskFreeGb
    swapSizeGb         = $swapSizeGb
    swapFreeGb         = $swapFreeGb
    cpuUtilisationPct  = $cpuUtilisationPct
    githubUsername     = $githubUsername
    hostClass          = $hostClass
    capturedAt         = (Get-Date).ToUniversalTime().ToString("o")
}

if ($AsJson) {
    $fingerprint | ConvertTo-Json -Depth 2
}
else {
    [PSCustomObject]$fingerprint
}
