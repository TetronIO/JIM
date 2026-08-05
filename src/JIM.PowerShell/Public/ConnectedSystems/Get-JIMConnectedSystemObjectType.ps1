# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Get-JIMConnectedSystemObjectType {
    <#
    .SYNOPSIS
        Gets object types for a Connected System in JIM.

    .DESCRIPTION
        Retrieves object types from a Connected System's discovered schema. Object types
        represent categories of objects in the external identity store (e.g. user, group).
        Each object type contains attributes that can be selected for synchronisation.

        Object types the Connected System classified as internal (a directory's own
        configuration and operational classes, which an administrator would never manage)
        are omitted by default, matching what the portal's schema screen shows. Use
        -IncludeInternal to return them as well. An object type that is already Selected is
        always returned, whatever its classification.

    .PARAMETER ConnectedSystemId
        The unique identifier of the Connected System.

    .PARAMETER IncludeInternal
        Also return object types the Connected System classified as internal.

    .OUTPUTS
        PSCustomObject representing the object types and their attributes.

        Each object carries a Tags collection of Key/Value classification pairs reported by
        the Connected System (for example class-kind = structural, visibility = internal),
        and an IsInternal boolean derived from them.

    .EXAMPLE
        Get-JIMConnectedSystemObjectType -ConnectedSystemId 1

        Gets the object types for Connected System 1 that an administrator would manage.

    .EXAMPLE
        Get-JIMConnectedSystemObjectType -ConnectedSystemId 1 -IncludeInternal

        Gets every object type for Connected System 1, including the directory's own
        configuration and operational classes.

    .EXAMPLE
        Get-JIMConnectedSystemObjectType -ConnectedSystemId 1 -IncludeInternal |
            Where-Object { $_.IsInternal } |
            Select-Object name

        Lists only the object types the Connected System uses internally.

    .EXAMPLE
        Get-JIMConnectedSystem -Name "Corporate*" | ForEach-Object {
            Get-JIMConnectedSystemObjectType -ConnectedSystemId $_.id
        }

        Gets object types for all Connected Systems matching "Corporate*".

    .LINK
        Set-JIMConnectedSystemObjectType
        Set-JIMConnectedSystemAttribute
        Get-JIMConnectedSystem
    #>
    [CmdletBinding()]
    [OutputType([PSCustomObject])]
    param(
        [Parameter(Mandatory, ValueFromPipelineByPropertyName)]
        [Alias('Id')]
        [int]$ConnectedSystemId,

        [Parameter()]
        [switch]$IncludeInternal
    )

    process {
        # Check connection first
        if (-not $script:JIMConnection) {
            Write-Error "You are not connected to JIM. Run Connect-JIM -Url <your JIM URL> to authenticate, then try again."
            return
        }

        Write-Verbose "Getting object types for Connected System: $ConnectedSystemId"

        try {
            $result = Invoke-JIMApi -Endpoint "/api/v1/synchronisation/connected-systems/$ConnectedSystemId/object-types"

            # The API returns everything it discovered; the default view is applied here so that the REST surface
            # stays complete and only the cmdlet's own convenience default hides anything. A Selected object type is
            # never withheld, whatever its classification.
            foreach ($objectType in $result) {
                if ($IncludeInternal -or -not $objectType.isInternal -or $objectType.selected) {
                    $objectType
                }
            }
        }
        catch {
            Write-Error "Failed to get object types: $_"
        }
    }
}
