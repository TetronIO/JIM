# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Approve-JIMGeneratedValueRename {
    <#
    .SYNOPSIS
        Allows JIM to rename the account holding a generated value that is held for a decision.

    .DESCRIPTION
        Authorises JIM, at the next export that meets the rejection, to choose the next free value and apply it
        everywhere the value is used. Where another Connected System has already accepted the current value
        (Reason AnchoredElsewhere), that account is renamed. The new value is decided at the next export, after JIM
        checks every system again, not now. The held export is released so that export happens.

        Because this renames a live account, it asks for confirmation (ConfirmImpact High) unless -Force is given.
        Each value is one request and is recorded as an Activity naming who allowed it.

    .PARAMETER Id
        The id of the generated value, as Get-JIMGeneratedValueDecision returns it.

    .PARAMETER InputObject
        A held value from Get-JIMGeneratedValueDecision, through the pipeline. The confirmation then names the
        value and its object.

    .PARAMETER Force
        Skips the confirmation prompt.

    .OUTPUTS
        The value as it now stands, in Get-JIMGeneratedValueDecision's shape (Status is RenameAllowed).

    .EXAMPLE
        Approve-JIMGeneratedValueRename -Id 8f1c2d3e-4a5b-6c7d-8e9f-0a1b2c3d4e5f

        Allows the rename of one held value, after confirmation.

    .EXAMPLE
        Get-JIMGeneratedValueDecision -MetaverseObjectId 8f1c2d3e-4a5b-6c7d-8e9f-0a1b2c3d4e5f -Status NeedsDecision |
            Approve-JIMGeneratedValueRename

        Allows the rename of every value held for one person, asking about each one (each renames an account).

    .EXAMPLE
        Get-JIMGeneratedValueDecision -SyncRuleId 2 -Status NeedsDecision -All | Approve-JIMGeneratedValueRename -WhatIf

        Shows which accounts would be renamed for every value held from Synchronisation Rule 2, without allowing
        anything. Review that list before running it again without -WhatIf: every value it names renames a live account.

    .LINK
        Get-JIMGeneratedValueDecision
        Reset-JIMGeneratedValueDecision
    #>
    [CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High', DefaultParameterSetName = 'ById')]
    [OutputType([PSCustomObject])]
    param(
        [Parameter(Mandatory, Position = 0, ParameterSetName = 'ById')]
        [guid]$Id,

        [Parameter(Mandatory, ValueFromPipeline, ParameterSetName = 'InputObject')]
        [PSCustomObject]$InputObject,

        [Parameter()]
        [switch]$Force
    )

    process {
        if (-not $script:JIMConnection) {
            Write-Error "You are not connected to JIM. Run Connect-JIM -Url <your JIM URL> to authenticate, then try again."
            return
        }

        $valueId = if ($PSCmdlet.ParameterSetName -eq 'InputObject') { $InputObject.Id } else { $Id }
        if (-not $valueId) {
            Write-Error "The object received has no Id; pipe in the output of Get-JIMGeneratedValueDecision."
            return
        }

        $target = if ($PSCmdlet.ParameterSetName -eq 'InputObject' -and $InputObject.Value) {
            $owner = if ($InputObject.MetaverseObjectDisplayName) { " for $($InputObject.MetaverseObjectDisplayName)" } else { '' }
            "$($InputObject.AttributeName) '$($InputObject.Value)'$owner"
        }
        else {
            "generated value $valueId"
        }

        if (-not $Force -and -not $PSCmdlet.ShouldProcess($target, "Allow the rename (renames the account holding it at the next export)")) {
            return
        }

        Write-Verbose "Allowing the rename of generated value $valueId"
        Invoke-JIMApi -Endpoint "/api/v1/generated-values/$valueId/allow-rename" -Method 'POST'
    }
}
