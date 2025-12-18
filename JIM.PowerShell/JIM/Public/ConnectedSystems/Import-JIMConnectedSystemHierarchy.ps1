function Import-JIMConnectedSystemHierarchy {
    <#
    .SYNOPSIS
        Imports the hierarchy (partitions and containers) from a Connected System.

    .DESCRIPTION
        Connects to the external system and retrieves its partition and container hierarchy.
        For LDAP connectors, this retrieves naming contexts and organisational units.

        After importing the hierarchy, you can select which partitions and containers to include
        in import operations using Set-JIMConnectedSystemPartition and Set-JIMConnectedSystemContainer.

        Note: This operation is destructive - it will replace any existing partition/container configuration.
        Any partition/container selections will be lost.

    .PARAMETER Id
        The unique identifier of the Connected System to import hierarchy for.

    .PARAMETER InputObject
        Connected System object to import hierarchy for (from pipeline).

    .PARAMETER PassThru
        If specified, returns the updated Connected System object with imported hierarchy.

    .OUTPUTS
        If -PassThru is specified, returns the updated Connected System object.

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
