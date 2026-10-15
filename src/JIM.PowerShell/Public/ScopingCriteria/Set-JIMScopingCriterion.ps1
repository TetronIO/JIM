# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Set-JIMScopingCriterion {
    <#
    .SYNOPSIS
        Replaces a criterion in a scoping criteria group.

    .DESCRIPTION
        Replaces a criterion: its attribute (or expression), comparison and value are all set from what you give, as
        New-JIMScopingCriterion would set them. Its case sensitivity is kept unless -CaseSensitive is given.

    .PARAMETER SyncRuleId
        The ID of the Synchronisation Rule.

    .PARAMETER GroupId
        The ID of the group the criterion is in.

    .PARAMETER CriterionId
        The ID of the criterion to replace.

    .PARAMETER MetaverseAttributeId
        The ID of the Metaverse attribute to test. An export Synchronisation Rule's criteria test Metaverse
        attributes.

    .PARAMETER MetaverseAttributeName
        The name of the Metaverse attribute to test, instead of its ID.

    .PARAMETER ConnectedSystemAttributeId
        The ID of the Connected System attribute to test. An import Synchronisation Rule's criteria test Connected
        System attributes.

    .PARAMETER ConnectedSystemAttributeName
        The name of the Connected System attribute to test, instead of its ID.

    .PARAMETER Expression
        An expression whose computed value the criterion compares, in place of an attribute. It reads the object the
        Synchronisation Rule scopes: cs["name"] on an import rule, mv["Name"] on an export rule. Use it to compare a
        value reshaped first, such as Lower(Trim(cs["department"])). Requires -CompareAs.

    .PARAMETER CompareAs
        The type the expression's computed value is compared as, which decides the comparisons and the value parameter
        that apply, as an attribute's own type does. A value the expression computes that cannot be read as this type
        stops that object with an error rather than deciding its scope.

    .PARAMETER ComparisonType
        How the attribute is compared with the value. The Not forms are met when the attribute holds a value and
        none matches; the others when any value does. HasValue and HasNoValue test whether the attribute holds a
        value at all and take no value. MatchesPattern and NotMatchesPattern take a .NET regular expression in
        -StringValue, for Text attributes.

    .PARAMETER StringValue
        The value to compare a Text attribute with, or the regular expression for MatchesPattern and
        NotMatchesPattern.

    .PARAMETER IntValue
        The value to compare a Number attribute with.

    .PARAMETER LongValue
        The value to compare a LongNumber attribute with.

    .PARAMETER DecimalValue
        The value to compare a Decimal attribute with. For a high-precision value, use the d suffix
        (123.456789012345678d) or a quoted string: a bare number is read as a double first, and can lose precision.

    .PARAMETER DateTimeValue
        The date and time to compare a DateTime attribute with, when -ValueMode is Absolute.

    .PARAMETER BoolValue
        The value to compare a Boolean attribute with.

    .PARAMETER GuidValue
        The value to compare a Guid attribute with.

    .PARAMETER CaseSensitive
        Whether a Text comparison respects case: $false ignores it. When omitted, the criterion keeps its current
        setting.

    .PARAMETER ValueMode
        For a DateTime attribute: Absolute compares with -DateTimeValue, and Relative with a date worked out from
        now each time the rule is evaluated, set by -RelativeCount, -RelativeUnit and -RelativeDirection. Relative
        cannot be used with -DateTimeValue. Absolute when omitted.

    .PARAMETER RelativeCount
        How many units from now the date is, zero or more. Used with -ValueMode Relative.

    .PARAMETER RelativeUnit
        The unit -RelativeCount counts. Used with -ValueMode Relative.

    .PARAMETER RelativeDirection
        Whether the date is in the past (Ago) or the future (FromNow). Used with -ValueMode Relative.

    .PARAMETER PassThru
        Returns the criterion as replaced.

    .OUTPUTS
        With -PassThru, the criterion as replaced; otherwise nothing.

        JIM.ScopingCriterion
        SyncRuleId (int): The ID of the Synchronisation Rule the criterion belongs to.
        GroupId (int): The ID of the group the criterion is in. With SyncRuleId and Id, it lets the criterion be
            piped to Set-JIMScopingCriterion and Remove-JIMScopingCriterion.

    .EXAMPLE
        Set-JIMScopingCriterion -SyncRuleId 5 -GroupId 10 -CriterionId 15 -MetaverseAttributeName 'AccountExpiry' -ComparisonType LessThanOrEquals -ValueMode Relative -RelativeCount 7 -RelativeUnit Days -RelativeDirection FromNow

        Updates criterion 15 to match when AccountExpiry is on or before 7 days from now.

    .EXAMPLE
        Set-JIMScopingCriterion -SyncRuleId 5 -GroupId 10 -CriterionId 15 -Expression 'Upper(Trim(mv["Department"]))' -CompareAs Text -ComparisonType Equals -StringValue 'FINANCE'

        Updates criterion 15 to compare the Department with its spacing removed and in capitals, so ' Finance ' and
        'finance' are both in scope.

    .LINK
        New-JIMScopingCriterion
        Get-JIMScopingCriteria
        Remove-JIMScopingCriterion
    #>
    [CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'Medium', DefaultParameterSetName = 'ByMvId')]
    [OutputType('JIM.ScopingCriterion')]
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

        [Parameter(Mandatory, ParameterSetName = 'ByExpression')]
        [ValidateNotNullOrEmpty()]
        [string]$Expression,

        [Parameter(Mandatory, ParameterSetName = 'ByExpression')]
        [ValidateSet('Text', 'Number', 'LongNumber', 'Decimal', 'DateTime', 'Boolean', 'Guid')]
        [string]$CompareAs,

        [Parameter(Mandatory)]
        [ValidateSet('Equals', 'NotEquals', 'StartsWith', 'NotStartsWith', 'EndsWith', 'NotEndsWith',
                     'Contains', 'NotContains', 'LessThan', 'LessThanOrEquals', 'GreaterThan', 'GreaterThanOrEquals',
                     'HasValue', 'HasNoValue', 'MatchesPattern', 'NotMatchesPattern')]
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
            try {
                $attribute = Resolve-JIMMetaverseAttribute -Name $MetaverseAttributeName
            }
            catch {
                Write-Error $_
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
                Write-Error "Synchronisation Rule $SyncRuleId not found."
                return
            }
            if ($syncRule.direction -ne 'Import') {
                Write-Error "Connected System attributes can only be used with import Synchronisation Rules. This Synchronisation Rule is an export rule."
                return
            }
            $connectedSystemId = $syncRule.connectedSystemId
            $objectTypeId = $syncRule.connectedSystemObjectTypeId
            $objectTypes = Invoke-JIMApi -Endpoint "/api/v1/synchronisation/connected-systems/$connectedSystemId/object-types"
            $objectType = $objectTypes | Where-Object { $_.id -eq $objectTypeId } | Select-Object -First 1
            if (-not $objectType) {
                Write-Error "Could not find object type $objectTypeId on Connected System $connectedSystemId."
                return
            }
            $attribute = $objectType.attributes | Where-Object { $_.name -eq $ConnectedSystemAttributeName } | Select-Object -First 1
            if (-not $attribute) {
                Write-Error "Connected System attribute '$ConnectedSystemAttributeName' not found on object type '$($objectType.name)'."
                return
            }
            $body.connectedSystemAttributeId = $attribute.id
        }

        if ($PSCmdlet.ParameterSetName -eq 'ByExpression') {
            $body.expression = $Expression
            $body.compareAs = $CompareAs
        }

        if ($PSBoundParameters.ContainsKey('StringValue')) { $body.stringValue = $StringValue }
        if ($PSBoundParameters.ContainsKey('IntValue')) { $body.intValue = $IntValue }
        if ($PSBoundParameters.ContainsKey('LongValue')) { $body.longValue = $LongValue }
        if ($PSBoundParameters.ContainsKey('DecimalValue')) { $body.decimalValue = $DecimalValue }
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

                # Something the administrator should know about the criterion as saved, such as an expression compared
                # against a relative date costing a full pass on every Temporal Scope Reconciliation.
                if ($result.notice) {
                    Write-Warning $result.notice
                }
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
