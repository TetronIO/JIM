# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Reset-JIMSystem {
    <#
    .SYNOPSIS
        Wipes all customer data and configuration from JIM.

    .DESCRIPTION
        Performs a factory reset against the connected JIM instance, removing all
        customer-configured Connected Systems, Sync Rules, Schedules, Activities,
        Pending Exports, Metaverse Objects, custom Metaverse Object Types, custom
        Metaverse Attributes, custom Roles, custom Connector Definitions, custom
        Predefined Searches, custom Example Data Sets, customer-created API Keys,
        and Trusted Certificates.

        Preserved by the reset:
          - Database schema and EF Core migration history
          - Built-in Metaverse Attributes and Object Types (BuiltIn = true)
          - Built-in Roles (Administrator, Viewer, etc.)
          - Built-in Connector Definitions, Example Data Sets/Templates, Predefined Searches
          - The singleton Service Settings record
          - Infrastructure API Keys (IsInfrastructureKey = true)

        The reset is refused (HTTP 409 / non-terminating PowerShell error) when any
        Activity is currently InProgress. Wait for activities to finish or cancel
        them before retrying.

        Files stored under the connector files mount (typically /connector-files) are
        NOT wiped by this cmdlet. Remove them out-of-band if a clean filesystem is
        required.

        This operation is destructive and cannot be undone. By default the cmdlet
        prompts for confirmation; pass -Force to suppress.

    .PARAMETER Force
        Suppresses the confirmation prompt.

    .OUTPUTS
        A PSCustomObject containing the counts of removed entities.

    .EXAMPLE
        Reset-JIMSystem

        Prompts for confirmation, then wipes all customer data and configuration.

    .EXAMPLE
        Reset-JIMSystem -Force

        Wipes all customer data and configuration without prompting.

    .EXAMPLE
        $result = Reset-JIMSystem -Force
        "Removed $($result.connectedSystemsRemoved) connected systems"

        Captures the result and reports on what was removed.

    .LINK
        Get-JIMHealth
        Clear-JIMConnectedSystem
    #>
    [CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
    [OutputType([PSCustomObject])]
    param(
        [switch]$Force
    )

    process {
        if (-not $script:JIMConnection) {
            Write-Error "Not connected to JIM. Use Connect-JIM first."
            return
        }

        $target = $script:JIMConnection.BaseUri ?? 'the connected JIM instance'

        if ($Force -or $PSCmdlet.ShouldProcess($target, "Factory reset: wipe ALL customer data and configuration")) {
            Write-Verbose "Initiating factory reset against $target"

            try {
                $result = Invoke-JIMApi -Endpoint '/api/v1/system/reset' -Method 'POST'
                Write-Verbose "Factory reset complete."
                $result
            }
            catch {
                Write-Error "Factory reset failed: $_"
            }
        }
    }
}
