# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Set-JIMConnectedSystemPasswordSynchronisation {
    <#
    .SYNOPSIS
        Creates or updates a Connected System's Password Synchronisation configuration.

    .DESCRIPTION
        Configures whether a Connected System receives synchronised passwords, and how hard JIM tries to
        deliver them. Running this against a Connected System with no configuration creates one, in which case
        -TargetObjectType is required.

        Enabling and configuring are separate on purpose. A configured but disabled Connected System keeps
        accumulating queued password changes rather than discarding them, and enabling it delivers what
        accumulated without further intervention. That is why there is no cmdlet to remove a configuration:
        removing it would throw the queue away, whereas disabling it is reversible.

        Only Connected Systems whose Connector can set passwords may be configured. Check
        ConnectorSupportsPasswordSet with Get-JIMConnectedSystemPasswordSynchronisation first.

        Omitted parameters leave the stored value unchanged.

    .PARAMETER Id
        The unique identifier of the Connected System.

    .PARAMETER InputObject
        Connected System object to configure (from pipeline).

    .PARAMETER Enabled
        Whether to deliver queued password changes to this Connected System. Enabling drains whatever
        accumulated while it was off.

    .PARAMETER TargetObjectType
        The identifier of the Connected System Object Type holding this system's user accounts. Required when
        creating a configuration, and it must be an Object Type selected for synchronisation.

    .PARAMETER MaxRetries
        How many delivery attempts to make before parking a queued change for an administrator. Use 0 for
        JIM's default.

    .PARAMETER RetryBackoffBase
        The first retry interval; each subsequent attempt waits twice as long, capped at the change's time to
        live. Use 0 for JIM's default.

    .PARAMETER ChangeReason
        An optional reason recorded against the Connected System's configuration change history.

    .PARAMETER PassThru
        Return the updated configuration.

    .EXAMPLE
        Set-JIMConnectedSystemPasswordSynchronisation -Id 3 -TargetObjectType 7 -Enabled $false

        Configures Connected System 3 for Password Synchronisation without switching it on yet, so the
        configuration can be staged ahead of a change window.

    .EXAMPLE
        Set-JIMConnectedSystemPasswordSynchronisation -Id 3 -Enabled $true -ChangeReason 'CHG0041288'

        Switches Password Synchronisation on, delivering everything queued while it was off.

    .EXAMPLE
        Set-JIMConnectedSystemPasswordSynchronisation -Id 3 -MaxRetries 10 -PassThru

        Requires an encrypted connection for password delivery to this system, and allows ten attempts before
        a change is parked.

    .LINK
        Get-JIMConnectedSystemPasswordSynchronisation
        Set-JIMConnectedSystemObjectPassword
    #>
    [CmdletBinding(DefaultParameterSetName = 'ById', SupportsShouldProcess)]
    [OutputType([PSCustomObject])]
    param(
        [Parameter(Mandatory, ParameterSetName = 'ById', ValueFromPipelineByPropertyName)]
        [int]$Id,

        [Parameter(Mandatory, ParameterSetName = 'ByInputObject', ValueFromPipeline)]
        [PSCustomObject]$InputObject,

        [Parameter()]
        [bool]$Enabled,

        [Parameter()]
        [Alias('TargetObjectTypeId')]
        [int]$TargetObjectType,

        [Parameter()]
        [int]$MaxRetries,

        [Parameter()]
        [timespan]$RetryBackoffBase,

        [Parameter()]
        [string]$ChangeReason,

        [Parameter()]
        [switch]$PassThru
    )

    process {
        if (-not $script:JIMConnection) {
            Write-Error "You are not connected to JIM. Run Connect-JIM -Url <your JIM URL> to authenticate, then try again."
            return
        }

        $connectedSystemId = if ($PSCmdlet.ParameterSetName -eq 'ByInputObject') { $InputObject.Id } else { $Id }

        # Only bound parameters are sent, so an omitted one leaves the stored value alone rather than
        # overwriting it with a PowerShell default.
        $body = @{}
        if ($PSBoundParameters.ContainsKey('Enabled')) { $body.enabled = $Enabled }
        if ($PSBoundParameters.ContainsKey('TargetObjectType')) { $body.targetObjectTypeId = $TargetObjectType }
        if ($PSBoundParameters.ContainsKey('MaxRetries')) { $body.maxRetries = $MaxRetries }
        if ($PSBoundParameters.ContainsKey('RetryBackoffBase')) { $body.retryBackoffBase = $RetryBackoffBase.ToString() }
        if ($PSBoundParameters.ContainsKey('ChangeReason')) { $body.changeReason = $ChangeReason }

        if ($body.Count -eq 0) {
            Write-Error "Nothing to change. Supply at least one setting, for example -Enabled or -TargetObjectType."
            return
        }

        $action = if ($PSBoundParameters.ContainsKey('Enabled')) {
            if ($Enabled) { 'Enable Password Synchronisation' } else { 'Disable Password Synchronisation' }
        } else {
            'Update the Password Synchronisation configuration'
        }

        if (-not $PSCmdlet.ShouldProcess("Connected System $connectedSystemId", $action)) {
            return
        }

        try {
            $result = Invoke-JIMApi `
                -Endpoint "/api/v1/synchronisation/connected-systems/$connectedSystemId/password-synchronisation" `
                -Method 'PUT' `
                -Body $body

            if ($PassThru) {
                $result
            }
        }
        catch {
            Write-Error "Failed to set the Password Synchronisation configuration for Connected System ${connectedSystemId}: $_"
        }
    }
}
