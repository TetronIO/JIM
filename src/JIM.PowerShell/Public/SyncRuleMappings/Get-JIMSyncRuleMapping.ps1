# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Get-JIMSyncRuleMapping {
    <#
    .SYNOPSIS
        Gets Synchronisation Rule Mappings (attribute flow rules) from JIM.

    .DESCRIPTION
        Retrieves attribute flow mappings for a Synchronisation Rule. These mappings define
        how data flows between Connected System attributes and Metaverse attributes.

    .PARAMETER SyncRuleId
        The unique identifier of the Synchronisation Rule.
        Also accepts pipeline input via the Id property.

    .PARAMETER MappingId
        Optional. The unique identifier of a specific mapping to retrieve.
        If not specified, all mappings for the Synchronisation Rule are returned.

    .OUTPUTS
        PSCustomObject representing Synchronisation Rule Mapping(s).

    .EXAMPLE
        Get-JIMSyncRuleMapping -SyncRuleId 1

        Gets all attribute flow mappings for Synchronisation Rule with ID 1.

    .EXAMPLE
        Get-JIMSyncRuleMapping -SyncRuleId 1 -MappingId 5

        Gets a specific mapping from Synchronisation Rule 1.

    .LINK
        New-JIMSyncRuleMapping
        Remove-JIMSyncRuleMapping
        Get-JIMSyncRule
    #>
    [CmdletBinding(DefaultParameterSetName = 'List')]
    [OutputType([PSCustomObject])]
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
