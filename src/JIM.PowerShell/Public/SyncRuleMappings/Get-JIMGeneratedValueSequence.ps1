# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Get-JIMGeneratedValueSequence {
    <#
    .SYNOPSIS
        Gets a generated Sequence mapping's counter state (Unique Value Generation, #242).

    .DESCRIPTION
        Returns the next number a generated Sequence mapping would issue, and how many it has issued so
        far. Read-only: nothing is allocated or reserved by calling this. Useful before raising a mapping's
        Sequence Start with Set-JIMSyncRuleMapping, to see what the save would actually move the counter to.

        Only meaningful for a generated mapping whose token kind is Sequence; every other mapping (including
        an ordinary attribute or Expression mapping, or a generated OnlyIfTaken/Random mapping) returns a
        "not found" error.

    .PARAMETER SyncRuleId
        The unique identifier of the Synchronisation Rule the mapping belongs to. Also accepts pipeline
        input via the Id property. Alias: Id.

    .PARAMETER MappingId
        The unique identifier of the mapping.

    .OUTPUTS
        One PSCustomObject describing the counter:

        | Property                | Description                                                          |
        |--------------------------|------------------------------------------------------------------------|
        | AttributeName            | The target attribute the counter belongs to                          |
        | NextNumber               | The next number this flow would issue                                |
        | NextNumberFormatted      | That number formatted with the mapping's Fixed Width, if any         |
        | NextNumberWidthExceeded  | True when the next number no longer fits the configured Fixed Width  |
        | AssignedCount            | How many numbers this flow has issued so far                         |
        | IsSeeded                 | Whether the counter has been seeded from existing values yet         |

    .EXAMPLE
        Get-JIMGeneratedValueSequence -SyncRuleId 1 -MappingId 12

        Shows Employee Number mapping 12's next number and how many it has issued.

    .EXAMPLE
        Get-JIMSyncRule -Id 1 | Get-JIMGeneratedValueSequence -MappingId 12

        Pipes a Synchronisation Rule straight in.

    .LINK
        Set-JIMSyncRuleMapping
        Restart-JIMGeneratedValues
        Get-JIMGeneratedValue
        Get-JIMSyncRuleMapping
    #>
    [CmdletBinding()]
    [OutputType([PSCustomObject])]
    param(
        [Parameter(Mandatory, ValueFromPipelineByPropertyName)]
        [Alias('Id')]
        [int]$SyncRuleId,

        [Parameter(Mandatory)]
        [int]$MappingId
    )

    process {
        if (-not $script:JIMConnection) {
            Write-Error "You are not connected to JIM. Run Connect-JIM -Url <your JIM URL> to authenticate, then try again."
            return
        }

        Write-Verbose "Getting Sequence counter state for mapping $MappingId of Synchronisation Rule $SyncRuleId"
        Invoke-JIMApi -Endpoint "/api/v1/synchronisation/sync-rules/$SyncRuleId/mappings/$MappingId/sequence"
    }
}
