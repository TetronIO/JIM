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

        Removing a partition that Run Profiles target would leave them reading every selected partition, so the
        refresh disables them instead, recording the partition's name, and warns about each one. Enable each again
        with Set-JIMRunProfile -Enabled $true, choosing its partition. With -RunProfileAction Cancel, such a refresh
        applies nothing and reports an error naming the Run Profiles.

    .PARAMETER Id
        The ID of the Connected System to import the hierarchy of.

    .PARAMETER InputObject
        A Connected System from Get-JIMConnectedSystem, through the pipeline.

    .PARAMETER RunProfileAction
        What to do when the refresh would remove a partition that Run Profiles target: Disable applies the refresh and
        disables them, and Cancel applies nothing from the refresh. A refresh that removes no such partition is applied
        either way.

    .PARAMETER PassThru
        Returns what the refresh changed.

    .OUTPUTS
        With -PassThru, what the refresh added, removed, renamed and moved, and the Run Profiles it disabled;
        otherwise nothing.

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
        Import-JIMConnectedSystemHierarchy -Id 1 -RunProfileAction Cancel

        Refreshes the hierarchy only if no Run Profile targets a partition the refresh would remove. Otherwise nothing
        is changed, and the error names each Run Profile and its partition.

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

        [ValidateSet('Disable', 'Cancel')]
        [string]$RunProfileAction = 'Disable',

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
                $result = Invoke-JIMApi -Endpoint "/api/v1/synchronisation/connected-systems/$systemId/import-hierarchy" -Method 'POST' `
                    -Body @{ runProfileAction = $RunProfileAction }

                Write-Verbose "Hierarchy imported for Connected System: $systemId ($($result.totalPartitions) partitions)"

                # A Run Profile disabled because its partition was removed stops running, Schedules included, so say
                # so whether or not the caller asked for the result.
                foreach ($runProfile in @($result.affectedRunProfiles) | Where-Object { $_.disabled }) {
                    Write-Warning ("Disabled Run Profile '$($runProfile.runProfileName)': the refresh removed partition " +
                        "'$($runProfile.partitionName)', which it targeted. Enable it again with Set-JIMRunProfile -Enabled `$true " +
                        "and -PartitionId or -AllSelectedPartitions.")
                }

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
