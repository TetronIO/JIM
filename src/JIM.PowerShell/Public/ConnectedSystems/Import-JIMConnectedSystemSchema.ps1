# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Import-JIMConnectedSystemSchema {
    <#
    .SYNOPSIS
        Imports the schema from a Connected System.

    .DESCRIPTION
        Connects to the external system and retrieves its schema (object types and attributes).
        This is required before creating Synchronisation Rules, as Synchronisation Rules reference object type IDs.

        A refresh never deletes: additions and attribute definition updates are applied, while object types and
        attributes the Connected System no longer reports are retained in JIM and flagged. Use -Preview first to
        see what a refresh would change without applying anything, then run again without -Preview to apply.

    .PARAMETER Id
        The unique identifier of the Connected System to import schema for.

    .PARAMETER InputObject
        Connected System object to import schema for (from pipeline).

    .PARAMETER Preview
        If specified, retrieves the schema and returns what a refresh would change without persisting anything.
        The result's HasRemovalsOrDefinitionChanges property flags removals and attribute definition changes,
        which are the changes that can affect existing configuration; additions alone cannot.

    .PARAMETER DisableDependents
        If specified, the refresh is applied with its dependents disabled: Synchronisation Rules bound to a
        removed object type and attribute mappings reading a removed or redefined attribute (directly or as an
        expression input) are disabled with a recorded reason, so nothing runs against entries the Connected
        System no longer reports. Preview first with -Preview, whose result's Dependents property names what
        this option would disable. Re-enabling is a manual choice per rule or mapping. Cannot be combined with
        -RemoveDependents.

    .PARAMETER RemoveDependents
        If specified, the refresh is applied with its dependents removed. This deletes configuration and
        identity data: the invalidated Synchronisation Rules and attribute mappings are deleted, and a
        background worker task marks every Connected System Object of a removed object type Obsolete (they
        deprovision through the standard pipeline, grace periods and Metaverse Deletion Rules included) and
        deletes every stored value of a removed attribute. Always preview first with -Preview, whose result's
        Dependents and RemovalImpact properties show exactly what this option would take. Follow the queued
        removal with Get-JIMWorkerTask. Cannot be combined with -DisableDependents.

    .PARAMETER PassThru
        If specified, returns the updated Connected System object with imported schema. Not needed with -Preview,
        which always returns the preview result.

    .OUTPUTS
        With -Preview, returns the schema refresh preview result. Otherwise, if -PassThru is specified, returns
        the updated Connected System object.

    .EXAMPLE
        Import-JIMConnectedSystemSchema -Id 1

        Imports the schema for Connected System with ID 1.

    .EXAMPLE
        $preview = Import-JIMConnectedSystemSchema -Id 1 -Preview
        if (-not $preview.HasRemovalsOrDefinitionChanges) { Import-JIMConnectedSystemSchema -Id 1 -Confirm:$false }

        Previews the refresh and only applies it when nothing was removed or redefined at the Connected System.

    .EXAMPLE
        $preview = Import-JIMConnectedSystemSchema -Id 1 -Preview
        $preview.Dependents; $preview.RemovalImpact
        Import-JIMConnectedSystemSchema -Id 1 -RemoveDependents

        Reviews exactly which Synchronisation Rules and mappings a destructive refresh would delete and how many
        objects and stored values the data removal would take, then applies the refresh with its dependents
        removed. This deletes configuration and identity data across everything the preview listed; the data
        removal runs as a background worker task.

    .EXAMPLE
        Import-JIMConnectedSystemSchema -Id 1 -PassThru

        Imports the schema and returns the updated Connected System.

    .EXAMPLE
        Get-JIMConnectedSystem -Id 1 | Import-JIMConnectedSystemSchema -PassThru

        Imports schema for a Connected System from the pipeline.

    .EXAMPLE
        $system = New-JIMConnectedSystem -Name "HR CSV" -ConnectorDefinitionId 2 -PassThru
        Set-JIMConnectedSystem -Id $system.id -SettingValues @{ "1" = @{ stringValue = "/data/hr.csv" } }
        Import-JIMConnectedSystemSchema -Id $system.id -PassThru

        Creates a Connected System, configures it, and imports its schema.

    .LINK
        Get-JIMConnectedSystem
        New-JIMConnectedSystem
        Set-JIMConnectedSystem
    #>
    [CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'Medium', DefaultParameterSetName = 'ById')]
    [OutputType([PSCustomObject])]
    param(
        [Parameter(Mandatory, ParameterSetName = 'ById', ValueFromPipelineByPropertyName)]
        [int]$Id,

        [Parameter(Mandatory, ParameterSetName = 'ByInputObject', ValueFromPipeline)]
        [PSCustomObject]$InputObject,

        [switch]$Preview,

        [switch]$DisableDependents,

        [switch]$RemoveDependents,

        [switch]$PassThru
    )

    process {
        # Check connection first
        if (-not $script:JIMConnection) {
            Write-Error "You are not connected to JIM. Run Connect-JIM -Url <your JIM URL> to authenticate, then try again."
            return
        }

        if ($DisableDependents -and $RemoveDependents) {
            Write-Error "-DisableDependents and -RemoveDependents are mutually exclusive; a refresh takes one posture."
            return
        }

        $systemId = if ($InputObject) { $InputObject.id } else { $Id }

        if ($Preview) {
            # A preview persists nothing and records nothing, so it bypasses ShouldProcess: there is no action to
            # confirm, and the result is the whole point, so it is returned without -PassThru.
            Write-Verbose "Previewing schema refresh for Connected System: $systemId"
            try {
                Invoke-JIMApi -Endpoint "/api/v1/synchronisation/connected-systems/$systemId/import-schema/preview" -Method 'POST'
            }
            catch {
                Write-Error "Failed to preview schema refresh: $_"
            }
            return
        }

        $action = if ($RemoveDependents) { "Import Schema and remove dependent configuration and data" } else { "Import Schema" }
        if ($PSCmdlet.ShouldProcess("Connected System $systemId", $action)) {
            Write-Verbose "Importing schema for Connected System: $systemId"

            try {
                $invokeParams = @{ Endpoint = "/api/v1/synchronisation/connected-systems/$systemId/import-schema"; Method = 'POST' }
                if ($DisableDependents) { $invokeParams.Body = @{ disableDependents = $true } }
                if ($RemoveDependents) { $invokeParams.Body = @{ removeDependents = $true } }
                $result = Invoke-JIMApi @invokeParams

                $objectTypeCount = if ($result.objectTypes) { $result.objectTypes.Count } else { 0 }
                Write-Verbose "Schema imported for Connected System: $systemId ($objectTypeCount object types)"

                if ($PassThru) {
                    $result
                }
            }
            catch {
                Write-Error "Failed to import schema: $_"
            }
        }
    }
}
