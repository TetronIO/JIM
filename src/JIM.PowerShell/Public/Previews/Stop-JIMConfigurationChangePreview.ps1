# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Stop-JIMConfigurationChangePreview {
    <#
    .SYNOPSIS
        Stops a running Configuration Change Preview.

    .DESCRIPTION
        Abandons a preview that is still evaluating. Nothing is deleted: the preview and whatever it had
        recorded stay readable with its Activity marked cancelled, because an administrator who stopped a
        preview after seeing its first stage usually stopped it because of what that stage said.

        A cancelled preview covers only the objects it had reached, so its counts are not an answer about
        the whole population. Run a new preview for that.

        Stopping a preview that has already finished is reported as an error rather than silently
        succeeding: there was nothing to stop, and the results are still there to read.

    .PARAMETER ActivityId
        The preview's Activity id.

    .EXAMPLE
        Stop-JIMConfigurationChangePreview -ActivityId 019fc824-f8c6-7588-8d9a-24a295e7621d

        Stops a running preview.

    .LINK
        New-JIMConfigurationChangePreview
        Get-JIMConfigurationChangePreview
    #>
    [CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'Low')]
    param(
        [Parameter(Mandatory, ValueFromPipelineByPropertyName)]
        [guid]$ActivityId
    )

    process {
        # Check connection first
        if (-not $script:JIMConnection) {
            Write-Error "You are not connected to JIM. Run Connect-JIM -Url <your JIM URL> to authenticate, then try again."
            return
        }

        if ($PSCmdlet.ShouldProcess("preview $ActivityId", "Stop Configuration Change Preview")) {
            try {
                Invoke-JIMApi -Endpoint "/api/v1/previews/$ActivityId" -Method 'DELETE' | Out-Null
                Write-Verbose "Stopped preview: $ActivityId"
            }
            catch {
                Write-Error "Failed to stop preview ${ActivityId}: $_"
            }
        }
    }
}
