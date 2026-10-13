# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Get-JIMConnectedSystemUnresolvedReferenceSummary {
    <#
    .SYNOPSIS
        Gets a summary of every unresolved reference across a Connected System in JIM, by likely cause and next step.

    .DESCRIPTION
        Summarises the reference values across a Connected System's Connector Space that could not be linked to
        another Connected System Object (for example group members outside Container Scope): how many there are
        and on how many objects, how they split by likely cause, and every distinct next step with how many
        values and objects it addresses. This is what the portal shows as "Where they point" when the Connector
        Space is filtered to unresolved references.

        Likely causes are judged against the Connected System's current Container Scope, so they describe the
        Connector Space as it stands now: widening the scope changes the answer the next time you ask.

        To list the objects themselves, use Get-JIMConnectedSystemObject -HasUnresolvedReference; to see one
        object's references, use Get-JIMConnectedSystemObjectUnresolvedReference.

    .PARAMETER ConnectedSystemId
        The ID of the Connected System.

    .OUTPUTS
        How many references are unresolved and on how many objects, how they split by likely cause, and every
        distinct next step with how many it would address.

    .EXAMPLE
        Get-JIMConnectedSystemUnresolvedReferenceSummary -ConnectedSystemId 1

        Gets the summary of Connected System 1's unresolved references.

    .EXAMPLE
        (Get-JIMConnectedSystemUnresolvedReferenceSummary -ConnectedSystemId 1).Fixes |
            Format-Table FixLocation, Count, ObjectCount, Finding -Wrap

        Lists every next step, with the values and objects each addresses and where the fix lies.

    .EXAMPLE
        Get-JIMConnectedSystem | ForEach-Object {
            $summary = $_ | Get-JIMConnectedSystemUnresolvedReferenceSummary
            [PSCustomObject]@{ Name = $_.Name; References = $summary.TotalCount; Objects = $summary.ObjectCount }
        }

        Shows how many unresolved references each Connected System holds, and on how many objects.

    .LINK
        Get-JIMConnectedSystemObject
        Get-JIMConnectedSystemObjectUnresolvedReference
        Get-JIMConnectedSystemUnresolvedReferenceCount
    #>
    [CmdletBinding()]
    [OutputType('JIM.ConnectedSystemUnresolvedReferenceSummary')]
    param(
        # Aliased so a Connected System can be piped in; the summary is the Connected System's own, so -Id names it.
        [Parameter(Mandatory, ValueFromPipelineByPropertyName)]
        [Alias('Id')]
        [int]$ConnectedSystemId
    )

    process {
        if (-not $script:JIMConnection) {
            Write-Error "You are not connected to JIM. Run Connect-JIM -Url <your JIM URL> to authenticate, then try again."
            return
        }

        Write-Verbose "Getting the unresolved reference summary for Connected System $ConnectedSystemId"
        Invoke-JIMApi -Endpoint "/api/v1/synchronisation/connected-systems/$ConnectedSystemId/connector-space/unresolved-references/summary"
    }
}
