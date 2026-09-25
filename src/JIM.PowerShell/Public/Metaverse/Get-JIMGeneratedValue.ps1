# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Get-JIMGeneratedValue {
    <#
    .SYNOPSIS
        Lists the generated values a Metaverse Object currently holds (Unique Value Generation, #242).

    .DESCRIPTION
        Returns the committed Unique Value Generation assignments a Metaverse Object holds, one per
        generated attribute: the value, which uniqueness token produced it, the Synchronisation Rule and
        mapping responsible, its state, and whether it was adopted from an existing accepted value rather
        than generated. Empty when the object holds no generated values.

    .PARAMETER MetaverseObjectId
        The unique identifier (GUID) of the Metaverse Object. Also accepts pipeline input via the Id
        property, so a Metaverse Object from Get-JIMMetaverseObject or Search-JIMMetaverseObject can be
        piped straight in. Alias: Id.

    .OUTPUTS
        One PSCustomObject per generated value the object holds:

        | Property           | Description                                                                    |
        |---------------------|--------------------------------------------------------------------------------|
        | AssignmentId        | The assignment's own identifier                                               |
        | MetaverseAttributeId | The Metaverse Attribute this value was generated for                         |
        | AttributeName        | Its name                                                                     |
        | Value                | The committed value                                                          |
        | TokenKind            | OnlyIfTaken, Sequence or Random                                              |
        | SyncRuleId           | The Synchronisation Rule whose generated mapping produced this value         |
        | SyncRuleName         | Its name                                                                      |
        | SyncRuleMappingId    | The mapping responsible                                                       |
        | State                | Proposed, Committed, Remediated or NeedsDecision                             |
        | Adopted              | True when the value was adopted from an existing accepted value, not generated |
        | AssignedDate         | When the assignment was created                                              |

    .EXAMPLE
        Get-JIMGeneratedValue -MetaverseObjectId 8f1c2d3e-4a5b-6c7d-8e9f-0a1b2c3d4e5f

        Lists every generated value this Metaverse Object holds.

    .EXAMPLE
        Get-JIMMetaverseObject -ObjectTypeName "person" -Search "j.smith" | Get-JIMGeneratedValue

        Pipes a Metaverse Object straight in to see what JIM generated for it.

    .LINK
        Get-JIMGeneratedValueSequence
        Restart-JIMGeneratedValues
        Get-JIMMetaverseObject
        Get-JIMSyncRuleMapping
    #>
    [CmdletBinding()]
    [OutputType([PSCustomObject])]
    param(
        # Aliased to Id so a Metaverse Object can be piped straight in (Get-JIMMetaverseObject exposes Id,
        # not MetaverseObjectId). This cmdlet has no other -Id parameter, so the alias carries no ambiguity;
        # see the parameter alias rules in src/JIM.PowerShell/CLAUDE.md before copying this pattern elsewhere.
        [Parameter(Mandatory, ValueFromPipelineByPropertyName)]
        [Alias('Id')]
        [guid]$MetaverseObjectId
    )

    process {
        if (-not $script:JIMConnection) {
            Write-Error "You are not connected to JIM. Run Connect-JIM -Url <your JIM URL> to authenticate, then try again."
            return
        }

        Write-Verbose "Getting generated values for Metaverse Object: $MetaverseObjectId"
        $response = Invoke-JIMApi -Endpoint "/api/v1/metaverse/objects/$MetaverseObjectId/generated-values"

        foreach ($item in $response) {
            $item
        }
    }
}
