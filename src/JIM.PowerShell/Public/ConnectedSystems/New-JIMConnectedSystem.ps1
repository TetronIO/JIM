# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function New-JIMConnectedSystem {
    <#
    .SYNOPSIS
        Creates a Connected System.

    .DESCRIPTION
        Creates a Connected System that uses a Connector, with that Connector's default settings. Change the
        settings afterwards with Set-JIMConnectedSystem.

    .PARAMETER Name
        The name of the Connected System.

    .PARAMETER Description
        What the Connected System is for, for administrators.

    .PARAMETER ConnectorDefinitionId
        The ID of the Connector the Connected System uses; Get-JIMConnectorDefinition lists them.

    .PARAMETER ChangeReason
        Why the change was made, recorded in the Connected System's change history.

    .PARAMETER PassThru
        Returns the Connected System created.

    .OUTPUTS
        With -PassThru, the Connected System created; otherwise nothing.

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
    [OutputType('JIM.ConnectedSystemDetailDto')]
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
