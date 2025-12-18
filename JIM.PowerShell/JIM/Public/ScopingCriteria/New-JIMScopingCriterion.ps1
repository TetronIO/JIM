function New-JIMScopingCriterion {
    <#
    .SYNOPSIS
        Adds a criterion to a scoping criteria group.

    .DESCRIPTION
        Creates a new scoping criterion within a criteria group.
        Criteria define filter conditions based on Metaverse attribute values.

    .PARAMETER SyncRuleId
        The unique identifier of the sync rule.

    .PARAMETER GroupId
        The unique identifier of the criteria group to add the criterion to.

    .PARAMETER MetaverseAttributeId
        The unique identifier of the Metaverse attribute to evaluate.

    .PARAMETER MetaverseAttributeName
        Alternative to MetaverseAttributeId. The name of the Metaverse attribute to evaluate.

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

        Creates a criterion that filters for Department = 'IT'.

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
    [CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'Medium', DefaultParameterSetName = 'ById')]
    [OutputType([PSCustomObject])]
    param(
        [Parameter(Mandatory, ValueFromPipelineByPropertyName)]
        [int]$SyncRuleId,

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

        # Resolve attribute ID if name was provided
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

        $attrDisplay = if ($PSCmdlet.ParameterSetName -eq 'ByName') { $MetaverseAttributeName } else { "ID $attributeId" }

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
