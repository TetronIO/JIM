# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Set-JIMSyncRuleMatchingRule {
    <#
    .SYNOPSIS
        Changes an Object Matching Rule on a Synchronisation Rule (advanced mode).

    .DESCRIPTION
        Changes an Object Matching Rule on a Synchronisation Rule. What you leave out stays as it is.

    .PARAMETER SyncRuleId
        The ID of the Synchronisation Rule.

    .PARAMETER Id
        The ID of the rule to change.

    .PARAMETER Order
        Where the rule comes in the order JIM tries the rules, lowest first.

    .PARAMETER TargetMetaverseAttributeId
        The ID of the Metaverse attribute the source is matched against.

    .PARAMETER SourceAttributeId
        Replaces the rule's source with this Connected System attribute. Cannot be used with -SourceExpression; give
        neither to leave the source as it is.

    .PARAMETER SourceExpression
        Replaces the rule's source with an expression computing the value to match on: for example '"E" +
        Trim(cs["employeeId"])'. It reads the Connected System Object only, through cs["attributeName"]. Its result
        is converted to the type of the rule's Metaverse attribute, and a list of text values is matched value by
        value. JIM refuses an expression that does not parse, reads mv["..."], reads no cs["..."] attribute or one
        the Object Type does not have, or calls a function that returns a different value each time. Cannot be used
        with -SourceAttributeId.

    .PARAMETER CaseSensitive
        Whether the comparison respects case: with $true, 'emp123' does not match 'EMP123'. Omit it to leave it as
        it is.

    .PARAMETER PassThru
        Returns the rule as changed.

    .OUTPUTS
        With -PassThru, the rule as changed; otherwise nothing.

    .EXAMPLE
        Set-JIMSyncRuleMatchingRule -SyncRuleId 5 -Id 12 -Order 0

        Updates the order of Matching Rule 12 on Synchronisation Rule 5 to be first (order 0).

    .EXAMPLE
        Set-JIMSyncRuleMatchingRule -SyncRuleId 5 -Id 12 -SourceExpression 'Lower(Trim(cs["mail"]))'

        Replaces the rule's source with an expression that trims the directory's address and lowers its case.

    .EXAMPLE
        Get-JIMSyncRuleMatchingRule -SyncRuleId 5 -Id 12 | Set-JIMSyncRuleMatchingRule -CaseSensitive $false

        Updates case sensitivity using pipeline input.

    .LINK
        Get-JIMSyncRuleMatchingRule
        New-JIMSyncRuleMatchingRule
        Remove-JIMSyncRuleMatchingRule
    #>
    [CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'Medium', DefaultParameterSetName = 'NoSource')]
    [OutputType('JIM.ObjectMatchingRuleDto')]
    param(
        [Parameter(Mandatory, ValueFromPipelineByPropertyName)]
        [int]$SyncRuleId,

        [Parameter(Mandatory, ValueFromPipelineByPropertyName)]
        [int]$Id,

        [Parameter()]
        [int]$Order,

        [Parameter()]
        [int]$TargetMetaverseAttributeId,

        [Parameter(Mandatory, ParameterSetName = 'Attribute')]
        [int]$SourceAttributeId,

        [Parameter(Mandatory, ParameterSetName = 'Expression')]
        [ValidateNotNullOrEmpty()]
        [ValidateScript({ -not [string]::IsNullOrWhiteSpace($_) })]
        [string]$SourceExpression,

        [Parameter()]
        [bool]$CaseSensitive,

        [switch]$PassThru
    )

    process {
        # Check connection first
        if (-not $script:JIMConnection) {
            Write-Error "You are not connected to JIM. Run Connect-JIM -Url <your JIM URL> to authenticate, then try again."
            return
        }

        $body = @{}

        if ($PSBoundParameters.ContainsKey('Order')) {
            $body.order = $Order
        }

        if ($PSBoundParameters.ContainsKey('TargetMetaverseAttributeId')) {
            $body.targetMetaverseAttributeId = $TargetMetaverseAttributeId
        }

        # A source is an attribute or an expression, never both (#2052); the parameter sets enforce that, and
        # giving neither leaves the rule's source as it is.
        if ($PSCmdlet.ParameterSetName -eq 'Attribute') {
            $body.sources = @(@{ order = 0; connectedSystemAttributeId = $SourceAttributeId })
        }
        elseif ($PSCmdlet.ParameterSetName -eq 'Expression') {
            $body.sources = @(@{ order = 0; expression = $SourceExpression })
        }

        if ($PSBoundParameters.ContainsKey('CaseSensitive')) {
            $body.caseSensitive = $CaseSensitive
        }

        if ($body.Count -eq 0) {
            Write-Warning "No updates specified."
            return
        }

        if ($PSCmdlet.ShouldProcess("Matching Rule $Id on Synchronisation Rule $SyncRuleId", "Update")) {
            Write-Verbose "Updating Matching Rule ID: $Id for Synchronisation Rule ID: $SyncRuleId"

            try {
                $result = Invoke-JIMApi -Endpoint "/api/v1/synchronisation/sync-rules/$SyncRuleId/matching-rules/$Id" -Method 'PUT' -Body $body

                Write-Verbose "Updated Matching Rule ID: $Id"

                if ($PassThru) {
                    $result | Add-Member -NotePropertyName 'SyncRuleId' -NotePropertyValue $SyncRuleId -PassThru -Force
                }
            }
            catch {
                Write-Error "Failed to update Matching Rule: $_"
            }
        }
    }
}
