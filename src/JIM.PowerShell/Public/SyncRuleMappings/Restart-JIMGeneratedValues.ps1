# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Restart-JIMGeneratedValues {
    <#
    .SYNOPSIS
        "Start again": moves a generated Sequence mapping's counter back to its configured Sequence Start
        (Unique Value Generation, #242).

    .DESCRIPTION
        In this release, this command does exactly one thing: for a generated Sequence mapping, it moves
        the target attribute's counter back to the mapping's configured Sequence Start (the move can go
        either direction; "back" is the common case, but a lower configured start is honoured too).

        It changes NOTHING else. No existing generated value on any object is changed. Nothing is
        exported, and no synchronisation runs as a result of this command. For every other token kind
        (OnlyIfTaken, Random) this is a documented no-op that still succeeds.

        There are no retired values to bring back in this release: the retired values register does not
        exist yet (it ships in release 2), so RetiredValuesForgotten on the result is always 0. A number
        already issued by this flow is simply skipped over the next time the counter reaches it, exactly
        as it always is; nothing about that changes.

    .PARAMETER SyncRuleId
        The unique identifier of the Synchronisation Rule the mapping belongs to. Also accepts pipeline
        input via the Id property. Alias: Id.

    .PARAMETER MappingId
        The unique identifier of the mapping.

    .OUTPUTS
        One PSCustomObject describing what moved:

        | Property                | Description                                                          |
        |--------------------------|------------------------------------------------------------------------|
        | RetiredValuesForgotten   | Always 0 in this release; no retired values register exists yet      |
        | CounterFrom              | The counter's position before this call, for a Sequence mapping      |
        | CounterTo                | The counter's position after this call (the mapping's Sequence Start) |

        CounterFrom and CounterTo are both $null for a mapping whose token kind is not Sequence, since
        there is nothing to move.

    .EXAMPLE
        Restart-JIMGeneratedValues -SyncRuleId 1 -MappingId 12

        Prompts for confirmation, then moves mapping 12's Sequence counter back to its configured Sequence
        Start. No existing Employee Number changes, and nothing exports.

    .EXAMPLE
        Restart-JIMGeneratedValues -SyncRuleId 1 -MappingId 12 -Confirm:$false

        Does the same without prompting, for use in a script.

    .LINK
        Get-JIMGeneratedValueSequence
        Set-JIMSyncRuleMapping
        Get-JIMGeneratedValue
        Get-JIMSyncRuleMapping
    #>
    [CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
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

        $target = "Mapping $MappingId in Synchronisation Rule $SyncRuleId"

        if ($PSCmdlet.ShouldProcess($target, "Start generated values again (move the Sequence counter back to its configured Start)")) {
            Write-Verbose "Restarting generated values for mapping $MappingId of Synchronisation Rule $SyncRuleId"

            try {
                $result = Invoke-JIMApi -Endpoint "/api/v1/synchronisation/sync-rules/$SyncRuleId/mappings/$MappingId/generation/restart" -Method 'POST'

                Write-Verbose "Restarted generated values for mapping $MappingId"

                $result
            }
            catch {
                Write-Error "Failed to restart generated values for mapping $MappingId in Synchronisation Rule $SyncRuleId: $_"
            }
        }
    }
}
