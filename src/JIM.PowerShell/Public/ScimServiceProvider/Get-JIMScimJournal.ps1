# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Get-JIMScimJournal {
    <#
    .SYNOPSIS
        Gets the SCIM requests an identity provider made to a resource in a Connected System's SCIM endpoint.

    .DESCRIPTION
        Returns a SCIM resource's accepted writes, oldest first: what each did, the attributes it changed or ignored
        (by name, never by value), and the key and address that made it. With -ChangeId, returns the requests behind
        one imported change to a Connected System Object instead: those the import that made the change read.

    .PARAMETER ConnectedSystemId
        The ID of the Connected System, which must use the SCIM 2.0 Service Provider Connector.

    .PARAMETER ResourceId
        The SCIM resource, which is the Connected System Object's external id.

    .PARAMETER AfterSequence
        Only writes after this change number.

    .PARAMETER UpToSequence
        Only writes up to and including this change number.

    .PARAMETER ChangeId
        The ID of a change in a Connected System Object's change history, to get the requests behind it.

    .OUTPUTS
        Each journal entry, oldest first; with -ChangeId, the requests behind the change.

    .EXAMPLE
        Get-JIMScimJournal -ConnectedSystemId 12 -ResourceId 2819c223-7f76-453a-919d-413861904646 | Select-Object Occurred, Operation, ChangedAttributes, ApiKeyName

        Shows what each request did to the resource, and which key made it.

    .EXAMPLE
        (Get-JIMScimJournal -ConnectedSystemId 12 -ChangeId $changeId).Entries

        Shows the requests behind one imported change.

    .LINK
        Get-JIMScimStoreSummary
        Get-JIMScimKey
    #>
    [CmdletBinding(DefaultParameterSetName = 'ByResource')]
    [OutputType('JIM.ScimJournalEntryDto', ParameterSetName = 'ByResource')]
    [OutputType('JIM.ScimRequestsBehindChangeDto', ParameterSetName = 'ByChange')]
    param(
        [Parameter(Mandatory)]
        [int]$ConnectedSystemId,

        [Parameter(Mandatory, ParameterSetName = 'ByResource')]
        [Guid]$ResourceId,

        [Parameter(ParameterSetName = 'ByResource')]
        [long]$AfterSequence,

        [Parameter(ParameterSetName = 'ByResource')]
        [long]$UpToSequence,

        [Parameter(Mandatory, ParameterSetName = 'ByChange')]
        [Guid]$ChangeId
    )

    process {
        if (-not $script:JIMConnection) {
            Write-Error "You are not connected to JIM. Run Connect-JIM -Url <your JIM URL> to authenticate, then try again."
            return
        }

        $base = "/api/v1/synchronisation/connected-systems/$ConnectedSystemId/scim/journal"
        if ($PSCmdlet.ParameterSetName -eq 'ByChange') {
            Write-Verbose "Getting the SCIM requests behind change $ChangeId"
            Invoke-JIMApi -Endpoint "$base/changes/$ChangeId"
            return
        }

        $query = "resourceId=$ResourceId"
        if ($PSBoundParameters.ContainsKey('AfterSequence')) {
            $query += "&afterSequence=$AfterSequence"
        }
        if ($PSBoundParameters.ContainsKey('UpToSequence')) {
            $query += "&upToSequence=$UpToSequence"
        }

        Write-Verbose "Getting the SCIM journal of resource $ResourceId on Connected System $ConnectedSystemId"
        $entries = Invoke-JIMApi -Endpoint "${base}?$query"
        foreach ($entry in $entries) {
            $entry
        }
    }
}
