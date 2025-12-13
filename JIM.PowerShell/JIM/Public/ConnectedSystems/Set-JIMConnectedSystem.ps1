function Set-JIMConnectedSystem {
    <#
    .SYNOPSIS
        Updates an existing Connected System in JIM.

    .DESCRIPTION
        Updates the name, description, and/or setting values of an existing Connected System.
        Only the parameters provided will be updated.

    .PARAMETER Id
        The unique identifier of the Connected System to update.

    .PARAMETER InputObject
        Connected System object to update (from pipeline).

    .PARAMETER Name
        The new name for the Connected System.

    .PARAMETER Description
        The new description for the Connected System.

    .PARAMETER SettingValues
        A hashtable of setting values to update, where keys are setting IDs and values are
        hashtables with stringValue, intValue, or checkboxValue properties.

    .PARAMETER PassThru
        If specified, returns the updated Connected System object.

    .OUTPUTS
        If -PassThru is specified, returns the updated Connected System object.

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
        Set-JIMConnectedSystem -Id 1 -SettingValues $settings

        Updates multiple setting values for the Connected System.

    .EXAMPLE
        Get-JIMConnectedSystem -Id 1 | Set-JIMConnectedSystem -Name "Renamed"

        Updates a Connected System from the pipeline.

    .LINK
        Get-JIMConnectedSystem
        New-JIMConnectedSystem
        Remove-JIMConnectedSystem
    #>
    [CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'Medium', DefaultParameterSetName = 'ById')]
    [OutputType([PSCustomObject])]
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

        [switch]$PassThru
    )

    process {
        # Check connection first
        if (-not $script:JIMConnection) {
            Write-Error "Not connected to JIM. Use Connect-JIM first."
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

        if ($body.Count -eq 0) {
            Write-Warning "No updates specified."
            return
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
