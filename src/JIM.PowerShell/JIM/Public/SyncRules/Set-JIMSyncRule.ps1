function Set-JIMSyncRule {
    <#
    .SYNOPSIS
        Updates an existing Synchronisation Rule in JIM.

    .DESCRIPTION
        Updates the properties of an existing Sync Rule.
        Only the parameters provided will be updated.

    .PARAMETER Id
        The unique identifier of the Sync Rule to update.

    .PARAMETER InputObject
        Sync Rule object to update (from pipeline).

    .PARAMETER Name
        The new name for the Sync Rule.

    .PARAMETER Enable
        Enables the Sync Rule.

    .PARAMETER Disable
        Disables the Sync Rule.

    .PARAMETER ProjectToMetaverse
        For Import rules, sets whether objects will be projected to the Metaverse.

    .PARAMETER ProvisionToConnectedSystem
        For Export rules, sets whether objects will be provisioned to the Connected System.

    .PARAMETER PassThru
        If specified, returns the updated Sync Rule object.

    .OUTPUTS
        If -PassThru is specified, returns the updated Sync Rule object.

    .EXAMPLE
        Set-JIMSyncRule -Id 1 -Name "Updated Rule Name"

        Updates the name of the Sync Rule with ID 1.

    .EXAMPLE
        Set-JIMSyncRule -Id 1 -Disable

        Disables the Sync Rule with ID 1.

    .EXAMPLE
        Set-JIMSyncRule -Id 1 -Enable -PassThru

        Enables the Sync Rule and returns the updated object.

    .EXAMPLE
        Get-JIMSyncRule -Id 1 | Set-JIMSyncRule -ProjectToMetaverse $true

        Updates a Sync Rule from the pipeline to enable projection.

    .LINK
        Get-JIMSyncRule
        New-JIMSyncRule
        Remove-JIMSyncRule
    #>
    [CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'Medium', DefaultParameterSetName = 'ById')]
    [OutputType([PSCustomObject])]
    param(
        [Parameter(Mandatory, ParameterSetName = 'ById', ValueFromPipelineByPropertyName)]
        [Parameter(Mandatory, ParameterSetName = 'Enable', ValueFromPipelineByPropertyName)]
        [Parameter(Mandatory, ParameterSetName = 'Disable', ValueFromPipelineByPropertyName)]
        [int]$Id,

        [Parameter(Mandatory, ParameterSetName = 'ByInputObject', ValueFromPipeline)]
        [PSCustomObject]$InputObject,

        [Parameter()]
        [ValidateNotNullOrEmpty()]
        [string]$Name,

        [Parameter(Mandatory, ParameterSetName = 'Enable')]
        [switch]$Enable,

        [Parameter(Mandatory, ParameterSetName = 'Disable')]
        [switch]$Disable,

        [Parameter()]
        [bool]$ProjectToMetaverse,

        [Parameter()]
        [bool]$ProvisionToConnectedSystem,

        [switch]$PassThru
    )

    process {
        # Check connection first
        if (-not $script:JIMConnection) {
            Write-Error "Not connected to JIM. Use Connect-JIM first."
            return
        }

        $ruleId = if ($InputObject) { $InputObject.id } else { $Id }

        # Build update body
        $body = @{}

        if ($Name) {
            $body.name = $Name
        }

        if ($Enable) {
            $body.enabled = $true
        }
        elseif ($Disable) {
            $body.enabled = $false
        }

        if ($PSBoundParameters.ContainsKey('ProjectToMetaverse')) {
            $body.projectToMetaverse = $ProjectToMetaverse
        }

        if ($PSBoundParameters.ContainsKey('ProvisionToConnectedSystem')) {
            $body.provisionToConnectedSystem = $ProvisionToConnectedSystem
        }

        if ($body.Count -eq 0) {
            Write-Warning "No updates specified."
            return
        }

        $displayName = $Name ?? $ruleId

        if ($PSCmdlet.ShouldProcess($displayName, "Update Sync Rule")) {
            Write-Verbose "Updating Sync Rule: $ruleId"

            try {
                $result = Invoke-JIMApi -Endpoint "/api/v1/synchronisation/sync-rules/$ruleId" -Method 'PUT' -Body $body

                Write-Verbose "Updated Sync Rule: $ruleId"

                if ($PassThru) {
                    $result
                }
            }
            catch {
                Write-Error "Failed to update Sync Rule: $_"
            }
        }
    }
}
