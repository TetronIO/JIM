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

        [switch]$PassThru
    )

    process {
        # Check connection first
        if (-not $script:JIMConnection) {
            Write-Error "You are not connected to JIM. Run Connect-JIM -Url <your JIM URL> to authenticate, then try again."
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

        if ($PSCmdlet.ShouldProcess("Connected System $systemId", "Import Schema")) {
            Write-Verbose "Importing schema for Connected System: $systemId"

            try {
                $result = Invoke-JIMApi -Endpoint "/api/v1/synchronisation/connected-systems/$systemId/import-schema" -Method 'POST'

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
