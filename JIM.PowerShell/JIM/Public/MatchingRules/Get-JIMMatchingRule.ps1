function Get-JIMMatchingRule {
    <#
    .SYNOPSIS
        Gets Object Matching Rules from JIM.

    .DESCRIPTION
        Retrieves Object Matching Rules for a Connected System Object Type from JIM.
        Object Matching Rules define how objects from a Connected System are correlated
        with Metaverse Objects during import (join) and export (provisioning) operations.

    .PARAMETER ConnectedSystemId
        The unique identifier of the Connected System.

    .PARAMETER ObjectTypeId
        The unique identifier of the Object Type to get Matching Rules for.

    .PARAMETER Id
        The unique identifier of a specific Matching Rule to retrieve.

    .OUTPUTS
        PSCustomObject representing Matching Rule(s).

    .EXAMPLE
        Get-JIMMatchingRule -ConnectedSystemId 1 -ObjectTypeId 10

        Gets all Matching Rules for Object Type ID 10 in Connected System ID 1.

    .EXAMPLE
        Get-JIMMatchingRule -ConnectedSystemId 1 -Id 5

        Gets the specific Matching Rule with ID 5 from Connected System ID 1.

    .LINK
        New-JIMMatchingRule
        Set-JIMMatchingRule
        Remove-JIMMatchingRule
    #>
    [CmdletBinding(DefaultParameterSetName = 'ByObjectType')]
    [OutputType([PSCustomObject])]
    param(
        [Parameter(Mandatory, ValueFromPipelineByPropertyName)]
        [int]$ConnectedSystemId,

        [Parameter(Mandatory, ParameterSetName = 'ByObjectType', ValueFromPipelineByPropertyName)]
        [int]$ObjectTypeId,

        [Parameter(Mandatory, ParameterSetName = 'ById')]
        [int]$Id
    )

    process {
        # Check connection first
        if (-not $script:JIMConnection) {
            Write-Error "Not connected to JIM. Use Connect-JIM first."
            return
        }

        if ($PSCmdlet.ParameterSetName -eq 'ById') {
            Write-Verbose "Getting Matching Rule ID: $Id for Connected System ID: $ConnectedSystemId"
            $result = Invoke-JIMApi -Endpoint "/api/v1/synchronisation/connected-systems/$ConnectedSystemId/matching-rules/$Id"
            $result | Add-Member -NotePropertyName 'ConnectedSystemId' -NotePropertyValue $ConnectedSystemId -PassThru -Force
        }
        else {
            Write-Verbose "Getting Matching Rules for Connected System ID: $ConnectedSystemId, Object Type ID: $ObjectTypeId"
            $result = Invoke-JIMApi -Endpoint "/api/v1/synchronisation/connected-systems/$ConnectedSystemId/object-types/$ObjectTypeId/matching-rules"

            # Output each rule individually for pipeline support
            foreach ($rule in $result) {
                $rule | Add-Member -NotePropertyName 'ConnectedSystemId' -NotePropertyValue $ConnectedSystemId -PassThru -Force
            }
        }
    }
}
