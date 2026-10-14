# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Get-JIMGeneratedValueSequence {
    <#
    .SYNOPSIS
        Gets a generated Sequence mapping's counter: the next number it would issue.

    .DESCRIPTION
        Gets the counter behind a generated mapping whose token is a Sequence: the next number it would issue,
        formatted as it would be, and how many it has issued. Nothing is allocated or reserved. Use it before
        raising the mapping's Sequence Start with Set-JIMSyncRuleMapping, to see what the change would move the
        counter to. Any other mapping, including a generated one whose token is OnlyIfTaken or Random, is not found.

    .PARAMETER SyncRuleId
        The ID of the Synchronisation Rule the mapping belongs to.

    .PARAMETER MappingId
        The ID of the generated mapping.

    .OUTPUTS
        The counter's state.

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
    [OutputType('JIM.GeneratedValueSequenceStateDto')]
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
