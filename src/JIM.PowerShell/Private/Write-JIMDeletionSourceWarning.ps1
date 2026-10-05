# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Write-JIMDeletionSourceWarning {
    <#
    .SYNOPSIS
        Warns about Connected Systems that project into a Metaverse Object Type without being one of its authoritative
        sources (#1256).

    .DESCRIPTION
        Under the WhenAuthoritativeSourceDisconnected deletion rule, a Connected System that projects into the type but
        is not a selected authoritative source can create objects no selected source governs: those are never deleted
        automatically, and the objects it shares with a selected source are deleted when that source disconnects. The
        API lists such systems in the Object Type response's DeletionSourceWarnings; this writes one Write-Warning line
        per system. Join-only contributors are never listed. It never prompts and never blocks.

    .PARAMETER ObjectType
        The Object Type response. Nothing is written when its DeletionSourceWarnings is empty or absent.

    .OUTPUTS
        None. Writes to the warning stream only.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [AllowNull()]
        [object]$ObjectType
    )

    if ($null -eq $ObjectType) {
        return
    }

    $typeName = $ObjectType.name
    foreach ($gap in @($ObjectType.deletionSourceWarnings | Where-Object { $_ })) {
        Write-Warning ("$($gap.connectedSystemName) projects $typeName objects but is not one of its authoritative " +
            "sources: objects only it holds will never be deleted, and objects it shares with an authoritative source " +
            "are deleted when that source disconnects. Select it as an authoritative source, or turn projection off " +
            "if it should only contribute attributes.")
    }
}
