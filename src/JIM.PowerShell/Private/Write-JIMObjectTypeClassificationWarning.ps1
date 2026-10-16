# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Write-JIMObjectTypeClassificationWarning {
    <#
    .SYNOPSIS
        Warns about each Object Type classification change a schema refresh preview reports (#2049).

    .DESCRIPTION
        A schema refresh can change how the Connected System classifies a selected Object Type: its class kind, or
        inheritance involving another selected Object Type. Applying such a change can change which Object Type
        existing entries resolve to at the next import, where by default each object that would change Object Type is
        refused and reported as an Object Type Changed error, unless a Full Import Run Profile with Allow Object Type
        Changes enabled carries the change out. This writes one line per change with Write-Warning, so a script
        reviewing a preview cannot miss one. It never prompts and never blocks.

    .PARAMETER ChangedObjectTypeClassifications
        The preview result's ChangedObjectTypeClassifications: a dictionary keyed by Object Type name, each value a
        list of changes carrying Aspect, OldValues and NewValues. Nothing is written when it is empty or absent.

    .OUTPUTS
        None. Writes to the warning stream only.
    #>
    [CmdletBinding()]
    param(
        [AllowNull()]
        $ChangedObjectTypeClassifications
    )

    if ($null -eq $ChangedObjectTypeClassifications) {
        return
    }

    # The API returns a JSON object keyed by Object Type name, which arrives as a PSCustomObject; accept a
    # dictionary too, so a caller holding a hashtable gets the same behaviour.
    $entries = if ($ChangedObjectTypeClassifications -is [System.Collections.IDictionary]) {
        @($ChangedObjectTypeClassifications.GetEnumerator() | ForEach-Object { [PSCustomObject]@{ Name = $_.Key; Value = $_.Value } })
    }
    else {
        @($ChangedObjectTypeClassifications.PSObject.Properties | ForEach-Object { [PSCustomObject]@{ Name = $_.Name; Value = $_.Value } })
    }

    foreach ($entry in $entries) {
        foreach ($change in @($entry.Value | Where-Object { $_ })) {
            $aspect = if ($change.Aspect -eq 'Inheritance') { 'inherited classes' } else { 'class kind' }

            $oldValues = @($change.OldValues | Where-Object { $_ })
            $newValues = @($change.NewValues | Where-Object { $_ })
            $old = if ($oldValues.Count -gt 0) { $oldValues -join ', ' } else { 'none' }
            $new = if ($newValues.Count -gt 0) { $newValues -join ', ' } else { 'none' }

            Write-Warning ("Object Type '$($entry.Name)': $aspect would change from $old to $new. " +
                "Applying this can change which Object Type existing entries resolve to: by default each object that would change " +
                "Object Type is refused at the next import and reported as an Object Type Changed error, unless a Full Import Run " +
                "Profile with Allow Object Type Changes enabled carries the change out.")
        }
    }
}
