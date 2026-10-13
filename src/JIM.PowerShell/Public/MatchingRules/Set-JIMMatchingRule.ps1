# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Set-JIMMatchingRule {
    <#
    .SYNOPSIS
        Changes an Object Matching Rule on a Connected System Object Type (simple mode).

    .DESCRIPTION
        Changes an Object Matching Rule on a Connected System Object Type. What you leave out stays as it is.

    .PARAMETER ConnectedSystemId
        The ID of the Connected System.

    .PARAMETER Id
        The ID of the rule to change.

    .PARAMETER Order
        Where the rule comes in the order JIM tries the rules, lowest first.

    .PARAMETER MetaverseObjectTypeId
        The ID of the Metaverse Object Type whose objects the rule searches.

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

        JIM.ObjectTypeMatchingRule
        ConnectedSystemId (int): The ID of the Connected System the rule belongs to, so the rule can be piped to
            Set-JIMMatchingRule and Remove-JIMMatchingRule.

    .EXAMPLE
        Set-JIMMatchingRule -ConnectedSystemId 1 -Id 5 -Order 0

        Updates the order of Matching Rule 5 to be first (order 0).

    .EXAMPLE
        Set-JIMMatchingRule -ConnectedSystemId 1 -Id 5 -TargetMetaverseAttributeId 10 -PassThru

        Updates the target MV attribute and returns the updated rule.

    .EXAMPLE
        Set-JIMMatchingRule -ConnectedSystemId 1 -Id 5 -SourceExpression '"E" + Trim(cs["employeeId"])'

        Replaces the rule's source attribute with an expression that adds the "E" prefix the Metaverse holds.

    .EXAMPLE
        Get-JIMMatchingRule -ConnectedSystemId 1 -Id 5 | Set-JIMMatchingRule -Order 2

        Updates the order using pipeline input.

    .LINK
        Get-JIMMatchingRule
        New-JIMMatchingRule
        Remove-JIMMatchingRule
    #>
    [CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'Medium', DefaultParameterSetName = 'NoSource')]
    [OutputType('JIM.ObjectTypeMatchingRule')]
    param(
        [Parameter(Mandatory, ValueFromPipelineByPropertyName)]
        [int]$ConnectedSystemId,

        [Parameter(Mandatory, ValueFromPipelineByPropertyName)]
        [int]$Id,

        [Parameter()]
        [int]$Order,

        [Parameter()]
        [int]$MetaverseObjectTypeId,

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

        if ($PSBoundParameters.ContainsKey('MetaverseObjectTypeId')) {
            $body.metaverseObjectTypeId = $MetaverseObjectTypeId
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

        if ($PSCmdlet.ShouldProcess("Matching Rule $Id", "Update")) {
            Write-Verbose "Updating Matching Rule ID: $Id for Connected System ID: $ConnectedSystemId"

            try {
                $result = Invoke-JIMApi -Endpoint "/api/v1/synchronisation/connected-systems/$ConnectedSystemId/matching-rules/$Id" -Method 'PUT' -Body $body

                Write-Verbose "Updated Matching Rule ID: $Id"

                if ($PassThru) {
                    $result | Add-Member -NotePropertyName 'ConnectedSystemId' -NotePropertyValue $ConnectedSystemId -PassThru -Force
                }
            }
            catch {
                Write-Error "Failed to update Matching Rule: $_"
            }
        }
    }
}
