function Get-JIMConnectedSystemUnresolvedReferenceCount {
    <#
    .SYNOPSIS
        Gets the count of unresolved reference attribute values in a Connected System.

    .DESCRIPTION
        Returns the total number of reference attribute values across all Connected System Objects
        in the specified Connected System that could not be resolved during the last import run.

        An unresolved reference occurs when a reference attribute (e.g. a group's 'member' attribute)
        contains a value that could not be matched to another Connected System Object. This typically
        happens when:
        - The referenced object is outside the configured container scope
        - The referenced object has not been imported yet (cross-run reference resolution failure)
        - The referenced object does not exist in the source system

        A non-zero count indicates data integrity issues. Use this after an import run to verify
        that all references were successfully resolved before proceeding with synchronisation.

    .PARAMETER ConnectedSystemId
        The unique identifier of the Connected System to check.

    .OUTPUTS
        [int] The count of unresolved reference attribute values.
        Returns 0 if all references are resolved.

    .EXAMPLE
        Get-JIMConnectedSystemUnresolvedReferenceCount -ConnectedSystemId 1

        Returns the number of unresolved references in Connected System 1.

    .EXAMPLE
        $count = Get-JIMConnectedSystemUnresolvedReferenceCount -ConnectedSystemId 1
        if ($count -gt 0) {
            Write-Warning "Found $count unresolved references - check container scope configuration"
        }

        Checks for unresolved references and warns if any are found.

    .LINK
        Get-JIMConnectedSystem
        Invoke-JIMRunProfile
    #>
    [CmdletBinding()]
    [OutputType([int])]
    param(
        [Parameter(Mandatory, ValueFromPipelineByPropertyName)]
        [Alias('Id')]
        [int]$ConnectedSystemId
    )

    process {
        if (-not $script:JIMConnection) {
            Write-Error "Not connected to JIM. Use Connect-JIM first."
            return
        }

        Write-Verbose "Getting unresolved reference count for Connected System: $ConnectedSystemId"

        try {
            $result = Invoke-JIMApi -Endpoint "/api/v1/synchronisation/connected-systems/$ConnectedSystemId/staging/unresolved-references/count"
            [int]$result
        }
        catch {
            Write-Error "Failed to get unresolved reference count: $_"
        }
    }
}
