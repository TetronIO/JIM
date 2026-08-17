# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Set-JIMConnectedSystemAuxiliaryClass {
    <#
    .SYNOPSIS
        Sets which auxiliary classes a Connected System Object Type carries.

    .DESCRIPTION
        Replaces the whole set of merged auxiliary classes: classes named here that were not merged
        are merged, and classes merged that are not named here are withdrawn.

        Merged classes contribute their attributes to the Object Type at the next schema refresh,
        and JIM writes the class onto an entry when a flow first gives that entry one of the class's
        attributes. It never stamps the class onto entries that lack its attributes.

        Because this replaces rather than adds, read the current set with
        Get-JIMConnectedSystemAuxiliaryClass -MergedOnly first if you mean to add one class to
        several, or use -Clear to withdraw every selection.

    .PARAMETER ConnectedSystemId
        The unique identifier of the Connected System.

    .PARAMETER ObjectTypeId
        The unique identifier of the Object Type the classes are merged into.

    .PARAMETER AuxiliaryClassObjectTypeId
        The auxiliary classes the Object Type should carry, by their own Object Type ids. Each must
        be an auxiliary class in the same Connected System.

    .PARAMETER Clear
        Withdraw every auxiliary class selection from the Object Type.

    .PARAMETER PassThru
        If specified, returns the updated Object Type.

    .OUTPUTS
        If -PassThru is specified, returns the updated Object Type, whose
        MergedAuxiliaryClassObjectTypeIds property lists what it now carries.

    .EXAMPLE
        Set-JIMConnectedSystemAuxiliaryClass -ConnectedSystemId 1 -ObjectTypeId 5 -AuxiliaryClassObjectTypeId 12

        Merges the one auxiliary class into the Object Type, withdrawing any others it carried.

    .EXAMPLE
        $merged = (Get-JIMConnectedSystemAuxiliaryClass -ConnectedSystemId 1 -ObjectTypeId 5 -MergedOnly).ObjectTypeId
        Set-JIMConnectedSystemAuxiliaryClass -ConnectedSystemId 1 -ObjectTypeId 5 -AuxiliaryClassObjectTypeId ($merged + 12)

        Adds one auxiliary class to whatever the Object Type already carried.

    .EXAMPLE
        Set-JIMConnectedSystemAuxiliaryClass -ConnectedSystemId 1 -ObjectTypeId 5 -Clear

        Withdraws every auxiliary class from the Object Type. Their attributes leave its schema at
        the next refresh, so inspect what is merged, and what flows those attributes, first.

    .LINK
        Get-JIMConnectedSystemAuxiliaryClass
        Set-JIMConnectedSystemStructuralCarrierClass
        Import-JIMConnectedSystemSchema
    #>
    [CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'Medium', DefaultParameterSetName = 'Set')]
    [OutputType([PSCustomObject])]
    param(
        [Parameter(Mandatory)]
        [int]$ConnectedSystemId,

        [Parameter(Mandatory, ValueFromPipelineByPropertyName)]
        [int]$ObjectTypeId,

        [Parameter(Mandatory, ParameterSetName = 'Set')]
        [int[]]$AuxiliaryClassObjectTypeId,

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

        # An empty array is how the API expresses "carry nothing", so -Clear and an explicit empty
        # set reach the same request rather than being two behaviours.
        $objectTypeIds = if ($Clear) { @() } else { @($AuxiliaryClassObjectTypeId) }
        $body = @{ objectTypeIds = $objectTypeIds }

        $description = if ($objectTypeIds.Count -eq 0) {
            "Withdraw every auxiliary class"
        }
        else {
            "Set auxiliary classes to $($objectTypeIds -join ', ')"
        }

        if ($PSCmdlet.ShouldProcess("Object Type $ObjectTypeId in Connected System $ConnectedSystemId", $description)) {
            Write-Verbose "Setting auxiliary classes for Object Type: $ObjectTypeId in Connected System: $ConnectedSystemId"

            try {
                $result = Invoke-JIMApi -Endpoint "/api/v1/synchronisation/connected-systems/$ConnectedSystemId/object-types/$ObjectTypeId/auxiliary-classes" -Method 'PUT' -Body $body

                Write-Verbose "Set auxiliary classes for Object Type: $ObjectTypeId"

                if ($PassThru) {
                    $result
                }
            }
            catch {
                Write-Error "Failed to set auxiliary classes: $_"
            }
        }
    }
}
