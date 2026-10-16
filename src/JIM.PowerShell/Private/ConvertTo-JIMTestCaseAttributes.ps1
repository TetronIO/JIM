# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function ConvertTo-JIMTestCaseAttributes {
    <#
    .SYNOPSIS
        Converts a hashtable of configuration test case input values to the form the API reads.
    #>
    [CmdletBinding()]
    [OutputType([hashtable])]
    param(
        [Parameter(Mandatory, Position = 0)]
        [hashtable]$Attributes
    )

    $converted = @{}
    foreach ($name in $Attributes.Keys) {
        $converted[$name] = ConvertTo-JIMTestCaseValue $Attributes[$name]
    }
    $converted
}
