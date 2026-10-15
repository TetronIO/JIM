# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Set-JIMConnectedSystem {
    <#
    .SYNOPSIS
        Changes a Connected System.

    .DESCRIPTION
        Changes a Connected System's name, description, settings, and how it exports and treats references and
        passwords. What you leave out stays as it is.

    .PARAMETER Id
        The ID of the Connected System to change.

    .PARAMETER InputObject
        A Connected System from Get-JIMConnectedSystem, through the pipeline.

    .PARAMETER Name
        The new name of the Connected System.

    .PARAMETER Description
        What the Connected System is for, for administrators.

    .PARAMETER SettingValues
        The Connector settings to change, as a hashtable keyed by setting ID (from Get-JIMConnectorDefinition) whose
        values are hashtables holding a stringValue, intValue or checkboxValue.

    .PARAMETER MaxExportParallelism
        How many export batches run at once, where the Connector supports exporting in parallel. 1 exports one batch
        at a time.

    .PARAMETER RequireSecureTransport
        Refuse to send a password to this Connected System over a connection JIM cannot confirm is encrypted.
        Governs every password JIM sends here: the first password on an account it provisions, one set by hand,
        and a synchronised password change. Off by default, because a signed and sealed bind is a legitimate
        encrypted alternative that cannot be detected from the Connected System's settings alone. Pass
        -RequireSecureTransport to turn it on and -RequireSecureTransport:$false to turn it off. Nothing is
        discarded when JIM refuses: queued password changes wait, and accounts stay owed their first password.

    .PARAMETER InitialPasswordTimeToLive
        How long an account provisioned into the Connected System stays owed an initial password before JIM records
        an expiry and stops trying, as a TimeSpan. Seven days until one is set.
        Raise it ahead of an outage longer than the current window, or every account provisioned while the Connected
        System is unreachable expires without a password.

    .PARAMETER UnresolvedReferenceHandling
        How an imported reference value that cannot be resolved to a Connected System Object is treated: Error fails
        the object's Run Profile execution item; Warn completes the Activity with a warning counting them instead;
        Ignore records neither, though the references are still logged and shown on the Connected System Object.

    .PARAMETER ChangeReason
        Why the change was made, recorded in the Connected System's change history.

    .PARAMETER PassThru
        Returns the Connected System as changed.

    .OUTPUTS
        With -PassThru, the Connected System as changed; otherwise nothing.

    .EXAMPLE
        Set-JIMConnectedSystem -Id 1 -Name "Updated Name"

        Updates the name of the Connected System with ID 1.

    .EXAMPLE
        Set-JIMConnectedSystem -Id 1 -Description "New description" -PassThru

        Updates the description and returns the updated object.

    .EXAMPLE
        $settings = @{
            1 = @{ stringValue = "server.example.com" }
            2 = @{ intValue = 389 }
            3 = @{ checkboxValue = $true }
        }
        Set-JIMConnectedSystem -Id 1 -SettingValues $settings -ChangeReason "Point at new DC (CHG0099)"

        Updates multiple setting values for the Connected System and records a reason against its change history.

    .EXAMPLE
        Get-JIMConnectedSystem -Id 1 | Set-JIMConnectedSystem -Name "Renamed"

        Updates a Connected System from the pipeline.

    .EXAMPLE
        Set-JIMConnectedSystem -Id 1 -MaxExportParallelism 4

        Enables parallel export batch processing with up to 4 concurrent batches.

    .EXAMPLE
        Set-JIMConnectedSystem -Id 1 -RequireSecureTransport

        Stops JIM sending any password to this Connected System unless it can confirm the connection is
        encrypted. Turn it back off with -RequireSecureTransport:$false.

    .EXAMPLE
        Set-JIMConnectedSystem -Id 1 -InitialPasswordTimeToLive (New-TimeSpan -Days 30)

        Gives accounts provisioned into this Connected System thirty days to receive their initial password,
        rather than the default seven.

    .EXAMPLE
        Set-JIMConnectedSystem -Id 1 -UnresolvedReferenceHandling Ignore

        Suppresses errors and warnings for import-time unresolved references (e.g. group members outside the
        configured Container Scope), while still logging each occurrence.

    .LINK
        Get-JIMConnectedSystem
        New-JIMConnectedSystem
        Remove-JIMConnectedSystem
    #>
    [CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'Medium', DefaultParameterSetName = 'ById')]
    [OutputType('JIM.ConnectedSystemDetailDto')]
    param(
        [Parameter(Mandatory, ParameterSetName = 'ById', ValueFromPipelineByPropertyName)]
        [int]$Id,

        [Parameter(Mandatory, ParameterSetName = 'ByInputObject', ValueFromPipeline)]
        [PSCustomObject]$InputObject,

        [Parameter()]
        [ValidateNotNullOrEmpty()]
        [string]$Name,

        [Parameter()]
        [string]$Description,

        [Parameter()]
        [hashtable]$SettingValues,

        [Parameter()]
        [ValidateRange(1, 16)]
        [int]$MaxExportParallelism,

        [Parameter()]
        [switch]$RequireSecureTransport,

        [Parameter()]
        [timespan]$InitialPasswordTimeToLive,

        [Parameter()]
        [ValidateSet('Error', 'Warn', 'Ignore')]
        [string]$UnresolvedReferenceHandling,

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

        $systemId = if ($InputObject) { $InputObject.id } else { $Id }

        # Build update body
        $body = @{}

        if ($Name) {
            $body.name = $Name
        }

        if ($PSBoundParameters.ContainsKey('Description')) {
            $body.description = $Description
        }

        if ($SettingValues) {
            # Convert hashtable keys to strings for JSON serialization
            # JSON requires string keys, but PowerShell hashtables can have integer keys
            $stringKeyedSettings = @{}
            foreach ($key in $SettingValues.Keys) {
                $stringKeyedSettings[$key.ToString()] = $SettingValues[$key]
            }
            $body.settingValues = $stringKeyedSettings
        }

        if ($PSBoundParameters.ContainsKey('MaxExportParallelism')) {
            $body.maxExportParallelism = $MaxExportParallelism
        }

        if ($PSBoundParameters.ContainsKey('RequireSecureTransport')) {
            # A switch, so -RequireSecureTransport:$false is how it is turned off; the bound value carries which.
            $body.requireSecureTransport = [bool]$RequireSecureTransport
        }

        if ($PSBoundParameters.ContainsKey('InitialPasswordTimeToLive')) {
            # Serialised in the ISO 8601 / .NET constant format the API's TimeSpan binder accepts.
            $body.initialPasswordTimeToLive = $InitialPasswordTimeToLive.ToString('c')
        }

        if ($PSBoundParameters.ContainsKey('UnresolvedReferenceHandling')) {
            $body.unresolvedReferenceHandling = $UnresolvedReferenceHandling
        }

        # A change reason alone is not an update; require at least one actual property change first.
        if ($body.Count -eq 0) {
            Write-Warning "No updates specified."
            return
        }

        if ($PSBoundParameters.ContainsKey('ChangeReason')) {
            $body.changeReason = $ChangeReason
        }

        $displayName = $Name ?? $systemId

        if ($PSCmdlet.ShouldProcess($displayName, "Update Connected System")) {
            Write-Verbose "Updating Connected System: $systemId"

            try {
                $result = Invoke-JIMApi -Endpoint "/api/v1/synchronisation/connected-systems/$systemId" -Method 'PUT' -Body $body

                Write-Verbose "Updated Connected System: $systemId"

                if ($PassThru) {
                    $result
                }
            }
            catch {
                Write-Error "Failed to update Connected System: $_"
            }
        }
    }
}
