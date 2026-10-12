# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Get-JIMHistoryCount {
    <#
    .SYNOPSIS
        Gets the count of change history records for a connected system.

    .DESCRIPTION
        Retrieves the number of CSO change records stored for a specific connected system.
        This can help assess how much history will be affected by deleting a connected system
        or clearing its connector space.

    .PARAMETER ConnectedSystemId
        The ID of the Connected System.

    .PARAMETER ConnectedSystemName
        The name of the Connected System; it must match exactly.

    .OUTPUTS
        One object giving how many Connected System Object change records the Connected System holds.

    .EXAMPLE
        Get-JIMHistoryCount -ConnectedSystemId 1

        Gets the change record count for connected system ID 1.

    .EXAMPLE
        Get-JIMHistoryCount -ConnectedSystemName 'Contoso AD'

        Gets the change record count for the 'Contoso AD' connected system.

    .EXAMPLE
        Get-JIMConnectedSystem | ForEach-Object {
            Get-JIMHistoryCount -ConnectedSystemId $_.id
        } | Format-Table

        Gets change record counts for all connected systems and displays as a table.

    .LINK
        Get-JIMConnectedSystem
        Invoke-JIMHistoryCleanup
    #>
    [CmdletBinding(DefaultParameterSetName = 'ById')]
    [OutputType('JIM.HistoryCountResponse')]
    param(
        [Parameter(Mandatory, ParameterSetName = 'ById', ValueFromPipelineByPropertyName)]
        [Alias('Id')]
        [int]$ConnectedSystemId,

        [Parameter(Mandatory, ParameterSetName = 'ByName')]
        [string]$ConnectedSystemName
    )

    process {
        # Check connection first
        if (-not $script:JIMConnection) {
            Write-Error "You are not connected to JIM. Run Connect-JIM -Url <your JIM URL> to authenticate, then try again."
            return
        }

        # If using name, resolve to ID first
        if ($PSCmdlet.ParameterSetName -eq 'ByName') {
            try {
                $ConnectedSystemId = (Resolve-JIMConnectedSystem -Name $ConnectedSystemName).id
            }
            catch {
                Write-Error $_
                return
            }
        }

        Write-Verbose "Getting history count for Connected System ID: $ConnectedSystemId"

        try {
            $result = Invoke-JIMApi -Endpoint "/api/v1/history/connected-systems/$ConnectedSystemId/count"
            $result
        }
        catch {
            Write-Error "Failed to get history count for Connected System ID ${ConnectedSystemId}: $_"
            throw
        }
    }
}
