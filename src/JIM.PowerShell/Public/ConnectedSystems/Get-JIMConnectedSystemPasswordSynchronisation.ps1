# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Get-JIMConnectedSystemPasswordSynchronisation {
    <#
    .SYNOPSIS
        Gets a Connected System's Password Synchronisation configuration.

    .DESCRIPTION
        Reports whether a Connected System receives synchronised passwords, and the settings governing how
        hard JIM tries to deliver them.

        A Connected System that has never been configured is reported with Configured set to false and JIM's
        defaults in the remaining fields, rather than as an error: that is exactly how such a system behaves,
        and a caller comparing settings across systems should not have to special-case the untouched ones.

        ConnectorSupportsPasswordSet says whether a configuration can be saved here at all. Password
        Synchronisation is only available on Connected Systems whose Connector can set passwords.

        Nothing returned here carries a password. Queued password changes are held encrypted and are never
        returned by any surface.

    .PARAMETER Id
        The unique identifier of the Connected System.

    .PARAMETER InputObject
        Connected System object to read the configuration from (from pipeline).

    .OUTPUTS
        An object with the following properties:

        - configured                   [bool]     Whether Password Synchronisation has been configured here
        - connectorSupportsPasswordSet [bool]     Whether this Connector can set passwords at all
        - enabled                      [bool]     Whether queued password changes are delivered
        - targetObjectTypeId           [int]      The Object Type that receives passwords
        - targetObjectTypeName         [string]   Its name
        - maxRetries                   [int]      Attempts before parking; 0 means use JIM's default
        - effectiveMaxRetries          [int]      The retry count actually applied
        - retryBackoffBase             [timespan] The first retry interval; 0 means use JIM's default
        - effectiveRetryBackoffBase    [timespan] The backoff base actually applied
        - requireSecureTransport       [bool]     Whether an unencrypted connection is refused
        - effectiveTimeToLive          [timespan] How long a queued change waits before it expires

    .EXAMPLE
        Get-JIMConnectedSystemPasswordSynchronisation -Id 3

        Shows whether Connected System 3 receives synchronised passwords.

    .EXAMPLE
        Get-JIMConnectedSystem -All | ForEach-Object {
            $p = Get-JIMConnectedSystemPasswordSynchronisation -Id $_.Id
            [PSCustomObject]@{ System = $_.Name; Configured = $p.configured; Enabled = $p.enabled }
        }

        Audits which Connected Systems are set up for Password Synchronisation, and which are switched on.

    .LINK
        Set-JIMConnectedSystemPasswordSynchronisation
        Get-JIMConnectedSystemPasswordPolicy
    #>
    [CmdletBinding(DefaultParameterSetName = 'ById')]
    [OutputType([PSCustomObject])]
    param(
        [Parameter(Mandatory, ParameterSetName = 'ById', ValueFromPipelineByPropertyName)]
        [int]$Id,

        [Parameter(Mandatory, ParameterSetName = 'ByInputObject', ValueFromPipeline)]
        [PSCustomObject]$InputObject
    )

    process {
        if (-not $script:JIMConnection) {
            Write-Error "You are not connected to JIM. Run Connect-JIM -Url <your JIM URL> to authenticate, then try again."
            return
        }

        $connectedSystemId = if ($PSCmdlet.ParameterSetName -eq 'ByInputObject') { $InputObject.Id } else { $Id }

        try {
            Invoke-JIMApi -Endpoint "/api/v1/synchronisation/connected-systems/$connectedSystemId/password-synchronisation" -Method 'GET'
        }
        catch {
            Write-Error "Failed to get the Password Synchronisation configuration for Connected System ${connectedSystemId}: $_"
        }
    }
}
