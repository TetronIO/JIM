# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function New-JIMConnectedSystem {
    <#
    .SYNOPSIS
        Creates a new Connected System in JIM.

    .DESCRIPTION
        Creates a new Connected System with the specified connector type. Default settings
        from the connector definition are applied automatically. Use Set-JIMConnectedSystem
        to configure the settings after creation.

    .PARAMETER Name
        The name for the Connected System.

    .PARAMETER Description
        Optional description for the Connected System.

    .PARAMETER ConnectorDefinitionId
        The ID of the ConnectorDefinition to use for this Connected System.

    .PARAMETER ChangeReason
        An optional reason for the change, recorded against this Connected System's change history.

    .PARAMETER PassThru
        If specified, returns the created Connected System object.

    .OUTPUTS
        If -PassThru is specified, returns the created Connected System object.

    .EXAMPLE
        New-JIMConnectedSystem -Name "HR System" -ConnectorDefinitionId 1

        Creates a new Connected System named "HR System" using connector definition 1.

    .EXAMPLE
        New-JIMConnectedSystem -Name "AD Connector" -Description "Active Directory" -ConnectorDefinitionId 2 -PassThru

        Creates a new Connected System with a description and returns the created object.

    .EXAMPLE
        $connDef = Get-JIMConnectorDefinition | Where-Object { $_.name -eq "CSV File" }
        New-JIMConnectedSystem -Name "CSV Import" -ConnectorDefinitionId $connDef.id -PassThru

        Creates a Connected System using a connector found by name.

    .LINK
        Get-JIMConnectedSystem
        Set-JIMConnectedSystem
        Remove-JIMConnectedSystem
        Get-JIMConnectorDefinition
    #>
    [CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'Medium')]
    [OutputType([PSCustomObject])]
    param(
        [Parameter(Mandatory, Position = 0)]
        [ValidateNotNullOrEmpty()]
        [string]$Name,

        [Parameter()]
        [string]$Description,

        [Parameter(Mandatory)]
        [int]$ConnectorDefinitionId,

        [Parameter()]
        [ValidateNotNullOrEmpty()]
        [string]$ChangeReason,

        [switch]$PassThru
    )

    process {
        # Check connection first
        if (-not $script:JIMConnection) {
            Write-Error "You are not connected to JIM. Run Connect-JIM -Url <your JIM URL> to authenticate, then try again."
            return
        }

        if ($PSCmdlet.ShouldProcess($Name, "Create Connected System")) {
            Write-Verbose "Creating Connected System: $Name with connector $ConnectorDefinitionId"

            $body = @{
                name = $Name
                connectorDefinitionId = $ConnectorDefinitionId
            }

            if ($Description) {
                $body.description = $Description
            }

            if ($PSBoundParameters.ContainsKey('ChangeReason')) {
                $body.changeReason = $ChangeReason
            }

            try {
                $result = Invoke-JIMApi -Endpoint "/api/v1/synchronisation/connected-systems" -Method 'POST' -Body $body

                Write-Verbose "Created Connected System: $($result.id) ($($result.name))"

                if ($PassThru) {
                    $result
                }
            }
            catch {
                Write-Error "Failed to create Connected System: $_"
            }
        }
    }
}
