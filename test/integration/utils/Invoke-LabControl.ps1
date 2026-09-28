# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Drive the Hyper-V host that carries the Active Directory lab, from the integration test runner

.DESCRIPTION
    The Active Directory lab is three real Windows Server domain controllers running as Hyper-V virtual
    machines. The runner (a Linux devcontainer or a CI machine) cannot reach Hyper-V directly, so it
    runs the control-plane scripts in test/integration/ad-lab/host/ on the hypervisor host over
    OpenSSH: build a domain controller, take or restore a checkpoint, report state.

    Invoke-LabControl runs one of those scripts and returns what it printed. It throws on a non-zero
    exit code with the script's own error text, so a failed restore stops the run instead of carrying on
    against a domain controller in an unknown state. Get-LabControlCommand is the pure part, building
    the ssh argument list, so the quoting is tested without ssh or a host.

    No password ever travels on the command line: the host-side scripts read theirs from the host's
    own environment, or take a SecureString when they are run interactively. This function has no
    parameter for one.

    Settings, all from the environment:

        JIM_AD_LAB_CONTROL_HOST   the hypervisor host, a name or an address (required)
        JIM_AD_LAB_CONTROL_USER   the SSH user, a member of Hyper-V Administrators (default jim-lab)
        JIM_AD_LAB_CONTROL_KEY    the SSH private key (default: ssh's own choice)
        JIM_AD_LAB_CONTROL_PORT   the SSH port (default 22)
        JIM_AD_LAB_SCRIPT_ROOT    the folder on the host holding the scripts (default C:\jim-ad-lab)
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Get-LabControlSetting {
    <#
    .SYNOPSIS
        Read one JIM_AD_LAB_CONTROL_* / JIM_AD_LAB_SCRIPT_ROOT environment variable.

    .DESCRIPTION
        Returns the default when the variable is unset or blank. With no -Default the variable is
        required, and the error names it.
    #>
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory=$true)]
        [string]$Name,

        [Parameter(Mandatory=$false)]
        [AllowNull()]
        [AllowEmptyString()]
        [string]$Default
    )

    $value = [System.Environment]::GetEnvironmentVariable($Name)
    if (-not [string]::IsNullOrWhiteSpace($value)) {
        return $value
    }
    if ($PSBoundParameters.ContainsKey('Default')) {
        return $Default
    }

    throw "The Active Directory lab needs the environment variable $Name to be set, and it is not."
}

function Test-LabControlUnsafeText {
    <#
    .SYNOPSIS
        Whether text holds a character the remote shell could interpret: a variable, an escape, a
        command separator, a redirection, a quote or a line break.
    #>
    [CmdletBinding()]
    [OutputType([bool])]
    param(
        [Parameter(Mandatory=$true)]
        [AllowEmptyString()]
        [string]$Text
    )

    return $Text -match '[$`%;&|<>^!"\r\n\0]'
}

function ConvertTo-LabControlArgument {
    <#
    .SYNOPSIS
        Quote one argument for the remote command line, or refuse it.

    .DESCRIPTION
        The remote command is one string that the host's shell (cmd.exe or PowerShell, depending on how
        OpenSSH is set up there) hands to pwsh. An argument made only of characters both shells leave
        alone goes through as it is; one with a space is wrapped in double quotes; one carrying a
        character either shell would interpret is refused, because no quoting is right for both.
        Every argument this is used for is a name, an address, a path or a switch.
    #>
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory=$true)]
        [AllowEmptyString()]
        [string]$Argument
    )

    if (Test-LabControlUnsafeText -Text $Argument) {
        throw "Invoke-LabControl: the argument '$($Argument -replace '[\r\n]', ' ')' holds a character that is unsafe on a remote command line (one of `$ `` % ; & | < > ^ ! `" or a line break). Refusing to send it."
    }
    # Quoted unless it is made only of characters no shell touches. A comma matters: with PowerShell as the
    # host's OpenSSH shell an unquoted "a,b" is parsed as an array and reaches the script as two
    # arguments, so a Distinguished Name would arrive in pieces. Double quotes are read the same way by
    # cmd.exe and PowerShell for these values, and the argument cannot contain one (refused above).
    if ($Argument -eq '' -or $Argument -notmatch '^[A-Za-z0-9._:\\/=-]+$') {
        return '"' + $Argument + '"'
    }

    return $Argument
}

