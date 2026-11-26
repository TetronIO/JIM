# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Remove-JIMMetaverseObjectAttributeValue {
    <#
    .SYNOPSIS
        Removes values an attribute in Accumulate mode has kept on a Metaverse Object.

    .DESCRIPTION
        An attribute in Accumulate mode keeps every value its contributors have ever supplied. This cmdlet removes values
        it no longer needs, such as entries for accounts that no longer exist. Name the values by their text with -Value,
        by their value id with -ValueId, or both. The removal is all or nothing.

        A value a Synchronisation Rule still supplies is refused, naming the rule and its Connected System: that rule's
        next synchronisation would only add the value back. Change the value at its source, or take the object out of
        the rule's scope, first.

        The removal is recorded on the object's change history as yours, under an Activity, and reaches the Connected
        Systems the attribute is exported to at their next synchronisation.

    .PARAMETER MetaverseObjectId
        The ID of the Metaverse Object.

    .PARAMETER AttributeName
        The name of the attribute in Accumulate mode.

    .PARAMETER AttributeId
        The ID of the attribute in Accumulate mode.

    .PARAMETER Value
        The values to remove, by their text: text exactly as held, including case; numbers in invariant form; dates as ISO 8601; a Guid in its hyphenated form; True or False.

    .PARAMETER ValueId
        The values to remove, by the value id shown on each of the Metaverse Object's attribute values.

    .PARAMETER Force
        Suppresses the confirmation prompt.

    .OUTPUTS
        The values removed and the Activity recording the removal.

    .EXAMPLE
        Remove-JIMMetaverseObjectAttributeValue -MetaverseObjectId 'b5e1c1a2-3f4d-4e5f-8a9b-0c1d2e3f4a5b' -AttributeName 'Account Audit Log' -Value 'AD|S-1-5-21-1004|sAMAccountName=jsmith'

        Removes one kept entry from Account Audit Log after confirmation.

    .EXAMPLE
        Remove-JIMMetaverseObjectAttributeValue -MetaverseObjectId 'b5e1c1a2-3f4d-4e5f-8a9b-0c1d2e3f4a5b' -AttributeName 'Account Audit Log' -Value 'AD|S-1-5-21-1004|sAMAccountName=jsmith' -WhatIf

        Shows what would be removed, without removing it.

    .LINK
        Get-JIMMetaverseObject
        Set-JIMMetaverseAttribute
    #>
    [CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High', DefaultParameterSetName = 'ByAttributeName')]
    [OutputType('JIM.RemoveMetaverseObjectAttributeValuesResponse')]
    param(
        [Parameter(Mandatory)]
        [guid]$MetaverseObjectId,

        [Parameter(Mandatory, ParameterSetName = 'ByAttributeName')]
        [ValidateNotNullOrEmpty()]
        [string]$AttributeName,

        [Parameter(Mandatory, ParameterSetName = 'ByAttributeId')]
        [int]$AttributeId,

        [Parameter()]
        [string[]]$Value,

        [Parameter()]
        [guid[]]$ValueId,

        [switch]$Force
    )

    process {
        if (-not $script:JIMConnection) {
            Write-Error "You are not connected to JIM. Run Connect-JIM -Url <your JIM URL> to authenticate, then try again."
            return
        }

        if (-not $Value -and -not $ValueId) {
            Write-Error "Name at least one value to remove, with -Value or -ValueId."
            return
        }

        if ($PSCmdlet.ParameterSetName -eq 'ByAttributeName') {
            try {
                $attribute = Resolve-JIMMetaverseAttribute -Name $AttributeName
            }
            catch {
                Write-Error $_
                return
            }
            $AttributeId = $attribute.id
        }
        else {
            $AttributeName = "attribute $AttributeId"
        }

        $valueIdTexts = if ($ValueId) { @($ValueId | ForEach-Object { $_.ToString() }) } else { @() }
        $body = @{}
        if ($Value) { $body.values = @($Value) }
        if ($ValueId) { $body.valueIds = $valueIdTexts }

        $named = @(@($Value) + $valueIdTexts | Where-Object { $_ })
        $target = "$($named -join ', ') from $AttributeName on Metaverse Object $MetaverseObjectId"
        if (-not (Confirm-JIMAction -Cmdlet $PSCmdlet -Target $target -Action 'Remove value(s)' -Force:$Force)) {
            return
        }

        try {
            Invoke-JIMApi -Endpoint "/api/v1/metaverse/objects/$MetaverseObjectId/attributes/$AttributeId/values" -Method 'DELETE' -Body $body
        }
        catch {
            Write-Error "Failed to remove values from $AttributeName on Metaverse Object ${MetaverseObjectId}: $_"
        }
    }
}
