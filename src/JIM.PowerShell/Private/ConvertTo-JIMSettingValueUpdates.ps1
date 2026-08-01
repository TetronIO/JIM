# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function ConvertTo-JIMSettingValueUpdates {
    <#
    .SYNOPSIS
        Turns a hashtable of Connector Definition Setting identifier to value into the shape the API expects.

    .DESCRIPTION
        The API's setting-value payload names the field by type (stringValue, intValue, checkboxValue) so a
        setting's type is never guessed from the value's JSON representation. Callers supply a plain
        hashtable, e.g. @{ 40 = 'https://hr.corp.local/scim/v2'; 55 = 10 }, and this picks the right field.

    .PARAMETER SettingValues
        Setting values keyed by Connector Definition Setting identifier.
    #>
    [CmdletBinding()]
    [OutputType([hashtable])]
    param(
        [Parameter(Mandatory)]
        [hashtable]$SettingValues
    )

    $payload = @{}

    foreach ($key in $SettingValues.Keys) {
        $value = $SettingValues[$key]

        $payload["$key"] = switch ($value) {
            { $_ -is [bool] } { @{ checkboxValue = $_ }; break }
            { $_ -is [int] -or $_ -is [long] } { @{ intValue = [int]$_ }; break }
            default { @{ stringValue = [string]$_ } }
        }
    }

    return $payload
}
