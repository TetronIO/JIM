# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Start-JIMConnectedSystemAuxiliaryClassDiscovery {
    <#
    .SYNOPSIS
        Starts a run that reads a Connected System's entries to find which auxiliary classes they carry.

    .DESCRIPTION
        Records, per Object Type, how many entries carry each auxiliary class, and offers those
        counts as suggestions when you list auxiliary classes. It changes no configuration: what an
        Object Type carries stays whatever an administrator has merged.

        A quick sample reads a fixed number of entries of each Object Type. It is fast, and enough
        to find the classes a population uses consistently, but a directory returns entries in its
        own order, so a quick sample cannot prove a class unused.

        A full scan reads every entry in scope, asking for class membership and nothing else. It is
        the only scope whose answer of "this class is not in use" means anything, and it can take a
        long time on a large directory.

        The run is queued as a worker task and reports against an Activity, so it can be watched and
        cancelled like any other long-running operation. One run at a time per Connected System.

    .PARAMETER ConnectedSystemId
        The unique identifier of the Connected System to read.

    .PARAMETER Scope
        QuickSample to read a fixed number of entries of each Object Type, or FullScan to read every
        entry in scope.

    .PARAMETER SampleSizePerObjectType
        How many entries of each Object Type a quick sample should read. Defaults to 5000, and is
        ignored for a full scan, which has no per-type limit.

    .OUTPUTS
        PSCustomObject with WorkerTaskId and ActivityId, for watching or cancelling the run.

    .EXAMPLE
        Start-JIMConnectedSystemAuxiliaryClassDiscovery -ConnectedSystemId 1 -Scope QuickSample

        Reads the first 5000 entries of each Object Type.

    .EXAMPLE
        Start-JIMConnectedSystemAuxiliaryClassDiscovery -ConnectedSystemId 1 -Scope FullScan

        Reads every entry in scope. Complete, and potentially long-running on a large directory.

    .EXAMPLE
        $run = Start-JIMConnectedSystemAuxiliaryClassDiscovery -ConnectedSystemId 1 -Scope QuickSample -SampleSizePerObjectType 20000
        Get-JIMActivity -Id $run.ActivityId

        Starts a larger sample and reads the Activity carrying its progress.

    .LINK
        Get-JIMConnectedSystemAuxiliaryClassDiscovery
        Get-JIMConnectedSystemAuxiliaryClass
        Get-JIMActivity
    #>
    [CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'Medium')]
    [OutputType([PSCustomObject])]
    param(
        [Parameter(Mandatory, ValueFromPipelineByPropertyName)]
        [int]$ConnectedSystemId,

        [Parameter(Mandatory)]
        [ValidateSet('QuickSample', 'FullScan')]
        [string]$Scope,

        [Parameter()]
        [ValidateRange(1, 1000000)]
        [int]$SampleSizePerObjectType = 5000
    )

    process {
        # Check connection first
        if (-not $script:JIMConnection) {
            Write-Error "You are not connected to JIM. Run Connect-JIM -Url <your JIM URL> to authenticate, then try again."
            return
        }

        $body = @{ scope = $Scope }

        # A full scan reads everything, so sending a sample size on one would be a number that
        # silently did nothing.
        if ($Scope -eq 'QuickSample') {
            $body.sampleSizePerObjectType = $SampleSizePerObjectType
        }

        $description = if ($Scope -eq 'QuickSample') {
            "Start auxiliary class discovery (quick sample of $SampleSizePerObjectType entries per Object Type)"
        }
        else {
            "Start auxiliary class discovery (full scan)"
        }

        if ($PSCmdlet.ShouldProcess("Connected System $ConnectedSystemId", $description)) {
            Write-Verbose "Starting auxiliary class discovery for Connected System: $ConnectedSystemId at scope: $Scope"

            try {
                Invoke-JIMApi -Endpoint "/api/v1/synchronisation/connected-systems/$ConnectedSystemId/auxiliary-class-discovery" -Method 'POST' -Body $body
            }
            catch {
                Write-Error "Failed to start auxiliary class discovery: $_"
            }
        }
    }
}
