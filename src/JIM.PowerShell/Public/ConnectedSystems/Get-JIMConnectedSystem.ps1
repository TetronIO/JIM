# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Get-JIMConnectedSystem {
    <#
    .SYNOPSIS
        Gets Connected Systems.

    .DESCRIPTION
        Gets Connected Systems: every one, those whose name matches, or one by ID. With -Id, -ObjectTypes gets its
        Object Types instead, and -DeletionPreview what deleting it would affect.

        The Connected System Objects in a Connected System are read with Get-JIMConnectedSystemObject.

    .PARAMETER Id
        The ID of the Connected System to get.

    .PARAMETER Name
        Returns only the Connected Systems whose name matches.

    .PARAMETER ObjectTypes
        Returns the Connected System's Object Types, from its imported schema.

    .PARAMETER DeletionPreview
        Returns what deleting the Connected System would affect.

    .OUTPUTS
        The Connected Systems, or with -Id the one Connected System, its Object Types or its deletion preview. A
        Connected System read by ID carries ConfigurationDrift: whether its configuration has changed in a way that
        needs a Full Synchronisation, in HasPendingChanges, IsDeterminable, NeverFullySynchronised,
        TrackingDisabled, LastFullSynchronisation, MostRecentChange, ChangeCount and HighestChangeClass.
        HasPendingChanges is also $false when JIM cannot tell, so test IsDeterminable first.

        ConfigurationDrift also carries AwaitingSynchronisation: the Object Types whose objects the next
        synchronisation, Delta or Full, will all process because of Synchronisation Rule or Object Matching Rule
        changes saved since it last did so, each with its ChangeCount, ObjectCount and the Changes behind it. Those
        changes are not counted in ChangeCount and do not set HasPendingChanges, as an ordinary synchronisation
        applies them.

    .EXAMPLE
        Get-JIMConnectedSystem

        Gets all Connected Systems.

    .EXAMPLE
        Get-JIMConnectedSystem -Id 1

        Gets the Connected System with ID 1.

    .EXAMPLE
        Get-JIMConnectedSystem -Name "HR*"

        Gets all Connected Systems with names starting with "HR".

    .EXAMPLE
        Get-JIMConnectedSystem -Id 1 -ObjectTypes

        Gets the object types defined in the Connected System's schema.

    .EXAMPLE
        (Get-JIMConnectedSystem -Id 1).ConfigurationDrift

        Gets whether the Connected System's configuration has changed since its last Full Synchronisation.

    .EXAMPLE
        Get-JIMConnectedSystem -Id 1 -DeletionPreview

        Gets a preview of what would be affected by deleting the Connected System.

    .LINK
        Get-JIMConnectedSystemObject
        New-JIMConnectedSystem
        Set-JIMConnectedSystem
        Remove-JIMConnectedSystem
    #>
    [CmdletBinding(DefaultParameterSetName = 'List')]
    [OutputType('JIM.ConnectedSystemHeader', ParameterSetName = 'List')]
    [OutputType('JIM.ConnectedSystemDetailDto', ParameterSetName = 'ById')]
    [OutputType('JIM.ConnectedSystemObjectTypeDto', ParameterSetName = 'ObjectTypes')]
    [OutputType('JIM.ConnectedSystemDeletionPreview', ParameterSetName = 'DeletionPreview')]
    param(
        [Parameter(Mandatory, ParameterSetName = 'ById', ValueFromPipelineByPropertyName)]
        [Parameter(Mandatory, ParameterSetName = 'ObjectTypes', ValueFromPipelineByPropertyName)]
        [Parameter(Mandatory, ParameterSetName = 'DeletionPreview', ValueFromPipelineByPropertyName)]
        [int]$Id,

        [Parameter(ParameterSetName = 'List')]
        [SupportsWildcards()]
        [string]$Name,

        [Parameter(Mandatory, ParameterSetName = 'ObjectTypes')]
        [switch]$ObjectTypes,

        [Parameter(Mandatory, ParameterSetName = 'DeletionPreview')]
        [switch]$DeletionPreview
    )

    process {
        # Check connection first
        if (-not $script:JIMConnection) {
            Write-Error "You are not connected to JIM. Run Connect-JIM -Url <your JIM URL> to authenticate, then try again."
            return
        }

        switch ($PSCmdlet.ParameterSetName) {
            'ById' {
                Write-Verbose "Getting Connected System with ID: $Id"
                $result = Invoke-JIMApi -Endpoint "/api/v1/synchronisation/connected-systems/$Id"
                $result
            }

            'ObjectTypes' {
                Write-Verbose "Getting object types for Connected System ID: $Id"
                $result = Invoke-JIMApi -Endpoint "/api/v1/synchronisation/connected-systems/$Id/object-types"
                $result
            }

            'DeletionPreview' {
                Write-Verbose "Getting deletion preview for Connected System ID: $Id"
                $result = Invoke-JIMApi -Endpoint "/api/v1/synchronisation/connected-systems/$Id/deletion-preview"
                $result
            }

            'List' {
                Write-Verbose "Getting all Connected Systems"
                # The list endpoint is paginated, so read every page; one read would stop at the server's default page size.
                $systems = Get-JIMPagedItems -Endpoint "/api/v1/synchronisation/connected-systems"

                # Filter by name if specified
                if ($Name) {
                    Write-Verbose "Filtering by name pattern: $Name"
                    $systems = $systems | Where-Object { $_.name -like $Name }
                }

                # Output each system individually for pipeline support
                foreach ($system in $systems) {
                    $system
                }
            }
        }
    }
}
