# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

# Property names whose value is a dynamic-key dictionary, where the keys are *data*
# (attribute names, Serilog property names, validation field names) rather than fixed
# DTO field names. The REST API serialises DTO property names as camelCase but leaves
# dictionary keys verbatim (only PropertyNamingPolicy is set, not DictionaryKeyPolicy),
# so the module must do the same: a value keyed by attribute name like 'mail' or
# 'employeeID' must never be rewritten to 'Mail' / 'EmployeeID', which would corrupt it.
#
# The container property itself IS a DTO field and is still renamed to PascalCase
# (e.g. 'attributes' -> 'Attributes'); only the keys *inside* its value are preserved.
#
# A name alone is not sufficient to decide: 'attributes' is a dynamic dictionary on
# MetaverseObjectHeaderDto but a List<DTO> on ConnectedSystemDto and
# MetaverseObjectTypeDetailDto (whose element fields DO need normalising). The
# discriminator is the JSON shape: a dictionary always serialises as a JSON object
# (a PSCustomObject after ConvertFrom-Json), a DTO list as a JSON array. So a property
# is treated as opaque only when its name is listed here AND its value is a JSON object.
#
# MAINTENANCE: when a new API response exposes a dictionary keyed by data (any
# Dictionary<string, ...> whose key is a name/identifier the user controls), add its
# camelCase wire property name here, or its keys will be silently PascalCased. See the
# ConvertTo-JIMOutputObject tests, which pin this list.
$script:JIMOpaqueValueProperties = @(
    'attributes'        # MetaverseObjectHeaderDto.Attributes: keyed by attribute name
    'mvAttributes'      # SyncRuleMappingPreview MV attributes: keyed by attribute name
    'csAttributes'      # SyncRuleMappingPreview CS attributes: keyed by attribute name
    'properties'        # LogEntryDto.Properties: keyed by Serilog property name
    'validationErrors'  # ApiErrorResponse.ValidationErrors: keyed by field name
)

function ConvertTo-JIMPascalCaseName {
    <#
    .SYNOPSIS
        Converts a single camelCase wire property name to PascalCase.

    .DESCRIPTION
        The REST API applies System.Text.Json's camelCase naming policy, which only
        lower-cases the leading character of each PascalCase property name. Upper-casing
        the leading character is therefore the exact inverse for every DTO field JIM
        exposes (verified: no API DTO property begins with a multi-letter acronym such
        as 'IPAddress' that the policy would lower-case beyond the first character).
        If such a property is ever introduced, the tail casing would round-trip
        imperfectly (e.g. 'ipAddress' -> 'IpAddress'); the value is still reachable via
        PowerShell's case-insensitive member access, so this is cosmetic, not breaking.
    #>
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)]
        [AllowEmptyString()]
        [string]$Name
    )

    if ([string]::IsNullOrEmpty($Name) -or [char]::IsUpper($Name[0])) {
        return $Name
    }

    return [char]::ToUpperInvariant($Name[0]) + $Name.Substring(1)
}

function ConvertTo-JIMOutputObject {
    <#
    .SYNOPSIS
        Normalises a deserialised REST API response to PascalCase property names.

    .DESCRIPTION
        JIM's REST API serialises JSON with camelCase property names (correct for a
        REST/JSON API), but PowerShell cmdlet output is expected to be PascalCase
        (Microsoft Cmdlet Development Guidelines). This helper rehydrates the object
        graph returned by ConvertFrom-Json (via Invoke-RestMethod) into an equivalent
        graph with PascalCase property names, so Get-Member, Format-Table,
        ConvertTo-Json and tab-completion all present the conventional casing.

        Dynamic-key dictionary values (see $script:JIMOpaqueValueProperties) are passed
        through with their keys preserved verbatim, because those keys are user data
        (attribute names, log property names), not DTO field names.

        Property order, arrays (including single-element and empty), scalars and nulls
        are all preserved. PowerShell member access is case-insensitive, so cmdlets that
        internally read wire-cased properties (e.g. $response.items) continue to work.

    .PARAMETER InputObject
        The deserialised value to normalise: a PSCustomObject, an array, a scalar, or null.

    .PARAMETER Verbatim
        Internal. When set, property names at this level (and below) are preserved as-is.
        Used to carry the "this subtree is dynamic-key data" state down the recursion.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Position = 0)]
        [AllowNull()]
        [object]$InputObject,

        [switch]$Verbatim
    )

    if ($null -eq $InputObject) {
        return $null
    }

    # Strings are IEnumerable; treat them (and every other scalar) as leaf values.
    if ($InputObject -is [string] -or $InputObject -is [System.ValueType]) {
        return $InputObject
    }

    # JSON object: rebuild with normalised property names, preserving order.
    if ($InputObject -is [System.Management.Automation.PSCustomObject]) {
        $normalised = [ordered]@{}
        foreach ($property in $InputObject.PSObject.Properties) {
            if ($Verbatim) {
                # Already inside a dynamic-key subtree: keep this key, and keep recursing
                # verbatim (nested values are data too, never DTO shapes).
                $normalised[$property.Name] = ConvertTo-JIMOutputObject -InputObject $property.Value -Verbatim
            }
            else {
                # A property is opaque only when it is both named as a dynamic-key holder
                # and its value is a JSON object (a dictionary), not a JSON array (a DTO
                # list, whose elements must still be normalised).
                $isOpaque = ($script:JIMOpaqueValueProperties -contains $property.Name) -and `
                    ($property.Value -is [System.Management.Automation.PSCustomObject])
                $newName = ConvertTo-JIMPascalCaseName -Name $property.Name
                $normalised[$newName] = ConvertTo-JIMOutputObject -InputObject $property.Value -Verbatim:$isOpaque
            }
        }
        return [PSCustomObject]$normalised
    }

    # JSON array (or any other enumerable): normalise each element, preserving arity.
    if ($InputObject -is [System.Collections.IEnumerable]) {
        $items = [System.Collections.Generic.List[object]]::new()
        foreach ($item in $InputObject) {
            $items.Add((ConvertTo-JIMOutputObject -InputObject $item -Verbatim:$Verbatim))
        }
        # Comma operator: return the array as a single object so assignment and pipeline
        # capture preserve it (including empty and single-element arrays) rather than
        # unrolling it away.
        return , $items.ToArray()
    }

    # Any other reference type (should not occur for JSON payloads): return unchanged.
    return $InputObject
}
