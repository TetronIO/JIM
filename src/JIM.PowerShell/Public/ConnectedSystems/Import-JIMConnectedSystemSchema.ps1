# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Import-JIMConnectedSystemSchema {
    <#
    .SYNOPSIS
        Imports the schema from a Connected System.

    .DESCRIPTION
        Reads a Connected System's schema (its Object Types and their attributes) into JIM. A Connected System needs
        its schema before Synchronisation Rules can be written for it.

        A refresh never deletes on its own: additions and changed attribute definitions are applied, while Object
        Types and attributes the Connected System no longer reports are kept and flagged. Run it with -Preview first
        to see what a refresh would change, then without -Preview to apply it.

    .PARAMETER Id
        The ID of the Connected System to import the schema of.

    .PARAMETER InputObject
        A Connected System from Get-JIMConnectedSystem, through the pipeline.

    .PARAMETER Preview
        Reads the schema and returns what a refresh would change, without changing anything.
        HasRemovalsOrDefinitionChanges flags the changes that can affect existing configuration: removals and
        changed attribute definitions. HasObjectTypeClassificationChanges flags a change to how the Connected System
        classifies a selected Object Type, each also written as a warning: applying one can move existing objects to
        another Object Type, which the next import refuses as an Object Type Changed error unless a Full Import Run
        Profile allows Object Type changes (Set-JIMRunProfile -AllowObjectTypeChanges).

    .PARAMETER DisableDependents
        Applies the refresh and disables what depends on what it removes or redefines: Synchronisation Rules bound
        to a removed Object Type, and Attribute Flows reading a removed or redefined attribute, each with the reason
        recorded. Each must be enabled again by hand. -Preview names them, in its Dependents property. Cannot be
        used with -RemoveDependents.

    .PARAMETER RemoveDependents
        Applies the refresh and deletes what depends on what it removes: the Synchronisation Rules and Attribute
        Flows are deleted, and a worker task obsoletes every Connected System Object of a removed Object Type, which
        deprovisions them as any deletion would, and deletes every stored value of a removed attribute. -Preview
        shows exactly what it would take, in its Dependents and RemovalImpact properties; follow the worker task
        with Get-JIMWorkerTask. Cannot be used with -DisableDependents.

    .PARAMETER PassThru
        Returns the Connected System with its refreshed schema. -Preview returns its result without it.

    .OUTPUTS
        With -Preview, what the refresh would change; with -PassThru, the Connected System with its refreshed
        schema; otherwise nothing. Derived Attribute Flows that disabling or removing dependents leaves, or would
        leave, with a missing input are written to the warning stream, as are changed Object Type classifications.

        JIM.SchemaRefreshResultDto: With -Preview

        JIM.ConnectedSystemDetailDto: With -PassThru

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
    [OutputType('JIM.SchemaRefreshResultDto')]
    [OutputType('JIM.ConnectedSystemDetailDto')]
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
                $previewResult = Invoke-JIMApi -Endpoint "/api/v1/synchronisation/connected-systems/$systemId/import-schema/preview" -Method 'POST'

                # Derived flows that disabling or removing the dependents would leave with a missing input
                # (#1750, FR 3), worded as a prediction: the preview changes nothing.
                Write-JIMDependentDerivedFlowWarning -DependentDerivedFlows $previewResult.Dependents.DependentDerivedFlows -Prospective

                # Object Type classification changes (#2049): applying one can change which Object Type existing
                # entries resolve to at the next import, so each is written as a warning a script cannot miss.
                Write-JIMObjectTypeClassificationWarning -ChangedObjectTypeClassifications $previewResult.ChangedObjectTypeClassifications

                $previewResult
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

                # Derived flows that disabling or removing the dependents left with a missing input (#1750, FR 3):
                # reported, never blocking. Empty unless -DisableDependents or -RemoveDependents was supplied.
                Write-JIMDependentDerivedFlowWarning -DependentDerivedFlows $result.DependentDerivedFlows

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
