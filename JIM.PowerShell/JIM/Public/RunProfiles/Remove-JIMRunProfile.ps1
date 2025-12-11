function Remove-JIMRunProfile {
    <#
    .SYNOPSIS
        Removes a Run Profile from JIM.

    .DESCRIPTION
        Permanently deletes a Run Profile from a Connected System.

    .PARAMETER ConnectedSystemId
        The ID of the Connected System the Run Profile belongs to.

    .PARAMETER RunProfileId
        The unique identifier of the Run Profile to delete.

    .PARAMETER InputObject
        Run Profile object to delete (from pipeline).

    .PARAMETER Force
        Suppresses confirmation prompts.

    .PARAMETER PassThru
        If specified, returns the deleted Run Profile object.

    .OUTPUTS
        If -PassThru is specified, returns the deleted Run Profile object.

    .EXAMPLE
        Remove-JIMRunProfile -ConnectedSystemId 1 -RunProfileId 1

        Removes the Run Profile (prompts for confirmation).

    .EXAMPLE
        Remove-JIMRunProfile -ConnectedSystemId 1 -RunProfileId 1 -Force

        Removes the Run Profile without confirmation.

    .EXAMPLE
        Get-JIMRunProfile -ConnectedSystemId 1 | Where-Object { $_.name -like "Test*" } | Remove-JIMRunProfile -Force

        Removes all Run Profiles with names starting with "Test".

    .LINK
        Get-JIMRunProfile
        New-JIMRunProfile
        Set-JIMRunProfile
        Start-JIMRunProfile
    #>
    [CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High', DefaultParameterSetName = 'ById')]
    [OutputType([PSCustomObject])]
    param(
        [Parameter(Mandatory, ParameterSetName = 'ById')]
        [int]$ConnectedSystemId,

        [Parameter(Mandatory, ParameterSetName = 'ById')]
        [int]$RunProfileId,

        [Parameter(Mandatory, ParameterSetName = 'ByInputObject', ValueFromPipeline)]
        [PSCustomObject]$InputObject,

        [switch]$Force,

        [switch]$PassThru
    )

    process {
        # Check connection first
        if (-not $script:JIMConnection) {
            Write-Error "Not connected to JIM. Use Connect-JIM first."
            return
        }

        $csId = if ($InputObject) { $InputObject.connectedSystemId } else { $ConnectedSystemId }
        $profileId = if ($InputObject) { $InputObject.id } else { $RunProfileId }

        if (-not $csId) {
            Write-Error "ConnectedSystemId is required. Provide -ConnectedSystemId parameter or pipe an object with connectedSystemId property."
            return
        }

        # Get the profile first for confirmation message and PassThru
        $existing = $null
        if ($InputObject) {
            $existing = $InputObject
        }
        else {
            try {
                $profiles = Invoke-JIMApi -Endpoint "/api/v1/synchronisation/connected-systems/$csId/run-profiles"
                $existing = $profiles | Where-Object { $_.id -eq $profileId } | Select-Object -First 1
            }
            catch {
                Write-Error "Failed to retrieve Run Profile: $_"
                return
            }
        }

        if (-not $existing) {
            Write-Error "Run Profile not found: $profileId"
            return
        }

        $displayName = $existing.name ?? $profileId

        if ($Force -or $PSCmdlet.ShouldProcess($displayName, "Delete Run Profile")) {
            Write-Verbose "Deleting Run Profile: $profileId from Connected System $csId"

            try {
                Invoke-JIMApi -Endpoint "/api/v1/synchronisation/connected-systems/$csId/run-profiles/$profileId" -Method 'DELETE'

                Write-Verbose "Deleted Run Profile: $profileId"

                if ($PassThru) {
                    $existing
                }
            }
            catch {
                Write-Error "Failed to delete Run Profile: $_"
            }
        }
    }
}
