# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function New-JIMPredefinedSearchCriterion {
    <#
    .SYNOPSIS
        Adds a criterion to a Predefined Search criteria group.

    .DESCRIPTION
        Creates a new criterion that filters a Predefined Search on a Metaverse attribute value.
        Provide the value carrier that matches the attribute's data type (for example -IntValue for
        a Number attribute, -DateTimeValue for a DateTime attribute). The operator must be applicable
        to the attribute's data type:
        - Text: Equals, NotEquals, StartsWith, NotStartsWith, EndsWith, NotEndsWith, Contains, NotContains
        - Number, LongNumber, DateTime: Equals, NotEquals, LessThan, LessThanOrEquals, GreaterThan, GreaterThanOrEquals
        - Boolean, Guid: Equals, NotEquals

    .PARAMETER PredefinedSearchId
        The unique identifier of the Predefined Search.

    .PARAMETER GroupId
        The unique identifier of the criteria group to add the criterion to.

    .PARAMETER MetaverseAttributeId
        The unique identifier of the Metaverse attribute to evaluate.

    .PARAMETER MetaverseAttributeName
        Alternative to MetaverseAttributeId. The name of the Metaverse attribute to evaluate.

    .PARAMETER ComparisonType
        The comparison operator.

    .PARAMETER StringValue
        The string value to compare against (for Text attributes).

    .PARAMETER IntValue
        The integer value to compare against (for Number attributes).

    .PARAMETER LongValue
        The 64-bit integer value to compare against (for LongNumber attributes).

    .PARAMETER DateTimeValue
        The date/time value to compare against (for DateTime attributes). Interpreted as UTC.

    .PARAMETER BoolValue
        The boolean value to compare against (for Boolean attributes).

    .PARAMETER GuidValue
        The GUID value to compare against (for Guid attributes).

    .PARAMETER CaseSensitive
        When provided as $false, text comparisons ignore case differences. When omitted the server
        default (true) applies. Only meaningful for Text attribute comparisons.

    .PARAMETER ValueMode
        For DateTime attributes, 'Absolute' (compare against -DateTimeValue, the default) or 'Relative'
        (compare against a date resolved relative to now). Relative requires -RelativeCount, -RelativeUnit
        and -RelativeDirection, and is mutually exclusive with -DateTimeValue.

    .PARAMETER RelativeCount
        The relative offset count (zero or positive).

    .PARAMETER RelativeUnit
        The relative offset unit: Hours, Days, Weeks, Months or Years.

    .PARAMETER RelativeDirection
        The relative offset direction: Ago (past) or FromNow (future).

    .PARAMETER PassThru
        If specified, returns the created criterion object.

    .OUTPUTS
        If -PassThru is specified, returns the created criterion.

    .EXAMPLE
        New-JIMPredefinedSearchCriterion -PredefinedSearchId 3 -GroupId 10 -MetaverseAttributeName 'Department' -ComparisonType Equals -StringValue 'Finance'

        Filters the search for Department = 'Finance'.

    .EXAMPLE
        New-JIMPredefinedSearchCriterion -PredefinedSearchId 3 -GroupId 10 -MetaverseAttributeName 'MemberCount' -ComparisonType GreaterThan -IntValue 0

        Filters the search for groups with more than zero members.

    .LINK
        Get-JIMPredefinedSearchCriteriaGroup
        Set-JIMPredefinedSearchCriterion
        Remove-JIMPredefinedSearchCriterion
    #>
    [CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'Medium', DefaultParameterSetName = 'ById')]
    [OutputType([PSCustomObject])]
    param(
        [Parameter(Mandatory, ValueFromPipelineByPropertyName)]
        [int]$PredefinedSearchId,

        [Parameter(Mandatory, ValueFromPipelineByPropertyName)]
        [int]$GroupId,

        [Parameter(Mandatory, ParameterSetName = 'ById')]
        [int]$MetaverseAttributeId,

        [Parameter(Mandatory, ParameterSetName = 'ByName')]
        [string]$MetaverseAttributeName,

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

        $attributeId = $MetaverseAttributeId
        if ($PSCmdlet.ParameterSetName -eq 'ByName') {
            Write-Verbose "Looking up Metaverse attribute: $MetaverseAttributeName"
            $attributes = Invoke-JIMApi -Endpoint "/api/v1/metaverse/attributes"
            $attribute = $attributes | Where-Object { $_.name -eq $MetaverseAttributeName } | Select-Object -First 1
            if (-not $attribute) {
                Write-Error "Metaverse attribute '$MetaverseAttributeName' not found."
                return
            }
            $attributeId = $attribute.id
            Write-Verbose "Resolved '$MetaverseAttributeName' to attribute ID $attributeId"
        }

        $body = @{
            metaverseAttributeId = $attributeId
            comparisonType = $ComparisonType
        }
        if ($PSBoundParameters.ContainsKey('StringValue')) { $body.stringValue = $StringValue }
        if ($PSBoundParameters.ContainsKey('IntValue')) { $body.intValue = $IntValue }
        if ($PSBoundParameters.ContainsKey('LongValue')) { $body.longValue = $LongValue }
        if ($PSBoundParameters.ContainsKey('DateTimeValue')) { $body.dateTimeValue = $DateTimeValue.ToString('o') }
        if ($PSBoundParameters.ContainsKey('BoolValue')) { $body.boolValue = $BoolValue }
        if ($PSBoundParameters.ContainsKey('GuidValue')) { $body.guidValue = $GuidValue.ToString() }
        if ($PSBoundParameters.ContainsKey('CaseSensitive')) { $body.caseSensitive = $CaseSensitive }

        # Relative date handling (DateTime attributes only; validated server-side too).
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

        if ($PSCmdlet.ShouldProcess("Criteria Group $GroupId on Predefined Search $PredefinedSearchId", "Add Criterion (Attribute $attributeId $ComparisonType)")) {
            Write-Verbose "Creating criterion in group $GroupId for Predefined Search $PredefinedSearchId"
            try {
                $result = Invoke-JIMApi -Endpoint "/api/v1/predefined-searches/$PredefinedSearchId/criteria-groups/$GroupId/criteria" -Method 'POST' -Body $body
                Write-Verbose "Created criterion ID: $($result.id)"
                if ($PassThru) {
                    $result | Add-Member -NotePropertyName 'PredefinedSearchId' -NotePropertyValue $PredefinedSearchId -Force
                    $result | Add-Member -NotePropertyName 'GroupId' -NotePropertyValue $GroupId -PassThru -Force
                }
            }
            catch {
                Write-Error "Failed to create criterion: $_"
            }
        }
    }
}