function Get-LabControlCommand {
    <#
    .SYNOPSIS
        Build the ssh argument list that runs one lab control script on the hypervisor host.

    .DESCRIPTION
        Pure: reads only the environment and returns the arguments to pass to ssh (not including the ssh
        executable itself). The shape is

            -p <port> [-i <key>] -o BatchMode=yes -o ConnectTimeout=15 -o ServerAliveInterval=30
            <user>@<host> pwsh -NoProfile -NonInteractive -File "<root>\<script>.ps1" <arguments>

        where the whole remote command is the last element, as one string. BatchMode makes ssh fail
        rather than prompt for a password or a host-key confirmation, which would hang an unattended
        run; the host's key must therefore already be in the runner's known_hosts.

    .PARAMETER Script
        The script's file name in the script root, with or without .ps1. Never a path.

    .PARAMETER Arguments
        The script's arguments, one element each. Passwords do not belong here.
    #>
    [CmdletBinding()]
    [OutputType([string[]])]
    param(
        [Parameter(Mandatory=$true)]
        [string]$Script,

        [Parameter(Mandatory=$true)]
        [AllowEmptyCollection()]
        [AllowEmptyString()]
        [string[]]$Arguments
    )

    $scriptName = if ($Script -like '*.ps1') { $Script } else { "$Script.ps1" }
    if ($scriptName -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]*\.ps1$') {
        throw "Invoke-LabControl: '$Script' is not a script name. Give the file name of a script in the lab script root, with no path."
    }

    $controlHost = Get-LabControlSetting -Name 'JIM_AD_LAB_CONTROL_HOST'
    $user = Get-LabControlSetting -Name 'JIM_AD_LAB_CONTROL_USER' -Default 'jim-lab'
    $key = Get-LabControlSetting -Name 'JIM_AD_LAB_CONTROL_KEY' -Default ''
    $port = Get-LabControlSetting -Name 'JIM_AD_LAB_CONTROL_PORT' -Default '22'
    $root = Get-LabControlSetting -Name 'JIM_AD_LAB_SCRIPT_ROOT' -Default 'C:\jim-ad-lab'

    if ($port -notmatch '^\d{1,5}$') {
        throw "JIM_AD_LAB_CONTROL_PORT must be a port number, not '$port'."
    }
    if ((Test-LabControlUnsafeText -Text $root) -or (Test-LabControlUnsafeText -Text $user) -or (Test-LabControlUnsafeText -Text $controlHost)) {
        throw "JIM_AD_LAB_SCRIPT_ROOT, JIM_AD_LAB_CONTROL_USER and JIM_AD_LAB_CONTROL_HOST must not contain a character the remote shell would interpret (`$ `` % ; & | < > ^ ! `" or a line break)."
    }

    $scriptPath = ($root.TrimEnd('\')) + '\' + $scriptName
    $remoteParts = [System.Collections.Generic.List[string]]::new()
    $remoteParts.Add('pwsh -NoProfile -NonInteractive -File "' + $scriptPath + '"')
    foreach ($argument in $Arguments) {
        $remoteParts.Add((ConvertTo-LabControlArgument -Argument $argument))
    }

    $argv = [System.Collections.Generic.List[string]]::new()
    $argv.Add('-p')
    $argv.Add($port)
    if ($key) {
        $argv.Add('-i')
        $argv.Add($key)
    }
    foreach ($option in 'BatchMode=yes', 'ConnectTimeout=15', 'ServerAliveInterval=30') {
        $argv.Add('-o')
        $argv.Add($option)
    }
    $argv.Add("$user@$controlHost")
    $argv.Add(($remoteParts -join ' '))

    return $argv.ToArray()
}

function Invoke-LabControlProcess {
    <#
    .SYNOPSIS
        Run an executable, wait for it up to a timeout, and return its exit code, stdout and stderr.

    .DESCRIPTION
        The one place a process is started, so Invoke-LabControl is testable with this replaced. Output
        is read concurrently so a chatty script cannot fill a pipe and stall. Standard input is closed
        at once, so ssh never waits on it. A process still running at the timeout is killed with its
        children and the call throws.

    .OUTPUTS
        A hashtable with ExitCode, StdOut and StdErr.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory=$true)]
        [string]$FilePath,

        [Parameter(Mandatory=$true)]
        [AllowEmptyCollection()]
        [string[]]$ArgumentList,

        [Parameter(Mandatory=$true)]
        [int]$TimeoutSeconds
    )

    $startInfo = [System.Diagnostics.ProcessStartInfo]::new($FilePath)
    foreach ($argument in $ArgumentList) { $startInfo.ArgumentList.Add($argument) }
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardInput = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true

    try {
        $process = [System.Diagnostics.Process]::Start($startInfo)
    }
    catch {
        throw "Invoke-LabControl: could not start '$FilePath': $($_.Exception.Message)"
    }

    try {
        $process.StandardInput.Close()
        $stdOutTask = $process.StandardOutput.ReadToEndAsync()
        $stdErrTask = $process.StandardError.ReadToEndAsync()

        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
            try { $process.Kill($true) } catch { Write-Verbose "Could not kill the timed-out process: $_" }
            throw "Invoke-LabControl: '$FilePath' timed out after $TimeoutSeconds seconds and was stopped."
        }
        # The parameterless overload waits for the redirected streams to finish, as the timed one may not.
        $process.WaitForExit()

        return @{
            ExitCode = $process.ExitCode
            StdOut   = $stdOutTask.GetAwaiter().GetResult()
            StdErr   = $stdErrTask.GetAwaiter().GetResult()
        }
    }
    finally {
        $process.Dispose()
    }
}

