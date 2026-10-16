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
        - Number, LongNumber, Decimal, DateTime: Equals, NotEquals, LessThan, LessThanOrEquals, GreaterThan, GreaterThanOrEquals
        - Boolean, Guid: Equals, NotEquals

    .PARAMETER PredefinedSearchId
        The ID of the Predefined Search.

    .PARAMETER GroupId
        The ID of the criteria group to add the criterion to.

    .PARAMETER MetaverseAttributeId
        The ID of the Metaverse Attribute to compare.

    .PARAMETER MetaverseAttributeName
        The name of the Metaverse Attribute to compare, instead of its ID.

    .PARAMETER ComparisonType
        The comparison, which must suit the attribute's data type. Text takes Equals, NotEquals, StartsWith,
        NotStartsWith, EndsWith, NotEndsWith, Contains and NotContains; Number, LongNumber, Decimal and DateTime
        take Equals, NotEquals, LessThan, LessThanOrEquals, GreaterThan and GreaterThanOrEquals; Boolean and Guid
        take Equals and NotEquals. Every type also takes HasValue and HasNoValue, which test whether the attribute
        holds a value at all and take no value.

    .PARAMETER StringValue
        The value to compare a Text attribute with.

    .PARAMETER IntValue
        The value to compare a Number attribute with.

    .PARAMETER LongValue
        The value to compare a LongNumber attribute with.

    .PARAMETER DecimalValue
        The value to compare a Decimal attribute with. For a high-precision value use PowerShell's decimal suffix
        (123.456789012345678d) or a quoted string, because a bare number is read as a double first and can lose
        precision.

    .PARAMETER DateTimeValue
        The value to compare a DateTime attribute with, read as UTC.

    .PARAMETER BoolValue
        The value to compare a Boolean attribute with.

    .PARAMETER GuidValue
        The value to compare a Guid attribute with.

    .PARAMETER CaseSensitive
        Whether a Text comparison respects case. $false ignores case; it does when omitted.

    .PARAMETER ValueMode
        For a DateTime attribute: Absolute compares with -DateTimeValue, and is the default; Relative compares with
        a date worked out from now, given by -RelativeCount, -RelativeUnit and -RelativeDirection, and cannot be
        combined with -DateTimeValue.

    .PARAMETER RelativeCount
        How many units from now, for a Relative comparison; zero or more.

    .PARAMETER RelativeUnit
        The unit of -RelativeCount.

    .PARAMETER RelativeDirection
        Whether the date is in the past (Ago) or the future (FromNow).

    .PARAMETER ChangeReason
        Why the change was made, recorded on the audit Activity and shown in the Predefined Search's Configuration
        Change History.

    .PARAMETER PassThru
        Returns the created criterion.

    .OUTPUTS
        With -PassThru, the created criterion, with a GroupId property added naming its group; otherwise nothing.

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
    [OutputType('JIM.PredefinedSearchCriteriaDto')]
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
                     'Contains', 'NotContains', 'LessThan', 'LessThanOrEquals', 'GreaterThan', 'GreaterThanOrEquals',
                     'HasValue', 'HasNoValue')]
        [string]$ComparisonType,

        [Parameter()]
        [string]$StringValue,

        [Parameter()]
        [int]$IntValue,

        [Parameter()]
        [long]$LongValue,

        [Parameter()]
        [decimal]$DecimalValue,

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

        [ValidateNotNullOrEmpty()]
        [string]$ChangeReason,

        [switch]$PassThru
    )

    process {
        if (-not $script:JIMConnection) {
            Write-Error "You are not connected to JIM. Run Connect-JIM -Url <your JIM URL> to authenticate, then try again."
            return
        }

        $attributeId = $MetaverseAttributeId
        if ($PSCmdlet.ParameterSetName -eq 'ByName') {
            try {
                $attributeId = (Resolve-JIMMetaverseAttribute -Name $MetaverseAttributeName).id
            }
            catch {
                Write-Error $_
                return
            }
            Write-Verbose "Resolved '$MetaverseAttributeName' to attribute ID $attributeId"
        }

        $body = @{
            metaverseAttributeId = $attributeId
            comparisonType = $ComparisonType
        }
        if ($PSBoundParameters.ContainsKey('StringValue')) { $body.stringValue = $StringValue }
        if ($PSBoundParameters.ContainsKey('IntValue')) { $body.intValue = $IntValue }
        if ($PSBoundParameters.ContainsKey('LongValue')) { $body.longValue = $LongValue }
        if ($PSBoundParameters.ContainsKey('DecimalValue')) { $body.decimalValue = $DecimalValue }
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

        if ($ChangeReason) {
            $body.changeReason = $ChangeReason
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
