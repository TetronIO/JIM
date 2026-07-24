# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function New-JIMScopingCriterion {
    <#
    .SYNOPSIS
        Adds a criterion to a scoping criteria group.

    .DESCRIPTION
        Creates a new scoping criterion within a criteria group.
        For export Synchronisation Rules, criteria evaluate Metaverse attribute values.
        For import Synchronisation Rules, criteria evaluate Connected System attribute values.

    .PARAMETER SyncRuleId
        The unique identifier of the Synchronisation Rule.

    .PARAMETER GroupId
        The unique identifier of the criteria group to add the criterion to.

    .PARAMETER MetaverseAttributeId
        The unique identifier of the Metaverse attribute to evaluate (for export Synchronisation Rules).

    .PARAMETER MetaverseAttributeName
        Alternative to MetaverseAttributeId. The name of the Metaverse attribute to evaluate (for export Synchronisation Rules).

    .PARAMETER ConnectedSystemAttributeId
        The unique identifier of the Connected System attribute to evaluate (for import Synchronisation Rules).

    .PARAMETER ConnectedSystemAttributeName
        Alternative to ConnectedSystemAttributeId. The name of the Connected System attribute to evaluate (for import Synchronisation Rules).

    .PARAMETER ComparisonType
        The comparison operator. Valid values:
        Equals, NotEquals, StartsWith, NotStartsWith, EndsWith, NotEndsWith,
        Contains, NotContains, LessThan, LessThanOrEquals, GreaterThan, GreaterThanOrEquals

    .PARAMETER StringValue
        The string value to compare against (for text attributes).

    .PARAMETER IntValue
        The integer value to compare against (for number attributes).

    .PARAMETER LongValue
        The 64-bit integer value to compare against (for long number attributes).

    .PARAMETER DecimalValue
        The decimal value to compare against (for decimal attributes). For high-precision values use
        PowerShell's decimal literal suffix (for example 123.456789012345678d) or a quoted string,
        because a bare numeric literal is parsed as a double first and can lose precision.

    .PARAMETER DateTimeValue
        The date/time value to compare against (for datetime attributes).

    .PARAMETER BoolValue
        The boolean value to compare against (for boolean attributes).

    .PARAMETER GuidValue
        The GUID value to compare against (for GUID attributes).

    .PARAMETER CaseSensitive
        When provided as $false, text comparisons ignore case differences. When omitted
        the server default (true) applies. Only meaningful for Text attribute comparisons.

    .PARAMETER ValueMode
        For DateTime attributes, 'Absolute' (compare against -DateTimeValue, the default) or 'Relative'
        (compare against a date resolved relative to now). Relative requires -RelativeCount, -RelativeUnit
        and -RelativeDirection, and is mutually exclusive with -DateTimeValue.

    .PARAMETER RelativeCount
        The relative offset count (zero or positive). Used with -RelativeUnit and -RelativeDirection.

    .PARAMETER RelativeUnit
        The relative offset unit: Hours, Days, Weeks, Months or Years.

    .PARAMETER RelativeDirection
        The relative offset direction: Ago (past) or FromNow (future).

    .PARAMETER PassThru
        If specified, returns the created criterion object.

    .OUTPUTS
        If -PassThru is specified, returns the created criterion.

    .EXAMPLE
        New-JIMScopingCriterion -SyncRuleId 5 -GroupId 10 -MetaverseAttributeName 'Department' -ComparisonType Equals -StringValue 'IT'

        Creates a criterion for an export Synchronisation Rule that filters for Department = 'IT'.

    .EXAMPLE
        New-JIMScopingCriterion -SyncRuleId 5 -GroupId 10 -ConnectedSystemAttributeName 'ou' -ComparisonType Equals -StringValue 'Finance'

        Creates a criterion for an import Synchronisation Rule that filters for ou = 'Finance'.

    .EXAMPLE
        New-JIMScopingCriterion -SyncRuleId 5 -GroupId 10 -MetaverseAttributeId 15 -ComparisonType StartsWith -StringValue 'Emp' -PassThru

        Creates a criterion using attribute ID 15 that matches values starting with 'Emp'.

    .EXAMPLE
        New-JIMScopingCriterion -SyncRuleId 5 -GroupId 10 -MetaverseAttributeName 'Active' -ComparisonType Equals -BoolValue $true

        Creates a criterion that filters for Active = true.

    .LINK
        Get-JIMScopingCriteria
        Remove-JIMScopingCriterion
        New-JIMScopingCriteriaGroup
    #>
    [CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'Medium', DefaultParameterSetName = 'ByMvId')]
    [OutputType([PSCustomObject])]
    param(
        [Parameter(Mandatory, ValueFromPipelineByPropertyName)]
        [int]$SyncRuleId,

        [Parameter(Mandatory, ValueFromPipelineByPropertyName)]
        [int]$GroupId,

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

        # Handle Metaverse attribute (for export Synchronisation Rules)
        if ($PSCmdlet.ParameterSetName -eq 'ByMvId') {
            $body.metaverseAttributeId = $MetaverseAttributeId
            $attrDisplay = "MV Attribute ID $MetaverseAttributeId"
        }
        elseif ($PSCmdlet.ParameterSetName -eq 'ByMvName') {
            Write-Verbose "Looking up Metaverse attribute: $MetaverseAttributeName"
            $attributes = Invoke-JIMApi -Endpoint "/api/v1/metaverse/attributes"
            $attribute = $attributes | Where-Object { $_.name -eq $MetaverseAttributeName } | Select-Object -First 1

            if (-not $attribute) {
                Write-Error "Metaverse attribute '$MetaverseAttributeName' not found."
                return
            }

            $body.metaverseAttributeId = $attribute.id
            $attrDisplay = "MV Attribute '$MetaverseAttributeName'"
            Write-Verbose "Resolved '$MetaverseAttributeName' to attribute ID $($attribute.id)"
        }
        # Handle Connected System attribute (for import Synchronisation Rules)
        elseif ($PSCmdlet.ParameterSetName -eq 'ByCsId') {
            $body.connectedSystemAttributeId = $ConnectedSystemAttributeId
            $attrDisplay = "CS Attribute ID $ConnectedSystemAttributeId"
        }
        elseif ($PSCmdlet.ParameterSetName -eq 'ByCsName') {
            # Get the Synchronisation Rule to find the connected system and object type
            Write-Verbose "Looking up Synchronisation Rule $SyncRuleId to find Connected System attribute"
            $syncRule = Invoke-JIMApi -Endpoint "/api/v1/synchronisation/sync-rules/$SyncRuleId"

            if (-not $syncRule) {
                Write-Error "Synchronisation Rule $SyncRuleId not found."
                return
            }

            if ($syncRule.direction -ne 'Import') {
                Write-Error "Connected System attributes can only be used with import Synchronisation Rules. This Synchronisation Rule is an export rule."
                return
            }

            # Get the object type attributes
            $connectedSystemId = $syncRule.connectedSystemId
            $objectTypeId = $syncRule.connectedSystemObjectTypeId
            Write-Verbose "Looking up Connected System $connectedSystemId object type $objectTypeId attributes"

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
            $attrDisplay = "CS Attribute '$ConnectedSystemAttributeName'"
            Write-Verbose "Resolved '$ConnectedSystemAttributeName' to attribute ID $($attribute.id)"
        }

        # Add the value based on what was provided
        if ($PSBoundParameters.ContainsKey('StringValue')) {
            $body.stringValue = $StringValue
        }
        if ($PSBoundParameters.ContainsKey('IntValue')) {
            $body.intValue = $IntValue
        }
        if ($PSBoundParameters.ContainsKey('LongValue')) {
            $body.longValue = $LongValue
        }
        if ($PSBoundParameters.ContainsKey('DecimalValue')) {
            $body.decimalValue = $DecimalValue
        }
        if ($PSBoundParameters.ContainsKey('DateTimeValue')) {
            $body.dateTimeValue = $DateTimeValue.ToString('o')
        }
        if ($PSBoundParameters.ContainsKey('BoolValue')) {
            $body.boolValue = $BoolValue
        }
        if ($PSBoundParameters.ContainsKey('GuidValue')) {
            $body.guidValue = $GuidValue.ToString()
        }
        if ($PSBoundParameters.ContainsKey('CaseSensitive')) {
            $body.caseSensitive = $CaseSensitive
        }

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

        if ($PSCmdlet.ShouldProcess("Scoping Criteria Group $GroupId", "Add Criterion ($attrDisplay $ComparisonType)")) {
            Write-Verbose "Creating criterion in group $GroupId for Synchronisation Rule $SyncRuleId"

            try {
                $result = Invoke-JIMApi -Endpoint "/api/v1/synchronisation/sync-rules/$SyncRuleId/scoping-criteria/$GroupId/criteria" -Method 'POST' -Body $body

                Write-Verbose "Created criterion ID: $($result.id)"

                if ($PassThru) {
                    $result | Add-Member -NotePropertyName 'SyncRuleId' -NotePropertyValue $SyncRuleId -Force
                    $result | Add-Member -NotePropertyName 'GroupId' -NotePropertyValue $GroupId -PassThru -Force
                }
            }
            catch {
                Write-Error "Failed to create criterion: $_"
            }
        }
    }
}
