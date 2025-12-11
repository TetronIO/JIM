function New-JIMSyncRule {
    <#
    .SYNOPSIS
        Creates a new Synchronisation Rule in JIM.

    .DESCRIPTION
        Creates a sync rule that defines how data flows between a Connected System and the Metaverse.
        For Import rules, set -ProjectToMetaverse to create Metaverse objects from imported data.
        For Export rules, set -ProvisionToConnectedSystem to create Connected System objects.

    .PARAMETER Name
        The name for the Sync Rule.

    .PARAMETER ConnectedSystemId
        The ID of the Connected System this rule applies to.

    .PARAMETER ConnectedSystemName
        The name of the Connected System this rule applies to. Must be an exact match.

    .PARAMETER ConnectedSystemObjectTypeId
        The ID of the Connected System Object Type.

    .PARAMETER MetaverseObjectTypeId
        The ID of the Metaverse Object Type.

    .PARAMETER Direction
        The direction of the sync rule: Import (from Connected System to Metaverse) or
        Export (from Metaverse to Connected System).

    .PARAMETER ProjectToMetaverse
        For Import rules, if specified, objects will be projected (created) in the Metaverse.

    .PARAMETER ProvisionToConnectedSystem
        For Export rules, if specified, objects will be provisioned (created) in the Connected System.

    .PARAMETER Enabled
        Whether the sync rule is enabled. Defaults to $true.

    .PARAMETER PassThru
        If specified, returns the created Sync Rule object.

    .OUTPUTS
        If -PassThru is specified, returns the created Sync Rule object.

    .EXAMPLE
        New-JIMSyncRule -Name "Import Users" -ConnectedSystemId 1 -ConnectedSystemObjectTypeId 1 -MetaverseObjectTypeId 1 -Direction Import -ProjectToMetaverse

        Creates an import sync rule that projects users to the Metaverse.

    .EXAMPLE
        New-JIMSyncRule -Name "Import Users" -ConnectedSystemName 'Contoso AD' -ConnectedSystemObjectTypeId 1 -MetaverseObjectTypeId 1 -Direction Import -ProjectToMetaverse

        Creates an import sync rule using the Connected System name.

    .EXAMPLE
        New-JIMSyncRule -Name "Export Users to AD" -ConnectedSystemId 2 -ConnectedSystemObjectTypeId 1 -MetaverseObjectTypeId 1 -Direction Export -ProvisionToConnectedSystem -PassThru

        Creates an export sync rule that provisions users to the Connected System.

    .LINK
        Get-JIMSyncRule
        Set-JIMSyncRule
        Remove-JIMSyncRule
    #>
    [CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'Medium', DefaultParameterSetName = 'ById')]
    [OutputType([PSCustomObject])]
    param(
        [Parameter(Mandatory, Position = 0)]
        [ValidateNotNullOrEmpty()]
        [string]$Name,

        [Parameter(Mandatory, ParameterSetName = 'ById')]
        [int]$ConnectedSystemId,

        [Parameter(Mandatory, ParameterSetName = 'ByName')]
        [string]$ConnectedSystemName,

        [Parameter(Mandatory)]
        [int]$ConnectedSystemObjectTypeId,

        [Parameter(Mandatory)]
        [int]$MetaverseObjectTypeId,

        [Parameter(Mandatory)]
        [ValidateSet('Import', 'Export')]
        [string]$Direction,

        [switch]$ProjectToMetaverse,

        [switch]$ProvisionToConnectedSystem,

        [bool]$Enabled = $true,

        [switch]$PassThru
    )

    process {
        # Check connection first
        if (-not $script:JIMConnection) {
            Write-Error "Not connected to JIM. Use Connect-JIM first."
            return
        }

        # Resolve ConnectedSystemName to ConnectedSystemId if specified
        if ($PSBoundParameters.ContainsKey('ConnectedSystemName')) {
            $connectedSystem = Resolve-JIMConnectedSystem -Name $ConnectedSystemName
            $ConnectedSystemId = $connectedSystem.id
        }

        if ($PSCmdlet.ShouldProcess($Name, "Create Sync Rule")) {
            Write-Verbose "Creating Sync Rule: $Name"

            # Map direction string to API enum value
            $directionValue = switch ($Direction) {
                'Import' { 1 }
                'Export' { 2 }
            }

            $body = @{
                name = $Name
                connectedSystemId = $ConnectedSystemId
                connectedSystemObjectTypeId = $ConnectedSystemObjectTypeId
                metaverseObjectTypeId = $MetaverseObjectTypeId
                direction = $directionValue
                enabled = $Enabled
            }

            if ($ProjectToMetaverse) {
                $body.projectToMetaverse = $true
            }

            if ($ProvisionToConnectedSystem) {
                $body.provisionToConnectedSystem = $true
            }

            try {
                $result = Invoke-JIMApi -Endpoint "/api/v1/synchronisation/sync-rules" -Method 'POST' -Body $body

                Write-Verbose "Created Sync Rule: $($result.id) ($($result.name))"

                if ($PassThru) {
                    $result
                }
            }
            catch {
                Write-Error "Failed to create Sync Rule: $_"
            }
        }
    }
}
