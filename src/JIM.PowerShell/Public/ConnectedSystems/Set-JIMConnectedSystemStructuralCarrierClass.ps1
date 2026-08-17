# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Set-JIMConnectedSystemStructuralCarrierClass {
    <#
    .SYNOPSIS
        Sets the Structural Carrier Class of an auxiliary Connected System Object Type.

    .DESCRIPTION
        An entry in a directory carries exactly one structural class, so an Object Type that is
        itself an auxiliary class has to name the structural class JIM writes alongside it when
        creating an entry. Until one is named, JIM can import objects of that type but cannot
        create them.

        Only an auxiliary Object Type takes a carrier, and only a structural Object Type in the same
        Connected System can be one.

    .PARAMETER ConnectedSystemId
        The unique identifier of the Connected System.

    .PARAMETER ObjectTypeId
        The unique identifier of the auxiliary Object Type.

    .PARAMETER StructuralCarrierObjectTypeId
        The structural Object Type JIM writes alongside the auxiliary class when creating an object.

    .PARAMETER Clear
        Clear the carrier, leaving the Object Type importable but not creatable.

    .PARAMETER PassThru
        If specified, returns the updated Object Type.

    .OUTPUTS
        If -PassThru is specified, returns the updated Object Type, whose
        StructuralCarrierObjectTypeId property names its carrier.

    .EXAMPLE
        Set-JIMConnectedSystemStructuralCarrierClass -ConnectedSystemId 1 -ObjectTypeId 12 -StructuralCarrierObjectTypeId 3

        Creates objects of the auxiliary Object Type as the structural Object Type 3 carrying it.

    .EXAMPLE
        Get-JIMConnectedSystemObjectType -ConnectedSystemId 1 |
            Where-Object { $_.isAuxiliary -and $_.selected -and -not $_.structuralCarrierObjectTypeId } |
            Select-Object -Property id, name

        Finds the selected auxiliary Object Types JIM cannot yet create objects for.

    .EXAMPLE
        Set-JIMConnectedSystemStructuralCarrierClass -ConnectedSystemId 1 -ObjectTypeId 12 -Clear

        Clears the carrier. JIM will keep importing objects of this type and stop creating them.

    .LINK
        Get-JIMConnectedSystemObjectType
        Set-JIMConnectedSystemAuxiliaryClass
    #>
    [CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'Medium', DefaultParameterSetName = 'Set')]
    [OutputType([PSCustomObject])]
    param(
        [Parameter(Mandatory)]
        [int]$ConnectedSystemId,

        [Parameter(Mandatory, ValueFromPipelineByPropertyName)]
        [int]$ObjectTypeId,

        [Parameter(Mandatory, ParameterSetName = 'Set')]
        [int]$StructuralCarrierObjectTypeId,

        [Parameter(Mandatory, ParameterSetName = 'Clear')]
        [switch]$Clear,

        [switch]$PassThru
    )

    process {
        # Check connection first
        if (-not $script:JIMConnection) {
            Write-Error "You are not connected to JIM. Run Connect-JIM -Url <your JIM URL> to authenticate, then try again."
            return
        }

        $carrierId = if ($Clear) { $null } else { $StructuralCarrierObjectTypeId }
        $body = @{ structuralCarrierObjectTypeId = $carrierId }

        $description = if ($null -eq $carrierId) {
            "Clear the Structural Carrier Class"
        }
        else {
            "Set the Structural Carrier Class to Object Type $carrierId"
        }

        if ($PSCmdlet.ShouldProcess("Object Type $ObjectTypeId in Connected System $ConnectedSystemId", $description)) {
            Write-Verbose "Setting the Structural Carrier Class for Object Type: $ObjectTypeId in Connected System: $ConnectedSystemId"

            try {
                $result = Invoke-JIMApi -Endpoint "/api/v1/synchronisation/connected-systems/$ConnectedSystemId/object-types/$ObjectTypeId/structural-carrier" -Method 'PUT' -Body $body

                Write-Verbose "Set the Structural Carrier Class for Object Type: $ObjectTypeId"

                if ($PassThru) {
                    $result
                }
            }
            catch {
                Write-Error "Failed to set the Structural Carrier Class: $_"
            }
        }
    }
}
