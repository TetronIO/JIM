# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Get-JIMSyncRuleMapping {
    <#
    .SYNOPSIS
        Gets a Synchronisation Rule's Attribute Flows.

    .DESCRIPTION
        Gets the Attribute Flows (mappings) on a Synchronisation Rule: every one, or one by ID. Each says what it
        reads, what it writes, and how.

    .PARAMETER SyncRuleId
        The ID of the Synchronisation Rule.

    .PARAMETER MappingId
        The ID of the mapping to get. Without it, every mapping on the rule comes back.

    .OUTPUTS
        The rule's mappings, or the one mapping. On a generated mapping, Generation.Participants says where its
        value is checked for availability, and how; Generation.Exclusions lists the Connected Systems left out.

    .EXAMPLE
        Get-JIMSyncRuleMapping -SyncRuleId 1

        Gets all attribute flow mappings for Synchronisation Rule with ID 1.

    .EXAMPLE
        Get-JIMSyncRuleMapping -SyncRuleId 1 -MappingId 5

        Gets a specific mapping from Synchronisation Rule 1.

    .EXAMPLE
        (Get-JIMSyncRuleMapping -SyncRuleId 1 -MappingId 12).Generation.Participants |
            Select-Object ConnectedSystemName, AttributeName, Check, Reason

        Lists where a generated value is checked for availability, and how: JIM's own records and a probe of
        the target, JIM's records only, or not at all, with the reason.

    .LINK
        New-JIMSyncRuleMapping
        Remove-JIMSyncRuleMapping
        Get-JIMSyncRule
    #>
    [CmdletBinding(DefaultParameterSetName = 'List')]
    [OutputType('JIM.SyncRuleMappingDto')]
    param(
        [Parameter(Mandatory, ValueFromPipelineByPropertyName, ParameterSetName = 'List')]
        [Parameter(Mandatory, ValueFromPipelineByPropertyName, ParameterSetName = 'ById')]
        [Alias('Id')]
        [int]$SyncRuleId,

        [Parameter(ParameterSetName = 'ById')]
        [int]$MappingId
    )

    process {
        # Check connection first
        if (-not $script:JIMConnection) {
            Write-Error "You are not connected to JIM. Run Connect-JIM -Url <your JIM URL> to authenticate, then try again."
            return
        }

        # Determine if we're getting a specific mapping or all mappings
        $isGettingById = $PSCmdlet.ParameterSetName -eq 'ById'

        if ($isGettingById) {
            Write-Verbose "Getting Synchronisation Rule Mapping with ID: $MappingId for Synchronisation Rule: $SyncRuleId"
            $result = Invoke-JIMApi -Endpoint "/api/v1/synchronisation/sync-rules/$SyncRuleId/mappings/$MappingId"
            $result
        }
        else {
            Write-Verbose "Getting all Synchronisation Rule Mappings for Synchronisation Rule: $SyncRuleId"
            $response = Invoke-JIMApi -Endpoint "/api/v1/synchronisation/sync-rules/$SyncRuleId/mappings"

            # Handle array or paginated response
            $mappings = if ($response -is [array]) { $response }
                       elseif ($response.items) { $response.items }
                       else { @($response) }

            # Output each mapping individually for pipeline support
            foreach ($mapping in $mappings) {
                $mapping
            }
        }
    }
}
