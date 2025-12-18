function New-JIMScopingCriterion {
    <#
    .SYNOPSIS
        Adds a criterion to a scoping criteria group.

    .DESCRIPTION
        Creates a new scoping criterion within a criteria group.
        For export sync rules, criteria evaluate Metaverse attribute values.
        For import sync rules, criteria evaluate Connected System attribute values.

    .PARAMETER SyncRuleId
        The unique identifier of the sync rule.

    .PARAMETER GroupId
        The unique identifier of the criteria group to add the criterion to.

    .PARAMETER MetaverseAttributeId
        The unique identifier of the Metaverse attribute to evaluate (for export sync rules).

    .PARAMETER MetaverseAttributeName
        Alternative to MetaverseAttributeId. The name of the Metaverse attribute to evaluate (for export sync rules).

    .PARAMETER ConnectedSystemAttributeId
        The unique identifier of the Connected System attribute to evaluate (for import sync rules).

    .PARAMETER ConnectedSystemAttributeName
        Alternative to ConnectedSystemAttributeId. The name of the Connected System attribute to evaluate (for import sync rules).

    .PARAMETER ComparisonType
        The comparison operator. Valid values:
        Equals, NotEquals, StartsWith, NotStartsWith, EndsWith, NotEndsWith,
        Contains, NotContains, LessThan, LessThanOrEquals, GreaterThan, GreaterThanOrEquals

    .PARAMETER StringValue
        The string value to compare against (for text attributes).

    .PARAMETER IntValue
        The integer value to compare against (for number attributes).

    .PARAMETER DateTimeValue
        The date/time value to compare against (for datetime attributes).

    .PARAMETER BoolValue
        The boolean value to compare against (for boolean attributes).

    .PARAMETER GuidValue
        The GUID value to compare against (for GUID attributes).

    .PARAMETER PassThru
        If specified, returns the created criterion object.

    .OUTPUTS
        If -PassThru is specified, returns the created criterion.

    .EXAMPLE
        New-JIMScopingCriterion -SyncRuleId 5 -GroupId 10 -MetaverseAttributeName 'Department' -ComparisonType Equals -StringValue 'IT'

        Creates a criterion for an export sync rule that filters for Department = 'IT'.

    .EXAMPLE
        New-JIMScopingCriterion -SyncRuleId 5 -GroupId 10 -ConnectedSystemAttributeName 'ou' -ComparisonType Equals -StringValue 'Finance'

        Creates a criterion for an import sync rule that filters for ou = 'Finance'.

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
        [datetime]$DateTimeValue,

        [Parameter()]
        [bool]$BoolValue,

        [Parameter()]
        [guid]$GuidValue,

        [switch]$PassThru
    )

    process {
        if (-not $script:JIMConnection) {
            Write-Error "Not connected to JIM. Use Connect-JIM first."
            return
        }

        $body = @{
            comparisonType = $ComparisonType
        }

        # Handle Metaverse attribute (for export sync rules)
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
        # Handle Connected System attribute (for import sync rules)
        elseif ($PSCmdlet.ParameterSetName -eq 'ByCsId') {
            $body.connectedSystemAttributeId = $ConnectedSystemAttributeId
            $attrDisplay = "CS Attribute ID $ConnectedSystemAttributeId"
        }
        elseif ($PSCmdlet.ParameterSetName -eq 'ByCsName') {
            # Get the sync rule to find the connected system and object type
            Write-Verbose "Looking up sync rule $SyncRuleId to find Connected System attribute"
            $syncRule = Invoke-JIMApi -Endpoint "/api/v1/synchronisation/sync-rules/$SyncRuleId"

            if (-not $syncRule) {
                Write-Error "Sync rule $SyncRuleId not found."
                return
            }

            if ($syncRule.direction -ne 'Import') {
                Write-Error "Connected System attributes can only be used with import sync rules. This sync rule is an export rule."
                return
            }

            # Get the object type attributes
            $connectedSystemId = $syncRule.connectedSystemId
            $objectTypeId = $syncRule.connectedSystemObjectTypeId
            Write-Verbose "Looking up Connected System $connectedSystemId object type $objectTypeId attributes"

            $objectType = Invoke-JIMApi -Endpoint "/api/v1/connected-systems/$connectedSystemId/object-types/$objectTypeId"
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
        if ($PSBoundParameters.ContainsKey('DateTimeValue')) {
            $body.dateTimeValue = $DateTimeValue.ToString('o')
        }
        if ($PSBoundParameters.ContainsKey('BoolValue')) {
            $body.boolValue = $BoolValue
        }
        if ($PSBoundParameters.ContainsKey('GuidValue')) {
            $body.guidValue = $GuidValue.ToString()
        }

        if ($PSCmdlet.ShouldProcess("Scoping Criteria Group $GroupId", "Add Criterion ($attrDisplay $ComparisonType)")) {
            Write-Verbose "Creating criterion in group $GroupId for sync rule $SyncRuleId"

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
