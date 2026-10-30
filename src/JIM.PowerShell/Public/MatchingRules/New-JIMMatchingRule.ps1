# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function New-JIMMatchingRule {
    <#
    .SYNOPSIS
        Creates an Object Matching Rule on a Connected System Object Type (simple mode).

    .DESCRIPTION
        Creates an Object Matching Rule on a Connected System Object Type. The Connected System must be in simple
        mode: JIM refuses a rule on an Object Type in advanced mode, because it would never be consulted. Switch the
        mode with Switch-JIMMatchingMode, or use New-JIMSyncRuleMatchingRule.

    .PARAMETER ConnectedSystemId
        The ID of the Connected System.

    .PARAMETER ObjectTypeId
        The ID of the Connected System Object Type the rule is for.

    .PARAMETER MetaverseObjectTypeId
        The ID of the Metaverse Object Type whose objects the rule searches.

    .PARAMETER SourceAttributeId
        The ID of the Connected System attribute whose values are matched against -TargetMetaverseAttributeId, for
        import and export matching alike. Give this or -SourceExpression.

    .PARAMETER SourceExpression
        An expression computing the value to match on, instead of a Connected System attribute, for an identifier
        held differently in each system: for example '"E" + Trim(cs["employeeId"])'. It reads the Connected System
        Object only, through cs["attributeName"]. Its result is converted to the type of
        -TargetMetaverseAttributeId, and a list of text values is matched value by value. JIM refuses an expression
        that does not parse, reads mv["..."], reads no cs["..."] attribute or one the Object Type does not have, or
        calls a function that returns a different value each time.

    .PARAMETER TargetMetaverseAttributeId
        The ID of the Metaverse attribute the source is matched against.

    .PARAMETER Order
        Where the rule comes in the order JIM tries the rules, lowest first. Without it, the rule goes after the
        existing ones.

    .PARAMETER CaseSensitive
        Whether the comparison respects case: with $true, 'emp123' does not match 'EMP123'. When omitted, follows
        the source: respects case when a source attribute's values are declared case-exact, and ignores it otherwise.

    .PARAMETER PassThru
        Returns the rule created.

    .OUTPUTS
        With -PassThru, the rule created; otherwise nothing. AwaitingSynchronisation names the Connected System Object Type
        the new rule makes its Connected System's next synchronisation, Delta or Full, process in full, with how many
        objects that is and about how long it takes, and a warning saying the same is written to the warning stream.

        JIM.ObjectTypeMatchingRule
        ConnectedSystemId (int): The ID of the Connected System the rule belongs to, so the rule can be piped to
            Set-JIMMatchingRule and Remove-JIMMatchingRule.

    .EXAMPLE
        New-JIMMatchingRule -ConnectedSystemId 1 -ObjectTypeId 10 -MetaverseObjectTypeId 1 -SourceAttributeId 25 -TargetMetaverseAttributeId 5

        Creates a matching rule that maps CS attribute 25 to MV attribute 5, searching MVO type 1.

    .EXAMPLE
        New-JIMMatchingRule -ConnectedSystemId 1 -ObjectTypeId 10 -MetaverseObjectTypeId 1 -SourceAttributeId 25 -TargetMetaverseAttributeId 5 -Order 0 -PassThru

        Creates a matching rule at order 0 and returns the created rule.

    .EXAMPLE
        New-JIMMatchingRule -ConnectedSystemId 1 -ObjectTypeId 10 -MetaverseObjectTypeId 1 -SourceExpression '"E" + Trim(cs["employeeId"])' -TargetMetaverseAttributeId 5

        Creates a matching rule that joins a directory account holding "1007" to the Metaverse Object whose
        attribute 5 holds "E1007".

    .LINK
        Get-JIMMatchingRule
        Set-JIMMatchingRule
        Remove-JIMMatchingRule
        Switch-JIMMatchingMode
    #>
    [CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'Medium', DefaultParameterSetName = 'Attribute')]
    [OutputType('JIM.ObjectTypeMatchingRule')]
    param(
        [Parameter(Mandatory, ValueFromPipelineByPropertyName)]
        [int]$ConnectedSystemId,

        [Parameter(Mandatory)]
        [int]$ObjectTypeId,

        [Parameter(Mandatory)]
        [int]$MetaverseObjectTypeId,

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
            connectedSystemObjectTypeId = $ObjectTypeId
            metaverseObjectTypeId = $MetaverseObjectTypeId
            targetMetaverseAttributeId = $TargetMetaverseAttributeId
            sources = @($source)
        }

        if ($PSBoundParameters.ContainsKey('Order')) {
            $body.order = $Order
        }

        if ($PSBoundParameters.ContainsKey('CaseSensitive')) {
            $body.caseSensitive = $CaseSensitive
        }

        if ($PSCmdlet.ShouldProcess("Connected System $ConnectedSystemId, Object Type $ObjectTypeId", "Create Matching Rule")) {
            Write-Verbose "Creating Matching Rule for Connected System ID: $ConnectedSystemId"

            try {
                $result = Invoke-JIMApi -Endpoint "/api/v1/synchronisation/connected-systems/$ConnectedSystemId/matching-rules" -Method 'POST' -Body $body

                Write-Verbose "Created Matching Rule ID: $($result.id)"

                # The Object Types the save made the next synchronisation process in full (#2615): advised, never blocking.
                Write-JIMAwaitingSynchronisationWarning -AwaitingSynchronisation $result.AwaitingSynchronisation

                if ($PassThru) {
                    $result | Add-Member -NotePropertyName 'ConnectedSystemId' -NotePropertyValue $ConnectedSystemId -PassThru -Force
                }
            }
            catch {
                Write-Error "Failed to create Matching Rule: $_"
            }
        }
    }
}
