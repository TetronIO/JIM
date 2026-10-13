# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function New-JIMScopingCriterion {
    <#
    .SYNOPSIS
        Adds a criterion to a scoping criteria group.

    .DESCRIPTION
        Adds a criterion to a scoping criteria group: one attribute compared with one value. Name a Metaverse
        attribute for an export Synchronisation Rule and a Connected System attribute for an import one, and give
        the value in the parameter for the attribute's data type; HasValue and HasNoValue take no value.

    .PARAMETER SyncRuleId
        The ID of the Synchronisation Rule.

    .PARAMETER GroupId
        The ID of the group to add the criterion to.

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
        Whether a Text comparison respects case: $false ignores it. Case-sensitive when omitted.

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
        Returns the criterion created.

    .OUTPUTS
        With -PassThru, the criterion created; otherwise nothing.

        JIM.ScopingCriterion
        SyncRuleId (int): The ID of the Synchronisation Rule the criterion belongs to.
        GroupId (int): The ID of the group the criterion is in. With SyncRuleId and Id, it lets the criterion be
            piped to Set-JIMScopingCriterion and Remove-JIMScopingCriterion.

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

    .EXAMPLE
        New-JIMScopingCriterion -SyncRuleId 5 -GroupId 10 -ConnectedSystemAttributeName 'employeeType' -ComparisonType HasValue

        Creates a criterion for an import Synchronisation Rule that keeps only objects holding an employeeType.

    .EXAMPLE
        New-JIMScopingCriterion -SyncRuleId 5 -GroupId 10 -ConnectedSystemAttributeName 'mail' -ComparisonType MatchesPattern -StringValue '^[a-z.]+@example\.org$' -CaseSensitive $false

        Creates a criterion that keeps only objects with a mail address in the example.org domain, ignoring case.

    .LINK
        Get-JIMScopingCriteria
        Remove-JIMScopingCriterion
        New-JIMScopingCriteriaGroup
    #>
    [CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'Medium', DefaultParameterSetName = 'ByMvId')]
    [OutputType('JIM.ScopingCriterion')]
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

        # Handle Metaverse attribute (for export Synchronisation Rules)
        if ($PSCmdlet.ParameterSetName -eq 'ByMvId') {
            $body.metaverseAttributeId = $MetaverseAttributeId
            $attrDisplay = "MV Attribute ID $MetaverseAttributeId"
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
