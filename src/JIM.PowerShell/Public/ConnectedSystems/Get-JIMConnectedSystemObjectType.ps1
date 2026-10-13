# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Get-JIMConnectedSystemObjectType {
    <#
    .SYNOPSIS
        Gets a Connected System's Object Types.

    .DESCRIPTION
        Gets the Object Types in a Connected System's imported schema (such as user and group), with their
        attributes.

        Object Types the Connected System marks as internal, such as a directory's own configuration and operational
        classes, are left out unless -IncludeInternal is given, as the portal does. A selected Object Type is always
        returned.

    .PARAMETER ConnectedSystemId
        The ID of the Connected System.

    .PARAMETER IncludeInternal
        Also returns the Object Types the Connected System marks as internal.

    .OUTPUTS
        The Object Types, each with its attributes. An attribute whose Writability is ReadOnly can be imported but
        not exported; one that is WritableOnCreate is exported only when JIM creates the object, because changing it
        would break the link to the object (a table's primary key, a directory entry's relative distinguished name).

    .EXAMPLE
        Get-JIMConnectedSystemObjectType -ConnectedSystemId 1

        Gets the object types for Connected System 1 that an administrator would manage.

    .EXAMPLE
        Get-JIMConnectedSystemObjectType -ConnectedSystemId 1 |
            ForEach-Object { $_.attributes } |
            Where-Object { $_.writability -eq 'WritableOnCreate' } |
            Select-Object name, type

        Lists the attributes JIM may only set when it creates the object.

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
    [OutputType('JIM.ConnectedSystemObjectTypeDto')]
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
