# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Resolve-JIMConnectedObjectType {
    <#
    .SYNOPSIS
        Resolves a Connected System Object Type, named by its Connected System and its own name, to its ID.

    .DESCRIPTION
        Internal helper for the scoping criterion cmdlets' connection criteria. Throws when the Connected System or the
        Object Type cannot be found, naming both.

    .PARAMETER ConnectedSystemName
        The name of the Connected System holding the Object Type. Must be an exact match.

    .PARAMETER Name
        The name of the Object Type, for example user. Must be an exact match.

    .OUTPUTS
        The ID of the Object Type.
    #>
    [CmdletBinding()]
    [OutputType([int])]
    param(
        [Parameter(Mandatory)]
        [string]$ConnectedSystemName,

        [Parameter(Mandatory)]
        [string]$Name
    )

    $connectedSystem = Resolve-JIMConnectedSystem -Name $ConnectedSystemName
    $objectTypes = Invoke-JIMApi -Endpoint "/api/v1/synchronisation/connected-systems/$($connectedSystem.id)/object-types"
    $objectType = @($objectTypes | Where-Object { $_.name -eq $Name }) | Select-Object -First 1
    if (-not $objectType) {
        throw "Object Type '$Name' not found on Connected System '$ConnectedSystemName'."
    }

    Write-Verbose "Resolved '$Name' in '$ConnectedSystemName' to Object Type ID $($objectType.id)"
    [int]$objectType.id
}
