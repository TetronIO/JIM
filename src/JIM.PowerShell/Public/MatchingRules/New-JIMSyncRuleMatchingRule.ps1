# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function New-JIMSyncRuleMatchingRule {
    <#
    .SYNOPSIS
        Creates a new Object Matching Rule on a Synchronisation Rule (advanced mode).

    .DESCRIPTION
        Creates a new Object Matching Rule on a specific Synchronisation Rule.
        This is used in advanced mode where matching rules are per-Synchronisation Rule
        rather than per-object type. The Metaverse Object Type is derived from
        the Synchronisation Rule automatically.

        The Connected System must be in advanced matching mode: JIM refuses to create a
        Synchronisation Rule scoped rule on a system in simple matching mode, because such
        a rule would never be consulted. Switch the mode first with Switch-JIMMatchingMode,
        or use New-JIMMatchingRule to create a per-object-type rule instead.

    .PARAMETER SyncRuleId
        The unique identifier of the Synchronisation Rule.

    .PARAMETER SourceAttributeId
        The Connected System attribute ID to use as the source for matching. Matched against
        -TargetMetaverseAttributeId, for both import and export matching. Give this or -SourceExpression.

    .PARAMETER SourceExpression
        An expression computing the value to match on, instead of a Connected System attribute, for
        identifiers that differ in format between systems: for example '"E" + Trim(cs["employeeId"])'.
        It reads the Connected System Object only, through cs["attributeName"]; its result is converted
        to the type of -TargetMetaverseAttributeId, and a list of text values is matched value by value.
        JIM refuses an expression that does not parse, reads mv["..."], reads no cs["..."] attribute or
        one the Object Type does not have, or calls a function that returns a different value each time.

    .PARAMETER TargetMetaverseAttributeId
        The Metaverse attribute ID to match against.

    .PARAMETER Order
        The evaluation order for this rule (lower values are evaluated first).
        If not specified, the rule will be added at the end.

    .PARAMETER CaseSensitive
        Whether the matching should be case-sensitive.
        When false (default), 'emp123' matches 'EMP123'.
        When true, 'emp123' does NOT match 'EMP123'.

    .PARAMETER PassThru
        If specified, returns the created Matching Rule object.

    .OUTPUTS
        If -PassThru is specified, returns the created Matching Rule object.

    .EXAMPLE
        New-JIMSyncRuleMatchingRule -SyncRuleId 5 -SourceAttributeId 25 -TargetMetaverseAttributeId 5

        Creates a matching rule on Synchronisation Rule 5 that maps CS attribute 25 to MV attribute 5.

    .EXAMPLE
        New-JIMSyncRuleMatchingRule -SyncRuleId 5 -SourceExpression 'Lower(Trim(cs["mail"]))' -TargetMetaverseAttributeId 5

        Creates a matching rule on Synchronisation Rule 5 that matches the address held in the directory, trimmed
        and in lower case, against MV attribute 5.

    .LINK
        Get-JIMSyncRuleMatchingRule
        Set-JIMSyncRuleMatchingRule
        Remove-JIMSyncRuleMatchingRule
        Switch-JIMMatchingMode
    #>
    [CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'Medium', DefaultParameterSetName = 'Attribute')]
    [OutputType([PSCustomObject])]
    param(
        [Parameter(Mandatory, ValueFromPipelineByPropertyName)]
        [int]$SyncRuleId,

        [Parameter(Mandatory, ParameterSetName = 'Attribute')]
        [int]$SourceAttributeId,

        [Parameter(Mandatory, ParameterSetName = 'Expression')]
        [ValidateNotNullOrEmpty()]
        [ValidateScript({ -not [string]::IsNullOrWhiteSpace($_) })]
        [string]$SourceExpression,

        [Parameter(Mandatory)]
        [int]$TargetMetaverseAttributeId,

        [Parameter()]
        [int]$Order,

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

        # A source is an attribute or an expression, never both (#2052); the parameter sets enforce that.
        $source = @{ order = 0 }
        if ($PSCmdlet.ParameterSetName -eq 'Expression') {
            $source.expression = $SourceExpression
        }
        else {
            $source.connectedSystemAttributeId = $SourceAttributeId
        }

        $body = @{
            targetMetaverseAttributeId = $TargetMetaverseAttributeId
            sources = @($source)
        }

        if ($PSBoundParameters.ContainsKey('Order')) {
            $body.order = $Order
        }

        if ($PSBoundParameters.ContainsKey('CaseSensitive')) {
            $body.caseSensitive = $CaseSensitive
        }

        if ($PSCmdlet.ShouldProcess("Synchronisation Rule $SyncRuleId", "Create Matching Rule")) {
            Write-Verbose "Creating Matching Rule for Synchronisation Rule ID: $SyncRuleId"

            try {
                $result = Invoke-JIMApi -Endpoint "/api/v1/synchronisation/sync-rules/$SyncRuleId/matching-rules" -Method 'POST' -Body $body

                Write-Verbose "Created Matching Rule ID: $($result.id)"

                if ($PassThru) {
                    $result | Add-Member -NotePropertyName 'SyncRuleId' -NotePropertyValue $SyncRuleId -PassThru -Force
                }
            }
            catch {
                Write-Error "Failed to create Matching Rule: $_"
            }
        }
    }
}
