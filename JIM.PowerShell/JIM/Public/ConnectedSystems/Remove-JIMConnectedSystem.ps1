function Remove-JIMConnectedSystem {
    <#
    .SYNOPSIS
        Removes a Connected System from JIM.

    .DESCRIPTION
        Deletes a Connected System and all its related data from JIM.

        This operation may execute synchronously or be queued as a background job
        depending on system size:
        - Small systems (< 1000 objects): Deleted immediately
        - Large systems: Queued as background job
        - Systems with running sync: Queued to run after sync completes

        Use Get-JIMConnectedSystem -Id <id> -DeletionPreview first to understand
        the impact before deleting.

    .PARAMETER Id
        The unique identifier of the Connected System to delete.

    .PARAMETER InputObject
        A Connected System object to delete. Accepts pipeline input.

    .PARAMETER PassThru
        If specified, returns the deletion result object.

    .PARAMETER Force
        Suppresses confirmation prompts.

    .OUTPUTS
        If -PassThru is specified, returns the deletion result with outcome and tracking IDs.

    .EXAMPLE
        Remove-JIMConnectedSystem -Id 1

        Deletes the Connected System with ID 1 (prompts for confirmation).

    .EXAMPLE
        Remove-JIMConnectedSystem -Id 1 -Force

        Deletes the Connected System with ID 1 without prompting.

    .EXAMPLE
        Get-JIMConnectedSystem -Name "Test*" | Remove-JIMConnectedSystem -Force

        Deletes all Connected Systems with names starting with "Test".

    .EXAMPLE
        Remove-JIMConnectedSystem -Id 1 -PassThru

        Deletes the Connected System and returns the deletion result.

    .LINK
        Get-JIMConnectedSystem
        New-JIMConnectedSystem
    #>
    [CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High', DefaultParameterSetName = 'ById')]
    [OutputType([PSCustomObject])]
    param(
        [Parameter(Mandatory, ParameterSetName = 'ById', ValueFromPipelineByPropertyName)]
        [int]$Id,

        [Parameter(Mandatory, ParameterSetName = 'ByInputObject', ValueFromPipeline)]
        [PSCustomObject]$InputObject,

        [switch]$PassThru,

        [switch]$Force
    )

    process {
        # Get the ID from InputObject if provided
        $systemId = if ($PSCmdlet.ParameterSetName -eq 'ByInputObject') {
            $InputObject.id
        } else {
            $Id
        }

        # Get system name for confirmation message
        $systemName = if ($PSCmdlet.ParameterSetName -eq 'ByInputObject') {
            $InputObject.name
        } else {
            try {
                $system = Invoke-JIMApi -Endpoint "/api/v1/synchronisation/connected-systems/$systemId"
                $system.name
            } catch {
                "ID $systemId"
            }
        }

        # Confirm deletion
        if ($Force -or $PSCmdlet.ShouldProcess($systemName, "Delete Connected System")) {
            Write-Verbose "Deleting Connected System: $systemName (ID: $systemId)"

            try {
                $result = Invoke-JIMApi -Endpoint "/api/v1/synchronisation/connected-systems/$systemId" -Method 'DELETE'

                Write-Verbose "Deletion result: $($result.outcome)"

                if ($PassThru) {
                    $result
                }
            }
            catch {
                Write-Error "Failed to delete Connected System '$systemName': $_"
            }
        }
    }
}
