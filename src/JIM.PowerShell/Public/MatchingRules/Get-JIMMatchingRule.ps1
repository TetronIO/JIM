# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Get-JIMMatchingRule {
    <#
    .SYNOPSIS
        Gets the Object Matching Rules on a Connected System Object Type (simple mode).

    .DESCRIPTION
        Gets the Object Matching Rules on a Connected System Object Type, which apply in simple mode: every rule on
        the type, in the order JIM tries them, or one rule by ID.

    .PARAMETER ConnectedSystemId
        The ID of the Connected System.

    .PARAMETER ObjectTypeId
        The ID of the Connected System Object Type whose rules to get.

    .PARAMETER Id
        The ID of the rule to get.

    .OUTPUTS
        The rules, in the order JIM tries them, or the one rule.

        JIM.ObjectTypeMatchingRule
        ConnectedSystemId (int): The ID of the Connected System the rule belongs to, so the rule can be piped to
            Set-JIMMatchingRule and Remove-JIMMatchingRule.

    .EXAMPLE
        Get-JIMMatchingRule -ConnectedSystemId 1 -ObjectTypeId 10

        Gets all Matching Rules for Object Type ID 10 in Connected System ID 1.

    .EXAMPLE
        Get-JIMMatchingRule -ConnectedSystemId 1 -Id 5

        Gets the specific Matching Rule with ID 5 from Connected System ID 1.

    .LINK
        New-JIMMatchingRule
        Set-JIMMatchingRule
        Remove-JIMMatchingRule
    #>
    [CmdletBinding(DefaultParameterSetName = 'ByObjectType')]
    [OutputType('JIM.ObjectTypeMatchingRule')]
    param(
        [Parameter(Mandatory, ValueFromPipelineByPropertyName)]
        [int]$ConnectedSystemId,

        [Parameter(Mandatory, ParameterSetName = 'ByObjectType', ValueFromPipelineByPropertyName)]
        [int]$ObjectTypeId,

        [Parameter(Mandatory, ParameterSetName = 'ById')]
        [int]$Id
    )

    process {
        # Check connection first
        if (-not $script:JIMConnection) {
            Write-Error "You are not connected to JIM. Run Connect-JIM -Url <your JIM URL> to authenticate, then try again."
            return
        }

        if ($PSCmdlet.ParameterSetName -eq 'ById') {
            Write-Verbose "Getting Matching Rule ID: $Id for Connected System ID: $ConnectedSystemId"
            $result = Invoke-JIMApi -Endpoint "/api/v1/synchronisation/connected-systems/$ConnectedSystemId/matching-rules/$Id"
            $result | Add-Member -NotePropertyName 'ConnectedSystemId' -NotePropertyValue $ConnectedSystemId -PassThru -Force
        }
        else {
            Write-Verbose "Getting Matching Rules for Connected System ID: $ConnectedSystemId, Object Type ID: $ObjectTypeId"
            $result = Invoke-JIMApi -Endpoint "/api/v1/synchronisation/connected-systems/$ConnectedSystemId/object-types/$ObjectTypeId/matching-rules"

            # Output each rule individually for pipeline support
            foreach ($rule in $result) {
                $rule | Add-Member -NotePropertyName 'ConnectedSystemId' -NotePropertyValue $ConnectedSystemId -PassThru -Force
            }
        }
    }
}