function Invoke-LabControl {
    <#
    .SYNOPSIS
        Run a lab control script on the Hyper-V host over SSH and return what it printed.

    .DESCRIPTION
        See the file header. Throws when ssh cannot connect or the script exits non-zero, with the exit
        code and the script's error text in the message. Returns standard output with trailing line
        breaks removed, which for a script run with -AsJson is the JSON document.

    .PARAMETER Script
        The script's file name (Get-LabDomainController.ps1, or without the extension).

    .PARAMETER Arguments
        The script's arguments, one element each, for example @('-Name', 'dc-primary', '-AsJson').

    .PARAMETER TimeoutSeconds
        How long to wait before stopping the call. Defaults to 900 seconds; building a domain
        controller takes far longer and needs a larger value from its caller.

    .EXAMPLE
        $state = Invoke-LabControl -Script Get-LabDomainController.ps1 -Arguments @('-Name', 'dc-primary', '-AsJson') | ConvertFrom-Json
    #>
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory=$true)]
        [string]$Script,

        [Parameter(Mandatory=$true)]
        [AllowEmptyCollection()]
        [AllowEmptyString()]
        [string[]]$Arguments,

        [Parameter(Mandatory=$false)]
        [int]$TimeoutSeconds = 900
    )

    $argv = Get-LabControlCommand -Script $Script -Arguments $Arguments
    $scriptName = if ($Script -like '*.ps1') { $Script } else { "$Script.ps1" }

    $result = Invoke-LabControlProcess -FilePath 'ssh' -ArgumentList $argv -TimeoutSeconds $TimeoutSeconds

    if ($result.ExitCode -ne 0) {
        $detail = $result.StdErr.Trim()
        if (-not $detail) { $detail = $result.StdOut.Trim() }
        throw "Invoke-LabControl: $scriptName on the lab host failed with exit code $($result.ExitCode): $detail"
    }

    return $result.StdOut.TrimEnd("`r", "`n")
}
