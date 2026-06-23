# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Set-JIMScopingCriterion {
    <#
    .SYNOPSIS
        Updates a criterion in a scoping criteria group (full replacement).

    .DESCRIPTION
        Replaces a scoping criterion's attribute, comparison operator and value. For export sync rules
        provide a Metaverse attribute; for import sync rules a Connected System attribute. For DateTime
        attributes set -ValueMode Relative and supply -RelativeCount/-RelativeUnit/-RelativeDirection to
        compare against a date resolved relative to now (re-evaluated on each sync run).

    .PARAMETER SyncRuleId
        The unique identifier of the sync rule.

    .PARAMETER GroupId
        The unique identifier of the criteria group containing the criterion.

    .PARAMETER CriterionId
        The unique identifier of the criterion to update.

    .PARAMETER MetaverseAttributeId
        The unique identifier of the Metaverse attribute to evaluate (for export sync rules).

    .PARAMETER MetaverseAttributeName
        Alternative to MetaverseAttributeId. The name of the Metaverse attribute to evaluate (for export sync rules).

    .PARAMETER ConnectedSystemAttributeId
        The unique identifier of the Connected System attribute to evaluate (for import sync rules).

    .PARAMETER ConnectedSystemAttributeName
        Alternative to ConnectedSystemAttributeId. The name of the Connected System attribute to evaluate (for import sync rules).

    .PARAMETER ComparisonType
        The comparison operator.

    .PARAMETER StringValue
        The string value to compare against (for text attributes).

    .PARAMETER IntValue
        The integer value to compare against (for number attributes).

    .PARAMETER LongValue
        The 64-bit integer value to compare against (for long number attributes).

    .PARAMETER DateTimeValue
        The date/time value to compare against (for absolute datetime criteria).

    .PARAMETER BoolValue
        The boolean value to compare against (for boolean attributes).

    .PARAMETER GuidValue
        The GUID value to compare against (for GUID attributes).

    .PARAMETER CaseSensitive
        When provided as $false, text comparisons ignore case differences. Only meaningful for Text attributes.

    .PARAMETER ValueMode
        For DateTime attributes, 'Absolute' (compare against -DateTimeValue, the default) or 'Relative'.
        Relative requires -RelativeCount, -RelativeUnit and -RelativeDirection, and is mutually exclusive with -DateTimeValue.

    .PARAMETER RelativeCount
        The relative offset count (zero or positive).

    .PARAMETER RelativeUnit
        The relative offset unit: Hours, Days, Weeks, Months or Years.

    .PARAMETER RelativeDirection
        The relative offset direction: Ago (past) or FromNow (future).

    .PARAMETER PassThru
        If specified, returns the updated criterion object.

    .OUTPUTS
        If -PassThru is specified, returns the updated criterion.

    .EXAMPLE
        Set-JIMScopingCriterion -SyncRuleId 5 -GroupId 10 -CriterionId 15 -MetaverseAttributeName 'AccountExpiry' -ComparisonType LessThanOrEquals -ValueMode Relative -RelativeCount 7 -RelativeUnit Days -RelativeDirection FromNow

        Updates criterion 15 to match when AccountExpiry is on or before 7 days from now.

    .LINK
        New-JIMScopingCriterion
        Get-JIMScopingCriteria
        Remove-JIMScopingCriterion
    #>
    [CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'Medium', DefaultParameterSetName = 'ByMvId')]
    [OutputType([PSCustomObject])]
    param(
        [Parameter(Mandatory, ValueFromPipelineByPropertyName)]
        [int]$SyncRuleId,

        [Parameter(Mandatory, ValueFromPipelineByPropertyName)]
        [int]$GroupId,

        [Parameter(Mandatory, ValueFromPipelineByPropertyName)]
        [Alias('Id')]
        [int]$CriterionId,

        [Parameter(Mandatory, ParameterSetName = 'ByMvId')]
        [int]$MetaverseAttributeId,

        [Parameter(Mandatory, ParameterSetName = 'ByMvName')]
        [string]$MetaverseAttributeName,

        [Parameter(Mandatory, ParameterSetName = 'ByCsId')]
        [int]$ConnectedSystemAttributeId,

        [Parameter(Mandatory, ParameterSetName = 'ByCsName')]
        [string]$ConnectedSystemAttributeName,

        [Parameter(Mandatory)]
        [ValidateSet('Equals', 'NotEquals', 'StartsWith', 'NotStartsWith', 'EndsWith', 'NotEndsWith',
                     'Contains', 'NotContains', 'LessThan', 'LessThanOrEquals', 'GreaterThan', 'GreaterThanOrEquals')]
        [string]$ComparisonType,

        [Parameter()]
        [string]$StringValue,

        [Parameter()]
        [int]$IntValue,

        [Parameter()]
        [long]$LongValue,

        [Parameter()]
        [datetime]$DateTimeValue,

        [Parameter()]
        [bool]$BoolValue,

        [Parameter()]
        [guid]$GuidValue,

        [Parameter()]
        [bool]$CaseSensitive,

        [Parameter()]
        [ValidateSet('Absolute', 'Relative')]
        [string]$ValueMode,

        [Parameter()]
        [int]$RelativeCount,

        [Parameter()]
        [ValidateSet('Hours', 'Days', 'Weeks', 'Months', 'Years')]
        [string]$RelativeUnit,

        [Parameter()]
        [ValidateSet('Ago', 'FromNow')]
        [string]$RelativeDirection,

        [switch]$PassThru
    )

    process {
        if (-not $script:JIMConnection) {
            Write-Error "You are not connected to JIM. Run Connect-JIM -Url <your JIM URL> to authenticate, then try again."
            return
        }

        $body = @{
            comparisonType = $ComparisonType
        }

        if ($PSCmdlet.ParameterSetName -eq 'ByMvId') {
            $body.metaverseAttributeId = $MetaverseAttributeId
        }
        elseif ($PSCmdlet.ParameterSetName -eq 'ByMvName') {
            $attributes = Invoke-JIMApi -Endpoint "/api/v1/metaverse/attributes"
            $attribute = $attributes | Where-Object { $_.name -eq $MetaverseAttributeName } | Select-Object -First 1
            if (-not $attribute) {
                Write-Error "Metaverse attribute '$MetaverseAttributeName' not found."
                return
            }
            $body.metaverseAttributeId = $attribute.id
        }
        elseif ($PSCmdlet.ParameterSetName -eq 'ByCsId') {
            $body.connectedSystemAttributeId = $ConnectedSystemAttributeId
        }
        elseif ($PSCmdlet.ParameterSetName -eq 'ByCsName') {
            $syncRule = Invoke-JIMApi -Endpoint "/api/v1/synchronisation/sync-rules/$SyncRuleId"
            if (-not $syncRule) {
                Write-Error "Sync rule $SyncRuleId not found."
                return
            }
            if ($syncRule.direction -ne 'Import') {
                Write-Error "Connected System attributes can only be used with import sync rules. This sync rule is an export rule."
                return
            }
            $objectType = Invoke-JIMApi -Endpoint "/api/v1/connected-systems/$($syncRule.connectedSystemId)/object-types/$($syncRule.connectedSystemObjectTypeId)"
            if (-not $objectType -or -not $objectType.attributes) {
                Write-Error "Could not find object type attributes."
                return
            }
            $attribute = $objectType.attributes | Where-Object { $_.name -eq $ConnectedSystemAttributeName } | Select-Object -First 1
            if (-not $attribute) {
                Write-Error "Connected System attribute '$ConnectedSystemAttributeName' not found on object type '$($objectType.name)'."
                return
            }
            $body.connectedSystemAttributeId = $attribute.id
        }

        if ($PSBoundParameters.ContainsKey('StringValue')) { $body.stringValue = $StringValue }
        if ($PSBoundParameters.ContainsKey('IntValue')) { $body.intValue = $IntValue }
        if ($PSBoundParameters.ContainsKey('LongValue')) { $body.longValue = $LongValue }
        if ($PSBoundParameters.ContainsKey('DateTimeValue')) { $body.dateTimeValue = $DateTimeValue.ToString('o') }
        if ($PSBoundParameters.ContainsKey('BoolValue')) { $body.boolValue = $BoolValue }
        if ($PSBoundParameters.ContainsKey('GuidValue')) { $body.guidValue = $GuidValue.ToString() }
        if ($PSBoundParameters.ContainsKey('CaseSensitive')) { $body.caseSensitive = $CaseSensitive }

        $relativeRequested = ($PSBoundParameters.ContainsKey('ValueMode') -and $ValueMode -eq 'Relative') -or
            $PSBoundParameters.ContainsKey('RelativeCount') -or $PSBoundParameters.ContainsKey('RelativeUnit') -or $PSBoundParameters.ContainsKey('RelativeDirection')
        if ($relativeRequested) {
            if ($PSBoundParameters.ContainsKey('DateTimeValue')) {
                Write-Error "Provide either -DateTimeValue (absolute) or the relative parameters, not both."
                return
            }
            if (-not ($PSBoundParameters.ContainsKey('RelativeCount') -and $PSBoundParameters.ContainsKey('RelativeUnit') -and $PSBoundParameters.ContainsKey('RelativeDirection'))) {
                Write-Error "Relative criteria require -RelativeCount, -RelativeUnit and -RelativeDirection."
                return
            }
            $body.valueMode = 'Relative'
            $body.relativeCount = $RelativeCount
            $body.relativeUnit = $RelativeUnit
            $body.relativeDirection = $RelativeDirection
        }

        if ($PSCmdlet.ShouldProcess("Criterion $CriterionId in Group $GroupId", "Update")) {
            try {
                $result = Invoke-JIMApi -Endpoint "/api/v1/synchronisation/sync-rules/$SyncRuleId/scoping-criteria/$GroupId/criteria/$CriterionId" -Method 'PUT' -Body $body
                if ($PassThru) {
                    $result | Add-Member -NotePropertyName 'SyncRuleId' -NotePropertyValue $SyncRuleId -Force
                    $result | Add-Member -NotePropertyName 'GroupId' -NotePropertyValue $GroupId -PassThru -Force
                }
            }
            catch {
                Write-Error "Failed to update criterion: $_"
            }
        }
    }
}
