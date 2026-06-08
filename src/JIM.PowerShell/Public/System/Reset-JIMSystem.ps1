# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Reset-JIMSystem {
    <#
    .SYNOPSIS
        Wipes all data and configuration from JIM.

    .DESCRIPTION
        Performs a factory reset against the connected JIM instance, removing all
        configured Connected Systems, Sync Rules, Schedules, Activities,
        Pending Exports, Metaverse Objects, custom Metaverse Object Types, custom
        Metaverse Attributes, custom Roles, custom Connector Definitions, custom
        Predefined Searches, custom Example Data Sets, non-infrastructure API Keys,
        and Trusted Certificates.

        By default the Metaverse Objects holding the built-in Administrator role are preserved,
        so you are not locked out of the portal, and a Reset Activity recording who initiated the
        wipe is created. Use -IncludeAdministrators to remove those administrator identities as well,
        leaving a true brand-new install. Every existing portal session is invalidated by the reset.

        Preserved by the reset:
          - Database schema and EF Core migration history
          - Built-in Metaverse Attributes and Object Types (BuiltIn = true)
          - Built-in Roles (Administrator, Viewer, etc.)
          - Built-in Connector Definitions, Example Data Sets/Templates, Predefined Searches
          - The singleton Service Settings record
          - Infrastructure API Keys (IsInfrastructureKey = true)
          - Metaverse Objects holding the Administrator role (unless -IncludeAdministrators)

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

    .PARAMETER IncludeAdministrators
        Also removes the Metaverse Objects holding the built-in Administrator role, leaving a true
        brand-new install. By default these are preserved so you are not locked out of the portal.
        When set with no initial administrator configured (JIM_SSO_INITIAL_ADMIN), the reset is
        refused unless -AcknowledgeAdministratorLockout is also supplied.

    .PARAMETER AcknowledgeAdministratorLockout
        Acknowledges the portal lockout risk so an administrator-inclusive wipe may proceed even when
        no initial administrator is configured (for example when access is retained via the
        infrastructure API key). Ignored unless -IncludeAdministrators is also set.

    .OUTPUTS
        A PSCustomObject containing the counts of removed entities.

    .EXAMPLE
        Reset-JIMSystem

        Prompts for confirmation, then wipes all data and configuration, preserving administrators.

    .EXAMPLE
        Reset-JIMSystem -Force

        Wipes all data and configuration without prompting, preserving administrators.

    .EXAMPLE
        Reset-JIMSystem -Force -IncludeAdministrators -AcknowledgeAdministratorLockout

        Wipes everything including administrator identities (a true brand-new install).

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
        [switch]$Force,
        [switch]$IncludeAdministrators,
        [switch]$AcknowledgeAdministratorLockout
    )

    process {
        if (-not $script:JIMConnection) {
            Write-Error "Not connected to JIM. Use Connect-JIM first."
            return
        }

        $target = $script:JIMConnection.BaseUri ?? 'the connected JIM instance'

        $scope = if ($IncludeAdministrators) {
            "Factory reset: wipe ALL data and configuration, INCLUDING administrators"
        } else {
            "Factory reset: wipe ALL data and configuration (administrators preserved)"
        }

        if ($Force -or $PSCmdlet.ShouldProcess($target, $scope)) {
            Write-Verbose "Initiating factory reset against $target (IncludeAdministrators=$IncludeAdministrators)"

            $body = @{
                includeAdministrators          = [bool]$IncludeAdministrators
                acknowledgeAdministratorLockout = [bool]$AcknowledgeAdministratorLockout
            }

            try {
                $result = Invoke-JIMApi -Endpoint '/api/v1/system/reset' -Method 'POST' -Body $body
                Write-Verbose "Factory reset complete."
                $result
            }
            catch {
                Write-Error "Factory reset failed: $_"
            }
        }
    }
}
