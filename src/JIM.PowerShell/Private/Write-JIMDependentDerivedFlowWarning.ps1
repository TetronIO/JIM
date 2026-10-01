# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Write-JIMDependentDerivedFlowWarning {
    <#
    .SYNOPSIS
        Warns about the Metaverse-Derived Attribute Flows a change left with a missing input (#1750, FR 3).

    .DESCRIPTION
        Removing or disabling a Synchronisation Rule or an Attribute Flow mapping can take away the last enabled
        contributor of a Metaverse attribute that an Attribute Flow deriving another Metaverse attribute reads. JIM
        allows the change and reports those flows in the response's DependentDerivedFlows; this writes a summary
        line, then one line per flow, with Write-Warning. It never prompts and never blocks: by the time a cmdlet
        calls it, the change has been made (or, with -Prospective, a preview has described it).

    .PARAMETER DependentDerivedFlows
        The response's DependentDerivedFlows list. Nothing is written when it is empty or absent.

    .PARAMETER Prospective
        Words the warnings as a prediction, for a preview that has changed nothing yet.

    .OUTPUTS
        None. Writes to the warning stream only.
    #>
    [CmdletBinding()]
    param(
        [AllowNull()]
        $DependentDerivedFlows,

        [switch]$Prospective
    )

    $flows = @($DependentDerivedFlows | Where-Object { $_ })
    if ($flows.Count -eq 0) {
        return
    }

    if ($Prospective) {
        Write-Warning ("This change would leave $($flows.Count) Attribute Flow(s) deriving Metaverse attributes with a missing input. " +
            "Each flow's Missing Input Behaviour would then decide what it contributes.")
    }
    else {
        Write-Warning ("This change left $($flows.Count) Attribute Flow(s) deriving Metaverse attributes with a missing input. " +
            "The change went ahead; each flow's Missing Input Behaviour now decides what it contributes.")
    }

    foreach ($flow in $flows) {
        $inputs = @($flow.MissingInputs | Where-Object { $_ } | ForEach-Object {
                $via = @($_.Via | Where-Object { $_ })
                if ($via.Count -gt 0) { "$($_.MetaverseAttributeName) through $($via -join ', then ')" } else { "$($_.MetaverseAttributeName)" }
            })
        $reads = switch ($inputs.Count) {
            0 { 'an attribute' }
            1 { $inputs[0] }
            default { "$(($inputs[0..($inputs.Count - 2)]) -join ', '), and $($inputs[-1])" }
        }
        $verb = if ($inputs.Count -gt 1) { 'have' } else { 'has' }
        $state = if ($Prospective) { "would no longer have an enabled contributor" } else { "no longer $verb an enabled contributor" }

        Write-Warning ("$($flow.TargetMetaverseAttributeName) (Synchronisation Rule '$($flow.SyncRuleName)', Connected System " +
            "'$($flow.ConnectedSystemName)', mapping $($flow.MappingId)) reads $reads, which $state.")
    }
}
