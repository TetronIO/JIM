function Clear-JIMConnectedSystem {
    <#
    .SYNOPSIS
        Clears all objects from a Connected System's connector space.

    .DESCRIPTION
        Removes all Connected System Objects (CSOs) and their related data from a
        Connected System's connector space. This is typically used before re-importing
        data from the source system.

        The operation deletes CSOs, attribute values, pending exports, and deferred
        references. Metaverse Objects are not deleted — only the link between the CSO
        and MVO is severed.

        By default, change history is also deleted (recommended for re-import scenarios).
        Use -KeepChangeHistory to preserve the audit trail.

    .PARAMETER Id
        The unique identifier of the Connected System to clear.

    .PARAMETER InputObject
        A Connected System object to clear. Accepts pipeline input.

    .PARAMETER KeepChangeHistory
        If specified, preserves change history records. The CSO foreign key on change
        records is nulled rather than the records being deleted.

        By default (without this switch), change history is deleted along with the CSOs.

    .PARAMETER Force
        Suppresses confirmation prompts.

    .OUTPUTS
        None.

    .EXAMPLE
        Clear-JIMConnectedSystem -Id 1

        Clears all objects from the Connected System with ID 1, including change history
        (prompts for confirmation).

    .EXAMPLE
        Clear-JIMConnectedSystem -Id 1 -Force

        Clears all objects from the Connected System with ID 1 without prompting.

    .EXAMPLE
        Clear-JIMConnectedSystem -Id 1 -KeepChangeHistory

        Clears all objects but preserves the change history audit trail.

    .EXAMPLE
        Get-JIMConnectedSystem -Name "HR*" | Clear-JIMConnectedSystem -Force

        Clears all objects from all Connected Systems with names starting with "HR".

    .LINK
        Get-JIMConnectedSystem
        Remove-JIMConnectedSystem
    #>
    [CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High', DefaultParameterSetName = 'ById')]
    param(
        [Parameter(Mandatory, ParameterSetName = 'ById', ValueFromPipelineByPropertyName)]
        [int]$Id,

        [Parameter(Mandatory, ParameterSetName = 'ByInputObject', ValueFromPipeline)]
        [PSCustomObject]$InputObject,

        [switch]$KeepChangeHistory,

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

        # Confirm clearing
        if ($Force -or $PSCmdlet.ShouldProcess($systemName, "Clear all objects from Connected System")) {
            Write-Verbose "Clearing connector space for Connected System: $systemName (ID: $systemId)"

            try {
                $deleteChangeHistory = if ($KeepChangeHistory) { 'false' } else { 'true' }
                Invoke-JIMApi -Endpoint "/api/v1/synchronisation/connected-systems/$systemId/clear?deleteChangeHistory=$deleteChangeHistory" -Method 'POST'

                Write-Verbose "Connector space cleared for Connected System: $systemName (ID: $systemId)"
            }
            catch {
                Write-Error "Failed to clear Connected System '$systemName': $_"
            }
        }
    }
}
