# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Get-JIMConnectedSystemAuxiliaryClass {
    <#
    .SYNOPSIS
        Lists the auxiliary classes that can be merged into a Connected System Object Type.

    .DESCRIPTION
        Returns every auxiliary class the Connected System's schema defines, marked with whether it
        is merged into this Object Type, how many attributes merging would contribute, and why JIM
        thinks it relevant: a DIT Content Rule permitting it, and how many entries the last
        discovery run saw carrying it.

        Suggestions are never configuration. A class is listed whether or not anything suggests it,
        and only what is merged is persisted, so a schema refresh cannot silently change what an
        Object Type carries.

        Nothing is returned for an Object Type whose Connected System does not let JIM compose class
        membership (Active Directory resolves its own auxiliary classes into each structural class),
        or for an Object Type that is itself an auxiliary class, which cannot carry another.

    .PARAMETER ConnectedSystemId
        The unique identifier of the Connected System.

    .PARAMETER ObjectTypeId
        The unique identifier of the Object Type to list auxiliary classes for.

    .PARAMETER MergedOnly
        Return only the classes currently merged into the Object Type.

    .PARAMETER SuggestedOnly
        Return only the classes something suggests: a DIT Content Rule permits them, or a discovery
        run observed them in use.

    .OUTPUTS
        PSCustomObject per auxiliary class, ordered merged first, then suggested, then the rest by
        name. Properties: ObjectTypeId, Name, Merged, ContributedAttributeCount,
        PermittedByTheConnectedSystem, EntriesObservedOn, IsSuggested.

        EntriesObservedOn is $null when no discovery run has observed the class, which is different
        from 0: 0 means a run read entries and saw none carrying it.

    .EXAMPLE
        Get-JIMConnectedSystemAuxiliaryClass -ConnectedSystemId 1 -ObjectTypeId 5

        Lists every auxiliary class on offer for the Object Type.

    .EXAMPLE
        Get-JIMConnectedSystemAuxiliaryClass -ConnectedSystemId 1 -ObjectTypeId 5 -SuggestedOnly |
            Format-Table Name, ContributedAttributeCount, EntriesObservedOn

        Shows only the classes JIM has a reason to suggest, with what each would contribute.

    .EXAMPLE
        Get-JIMConnectedSystemAuxiliaryClass -ConnectedSystemId 1 -ObjectTypeId 5 -MergedOnly |
            Select-Object -ExpandProperty Name

        Names the auxiliary classes the Object Type currently carries.

    .LINK
        Set-JIMConnectedSystemAuxiliaryClass
        Start-JIMConnectedSystemAuxiliaryClassDiscovery
        Get-JIMConnectedSystemObjectType
    #>
    [CmdletBinding()]
    [OutputType([PSCustomObject])]
    param(
        [Parameter(Mandatory)]
        [int]$ConnectedSystemId,

        [Parameter(Mandatory, ValueFromPipelineByPropertyName)]
        [int]$ObjectTypeId,

        [switch]$MergedOnly,

        [switch]$SuggestedOnly
    )

    process {
        # Check connection first
        if (-not $script:JIMConnection) {
            Write-Error "You are not connected to JIM. Run Connect-JIM -Url <your JIM URL> to authenticate, then try again."
            return
        }

        Write-Verbose "Getting auxiliary classes for Object Type: $ObjectTypeId in Connected System: $ConnectedSystemId"

        try {
            $result = Invoke-JIMApi -Endpoint "/api/v1/synchronisation/connected-systems/$ConnectedSystemId/object-types/$ObjectTypeId/auxiliary-classes"

            foreach ($auxiliaryClass in $result) {
                if ($MergedOnly -and -not $auxiliaryClass.merged) { continue }
                if ($SuggestedOnly -and -not $auxiliaryClass.isSuggested) { continue }
                $auxiliaryClass
            }
        }
        catch {
            Write-Error "Failed to get auxiliary classes: $_"
        }
    }
}
