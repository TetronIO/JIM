# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Restart-JIMGeneratedValues {
    <#
    .SYNOPSIS
        Starts a generated Sequence mapping again from its Sequence Start.

    .DESCRIPTION
        Starts a generated Sequence mapping again: moves its attribute's counter to the mapping's Sequence Start,
        back or forward. The attribute's retired values are forgotten, so they can be issued again. Nothing else
        changes: no existing generated value changes, nothing is exported, and no synchronisation runs. A number a
        Metaverse Object still holds is skipped when the counter reaches it, as always. For a mapping whose token is
        OnlyIfTaken or Random, nothing happens and the command still succeeds.

    .PARAMETER SyncRuleId
        The ID of the Synchronisation Rule the mapping belongs to.

    .PARAMETER MappingId
        The ID of the generated mapping.

    .OUTPUTS
        What moved, and how many retired values were forgotten.

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
    [OutputType('JIM.GeneratedValueRestartResultDto')]
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
                Write-Error "Failed to restart generated values for mapping $MappingId in Synchronisation Rule ${SyncRuleId}: $_"
            }
        }
    }
}
