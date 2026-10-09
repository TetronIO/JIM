# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Get-JIMActivityExecutionItem {
    <#
    .SYNOPSIS
        Gets one Run Profile Execution Item, including its error message.

    .DESCRIPTION
        Retrieves one execution item of a Run Profile Activity: what happened to one object and, when it failed,
        why. The list from Get-JIMActivity -ExecutionItems names each failed item's error type; this returns the
        error message and stack trace too, with the snapshots taken when the item was recorded (the object's
        external ID, display name and type) and the ids of the Connected System Object, Metaverse Object or
        Pending Export it is about.

        Item ids come from Get-JIMActivity -ExecutionItems, whose rows can be piped straight in.

    .PARAMETER Id
        The unique identifier (GUID) of the execution item.

    .OUTPUTS
        PSCustomObject with Id, ActivityId, ObjectChangeType, NoChangeReason, ConnectedSystemObjectId,
        MetaverseObjectId, PendingExportId, ExternalIdSnapshot, DisplayNameSnapshot, ObjectTypeSnapshot, ErrorType,
        ErrorMessage, ErrorStackTrace, AttributeFlowCount and OutcomeSummary.

    .EXAMPLE
        Get-JIMActivityExecutionItem -Id "b2c3d4e5-f6a7-8901-bcde-f12345678901"

        Gets one execution item, with its error message if it failed.

    .EXAMPLE
        Get-JIMActivity -Id "a1b2c3d4-e5f6-7890-abcd-ef1234567890" -ExecutionItems |
            Where-Object { $_.ErrorType -and $_.ErrorType -ne 'NotSet' } |
            Get-JIMActivityExecutionItem |
            Select-Object DisplayNameSnapshot, ErrorType, ErrorMessage

        Lists why each object in a Run Profile execution failed.

    .LINK
        Get-JIMActivity
        Get-JIMActivityStats
    #>
    [CmdletBinding()]
    [OutputType([PSCustomObject])]
    param(
        # No ActivityId alias: an item's id is not its Activity's, and an object carrying both must bind the item's.
        [Parameter(Mandatory, ValueFromPipelineByPropertyName)]
        [guid]$Id
    )

    process {
        if (-not $script:JIMConnection) {
            Write-Error "You are not connected to JIM. Run Connect-JIM -Url <your JIM URL> to authenticate, then try again."
            return
        }

        Write-Verbose "Getting execution item with ID: $Id"
        Invoke-JIMApi -Endpoint "/api/v1/activities/items/$Id"
    }
}
