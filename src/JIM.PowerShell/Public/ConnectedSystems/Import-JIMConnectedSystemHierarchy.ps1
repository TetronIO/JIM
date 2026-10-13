# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Import-JIMConnectedSystemHierarchy {
    <#
    .SYNOPSIS
        Imports a Connected System's partitions and containers.

    .DESCRIPTION
        Reads a Connected System's partitions and containers into JIM, or refreshes those imported before: on an
        LDAP directory, its naming contexts and organisational units. Choose which to import from with
        Set-JIMConnectedSystemPartition and Set-JIMConnectedSystemContainer.

        A refresh keeps your selections. Partitions and containers are matched by the Connected System's own
        immutable identifier where it has one, so one that was renamed or moved keeps its selection. A selected
        partition or container the Connected System no longer reports is removed, and the result's
        HasSelectedItemsRemoved says so.

    .PARAMETER Id
        The ID of the Connected System to import the hierarchy of.

    .PARAMETER InputObject
        A Connected System from Get-JIMConnectedSystem, through the pipeline.

    .PARAMETER PassThru
        Returns what the refresh changed.

    .OUTPUTS
        With -PassThru, what the refresh added, removed, renamed and moved; otherwise nothing.

    .EXAMPLE
        Import-JIMConnectedSystemHierarchy -Id 1

        Imports the hierarchy for Connected System with ID 1.

    .EXAMPLE
        Import-JIMConnectedSystemHierarchy -Id 1 -PassThru

        Imports the hierarchy and returns the updated Connected System.

    .EXAMPLE
        Get-JIMConnectedSystem -Id 1 | Import-JIMConnectedSystemHierarchy -PassThru

        Imports hierarchy for a Connected System from the pipeline.

    .EXAMPLE
        $system = New-JIMConnectedSystem -Name "AD" -ConnectorDefinitionId 1 -PassThru
        Set-JIMConnectedSystem -Id $system.id -SettingValues @{ ... }
        Import-JIMConnectedSystemSchema -Id $system.id
        Import-JIMConnectedSystemHierarchy -Id $system.id -PassThru

        Creates a Connected System, configures it, imports its schema and hierarchy.

    .LINK
        Get-JIMConnectedSystem
        Get-JIMConnectedSystemPartition
        Set-JIMConnectedSystemPartition
        Set-JIMConnectedSystemContainer
    #>
    [CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'Medium', DefaultParameterSetName = 'ById')]
    [OutputType('JIM.HierarchyRefreshResultDto')]
    param(
        [Parameter(Mandatory, ParameterSetName = 'ById', ValueFromPipelineByPropertyName)]
        [int]$Id,

        [Parameter(Mandatory, ParameterSetName = 'ByInputObject', ValueFromPipeline)]
        [PSCustomObject]$InputObject,

        [switch]$PassThru
    )

    process {
        # Check connection first
        if (-not $script:JIMConnection) {
            Write-Error "You are not connected to JIM. Run Connect-JIM -Url <your JIM URL> to authenticate, then try again."
            return
        }

        $systemId = if ($InputObject) { $InputObject.id } else { $Id }

        if ($PSCmdlet.ShouldProcess("Connected System $systemId", "Import Hierarchy")) {
            Write-Verbose "Importing hierarchy for Connected System: $systemId"

            try {
                $result = Invoke-JIMApi -Endpoint "/api/v1/synchronisation/connected-systems/$systemId/import-hierarchy" -Method 'POST'

                $partitionCount = if ($result.partitions) { $result.partitions.Count } else { 0 }
                Write-Verbose "Hierarchy imported for Connected System: $systemId ($partitionCount partitions)"

                if ($PassThru) {
                    $result
                }
            }
            catch {
                Write-Error "Failed to import hierarchy: $_"
            }
        }
    }
}
