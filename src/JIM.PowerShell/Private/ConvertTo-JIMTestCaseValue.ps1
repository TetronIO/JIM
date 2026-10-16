# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function ConvertTo-JIMTestCaseValue {
    <#
    .SYNOPSIS
        Converts a configuration test case value to the form the API reads.

    .DESCRIPTION
        A [datetime] becomes ISO 8601: with a Z when it is UTC or local (local converted to UTC), and with no offset
        when it has no time zone, which the API reads as UTC. An array is converted value by value, so a multi-valued
        attribute keeps its values. Anything else passes through as it is.
    #>
    [CmdletBinding()]
    [OutputType([object])]
    param(
        [Parameter(Position = 0)]
        [AllowNull()]
        [object]$Value
    )

    if ($null -eq $Value) {
        return $null
    }

    if ($Value -is [datetime]) {
        if ($Value.Kind -eq [DateTimeKind]::Unspecified) {
            return $Value.ToString('yyyy-MM-ddTHH:mm:ss', [System.Globalization.CultureInfo]::InvariantCulture)
        }
        return $Value.ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ', [System.Globalization.CultureInfo]::InvariantCulture)
    }

    if ($Value -is [System.Collections.IEnumerable] -and $Value -isnot [string]) {
        # The comma keeps a one-value array an array when it is returned.
        return , @($Value | ForEach-Object { ConvertTo-JIMTestCaseValue $_ })
    }

    $Value
}
