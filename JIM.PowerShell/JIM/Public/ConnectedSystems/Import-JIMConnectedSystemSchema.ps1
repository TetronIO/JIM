function Import-JIMConnectedSystemSchema {
    <#
    .SYNOPSIS
        Imports the schema from a Connected System.

    .DESCRIPTION
        Connects to the external system and retrieves its schema (object types and attributes).
        This is required before creating sync rules, as sync rules reference object type IDs.

        Note: This operation is destructive - it will replace any existing schema configuration.
        Any sync rules referencing removed object types/attributes will need to be updated.

    .PARAMETER Id
        The unique identifier of the Connected System to import schema for.

    .PARAMETER InputObject
        Connected System object to import schema for (from pipeline).

    .PARAMETER PassThru
        If specified, returns the updated Connected System object with imported schema.

    .OUTPUTS
        If -PassThru is specified, returns the updated Connected System object.

    .EXAMPLE
        Import-JIMConnectedSystemSchema -Id 1

        Imports the schema for Connected System with ID 1.

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

        [switch]$PassThru
    )

    process {
        # Check connection first
        if (-not $script:JIMConnection) {
            Write-Error "Not connected to JIM. Use Connect-JIM first."
            return
        }

        $systemId = if ($InputObject) { $InputObject.id } else { $Id }

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
